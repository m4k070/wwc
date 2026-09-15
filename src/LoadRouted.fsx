#r "bin/Debug/net8.0/WwHdl.dll"
// LoadRouted.fsx — 保存済み配線結果 (<dir>/<circuit>.{bin,meta.json}) を読み込み、
// 整合性 (寸法・ファイル長・ピン/プローブ座標) と鮮度 (verilog JSON の SHA-256) を確認する
//
// 使い方: dotnet fsi src/LoadRouted.fsx <circuit> [--dir DIR]
// 終了コード: 0=OK / 1=読込・整合性エラー / 2=引数エラー / 3=配線後に verilog JSON が変更された
//
// 検証段 (TODO Step B/C) は load → ensureFresh の流れをこのスクリプトから流用する。
open System.IO
open WwHdl
open WwHdl.RoutedArtifact

let repoRoot = Path.GetFullPath (Path.Combine (__SOURCE_DIRECTORY__, ".."))

let parseArgs (args: string list) : (string * string) option =
    match args with
    | [ circuit ] -> Some (circuit, Path.Combine (repoRoot, "routed"))
    | [ circuit; "--dir"; dir ] -> Some (circuit, Path.GetFullPath dir)
    | _ -> None

let describeProbes (probes: OutputProbe list) : string =
    let count predicate = probes |> List.filter predicate |> List.length
    let cells = count (function CellProbe _ -> true | _ -> false)
    let consts = count (function ConstProbe _ -> true | _ -> false)
    let unobservable = count (function Unobservable _ -> true | _ -> false)
    sprintf "%d bit (cell %d, const %d, unobservable %d)" probes.Length cells consts unobservable

let checkArtifact (circuit: string) (dir: string) : int =
    match load dir circuit with
    | Error e ->
        eprintfn "ERROR: %s" (describeError e)
        1
    | Ok (grid, meta) ->
        printfn "%s: grid %dx%d (%d cells), gates=%d (DFF %d)"
            meta.Circuit meta.Width meta.Height (Map.count grid) meta.GateCount meta.DffCount
        printfn "  生成: %s  git=%s" (meta.CreatedAtUtc.ToString "u") meta.GitCommit
        for KeyValue (name, coords) in meta.Inputs do
            printfn "  in  %-12s %d bit" name coords.Length
        for KeyValue (name, probes) in meta.Outputs do
            printfn "  out %-12s %s" name (describeProbes probes)
        printfn "  整合性: OK"
        let srcPath = Path.Combine (repoRoot, "verilog", circuit + ".json")
        let freshness =
            if File.Exists srcPath then ensureFresh (sourceSha256 (File.ReadAllBytes srcPath)) meta
            else Error (FileMissing srcPath)
        match freshness with
        | Ok _ ->
            printfn "  鮮度: OK (verilog JSON は配線時と同一)"
            0
        | Error (StaleSource _ as e) ->
            eprintfn "  STALE: %s" (describeError e)
            3
        | Error e ->
            eprintfn "  ERROR: %s" (describeError e)
            1

let exitCode =
    match parseArgs (fsi.CommandLineArgs |> Array.toList |> List.tail) with
    | None ->
        eprintfn "使い方: dotnet fsi src/LoadRouted.fsx <circuit> [--dir DIR]"
        2
    | Some (circuit, dir) -> checkArtifact circuit dir

exit exitCode
