#r "bin/Debug/net8.0/WwHdl.dll"
// AnalyzeHold.fsx — 配線済み WireLevel グリッドの hold タイミング静的検査
//
// 使い方:
//   dotnet fsi src/AnalyzeHold.fsx <circuit> [--dir DIR] [--top N] [--clk PORT]
//       routed/<circuit>.{bin,meta.json} を読んで検査する (RoutedArtifact.load)
//   dotnet fsi src/AnalyzeHold.fsx --compile <circuit> [--place rowmajor|anneal] [--top N] [--clk PORT]
//       verilog/<circuit>.json をその場で compileWL して検査する (小回路の確認用)
//   dotnet fsi src/AnalyzeHold.fsx --selftest
//       合成した 2-DFF グリッドで「静的判定 = WireLevel.step による実シミュレーション」を掃引照合する
//
// 終了コード: 0=違反なし (selftest は合格) / 1=読込・解析エラー (selftest は不合格) / 2=引数エラー / 3=hold 違反あり
//
// ---------------------------------------------------------------------
// 遅延モデル (WireLevel.step の意味論から導出。世代 = step 1 回)
//
//   時刻の基準: ホストが clk ピンを 0→1 に書いた直後の状態を gen 0 とする。
//   セル x の「到達時刻」 = x が新しい値を保持する最初の世代。
//
//   * LWire / LNand / Cross の各チャネル: 次世代の値 = 現世代の隣接提示値の関数
//       → 入力側隣セルの到達時刻 + 1 (CellDelay)。Pin は gen 0 で既に新値。
//   * LDff: step の中で「現世代の clk 側隣セル値」と「前世代の clk (prevClk)」から
//       立ち上がりを検知し、そのとき「現世代の D 側隣セル値」を q' に取り込む。
//       clk 側隣セルの到達時刻を T とすると:
//         - 捕捉される D は gen T の D 側隣セル値            → CaptureWindow = 0
//         - 新しい q が見えるのは gen T+1                    → DffClockToQ   = 1
//   * DFF A の Q から DFF B の D 側隣セルまでの最短経路のセル数を minData(A→B)
//     (A のセル自身が B の D 側隣セルなら 0) とすると、B の D 側隣セルが A の新値を
//     持つ最初の世代は clkArrival(A) + DffClockToQ + minData(A→B)。
//   * hold 違反 ⇔ その世代 ≤ clkArrival(B) + CaptureWindow
//     (B が捕捉する世代に、既に A の新値が見えてしまう)。
//     slack = dataArrival − (captureGen + 1) と定義し、slack < 0 を違反とする
//     (slack = 0 は「捕捉の 1 世代後に新値が届く」ぎりぎり安全)。
//
//   最短経路は論理的マスキングを無視する (NAND の他入力で値が変わらない場合も
//   「変わり得る」とみなす) ので、安全側 (違反を多めに出す側) の見積もりになる。
//   これらの定数が step と一致していることは --selftest で実シミュレーションと照合している。
// ---------------------------------------------------------------------
open System
open System.IO
open System.Collections.Generic
open WwHdl
open WwHdl.Domain
open WwHdl.WireLevel
open WwHdl.RoutedArtifact

/// Wire / NAND / Cross チャネル 1 段の遅延 (世代)
[<Literal>]
let CellDelay = 1
/// DFF がクロック立ち上がりを見た世代から Q が新値になるまで (世代)
[<Literal>]
let DffClockToQ = 1
/// DFF がクロック立ち上がりを見た世代から、D をサンプルする世代までのずれ
[<Literal>]
let CaptureWindow = 0
/// 上位何件の違反候補ペアを表示するか (既定)
[<Literal>]
let DefaultTopCount = 30
/// クロック到達分布の表示で、異なる到達時刻を最大何種類まで列挙するか
[<Literal>]
let MaxArrivalBuckets = 20
/// Cross は 1 セルに 2 チャネル (水平/垂直) を持つ。ノード ID = セル番号 × 2 + チャネル
[<Literal>]
let ChannelsPerCell = 2
[<Literal>]
let MainOrHorizontal = 0
[<Literal>]
let Vertical = 1

let repoRoot = Path.GetFullPath (Path.Combine (__SOURCE_DIRECTORY__, ".."))

// --- 密グリッド (解析用の配列表現) -------------------------------------------

