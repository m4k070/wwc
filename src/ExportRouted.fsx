#r "bin/Debug/net8.0/WwHdl.dll"
// ExportRouted.fsx — 回路を配線し、結果を <out>/<circuit>.{bin,meta.json} に保存する (TODO Step A)
//
// 使い方:
//   dotnet fsi src/ExportRouted.fsx <circuit> [--pitch X Y] [--out DIR]
//     <circuit>  verilog/<circuit>.json を読む (例: sm83_subset)
//     --pitch    ピッチ固定 (省略時は compileWL の自動決定 + 輻輳時の自動拡大)
//     --out      出力先 (既定: routed/)
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

type Options =
    { Circuit: string
      Pitch: (int * int) option
      OutDir: string }

let parseArgs (args: string list) : Result<Options, string> =
    let rec go (opts: Options) (rest: string list) =
        match rest with
        | [] -> Ok opts
        | "--pitch" :: x :: y :: tail ->
            match Int32.TryParse x, Int32.TryParse y with
            | (true, px), (true, py) -> go { opts with Pitch = Some (px, py) } tail
            | _ -> Error (sprintf "--pitch には整数が 2 つ必要: %s %s" x y)
        | "--out" :: dir :: tail -> go { opts with OutDir = Path.GetFullPath dir } tail
        | arg :: _ when arg.StartsWith "--" -> Error (sprintf "不明なオプション: %s" arg)
        | name :: tail when opts.Circuit = "" -> go { opts with Circuit = name } tail
        | extra :: _ -> Error (sprintf "余分な引数: %s" extra)
    let defaults = { Circuit = ""; Pitch = None; OutDir = Path.Combine (repoRoot, "routed") }
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
            printfn "[export] %s: compileWL 開始 (pitch=%s, %s)" opts.Circuit pitchLabel (DateTimeOffset.Now.ToString "yyyy-MM-dd HH:mm:ss")
            let sw = Stopwatch.StartNew ()
            let compiled =
                match opts.Pitch with
                | Some (px, py) -> compileWLWithPitch px py json
                | None -> compileWL json
            match compiled with
            | Error e ->
                eprintfn "COMPILE ERROR (%.1f 分): %A" sw.Elapsed.TotalMinutes e
                1
            | Ok (grid, placed, pins) ->
                printfn "[export] compileWL OK (%.1f 分)" sw.Elapsed.TotalMinutes
                let provenance : Provenance = { GitCommit = currentGitCommit (); CreatedAtUtc = DateTimeOffset.UtcNow }
                match buildMeta opts.Circuit (sourceSha256 sourceBytes) provenance ports grid placed pins with
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
        eprintfn "使い方: dotnet fsi src/ExportRouted.fsx <circuit> [--pitch X Y] [--out DIR]"
        2
    | Ok opts -> exportCircuit opts

exit exitCode
