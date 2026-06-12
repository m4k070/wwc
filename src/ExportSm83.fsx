#r "bin/Debug/net8.0/WwHdl.dll"

open WwHdl
open WwHdl.Domain
open WwHdl.Netlist
open WwHdl.WireLevel
open WwHdl.PipelineWL
open WwHdl.Pipeline

// SM83 CPU グリッドを .bin にエクスポート (GPU ゴールデンテスト用)
// パターン: init = ピン設定直後 (未収束), expected = settle 後 (収束状態)
// GPU テスト: init → N 世代ステップ → expected と byte一致
let json = System.IO.File.ReadAllText "verilog/sm83_min.json"

match compileWL json with
| Error e -> eprintfn "SM83 compile error: %A" e
| Ok (grid, placed, pins) ->
    printfn "OK: grid=%d cells, placed=%d gates" (Map.count grid) placed.Length

    let outDir =
        System.IO.Path.Combine(__SOURCE_DIRECTORY__, "..", "web")
        |> System.IO.Path.GetFullPath

    // 初期状態 (clk=0, rst=0, inst=0)
    let initBin = exportGrid grid
    System.IO.File.WriteAllBytes(System.IO.Path.Combine(outDir, "sm83_init.bin"), initBin)
    printfn "sm83_init.bin: %d bytes (%d cells)" initBin.Length (Map.count grid)

    let clkPin = pins.[NetId 2]
    let rstPin = pins.[NetId 3]
    let instPins = [| for i in 0..7 -> pins.[NetId (4 + i)] |]

    let setInst (v: int) (g: LGrid) =
        let mutable g = g
        for i in 0..7 do
            g <- setPin instPins.[i] ((v >>> i) &&& 1 = 1) g
        g

    // 全テストケース共通: rst=0, inst=NOP(0xC0)
    let grid0 = grid |> setInst 0xC0 |> setPin rstPin false

    // --- cyc0 high phase (clk=0 → clk=1) ---
    let gHiInit = setPin clkPin true grid0
    let gHiInitBin = exportGrid gHiInit
    System.IO.File.WriteAllBytes(System.IO.Path.Combine(outDir, "sm83_cyc0_high_init.bin"), gHiInitBin)
    printfn "sm83_cyc0_high_init.bin: %d bytes (clk=1, NOT settled)" gHiInitBin.Length

    let gHigh, tHigh = settle 2000 gHiInit
    let gHighBin = exportGrid gHigh
    System.IO.File.WriteAllBytes(System.IO.Path.Combine(outDir, "sm83_cyc0_high.bin"), gHighBin)
    printfn "sm83_cyc0_high.bin: %d bytes (settle %d gen)" gHighBin.Length tHigh

    // --- cyc0 low phase (clk=1 → clk=0) ---
    let gLoInit = setPin clkPin false gHigh
    let gLoInitBin = exportGrid gLoInit
    System.IO.File.WriteAllBytes(System.IO.Path.Combine(outDir, "sm83_cyc0_low_init.bin"), gLoInitBin)
    printfn "sm83_cyc0_low_init.bin: %d bytes (clk=0, NOT settled)" gLoInitBin.Length

    let gLow, tLow = settle 2000 gLoInit
    let gLowBin = exportGrid gLow
    System.IO.File.WriteAllBytes(System.IO.Path.Combine(outDir, "sm83_cyc0_low.bin"), gLowBin)
    printfn "sm83_cyc0_low.bin: %d bytes (settle %d gen)" gLowBin.Length tLow

    printfn "\nDone. Run: web/run-wl.sh sm83"
