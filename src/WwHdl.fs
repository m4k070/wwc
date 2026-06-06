// =====================================================================
//  WwHdl — HDL → WireWorld コンパイラ (F# 設計スケッチ)
//
//  設計方針:
//   1. 各コンパイル段の中間表現を「別々の型」にする → 段の取り違えを
//      コンパイルエラーで弾く (phantom-stage 的なアプローチ)。
//   2. WireWorld 固有の "配線長 = 遅延" を units of measure (<gen>) で
//      型に載せる → タイミング不整合を型と関数で検出可能にする。
//   3. パイプラインは Result ベースの railway-oriented composition。
//   4. グリッドは疎な Map<Coord, CellState> で表現 (回路は広大かつ疎)。
// =====================================================================

namespace WwHdl

// ---------------------------------------------------------------------
// 0. 単位と基礎型
// ---------------------------------------------------------------------
module Units =
    /// WireWorld の 1 世代 (tick)。WireWorld では 1 セル進む = 1 gen なので
    /// 「距離」と「遅延」が同じ次元になる。これが設計上の最重要ポイント。
    [<Measure>] type gen


module Domain =
    open Units

    [<Struct>]
    type Coord = { X: int; Y: int }

    /// WireWorld の 4 状態
    type CellState =
        | Empty       // 0: 背景
        | Head        // 1: electron head
        | Tail        // 2: electron tail
        | Wire        // 3: conductor

    /// 疎なグリッド。Empty は格納しない (存在しない = Empty)。
    type Grid = Map<Coord, CellState>

    let get (g: Grid) (c: Coord) : CellState =
        Map.tryFind c g |> Option.defaultValue Empty


// ---------------------------------------------------------------------
// 1. WireWorld の遷移規則そのもの (シミュレータの核)
//    コンパイラの検証 (生成した回路を実際に回す) に使う。
// ---------------------------------------------------------------------
module Rule =
    open Domain

    let private neighbours (c: Coord) =
        [ for dx in -1..1 do
            for dy in -1..1 do
                if not (dx = 0 && dy = 0) then
                    yield { X = c.X + dx; Y = c.Y + dy } ]

    /// 1 世代進める。Wire → Head は近傍 head 数が 1 or 2 のときのみ。
    /// Empty は Empty のままなので、格納済みセル (= 非 Empty) だけ評価すれば足りる。
    let step (g: Grid) : Grid =
        g |> Map.map (fun coord state ->
            match state with
            | Empty -> Empty
            | Head  -> Tail
            | Tail  -> Wire
            | Wire  ->
                let heads =
                    neighbours coord
                    |> List.sumBy (fun n -> if get g n = Head then 1 else 0)
                if heads = 1 || heads = 2 then Head else Wire)

    let run (generations: int) (g: Grid) : Grid =
        Seq.fold (fun acc _ -> step acc) g (seq { 1 .. generations })


