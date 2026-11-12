#r "bin/Release/net8.0/WwHdl.dll"
// ExportGoldenParts.fsx — プログラム JSON を NetlistSim で実行し、golden を分割して書き出す
//
// ExportGolden.fsx は全周期を 1 個の JSON にする (1 周期 約 400 バイト)。数千万周期 (Bootix のフル起動
// = 段階 2) では 9 GB を超え、F# も runner も 1 個の値として持てない。このスクリプトは周期ごとの
// 固定長レコード (dataIn u8 + irq u8 + 出力ポート u16 LE × ポート数) を --part-cycles 周期ずつの
// バイナリに分け、目次 (wwc-golden-parts/1) をプログラムの "golden" のパスに書く。
// runner は目次の format を見て分割 golden として読み、進行に合わせて 1 部分ずつ照合する。
//
// 使い方: dotnet fsi src/ExportGoldenParts.fsx <program.json> [--part-cycles N]
//   --part-cycles  1 部分の周期数 (既定 1,000,000 = sm83_full で 32 MB)
//   部分ファイルは目次と同じディレクトリの <目次名から .json を除いたもの>.partNNN.bin
//
// 前提: dotnet build src/WwHdl.fsproj -c Release (Debug の NetlistSim は数倍遅い)
//
// 終了コード: ExportGolden.fsx と同じ (0=出力 + expect 合格 / 1=expect 不一致・失敗 / 2=入力エラー /
//             3=routed meta が現在の verilog JSON と一致しない)
open System
open System.IO
open System.Security.Cryptography
open WwHdl
open WwHdl.RoutedArtifact
open WwHdl.NetlistSim
open WwHdl.Testbench

let repoRoot = Path.GetFullPath (Path.Combine (__SOURCE_DIRECTORY__, ".."))

/// 部分ファイルを順に書く。1 部分が埋まるたびに閉じて SHA-256 を記録する。
type PartWriter (manifestPath: string, partCycles: int) =
    let dir = Path.GetDirectoryName manifestPath
    let stem = Path.GetFileNameWithoutExtension manifestPath
    let mutable ports : string[] = [||]
    let mutable buffer : byte[] = [||]
    let mutable filled = 0
    let mutable parts : GoldenPart list = []

    member _.Ports = ports
    member _.Parts = List.rev parts

    member private _.Flush () =
        if filled > 0 then
            let size = goldenRecordSize ports.Length
            let name = sprintf "%s.part%03d.bin" stem (List.length parts)
            let bytes = ReadOnlySpan<byte> (buffer, 0, filled * size)
            let sha = (SHA256.HashData bytes |> Convert.ToHexString).ToLowerInvariant ()
            use f = File.Create (Path.Combine (dir, name))
            f.Write bytes
            parts <- { PartFile = name; PartCycles = filled; PartSha256 = sha } :: parts
            filled <- 0

    member this.Add (record: CycleRecord) =
        if buffer.Length = 0 then
            ports <- record.Outputs |> Array.map fst
            buffer <- Array.zeroCreate (goldenRecordSize ports.Length * partCycles)
        encodeGoldenRecord record buffer (filled * goldenRecordSize ports.Length)
        filled <- filled + 1
        if filled = partCycles then this.Flush ()

    member this.Finish () = this.Flush ()

