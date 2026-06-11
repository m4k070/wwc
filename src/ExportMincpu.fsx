#r "bin/Debug/net8.0/WwHdl.dll"
// mincpu をコンパイル → grid.bin にエクスポート (GPU ゴールデンテスト用)
open System.IO
open WwHdl.Domain
open WwHdl.Netlist
open WwHdl.WireLevel
open WwHdl.PipelineWL

let outDir = Path.Combine(__SOURCE_DIRECTORY__, "..", "web")
Directory.CreateDirectory(outDir) |> ignore

let json = File.ReadAllText (Path.Combine(__SOURCE_DIRECTORY__, "..", "verilog/mincpu.json"))

match compileWL json with
| Error e -> eprintfn "mincpu compile error: %A" e
| Ok (grid, placed, pins) ->
    printfn "Compiled: %d cells, %d gates" (Map.count grid) placed.Length
    let clkPin = pins.[NetId 2]

    // 初期グリッド (clk=0, all DFFs=0, wires=0)
    let initBin = exportGrid grid
    File.WriteAllBytes(Path.Combine(outDir, "mincpu_init.bin"), initBin)
    printfn "mincpu_init.bin: %d bytes (%d cells)" initBin.Length (Map.count grid)

    // clk=1 設定直後 (未収束) — GPU シミュレーションの初期状態
    let g1init = setPin clkPin true grid
    let g1initBin = exportGrid g1init
    File.WriteAllBytes(Path.Combine(outDir, "mincpu_clk1_init.bin"), g1initBin)
    printfn "mincpu_clk1_init.bin: %d bytes" g1initBin.Length

    // clk=1 で 3500 世代 settle (収束) — GPU の期待値 (cyc1 high)
    let g1, t1 = settle 3500 (setPin clkPin true grid)
    let g1Bin = exportGrid g1
    File.WriteAllBytes(Path.Combine(outDir, "mincpu_clk1.bin"), g1Bin)
    printfn "mincpu_clk1.bin: %d bytes (settle %d gen)" g1Bin.Length t1

    // clk=0 設定直後 (未収束)
    let g0init = setPin clkPin false g1
    File.WriteAllBytes(Path.Combine(outDir, "mincpu_clk0_init.bin"), exportGrid g0init)
    printfn "mincpu_clk0_init.bin: %d bytes" (exportGrid g0init).Length

    // clk=0 で 800 世代 settle (収束) — GPU の期待値 (cyc1 low)
    let g0, t0 = settle 800 g0init
    let g0Bin = exportGrid g0
    File.WriteAllBytes(Path.Combine(outDir, "mincpu_clk0.bin"), g0Bin)
    printfn "mincpu_clk0.bin: %d bytes (settle %d gen)" g0Bin.Length t0

    printfn "\nDone. Files exported to %s" outDir
