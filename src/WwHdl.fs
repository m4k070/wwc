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
          Pattern = ofAscii [ System.String.replicate (n' + 1) "#" ] }

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
          Pattern = ofAscii [ "###  "
                               "   ##"
                               "###  " ] }

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
          Pattern = ofAscii [ "..###"
                               "##..."
                               "..###" ] }

    // -----------------------------------------------------------------------
    // スタブセル (パターン未確定 — Rule.run 検証待ち)
    // -----------------------------------------------------------------------

    /// NOT1: クロック同期型 NOT ゲートのスタブ。
    ///
    /// 設計方針 (DESIGN.md §2.3 参照):
    ///   period-8 クロックループの出力を data=1 の 3-Head 規則で抑制する。
    ///     data=0 → クロック電子が出力ポートへ (output=1)
    ///     data=1 → clock + data + 補助信号 = 3 Head → 出力セル発火せず (output=0)
    ///
    /// 未解決: ループ周期とデータ到達タイミングの調整。Rule.run で確認後に Pattern/Latency を確定。
    let not1 : StdCell =
        { Name    = "NOT1"
          Kind    = Not
          Size    = { X = 10; Y = 6 }
          Ports   = [ { Role = In;    Offset = { X = 0; Y = 3 } }
                      { Role = Out;   Offset = { X = 9; Y = 3 } }
                      { Role = Clock; Offset = { X = 5; Y = 0 } } ]
          Latency = 0<gen>       // TODO: Rule.run で実測後に更新
          Pattern = Map.empty }

    /// JUNC3: 3 入力合流点。NOT / NAND の核となるセル。Latency = 4<gen>。
    ///
    /// パターン (5×5):
    ///   ..#..   y=0  入力 C の根本
    ///   ..#..   y=1  入力 C 経路
    ///   #####   y=2  左=A, junction=(2,2), 右=出力
    ///   ..#..   y=3  入力 B 経路
    ///   ..#..   y=4  入力 B の根本
    ///
    /// 動作 (t=0 で A,B,C が各ポートに Head):
    ///   t=1 → (1,2),(2,1),(2,3) が Head (中間セル)
    ///   t=2 → junction (2,2) が隣接 Head 数を評価
    ///     1 or 2 個 → fires → t=4 で (4,2) = 出力 Head
    ///     3 個     → no fire → 出力なし
    ///
    /// NOT(A)   : left=A, top=clock1, bottom=clock2 → right=NOT(A)
    ///   A=0 → 2 Head → fires → output=1 ✓
    ///   A=1 → 3 Head → no fire → output=0 ✓
    ///
    /// NAND(A,B): left=A, top=B, bottom=clock → right=NAND(A,B)
    ///   A∧B=1 → 3 Head → no fire → NAND=0 ✓
    ///   otherwise → 1-2 Head → fires → NAND=1 ✓
    let junc3 : StdCell =
        { Name    = "JUNC3"
          Kind    = Nand
          Size    = { X = 5; Y = 5 }
          Ports   = [ { Role = In;    Offset = { X = 0; Y = 2 } }  // 左: data / A
                      { Role = In;    Offset = { X = 2; Y = 0 } }  // 上: B / clock1
                      { Role = In;    Offset = { X = 2; Y = 4 } }  // 下: C / clock2
                      { Role = Out;   Offset = { X = 4; Y = 2 } } ]// 右: output
          Latency = 4<gen>
          Pattern = ofAscii [ "..#.."
                               "..#.."
                               "#####"
                               "..#.."
                               "..#.." ] }

    /// NOT1: junc3 の上下ポートにクロックを接続した NOT ゲート。
    /// パターン・Latency は junc3 と同一。コンパイラが Clock ポートへ同期信号を配線する。
    let not1 : StdCell = { junc3 with Name = "NOT1"; Kind = Not }

    /// AND2: NAND(A,B,clock) + NOT(nand_out,clock',clock') の 2 段 junc3 構成。
    ///
    /// 真理値表の確認:
    ///   A=0,B=0 → NAND fires (1 Head) → NOT receives 0 (no wire) → NOT fires with 2 clocks → AND=0?
    ///             Wait: NOT(0) means input=no electron. NOT stage sees only 2 clocks → fires → AND=1? NO.
    ///             ここが混乱点。「NAND fires = NAND_out=1 = 電子が出る」という理解が必要。
    ///             NAND(0,0,clock)=1 → 電子が出る → NOT stage input=1 + 2 clocks = 3 Head → no fire → AND=0 ✓
    ///   A=1,B=0 → NAND(1,0,clock)=1 → 電子 → NOT 3 Head → AND=0 ✓
    ///   A=1,B=1 → NAND(1,1,clock)=0 → 電子なし → NOT 2 clocks = 2 Head → fires → AND=1 ✓
    ///
    /// AND 全体 Latency = 4 (NAND) + 4 (NOT) = 8<gen>。
    let and2 : StdCell =
        { Name    = "AND2"
          Kind    = And
          Size    = { X = 13; Y = 5 }
          Ports   = [ { Role = In;    Offset = { X = 0;  Y = 2 } }  // 左: A
                      { Role = In;    Offset = { X = 2;  Y = 0 } }  // 上1: B (NAND 用)
                      { Role = In;    Offset = { X = 2;  Y = 4 } }  // 下1: clock (NAND 用)
                      { Role = In;    Offset = { X = 9;  Y = 0 } }  // 上2: clock' (NOT 用)
                      { Role = In;    Offset = { X = 9;  Y = 4 } }  // 下2: clock' (NOT 用)
                      { Role = Out;   Offset = { X = 12; Y = 2 } } ]
          Latency = 8<gen>
          Pattern = Map.empty }  // TODO: 2 junc3 を直列配置した複合パターン

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
          Pattern = ofAscii [ ".##."
                               "##.#"
                               ".##." ] }

    /// 検証済みセルのみを含むデフォルトライブラリ。
    let defaultLib : CellLibrary =
        [ Buf,  buf
          Or,   or2
          Nand, junc3 ]
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
        let result = run (int junc3.Latency) initial
        let fired  = get result { X = 4; Y = 2 } = Head
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

    /// 1 本の配線。長さ (= Path のセル数) がそのまま遅延になる。
    type Wire =
        { Net: NetId
          Path: Coord list
          Delay: int<gen> }

    let ofPath (net: NetId) (path: Coord list) : Wire =
        { Net = net
          Path = path
          Delay = (List.length path) * 1<gen> }

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
    let leePath (grid: RoutingGrid) (src: Coord) (dst: Coord) : Coord list option =
        // TODO: BFS 実装
        // 1. dist: Map<Coord, int> で管理、初期値 src=0
        // 2. 上下左右の隣接セルを展開、Free (= gridに存在しない) のみ通過可
        // 3. dst 到達後に逆追跡して Path を返す
        if src = dst then Some [ src ] else None

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
    open Netlist
    open Place
    open Route

    /// ネットごとの信号到達世代。
    type ArrivalMap = Map<NetId, int<gen>>

    /// トポロジカル順で各ネットの到達時刻を計算する。
    ///   arrival(primary_input) = 0<gen>
    ///   arrival(gate_output)   = max(arrival(input_i) + wire_i.Delay) + gate.Latency
    let computeArrival
        (_placement: Placement)
        (_wires: Wire list)
        : ArrivalMap =
        // TODO: 依存グラフのトポロジカルソート → 各ネットへ伝播
        Map.empty

    /// 各 Wire のスラック（余裕世代）を計算する。
    ///   slack(w) = target(gate) - arrival(src(w)) - w.Delay
    /// target は当該ゲートの全入力の中で最大の到達時刻。
    let computeSlack
        (_arrival: ArrivalMap)
        (_wires: Wire list)
        : Map<NetId, int<gen>> =
        // TODO: ゲートごとに target を算出しスラックを返す
        Map.empty

    /// スラックが正の Wire に DELAY_n 相当の遅延を付加して均等化する。
    /// Delay フィールドを更新し、emit 時に伸長したパスとして展開する。
    let insertDelays
        (slack: Map<NetId, int<gen>>)
        (wires: Wire list)
        : Wire list =
        wires |> List.map (fun w ->
            match Map.tryFind w.Net slack with
            | Some s when s > 0<gen> -> { w with Delay = w.Delay + s }
            | _                       -> w)