// ---------------------------------------------------------------------
// 2. ネットリスト IR (テクノロジ非依存・合成済み)
// ---------------------------------------------------------------------
module Netlist =
    type NetId = NetId of int

    type GateKind =
        | Not | And | Or | Xor | Nand | Nor | Buf
        | Dff                 // D フリップフロップ (順序素子)
        | Const of bool

    type Gate =
        { Id: int
          Kind: GateKind
          Inputs: NetId list
          Output: NetId }

    /// 合成済みネットリスト。Frontend/Synthesis の出力。
    type Netlist =
        { Gates: Gate list
          PrimaryInputs: NetId list
          PrimaryOutputs: NetId list
          ClockNet: NetId option }   // 順序回路があるときのみ Some


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
          Pattern = ofAscii [ String.replicate (n' + 1) "#" ] }

    /// Crossover StdCell のスタブ。水平・垂直 2 信号を交差させる 7×7 パターン。
    /// Pattern は Rule.run で検証後に埋める。ポートは水平(In/Out) + 垂直(In/Out) の 4 本。
    let crossover : StdCell =
        { Name    = "CROSSOVER"
          Kind    = Buf        // 専用 GateKind への変更は交差処理実装時に検討
          Size    = { X = 7; Y = 7 }
          Ports   = [ { Role = In;  Offset = { X = 0; Y = 3 } }   // 水平入力
                      { Role = Out; Offset = { X = 6; Y = 3 } }   // 水平出力
                      { Role = In;  Offset = { X = 3; Y = 0 } }   // 垂直入力
                      { Role = Out; Offset = { X = 3; Y = 6 } } ] // 垂直出力
          Latency = 6<gen>
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
          Pattern = ofAscii [ "..###"; "##..."; "..###" ] }

    // -----------------------------------------------------------------------
    // スタブセル (パターン未確定 — Rule.run 検証待ち)
    // -----------------------------------------------------------------------

    /// JUNC3: 3 入力合流点。NOT / NAND の核となるセル。Latency = 4<gen>。
    ///
    /// パターン (5×3):
    ///   #....   y=0  入力 A (0,0) — junction(1,1) と対角隣接
    ///   #####   y=1  入力 B (0,1), junction (1,1), 出力経路 (2,1)..(4,1)
    ///   #....   y=2  入力 C (0,2) — junction(1,1) と対角隣接
    ///
    /// 設計ポイント: 3 入力すべてを左列に集約し junction を (1,1) に置く。
    ///   (2,1) の隣は (1,1) のみ (A/C ポートとの距離=2) → 対角ショートカット排除 ✓
    ///
    /// 動作 (t=0 で A,B,C がポートに Head):
    ///   t=1: junction(1,1) が隣接 Head 数を評価
    ///     1 or 2 個 → fires → t=4 で (4,1) = 出力 Head
    ///     3 個     → no fire → 出力なし
    ///
    /// NOT(A)   : A=(0,0), clock1=(0,1), clock2=(0,2) → out=(4,1)
    ///   A=0 → 2 Head → fires → output=1 ✓
    ///   A=1 → 3 Head → no fire → output=0 ✓
    ///
    /// NAND(A,B): A=(0,0), B=(0,1), clock=(0,2) → out=(4,1)
    ///   A∧B=1 → 3 Head → no fire → NAND=0 ✓
    ///   otherwise → 1-2 Head → fires → NAND=1 ✓
    let junc3 : StdCell =
        { Name    = "JUNC3"
          Kind    = Nand
          Size    = { X = 5; Y = 3 }
          Ports   = [ { Role = In;    Offset = { X = 0; Y = 0 } }  // A: (0,0) diagonal to junction
                      { Role = In;    Offset = { X = 0; Y = 1 } }  // B: (0,1) direct to junction
                      { Role = In;    Offset = { X = 0; Y = 2 } }  // C: (0,2) diagonal to junction
                      { Role = Out;   Offset = { X = 4; Y = 1 } } ]// 右: output
          Latency = 4<gen>
          Pattern = ofAscii [ "#...."; "#####"; "#...." ] }

    /// NOT1: junc3 の上下ポートにクロックを接続した NOT ゲート。
    /// パターン・Latency は junc3 と同一。コンパイラが Clock ポートへ同期信号を配線する。
    let not1 : StdCell = { junc3 with Name = "NOT1"; Kind = Not }

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
          Pattern = ofAscii [ ".##."; "##.#"; ".##." ] }

    /// DFF: クロックゲート型 D フリップフロップのスタブ。
    ///
    /// 設計方針 (DESIGN.md §2.7 参照):
    ///   AND(D, CLK) = NAND(NAND(D,CLK)) で実現するクロックセンシティブ D ラッチ。
    ///   junc3 2 個 (NAND + NOT) を配線して構成する。
    ///   Ports: In=D(x=0,y=5), Clock=CLK(x=5,y=0), Out=Q(x=11,y=5)
    ///   Latency ≈ 10<gen> (NAND 4 + NOT 4 + 配線 2)。
    ///
    /// 未解決: NAND と NOT の間の Clock 配線タイミング調整。Rule.run 検証後に Pattern/Latency を確定。
    let dff : StdCell =
        { Name    = "DFF"
          Kind    = Dff
          Size    = { X = 12; Y = 10 }
          Ports   = [ { Role = In;    Offset = { X = 0;  Y = 5 } }   // D 入力
                      { Role = Clock; Offset = { X = 5;  Y = 0 } }   // CLK
                      { Role = Out;   Offset = { X = 11; Y = 5 } } ] // Q 出力
          Latency = 10<gen>     // TODO: Rule.run で実測後に更新
          Pattern = Map.empty }

    /// M2 ターゲット (`abc -g NAND,NOT`) 対応のデフォルトライブラリ。
    /// AND/XOR は Yosys が NAND+NOT に分解するためモノリシックセルは不要。
    let defaultLib : CellLibrary =
        [ Buf,  buf
          Or,   or2
          Nand, junc3
          Not,  not1
          Dff,  dff ]
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

    /// 検証済みセルをまとめてテストし (テスト名, 合否) リストを返す。
    let runAll () : (string * bool) list =
        [ "BUF_h4   latency",          verifyLatency   buf
          "OR2      latency(in1)",      verifyLatency   or2
          "OR2      symmetry",          verifySymmetry  or2
          "SPLIT    latency",           verifyLatency   splitter
          "SPLIT    all-outputs",       verifyAllOutputs splitter
          // JUNC3: NOT(A) = JUNC3(left=A, top=clock, bottom=clock)
          "JUNC3    NOT(0)=1 fires",    testJunc3 [1;2]   true   // A=0, 2 clocks → fires
          "JUNC3    NOT(1)=0 no-fire",  testJunc3 [0;1;2] false  // A=1 + 2 clocks → 3 Head → no fire
          // JUNC3: NAND(A,B) = JUNC3(left=A, top=B, bottom=clock)
          "JUNC3    NAND(0,0)=1",       testJunc3 [2]     true   // clock only → fires
          "JUNC3    NAND(1,0)=1",       testJunc3 [0;2]   true   // A+clock → fires
          "JUNC3    NAND(0,1)=1",       testJunc3 [1;2]   true   // B+clock → fires
          "JUNC3    NAND(1,1)=0",       testJunc3 [0;1;2] false  // A+B+clock → 3 Head → no fire
          // DIODE: Quinapalus 公式設計
          "DIODE    forward pass",      testDiode true           // 順方向通過 Latency=3
          "DIODE    backward block",    testDiode false          // 逆方向遮断
        ]


// ---------------------------------------------------------------------
// 4. 配置 (Placement) と配線 (Routing)
// ---------------------------------------------------------------------
module Place =
    open Domain
    open Netlist
    open Library

    type Placed =
        { Gate: Gate
          Cell: StdCell
          Origin: Coord }          // グリッド上の絶対配置位置

    type Placement = Placed list

    /// セルの絶対ポート座標を求める。
    let portCoord (p: Placed) (port: Port) : Coord =
        { X = p.Origin.X + port.Offset.X
          Y = p.Origin.Y + port.Offset.Y }


module Route =
    open Units
    open Domain
    open Netlist
    open Place
    open Rule

    /// 1 本の配線。長さ (= Path のセル数) がそのまま遅延になる。
    type Wire =
        { Net: NetId
          Path: Coord list
          Delay: int<gen> }

    /// パスをシミュレーションして実効遅延を実測する。
    /// src に Head を置き、dst が Head になるまでの世代数を返す。
    /// 解析的な計算 (N-1-turns 等) は L ターンが連続する場合に誤るため、実測する。
    let private measureDelay (path: Coord list) : int<gen> =
        match path with
        | [] | [_] -> 0<gen>
        | _ ->
            let src = List.head path
            let dst = List.last path
            let wireGrid = path |> List.map (fun c -> c, Wire) |> Map.ofList
            let initial = wireGrid |> Map.add src Head
            let limit = (List.length path) * 2
            let rec find (g: Grid) (t: int) =
                if t >= limit then (List.length path - 1) * 1<gen>   // fallback
                elif Domain.get g dst = Head then t * 1<gen>
                else find (Rule.step g) (t + 1)
            find initial 0

    let ofPath (net: NetId) (path: Coord list) : Wire =
        { Net   = net
          Path  = path
          Delay = measureDelay path }

    type RoutingCell =
        | Free              // 配線可能
        | Blocked           // セルが占有または禁止領域
        | Routed of NetId   // 既配線済みのネット

    type RoutingGrid = Map<Coord, RoutingCell>

    /// Placement からルーティンググリッドを構築する。
    /// 各セルの bounding box 内を Blocked にし、それ以外は Free (= Map に存在しない) とする。
    let buildGrid (placement: Placement) : RoutingGrid =
        placement
        |> List.collect (fun p ->
            [ for x in 0 .. p.Cell.Size.X - 1 do
                for y in 0 .. p.Cell.Size.Y - 1 do
                    yield { X = p.Origin.X + x; Y = p.Origin.Y + y }, Blocked ])
        |> Map.ofList

    /// Lee 法 BFS で src から dst への最短経路を返す。到達不能なら None。
    /// src/dst は Blocked 領域内 (セルのポート座標) でも通過できる。
    /// 中間セルは Free (grid に存在しない) のみ通過可。
    let leePath (grid: RoutingGrid) (src: Coord) (dst: Coord) : Coord list option =
        if src = dst then Some [src]
        else
            let passable c =
                c = src || c = dst ||
                match Map.tryFind c grid with
                | None | Some Free -> true
                | _ -> false

            let dirs = [| {X=1;Y=0}; {X= -1;Y=0}; {X=0;Y=1}; {X=0;Y= -1} |]
            let prev  = System.Collections.Generic.Dictionary<Coord, Coord>()
            let queue = System.Collections.Generic.Queue<Coord>()
            prev.[src] <- src
            queue.Enqueue src

            let mutable found = false
            while queue.Count > 0 && not found do
                let c = queue.Dequeue()
                if c = dst then found <- true
                else
                    for d in dirs do
                        let n = { X = c.X + d.X; Y = c.Y + d.Y }
                        if not (prev.ContainsKey n) && passable n then
                            prev.[n] <- c
                            queue.Enqueue n

            if not found then None
            else
                let path = System.Collections.Generic.List<Coord>()
                let mutable cur = dst
                while cur <> src do
                    path.Insert(0, cur)
                    cur <- prev.[cur]
                path.Insert(0, src)
                Some (List.ofSeq path)

    /// Wire リストから 2 本以上の経路が共有するセルを衝突として列挙する。
    let findConflicts (wires: Wire list) : (Coord * NetId * NetId) list =
        wires
        |> List.collect (fun w -> w.Path |> List.map (fun c -> c, w.Net))
        |> List.groupBy fst
        |> List.choose (fun (coord, entries) ->
            match List.map snd entries |> List.distinct with
            | a :: b :: _ -> Some (coord, a, b)
            | _            -> None)

    /// 衝突点に Crossover セルを挿入し両ネットの経路を張り替える。
    let insertCrossovers
        (_conflicts: (Coord * NetId * NetId) list)
        (wires: Wire list)
        : Wire list =
        // TODO: Crossover StdCell を配置し Path を入出力ポートに接続しなおす
        wires


// ---------------------------------------------------------------------
// 6. 静的タイミング解析 (STA)
//    到達時刻の計算と DELAY_n セル挿入によるタイミング均等化。
// ---------------------------------------------------------------------
module Sta =
    open Units
    open Domain
    open Netlist
    open Place
    open Route

    /// ネットごとの信号到達世代。
    type ArrivalMap = Map<NetId, int<gen>>

    /// トポロジカル順で各ネットの到達時刻を計算する。
    ///   arrival(primary_input) = 0<gen>
    ///   arrival(gate_output)   = max(arrival(input_i) + wire_i.Delay) + gate.Latency
    ///
    /// 実装: 全入力の到達時刻が確定したゲートを繰り返し処理する。
    ///   組合せ回路 (DAG) なら必ず収束する。
    let computeArrival (placement: Placement) (wires: Wire list) : ArrivalMap =
        let wireDelay =
            wires |> List.map (fun w -> w.Net, w.Delay) |> Map.ofList

        let gateDrivenNets =
            placement |> List.map (fun p -> p.Gate.Output) |> Set.ofList

        // 一次入力ネット (いずれのゲートも駆動しないネット) の到達時刻 = 0
        let primaryArrivals =
            placement
            |> List.collect (fun p -> p.Gate.Inputs)
            |> List.filter (fun n -> not (Set.contains n gateDrivenNets))
            |> List.distinct
            |> List.map (fun n -> n, 0<gen>)
            |> Map.ofList

        // 全入力の到達時刻が確定したゲートを順次処理して伝播する。
        let rec propagate (arr: ArrivalMap) (remaining: Placed list) =
            if List.isEmpty remaining then arr
            else
                let ready, notReady =
                    remaining |> List.partition (fun p ->
                        p.Gate.Inputs |> List.forall (fun n -> Map.containsKey n arr))
                if List.isEmpty ready then arr  // 進捗なし (サイクル or 孤立)
                else
                    let arr' =
                        ready |> List.fold (fun acc p ->
                            let inputTimes =
                                p.Gate.Inputs |> List.map (fun n ->
                                    (Map.tryFind n acc |> Option.defaultValue 0<gen>)
                                    + (Map.tryFind n wireDelay |> Option.defaultValue 0<gen>))
                            let target =
                                if List.isEmpty inputTimes then 0<gen>
                                else List.max inputTimes
                            Map.add p.Gate.Output (target + p.Cell.Latency) acc) arr
                    propagate arr' notReady

        propagate primaryArrivals placement

    /// 各 Wire のスラック（余裕世代）を計算する。
    ///   target(gate)  = max { arrival(input_i) + wireDelay(input_i) }
    ///   slack(net_i)  = target(gate) - arrival(net_i) - wireDelay(net_i)
    /// スラック > 0 のネットは target に合わせて遅延を追加する必要がある。
    /// 同一ネットが複数ゲートを駆動する場合は最小スラックを採用する。
    let computeSlack (placement: Placement) (wires: Wire list) (arrivals: ArrivalMap)
        : Map<NetId, int<gen>> =
        let wireDelay =
            wires |> List.map (fun w -> w.Net, w.Delay) |> Map.ofList

        let inputArrivalAt n =
            (Map.tryFind n arrivals   |> Option.defaultValue 0<gen>)
            + (Map.tryFind n wireDelay |> Option.defaultValue 0<gen>)

        placement
        |> List.collect (fun p ->
            if List.isEmpty p.Gate.Inputs then []
            else
                let target = p.Gate.Inputs |> List.map inputArrivalAt |> List.max
                p.Gate.Inputs |> List.map (fun n -> n, target - inputArrivalAt n))
        |> List.groupBy fst
        |> List.map (fun (k, vs) -> k, vs |> List.map snd |> List.min)
        |> Map.ofList

    /// パスに extra 世代分のジグザグ迂回を物理的に挿入する。
    ///
    /// 戦略: パスの終端直前の点 pivot から -Y 方向へ N/2 セル往復する。
    ///   これで 2*(N/2) = N セル追加 (偶数)。
    ///   奇数の extra は N+1 に切り上げる (STA は 1 gen 余裕を持って吸収)。
    /// 前提: pivot の Y < 0 空間が空いている (直線配置なら常に成立)。
    let extendPath (extra: int<gen>) (path: Coord list) : Coord list =
        let n = int extra
        if n <= 0 || path.Length < 2 then path
        else
            let n' = if n % 2 = 0 then n else n + 1  // 偶数に切り上げ
            let halfN = n' / 2
            // pivot = 終端 dst の 1 つ手前
            let pivot = List.item (path.Length - 2) path
            let dst   = List.last path
            let initPath = path |> List.take (path.Length - 2)
            // pivot から -Y へ halfN 歩、戻り pivot を経由して dst へ
            let up   = [ for i in 1 .. halfN          -> { X = pivot.X; Y = pivot.Y - i } ]
            let down = [ for i in halfN - 1 .. -1 .. 0 -> { X = pivot.X; Y = pivot.Y - i } ]
            // down の末尾は pivot 自身 (i=0)
            initPath @ [pivot] @ up @ down @ [dst]

    /// スラックが正の Wire に DELAY_n 相当の遅延を物理的に付加して均等化する。
    /// Path を extendPath でジグザグ延長し、Delay を加算式で更新する。
    ///   新 Delay = 旧 Delay + slack  (パス長依存ではなく加算: ofPath が N-1 形式を想定)
    let insertDelays (slack: Map<NetId, int<gen>>) (wires: Wire list) : Wire list =
        wires |> List.map (fun w ->
            match Map.tryFind w.Net slack with
            | Some s when s > 0<gen> ->
                let path' = extendPath s w.Path
                { w with Path = path'; Delay = w.Delay + s }
            | _ -> w)


// ---------------------------------------------------------------------
// 7. クロック注入付きシミュレーター
//    STA 結果から各ゲートへのクロック注入時刻を自動計算し、
//    step-wise でシミュレーションを走らせる。
// ---------------------------------------------------------------------
module Sim =
    open Units
    open Domain
    open Netlist
    open Library
    open Place
    open Route
    open Sta
    open Rule

    /// 配置済みゲートのクロックポート絶対座標を返す。
    /// Gate.Inputs.Length 番目以降の In ポートをクロック用とみなす。
    let clockCoords (p: Placed) : Coord list =
        let inPorts = p.Cell.Ports |> List.filter (fun port -> port.Role = In)
        let nLogical = p.Gate.Inputs.Length
        inPorts
        |> List.mapi (fun i port -> i, port)
        |> List.choose (fun (i, port) ->
            if i >= nLogical then Some (portCoord p port) else None)

    /// ゲート G のクロック注入時刻 = G の全入力の到達時刻の最大値 (= target)。
    let clockTimeOf (p: Placed) (arrivals: ArrivalMap) (wireDelay: Map<NetId, int<gen>>) : int<gen> =
        if p.Gate.Inputs.IsEmpty then 0<gen>
        else
            p.Gate.Inputs |> List.map (fun n ->
                (arrivals |> Map.tryFind n |> Option.defaultValue 0<gen>)
                + (wireDelay |> Map.tryFind n |> Option.defaultValue 0<gen>))
            |> List.max

    /// 注入マップ (世代 → 座標リスト) を使って `steps` 世代進める。
    /// 各イテレーション: 該当世代の Head を注入してから Rule.step を呼ぶ。
    let runWithInjections (injections: Map<int<gen>, Coord list>) (g: Grid) (steps: int) : Grid =
        let mutable state = g
        for idx in 0 .. steps - 1 do
            let t = idx * 1<gen>
            match Map.tryFind t injections with
            | Some coords ->
                state <- coords |> List.fold (fun acc c -> Map.add c Head acc) state
            | None -> ()
            state <- step state
        state

    /// クロック自動注入付き WireWorld シミュレーション。
    ///   placement: 配置情報 (クロックポート位置取得に使用)
    ///   arrivals:  STA 計算済み到達時刻マップ
    ///   wires:     遅延情報付き配線リスト
    ///   dataInj:   データ信号手動注入: (座標, 注入世代) list
    ///   grid:      コンパイル済み Grid (emit 済み)
    ///   steps:     シミュレーション世代数
    let runWithClocks
        (placement: Placement)
        (arrivals: ArrivalMap)
        (wires: Wire list)
        (dataInj: (Coord * int<gen>) list)
        (grid: Grid)
        (steps: int)
        : Grid =
        let wireDelay = wires |> List.map (fun w -> w.Net, w.Delay) |> Map.ofList

        let allEntries =
            [ yield! placement |> List.collect (fun p ->
                let t = clockTimeOf p arrivals wireDelay
                clockCoords p |> List.map (fun c -> t, c))
              yield! dataInj |> List.map (fun (c, t) -> t, c) ]
            |> List.groupBy fst
            |> List.map (fun (t, pairs) -> t, List.map snd pairs)
            |> Map.ofList

        runWithInjections allEntries grid steps


// ---------------------------------------------------------------------
// 8. パイプライン: 各段の型と railway 合成
// ---------------------------------------------------------------------
module Pipeline =
    open Units
    open Domain
    open Netlist
    open Library
    open Place
    open Route

    // --- Yosys write_json の最小デシリアライズ型 ---
    // `synth -flatten; abc -g AND,NOT; write_json` の出力に対応。

    type YosysPort =
        { Direction: string       // "input" | "output"
          Bits: int list }        // ネット番号 (= NetId の元になる)

    type YosysCell =
        { Type: string                        // "$_NOT_", "$_AND_" など
          PortDirections: Map<string, string> // ポート名 → "input"/"output"
          Connections: Map<string, int list> } // ポート名 → ネット番号リスト

    type YosysModule =
        { Ports: Map<string, YosysPort>
          Cells: Map<string, YosysCell> }

    type CompileError =
        | ParseError      of string
        | UnmappableGate  of GateKind
        | PlacementOverflow
        | RoutingCongestion of NetId
        | TimingViolation of NetId * expected: int<gen> * actual: int<gen>

    // --- 段の境界。それぞれ別の型を返すので順序を取り違えられない ---

    /// JSON の bits 配列から整数ネット番号だけを抽出する。
    /// Yosys は定数 "0"/"1" を文字列で埋め込む場合があるので数値要素のみ取る。
    let private parseBits (el: System.Text.Json.JsonElement) : int list =
        el.EnumerateArray()
        |> Seq.choose (fun b ->
            if b.ValueKind = System.Text.Json.JsonValueKind.Number
            then Some (b.GetInt32())
            else None)
        |> List.ofSeq

    /// Yosys write_json 出力を YosysModule へパースする。
    /// JSON 構造: { "modules": { "<top>": { "ports": {...}, "cells": {...} } } }
    /// 複数モジュールがある場合は "top" を優先し、なければ先頭を使用する。
    let parseYosysJson (json: string) : Result<YosysModule, CompileError> =
        try
            use doc = System.Text.Json.JsonDocument.Parse(json)
            let modulesEl = doc.RootElement.GetProperty("modules")
            let entries = modulesEl.EnumerateObject() |> Array.ofSeq
            if entries.Length = 0 then
                Error (ParseError "no modules in JSON")
            else
                let topEntry =
                    entries |> Array.tryFind (fun e -> e.Name = "top")
                    |> Option.defaultValue entries.[0]
                let m = topEntry.Value

                let ports =
                    m.GetProperty("ports").EnumerateObject()
                    |> Seq.map (fun p ->
                        let dir  = p.Value.GetProperty("direction").GetString()
                        let bits = parseBits (p.Value.GetProperty("bits"))
                        p.Name, { Direction = dir; Bits = bits })
                    |> Map.ofSeq

                let cells =
                    m.GetProperty("cells").EnumerateObject()
                    |> Seq.map (fun c ->
                        let v = c.Value
                        let cellType = v.GetProperty("type").GetString()
                        let portDirs =
                            v.GetProperty("port_directions").EnumerateObject()
                            |> Seq.map (fun p -> p.Name, p.Value.GetString())
                            |> Map.ofSeq
                        let conns =
                            v.GetProperty("connections").EnumerateObject()
                            |> Seq.map (fun p -> p.Name, parseBits p.Value)
                            |> Map.ofSeq
                        c.Name, { Type = cellType; PortDirections = portDirs; Connections = conns })
                    |> Map.ofSeq

                Ok { Ports = ports; Cells = cells }
        with ex ->
            Error (ParseError ex.Message)

    /// Yosys type 文字列を GateKind に変換する。
    /// `abc -g NAND,NOT` の出力は $\_NOT\_ と $\_NAND\_ のみ。
    let private parseGateKind (t: string) : GateKind option =
        match t with
        | "$_NOT_"   -> Some Not
        | "$_NAND_"  -> Some Nand
        | "$_AND_"   -> Some And    // abc -g AND,NOT 使用時の後方互換
        | "$_OR_"    -> Some Or
        | "$_XOR_"   -> Some Xor    // abc -g NAND,NOT では出力されないが後方互換用に残す
        | "$_DFF_P_" -> Some Dff
        | "$_BUF_"   -> Some Buf
        | _          -> None

    /// YosysModule を Netlist IR へ変換する。
    let yosysToNetlist (m: YosysModule) : Result<Netlist, CompileError> =
        // ① ports → PrimaryInputs / PrimaryOutputs / ClockNet
        let primaryInputs =
            m.Ports |> Map.toList
            |> List.filter (fun (_, p) -> p.Direction = "input")
            |> List.collect (fun (_, p) -> p.Bits |> List.map NetId)

        let primaryOutputs =
            m.Ports |> Map.toList
            |> List.filter (fun (_, p) -> p.Direction = "output")
            |> List.collect (fun (_, p) -> p.Bits |> List.map NetId)

        let clockNet =
            m.Ports |> Map.tryFindKey (fun name _ ->
                let n = name.ToLowerInvariant()
                n = "clk" || n = "clock" || n = "ck" || n = "clk_i")
            |> Option.bind (fun name ->
                m.Ports.[name].Bits |> List.tryHead |> Option.map NetId)

        // ② cells → Gate list
        let gateResults =
            m.Cells |> Map.toList
            |> List.mapi (fun i (name, cell) ->
                match parseGateKind cell.Type with
                | None -> Error (ParseError $"unknown gate type '{cell.Type}' in cell '{name}'")
                | Some kind ->
                    let inputs =
                        cell.PortDirections |> Map.toList
                        |> List.filter (fun (_, dir) -> dir = "input")
                        |> List.sortBy fst        // 名前順: A → B → C → ...
                        |> List.collect (fun (port, _) ->
                            cell.Connections |> Map.tryFind port
                            |> Option.defaultValue []
                            |> List.map NetId)

                    let outputNet =
                        cell.PortDirections |> Map.toList
                        |> List.filter (fun (_, dir) -> dir = "output")
                        |> List.sortBy fst
                        |> List.tryHead
                        |> Option.bind (fun (port, _) ->
                            cell.Connections |> Map.tryFind port
                            |> Option.bind List.tryHead
                            |> Option.map NetId)

                    match outputNet with
                    | None    -> Error (ParseError $"cell '{name}' has no output connection")
                    | Some o  -> Ok { Id = i; Kind = kind; Inputs = inputs; Output = o })

        gateResults
        |> List.fold (fun acc r ->
            match acc, r with
            | Ok xs, Ok x -> Ok (x :: xs)
            | Error e, _  -> Error e
            | _, Error e  -> Error e) (Ok [])
        |> Result.map (fun gates ->
            { Gates          = List.rev gates
              PrimaryInputs  = primaryInputs
              PrimaryOutputs = primaryOutputs
              ClockNet       = clockNet })

    /// Frontend: Yosys JSON 文字列 → Netlist。
    /// 呼び出し元は `yosys -p "synth -flatten; abc -g NAND,NOT; write_json out.json" design.v`
    /// で生成した JSON ファイルの内容を渡す。
    let frontend (src: string) : Result<Netlist, CompileError> =
        parseYosysJson src |> Result.bind yosysToNetlist

    /// テクノロジマッピング: 各ゲートを StdCell に割り当てる。
    let techMap (lib: CellLibrary) (nl: Netlist)
        : Result<(Gate * StdCell) list, CompileError> =
        nl.Gates
        |> List.map (fun g ->
            match Map.tryFind g.Kind lib with
            | Some cell -> Ok (g, cell)
            | None      -> Error (UnmappableGate g.Kind))
        |> List.fold (fun acc r ->
            match acc, r with
            | Ok xs, Ok x   -> Ok (x :: xs)
            | Error e, _    -> Error e
            | _, Error e    -> Error e) (Ok [])
        |> Result.map List.rev

    /// 配置: グリッドにセルを並べる (force-directed 等)。ここでは行優先の素朴版。
    let place (mapped: (Gate * StdCell) list) : Result<Placement, CompileError> =
        let mutable cursorX = 0
        let placed =
            mapped |> List.map (fun (g, cell) ->
                let origin = { X = cursorX; Y = 0 }
                cursorX <- cursorX + cell.Size.X + 4   // 4 セルの間隔
                { Gate = g; Cell = cell; Origin = origin })
        Ok placed

    /// 配線: Lee 法でゲート間ネットを配線する。
    /// ポートは Blocked 領域内に存在するため src/dst は例外扱いで通過させる。
    /// クロックポート (Role=Clock) および論理入力数を超える物理 In ポートは今回スキップ。
    let route (placement: Placement) : Result<Wire list, CompileError> =
        let baseGrid = buildGrid placement

        // 出力ポート座標: NetId → Coord
        let outCoords =
            placement |> List.choose (fun p ->
                p.Cell.Ports |> List.tryFind (fun port -> port.Role = Out)
                |> Option.map (fun port -> p.Gate.Output, portCoord p port))
            |> Map.ofList

        // 入力ポート座標: (NetId, Coord) list
        // Seq.zip で Gate.Inputs と In ポートを短い方に合わせて対応付ける
        let inCoordPairs =
            placement |> List.collect (fun p ->
                let inPorts = p.Cell.Ports |> List.filter (fun port -> port.Role = In)
                Seq.zip p.Gate.Inputs inPorts
                |> Seq.map (fun (netId, port) -> netId, portCoord p port)
                |> List.ofSeq)

        let inCoordsMap =
            inCoordPairs
            |> List.groupBy fst
            |> List.map (fun (k, vs) -> k, List.map snd vs)
            |> Map.ofList

        // src と dst がそろっている内部ネットのみ配線
        let nets =
            outCoords |> Map.toList
            |> List.choose (fun (netId, src) ->
                inCoordsMap |> Map.tryFind netId
                |> Option.map (fun dsts -> netId, src, dsts))

        let routeOne (grid: RoutingGrid) (wires: Wire list) (netId: NetId) (src: Coord) (dst: Coord)
            : Result<RoutingGrid * Wire list, CompileError> =
            match leePath grid src dst with
            | None -> Error (RoutingCongestion netId)
            | Some path ->
                let wire = ofPath netId path
                let grid' =
                    path |> List.fold (fun g c ->
                        match Map.tryFind c baseGrid with
                        | Some Blocked -> g     // ポート座標の Blocked 領域は上書きしない
                        | _ -> Map.add c (Routed netId) g) grid
                Ok (grid', wire :: wires)

        nets
        |> List.fold (fun acc (netId, src, dsts) ->
            acc |> Result.bind (fun (g, ws) ->
                dsts |> List.fold (fun acc2 dst ->
                    acc2 |> Result.bind (fun (g2, ws2) -> routeOne g2 ws2 netId src dst)
                ) (Ok (g, ws))))
            (Ok (baseGrid, []))
        |> Result.map (fun (_, wires) -> List.rev wires)

    /// ★ タイミング均等化 ★ — WireWorld 設計の肝。
    /// あるゲートの全入力で「信号到達世代」が一致しないと誤動作する。
    /// 速いパスに遅延 (蛇行配線・遅延ループ) を足して最も遅いパスに合わせる。
    let balanceGateInputs (inputs: Wire list) : Result<Wire list, CompileError> =
        match inputs with
        | [] -> Ok []
        | _  ->
            let target = inputs |> List.map (fun w -> w.Delay) |> List.max
            // 各入力を target まで遅延パディング。実体としては Path を蛇行で
            // 伸ばす操作だが、ここでは Delay の調整として表現する。
            inputs
            |> List.map (fun w ->
                if w.Delay = target then Ok w
                elif w.Delay < target then
                    Ok { w with Delay = target }   // ← 実装では Path を延長
                else
                    // target は max なので理論上ここには来ない
                    Error (TimingViolation (w.Net, target, w.Delay)))
            |> List.fold (fun acc r ->
                match acc, r with
                | Ok xs, Ok x -> Ok (x :: xs)
                | Error e, _  -> Error e
                | _, Error e  -> Error e) (Ok [])
            |> Result.map List.rev

    /// Emitter: 配置 + 配線を 1 枚の Grid に合成する。
    let emit (placement: Placement) (wires: Wire list) : Grid =
        // セルパターンを絶対座標へ平行移動して合成
        let cells =
            placement
            |> List.collect (fun p ->
                p.Cell.Pattern
                |> Map.toList
                |> List.map (fun (c, s) ->
                    { X = c.X + p.Origin.X; Y = c.Y + p.Origin.Y }, s))
        // 配線は導線として敷く
        let routed =
            wires
            |> List.collect (fun w -> w.Path |> List.map (fun c -> c, Wire))
        (cells @ routed) |> Map.ofList

    /// Golly 拡張 RLE 出力 (state: 1=Head 'A', 2=Tail 'B', 3=Wire 'C')。
    let toRle (g: Grid) : string =
        if Map.isEmpty g then "x = 0, y = 0, rule = WireWorld\n!"
        else
            let xs = g |> Map.toSeq |> Seq.map (fun (c, _) -> c.X)
            let ys = g |> Map.toSeq |> Seq.map (fun (c, _) -> c.Y)
            let minX, maxX = Seq.min xs, Seq.max xs
            let minY, maxY = Seq.min ys, Seq.max ys
            let w, h = maxX - minX + 1, maxY - minY + 1
            let charOf = function Head -> "A" | Tail -> "B" | Wire -> "C" | Empty -> "."
            let sb = System.Text.StringBuilder()
            sb.Append(sprintf "x = %d, y = %d, rule = WireWorld\n" w h) |> ignore
            for y in minY .. maxY do
                for x in minX .. maxX do
                    sb.Append(charOf (get g { X = x; Y = y })) |> ignore
                sb.Append(if y = maxY then "!" else "$") |> ignore
            sb.ToString()

    // --- railway 演算子 ---
    let (>>=) m f = Result.bind f m

    /// トップレベル: HDL ソース → WireWorld Grid。
    let compile (lib: CellLibrary) (src: string) : Result<Grid, CompileError> =
        frontend src >>= fun nl ->
        techMap lib nl >>= fun mapped ->
        place mapped >>= fun placement ->
        route placement >>= fun wires ->
        let arrivals = Sta.computeArrival placement wires
        let slack    = Sta.computeSlack   placement wires arrivals
        let wires'   = Sta.insertDelays   slack wires
        Ok (emit placement wires')

    /// Grid + Placement + Wire list を返す詳細版 (テスト・デバッグ用)。
    let compileFull (lib: CellLibrary) (src: string)
        : Result<Grid * Placement * Wire list, CompileError> =
        frontend src >>= fun nl ->
        techMap lib nl >>= fun mapped ->
        place mapped >>= fun placement ->
        route placement >>= fun wires ->
        let arrivals = Sta.computeArrival placement wires
        let slack    = Sta.computeSlack   placement wires arrivals
        let wires'   = Sta.insertDelays   slack wires
        Ok (emit placement wires', placement, wires')


// ---------------------------------------------------------------------
// 8. フロントエンド単体テスト (M2)
//    parseYosysJson → yosysToNetlist のパイプラインを JSON 文字列で検証する。
// ---------------------------------------------------------------------
module FrontendTest =
    open Netlist
    open Pipeline

    /// AND-NOT 2 ゲート回路: NAND(a,b) + NOT(nand_out) = AND(a,b)
    /// `abc -g NAND,NOT` が出力する典型的な JSON。
    let andNotJson = """
{
  "modules": {
    "top": {
      "ports": {
        "a": { "direction": "input",  "bits": [2] },
        "b": { "direction": "input",  "bits": [3] },
        "y": { "direction": "output", "bits": [5] }
      },
      "cells": {
        "u0": {
          "type": "$_NAND_",
          "port_directions": { "A": "input", "B": "input", "Y": "output" },
          "connections": { "A": [2], "B": [3], "Y": [4] }
        },
        "u1": {
          "type": "$_NOT_",
          "port_directions": { "A": "input", "Y": "output" },
          "connections": { "A": [4], "Y": [5] }
        }
      }
    }
  }
}"""

    let runAll () : (string * bool) list =
        let result = frontend andNotJson

        [ "parse succeeds",
            match result with Ok _ -> true | _ -> false

          "gate count = 2",
            match result with
            | Ok nl -> nl.Gates.Length = 2
            | _ -> false

          "primary inputs = [2;3]",
            match result with
            | Ok nl -> nl.PrimaryInputs = [NetId 2; NetId 3]
            | _ -> false

          "primary outputs = [5]",
            match result with
            | Ok nl -> nl.PrimaryOutputs = [NetId 5]
            | _ -> false

          "clock net = None",
            match result with
            | Ok nl -> nl.ClockNet = None
            | _ -> false

          "u0 is Nand with inputs [2;3] output 4",
            match result with
            | Ok nl ->
                nl.Gates |> List.exists (fun g ->
                    g.Kind = Nand &&
                    g.Inputs = [NetId 2; NetId 3] &&
                    g.Output = NetId 4)
            | _ -> false

          "u1 is Not with input [4] output 5",
            match result with
            | Ok nl ->
                nl.Gates |> List.exists (fun g ->
                    g.Kind = Not &&
                    g.Inputs = [NetId 4] &&
                    g.Output = NetId 5)
            | _ -> false

          "techMap succeeds with defaultLib",
            match result |> Result.bind (techMap Library.defaultLib) with
            | Ok mapped -> mapped.Length = 2
            | _ -> false
        ]


// ---------------------------------------------------------------------
// 9. ルーティング単体テスト (M3)
//    Lee 法 BFS + route + emit のパイプラインを 4 ゲート回路で検証する。
// ---------------------------------------------------------------------
module RoutingTest =
    open Domain
    open Netlist
    open Library
    open Place
    open Route
    open Pipeline

    /// 4 NOT チェーン: a → NOT → NOT → NOT → NOT → y
    /// 内部ネット 3→4, 4→5, 5→6 の 3 本を配線することを確認する。
    let chainJson = """
{
  "modules": {
    "top": {
      "ports": {
        "a": { "direction": "input",  "bits": [2] },
        "y": { "direction": "output", "bits": [6] }
      },
      "cells": {
        "u0": { "type": "$_NOT_", "port_directions": {"A":"input","Y":"output"}, "connections": {"A":[2],"Y":[3]} },
        "u1": { "type": "$_NOT_", "port_directions": {"A":"input","Y":"output"}, "connections": {"A":[3],"Y":[4]} },
        "u2": { "type": "$_NOT_", "port_directions": {"A":"input","Y":"output"}, "connections": {"A":[4],"Y":[5]} },
        "u3": { "type": "$_NOT_", "port_directions": {"A":"input","Y":"output"}, "connections": {"A":[5],"Y":[6]} }
      }
    }
  }
}"""

    let runAll () : (string * bool) list =
        let lib = Library.defaultLib

        // leePath の単体テスト
        let emptyGrid : RoutingGrid = Map.empty
        // src の 4 近傍すべてをブロック → BFS が 1 歩も進めず到達不能
        let blockedGrid : RoutingGrid =
            [ {X=1;Y=0}; {X= -1;Y=0}; {X=0;Y=1}; {X=0;Y= -1} ]
            |> List.map (fun c -> c, Blocked)
            |> Map.ofList

        // compile end-to-end
        let gridResult = compile lib chainJson

        // placement + wires を個別に取得してアサート
        let detailResult =
            frontend chainJson
            |> Result.bind (techMap lib)
            |> Result.bind place
            |> Result.bind (fun pl ->
                route pl |> Result.map (fun ws -> pl, ws))

        let obstacleGrid =
            [ for y in 0..4 -> { X=2; Y=y }, Blocked ] |> Map.ofList

        [ "leePath trivial (src=dst)",
            leePath emptyGrid {X=0;Y=0} {X=0;Y=0} = Some [{X=0;Y=0}]

          "leePath straight 3 cells",
            leePath emptyGrid {X=0;Y=0} {X=2;Y=0}
            |> Option.map List.length = Some 3

          "leePath blocked returns None",
            leePath blockedGrid {X=0;Y=0} {X=4;Y=0} = None

          "leePath goes around obstacle",
            leePath obstacleGrid {X=0;Y=2} {X=4;Y=2} |> Option.isSome

          "compile chain succeeds",
            match gridResult with Ok _ -> true | _ -> false

          "compile chain grid is non-empty",
            match gridResult with
            | Ok g -> not (Map.isEmpty g)
            | _ -> false

          "placement has 4 gates",
            match detailResult with
            | Ok (pl, _) -> pl.Length = 4
            | _ -> false

          "routing produces 3 wires (3 internal nets)",
            match detailResult with
            | Ok (_, ws) -> ws.Length = 3
            | _ -> false

          "wires cover nets 3,4,5",
            match detailResult with
            | Ok (_, ws) ->
                let nets = ws |> List.map (fun w -> w.Net) |> List.sort
                nets = [NetId 3; NetId 4; NetId 5]
            | _ -> false

          "wire paths are non-empty and start/end at expected coords",
            match detailResult with
            | Ok (_, ws) ->
                // not1 cell: size 5×3, out=(4,1), in[0]=(0,0)
                // gate spacing = size.X(5) + gap(4) = 9
                // u0 origin=(0,0) out-abs=(4,1); u1 origin=(9,0) in[0]-abs=(9,0)
                ws |> List.tryFind (fun w -> w.Net = NetId 3)
                |> Option.exists (fun w ->
                    List.head w.Path = {X=4;Y=1} &&
                    List.last w.Path = {X=9;Y=0})
            | _ -> false

          "emit wire cells present in grid",
            match gridResult with
            | Ok g ->
                // routing path between u0 and u1 must include some free cells
                // (5,1) is in the gap between gate 0 (x:0-4) and gate 1 (x:9-13)
                Map.containsKey {X=5;Y=1} g
            | _ -> false
        ]


// ---------------------------------------------------------------------
// 10. STA 単体テスト (M4)
// ---------------------------------------------------------------------
module StaTest =
    open Units
    open Netlist
    open Library
    open Place
    open Route
    open Sta
    open Pipeline

    // ── ヘルパー: ダミー Placed を作る ──────────────────────────────────
    let private makePlaced (id: int) (kind: GateKind) (inputs: int list) (output: int) (cell: StdCell) : Placed =
        { Gate   = { Id = id; Kind = kind; Inputs = inputs |> List.map NetId; Output = NetId output }
          Cell   = cell
          Origin = { X = id * (cell.Size.X + 4); Y = 0 } }

    // ── テスト 1: 2-NOT チェーン (対称) ─────────────────────────────────
    //   net2(pi) → NOT(u0) → net3 --wire(delay=7)--> NOT(u1) → net4
    //   arrival(net2) = 0
    //   arrival(net3) = 0 + 0(no wire for pi) + 4(latency) = 4
    //   arrival(net4) = 4 + 7(wire) + 4(latency) = 15
    //   slack(net3 wire) = 0  (only input to u1, it IS the target)
    let private chain2 =
        [ makePlaced 0 Not [2] 3 Library.not1
          makePlaced 1 Not [3] 4 Library.not1 ]

    let private chain2Wires =
        [ { Net = NetId 3; Path = [ for x in 4..10 -> {X=x;Y=1} ]; Delay = 7<gen> } ]

    // ── テスト 2: NAND(a,b) — 入力パスの非対称スラック ──────────────────
    //   net2(pi), net3(pi)  → NAND(u0) → net4
    //   wire net2: delay=5,  wire net3: delay=3
    //   target = max(0+5, 0+3) = 5
    //   slack(net2 wire) = 5-5 = 0,  slack(net3 wire) = 5-3 = 2
    let private nandPlaced =
        [ makePlaced 0 Nand [2; 3] 4 Library.junc3 ]

    let private nandWires =
        [ { Net = NetId 2; Path = [ for x in 0..4  -> {X=x;Y=0} ]; Delay = 5<gen> }
          { Net = NetId 3; Path = [ for x in 0..2  -> {X=x;Y=0} ]; Delay = 3<gen> } ]

    let runAll () : (string * bool) list =
        let arr2    = computeArrival chain2 chain2Wires
        let slk2    = computeSlack   chain2 chain2Wires arr2
        let wires2' = insertDelays   slk2  chain2Wires

        let arrN    = computeArrival nandPlaced nandWires
        let slkN    = computeSlack   nandPlaced nandWires arrN
        let wiresN' = insertDelays   slkN  nandWires
        let net3DelayAfterInsert =
            wiresN' |> List.find (fun w -> w.Net = NetId 3) |> fun w -> w.Delay

        [ "chain2: arrival(net2)=0 (primary input)",
            Map.tryFind (NetId 2) arr2 = Some 0<gen>

          "chain2: arrival(net3)=4 (u0 output after Latency=4)",
            Map.tryFind (NetId 3) arr2 = Some 4<gen>

          "chain2: arrival(net4)=15 (4 + wire7 + Latency4)",
            Map.tryFind (NetId 4) arr2 = Some 15<gen>

          "chain2: slack(net3)=0 (only path, no slack)",
            Map.tryFind (NetId 3) slk2 = Some 0<gen>

          "chain2: insertDelays is no-op when all slacks=0",
            wires2' = chain2Wires

          "nand: arrival(net2)=0, arrival(net3)=0 (both primary)",
            Map.tryFind (NetId 2) arrN = Some 0<gen> &&
            Map.tryFind (NetId 3) arrN = Some 0<gen>

          "nand: arrival(net4)=5+4=9 (target=5, Latency=4)",
            Map.tryFind (NetId 4) arrN = Some 9<gen>

          "nand: slack(net2 wire)=0 (critical path)",
            Map.tryFind (NetId 2) slkN = Some 0<gen>

          "nand: slack(net3 wire)=2 (shorter path needs 2 gen delay)",
            Map.tryFind (NetId 3) slkN = Some 2<gen>

          "nand: insertDelays adds 2 to net3 wire delay (3+2=5)",
            net3DelayAfterInsert = 5<gen>

          "compile 4-NOT chain still passes with STA in pipeline",
            match Pipeline.compile Library.defaultLib RoutingTest.chainJson with
            | Ok g -> not (Map.isEmpty g)
            | _    -> false
        ]


// ---------------------------------------------------------------------
// 11. E2E シミュレーション検証 (M5)
//     コンパイラが生成した Grid を Rule.run で動かし正しい論理値を確認する。
// ---------------------------------------------------------------------
module E2eTest =
    open Units
    open Domain
    open Netlist
    open Library
    open Place
    open Route
    open Sta
    open Pipeline
    open Rule

    // ── ヘルパー ─────────────────────────────────────────────────────────

    /// 配置済みゲートのクロックポート座標を返す。
    /// In ポートのうち Gate.Inputs.Length 番目以降が「論理入力を超えた物理ポート」= クロック用。
    let clockCoords (p: Placed) : Coord list =
        let inPorts = p.Cell.Ports |> List.filter (fun port -> port.Role = In)
        let nLogical = p.Gate.Inputs.Length
        inPorts
        |> List.mapi (fun i port -> i, port)
        |> List.choose (fun (i, port) ->
            if i >= nLogical then Some (portCoord p port) else None)

    /// 格子上に Head を注入し `steps` 世代後の状態を返す。
    let inject (coords: Coord list) (g: Grid) : Grid =
        coords |> List.fold (fun acc c -> Map.add c Head acc) g

    /// Grid + Placement を受け取り、全ゲートのクロックポートに Head を注入する。
    let injectClocks (placement: Placement) (g: Grid) : Grid =
        placement |> List.collect clockCoords |> fun cs -> inject cs g

    // ── extendPath 単体テスト ────────────────────────────────────────────

    let private straightPath n = [ for x in 0..n -> { X=x; Y=0 } ]

    // ── NOT ゲート E2E JSON ──────────────────────────────────────────────

    let private notJson = """
{
  "modules": { "top": {
    "ports": {
      "a": {"direction":"input","bits":[2]},
      "y": {"direction":"output","bits":[3]}
    },
    "cells": {
      "u0": {"type":"$_NOT_",
             "port_directions":{"A":"input","Y":"output"},
             "connections":{"A":[2],"Y":[3]}}
    }
  }}
}"""

    let runAll () : (string * bool) list =
        // ── extendPath テスト ──
        let path5  = straightPath 4              // 5 cells, Delay=5
        let ext2   = extendPath 2<gen> path5     // add 2: should be 7 cells
        let ext4   = extendPath 4<gen> path5     // add 4: should be 9 cells
        let ext3   = extendPath 3<gen> path5     // odd: rounds up to 4 → 9 cells

        // パスが連続している (各ステップが隣接) か検証
        let isContinuous (p: Coord list) =
            p |> List.pairwise
              |> List.forall (fun (a, b) ->
                  abs (a.X - b.X) + abs (a.Y - b.Y) = 1)

        // ── NOT E2E コンパイル ──
        let lib = Library.defaultLib
        let fullResult = compileFull lib notJson

        // not1 ゲートのポート座標 (origin = (0,0) で place される)
        // Port[0]=(0,0)=A, Port[1]=(0,1)=clk1, Port[2]=(0,2)=clk2, Out=(4,1)
        let portA    = { X=0; Y=0 }
        let portClk1 = { X=0; Y=1 }
        let portClk2 = { X=0; Y=2 }
        let portOut  = { X=4; Y=1 }

        let latency = int Library.not1.Latency

        // NOT(A=0)=1: クロックのみ注入 → Latency 後に出力 Head
        let not0Result =
            match fullResult with
            | Ok (grid, placement, _) ->
                let g = grid |> inject [portClk1; portClk2]
                get (run latency g) portOut = Head
            | _ -> false

        // NOT(A=1)=0: A + クロック注入 → Latency 後に出力 Head なし
        let not1Result =
            match fullResult with
            | Ok (grid, placement, _) ->
                let g = grid |> inject [portA; portClk1; portClk2]
                get (run latency g) portOut = Head
            | _ -> true   // error → fail

        // ── テスト一覧 ──
        [ "extendPath: +2 → length 7",
            ext2.Length = 7

          "extendPath: +4 → length 9",
            ext4.Length = 9

          "extendPath: +3 (odd) → length 9 (rounded up to +4)",
            ext3.Length = 9

          "extendPath: result is continuous (each step adjacent)",
            isContinuous ext2 && isContinuous ext4 && isContinuous ext3

          "extendPath: endpoints unchanged",
            List.head ext2 = List.head path5 &&
            List.last ext2 = List.last path5

          "NOT E2E: compileFull succeeds",
            match fullResult with Ok _ -> true | _ -> false

          "NOT E2E: grid is non-empty",
            match fullResult with
            | Ok (g, _, _) -> not (Map.isEmpty g)
            | _ -> false

          "NOT E2E: port A is Wire in compiled grid",
            match fullResult with
            | Ok (g, _, _) -> get g portA = Wire
            | _ -> false

          "NOT E2E: NOT(A=0)=1  (clocks only → output fires)",
            not0Result

          "NOT E2E: NOT(A=1)=0  (A + clocks → output blocked)",
            not (not1Result)

          "NOT E2E: insertDelays with slack=2 extends path length",
            let p = straightPath 6   // 7 cells, Delay=7
            let w = { Net = NetId 1; Path = p; Delay = 7<gen> }
            let slack = Map.ofList [ NetId 1, 2<gen> ]
            match insertDelays slack [w] with
            | [w'] -> w'.Path.Length = 9 && w'.Delay = 9<gen>
            | _    -> false
        ]


// ---------------------------------------------------------------------
// 12. 多段回路 E2E テスト (クロック自動注入)
// ---------------------------------------------------------------------
module MultiStageTest =
    open Units
    open Domain
    open Netlist
    open Library
    open Place
    open Route
    open Sta
    open Sim
    open Pipeline

    /// 2-NOT チェーン: a → NOT(u0) → NOT(u1) → y
    /// 二重反転なので NOT(NOT(a)) = a。
    let private twoNotJson = """
{
  "modules": { "top": {
    "ports": {
      "a": {"direction":"input","bits":[2]},
      "y": {"direction":"output","bits":[4]}
    },
    "cells": {
      "u0": {"type":"$_NOT_","port_directions":{"A":"input","Y":"output"},"connections":{"A":[2],"Y":[3]}},
      "u1": {"type":"$_NOT_","port_directions":{"A":"input","Y":"output"},"connections":{"A":[3],"Y":[4]}}
    }
  }}
}"""

    /// a の値を注入して 2-NOT チェーンを実行し、y の Head 有無を返す。
    ///   期待: NOT(NOT(a)) = a
    /// totalSteps = STA arrival(output) で出力発火の瞬間に止める。
    /// arrival より多く回すと Head→Tail になって検出できなくなる。
    let private runTwoNot (a: bool) : bool =
        let lib = Library.defaultLib
        match compileFull lib twoNotJson with
        | Error _ -> false
        | Ok (grid, placement, wires) ->
            let arrivals = computeArrival placement wires
            let u0 = placement |> List.find (fun p -> p.Gate.Output = NetId 3)
            let u0inPort =
                u0.Cell.Ports |> List.find (fun p -> p.Role = In) |> portCoord u0
            let dataInj = if a then [(u0inPort, 0<gen>)] else []
            let u1 = placement |> List.find (fun p -> p.Gate.Output = NetId 4)
            let u1outPort =
                u1.Cell.Ports |> List.find (fun p -> p.Role = Out) |> portCoord u1
            // STA が計算した到達時刻 = 出力ポートが Head になる世代
            let totalSteps =
                arrivals |> Map.tryFind (NetId 4) |> Option.map int |> Option.defaultValue 20
            let result = runWithClocks placement arrivals wires dataInj grid totalSteps
            get result u1outPort = Head

    /// 3-NOT チェーン: a → NOT→NOT→NOT → y
    /// NOT(NOT(NOT(a))) = NOT(a)。
    let private threeNotJson = """
{
  "modules": { "top": {
    "ports": {
      "a": {"direction":"input","bits":[2]},
      "y": {"direction":"output","bits":[5]}
    },
    "cells": {
      "u0": {"type":"$_NOT_","port_directions":{"A":"input","Y":"output"},"connections":{"A":[2],"Y":[3]}},
      "u1": {"type":"$_NOT_","port_directions":{"A":"input","Y":"output"},"connections":{"A":[3],"Y":[4]}},
      "u2": {"type":"$_NOT_","port_directions":{"A":"input","Y":"output"},"connections":{"A":[4],"Y":[5]}}
    }
  }}
}"""

    let private runThreeNot (a: bool) : bool =
        let lib = Library.defaultLib
        match compileFull lib threeNotJson with
        | Error _ -> false
        | Ok (grid, placement, wires) ->
            let arrivals = computeArrival placement wires
            let u0 = placement |> List.find (fun p -> p.Gate.Output = NetId 3)
            let u0inPort =
                u0.Cell.Ports |> List.find (fun p -> p.Role = In) |> portCoord u0
            let dataInj = if a then [(u0inPort, 0<gen>)] else []
            let u2 = placement |> List.find (fun p -> p.Gate.Output = NetId 5)
            let u2outPort =
                u2.Cell.Ports |> List.find (fun p -> p.Role = Out) |> portCoord u2
            let totalSteps =
                arrivals |> Map.tryFind (NetId 5) |> Option.map int |> Option.defaultValue 30
            let result = runWithClocks placement arrivals wires dataInj grid totalSteps
            get result u2outPort = Head

    let runAll () : (string * bool) list =
        // ── ワイヤ遅延の確認 ──────────────────────────────────────────────
        // 2-NOT チェーンの実際のルーティング遅延を確認
        let wireDelayCheck =
            match compileFull Library.defaultLib twoNotJson with
            | Error _ -> -1<gen>
            | Ok (_, _, wires) ->
                wires |> List.tryFind (fun w -> w.Net = NetId 3)
                      |> Option.map (fun w -> w.Delay)
                      |> Option.defaultValue -1<gen>

        // ── 2-NOT E2E ─────────────────────────────────────────────────────
        let two0 = runTwoNot false   // NOT(NOT(0)) = 0
        let two1 = runTwoNot true    // NOT(NOT(1)) = 1

        // ── 3-NOT E2E ─────────────────────────────────────────────────────
        let three0 = runThreeNot false  // NOT(NOT(NOT(0))) = 1
        let three1 = runThreeNot true   // NOT(NOT(NOT(1))) = 0

        [ "2-NOT: wire (net3) delay = measureDelay (simulation-based)",
            wireDelayCheck = 5<gen>   // 7-cell path with 2 L-turns → measured delay=5

          "2-NOT: NOT(NOT(0)) = 0  (a=0 → buffer → y=0)",
            not two0

          "2-NOT: NOT(NOT(1)) = 1  (a=1 → buffer → y=1)",
            two1

          "3-NOT: NOT(NOT(NOT(0))) = 1  (a=0 → y=1)",
            three0

          "3-NOT: NOT(NOT(NOT(1))) = 0  (a=1 → y=0)",
            not three1
        ]
