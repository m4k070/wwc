#r "bin/Debug/net8.0/WwHdl.dll"
// AnalyzeTiming.fsx — 配線済み WireLevel グリッドのクリティカルパス (最長経路) 静的解析 CLI
//
// 使い方:
//   dotnet fsi src/AnalyzeTiming.fsx <circuit> [--dir DIR] [--top N]
//       routed/<circuit>.{bin,meta.json} を読んで解析する (2 相クロックのみ対応。issue #7 (c))
//   dotnet fsi src/AnalyzeTiming.fsx --compile <circuit> [--place rowmajor|anneal] [--top N]
//       verilog/<circuit>.json をその場で 2 相 + compileWL して解析する (小回路の確認用)
//
// 解析本体 (3 つの収束窓・最長経路 DP など) は src/TimingAnalysis.fs を参照。
// 終了コード: 0=解析成功 / 1=読込・コンパイル・解析エラー (組合せ閉路を含む) / 2=引数エラー
open System
open System.IO
open WwHdl
open WwHdl.Domain
open WwHdl.WireLevel
open WwHdl.RoutedArtifact
open WwHdl.HoldAnalysis
open WwHdl.TimingAnalysis

/// 上位何件のクリティカルパスを表示するか (既定)
[<Literal>]
let DefaultTopCount = 10

let repoRoot = Path.GetFullPath (Path.Combine (__SOURCE_DIRECTORY__, ".."))

// --- 表示 ---------------------------------------------------------------------

/// 座標 → それを直接指す出力ポートのビット名。
let outputNames (meta: RoutedMeta) : Map<Coord, string list> =
    [ for KeyValue (port, probes) in meta.Outputs do
        for (i, probe) in List.indexed probes do
            match probe with
            | CellProbe c -> yield c, sprintf "%s[%d]" port i
            | ConstProbe _ | Unobservable _ -> () ]
    |> List.groupBy fst
    |> List.map (fun (c, xs) -> c, xs |> List.map snd)
    |> Map.ofList

let describeCoord (names: Map<Coord, string list>) (c: Coord) : string =
    match Map.tryFind c names with
    | Some ns -> sprintf "(%d,%d)[%s]" c.X c.Y (String.concat "," ns)
    | None -> sprintf "(%d,%d)" c.X c.Y

let describeTarget (dg: DenseGrid) (names: Map<Coord, string list>) (t: PathEndTarget) : string =
    match t with
    | DffD dff -> sprintf "DFF%s.D" (describeCoord names (coordOf dg dff))
    | ProbePoint (c, ns) -> sprintf "(%d,%d)[%s]" c.X c.Y (String.concat "," ns)

/// 経路長 (セル数) の配列から中央値・90 パーセンタイル・最大を表示用に整形する。
let describeDistribution (lengths: int[]) : string =
    if lengths.Length = 0 then "経路なし"
    else
        let sorted = Array.sort lengths
        sprintf "本数 %d / 中央値 %d / 90%% %d / 最大 %d"
            sorted.Length (percentile 0.5 sorted) (percentile 0.9 sorted) (Array.max sorted)

let describeNandRatio (b: Breakdown) : string =
    if b.Total = 0 then "n/a"
    else sprintf "wire=%d cross=%d nand=%d (NAND 割合 %.1f%%)" b.Wire b.Cross b.Nand (100.0 * float b.Nand / float b.Total)

/// 1 区間 (隣り合うゲート間) の内訳を 1 行で。
let describeSegment (names: Map<Coord, string list>) (s: Segment) : string =
    sprintf "%-24s -> %-24s  配置距離 %6d  実配線 %6d  回り道 %6d"
        (describeCoord names s.From) (describeCoord names s.To) s.PlacementDistance s.WireHops s.Detour

/// 1 経路のゲート単位分解 (配置距離・実配線長・回り道の内訳と区間の並び)。
let printPathBreakdown (names: Map<Coord, string list>) (b: PathBreakdown) : unit =
    printfn "      配置距離計 %6d  実配線計 %6d  回り道計 %6d  (始点-終点マンハッタン %6d、区間 %d 本)"
        b.TotalPlacementDistance b.TotalWireHops b.TotalDetour b.EndToEndManhattan b.Segments.Length
    for s in b.Segments do
        printfn "        %s" (describeSegment names s)

