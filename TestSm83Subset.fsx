// TestSm83Subset.fsx — Compile sm83_subset and verify it
#load "src/WwHdl.fs"
open WwHdl
open WwHdl.Domain
open WwHdl.PipelineWL
open System.IO

let jsonPath = "verilog/sm83_subset.json"
if not (File.Exists jsonPath) then
    eprintfn "ERROR: %s not found — run yosys first" jsonPath
    exit 1

printfn "=== sm83_subset compileWL ==="
let sw = System.Diagnostics.Stopwatch.StartNew()
match compileWL jsonPath with
| Error e ->
    printfn "compileWL FAILED: %A" e
    exit 1
| Ok (grid, info) ->
    sw.Stop()
    printfn "compileWL OK in %d ms" sw.ElapsedMilliseconds
    printfn "Grid size: %d cells" (grid |> Map.count)
    match info with
    | :? WireLevelCompileInfo as wl ->
        printfn "Gates: %d" wl.Gates.Length
        printfn "Placed: %d" wl.Placed.Length
        printfn "Pins: %d" wl.Pins.Count
        printfn "Wires: %d" wl.WireLen
        printfn "Max explore: %d" wl.MaxExplore
    | _ -> ()
