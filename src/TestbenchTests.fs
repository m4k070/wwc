namespace WwHdl

// ---------------------------------------------------------------------
// Testbench — メモリバス TB (TODO Step B-2)
//     sm83_subset smoke を GPU トレース (2026-09-16) と、sm83_full を SM83 の仕様と照合する
// ---------------------------------------------------------------------
module TestbenchTest =
    open System.IO
    open System.Text.Json
    open RoutedArtifact
    open NetlistSim
    open Testbench

    let private repoPath (relative: string) = Path.GetFullPath (Path.Combine (__SOURCE_DIRECTORY__, "..", relative))

    let private loadCircuit (name: string) : Result<CompiledNetlist * YosysPortBits list, string> =
        let json = File.ReadAllText (repoPath (sprintf "verilog/%s.json" name))
        match Pipeline.frontend json, parseYosysPorts json with
        | Error e, _ -> Error (sprintf "frontend: %A" e)
        | _, Error e -> Error (RoutedArtifact.describeError e)
        | Ok nl, Ok ports -> compile nl |> Result.mapError describeSimError |> Result.map (fun c -> c, ports)

    let private memoryTests () : (string * bool) list =
        let rom = Array.init 0x200 (fun i -> byte (i &&& 0xFF))
        let m = createMemory rom defaultMemoryConfig
        let written = writeMemory (writeMemory m 0xC010us 0x5Auy) 0x0010us 0x99uy
        [ "TB: memory reads ROM, RAM (initially 0), and 0xFF elsewhere",
          readMemory m 0x0123us = 0x23uy && readMemory m 0xC000us = 0uy && readMemory m 0x8000us = 0xFFuy
          "TB: memory writes only the RAM window (ROM write ignored, original unchanged)",
          readMemory written 0xC010us = 0x5Auy && readMemory written 0x0010us = 0x10uy && readMemory m 0xC010us = 0uy
          "TB: memory I/O holds IF (0xFF0F), IE (0xFFFF), and HRAM (0xFF80-0xFFFE)",
          (let io = writeMemory (writeMemory (writeMemory m 0xFF0Fus 0x06uy) 0xFFFFus 0x05uy) 0xFF90us 0x77uy
           readMemory io 0xFF0Fus = 0x06uy && readMemory io 0xFFFFus = 0x05uy && readMemory io 0xFF90us = 0x77uy
           && readMemory io 0xFF10us = 0xFFuy)
          "TB: pending interrupts are IE & IF & 0x1F, and ack clears only the acknowledged bit",
          (let io = writeMemory (writeMemory m 0xFF0Fus 0xE6uy) 0xFFFFus 0x07uy
           pendingInterrupts io = 0x06uy && (acknowledgeInterrupts io 0x02uy).InterruptFlag = 0xE4uy)
          "TB: RAM window overlapping I/O takes precedence (sm83_subset_call_stack uses 0xF000-0xFFFF)",
          (let overlap = createMemory rom { RamBase = 0xF000; RamSize = 4096 }
           let w = writeMemory overlap 0xFF0Fus 0x1Fuy
           readMemory w 0xFF0Fus = 0x1Fuy && w.InterruptFlag = 0uy) ]

    let private parseTests () : (string * bool) list =
        let smokePath = repoPath "routed/sm83_subset_smoke.json"
        let smoke = parseProgram smokePath (File.ReadAllText smokePath)
        let smokeOk =
            match smoke with
            | Ok p ->
                p.Cycles = 3 && p.RstPulses = 2
                && p.Expect = Map.ofList [ "a_out", 66UL; "pc_out", 258UL ]
                && p.Memory = { RamBase = 49152; RamSize = 8192 }
                && p.Rom = RomFile (repoPath "routed/rom_sm83_subset_smoke.bin")
                && p.MetaPath = repoPath "routed/sm83_subset.meta.json"
            | Error _ -> false
        let inlineJson =
            """{"meta":"m.json","init":"i.bin","memory":{"rom":"base64:PkI="},"cycles":5,
                "expectMem":{"0xC000":42,"49153":7}}"""
        let inlineOk =
            match parseProgram "/tmp/p/inline.json" inlineJson with
            | Ok p ->
                p.Rom = RomBase64 "PkI=" && p.RstPulses = 2 && p.Memory = defaultMemoryConfig
                && p.ExpectMem = Map.ofList [ 0xC000us, 42uy; 0xC001us, 7uy ]
                && p.GoldenPath = "/tmp/p/inline.golden.json"
                && loadRom p.Rom = Ok [| 0x3Euy; 0x42uy |]
            | Error _ -> false
        let badAddress =
            match parseProgram "/tmp/p/bad.json" """{"meta":"m","init":"i","memory":{"rom":"r"},"cycles":1,"expectMem":{"C000":1}}""" with
            | Error (ProgramParseFailed (_, reason)) -> reason.Contains "C000"
            | _ -> false
        [ "TB: parses the sm83_subset smoke program (paths resolved from the program dir)", smokeOk
          "TB: parses defaults, base64 ROM, and decimal/hex expectMem", inlineOk
          "TB: rejects an expectMem address without 0x prefix (same as runner)", badAddress ]

    let private busTests () : (string * bool) list =
        match loadCircuit "counter4" with
        | Error msg -> [ sprintf "TB: counter4 loads (%s)" msg, false ]
        | Ok (_, ports) ->
            [ "TB: resolveBus reports the first missing bus port",
              (match resolveBus ports with Error (MissingBusPort "rst") -> true | _ -> false) ]

    let private subsetSmokeTest () : (string * bool) list =
        // GPU smoke (2026-09-16, RTX 3060) のトレースのうち、golden と同じ時点で取った値:
        // (data_in, pc_out, a_out)。GPU トレースの addr は clk=0 settle 時点 (メモリ読出に使った番地) で、
        // clk=1 settle 後の出力を記録する golden とは時点が違うので比べない。
        // モジュール最上位の値にするとファイル全体の静的初期化が走るので関数内に置く
        let gpuSmokeTrace =
            [ 0x3EUL, 0x0101UL, 0x01UL
              0x42UL, 0x0101UL, 0x42UL
              0x00UL, 0x0102UL, 0x42UL ]
        let smokePath = repoPath "routed/sm83_subset_smoke.json"
        let outcome =
            parseProgram smokePath (File.ReadAllText smokePath)
            |> Result.mapError describeTestbenchError
            |> Result.bind (fun program ->
                loadCircuit "sm83_subset"
                |> Result.bind (fun (c, ports) ->
                    resolveBus ports
                    |> Result.bind (fun bus ->
                        loadRom program.Rom
                        |> Result.bind (fun rom -> run c ports bus (createMemory rom program.Memory) program.RstPulses program.Cycles))
                    |> Result.mapError describeTestbenchError)
                |> Result.map (fun result -> program, result))
        match outcome with
        | Error msg -> [ sprintf "TB: sm83_subset smoke runs (%s)" msg, false ]
        | Ok (program, result) ->
            let observed =
                result.Cycles
                |> List.map (fun cy -> cy.DataIn, cy.Outputs.["pc_out"], cy.Outputs.["a_out"])
            let mismatches = checkExpectations program result
            for m in mismatches do
                printfn "  TB_SMOKE: %s" m
            if observed <> gpuSmokeTrace then
                printfn "  TB_SMOKE: trace (data_in, pc, a) expected %A" gpuSmokeTrace
                printfn "  TB_SMOKE: trace (data_in, pc, a) observed %A" observed
            [ "TB: sm83_subset smoke matches the GPU trace cycle by cycle (data_in/pc/a)", observed = gpuSmokeTrace
              "TB: sm83_subset smoke expectations pass on NetlistSim", mismatches.IsEmpty ]

    /// routed/sm83_full_*.json の仕様テスト (期待値は SM83 仕様から手で導いたもの) を NetlistSim で実行する。
    /// RTL を直したら再合成してこのテストを回す。1 プログラム = 1 テスト項目
    let private fullSpecProgramTests () : (string * bool) list =
        let programPaths =
            Directory.GetFiles (repoPath "routed", "sm83_full_*.json")
            |> Array.filter (fun p -> not (p.EndsWith ".golden.json"))
            |> Array.sort
            |> List.ofArray
        match loadCircuit "sm83_full" with
        | Error msg -> [ sprintf "TB: sm83_full loads (%s)" msg, false ]
        | Ok _ when programPaths.IsEmpty -> [ "TB: routed/sm83_full_*.json spec programs present", false ]
        | Ok (c, ports) ->
            match resolveBus ports with
            | Error e -> [ sprintf "TB: sm83_full bus resolves (%s)" (describeTestbenchError e), false ]
            | Ok bus ->
                [ for path in programPaths do
                    let name = Path.GetFileNameWithoutExtension path
                    let outcome =
                        parseProgram path (File.ReadAllText path)
                        |> Result.bind (fun program ->
                            loadRom program.Rom
                            |> Result.bind (fun rom ->
                                run c ports bus (createMemory rom program.Memory) program.RstPulses program.Cycles)
                            |> Result.map (fun result -> checkExpectations program result, program.Expect.Count + program.ExpectMem.Count))
                    match outcome with
                    | Error e -> yield sprintf "TB-SPEC: %s runs (%s)" name (describeTestbenchError e), false
                    | Ok (mismatches, checks) ->
                        for m in mismatches do
                            printfn "  TB_SPEC %s: %s" name m
                        yield sprintf "TB-SPEC: %s matches SM83 spec (%d checks)" name checks, mismatches.IsEmpty ]

    /// DAA (0x27) の Pan Docs 仕様どおりの参照実装。RTL (sm83_full.v) の模倣ではなく、
    /// 仕様書 (加算後: C||A>0x99 で +0x60、H||下位ニブル>0x09 で +0x06。減算後: H で -0x06、C で -0x60。
    /// いずれも判定は補正前の元の A で行う) から独立に書く
    let private daaReference (a: int) (f: int) : int * int =
        let n = (f &&& 0x40) <> 0
        let h = (f &&& 0x20) <> 0
        let c = (f &&& 0x10) <> 0
        let mutable result = a
        let mutable newC = c
        if n then
            if h then result <- (result - 0x06) &&& 0xFF
            if c then result <- (result - 0x60) &&& 0xFF
        else
            if h || (a &&& 0x0F) > 0x09 then result <- (result + 0x06) &&& 0xFF
            if c || a > 0x99 then
                result <- (result + 0x60) &&& 0xFF
                newC <- true
        let z = result = 0
        let newF = (if z then 0x80 else 0) ||| (if n then 0x40 else 0) ||| (if newC then 0x10 else 0)
        result, newF

    /// DAA の全数テスト (A 256 通り × F 上位ニブル 16 通り = 4096 通り)。
    /// 各ケースを個別の短いプログラム (LD BC,a:f; PUSH BC; POP AF; DAA; HALT) として NetlistSim で実行し、
    /// daaReference と照合する。RTL の DAA 不具合 (2026-09-27 修正) の回帰テスト
    let private daaExhaustiveTest () : (string * bool) list =
        match loadCircuit "sm83_full" with
        | Error msg -> [ sprintf "TB: sm83_full loads for DAA exhaustive (%s)" msg, false ]
        | Ok (c, ports) ->
            match resolveBus ports with
            | Error e -> [ sprintf "TB: sm83_full bus resolves for DAA exhaustive (%s)" (describeTestbenchError e), false ]
            | Ok bus ->
                let sp = 0xDFFE
                let buildRom (a: int) (f: int) : byte[] =
                    let rom = Array.create 0x8000 0x76uy
                    let code =
                        [| 0x31uy; byte (sp &&& 0xFF); byte (sp >>> 8)   // LD SP,sp
                           0x01uy; byte f; byte a                        // LD BC,a:f (B=a, C=f)
                           0xC5uy                                        // PUSH BC
                           0xF1uy                                        // POP AF (A=a, F=f)
                           0x27uy                                        // DAA
                           0x76uy |]                                     // HALT
                    Array.blit code 0 rom 0x0100 code.Length
                    rom
                let cycles = 60
                let mismatches =
                    [ for a in 0 .. 255 do
                        for fHi in 0 .. 15 do
                            let f = fHi <<< 4
                            let expectedA, expectedF = daaReference a f
                            let rom = buildRom a f
                            match run c ports bus (createMemory rom defaultMemoryConfig) 2 cycles with
                            | Error e -> yield sprintf "A=%02X F=%02X: netlist error %s" a f (describeTestbenchError e)
                            | Ok result ->
                                let gotA = int result.FinalOutputs.["a_out"]
                                let gotF = int result.FinalOutputs.["f_out"]
                                if gotA <> expectedA || gotF <> expectedF then
                                    yield sprintf "A=%02X F=%02X: expected A=%02X F=%02X, got A=%02X F=%02X" a f expectedA expectedF gotA gotF ]
                for m in mismatches |> List.truncate 20 do
                    printfn "  TB_DAA: %s" m
                if mismatches.Length > 20 then
                    printfn "  TB_DAA: ... ほか %d 件" (mismatches.Length - 20)
                [ "TB: DAA 全数一致 (A 256 × F 上位ニブル 16 = 4096 通り、Pan Docs 参照関数と照合)", mismatches.IsEmpty ]

    let private goldenJsonTest () : (string * bool) list =
        let info : GoldenInfo =
            { GoldenCircuit = "c"; GoldenProgram = "p"; SourceSha256 = "s"; RomSha256 = "r"; RstPulses = 2 }
        let cycles = [ { DataIn = 62UL; Irq = 0UL; Outputs = Map.ofList [ "addr", 256UL; "a_out", 1UL ] } ]
        use doc = JsonDocument.Parse (goldenToJson info cycles)
        let root = doc.RootElement
        let first = root.GetProperty("cycles").[0]
        [ "TB: golden JSON has format, hashes, and per-cycle dataIn/outputs",
          root.GetProperty("format").GetString () = GoldenFormat
          && root.GetProperty("romSha256").GetString () = "r"
          && root.GetProperty("cycles").GetArrayLength () = 1
          && first.GetProperty("dataIn").GetUInt64 () = 62UL
          && first.GetProperty("outputs").GetProperty("addr").GetUInt64 () = 256UL ]

    let runAll () : (string * bool) list =
        memoryTests () @ parseTests () @ busTests () @ subsetSmokeTest () @ goldenJsonTest () @ fullSpecProgramTests ()
        @ daaExhaustiveTest ()