type DenseGrid =
    { Origin: Coord
      Width: int
      Height: int
      Cells: LCell[] }

let toDense (g: LGrid) : DenseGrid =
    let origin, width, height = gridBounds g
    let cells = Array.create (width * height) LEmpty
    for KeyValue (c, cell) in g do
        cells.[(c.Y - origin.Y) * width + (c.X - origin.X)] <- cell
    { Origin = origin; Width = width; Height = height; Cells = cells }

let indexOf (dg: DenseGrid) (c: Coord) : int option =
    let x = c.X - dg.Origin.X
    let y = c.Y - dg.Origin.Y
    let inside = x >= 0 && y >= 0 && x < dg.Width && y < dg.Height
    if inside then Some (y * dg.Width + x) else None

let coordOf (dg: DenseGrid) (idx: int) : Coord =
    { X = dg.Origin.X + idx % dg.Width; Y = dg.Origin.Y + idx / dg.Width }

let neighborIndex (dg: DenseGrid) (idx: int) (side: Dir) : int option =
    let c = coordOf dg idx
    let d = delta side
    indexOf dg { X = c.X + d.X; Y = c.Y + d.Y }

let allDirs = [| E; W; N; S |]

let clockSides (dir: Dir) : Dir list =
    match dir with
    | E | W -> [ N; S ]
    | N | S -> [ E; W ]

// --- 信号の流れ (pull 関係) の順方向グラフ -------------------------------------

/// ノードの値を読む側 (読み手) の種類。
type Sink =
    /// 組合せ要素の入力 (Wire / Cross チャネル / NAND)。値が伝わり、さらに先へ進む
    | CombNode of node: int * isNand: bool
    /// DFF の D 入力 (背面) として読まれる
    | DffData of dffCell: int
    /// DFF のクロック入力 (側面) として読まれる
    | DffClock of dffCell: int

/// セル cell のチャネル ch が toward 方向の隣へ値を提示するか (WireLevel.presentedTo と同じ規則)。
let presents (cell: LCell) (ch: int) (toward: Dir) : bool =
    match cell with
    | LEmpty -> false
    | Pin _ | LWire _ | LNand _ | LDff _ -> ch = MainOrHorizontal
    | Cross (hd, vd, _, _) ->
        if ch = MainOrHorizontal then toward = hd else toward = vd

/// 座標 readerIdx のセルが、自分の side 側の隣から値を読むなら、その読み手を返す
/// (WireLevel.step の pullFrom の使われ方と同じ規則)。
let sinkOf (reader: LCell) (readerIdx: int) (side: Dir) : Sink option =
    match reader with
    | LWire (d, _) when side = opposite d -> Some (CombNode (readerIdx * ChannelsPerCell, false))
    | LNand (d, _) when side <> d -> Some (CombNode (readerIdx * ChannelsPerCell, true))
    | Cross (hd, vd, _, _) ->
        if side = opposite hd then Some (CombNode (readerIdx * ChannelsPerCell + MainOrHorizontal, false))
        elif side = opposite vd then Some (CombNode (readerIdx * ChannelsPerCell + Vertical, false))
        else None
    | LDff (d, _, _) ->
        if side = opposite d then Some (DffData readerIdx)
        elif List.contains side (clockSides d) then Some (DffClock readerIdx)
        else None
    | _ -> None

/// ノードの値を読む読み手をすべて列挙する。
let sinksOf (dg: DenseGrid) (node: int) : Sink list =
    let idx = node / ChannelsPerCell
    let ch = node % ChannelsPerCell
    let cell = dg.Cells.[idx]
    [ for toward in allDirs do
        if presents cell ch toward then
            match neighborIndex dg idx toward with
            | Some nIdx ->
                match sinkOf dg.Cells.[nIdx] nIdx (opposite toward) with
                | Some sink -> yield sink
                | None -> ()
            | None -> () ]

// --- 1. クロック到達時刻 -------------------------------------------------------

type ClockAnalysis =
    { /// DFF セル番号 → clk 側隣セルに立ち上がりが届く世代
      Arrival: Map<int, int>
      /// クロック網が NAND の入力に入っている箇所 (極性が変わるため追跡しない)
      NandLoads: int list
      /// クロック網が DFF の D 入力に入っている箇所
      DataLoads: int list }