// ---------------------------------------------------------------------
// 7. パイプライン: 各段の型と railway 合成
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

    /// Yosys write_json 出力を YosysModule へパースする。
    let parseYosysJson (_json: string) : Result<YosysModule, CompileError> =
        // TODO: System.Text.Json でデシリアライズ
        // JSON 構造: { "modules": { "top": { "ports": {...}, "cells": {...} } } }
        Error (ParseError "parseYosysJson not implemented")

    /// YosysModule を Netlist IR へ変換する。
    let yosysToNetlist (_m: YosysModule) : Result<Netlist, CompileError> =
        // TODO:
        //   1. cells → Gate list (Type を GateKind に、connections を NetId に変換)
        //   2. ports → PrimaryInputs / PrimaryOutputs
        //   3. clk ポートを検出して ClockNet に設定
        Error (ParseError "yosysToNetlist not implemented")

    /// Frontend: Yosys JSON 文字列 → Netlist。
    /// 呼び出し元は `yosys -p "synth -flatten; abc -g AND,NOT; write_json out.json" design.v`
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

    /// 配線: Lee 法 (迷路探索) でネットごとに経路を引く。
    let route (p: Placement) : Result<Wire list, CompileError> =
        // TODO: Lee algorithm + 交差処理 (Wireworld++ ルールなら容易)
        Error (RoutingCongestion (NetId -1))

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
    /// 各段が Result を返すので、どこで落ちても CompileError で伝播する。
    let compile (lib: CellLibrary) (src: string) : Result<Grid, CompileError> =
        frontend src
        >>= techMap lib
        >>= place
        >>= (fun placement ->
                route placement
                >>= (fun wires ->
                        // 本来はゲートごとに balanceGateInputs を適用して
                        // タイミングを均等化してから emit する
                        Ok (emit placement wires)))
