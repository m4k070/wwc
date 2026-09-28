#r "bin/Debug/net8.0/WwHdl.dll"
// AnalyzePlacementTiming.fsx — 配置段階の静的タイミング解析とタイミング駆動配置の掃引 (issue #7 (c))
//
// 使い方:
//   dotnet fsi src/AnalyzePlacementTiming.fsx <circuit> [--pitch X Y] [--moves N] [--seed N]
//                                             [--init-cache FILE] [--routed DIR]
//                                             [--sweep "α,β,rounds,movesPerRound,T0,msWeight,memory;..."]
//     <circuit>     verilog/<circuit>.json を 2 相化して配置する (配線はしない)
//     --pitch       配置ピッチ (既定 20 14 = sm83_full の配線実績)
//     --moves/--seed 初期解 (総アーク距離のアニーリング) の設定。既定は defaultAnnealConfig
//     --init-cache  初期解の割り当てをこのファイルに保存 / あれば読む (掃引の繰り返しを速くする)
//     --routed      routed/<circuit>.{bin,meta.json} の配線後の予測 (AnalyzeTiming と同じ解析) と、
//                   初期解 (= その配線に使った配置のはず) の配置段階の予測を DFF ごとに突き合わせる
//     --routed-timing  --routed の突き合わせ相手を、初期解ではなく既定のタイミング駆動配置
//                   (defaultTimingDrivenConfig、--place anneal-timing と同じ解) にする
//     --sweep       初期解からのタイミング駆動の再アニーリングを、設定ごとに試して表にする
//                   (T1 は defaultTimingDrivenConfig.FinalTemperature)
//
// 終了コード: 0=成功 / 1=読込・配置・解析のエラー / 2=引数エラー
open System
open System.IO
open WwHdl
open WwHdl.Domain
open WwHdl.Netlist
open WwHdl.PlacementTiming

let repoRoot = Path.GetFullPath (Path.Combine (__SOURCE_DIRECTORY__, ".."))

/// アーク距離 → 実セグメント需要の換算係数 (AnalyzePlacement.fsx の SegmentNeedFactor と同じ実績値)。
let SegmentNeedFactor = 1.040

type Options =
    { Circuit: string
      Pitch: int * int
      Anneal: GatePlacement.AnnealConfig
      InitCache: string option
      RoutedDir: string option
      RoutedIsTimingDriven: bool
      Sweep: GatePlacement.TimingDrivenConfig list }

let parseFloat (s: string) : float option =
    match Double.TryParse (s, Globalization.NumberStyles.Float, Globalization.CultureInfo.InvariantCulture) with
    | true, v -> Some v
    | _ -> None

/// "α,β,rounds,moves,T0" を 1 設定に。
let parseSweepEntry (entry: string) : Result<GatePlacement.TimingDrivenConfig, string> =
    match entry.Split ',' |> Array.map (fun s -> s.Trim ()) with
    | [| a; b; r; m; t0; ms; mem |] ->
        match parseFloat a, parseFloat b, Int32.TryParse r, Int32.TryParse m, parseFloat t0, parseFloat ms, parseFloat mem with
        | Some alpha, Some beta, (true, rounds), (true, moves), Some temp, Some msWeight, Some memory ->
            Ok { GatePlacement.defaultTimingDrivenConfig with
                    Alpha = alpha; Beta = beta; Rounds = rounds; MovesPerRound = moves; InitialTemperature = temp
                    MasterSlaveWeight = msWeight; CriticalityMemory = memory }
        | _ -> Error (sprintf "--sweep の項目が読めない: %s" entry)
    | _ -> Error (sprintf "--sweep の項目は α,β,rounds,moves,T0,msWeight,memory の 7 つ: %s" entry)

