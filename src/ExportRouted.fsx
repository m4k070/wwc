#r "bin/Debug/net8.0/WwHdl.dll"
// ExportRouted.fsx — 回路を配線し、結果を <out>/<circuit>.{bin,meta.json} に保存する (TODO Step A)
//
// 使い方:
//   dotnet fsi src/ExportRouted.fsx <circuit> [--pitch X Y] [--out DIR]
//                                   [--place rowmajor|anneal|anneal-timing] [--moves N] [--seed N] [--backward P]
//                                   [--td-alpha A] [--td-beta B] [--td-rounds N]
//                                   [--clocking single|two-phase]
//     <circuit>  verilog/<circuit>.json を読む (例: sm83_subset)
//     --pitch    ピッチ固定 (省略時は compileWL の自動決定 + 輻輳時の自動拡大)
//     --out      出力先 (既定: routed/)
//     --place    配置戦略 (既定 rowmajor)。anneal = アーク距離のアニーリング最適化。
//                anneal-timing = anneal の結果からタイミング駆動の再アニーリング (2 相のみ、issue #7 (c))
//     --moves / --seed / --backward
//                anneal のパラメータ (既定は GatePlacement.defaultAnnealConfig)
//     --td-alpha / --td-beta / --td-rounds
//                anneal-timing のパラメータ (既定は GatePlacement.defaultTimingDrivenConfig)
//     --clocking クロック方式 (既定 single)。two-phase = 各 DFF をマスター (clk_a) / スレーブ (clk_b) に
//                分ける 2 相ノンオーバーラップクロック。skew 均等化をせず、hold は相間の settle で守る。
//                meta の clocking に clkA / clkB の座標が入り、元の clk は inputs に載らない
//                (駆動手順は DESIGN-VERIFY.md §5.2)
//     --clock-pins K
//                クロックピンの区画分割数 (既定 1 = 従来どおり)。2 相のときだけ意味を持つ
//                (issue #7 (b))。clk_a / clk_b それぞれを K 個の区画に分け、各区画の中心に
//                ピンを置く。ホストは同じクロックの全ピンを同じ世代・同じ値で駆動する
//
// 長時間配線 (sm83_subset 約 100 分 / sm83_full 5〜8 時間) はバックグラウンドで:
//   nohup dotnet fsi src/ExportRouted.fsx sm83_full > routed/sm83_full.log 2>&1 &
//
// 終了コード: 0=保存成功 / 1=配線または meta 生成の失敗 / 2=引数エラー
// 保存後はすぐ再読込して整合性を確認する。meta 生成に失敗しても grid は .rescue.bin に退避する。
open System
open System.IO
open System.Diagnostics
open WwHdl
open WwHdl.PipelineWL
open WwHdl.RoutedArtifact

let repoRoot = Path.GetFullPath (Path.Combine (__SOURCE_DIRECTORY__, ".."))

/// CLI で選ぶ配置戦略。
type PlaceChoice =
    | PlaceRowMajor
    | PlaceAnneal
    | PlaceAnnealTiming

type Options =
    { Circuit: string
      Pitch: (int * int) option
      OutDir: string
      Place: PlaceChoice
      Clocking: Clocking.ClockingScheme
      /// クロックピンの区画分割数 (既定 1、issue #7 (b))。
      ClockPins: int
      /// --place の前後どちらに --moves 等を書いても効くよう、anneal 設定は常に保持する
      Anneal: GatePlacement.AnnealConfig
      TimingDriven: GatePlacement.TimingDrivenConfig }

let placementStrategy (opts: Options) : GatePlacement.PlacementStrategy =
    match opts.Place with
    | PlaceRowMajor -> GatePlacement.RowMajor
    | PlaceAnneal -> GatePlacement.Annealed opts.Anneal
    | PlaceAnnealTiming -> GatePlacement.TimingDriven (opts.Anneal, opts.TimingDriven)

