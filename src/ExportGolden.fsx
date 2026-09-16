#r "bin/Debug/net8.0/WwHdl.dll"
// ExportGolden.fsx — プログラム JSON を NetlistSim で実行し、周期ごとの golden を書き出す (TODO Step B-2)
//
// 使い方: dotnet fsi src/ExportGolden.fsx <program.json>
//   プログラム JSON は wgpu-runner --memory と同じ形式 (DESIGN-VERIFY.md §6.1)。
//   golden の出力先はプログラムの "golden" (省略時は <プログラム名>.golden.json)。
//
// 手順:
//   1. 回路名はプログラムの "circuit"、なければ meta の circuit
//   2. verilog/<circuit>.json の SHA-256 を routed meta と照合 (不一致なら配線結果と合わない golden になるので中止)
//   3. NetlistSim + メモリで実行し、expect / expectMem を最終状態と照合
//   4. golden を書く (expect が不一致でも書く。GPU 側でも同じ結果になるかを確かめられるように)
//
// 終了コード: 0=golden 出力 + expect 合格 / 1=expect 不一致またはシミュレーション失敗 /
//             2=入力エラー / 3=routed meta が現在の verilog JSON と一致しない
open System.IO
open WwHdl
open WwHdl.RoutedArtifact
open WwHdl.NetlistSim
open WwHdl.Testbench

let repoRoot = Path.GetFullPath (Path.Combine (__SOURCE_DIRECTORY__, ".."))

type MetaCheck =
    | MetaMatches of circuit: string
    | MetaMissing
    | MetaStale of reason: string
    | MetaUnreadable of reason: string

let checkMeta (metaPath: string) (sourceSha: string) : MetaCheck =
    if not (File.Exists metaPath) then MetaMissing
    else
        match metaOfJson metaPath (File.ReadAllText metaPath) with
        | Error e -> MetaUnreadable (RoutedArtifact.describeError e)
        | Ok meta ->
            match ensureFresh sourceSha meta with
            | Ok m -> MetaMatches m.Circuit
            | Error e -> MetaStale (RoutedArtifact.describeError e)

let exportGolden (programPath: string) : int =
    match parseProgram programPath (File.ReadAllText programPath) with
    | Error e ->
        eprintfn "ERROR: %s" (describeTestbenchError e)
        2
    | Ok program ->
        let metaCircuit =
            if File.Exists program.MetaPath then
                metaOfJson program.MetaPath (File.ReadAllText program.MetaPath)
                |> Result.toOption
                |> Option.map (fun m -> m.Circuit)
            else None
        match program.Circuit |> Option.orElse metaCircuit with
        | None ->
            eprintfn "ERROR: 回路名が決まらない (プログラムに \"circuit\" がなく、meta も読めない)"
            2
        | Some circuit ->
        let sourcePath = Path.Combine (repoRoot, "verilog", circuit + ".json")
        if not (File.Exists sourcePath) then
            eprintfn "ERROR: %s がない" sourcePath
            2
        else
        let sourceBytes = File.ReadAllBytes sourcePath
        let sourceSha = sourceSha256 sourceBytes
        match checkMeta program.MetaPath sourceSha with
        | MetaStale reason ->
            eprintfn "STALE: %s — 配線し直してから golden を作る" reason
            3
        | MetaUnreadable reason ->
            eprintfn "ERROR: %s" reason
            2
        | metaCheck ->
            match metaCheck with
            | MetaMissing -> eprintfn "WARN: meta %s がない — 配線結果との鮮度照合をスキップ" program.MetaPath
            | _ -> printfn "[golden] meta の sourceSha256 は verilog JSON と一致"
            let json = System.Text.Encoding.UTF8.GetString sourceBytes
            let setup =
                match Pipeline.frontend json, parseYosysPorts json with
                | Error e, _ -> Error (sprintf "frontend: %A" e)
                | _, Error e -> Error (RoutedArtifact.describeError e)
                | Ok nl, Ok ports ->
                    compile nl
                    |> Result.mapError describeSimError
                    |> Result.bind (fun c ->
                        resolveBus ports
                        |> Result.bind (fun bus -> loadRom program.Rom |> Result.map (fun rom -> c, ports, bus, rom))
                        |> Result.mapError describeTestbenchError)
            match setup with
            | Error msg ->
                eprintfn "ERROR: %s" msg
                2
            | Ok (c, ports, bus, rom) ->
                let sw = System.Diagnostics.Stopwatch.StartNew ()
                match run c ports bus (createMemory rom program.Memory) program.RstPulses program.Cycles with
                | Error e ->
                    eprintfn "ERROR: %s" (describeTestbenchError e)
                    1
                | Ok result ->
                    printfn "[golden] %s: %d 周期 (rst %d) を NetlistSim で実行 (%.2fs)"
                        program.ProgramName program.Cycles program.RstPulses sw.Elapsed.TotalSeconds
                    let info : GoldenInfo =
                        { GoldenCircuit = circuit
                          GoldenProgram = program.ProgramName
                          SourceSha256 = sourceSha
                          RomSha256 = sourceSha256 rom
                          RstPulses = program.RstPulses }
                    Directory.CreateDirectory (Path.GetDirectoryName program.GoldenPath) |> ignore
                    File.WriteAllText (program.GoldenPath, goldenToJson info result.Cycles)
                    printfn "[golden] 保存: %s" program.GoldenPath
                    let finalLine =
                        result.FinalOutputs |> Map.toList |> List.map (fun (k, v) -> sprintf "%s=0x%X" k v) |> String.concat " "
                    printfn "[golden] 最終出力: %s" finalLine
                    match checkExpectations program result with
                    | [] ->
                        printfn "[golden] expect: %d 件すべて一致" (program.Expect.Count + program.ExpectMem.Count)
                        0
                    | mismatches ->
                        for m in mismatches do
                            eprintfn "MISMATCH: %s" m
                        1

let exitCode =
    match fsi.CommandLineArgs |> Array.toList |> List.tail with
    | [ programPath ] -> exportGolden programPath
    | _ ->
        eprintfn "使い方: dotnet fsi src/ExportGolden.fsx <program.json>"
        2

exit exitCode
