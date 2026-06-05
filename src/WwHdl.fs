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

    /// 1 本の配線。長さ (= Path のセル数) がそのまま遅延になる。
    type Wire =
        { Net: NetId
          Path: Coord list
          Delay: int<gen> }

    let ofPath (net: NetId) (path: Coord list) : Wire =
        { Net = net
          Path = path
          Delay = (List.length path) * 1<gen> }


// ---------------------------------------------------------------------
// 5. パイプライン: 各段の型と railway 合成
// ---------------------------------------------------------------------
module Pipeline =
    open Units
    open Domain
    open Netlist
    open Library
    open Place
    open Route

    type CompileError =
        | ParseError      of string
        | UnmappableGate  of GateKind
        | PlacementOverflow
        | RoutingCongestion of NetId
        | TimingViolation of NetId * expected: int<gen> * actual: int<gen>

    // --- 段の境界。それぞれ別の型を返すので順序を取り違えられない ---

    /// Frontend: HDL ソース → AST → 合成 → ネットリスト。
    /// 実装方針: Yosys に投げて write_json した結果をパースするのが現実的
    /// (`synth -flatten; abc -g AND,NOT` で正規化済みネットリストを得る)。
    let frontend (src: string) : Result<Netlist, CompileError> =
        // TODO: Verilog パーサ or Yosys JSON 取り込み
        Error (ParseError "frontend not implemented")

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