let exportParts (programPath: string) (partCycles: int) : int =
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
        // 分割 golden は runner が meta の sourceSha256 と突き合わせるので、meta は必須にする
        let metaState =
            if not (File.Exists program.MetaPath) then Error (2, sprintf "meta %s がない" program.MetaPath)
            else
                match metaOfJson program.MetaPath (File.ReadAllText program.MetaPath) with
                | Error e -> Error (2, RoutedArtifact.describeError e)
                | Ok meta ->
                    match ensureFresh sourceSha meta with
                    | Ok _ -> Ok ()
                    | Error e -> Error (3, sprintf "STALE: %s — 配線し直してから golden を作る" (RoutedArtifact.describeError e))
        match metaState with
        | Error (code, msg) ->
            eprintfn "ERROR: %s" msg
            code
        | Ok () ->
            let json = Text.Encoding.UTF8.GetString sourceBytes
            let setup =
                match Pipeline.frontend json, parseYosysPorts json with
                | Error e, _ -> Error (sprintf "frontend: %A" e)
                | _, Error e -> Error (RoutedArtifact.describeError e)
                | Ok nl, Ok ports ->
                    compile nl
                    |> Result.mapError describeSimError
                    |> Result.bind (fun c ->
                        resolveBus ports
                        |> Result.bind (fun bus ->
                            loadRom program.Rom
                            |> Result.bind (fun rom ->
                                match program.BootRom with
                                | None -> Ok (c, ports, bus, rom, None)
                                | Some src -> loadRom src |> Result.map (fun bootRom -> c, ports, bus, rom, Some bootRom)))
                        |> Result.mapError describeTestbenchError)
            match setup with
            | Error msg ->
                eprintfn "ERROR: %s" msg
                2
            | Ok (c, ports, bus, rom, bootRom) ->
                let sw = Diagnostics.Stopwatch.StartNew ()
                let memory =
                    match bootRom with
                    | None -> createMemory rom program.Memory
                    | Some br -> createMemoryWithBootRom rom br program.Memory
                Directory.CreateDirectory (Path.GetDirectoryName program.GoldenPath) |> ignore
                let writer = PartWriter (program.GoldenPath, partCycles)
                // ブート ROM 解除 (0xFF50 への書込) がバスに出た最初の周期。周期数を決める目安に表示する
                let mutable unmapCycle = -1
                let sink (k: int) (record: CycleRecord) =
                    writer.Add record
                    if unmapCycle < 0 then
                        let value name = record.Outputs |> Array.tryFind (fun (n, _) -> n = name) |> Option.map snd
                        if value "mem_write" = Some 1UL && value "addr" = Some (uint64 BootRomDisableAddress) then
                            unmapCycle <- k
                    if (k + 1) % 1_000_000 = 0 then
                        printfn "[golden-parts] %d / %d 周期 (%.0fs)" (k + 1) program.Cycles sw.Elapsed.TotalSeconds
                match runStreaming sink c ports bus memory program.RstPulses program.Cycles with
                | Error e ->
                    eprintfn "ERROR: %s" (describeTestbenchError e)
                    1
                | Ok result ->
                    writer.Finish ()
                    printfn "[golden-parts] %s: %d 周期 (rst %d) を NetlistSim で実行 (%.1fs)"
                        program.ProgramName program.Cycles program.RstPulses sw.Elapsed.TotalSeconds
                    let info : GoldenInfo =
                        { GoldenCircuit = circuit
                          GoldenProgram = program.ProgramName
                          SourceSha256 = sourceSha
                          RomSha256 = sourceSha256 rom
                          BootRomSha256 = bootRom |> Option.map sourceSha256
                          RstPulses = program.RstPulses }
                    File.WriteAllText (program.GoldenPath, goldenPartsToJson info writer.Ports program.Cycles writer.Parts)
                    printfn "[golden-parts] 保存: %s (%d 部分、1 周期 %d バイト)"
                        program.GoldenPath writer.Parts.Length (goldenRecordSize writer.Ports.Length)
                    let finalLine =
                        result.FinalOutputs |> Map.toList |> List.map (fun (k, v) -> sprintf "%s=0x%X" k v) |> String.concat " "
                    printfn "[golden-parts] 最終出力: %s" finalLine
                    if unmapCycle >= 0 then
                        printfn "[golden-parts] 0xFF50 への書込が出力に現れた周期: %d (書込の実行は次の周期)" unmapCycle
                    printfn "[golden-parts] ブート ROM: %s" (if result.Memory.BootRomEnabled then "マップ中" else "解除済み")
                    match checkExpectations program result with
                    | [] ->
                        printfn "[golden-parts] expect: %d 件すべて一致" (program.Expect.Count + program.ExpectMem.Count)
                        0
                    | mismatches ->
                        for m in mismatches do
                            eprintfn "MISMATCH: %s" m
                        1

let exitCode =
    match fsi.CommandLineArgs |> Array.toList |> List.tail with
    | [ programPath ] -> exportParts programPath 1_000_000
    | [ programPath; "--part-cycles"; n ] when fst (Int32.TryParse n) && int n > 0 -> exportParts programPath (int n)
    | _ ->
        eprintfn "使い方: dotnet fsi src/ExportGoldenParts.fsx <program.json> [--part-cycles N]"
        2

exit exitCode
