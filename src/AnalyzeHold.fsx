#r "bin/Debug/net8.0/WwHdl.dll"
// AnalyzeHold.fsx — 配線済み WireLevel グリッドの hold タイミング静的検査
//
// 使い方:
//   dotnet fsi src/AnalyzeHold.fsx <circuit> [--dir DIR] [--top N] [--clk PORT]
//       routed/<circuit>.{bin,meta.json} を読んで検査する (RoutedArtifact.load)
//   dotnet fsi src/AnalyzeHold.fsx --compile <circuit> [--place rowmajor|anneal] [--clocking single|two-phase] [--top N] [--clk PORT]
//       verilog/<circuit>.json をその場で compileWL して検査する (小回路の確認用)
//   dotnet fsi src/AnalyzeHold.fsx --selftest
//       合成した 2-DFF グリッドで「静的判定 = WireLevel.step による実シミュレーション」を掃引照合する
//
// 終了コード: 0=違反なし (selftest は合格) / 1=読込・解析エラー (selftest は不合格) / 2=引数エラー
//             3=hold 違反あり (twoPhase は不変条件違反あり)
//
// 遅延モデルと 2 相の不変条件は src/HoldAnalysis.fs の冒頭を参照 (解析本体もそこにある)。
//
// クロック方式は meta の clocking に従う (--compile では --clocking で選ぶ):
//   * singleEdge: clk ピンからの到達時刻と DFF 間最短データ経路から hold slack を求める
//   * twoPhase:   clk_a / clk_b から各 DFF の相を決め、「同じ相の DFF → DFF の組合せデータ経路」が
//                 0 本であること (2 相の正しさの不変条件) を検査する。skew は参考として表示する
open System
open System.IO
open System.Collections.Generic
open WwHdl
open WwHdl.Domain
open WwHdl.WireLevel
open WwHdl.RoutedArtifact
open WwHdl.HoldAnalysis

/// 上位何件の違反候補ペアを表示するか (既定)
[<Literal>]
let DefaultTopCount = 30
/// クロック到達分布の表示で、異なる到達時刻を最大何種類まで列挙するか
[<Literal>]
let MaxArrivalBuckets = 20

let repoRoot = Path.GetFullPath (Path.Combine (__SOURCE_DIRECTORY__, ".."))

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

// --- 実行モード ------------------------------------------------------------------

type Options =
    { Source: SourceChoice
      TopCount: int
      ClockPort: string
      ClockPins: int }

and SourceChoice =
    | Routed of circuit: string * dir: string
    | Compile of circuit: string * placement: GatePlacement.PlacementStrategy * clocking: Clocking.ClockingScheme
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
        | "--clock-pins" :: n :: tail ->
            match Int32.TryParse n with
            | true, v when v >= 1 -> go { opts with ClockPins = v } tail
            | _ -> Error (sprintf "--clock-pins には 1 以上の整数が必要: %s" n)
        | "--dir" :: dir :: tail ->
            match opts.Source with
            | Routed (c, _) -> go { opts with Source = Routed (c, Path.GetFullPath dir) } tail
            | _ -> Error "--dir は <circuit> の後に指定する"
        | "--compile" :: circuit :: tail ->
            go { opts with Source = Compile (circuit, GatePlacement.RowMajor, Clocking.SingleEdge) } tail
        | "--place" :: place :: tail ->
            match opts.Source, place with
            | Compile (c, _, k), "rowmajor" -> go { opts with Source = Compile (c, GatePlacement.RowMajor, k) } tail
            | Compile (c, _, k), "anneal" ->
                go { opts with Source = Compile (c, GatePlacement.Annealed GatePlacement.defaultAnnealConfig, k) } tail
            | Compile _, other -> Error (sprintf "--place は rowmajor|anneal: %s" other)
            | _ -> Error "--place は --compile <circuit> の後に指定する"
        | "--clocking" :: scheme :: tail ->
            match opts.Source, scheme with
            | Compile (c, p, _), "single" -> go { opts with Source = Compile (c, p, Clocking.SingleEdge) } tail
            | Compile (c, p, _), "two-phase" -> go { opts with Source = Compile (c, p, Clocking.TwoPhase) } tail
            | Compile _, other -> Error (sprintf "--clocking は single|two-phase: %s" other)
            | _ -> Error "--clocking は --compile <circuit> の後に指定する (routed はmeta の clocking に従う)"
        | circuit :: tail when not (circuit.StartsWith "--") ->
            go { opts with Source = Routed (circuit, Path.Combine (repoRoot, "routed")) } tail
        | other :: _ -> Error (sprintf "不明な引数: %s" other)
    match args with
    | [] -> Error "引数がない"
    | _ -> go { Source = SelfTest; TopCount = DefaultTopCount; ClockPort = "clk"; ClockPins = 1 } args