let parseArgs (args: string list) : Result<Options, string> =
    let rec go (opts: Options) (rest: string list) =
        match rest with
        | [] -> Ok opts
        | "--pitch" :: x :: y :: tail ->
            match Int32.TryParse x, Int32.TryParse y with
            | (true, px), (true, py) -> go { opts with Pitch = Some (px, py) } tail
            | _ -> Error (sprintf "--pitch には整数が 2 つ必要: %s %s" x y)
        | "--out" :: dir :: tail -> go { opts with OutDir = Path.GetFullPath dir } tail
        | "--place" :: "rowmajor" :: tail -> go { opts with Place = PlaceRowMajor } tail
        | "--place" :: "anneal" :: tail -> go { opts with Place = PlaceAnneal } tail
        | "--place" :: "anneal-timing" :: tail -> go { opts with Place = PlaceAnnealTiming } tail
        | "--place" :: v :: _ -> Error (sprintf "--place は rowmajor|anneal|anneal-timing: %s" v)
        | "--td-alpha" :: v :: tail ->
            match Double.TryParse (v, Globalization.NumberStyles.Float, Globalization.CultureInfo.InvariantCulture) with
            | true, a when a >= 0.0 -> go { opts with TimingDriven = { opts.TimingDriven with Alpha = a } } tail
            | _ -> Error (sprintf "--td-alpha には 0 以上の数が必要: %s" v)
        | "--td-beta" :: v :: tail ->
            match Double.TryParse (v, Globalization.NumberStyles.Float, Globalization.CultureInfo.InvariantCulture) with
            | true, b when b > 0.0 -> go { opts with TimingDriven = { opts.TimingDriven with Beta = b } } tail
            | _ -> Error (sprintf "--td-beta には正の数が必要: %s" v)
        | "--td-rounds" :: v :: tail ->
            match Int32.TryParse v with
            | true, r when r >= 0 -> go { opts with TimingDriven = { opts.TimingDriven with Rounds = r } } tail
            | _ -> Error (sprintf "--td-rounds には 0 以上の整数が必要: %s" v)
        | "--clocking" :: "single" :: tail -> go { opts with Clocking = Clocking.SingleEdge } tail
        | "--clocking" :: "two-phase" :: tail -> go { opts with Clocking = Clocking.TwoPhase } tail
        | "--clocking" :: v :: _ -> Error (sprintf "--clocking は single|two-phase: %s" v)
        | "--clock-pins" :: n :: tail ->
            match Int32.TryParse n with
            | true, v when v >= 1 -> go { opts with ClockPins = v } tail
            | _ -> Error (sprintf "--clock-pins には 1 以上の整数が必要: %s" n)
        | "--moves" :: n :: tail ->
            match Int32.TryParse n with
            | true, v when v >= 0 -> go { opts with Anneal = { opts.Anneal with Moves = v } } tail
            | _ -> Error (sprintf "--moves には 0 以上の整数が必要: %s" n)
        | "--seed" :: n :: tail ->
            match UInt64.TryParse n with
            | true, v -> go { opts with Anneal = { opts.Anneal with Seed = v } } tail
            | _ -> Error (sprintf "--seed には 0 以上の整数が必要: %s" n)
        | "--backward" :: p :: tail ->
            match Double.TryParse (p, Globalization.NumberStyles.Float, Globalization.CultureInfo.InvariantCulture) with
            | true, v when v >= 0.0 -> go { opts with Anneal = { opts.Anneal with BackwardPenalty = v } } tail
            | _ -> Error (sprintf "--backward には 0 以上の数が必要: %s" p)
        | ("--place" | "--moves" | "--seed" | "--backward" | "--out" | "--clocking" | "--clock-pins" | "--td-alpha" | "--td-beta" | "--td-rounds") as o :: [] ->
            Error (sprintf "%s の引数が足りない" o)
        | arg :: _ when arg.StartsWith "--" -> Error (sprintf "不明なオプション: %s" arg)
        | name :: tail when opts.Circuit = "" -> go { opts with Circuit = name } tail
        | extra :: _ -> Error (sprintf "余分な引数: %s" extra)
    let defaults =
        { Circuit = ""; Pitch = None; OutDir = Path.Combine (repoRoot, "routed"); Place = PlaceRowMajor
          Clocking = Clocking.SingleEdge
          ClockPins = 1
          Anneal = GatePlacement.defaultAnnealConfig
          TimingDriven = GatePlacement.defaultTimingDrivenConfig }
    match go defaults args with
    | Ok opts when opts.Circuit = "" -> Error "回路名が必要 (例: sm83_subset)"
    | result -> result

