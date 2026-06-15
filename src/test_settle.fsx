#r "bin/Debug/net8.0/WwHdl.dll"
open WwHdl
open WwHdl.Domain
open WwHdl.Netlist
open WwHdl.WireLevel
open WwHdl.PipelineWL

let json = System.IO.File.ReadAllText "verilog/sm83_min.json"
match compileWL json with
| Error e -> printfn "Error: %A" e
| Ok (grid, placed, pins) ->
    printfn "Compiled OK: %d cells" (Map.count grid)
    let clkPin = pins.[NetId 2]
    printfn "clk at (%d,%d)" clkPin.X clkPin.Y
    let watch = System.Diagnostics.Stopwatch.StartNew()
    let g, n = settle 100 grid
    watch.Stop()
    printfn "Settled in %d generations (%d ms)" n watch.ElapsedMilliseconds