/// クロック到達時刻の要約 (min / max / skew)。
let describeArrivals (label: string) (c: ClockAnalysis) : string =
    match c.Arrival |> Map.toList |> List.map snd with
    | [] -> sprintf "%s: 到達なし" label
    | times ->
        let lo = List.min times
        let hi = List.max times
        sprintf "%s: DFF %d 個、到達 min %d / max %d / skew %d 世代 (参考)" label times.Length lo hi (hi - lo)

/// 最大到達時間と下限 (各端子について最も近いピンまでのマンハッタン距離の最大) の比較。
/// pinIdxs は区画ごとのクロックピン (issue #7 (b)。既定は 1 本)。
let describeLatency (label: string) (dg: DenseGrid) (pinIdxs: int list) (c: ClockAnalysis) : string =
    match clockLatencyMulti dg pinIdxs c with
    | None -> sprintf "%s: 到達なし" label
    | Some l ->
        sprintf "%s (ピン %d 本): 最大到達 %d / 下限 %d (比 %.3f) / 最大の遠回り %d / ピン位置も最適なら下限 %d 世代"
            label pinIdxs.Length l.MaxArrival l.LowerBound (latencyRatio l) l.MaxDetour l.IdealPinBound

let printTwoPhaseReport (dg: DenseGrid) (names: Map<Coord, string list>) (topCount: int) (r: TwoPhaseReport) : unit =
    let count phase = r.PhaseOf |> Map.filter (fun _ p -> p = phase) |> Map.count
    printfn "DFF %d 個 (clk_a 相 %d / clk_b 相 %d)" r.Dffs.Length (count PhaseA) (count PhaseB)
    printfn "  %s" (describeArrivals "clk_a" r.ClockA)
    printfn "  %s" (describeArrivals "clk_b" r.ClockB)
    for d in r.Unclocked do
        printfn "  NG: どちらのクロックも届かない DFF %s" (describeDff dg names d)
    for d in r.DoublyClocked do
        printfn "  NG: clk_a と clk_b の両方が届く DFF %s" (describeDff dg names d)
    for (label, c) in [ "clk_a", r.ClockA; "clk_b", r.ClockB ] do
        for n in c.NandLoads do
            let p = coordOf dg n
            printfn "  NG: %s が NAND 入力に接続 (%d,%d)" label p.X p.Y
        for d in c.DataLoads do
            printfn "  NG: %s が DFF の D 入力に接続 %s" label (describeDff dg names d)
    for (a, b) in r.GatedClocks do
        printfn "  NG: データ経路が DFF のクロック入力に到達 %s → %s" (describeDff dg names a) (describeDff dg names b)
    printfn "DFF→DFF データ経路: clk_a→clk_b %d 組、clk_b→clk_a %d 組、同相 %d 組 (0 であるべき)"
        r.AToB r.BToA r.SamePhasePaths.Length
    for p in List.truncate topCount (r.SamePhasePaths |> List.sortBy (fun p -> p.MinData)) do
        printfn "  NG 同相経路 (%A) minData %d: %s -> %s"
            p.Phase p.MinData (describeDff dg names p.Launch) (describeDff dg names p.Capture)
    printfn "2 相の不変条件: %s" (if twoPhaseInvariantHolds r then "成立" else "不成立")

let private fmtCoords (cs: Coord list) = cs |> List.map (fun c -> sprintf "(%d,%d)" c.X c.Y) |> String.concat " "