let printWindow
        (dg: DenseGrid)
        (topo: int[])
        (dffDataNodes: Map<int, int>)
        (names: Map<Coord, string list>)
        (topCount: int)
        (r: WindowReport)
    : unit =
    printfn "== %s: 予測最大 %d 世代 (経路 %d 本) ==" (windowLabel r.Window) r.PredictedMax r.Paths.Length
    if List.isEmpty r.Paths then
        printfn "  (この窓で到達する DFF/出力プローブがない)"
    else
        let lengths = r.Paths |> List.map (fun p -> p.PathLength) |> Array.ofList
        printfn "  経路長の分布 (クロック到達を含まないセル数): %s" (describeDistribution lengths)
        printfn "  全経路の内訳合計: %s" (describeNandRatio (totalBreakdown r.Paths))
        let top = List.truncate topCount r.Paths
        printfn "  上位 %d 本の内訳合計: %s" top.Length (describeNandRatio (totalBreakdown top))
        printfn "  %8s %8s %6s %6s %6s %6s  %-24s -> %s" "予測gen" "clk到達" "経路長" "wire" "cross" "nand" "起点" "終点"
        for p in top do
            printfn "  %8d %8d %6d %6d %6d %6d  %-24s -> %s"
                p.PredictedGen p.OriginClockArrival p.PathLength p.Breakdown.Wire p.Breakdown.Cross p.Breakdown.Nand
                (describeCoord names p.Source) (describeTarget dg names p.Target)
        // ゲート単位分解 (配置距離 vs 回り道)。上位 N 本の longestPathsFrom をもう一度
        // 走らせて (analyzeWindow と同じ DP を 1 回だけ再実行) 経路を復元する。issue #7 (c)。
        let breakdowns = pathBreakdownsForWindow dg topo dffDataNodes r top
        printfn "  -- ゲート単位分解 (配置距離 = 隣り合うゲート間のマンハッタン距離の下限、回り道 = 実配線長 - 配置距離) --"
        for (p, bOpt) in breakdowns do
            printfn "    %-24s -> %s" (describeCoord names p.Source) (describeTarget dg names p.Target)
            match bOpt with
            | None -> printfn "      (分解できない: ターゲットノードが見つからない)"
            | Some b -> printPathBreakdown names b
        let allSegments = breakdowns |> List.choose snd |> List.collect (fun b -> b.Segments)
        let placementTotal = allSegments |> List.sumBy (fun s -> s.PlacementDistance)
        let detourTotal = allSegments |> List.sumBy (fun s -> s.Detour)
        let wireTotal = allSegments |> List.sumBy (fun s -> s.WireHops)
        if wireTotal > 0 then
            printfn "  上位 %d 本合計 (区間 %d 本): 配置距離 %d (%.1f%%) / 回り道 %d (%.1f%%) / 実配線 %d"
                top.Length allSegments.Length placementTotal (100.0 * float placementTotal / float wireTotal)
                detourTotal (100.0 * float detourTotal / float wireTotal) wireTotal
        let worstDetours = allSegments |> List.sortByDescending (fun s -> s.Detour) |> List.truncate 10
        if not (List.isEmpty worstDetours) then
            printfn "  -- 回り道が大きい区間 上位 %d --" worstDetours.Length
            for s in worstDetours do
                printfn "    %s" (describeSegment names s)

// --- 実行モード ------------------------------------------------------------------

type Options = { Source: SourceChoice; TopCount: int }

and SourceChoice =
    | Routed of circuit: string * dir: string
    | Compile of circuit: string * placement: GatePlacement.PlacementStrategy

let parseArgs (args: string list) : Result<Options, string> =
    let rec go (opts: Options) (rest: string list) =
        match rest with
        | [] -> Ok opts
        | "--top" :: n :: tail ->
            match Int32.TryParse n with
            | true, v when v > 0 -> go { opts with TopCount = v } tail
            | _ -> Error (sprintf "--top には正の整数が必要: %s" n)
        | "--dir" :: dir :: tail ->
            match opts.Source with
            | Routed (c, _) -> go { opts with Source = Routed (c, Path.GetFullPath dir) } tail
            | _ -> Error "--dir は <circuit> の後に指定する"
        | "--compile" :: circuit :: tail ->
            go { opts with Source = Compile (circuit, GatePlacement.RowMajor) } tail
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
    | _ -> go { Source = Routed ("", Path.Combine (repoRoot, "routed")); TopCount = DefaultTopCount } args