/// クロックピンから、Wire / Cross だけを通って前進する幅優先探索。
/// 1 段 = CellDelay 世代なので、BFS の距離がそのまま到達世代になる。
let analyzeClock (dg: DenseGrid) (clkPinIdx: int) : ClockAnalysis =
    let dist = Dictionary<int, int>()
    let queue = Queue<int>()
    let pinNode = clkPinIdx * ChannelsPerCell
    dist.[pinNode] <- 0
    queue.Enqueue pinNode
    let mutable arrival = Map.empty
    let nandLoads = ResizeArray<int>()
    let dataLoads = ResizeArray<int>()
    while queue.Count > 0 do
        let u = queue.Dequeue ()
        let du = dist.[u]
        for sink in sinksOf dg u do
            match sink with
            | CombNode (_, true) -> nandLoads.Add (u / ChannelsPerCell)
            | CombNode (v, false) ->
                if not (dist.ContainsKey v) then
                    dist.[v] <- du + CellDelay
                    queue.Enqueue v
            | DffClock dff ->
                // BFS は距離の昇順に取り出すので、最初に記録した値が最短
                if not (Map.containsKey dff arrival) then arrival <- Map.add dff du arrival
            | DffData dff -> dataLoads.Add dff
    { Arrival = arrival
      NandLoads = List.ofSeq nandLoads |> List.distinct
      DataLoads = List.ofSeq dataLoads |> List.distinct }

// --- 2. DFF 間の最短データ遅延 ---------------------------------------------------

type DataReach =
    { /// 捕捉側 DFF セル番号 → minData (発射側 DFF セル自身を 0 とする経路長)
      ToData: Map<int, int>
      /// データ経路が到達した DFF のクロック入力 (ゲーテッドクロック。hold 解析の前提外)
      ToClock: int list }

/// DFF (セル番号 src) の Q から、組合せ要素 (Wire / Cross / NAND) だけを通って前進し、
/// 各 DFF の D 側隣セルまでの最短段数を求める。DFF は通り抜けない (順序境界)。
/// dist / stamp は呼び出し間で使い回す作業領域 (全ノード分の配列を毎回確保しないため)。
let reachFromDff (dg: DenseGrid) (dist: int[]) (stamp: int[]) (stampId: int) (src: int) : DataReach =
    let queue = Queue<int>()
    let start = src * ChannelsPerCell
    dist.[start] <- 0
    stamp.[start] <- stampId
    queue.Enqueue start
    let mutable toData = Map.empty
    let toClock = ResizeArray<int>()
    while queue.Count > 0 do
        let u = queue.Dequeue ()
        let du = dist.[u]
        for sink in sinksOf dg u do
            match sink with
            | CombNode (v, _) ->
                if stamp.[v] <> stampId then
                    stamp.[v] <- stampId
                    dist.[v] <- du + CellDelay
                    queue.Enqueue v
            | DffData dff ->
                // du = D 側隣セル (u) までの段数。BFS 順なので最初が最短
                if not (Map.containsKey dff toData) then toData <- Map.add dff du toData
            | DffClock dff -> toClock.Add dff
    { ToData = toData; ToClock = List.ofSeq toClock |> List.distinct }

// --- 3. hold 判定 -------------------------------------------------------------

type HoldPair =
    { Launch: int
      Capture: int
      LaunchClk: int
      CaptureClk: int
      MinData: int
      /// dataArrival − (captureGen + 1)。負なら違反
      Slack: int }

let holdSlack (launchClk: int) (captureClk: int) (minData: int) : int =
    let dataArrival = launchClk + DffClockToQ + minData
    let captureGen = captureClk + CaptureWindow
    dataArrival - (captureGen + 1)

type HoldReport =
    { Clock: ClockAnalysis
      Dffs: int list
      Pairs: HoldPair list
      /// クロック未到達の DFF
      Unclocked: int list
      /// データ経路がクロック入力に入る (発射 DFF, 被駆動 DFF)
      GatedClocks: (int * int) list }

let findDffs (dg: DenseGrid) : int list =
    dg.Cells
    |> Array.indexed
    |> Array.choose (fun (i, cell) ->
        match cell with
        | LDff _ -> Some i
        | _ -> None)
    |> List.ofArray