/// 生成時のソース版を記録する (監査用)。src/ か verilog/ に未コミット変更があれば "+dirty" を付ける。
let currentGitCommit () : string =
    let runGit (args: string) : string option =
        try
            let psi =
                ProcessStartInfo ("git", args,
                                  RedirectStandardOutput = true,
                                  UseShellExecute = false,
                                  WorkingDirectory = repoRoot)
            use proc = Process.Start psi
            let output = proc.StandardOutput.ReadToEnd().Trim ()
            proc.WaitForExit ()
            if proc.ExitCode = 0 then Some output else None
        with ex ->
            eprintfn "[export] git %s に失敗: %s" args ex.Message
            None
    match runGit "rev-parse HEAD" with
    | None -> "unknown"
    | Some sha ->
        match runGit "status --porcelain -- src verilog" with
        | Some "" -> sha
        | Some _ -> sha + "+dirty"
        | None -> sha

let fileSize (path: string) = FileInfo(path).Length

let printSummary (opts: Options) (meta: RoutedMeta) =
    let probeLabels (predicate: OutputProbe -> bool) =
        meta.Outputs
        |> Map.toList
        |> List.collect (fun (name, probes) ->
            probes |> List.indexed |> List.filter (snd >> predicate) |> List.map (fun (i, _) -> sprintf "%s[%d]" name i))
    let constBits = probeLabels (function ConstProbe _ -> true | _ -> false)
    let unobservable = probeLabels (function Unobservable _ -> true | _ -> false)
    printfn "[export] grid %dx%d, gates=%d (DFF %d)" meta.Width meta.Height meta.GateCount meta.DffCount
    printfn "[export] inputs:  %s" (meta.Inputs |> Map.toList |> List.map (fun (n, cs) -> sprintf "%s[%d]" n cs.Length) |> String.concat " ")
    printfn "[export] outputs: %s" (meta.Outputs |> Map.toList |> List.map (fun (n, ps) -> sprintf "%s[%d]" n ps.Length) |> String.concat " ")
    match meta.Clocking with
    | SingleEdgeClocking -> printfn "[export] clocking: singleEdge"
    | TwoPhaseClocking (port, a, b) ->
        let fmt (cs: Domain.Coord list) = cs |> List.map (fun c -> sprintf "(%d,%d)" c.X c.Y) |> String.concat " "
        printfn "[export] clocking: twoPhase (%s → clkA[%d] %s / clkB[%d] %s)" port a.Length (fmt a) b.Length (fmt b)
    if not constBits.IsEmpty then
        printfn "[export] 定数ビット (%d): %s" constBits.Length (String.concat " " constBits)
    if not unobservable.IsEmpty then
        eprintfn "[export] WARN 観測不能な出力ビット (%d): %s" unobservable.Length (String.concat " " unobservable)
    let bp = binPath opts.OutDir meta.Circuit
    let mp = metaPath opts.OutDir meta.Circuit
    printfn "[export] 保存: %s (%d byte)" bp (fileSize bp)
    printfn "[export] 保存: %s (%d byte)" mp (fileSize mp)
    printfn "[export] source sha256=%s git=%s" meta.SourceSha256 meta.GitCommit