/// grid と meta を解析して表示し、終了コードを返す。2 相クロックのみ対応
/// (この解析全体が clk_a / clk_b の 3 窓モデルに依っているため)。
let analyzeAndPrint (opts: Options) (grid: LGrid) (meta: RoutedMeta) : int =
    match meta.Clocking with
    | SingleEdgeClocking ->
        eprintfn "ERROR: この解析は 2 相クロック (twoPhase) のみ対応 (%s は singleEdge)" meta.Circuit
        1
    | TwoPhaseClocking (_, clkA, clkB) ->
        let dg = toDense grid
        let fmtCoords (cs: Coord list) = cs |> List.map (fun c -> sprintf "(%d,%d)" c.X c.Y) |> String.concat " "
        let ai = clkA |> List.choose (indexOf dg)
        let bi = clkB |> List.choose (indexOf dg)
        if ai.Length <> clkA.Length || bi.Length <> clkB.Length || ai.IsEmpty || bi.IsEmpty then
            eprintfn "ERROR: clk_a %s / clk_b %s のいずれかがグリッド外" (fmtCoords clkA) (fmtCoords clkB)
            1
        else
            let sw = Diagnostics.Stopwatch.StartNew ()
            match analyzeTwoPhaseTiming dg meta ai bi with
            | Error e ->
                eprintfn "ERROR: %s" (describeTimingError e)
                1
            | Ok reports ->
                printfn "%s: grid %dx%d, clk_a[%d] %s / clk_b[%d] %s, 解析 %.1f 秒"
                    meta.Circuit dg.Width dg.Height clkA.Length (fmtCoords clkA) clkB.Length (fmtCoords clkB) sw.Elapsed.TotalSeconds
                let names = outputNames meta
                // ゲート単位分解 (printWindow 内) は buildTopoOrder / buildDffDataNodes を
                // 使い回す。analyzeTwoPhaseTiming が既に成功しているので buildTopoOrder は
                // 必ず Ok になる (組合せグラフは窓に依らず共有)
                match buildTopoOrder dg with
                | Error e ->
                    eprintfn "ERROR: %s (ゲート単位分解の再計算で閉路検出。窓ごとの結果は上に出力済み)" (describeTimingError e)
                    1
                | Ok topo ->
                    let dffDataNodes = buildDffDataNodes dg
                    for r in reports do
                        printWindow dg topo dffDataNodes names opts.TopCount r
                    printfn ""
                    let masterSlave = masterSlaveDistances dg ai bi
                    let dists = masterSlave |> List.map (fun d -> d.PlacementDistance) |> Array.ofList
                    printfn "== マスター→スレーブ配置距離 (2 相化で分割した DFF 対、issue #7 (c) の外れ値調査) =="
                    printfn "  組数 %d (findDffs 由来の DFF 総数の半分程度のはず): %s" masterSlave.Length (describeDistribution dists)
                    let over1000 = dists |> Array.filter (fun d -> d > 1000) |> Array.length
                    printfn "  距離 > 1000 の組: %d" over1000
                    let worst = masterSlave |> List.sortByDescending (fun d -> d.PlacementDistance) |> List.truncate 5
                    printfn "  -- 配置距離が大きい組 上位 %d --" worst.Length
                    for d in worst do
                        printfn "    %-16s -> %-16s  配置距離 %d"
                            (describeCoord names d.Master) (describeCoord names d.Slave) d.PlacementDistance
                    0

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
        let compileOptions = { PipelineWL.defaultCompileOptions with Placement = placement; Clocking = Clocking.TwoPhase }
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

let exitCode =
    match parseArgs (fsi.CommandLineArgs |> Array.toList |> List.tail) with
    | Error msg ->
        eprintfn "%s" msg
        eprintfn "使い方: dotnet fsi src/AnalyzeTiming.fsx <circuit> [--dir DIR] [--top N]"
        eprintfn "        dotnet fsi src/AnalyzeTiming.fsx --compile <circuit> [--place rowmajor|anneal] [--top N]"
        2
    | Ok opts ->
        match opts.Source with
        | Routed ("", _) ->
            eprintfn "回路名を指定してください"
            2
        | Routed (circuit, dir) -> runRouted opts circuit dir
        | Compile (circuit, placement) -> runCompile opts circuit placement

exit exitCode