let analyzeHold (dg: DenseGrid) (clkPinIdx: int) : HoldReport =
    let clock = analyzeClock dg clkPinIdx
    let dffs = findDffs dg
    let nodeCount = dg.Cells.Length * ChannelsPerCell
    let dist = Array.zeroCreate<int> nodeCount
    let stamp = Array.zeroCreate<int> nodeCount
    let reaches =
        dffs |> List.mapi (fun i dff -> dff, reachFromDff dg dist stamp (i + 1) dff)
    let pairs =
        [ for (launch, reach) in reaches do
            match Map.tryFind launch clock.Arrival with
            | None -> ()
            | Some launchClk ->
                for KeyValue (capture, minData) in reach.ToData do
                    match Map.tryFind capture clock.Arrival with
                    | None -> ()
                    | Some captureClk ->
                        yield { Launch = launch
                                Capture = capture
                                LaunchClk = launchClk
                                CaptureClk = captureClk
                                MinData = minData
                                Slack = holdSlack launchClk captureClk minData } ]
    { Clock = clock
      Dffs = dffs
      Pairs = pairs |> List.sortBy (fun p -> p.Slack)
      Unclocked = dffs |> List.filter (fun d -> not (Map.containsKey d clock.Arrival))
      GatedClocks = [ for (launch, reach) in reaches do for c in reach.ToClock do yield launch, c ] }

// --- 表示 ---------------------------------------------------------------------

/// DFF 座標 → それが直接駆動している出力ポートのビット名
let outputNames (meta: RoutedMeta) : Map<Coord, string list> =
    [ for KeyValue (port, probes) in meta.Outputs do
        for (i, probe) in List.indexed probes do
            match probe with
            | CellProbe c -> yield c, sprintf "%s[%d]" port i
            | ConstProbe _ | Unobservable _ -> () ]
    |> List.groupBy fst
    |> List.map (fun (c, xs) -> c, xs |> List.map snd)
    |> Map.ofList

let describeDff (dg: DenseGrid) (names: Map<Coord, string list>) (idx: int) : string =
    let c = coordOf dg idx
    let label =
        match Map.tryFind c names with
        | Some ns -> " " + String.Join (",", ns)
        | None -> ""
    sprintf "(%d,%d)%s" c.X c.Y label

let printReport (dg: DenseGrid) (names: Map<Coord, string list>) (topCount: int) (r: HoldReport) : unit =
    let arrivals = r.Clock.Arrival |> Map.toList
    printfn "DFF %d 個、クロック到達 %d 個" r.Dffs.Length arrivals.Length
    match arrivals |> List.map snd with
    | [] -> printfn "  クロック到達なし"
    | times ->
        let lo = List.min times
        let hi = List.max times
        printfn "  クロック到達: min %d / max %d / skew %d 世代" lo hi (hi - lo)
        let buckets = times |> List.countBy id |> List.sortBy fst
        printfn "  到達時刻の分布 (%d 種類):" buckets.Length
        for (t, n) in List.truncate MaxArrivalBuckets buckets do
            let who =
                if n <= 3 then
                    arrivals
                    |> List.filter (fun (_, a) -> a = t)
                    |> List.map (fst >> describeDff dg names)
                    |> fun xs -> "  " + String.Join (" ", xs)
                else ""
            printfn "    %6d 世代: %4d 個%s" t n who
        if buckets.Length > MaxArrivalBuckets then
            printfn "    … 他 %d 種類" (buckets.Length - MaxArrivalBuckets)
    for d in r.Unclocked do
        printfn "  WARN: クロック未到達の DFF %s" (describeDff dg names d)
    for c in r.Clock.NandLoads do
        let p = coordOf dg c
        printfn "  WARN: クロック網が NAND 入力に接続 (%d,%d) — 極性反転のため追跡しない" p.X p.Y
    for d in r.Clock.DataLoads do
        printfn "  WARN: クロック網が DFF の D 入力に接続 %s" (describeDff dg names d)
    for (a, b) in r.GatedClocks do
        printfn "  WARN: データ経路が DFF のクロック入力に到達 %s → %s" (describeDff dg names a) (describeDff dg names b)
    let violations = r.Pairs |> List.filter (fun p -> p.Slack < 0)
    printfn "DFF→DFF データ経路 %d 組、hold 違反 %d 組 (違反の捕捉側 DFF %d 個)"
        r.Pairs.Length violations.Length (violations |> List.map (fun p -> p.Capture) |> List.distinct |> List.length)
    printfn "slack 小さい順 上位 %d 件 (slack = 発射clk + %d + minData − (捕捉clk + %d + 1)、負 = 違反):"
        topCount DffClockToQ CaptureWindow
    printfn "  %6s %7s %7s %7s  %-40s -> %s" "slack" "clk差" "minData" "捕捉clk" "発射 DFF (A)" "捕捉 DFF (B)"
    for p in List.truncate topCount r.Pairs do
        printfn "  %6d %7d %7d %7d  %-40s -> %s"
            p.Slack (p.CaptureClk - p.LaunchClk) p.MinData p.CaptureClk
            (describeDff dg names p.Launch) (describeDff dg names p.Capture)

