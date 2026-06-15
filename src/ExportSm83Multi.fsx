#r "bin/Debug/net8.0/WwHdl.dll"

open System
open System.IO
open WwHdl
open WwHdl.Domain
open WwHdl.Netlist
open WwHdl.WireLevel
open WwHdl.PipelineWL
open WwHdl.Pipeline

/// sm83_min マルチ命令 GPU ゴールデンテスト用エクスポート + レジスタ値検証
/// 4 命令 (NOP / LD_A #42 / LD_B #17 / ADD A,B) を順次実行し、
/// 各フェーズ (clk high/low) の .bin と register 値を検証する。
let exitCode =
    let jsonPath = Path.Combine (__SOURCE_DIRECTORY__, "..", "verilog", "sm83_min.json")
    if not (File.Exists jsonPath) then
        eprintfn "ERROR: %s not found" jsonPath
        1
    else
        let json = File.ReadAllText jsonPath
        let sw = Diagnostics.Stopwatch.StartNew()

        match compileWL json with
        | Error e ->
            eprintfn "COMPILE ERROR: %A" e
            1
        | Ok (grid, placed, pins) ->
            printfn "OK: grid=%d cells, placed=%d gates (%.1fs)" (Map.count grid) placed.Length sw.Elapsed.TotalSeconds

            let outDir =
                Path.Combine (__SOURCE_DIRECTORY__, "..", "web")
                |> Path.GetFullPath

            // --- ポート構成 ---
            let clkPin = pins.[NetId 2]
            let rstPin = pins.[NetId 3]
            let instPins = [| for i in 0..7 -> pins.[NetId (4 + i)] |]
            let aOutPins  = [| for i in 0..7 -> placed |> List.find (fun p -> p.Gate.Output = NetId (20 + i)) |> fun p -> p.Coord |]
            let bOutPins  = [| for i in 0..5 -> placed |> List.find (fun p -> p.Gate.Output = NetId (28 + i)) |> fun p -> p.Coord |]
            let pcOutPins = [| for i in 0..7 -> placed |> List.find (fun p -> p.Gate.Output = NetId (12 + i)) |> fun p -> p.Coord |]
            let flagPins  = [| for i in 0..3 -> placed |> List.find (fun p -> p.Gate.Output = NetId (34 + i)) |> fun p -> p.Coord |]

            let readReg8 (pins: Coord[]) (g: LGrid) : int =
                pins |> Array.mapi (fun i c -> if levelOf g c then 1 <<< i else 0) |> Array.sum

            let readReg6 (pins: Coord[]) (g: LGrid) : int =
                pins |> Array.mapi (fun i c -> if levelOf g c then 1 <<< i else 0) |> Array.sum

            let readFlags (pins: Coord[]) (g: LGrid) : int =
                pins |> Array.mapi (fun i c -> if levelOf g c then 1 <<< i else 0) |> Array.sum

            let setInst (v: int) (g: LGrid) =
                let mutable g = g
                for i in 0..7 do g <- setPin instPins.[i] ((v >>> i) &&& 1 = 1) g
                g

            let export (name: string) (g: LGrid) =
                let bin = exportGrid g
                let path = Path.Combine(outDir, name)
                File.WriteAllBytes(path, bin)
                printfn "  %s: %d bytes" name bin.Length

            let settlePhase (limit: int) (g: LGrid) =
                let sw2 = Diagnostics.Stopwatch.StartNew()
                let gSettled, t = settle limit g
                printfn "    settle: %d gen (%.1fs)" t sw2.Elapsed.TotalSeconds
                gSettled, t

            // --- リセット ---
            printfn "\n=== RESET ==="
            let afterRst, _ = grid |> setPin rstPin true |> setPin clkPin false |> settlePhase 2500
            printfn "  after rst: a=%d b=%d pc=%d flags=0x%X"
                (readReg8 aOutPins afterRst) (readReg6 bOutPins afterRst)
                (readReg8 pcOutPins afterRst) (readFlags flagPins afterRst)

            // --- rst 解除 ---
            let g0, _ = afterRst |> setPin rstPin false |> settlePhase 2500

            // --- 命令列 ---
            let program : (string * int * int * (int * int * int * int))[] = [|
                "nop", 0xC0, 0,  (0, 0, 1, 0x0)
                "lda", 0x2A, 42, (42, 0, 2, 0x0)
                "ldb", 0x51, 17, (42, 17, 3, 0x0)
                "add", 0x80, 0,  (59, 17, 4, 0x2)   // Z=0 N=0 H=1 C=0 → 0b0010 = 0x2
            |]

            let cases = System.Text.StringBuilder()
            cases.AppendLine "[" |> ignore
            let mutable first = true
            let mutable g = g0

            for (prefix, inst, imm, (expA, expB, expPc, expFlags)) in program do
                printfn "\n=== %s (inst=0x%02X) ===" prefix inst

                // inst 設定 (clk=0 のまま) + settle で組合せ論理を安定化
                g <- g |> setInst inst
                let gSetup, _ = settlePhase 2500 g

                let gHiInit = setPin clkPin true gSetup
                export (sprintf "sm83_mc_%s_high_init.bin" prefix) gHiInit

                let gHigh, tHigh = settlePhase 2500 gHiInit
                export (sprintf "sm83_mc_%s_high.bin" prefix) gHigh

                // レジスタ読み出し (High settle 後)
                let aVal = readReg8 aOutPins gHigh
                let bVal = readReg6 bOutPins gHigh
                let pcVal = readReg8 pcOutPins gHigh
                let flVal = readFlags flagPins gHigh
                printfn "    regs: a=%d b=%d pc=%d flags=0x%X" aVal bVal pcVal flVal

                // --- Low phase (clk=0 → settle) ---
                printfn "  LOW phase:"
                let gLoInit = setPin clkPin false gHigh
                export (sprintf "sm83_mc_%s_low_init.bin" prefix) gLoInit

                let gLow, tLow = settlePhase 2500 gLoInit
                export (sprintf "sm83_mc_%s_low.bin" prefix) gLow

                // レジスタ読み出し (Low settle 後)
                let aValLo = readReg8 aOutPins gLow
                let bValLo = readReg6 bOutPins gLow
                let pcValLo = readReg8 pcOutPins gLow
                let flValLo = readFlags flagPins gLow
                printfn "    regs: a=%d b=%d pc=%d flags=0x%X" aValLo bValLo pcValLo flValLo

                // レジスタ値検証 (posedge 後の High phase で正しい値を確認)
                let aOk = aVal = expA
                let bOk = bVal = expB
                let pcOk = pcVal = expPc
                let flOk = flVal = expFlags
                if not (aOk && bOk && pcOk && flOk) then
                    printfn "    *** REGISTER MISMATCH: expected a=%d b=%d pc=%d flags=0x%X ***" expA expB expPc expFlags
                else
                    printfn "    ✓ regs match expected"

                // golden-cases.json 追記
                if not first then cases.Append "," |> ignore
                first <- false
                cases.AppendLine (sprintf """  { "name": "sm83-mc-%s-high", "initFile": "sm83_mc_%s_high_init.bin", "steps": %d, "expectedFile": "sm83_mc_%s_high.bin", "exportScript": "src/ExportSm83Multi.fsx", "timeoutMs": 180000 }""" prefix prefix tHigh prefix) |> ignore
                cases.AppendLine "," |> ignore
                cases.Append (sprintf """  { "name": "sm83-mc-%s-low", "initFile": "sm83_mc_%s_low_init.bin", "steps": %d, "expectedFile": "sm83_mc_%s_low.bin", "exportScript": "src/ExportSm83Multi.fsx", "timeoutMs": 180000 }""" prefix prefix tLow prefix) |> ignore

                g <- gLow

            // --- メタデータ出力 (座標マッピング) ---
            let meta = {|
                regs = {|
                    a   = [| for c in aOutPins -> {| x = c.X; y = c.Y |} |]
                    b   = [| for c in bOutPins -> {| x = c.X; y = c.Y |} |]
                    pc  = [| for c in pcOutPins -> {| x = c.X; y = c.Y |} |]
                    flags = [| for c in flagPins -> {| x = c.X; y = c.Y |} |]
                |}
            |}
            let metaJson = System.Text.Json.JsonSerializer.Serialize(meta, System.Text.Json.JsonSerializerOptions(WriteIndented = true))
            File.WriteAllText(Path.Combine(outDir, "sm83_mc_reg_coords.json"), metaJson)
            printfn "\nsm83_mc_reg_coords.json written"

            cases.AppendLine ""
            cases.AppendLine "]"
            let mcJsonPath = Path.Combine(outDir, "golden-cases-mc-sm83_min.json")
            File.WriteAllText(mcJsonPath, cases.ToString())
            printfn "\n=== golden-cases-mc-sm83_min.json written ==="
            printfn "Total: %.1fs" sw.Elapsed.TotalSeconds
            0

exitCode
