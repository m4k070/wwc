#r "bin/Debug/net8.0/WwHdl.dll"

open WwHdl
open WwHdl.Domain
open WwHdl.Netlist
open WwHdl.WireLevel
open WwHdl.PipelineWL
open WwHdl.Pipeline

/// SM83 P0 マルチサイクル GPU ゴールデンテスト用エクスポート
/// 複数の命令を順次実行し、各フェーズ (clk high/low) の .bin を出力する。
let json = System.IO.File.ReadAllText "verilog/sm83_p0.json"

let sw = System.Diagnostics.Stopwatch.StartNew()

match compileWL json with
| Error e -> eprintfn "SM83 P0 compile error: %A" e
| Ok (grid, placed, pins) ->
    printfn "OK: grid=%d cells, placed=%d gates (%.1fs)" (Map.count grid) placed.Length sw.Elapsed.TotalSeconds

    let outDir =
        System.IO.Path.Combine(__SOURCE_DIRECTORY__, "..", "web")
        |> System.IO.Path.GetFullPath

    let clkPin = pins.[NetId 2]
    let rstPin = pins.[NetId 3]
    let instPins = [| for i in 0..7 -> pins.[NetId (4 + i)] |]
    let dataInPins = [| for i in 0..7 -> pins.[NetId (12 + i)] |]

    let setInst (v: int) (g: LGrid) =
        let mutable g = g
        for i in 0..7 do g <- setPin instPins.[i] ((v >>> i) &&& 1 = 1) g
        g

    let setDataIn (v: int) (g: LGrid) =
        let mutable g = g
        for i in 0..7 do g <- setPin dataInPins.[i] ((v >>> i) &&& 1 = 1) g
        g

    let export (name: string) (g: LGrid) =
        let bin = exportGrid g
        let path = System.IO.Path.Combine(outDir, name)
        System.IO.File.WriteAllBytes(path, bin)
        printfn "  %s: %d bytes" name bin.Length

    // 1 命令の 1 フェーズ (clk high または low) を実行する。
    // g: 現在のグリッド (clk pin はまだ変更前)
    // clk: true=high(1), false=low(0)
    // 戻り値: (initグリッド, 収束グリッド, 経過世代数)
    let runPhase (g: LGrid) (clk: bool) (prefix: string) (label: string) =
        let gInit = setPin clkPin clk g
        export (sprintf "sm83p0_mc_%s_%s_init.bin" prefix label) gInit
        let stepSw = System.Diagnostics.Stopwatch.StartNew()
        let gSettled, t = settle 2500 gInit
        printfn "  %s: settled %d gen (%.1fs)" label t stepSw.Elapsed.TotalSeconds
        export (sprintf "sm83p0_mc_%s_%s.bin" prefix label) gSettled
        gSettled, t

    // すべての命令の F# runAll を呼び出す。
    // 各 (prefix, inst, data_in, 説明) を順次実行する。
    let program = [|
        "nop",  0xE0, 0,   "NOP"
        "lda",  0x00, 42,  "LD A,#42"
        "ldb",  0x20, 17,  "LD B,#17"
        "add",  0x42, 0,   "ADD A,B (42+17=59)"
    |]

    // 全フェーズのメタデータを JSON で収集する (golden-cases.json 生成用)
    let cases = System.Text.StringBuilder()
    cases.AppendLine "["
    |> ignore

    let mutable g = grid
    let mutable first = true

    for (prefix, inst, dataIn, desc) in program do
        printfn "\n=== %s: %s ===" prefix desc

        // clk=0 のまま instruction を設定
        g <- g |> setInst inst |> setDataIn dataIn

        // High phase
        let gHigh, tHigh = runPhase g true prefix "high"

        // Low phase
        let gLow, tLow = runPhase gHigh false prefix "low"

        if not first then
            cases.Append "," |> ignore
        first <- false

        cases.AppendLine (sprintf """  { "name": "sm83p0-mc-%s-high", "initFile": "sm83p0_mc_%s_high_init.bin", "steps": %d, "expectedFile": "sm83p0_mc_%s_high.bin", "exportScript": "src/ExportSm83P0Multi.fsx", "timeoutMs": 600000 }""" prefix prefix tHigh prefix) |> ignore
        cases.AppendLine "," |> ignore
        cases.Append (sprintf """  { "name": "sm83p0-mc-%s-low", "initFile": "sm83p0_mc_%s_low_init.bin", "steps": %d, "expectedFile": "sm83p0_mc_%s_low.bin", "exportScript": "src/ExportSm83P0Multi.fsx", "timeoutMs": 600000 }""" prefix prefix tLow prefix) |> ignore

        g <- gLow

    cases.AppendLine ""
    cases.AppendLine "]"

    let jsonPath = System.IO.Path.Combine(outDir, "golden-cases-mc.json")
    System.IO.File.WriteAllText(jsonPath, cases.ToString())
    printfn "\n=== golden-cases-mc.json written ==="
    printfn "Total: %.1fs" sw.Elapsed.TotalSeconds