/// 既存の WireLevel.clockArrivals (終端から逆走して数える) と BFS の結果を照合する。
/// 両者は独立な数え方なので、一致すれば到達時刻の基準 (Pin = 0) が揃っていることの確認になる。
let crossCheckClock (g: LGrid) (dg: DenseGrid) (r: HoldReport) : string list =
    let reference = clockArrivals g |> Map.ofList
    [ for KeyValue (dff, t) in r.Clock.Arrival do
        let c = coordOf dg dff
        match Map.tryFind c reference with
        | Some t' when t' = t -> ()
        | other -> yield sprintf "(%d,%d): BFS %d / clockArrivals %A" c.X c.Y t other ]

// --- 実行モード ------------------------------------------------------------------

type Options =
    { Source: SourceChoice
      TopCount: int
      ClockPort: string }

and SourceChoice =
    | Routed of circuit: string * dir: string
    | Compile of circuit: string * placement: GatePlacement.PlacementStrategy
    | SelfTest

let parseArgs (args: string list) : Result<Options, string> =
    let rec go (opts: Options) (rest: string list) =
        match rest with
        | [] -> Ok opts
        | "--selftest" :: tail -> go { opts with Source = SelfTest } tail
        | "--top" :: n :: tail ->
            match Int32.TryParse n with
            | true, v when v > 0 -> go { opts with TopCount = v } tail
            | _ -> Error (sprintf "--top には正の整数が必要: %s" n)
        | "--clk" :: port :: tail -> go { opts with ClockPort = port } tail
        | "--dir" :: dir :: tail ->
            match opts.Source with
            | Routed (c, _) -> go { opts with Source = Routed (c, Path.GetFullPath dir) } tail
            | _ -> Error "--dir は <circuit> の後に指定する"
        | "--compile" :: circuit :: tail -> go { opts with Source = Compile (circuit, GatePlacement.RowMajor) } tail
        | "--place" :: place :: tail ->
            match opts.Source, place with
            | Compile (c, _), "rowmajor" -> go { opts with Source = Compile (c, GatePlacement.RowMajor) } tail
            | Compile (c, _), "anneal" ->
                go { opts with Source = Compile (c, GatePlacement.Annealed GatePlacement.defaultAnnealConfig) } tail
            | Compile _, other -> Error (sprintf "--place は rowmajor|anneal: %s" other)
            | _ -> Error "--place は --compile <circuit> の後に指定する"
        | circuit :: tail when not (circuit.StartsWith "--") ->
            go { opts with Source = Routed (circuit, Path.Combine (repoRoot, "routed")) } tail
        | other :: _ -> Error (sprintf "不明な引数: %s" other)
    match args with
    | [] -> Error "引数がない"
    | _ -> go { Source = SelfTest; TopCount = DefaultTopCount; ClockPort = "clk" } args