/// clkA/clkB は区画ごとのクロックピン座標 (issue #7 (b)。既定は各 1 本)。
let analyzeTwoPhaseAndPrint (opts: Options) (grid: LGrid) (meta: RoutedMeta) (clkA: Coord list) (clkB: Coord list) : int =
    let dg = toDense grid
    let aIdxs = clkA |> List.choose (indexOf dg)
    let bIdxs = clkB |> List.choose (indexOf dg)
    if aIdxs.Length = clkA.Length && bIdxs.Length = clkB.Length && not aIdxs.IsEmpty && not bIdxs.IsEmpty then
        let sw = Diagnostics.Stopwatch.StartNew ()
        let report = analyzeTwoPhaseMulti dg aIdxs bIdxs
        printfn "%s: grid %dx%d, 2 相クロック clk_a[%d] %s / clk_b[%d] %s, 解析 %.1f 秒"
            meta.Circuit dg.Width dg.Height clkA.Length (fmtCoords clkA) clkB.Length (fmtCoords clkB) sw.Elapsed.TotalSeconds
        printTwoPhaseReport dg (outputNames meta) opts.TopCount report
        printfn "クロック木の最大到達時間 (下限 = 各端子から最も近いピンまでのマンハッタン距離の最大。ピンごとの下限との比較):"
        printfn "  %s" (describeLatency "clk_a" dg aIdxs report.ClockA)
        printfn "  %s" (describeLatency "clk_b" dg bIdxs report.ClockB)
        if twoPhaseInvariantHolds report then 0 else 3
    else
        eprintfn "ERROR: 2 相クロックピン clk_a %s / clk_b %s のいずれかがグリッド外" (fmtCoords clkA) (fmtCoords clkB)
        1

/// grid と meta を解析して表示し、終了コードを返す。
let analyzeAndPrint (opts: Options) (grid: LGrid) (meta: RoutedMeta) : int =
    match meta.Clocking with
    | TwoPhaseClocking (_, clkA, clkB) -> analyzeTwoPhaseAndPrint opts grid meta clkA clkB
    | SingleEdgeClocking ->
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
            printfn "%s" (describeLatency "クロック木の最大到達時間 (clk)" dg [ clkIdx ] report.Clock)
            let hasViolation = report.Pairs |> List.exists (fun p -> p.Slack < 0)
            if hasViolation then 3 else 0

let runRouted (opts: Options) (circuit: string) (dir: string) : int =
    match load dir circuit with
    | Error e ->
        eprintfn "ERROR: %s" (describeError e)
        1
    | Ok (grid, meta) -> analyzeAndPrint opts grid meta

let runCompile (opts: Options) (circuit: string) (placement: GatePlacement.PlacementStrategy) (clocking: Clocking.ClockingScheme) : int =
    let path = Path.Combine (repoRoot, "verilog", circuit + ".json")
    if not (File.Exists path) then
        eprintfn "ERROR: %s がない" path
        1
    else
        let json = File.ReadAllText path
        let provenance = { GitCommit = "(analyze)"; CreatedAtUtc = DateTimeOffset.UtcNow }
        let compileOptions = { PipelineWL.defaultCompileOptions with Placement = placement; Clocking = clocking; ClockPins = opts.ClockPins }
        let compiled =
            PipelineWL.compileWLWithOptions compileOptions json
            |> Result.mapError (sprintf "compileWL 失敗: %A")
        let ported = parseYosysPorts json |> Result.mapError describeError
        match compiled, ported with
        | Error e, _ | _, Error e ->
            eprintfn "ERROR: %s" e
            1
        | Ok c, Ok ports ->
            match buildMetaOfCompiled circuit (sourceSha256 (File.ReadAllBytes path)) provenance ports c with
            | Error e ->
                eprintfn "ERROR: %s" (describeError e)
                1
            | Ok meta ->
                // meta の座標は正規化座標なので、グリッドも .bin と同じ往復で正規化する
                let normalized = importGrid (exportGrid c.Grid)
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
        eprintfn "        dotnet fsi src/AnalyzeHold.fsx --compile <circuit> [--place rowmajor|anneal] [--clocking single|two-phase] [--top N]"
        eprintfn "        dotnet fsi src/AnalyzeHold.fsx --selftest"
        2
    | Ok opts ->
        match opts.Source with
        | SelfTest -> runSelfTest ()
        | Routed (circuit, dir) -> runRouted opts circuit dir
        | Compile (circuit, placement, clocking) -> runCompile opts circuit placement clocking

exit exitCode