let parseArgs (args: string list) : Result<Options, string> =
    let rec go (opts: Options) (rest: string list) =
        match rest with
        | [] -> Ok opts
        | "--pitch" :: x :: y :: tail ->
            match Int32.TryParse x, Int32.TryParse y with
            | (true, px), (true, py) when px > 0 && py > 0 -> go { opts with Pitch = (px, py) } tail
            | _ -> Error (sprintf "--pitch には正の整数が 2 つ必要: %s %s" x y)
        | "--moves" :: n :: tail ->
            match Int32.TryParse n with
            | true, v when v >= 0 -> go { opts with Anneal = { opts.Anneal with Moves = v } } tail
            | _ -> Error (sprintf "--moves には 0 以上の整数が必要: %s" n)
        | "--seed" :: n :: tail ->
            match UInt64.TryParse n with
            | true, v -> go { opts with Anneal = { opts.Anneal with Seed = v } } tail
            | _ -> Error (sprintf "--seed には 0 以上の整数が必要: %s" n)
        | "--init-cache" :: f :: tail -> go { opts with InitCache = Some (Path.GetFullPath f) } tail
        | "--routed" :: d :: tail -> go { opts with RoutedDir = Some (Path.GetFullPath d) } tail
        | "--routed-timing" :: tail -> go { opts with RoutedIsTimingDriven = true } tail
        | "--sweep" :: spec :: tail ->
            let entries =
                spec.Split (';', StringSplitOptions.RemoveEmptyEntries)
                |> Array.toList
                |> List.map parseSweepEntry
            match entries |> List.tryPick (function Error e -> Some e | Ok _ -> None) with
            | Some e -> Error e
            | None -> go { opts with Sweep = opts.Sweep @ (entries |> List.choose (function Ok c -> Some c | Error _ -> None)) } tail
        | ("--pitch" | "--moves" | "--seed" | "--init-cache" | "--routed" | "--sweep") as o :: _ ->
            Error (sprintf "%s の引数が足りない" o)
        | arg :: _ when arg.StartsWith "--" -> Error (sprintf "不明なオプション: %s" arg)
        | name :: tail when opts.Circuit = "" -> go { opts with Circuit = name } tail
        | extra :: _ -> Error (sprintf "余分な引数: %s" extra)
    let defaults =
        { Circuit = ""; Pitch = (20, 14); Anneal = GatePlacement.defaultAnnealConfig
          InitCache = None; RoutedDir = None; RoutedIsTimingDriven = false; Sweep = [] }
    match go defaults args with
    | Ok opts when opts.Circuit = "" -> Error "回路名が必要 (例: sm83_full)"
    | result -> result

// --- 表示用の集計 ------------------------------------------------------------

let median (xs: int[]) : int =
    if xs.Length = 0 then 0 else (Array.sort xs).[xs.Length / 2]

/// マスター → スレーブの配置距離 (スレーブの D アークの長さ)。
let masterSlaveDistances (net: TimingNetwork) (coordOf: int -> Coord) : int[] =
    net.Arcs
    |> Array.choose (fun a ->
        match a.Source, net.Roles.[a.Sink] with
        | FromGateOutput m, Slave when net.Roles.[m] = Master ->
            let p, q = coordOf m, coordOf a.Sink
            Some (abs (p.X - q.X) + abs (p.Y - q.Y))
        | _ -> None)

/// ピンを含む配置の外接矩形の面積 (AnalyzePlacement.fsx の bbox と同じ考え方)。
let boundingArea (placed: PipelineWL.WlPlaced list) (pins: Map<NetId, Coord>) : int64 =
    let coords = (placed |> List.map (fun p -> p.Coord)) @ (pins |> Map.toList |> List.map snd)
    let xs = coords |> List.map (fun c -> c.X)
    let ys = coords |> List.map (fun c -> c.Y)
    int64 (List.max xs - List.min xs + 1) * int64 (List.max ys - List.min ys + 1)

type Evaluation =
    { Summary: TimingSummary
      ArcDistance: int64
      AreaRatio: float
      MasterSlaveMedian: int
      MasterSlaveMax: int
      Windows: WindowTiming list }

/// クロックピンは常に 1 本 (分割なし、issue #7 (b) は AnalyzePlacementTiming.fsx では未対応。
/// ExportRouted.fsx --clock-pins K で作った配線結果は --routed の突き合わせ対象にしない)。
let private resolvePinsOf (grid: GatePlacement.SlotGrid) (circuit: PipelineWL.PreparedCircuit) (x: GatePlacement.Assignment) =
    let placedX, pinsX, _ = PipelineWL.placeCircuitFromAssignment grid circuit x 1
    let placedArr = List.toArray placedX
    pinsX, (fun (g: int) -> List.head placedArr.[g].Gate.Inputs)

let evaluatePlacement (grid: GatePlacement.SlotGrid) (circuit: PipelineWL.PreparedCircuit) (net: TimingNetwork) (a: GatePlacement.Assignment)
    : Result<Evaluation, string> =
    let placed, pins, _ = PipelineWL.placeCircuitFromAssignment grid circuit a 1
    let coordOf (g: int) = GatePlacement.slotCoord grid a.[g]
    evaluate defaultTimingModel grid net (resolvePinsOf grid circuit) a
    |> Result.mapError describePlacementTimingError
    |> Result.map (fun (windows, _, distance) ->
        let ms = masterSlaveDistances net coordOf
        { Summary = summarize windows
          ArcDistance = distance
          AreaRatio = float distance * SegmentNeedFactor / float (boundingArea placed pins)
          MasterSlaveMedian = median ms
          MasterSlaveMax = (if ms.Length = 0 then 0 else Array.max ms)
          Windows = windows })