/// grid と meta を解析して表示し、終了コードを返す。
let analyzeAndPrint (opts: Options) (grid: LGrid) (meta: RoutedMeta) : int =
    match Map.tryFind opts.ClockPort meta.Inputs with
    | None | Some [] ->
        eprintfn "ERROR: meta にクロック入力 %s がない (inputs: %A)" opts.ClockPort (meta.Inputs |> Map.keys |> List.ofSeq)
        1
    | Some (clkPin :: _) ->
        let dg = toDense grid
        match indexOf dg clkPin with
        | None ->
            eprintfn "ERROR: クロックピン (%d,%d) がグリッド外" clkPin.X clkPin.Y
            1
        | Some clkIdx ->
            let sw = Diagnostics.Stopwatch.StartNew ()
            let report = analyzeHold dg clkIdx
            printfn "%s: grid %dx%d, clk ピン (%d,%d), 解析 %.1f 秒"
                meta.Circuit dg.Width dg.Height clkPin.X clkPin.Y sw.Elapsed.TotalSeconds
            printfn "遅延モデル: Wire/Cross/NAND 1 段 = %d 世代, clk→Q = %d 世代, 捕捉窓 = %d" CellDelay DffClockToQ CaptureWindow
            match crossCheckClock grid dg report with
            | [] -> printfn "クロック到達の照合 (WireLevel.clockArrivals): 全 %d 個一致" report.Clock.Arrival.Count
            | mismatches ->
                printfn "WARN: クロック到達が WireLevel.clockArrivals と %d 個不一致:" mismatches.Length
                for m in List.truncate DefaultTopCount mismatches do printfn "  %s" m
            printReport dg (outputNames meta) opts.TopCount report
            let hasViolation = report.Pairs |> List.exists (fun p -> p.Slack < 0)
            if hasViolation then 3 else 0

let runRouted (opts: Options) (circuit: string) (dir: string) : int =
    match load dir circuit with
    | Error e ->
        eprintfn "ERROR: %s" (describeError e)
        1
    | Ok (grid, meta) -> analyzeAndPrint opts grid meta

let runCompile (opts: Options) (circuit: string) (placement: GatePlacement.PlacementStrategy) : int =
    let path = Path.Combine (repoRoot, "verilog", circuit + ".json")
    if not (File.Exists path) then
        eprintfn "ERROR: %s がない" path
        1
    else
        let json = File.ReadAllText path
        let provenance = { GitCommit = "(analyze)"; CreatedAtUtc = DateTimeOffset.UtcNow }
        let compiled =
            PipelineWL.compileWLWithStrategy placement json
            |> Result.mapError (sprintf "compileWL 失敗: %A")
        let ported = parseYosysPorts json |> Result.mapError describeError
        match compiled, ported with
        | Error e, _ | _, Error e ->
            eprintfn "ERROR: %s" e
            1
        | Ok (grid, placed, pins), Ok ports ->
            match buildMeta circuit (sourceSha256 (File.ReadAllBytes path)) provenance ports grid placed pins with
            | Error e ->
                eprintfn "ERROR: %s" (describeError e)
                1
            | Ok meta ->
                // meta の座標は正規化座標なので、グリッドも .bin と同じ往復で正規化する
                let normalized = importGrid (exportGrid grid)
                analyzeAndPrint opts normalized meta

// --- セルフテスト: 静的判定と実シミュレーションの一致 --------------------------------
//
// 2 個の DFF (A → 長さ k の配線 → B) を 1 本のクロックバスで駆動するグリッドを合成する。
//   y=1:  . D A > > … > B        (D = A の D 入力ピン = 1、A/B の初期 q = 0)
//   y=2:  クロックの枝 (A の南、B の南)。A の枝は 1 セル (短) か 2 セル (長、西から回り込む)
//   y=3:  クロックバス。ピン位置 px から左右へ配線
// clk を 0 で settle → 1 にして settle。B が A の新値 (1) を捕捉したら hold 違反が顕在化している。

type SelfTestCase = { DataLen: int; PinX: int; LongStubA: bool }

[<Literal>]
let SelfTestSettleLimit = 1000
[<Literal>]
let RowData = 1
[<Literal>]
let RowStub = 2
[<Literal>]
let RowBus = 3
[<Literal>]
let DffAX = 2

let buildPairGrid (tc: SelfTestCase) : LGrid * Coord * Coord * Coord =
    let at x y = { X = x; Y = y }
    let bX = DffAX + 1 + tc.DataLen
    let dataCells =
        [ yield at (DffAX - 1) RowData, Pin true
          yield at DffAX RowData, LDff (E, false, false)
          for x in DffAX + 1 .. bX - 1 do yield at x RowData, LWire (E, false)
          yield at bX RowData, LDff (E, false, false) ]
    let stubCells =
        [ if tc.LongStubA then
              yield at (DffAX - 1) RowStub, LWire (N, false)
              yield at DffAX RowStub, LWire (E, false)
          else
              yield at DffAX RowStub, LWire (N, false)
          yield at bX RowStub, LWire (N, false) ]
    let busCells =
        [ for x in 0 .. bX do
            if x < tc.PinX then yield at x RowBus, LWire (W, false)
            elif x = tc.PinX then yield at x RowBus, Pin false
            else yield at x RowBus, LWire (E, false) ]
    Map.ofList (dataCells @ stubCells @ busCells), at tc.PinX RowBus, at DffAX RowData, at bX RowData

