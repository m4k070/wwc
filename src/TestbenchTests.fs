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
          readMemory written 0xC010us = 0x5Auy && readMemory written 0x0010us = 0x10uy && readMemory m 0xC010us = 0uy ]

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

    /// SM83 の仕様から決めた期待値: LD HL,0xC000; LD A,0x42; LD (HL),A; HALT
    let private fullSemanticTest () : (string * bool) list =
        let rom = Array.append (Array.zeroCreate 0x100) [| 0x21uy; 0x00uy; 0xC0uy; 0x3Euy; 0x42uy; 0x77uy; 0x76uy |]
        let program : MemoryProgram =
            { ProgramName = "sm83_full_ld_mem"
              Circuit = Some "sm83_full"
              MetaPath = ""
              InitPath = ""
              Rom = RomBase64 ""
              Memory = defaultMemoryConfig
              RstPulses = 2
              Cycles = 20
              Expect = Map.ofList [ "a_out", 0x42UL; "h_out", 0xC0UL; "l_out", 0x00UL; "pc_out", 0x0107UL ]
              ExpectMem = Map.ofList [ 0xC000us, 0x42uy ]
              GoldenPath = "" }
        let outcome =
            loadCircuit "sm83_full"
            |> Result.bind (fun (c, ports) ->
                resolveBus ports
                |> Result.bind (fun bus -> run c ports bus (createMemory rom program.Memory) program.RstPulses program.Cycles)
                |> Result.mapError describeTestbenchError)
        match outcome with
        | Error msg -> [ sprintf "TB: sm83_full LD/HALT program runs (%s)" msg, false ]
        | Ok result ->
            let mismatches = checkExpectations program result
            for m in mismatches do
                printfn "  TB_FULL: %s" m
            [ "TB: sm83_full executes LD HL,nn / LD A,n / LD (HL),A / HALT per SM83 spec (a, HL, pc, RAM)", mismatches.IsEmpty ]

    let private goldenJsonTest () : (string * bool) list =
        let info : GoldenInfo =
            { GoldenCircuit = "c"; GoldenProgram = "p"; SourceSha256 = "s"; RomSha256 = "r"; RstPulses = 2 }
        let cycles = [ { DataIn = 62UL; Outputs = Map.ofList [ "addr", 256UL; "a_out", 1UL ] } ]
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
        memoryTests () @ parseTests () @ busTests () @ subsetSmokeTest () @ fullSemanticTest () @ goldenJsonTest ()