// --- 配線後の予測との突き合わせ ------------------------------------------------

let pearson (xs: float[]) (ys: float[]) : float =
    let n = float xs.Length
    let mx, my = Array.average xs, Array.average ys
    let cov = Array.map2 (fun x y -> (x - mx) * (y - my)) xs ys |> Array.sum
    let sx = xs |> Array.sumBy (fun x -> (x - mx) ** 2.0) |> sqrt
    let sy = ys |> Array.sumBy (fun y -> (y - my) ** 2.0) |> sqrt
    if sx = 0.0 || sy = 0.0 || n = 0.0 then nan else cov / (sx * sy)

let compareWithRouted
        (dir: string)
        (circuit: string)
        (grid: GatePlacement.SlotGrid)
        (prepared: PipelineWL.PreparedCircuit)
        (net: TimingNetwork)
        (a: GatePlacement.Assignment)
        (eval: Evaluation)
    : Result<unit, string> =
    match RoutedArtifact.load dir circuit with
    | Error e -> Error (RoutedArtifact.describeError e)
    | Ok (lgrid, meta) ->
        match meta.Clocking with
        | RoutedArtifact.SingleEdgeClocking -> Error "配線結果が 2 相でない"
        | RoutedArtifact.TwoPhaseClocking (_, a, b) when a.Length <> 1 || b.Length <> 1 ->
            Error (sprintf "配線結果のクロックピンが複数本 (clk_a %d 本 / clk_b %d 本) — --routed の突き合わせは k=1 のみ対応 (issue #7 (b))" a.Length b.Length)
        | RoutedArtifact.TwoPhaseClocking (_, [ clkA ], [ clkB ]) ->
            let _, pins, _ = PipelineWL.placeCircuitFromAssignment grid prepared a 1
            let placedClkA = Map.find net.ClockA pins
            // meta の座標は正規化座標 (exportGrid が原点を詰める)。クロックピンでずれを求める
            let dx, dy = clkA.X - placedClkA.X, clkA.Y - placedClkA.Y
            let placedClkB = Map.find net.ClockB pins
            if clkB.X - placedClkB.X <> dx || clkB.Y - placedClkB.Y <> dy then
                Error (sprintf "クロックピンのずれが clk_a と clk_b で違う: 配線に使った配置と初期解が一致していない (clk_a %A→%A, clk_b %A→%A)" placedClkA clkA placedClkB clkB)
            else
            let dg = HoldAnalysis.toDense lgrid
            match HoldAnalysis.indexOf dg clkA, HoldAnalysis.indexOf dg clkB with
            | None, _ | _, None -> Error "クロックピンがグリッド外"
            | Some ai, Some bi ->
                match TimingAnalysis.analyzeTwoPhaseTiming dg meta [ ai ] [ bi ] with
                | Error e -> Error (TimingAnalysis.describeTimingError e)
                | Ok reports ->
                    printfn ""
                    printfn "== 配置段階の予測 vs 配線後の予測 (AnalyzeTiming、DFF の D ごと) =="
                    printfn "%-8s %8s %8s %8s | %6s %8s %8s %8s" "窓" "配置予測" "配線予測" "比" "組数" "相関 r" "比 中央" "比 90%"
                    let routedWindowOf (w: Window) =
                        match w with
                        | Window.DataIn -> TimingAnalysis.DataInWindow
                        | Window.PhaseA -> TimingAnalysis.PhaseAWindow
                        | Window.PhaseB -> TimingAnalysis.PhaseBWindow
                    for w in eval.Windows do
                        match reports |> List.tryFind (fun r -> r.Window = routedWindowOf w.Window) with
                        | None -> ()
                        | Some r ->
                            let routedByCoord =
                                r.Paths
                                |> List.choose (fun p ->
                                    match p.Target with
                                    | TimingAnalysis.DffD dff -> Some (HoldAnalysis.coordOf dg dff, p.PredictedGen)
                                    | TimingAnalysis.ProbePoint _ -> None)
                                |> Map.ofList
                            let pairs =
                                w.EndpointArrival
                                |> Map.toList
                                |> List.choose (fun (g, placedArrival) ->
                                    let c = GatePlacement.slotCoord grid a.[g]
                                    Map.tryFind { X = c.X + dx; Y = c.Y + dy } routedByCoord
                                    |> Option.map (fun routed -> float placedArrival, float routed))
                                |> Array.ofList
                            let ratios =
                                pairs |> Array.filter (fun (p, _) -> p > 0.0) |> Array.map (fun (p, r) -> r / p) |> Array.sort
                            let pct (q: float) = if ratios.Length = 0 then nan else ratios.[min (ratios.Length - 1) (int (q * float ratios.Length))]
                            printfn "%-8s %8d %8d %8.3f | %6d %8.4f %8.3f %8.3f"
                                (windowLabel w.Window) w.PredictedMax r.PredictedMax
                                (float r.PredictedMax / float (max 1 w.PredictedMax))
                                pairs.Length
                                (if pairs.Length < 2 then nan else pearson (Array.map fst pairs) (Array.map snd pairs))
                                (pct 0.5) (pct 0.9)
                    Ok ()