/// 実シミュレーションで B が A の新値を捕捉したか (= hold 違反の顕在化)
let simulateCapturesNew (g: LGrid) (clkPin: Coord) (dffB: Coord) : Result<bool, string> =
    let settled0, t0 = settle SelfTestSettleLimit g
    if t0 >= SelfTestSettleLimit then Error "clk=0 で収束しない" else
    let settled1, t1 = settle SelfTestSettleLimit (setPin clkPin true settled0)
    if t1 >= SelfTestSettleLimit then Error "clk=1 で収束しない"
    else Ok (levelOf settled1 dffB)

let runSelfTest () : int =
    let cases =
        [ for k in [ 1; 2; 3; 5 ] do
            for longStub in [ false; true ] do
                for px in 0 .. DffAX + 1 + k do
                    yield { DataLen = k; PinX = px; LongStubA = longStub } ]
    let results =
        cases |> List.map (fun tc ->
            let g, clkPin, dffA, dffB = buildPairGrid tc
            let dg = toDense g
            let report =
                match indexOf dg clkPin with
                | Some i -> analyzeHold dg i
                | None -> failwith "selftest: クロックピンがグリッド外 (テスト生成の誤り)"
            let pair =
                report.Pairs
                |> List.tryFind (fun p -> coordOf dg p.Launch = dffA && coordOf dg p.Capture = dffB)
            tc, pair, simulateCapturesNew g clkPin dffB)
    let mutable failures = 0
    let mutable violated = 0
    for (tc, pair, sim) in results do
        match pair, sim with
        | None, _ ->
            failures <- failures + 1
            printfn "FAIL %A: A→B の経路が見つからない" tc
        | _, Error e ->
            failures <- failures + 1
            printfn "FAIL %A: %s" tc e
        | Some p, Ok capturedNew ->
            let predicted = p.Slack < 0
            if predicted then violated <- violated + 1
            let verdict = if predicted = capturedNew then "ok  " else "FAIL"
            if predicted <> capturedNew then failures <- failures + 1
            printfn "%s k=%d px=%d longStubA=%-5b clkA=%d clkB=%d minData=%d slack=%3d 予測違反=%-5b 実際に新値捕捉=%b"
                verdict tc.DataLen tc.PinX tc.LongStubA p.LaunchClk p.CaptureClk p.MinData p.Slack predicted capturedNew
    let slacks = results |> List.choose (fun (_, p, _) -> p |> Option.map (fun p -> p.Slack)) |> List.distinct
    // 格子は二部グラフなので経路長の偶奇は端点で決まり、この幾何では slack は常に奇数になる
    // (slack 0 は作れない)。遅延の数え方の 1 世代のずれは -1 / +1 の判定で検出できる。
    let coversBoundary = List.contains -1 slacks && List.contains 1 slacks
    printfn "selftest: %d ケース、予測違反 %d、不一致 %d、境界 (slack -1 / +1) を含む: %b"
        results.Length violated failures coversBoundary
    if failures = 0 && coversBoundary && violated > 0 then 0 else 1

let exitCode =
    match parseArgs (fsi.CommandLineArgs |> Array.toList |> List.tail) with
    | Error msg ->
        eprintfn "%s" msg
        eprintfn "使い方: dotnet fsi src/AnalyzeHold.fsx <circuit> [--dir DIR] [--top N] [--clk PORT]"
        eprintfn "        dotnet fsi src/AnalyzeHold.fsx --compile <circuit> [--place rowmajor|anneal] [--top N]"
        eprintfn "        dotnet fsi src/AnalyzeHold.fsx --selftest"
        2
    | Ok opts ->
        match opts.Source with
        | SelfTest -> runSelfTest ()
        | Routed (circuit, dir) -> runRouted opts circuit dir
        | Compile (circuit, placement) -> runCompile opts circuit placement

exit exitCode
