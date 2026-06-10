namespace WwHdl


// ---------------------------------------------------------------------
// 3. WireWorld 標準セルライブラリ
//    各セルは「入力到達から出力放出まで何世代か」を Latency として持つ。
// ---------------------------------------------------------------------
module Library =
    open Units
    open Domain
    open Netlist

    type PortRole = In | Out | Clock

    type Port =
        { Role: PortRole
          /// セルパターン原点 (0,0) を基準にした、電子の入口/出口座標
          Offset: Coord }

    /// WireWorld 上の 1 ゲートを表す物理セル。
    type StdCell =
        { Name: string
          Kind: GateKind
          Size: Coord                 // bounding box (W, H)
          Ports: Port list
          /// 入力到達 → 出力放出 の世代差。P&R が配線遅延を補償する基準値。
          Latency: int<gen>
          /// 各 In ポートの内部遅延 (ポート→junction)。省略時は Latency と同一。
          PortDelays: int<gen> list
          Pattern: Grid }             // 原点基準の conductor 配置

    type CellLibrary = Map<GateKind, StdCell>

    /// ASCII アートから Grid を組む小道具。
    ///   '.' = Empty, '#' = Wire, 'H' = Head, 't' = Tail
    /// セルパターンを手で書くときに使う (テスト・ライブラリ定義用)。
    let ofAscii (rows: string list) : Grid =
        rows
        |> List.mapi (fun y row ->
            row |> Seq.mapi (fun x ch ->
                let st =
                    match ch with
                    | '#' -> Wire
                    | 'H' -> Head
                    | 't' -> Tail
                    | _   -> Empty
                { X = x; Y = y }, st)
            |> List.ofSeq)
        |> List.concat
        |> List.filter (fun (_, s) -> s <> Empty)
        |> Map.ofList

    // --- 例: 水平 BUF (単なる導線片)。Latency は導線長に等しい ---
    //   入口 (0,0) → 出口 (4,0)、4 セル分なので 4<gen>
    let buf : StdCell =
        { Name = "BUF_h4"
          Kind = Buf
          Size = { X = 5; Y = 1 }
          Ports = [ { Role = In;  Offset = { X = 0; Y = 0 } }
                    { Role = Out; Offset = { X = 4; Y = 0 } } ]
          Latency = 4<gen>
          PortDelays = [4<gen>]
          Pattern = ofAscii [ "#####" ] }

    // NOT / AND / OR などはクロックループや合流構造が必要で、手書きは
    // 誤りやすい。実際にはここに検証済みパターンを登録していく。
    // (各セルは Rule.run で単体テストして Latency を確定させる)

    /// DELAY_n StdCell を動的生成する。n+1 セルの直線導線 (1 セル = 1 gen)。
    /// n > 16 のときは蛇行パターンに切り替える予定 (TODO)。
    let makeDelay (n: int<gen>) : StdCell =
        let n' = int n
        { Name    = sprintf "DELAY_%d" n'
          Kind    = Buf
          Size    = { X = n' + 1; Y = 1 }
          Ports   = [ { Role = In;  Offset = { X = 0;  Y = 0 } }
                      { Role = Out; Offset = { X = n'; Y = 0 } } ]
          Latency = n
          PortDelays = [n]
          Pattern = ofAscii [ String.replicate (n' + 1) "#" ] }

    /// Crossover StdCell のスタブ。水平・垂直 2 信号を交差させる 7×7 パターン。
    /// Pattern は Rule.run で検証後に埋める。ポートは水平(In/Out) + 垂直(In/Out) の 4 本。
    let crossover : StdCell =
        { Name    = "CROSSOVER"
          Kind    = Buf        // 専用 GateKind の変更は交差処理実装時に検討
          Size    = { X = 7; Y = 7 }
          Ports   = [ { Role = In;  Offset = { X = 0; Y = 3 } }   // 水平入力
                      { Role = Out; Offset = { X = 6; Y = 3 } }   // 水平出力
                      { Role = In;  Offset = { X = 3; Y = 0 } }   // 垂直入力
                      { Role = Out; Offset = { X = 3; Y = 6 } } ] // 垂直出力
          Latency = 6<gen>
          PortDelays = [6<gen>; 6<gen>]
          Pattern = Map.empty }

    // -----------------------------------------------------------------------
    // 検証済みセル (Rule.run で Latency を実測済み)
    // -----------------------------------------------------------------------

    /// OR2: 2 入力 OR ゲート。Latency = 4<gen>。
    ///
    /// パターン (5×3):
    ///   ###..   y=0  入力 A  (x=0..2)
    ///   ...##   y=1  合流+出力 (x=3..4)
    ///   ###..   y=2  入力 B  (x=0..2)
    ///
    /// 動作: (2,0) または (2,2) の Head が (3,1) と対角隣接 (Δx=1,Δy=1)。
    ///   Head 1 個 → (3,1) fires → (4,1) = 出力。
    ///   Head 2 個 → (3,1) に 2 Head 近傍 → fires (WW 規則: 1 or 2 で発火)。
    let or2 : StdCell =
        { Name    = "OR2"
          Kind    = Or
          Size    = { X = 5; Y = 3 }
          Ports   = [ { Role = In;  Offset = { X = 0; Y = 0 } }
                      { Role = In;  Offset = { X = 0; Y = 2 } }
                      { Role = Out; Offset = { X = 4; Y = 1 } } ]
          Latency = 4<gen>
          PortDelays = [4<gen>; 4<gen>]
          Pattern = ofAscii [ "###  "; "   ##"; "###  " ] }

    /// SPLIT: 1 入力 2 出力スプリッタ。Latency = 4<gen>。
    ///
    /// パターン (5×3):
    ///   ..###   y=0  出力 A 方向 (x=2..4)
    ///   ##...   y=1  入力+折れ点 (x=0..1)
    ///   ..###   y=2  出力 B 方向 (x=2..4)
    ///
    /// 動作: (0,1)→(1,1)→対角→(2,0) と (2,2) が同時発火 → (4,0),(4,2) へ到達。
    let splitter : StdCell =
        { Name    = "SPLIT"
          Kind    = Buf
          Size    = { X = 5; Y = 3 }
          Ports   = [ { Role = In;  Offset = { X = 0; Y = 1 } }
                      { Role = Out; Offset = { X = 4; Y = 0 } }
                      { Role = Out; Offset = { X = 4; Y = 2 } } ]
          Latency = 4<gen>
          PortDelays = [4<gen>]
          Pattern = ofAscii [ "..###"; "##..."; "..###" ] }

    // -----------------------------------------------------------------------
    // スタブセル (パターン未確定 — Rule.run 検証待ち)
    // -----------------------------------------------------------------------

    /// JUNC3: 3 入力合流点。NOT / NAND の核となるセル。Latency = 4<gen>。
    ///
    /// パターン (5×3):
    ///   #.#..   y=0  入力 A (0,0), 入力 B (2,0) — junction(1,1) と対角隣接
    ///   .#...   y=1  junction (1,1)
    ///   #.###   y=2  入力 C (0,2) — junction(1,1) と対角隣接、出力経路 (2,2)..(4,2)
    ///
    /// 設計ポイント: A/B/C ポートを対角4隅に配置してポート間を全て非隣接 (チェビシェフ距離≥2) にする。
    let junc3 : StdCell =
        { Name    = "JUNC3"
          Kind    = Nand
          Size    = { X = 5; Y = 3 }
          Ports   = [ { Role = In;    Offset = { X = 0; Y = 0 } }  // A: (0,0) upper-left diagonal
                      { Role = In;    Offset = { X = 2; Y = 0 } }  // B: (2,0) upper-right diagonal
                      { Role = In;    Offset = { X = 0; Y = 2 } }  // C: (0,2) lower-left diagonal
                      { Role = Out;   Offset = { X = 4; Y = 2 } } ]// 出力: (4,2) 右下
          Latency = 4<gen>
          PortDelays = [1<gen>; 1<gen>; 1<gen>]
          Pattern = ofAscii [ "#.#.."; ".#..."; "#.###" ] }

    /// NOT1: junc3 の上下ポートにクロックを接続した NOT ゲート。
    /// パターン・Latency は junc3 と同一。コンパイラが Clock ポートへ同期信号を配線する。
    let not1 : StdCell = { junc3 with Name = "NOT1"; Kind = Not; PortDelays = [1<gen>; 1<gen>; 1<gen>] }

    /// JUNC3_Ab3: A 入力に 3gen 内蔵バッファを持つ NAND バリアント (9×3)。
    ///
    /// 設計: A=(0,0)→(1,0)→(2,0)→(3,0)→junction(4,1)、B=(5,0)、C=(3,2)、Out=(8,2)
    ///
    ///   A ポート基準遅延 (junction到達):
    ///     (0,0)→(1,0)→(2,0)→(3,0): 3 セル水平 + 対角 (3,0)→(4,1) = 計 4 gen
    ///   B ポート基準遅延:
    ///     (5,0)→(4,1): 対角 1 gen
    ///   C ポート基準遅延:
    ///     (3,2)→(4,1): 対角 1 gen
    ///
    ///   同期条件: T_A + 4 = T_B + 1 → T_A = T_B - 3
    ///     すなわち A が B より 3 gen 早く到着した場合に junction で同時評価。
    ///
    ///   Latency (B/C 基準): junction 評価 (t=1) + 出力経路 3 hop (t=4) = 4? → 実測で確認。
    ///   実際には B が clockTimeOf=62 で注入されたとき出力は t=67 で到達 → Latency = 5<gen>。
    ///
    ///   注意: Latency は B/C 入力基準 (STA target からの差)。
    ///     A 入力は 3 gen 早く到達することが前提 (insertDelays で保証するか、
    ///     または STA スラックが 3<gen> の NAND ゲートにこのセルを選択する)。
    ///
    /// パターン (9×3):
    ///   y=0: ####.#...  (0..3)=A バッファ、(4)=空、(5)=B ポート
    ///   y=1: ....#....  (4)=junction
    ///   y=2: ...#.####  (3)=C ポート、(4)=空、(5..8)=出力経路
    let junc3_Ab3 : StdCell =
        { Name    = "JUNC3_Ab3"
          Kind    = Nand
          Size    = { X = 9; Y = 3 }
          Ports   = [ { Role = In;  Offset = { X = 0; Y = 0 } }   // A (3gen 内蔵バッファ後 junction)
                      { Role = In;  Offset = { X = 5; Y = 0 } }   // B
                      { Role = In;  Offset = { X = 3; Y = 2 } }   // C (clock)
                      { Role = Out; Offset = { X = 8; Y = 2 } } ]
          Latency = 5<gen>   // B/C 基準: junction 評価 1gen + 出力 4hop = 5gen
          PortDelays = [4<gen>; 1<gen>; 1<gen>]   // A=4gen, B=1gen, C=1gen (junction到達)
          Pattern = ofAscii [ "####.#..."; "....#...."; "...#.####" ] }

    /// JUNC3_Ab7: A 入力に 7gen 内蔵バッファを持つ NAND バリアント (13×3)。
    ///
    /// 設計: A=(0,0)→...→(7,0)→junction(8,1)、B=(9,0)、C=(7,2)、Out=(12,2)
    ///
    ///   A ポート基準遅延 (junction到達): 8 gen  (7セル水平 + 対角)
    ///   B ポート基準遅延:                1 gen  (対角)
    ///   C ポート基準遅延:                1 gen  (対角)
    ///
    ///   同期条件: T_A + 8 = T_B + 1 → T_A = T_B - 7
    ///     すなわち A が B より 7 gen 早く到着した場合に junction で同時評価。
    ///
    ///   Latency (B/C 基準): junction 評価 1gen + 出力 4hop = 5gen
    ///
    /// パターン (13×3):
    ///   y=0: ########.#...  (0..7)=A バッファ、(8)=空、(9)=B ポート
    ///   y=1: ........#....  (8)=junction
    ///   y=2: .......#.####  (7)=C ポート、(8)=空、(9..12)=出力経路
    let junc3_Ab7 : StdCell =
        { Name    = "JUNC3_Ab7"
          Kind    = Nand
          Size    = { X = 13; Y = 3 }
          Ports   = [ { Role = In;  Offset = { X = 0; Y = 0 } }   // A (7gen 内蔵バッファ後 junction)
                      { Role = In;  Offset = { X = 9; Y = 0 } }   // B
                      { Role = In;  Offset = { X = 7; Y = 2 } }   // C (clock)
                      { Role = Out; Offset = { X = 12; Y = 2 } } ]
          Latency = 5<gen>   // B/C 基準: junction 評価 1gen + 出力 4hop = 5gen
          PortDelays = [8<gen>; 1<gen>; 1<gen>]   // A=8gen, B=1gen, C=1gen (junction到達)
          Pattern = ofAscii [ "########.#..."; "........#...."; ".......#.####" ] }

    /// JUNC3_Ab5: A 入力に 5gen 内蔵バッファを持つ NAND バリアント (11×3)。
    ///
    /// 設計: A=(0,0)→...→(5,0)→junction(6,1)、B=(7,0)、C=(5,2)、Out=(10,2)
    ///
    ///   A ポート基準遅延 (junction到達): 6 gen  (5セル水平 + 対角)
    ///   B ポート基準遅延:                1 gen  (対角)
    ///   C ポート基準遅延:                1 gen  (対角)
    ///
    ///   同期条件: T_A + 6 = T_B + 1 → T_A = T_B - 5
    ///     すなわち A が B より 5 gen 早く到着した場合に junction で同時評価。
    ///
    ///   Latency (B/C 基準): junction 評価 1gen + 出力 4hop = 5gen
    ///
    /// パターン (11×3):
    ///   y=0: ######.#...  (0..5)=A バッファ、(6)=空、(7)=B ポート
    ///   y=1: ......#....  (6)=junction
    ///   y=2: .....#.####  (5)=C ポート、(6)=空、(7..10)=出力経路
    let junc3_Ab5 : StdCell =
        { Name    = "JUNC3_Ab5"
          Kind    = Nand
          Size    = { X = 11; Y = 3 }
          Ports   = [ { Role = In;  Offset = { X = 0; Y = 0 } }   // A (5gen 内蔵バッファ後 junction)
                      { Role = In;  Offset = { X = 7; Y = 0 } }   // B
                      { Role = In;  Offset = { X = 5; Y = 2 } }   // C (clock)
                      { Role = Out; Offset = { X = 10; Y = 2 } } ]
          Latency = 5<gen>   // B/C 基準: junction 評価 1gen + 出力 4hop = 5gen
          PortDelays = [6<gen>; 1<gen>; 1<gen>]   // A=6gen, B=1gen, C=1gen (junction到達)
          Pattern = ofAscii [ "######.#..."; "......#...."; ".....#.####" ] }

    // AND2 はモノリシック StdCell として実装しない。理由:
    //   JUNC3 を同一水平導線上に直列配置すると B/clock 入力が (3,2) を対角で誤発火させ、
    //   NAND=0 の場合に偽信号が NOT 段へ漏れる (スプリアス信号問題)。
    //   代わりに `abc -g NAND,NOT` で $\_NAND\_ + $\_NOT\_ に分解し、
    //   M3 ルーターが中間配線と STA が遅延補償を行う。

    /// DIODE: 電子ダイオード (Quinapalus WireWorld 公式設計)。Latency = 3<gen>。
    ///
    /// パターン (4×3)  — 出典: https://www.quinapalus.com/wi-diode.gif
    ///   .##.   y=0  アーム上 (x=1..2)
    ///   ##.#   y=1  入力(x=0)・中間(x=1)・ギャップ(x=2)・出力(x=3)
    ///   .##.   y=2  アーム下 (x=1..2)
    ///
    /// 動作原理 (3-Head 吸収則):
    ///   順方向 (→): H at (0,1)
    ///     t+1: (1,0),(1,1),(1,2) → Head  [対角/直交で同時発火]
    ///     t+2: (2,0),(2,2) → Head; (0,1) は 3 Head 近傍 → 反射ブロック
    ///     t+3: (3,1) → Head  [(2,0)(2,2) からの対角合流] ✓ Latency=3
    ///
    ///   逆方向 (←): H at (3,1)
    ///     t+1: (2,0),(2,2) → Head  [対角]
    ///     t+2: (1,0),(1,1),(1,2) → Head  [3 本同時]
    ///     t+3: (0,1) が 3 Head 近傍 → 発火せず → 遮断 ✓
    ///
    /// 注意: 単一電子では内部発振が生じる (t+3 以降の反射)。
    ///   同期回路でクロック周期を十分長く取るか、junc3 (clock-gated pass) で代替すること。
    let diode : StdCell =
        { Name    = "DIODE"
          Kind    = Buf
          Size    = { X = 4; Y = 3 }
          Ports   = [ { Role = In;  Offset = { X = 0; Y = 1 } }
                      { Role = Out; Offset = { X = 3; Y = 1 } } ]
          Latency = 3<gen>
          PortDelays = [3<gen>]
          Pattern = ofAscii [ ".##."; "##.#"; ".##." ] }

    // -----------------------------------------------------------------------
    // DFF 構築ヘルパー — 5つの JUNC3 を合成して D ラッチを形成する
    // -----------------------------------------------------------------------

    /// セルのパターンを指定オフセットに配置する。
    let private placePattern (pattern: Grid) (offset: Coord) : Grid =
        pattern |> Map.toList
        |> List.map (fun (c, s) -> { X = c.X + offset.X; Y = c.Y + offset.Y }, s)
        |> Map.ofList

    /// 2点間を水平・垂直の導線で結ぶ。delta 方向に L 字配線。
    let private routeWire (grid: Grid) (a: Coord) (b: Coord) : Grid =
        let route = System.Collections.Generic.List<Coord>()
        let mutable cur = a
        // 水平優先
        while cur.X <> b.X do
            cur <- { X = cur.X + (if b.X > cur.X then 1 else -1); Y = cur.Y }
            if cur <> a then route.Add cur
        while cur.Y <> b.Y do
            cur <- { X = cur.X; Y = cur.Y + (if b.Y > cur.Y then 1 else -1) }
            if cur <> a && cur <> b then route.Add cur
        route |> Seq.fold (fun g c -> Map.add c Wire g) grid

    /// 5-JUNC3 構成の D ラッチ (レベルセンシティブ) パターンを生成する。
    /// 内部構造:
    ///   J1=NOT(D)=JUNC3(D,Vdd,Vdd), J2=NAND(D,CLK)=JUNC3(D,CLK,Vdd)
    ///   J3=NAND(nD,CLK)=JUNC3(nD,CLK,Vdd)
    ///   J4=SR-Q=JUNC3(S',Qb,Vdd), J5=SR-Qb=JUNC3(R',Q,Vdd)
    /// CLK/Vdd は全 JUNC3 の C ポート(0,2)および J1 の B ポート(2,0)へ分配。
    let buildDLatch () : StdCell =
        // 5つの JUNC3 を水平1行に配置 (y=4)。D バス(y=0)と CLK バス(y=2)が上を平行に走る。
        // 各 JUNC3 までの D→A, CLK→B, CLK→C の遅延が自然一致する。
        // Ports は [In(D); In(CLK_B); In(CLK_C); Out(Q)] の4本。
        // CLK_B・CLK_C とも y=2 のバスから供給 (遅延一致のため)。
        let pJ1 = { X=0;  Y=4 }    // NOT D
        let pJ2 = { X=8;  Y=4 }    // NAND(D,CLK)
        let pJ3 = { X=16; Y=4 }    // NAND(nD,CLK)
        let pJ4 = { X=24; Y=4 }    // SR-Q
        let pJ5 = { X=32; Y=4 }    // SR-Qb

        let portA  (p: Coord) = { X = p.X + 0; Y = p.Y + 0 }
        let portB  (p: Coord) = { X = p.X + 2; Y = p.Y + 0 }
        let portC  (p: Coord) = { X = p.X + 0; Y = p.Y + 2 }
        let portOut(p: Coord) = { X = p.X + 4; Y = p.Y + 2 }

        // 全 JUNC3 パターンを配置
        let g0 = [pJ1; pJ2; pJ3; pJ4; pJ5]
                |> List.fold (fun acc p ->
                    placePattern junc3.Pattern p
                    |> Map.fold (fun a k v -> Map.add k v a) acc) Map.empty

        // --- JUNC3 間接続 ---
        // nD: J1出力(4,6) → J3 A(16,4)
        let g = routeWire g0 (portOut pJ1) (portA pJ3)
        // S: J2出力(12,6) → J4 A(24,4)
        let g = routeWire g  (portOut pJ2) (portA pJ4)
        // R: J3出力(20,6) → J5 A(32,4)
        let g = routeWire g  (portOut pJ3) (portA pJ5)
        // Q → J5 B / Qb → J4 B (SR フィードバック)
        let g = routeWire g  (portOut pJ4) (portB pJ5)
        let g = routeWire g  (portOut pJ5) (portB pJ4)

        // --- D バス: y=0 → J1_A(0,4), J2_A(8,4) ---
        let dIn = { X = 0; Y = 0 }
        let g = routeWire g dIn (portA pJ1)
        let g = routeWire g dIn (portA pJ2)

        // --- CLK バス: y=2 → J1_B (NOT data), および全 C/B ポート (Vdd/CLK 供給) ---
        // J1 の B ポートには CLK_B と CLK_C の両方が y=2 から供給され、遅延が一致する。
        let clkIn = { X = 0; Y = 2 }
        let g = routeWire g clkIn (portB pJ1)   // CLK_B: J1 NOT の B 入力
        let clkTargets =
            [ portC pJ1              // J1 C
              portB pJ2; portC pJ2   // J2 B+C
              portB pJ3; portC pJ3   // J3 B+C
              portC pJ4              // J4 C
              portC pJ5 ]            // J5 C
        let g = clkTargets |> List.fold (fun acc t -> routeWire acc clkIn t) g

        let pattern = g
        let maxX = pattern |> Map.toList |> List.map (fun (c,_) -> c.X) |> List.max
        let maxY = pattern |> Map.toList |> List.map (fun (c,_) -> c.Y) |> List.max

        // ポート定義: [In(D); In(CLK_B); In(CLK_C); Out(Q)]
        //   D(0,0) → J1_A/J2_A。CLK_B/C(0,2) → y=2 バスから全対象へ。
        //   CLK_B はルーターが配線 (NOT D の B 入力)、CLK_C はクロックツリーが分配。
        let ports : Port list =
            [ { Role = In;  Offset = { X = 0; Y = 0 } }   // D 入力
              { Role = In;  Offset = { X = 0; Y = 2 } }   // CLK_B (NOT D の B 入力用データ配線)
              { Role = In;  Offset = { X = 0; Y = 2 } }   // CLK_C (クロックツリー分配)
              { Role = Out; Offset = { X = 28; Y = 6 } } ]// Q = J4 出力

        // PortDelays: [D; CLK_B; CLK_C]
        //   D: (0,0)→J1_A(0,4)=4→junction=1=5, (0,0)→J2_A(8,4)=12→junction=1=13 → worst=13
        //   CLK_B: (0,2)→J1_B(2,4)=4→junction=1=5 → 5
        //   CLK_C: (0,2)→J5_C(32,6)=36→junction=1=37 → worst=37
        { Name      = "DLATCH"
          Kind      = Dff
          Size      = { X = maxX + 1; Y = maxY + 1 }
          Ports     = ports
          Latency   = 40<gen>    // 全経路通して出力が確定する最悪世代
          PortDelays = [13<gen>; 5<gen>; 37<gen>]
          Pattern   = pattern }

    /// DFF: D-Latch 版。検証中につき defaultLib には未登録。
    let dff : StdCell =
        buildDLatch ()

    /// M2 ターゲット (`abc -g NAND,NOT`) 対応のデフォルトライブラリ。
    /// AND/XOR は Yosys が NAND+NOT に分解するためモノリシックセルは不要。
    /// DFF は設計検証中のため除外。
    let defaultLib : CellLibrary =
        [ Buf,  buf
          Or,   or2
          Nand, junc3
          Not,  not1 ]
        |> Map.ofList


// ---------------------------------------------------------------------
// 3.5 セル単体テスト (Rule.run ベース)
//     各 StdCell の Latency と対称性を Rule.run でチェックするハーネス。
//     `dotnet script` や xUnit から呼び出して使う。
// ---------------------------------------------------------------------
module CellTest =
    open Units
    open Domain
    open Netlist
    open Library
    open Rule

    /// In ポートに Head を置いて Latency 世代後に Out ポートへ Head が
    /// 届くか確認する。Clock ポートを持つセルはこのテストでは検証不可。
    let verifyLatency (cell: StdCell) : bool =
        match cell.Ports |> List.tryFind (fun p -> p.Role = In),
              cell.Ports |> List.tryFind (fun p -> p.Role = Out) with
        | Some inPort, Some outPort ->
            let initial = cell.Pattern |> Map.add inPort.Offset Head
            let result  = run (int cell.Latency) initial
            get result outPort.Offset = Head
        | _ -> false

    /// 各入力ポートに単独で Head を入れたとき、すべて同じ Latency で
    /// 最初の Out ポートへ届くことを確認する (OR/AND の対称性テスト)。
    let verifySymmetry (cell: StdCell) : bool =
        let outPort = cell.Ports |> List.find (fun p -> p.Role = Out)
        cell.Ports
        |> List.filter (fun p -> p.Role = In)
        |> List.forall (fun inPort ->
            let initial = cell.Pattern |> Map.add inPort.Offset Head
            let result  = run (int cell.Latency) initial
            get result outPort.Offset = Head)

    /// スプリッタ専用: 単一入力から全 Out ポートへ同時到達するか確認する。
    let verifyAllOutputs (cell: StdCell) : bool =
        match cell.Ports |> List.tryFind (fun p -> p.Role = In) with
        | None -> false
        | Some inPort ->
            let initial  = cell.Pattern |> Map.add inPort.Offset Head
            let result   = run (int cell.Latency) initial
            cell.Ports
            |> List.filter (fun p -> p.Role = Out)
            |> List.forall (fun op -> get result op.Offset = Head)

    /// JUNC3 の特定入力パターンで出力の有無を確認する。
    /// heads: In ポートのうち Head を注入するポートのインデックスリスト (0=left,1=top,2=bottom)
    /// expectFires: 出力 (4,2) に Head が来るか
    let testJunc3 (headPortIndices: int list) (expectFires: bool) : bool =
        let inPorts = junc3.Ports |> List.filter (fun p -> p.Role = In)
        let initial =
            headPortIndices
            |> List.fold (fun g i -> g |> Map.add inPorts.[i].Offset Head) junc3.Pattern
        let outPort = junc3.Ports |> List.find (fun p -> p.Role = Out)
        let result  = run (int junc3.Latency) initial
        let fired   = get result outPort.Offset = Head
        fired = expectFires

    /// DIODE の順方向通過と逆方向遮断を確認する。
    /// forward=true → In に Head を置いて Latency 後に Out に Head が来るか
    /// forward=false → Out に Head を置いて Latency 後に In に Head が来ないか
    let testDiode (forward: bool) : bool =
        let inPort  = diode.Ports |> List.find (fun p -> p.Role = In)
        let outPort = diode.Ports |> List.find (fun p -> p.Role = Out)
        if forward then
            let initial = diode.Pattern |> Map.add inPort.Offset Head
            let result  = run (int diode.Latency) initial
            get result outPort.Offset = Head
        else
            // 逆方向: Out に Head を置き、In の先 (入力方向) に Head が伝わらないことを確認
            // 正確には Out 入力後 Latency gen で In セルが Head にならないこと
            let initial = diode.Pattern |> Map.add outPort.Offset Head
            let result  = run (int diode.Latency) initial
            get result inPort.Offset <> Head  // blocked = In セルに Head が来ない

    /// JUNC3 ポートインデックス: testJunc3 の headPortIndices で使う名前付き定数
    let private juncPortA = 0  // left (0,0) — NAND input A / NOT data input
    let private juncPortB = 1  // top  (2,0) — NAND input B / NOT clock
    let private juncPortC = 2  // bottom (0,2) — NAND clock / NOT clock

    /// 検証済みセルをまとめてテストし (テスト名, 合否) リストを返す。
    let runAll () : (string * bool) list =
        [ "BUF_h4   latency",          verifyLatency   buf
          "OR2      latency(in1)",      verifyLatency   or2
          "OR2      symmetry",          verifySymmetry  or2
          "SPLIT    latency",           verifyLatency   splitter
          "SPLIT    all-outputs",       verifyAllOutputs splitter
          // JUNC3: NOT(A) = JUNC3(left=A, top=clock, bottom=clock)
          "JUNC3    NOT(0)=1 fires",    testJunc3 [juncPortB; juncPortC]   true   // A=0, 2 clocks → fires
          "JUNC3    NOT(1)=0 no-fire",  testJunc3 [juncPortA; juncPortB; juncPortC] false  // A=1 + 2 clocks → 3 Head → no fire
          // JUNC3: NAND(A,B) = JUNC3(left=A, top=B, bottom=clock)
          "JUNC3    NAND(0,0)=1",       testJunc3 [juncPortC]     true   // clock only → fires
          "JUNC3    NAND(1,0)=1",       testJunc3 [juncPortA; juncPortC]   true   // A+clock → fires
          "JUNC3    NAND(0,1)=1",       testJunc3 [juncPortB; juncPortC]   true   // B+clock → fires
          "JUNC3    NAND(1,1)=0",       testJunc3 [juncPortA; juncPortB; juncPortC] false  // A+B+clock → 3 Head → no fire
          // DIODE: Quinapalus 公式設計
          "DIODE    forward pass",      testDiode true           // 順方向通過 Latency=3
          "DIODE    backward block",    testDiode false          // 逆方向遮断
        ]