// --- 初期解 ------------------------------------------------------------------

let loadOrComputeInitial (opts: Options) (grid: GatePlacement.SlotGrid) (prepared: PipelineWL.PreparedCircuit)
    : Result<GatePlacement.Assignment, string> =
    let n = prepared.Netlist.Gates.Length
    let compute () =
        let sw = Diagnostics.Stopwatch.StartNew ()
        GatePlacement.anneal opts.Anneal grid n (PipelineWL.annealArcsOf (snd opts.Pitch) prepared) (GatePlacement.rowMajorAssignment n)
        |> Result.mapError GatePlacement.describeConfigError
        |> Result.map (fun o ->
            printfn "[init] anneal moves=%s seed=%d: %.1f 秒" (opts.Anneal.Moves.ToString "N0") opts.Anneal.Seed sw.Elapsed.TotalSeconds
            o.Best)
    match opts.InitCache with
    | Some path when File.Exists path ->
        let a = File.ReadAllText(path).Split (' ', StringSplitOptions.RemoveEmptyEntries) |> Array.map int
        match GatePlacement.validateAssignment grid n a with
        | Ok () ->
            printfn "[init] %s から読込" path
            Ok a
        | Error e -> Error (sprintf "キャッシュ %s が不正: %s" path e)
    | Some path ->
        compute ()
        |> Result.map (fun a ->
            File.WriteAllText (path, a |> Array.map string |> String.concat " ")
            a)
    | None -> compute ()

// --- 本体 --------------------------------------------------------------------

let printEvaluationHeader () =
    printfn "%-40s | %8s %7s %8s %8s | %11s %7s %6s | %9s | %6s" "設定" "data_in" "phaseA" "phaseB" "1周期" "総アーク距離" "増分" "面積比" "M→S 最大" "秒"

let printEvaluationRow (label: string) (baseline: Evaluation) (e: Evaluation) (seconds: float) =
    printfn "%-40s | %8d %7d %8d %8d | %11s %+6.1f%% %6.3f | %9d | %6.1f"
        label e.Summary.DataIn e.Summary.PhaseA e.Summary.PhaseB e.Summary.Cycle
        (e.ArcDistance.ToString "N0")
        (100.0 * (float e.ArcDistance / float baseline.ArcDistance - 1.0))
        e.AreaRatio e.MasterSlaveMax seconds