let exportCircuit (opts: Options) : int =
    let srcPath = Path.Combine (repoRoot, "verilog", opts.Circuit + ".json")
    if not (File.Exists srcPath) then
        eprintfn "ERROR: %s がない" srcPath
        2
    else
        let sourceBytes = File.ReadAllBytes srcPath
        let json = Text.Encoding.UTF8.GetString sourceBytes
        // ポート解析は配線前に済ませる (長時間配線の後で失敗しないように)
        match parseYosysPorts json with
        | Error e ->
            eprintfn "ERROR: %s" (describeError e)
            1
        | Ok ports ->
            let pitchLabel = opts.Pitch |> Option.map (fun (x, y) -> sprintf "%dx%d" x y) |> Option.defaultValue "auto"
            let placeLabel =
                match placementStrategy opts with
                | GatePlacement.RowMajor -> "rowmajor"
                | GatePlacement.Annealed c ->
                    sprintf "anneal moves=%d seed=%d T0=%g T1=%g backward=%g"
                        c.Moves c.Seed c.InitialTemperature c.FinalTemperature c.BackwardPenalty
                | GatePlacement.TimingDriven (c, t) ->
                    sprintf "anneal-timing moves=%d seed=%d T0=%g T1=%g backward=%g | alpha=%g beta=%g rounds=%d movesPerRound=%d T0=%g T1=%g msWeight=%g memory=%g"
                        c.Moves c.Seed c.InitialTemperature c.FinalTemperature c.BackwardPenalty
                        t.Alpha t.Beta t.Rounds t.MovesPerRound t.InitialTemperature t.FinalTemperature
                        t.MasterSlaveWeight t.CriticalityMemory
            let clockingLabel =
                match opts.Clocking with
                | Clocking.SingleEdge -> "single"
                | Clocking.TwoPhase -> "two-phase"
            printfn "[export] %s: compileWL 開始 (pitch=%s, place=%s, clocking=%s, clock-pins=%d, %s)"
                opts.Circuit pitchLabel placeLabel clockingLabel opts.ClockPins (DateTimeOffset.Now.ToString "yyyy-MM-dd HH:mm:ss")
            let sw = Stopwatch.StartNew ()
            let compileOptions =
                { Placement = placementStrategy opts
                  Pitch =
                    match opts.Pitch with
                    | Some (px, py) -> FixedPitch (px, py)
                    | None -> AutoPitch
                  Clocking = opts.Clocking
                  ClockPins = opts.ClockPins }
            let compiled = compileWLWithOptions compileOptions json
            match compiled with
            | Error e ->
                eprintfn "COMPILE ERROR (%.1f 分): %A" sw.Elapsed.TotalMinutes e
                1
            | Ok compiledCircuit ->
                let grid = compiledCircuit.Grid
                printfn "[export] compileWL OK (%.1f 分)" sw.Elapsed.TotalMinutes
                let provenance : Provenance = { GitCommit = currentGitCommit (); CreatedAtUtc = DateTimeOffset.UtcNow }
                match buildMetaOfCompiled opts.Circuit (sourceSha256 sourceBytes) provenance ports compiledCircuit with
                | Error e ->
                    // 配線結果は失わない: meta なしでも grid を退避する
                    Directory.CreateDirectory opts.OutDir |> ignore
                    let rescuePath = Path.Combine (opts.OutDir, opts.Circuit + ".rescue.bin")
                    File.WriteAllBytes (rescuePath, WireLevel.exportGrid grid)
                    eprintfn "META ERROR: %s (grid は %s に退避)" (describeError e) rescuePath
                    1
                | Ok meta ->
                    save opts.OutDir meta grid
                    match load opts.OutDir opts.Circuit with
                    | Error e ->
                        eprintfn "RELOAD ERROR: %s" (describeError e)
                        1
                    | Ok _ ->
                        printSummary opts meta
                        printfn "[export] 再読込による整合性確認 OK"
                        0

let exitCode =
    match parseArgs (fsi.CommandLineArgs |> Array.toList |> List.tail) with
    | Error msg ->
        eprintfn "ERROR: %s" msg
        eprintfn "使い方: dotnet fsi src/ExportRouted.fsx <circuit> [--pitch X Y] [--out DIR] [--place rowmajor|anneal|anneal-timing] [--moves N] [--seed N] [--backward P] [--td-alpha A] [--td-beta B] [--td-rounds N] [--clocking single|two-phase] [--clock-pins K]"
        2
    | Ok opts -> exportCircuit opts

exit exitCode