let run (opts: Options) : int =
    let srcPath = Path.Combine (repoRoot, "verilog", opts.Circuit + ".json")
    if not (File.Exists srcPath) then
        eprintfn "ERROR: %s がない" srcPath
        1
    else
    let prepared =
        Pipeline.frontend (File.ReadAllText srcPath)
        |> Result.mapError (sprintf "frontend: %A")
        |> Result.bind (fun nl -> PipelineWL.prepareCircuit Clocking.TwoPhase nl |> Result.mapError (sprintf "2 相化: %A"))
    match prepared with
    | Error e ->
        eprintfn "ERROR: %s" e
        1
    | Ok prepared ->
    match prepared.Clocking with
    | PipelineWL.SingleEdgeClock _ ->
        eprintfn "ERROR: 2 相化されていない"
        1
    | PipelineWL.TwoPhaseClock tp ->
    match buildNetwork tp with
    | Error e ->
        eprintfn "ERROR: %s" (describePlacementTimingError e)
        1
    | Ok net ->
    let px, py = opts.Pitch
    let grid = PipelineWL.slotGridOf px py prepared
    printfn "[analyze] %s: gates=%d (マスター %d / スレーブ %d), アーク %d, pitch %dx%d"
        opts.Circuit net.Roles.Length
        (net.Roles |> Array.filter ((=) Master) |> Array.length)
        (net.Roles |> Array.filter ((=) Slave) |> Array.length)
        net.Arcs.Length px py
    match loadOrComputeInitial opts grid prepared with
    | Error e ->
        eprintfn "ERROR: %s" e
        1
    | Ok initial ->
    match evaluatePlacement grid prepared net initial with
    | Error e ->
        eprintfn "ERROR: %s" e
        1
    | Ok baseline ->
    printfn ""
    printfn "== 初期解 (総アーク距離のアニーリング) の配置段階の予測 =="
    for w in baseline.Windows do
        printfn "  %-8s 予測最大 %6d (経路 %6d / クロック下限 %6d、終点 %d)"
            (windowLabel w.Window) w.PredictedMax w.PathMax w.ClockFloor w.EndpointArrival.Count
    printfn "  マスター→スレーブ配置距離: 中央値 %d / 最大 %d" baseline.MasterSlaveMedian baseline.MasterSlaveMax
    let routedResult =
        match opts.RoutedDir with
        | None -> Ok ()
        | Some dir when not opts.RoutedIsTimingDriven -> compareWithRouted dir opts.Circuit grid prepared net initial baseline
        | Some dir ->
            PipelineWL.timingDrivenFrom GatePlacement.defaultTimingDrivenConfig opts.Anneal grid prepared initial
            |> Result.mapError (sprintf "%A")
            |> Result.bind (fun td ->
                evaluatePlacement grid prepared net td.Best
                |> Result.bind (fun e ->
                    printfn "  (突き合わせ相手: 既定のタイミング駆動配置 round %d、1 周期の予測 %d)" td.BestRound e.Summary.Cycle
                    compareWithRouted dir opts.Circuit grid prepared net td.Best e))
    match routedResult with
    | Error e ->
        eprintfn "ERROR (配線後との比較): %s" e
        1
    | Ok () ->
    if not opts.Sweep.IsEmpty then
        printfn ""
        printfn "== タイミング駆動の再アニーリングの掃引 (初期解から、T1=%g) ==" GatePlacement.defaultTimingDrivenConfig.FinalTemperature
        printEvaluationHeader ()
        printEvaluationRow "初期解 (anneal)" baseline baseline 0.0
    let failures =
        opts.Sweep
        |> List.choose (fun cfg ->
            let label =
                sprintf "a=%g b=%g R=%d M=%dM T0=%g ms=%g m=%g"
                    cfg.Alpha cfg.Beta cfg.Rounds (cfg.MovesPerRound / 1_000_000) cfg.InitialTemperature
                    cfg.MasterSlaveWeight cfg.CriticalityMemory
            let sw = Diagnostics.Stopwatch.StartNew ()
            match PipelineWL.timingDrivenFrom cfg opts.Anneal grid prepared initial with
            | Error e -> Some (sprintf "%s: %A" label e)
            | Ok td ->
                let seconds = sw.Elapsed.TotalSeconds
                match evaluatePlacement grid prepared net td.Best with
                | Error e -> Some (sprintf "%s: %s" label e)
                | Ok e ->
                    printEvaluationRow (sprintf "%s [r%d]" label td.BestRound) baseline e seconds
                    for r in td.Rounds do
                        printfn "    round %d: data_in %6d phaseA %5d phaseB %6d 1周期 %6d  総アーク距離 %s"
                            r.Round r.Summary.DataIn r.Summary.PhaseA r.Summary.PhaseB r.Summary.Cycle (r.ArcDistance.ToString "N0")
                    None)
    for f in failures do
        eprintfn "ERROR: %s" f
    if failures.IsEmpty then 0 else 1

let exitCode =
    match parseArgs (fsi.CommandLineArgs |> Array.toList |> List.tail) with
    | Error msg ->
        eprintfn "ERROR: %s" msg
        eprintfn "使い方: dotnet fsi src/AnalyzePlacementTiming.fsx <circuit> [--pitch X Y] [--moves N] [--seed N] [--init-cache FILE] [--routed DIR] [--routed-timing] [--sweep \"α,β,rounds,moves,T0,msWeight,memory;...\"]"
        2
    | Ok opts -> run opts

exit exitCode
