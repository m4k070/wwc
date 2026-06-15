#r "src/bin/Debug/net8.0/WwHdl.dll"

open System.IO
open WwHdl.PipelineWL

let jsonPath = "verilog/sm83_full.json"
if not (File.Exists jsonPath) then
    printfn "ERROR: %s not found" jsonPath
    exit 1

printfn "Loading %s..." jsonPath
let json = File.ReadAllText jsonPath

printfn "Compiling with compileWL (this may take a while)..."
let sw = System.Diagnostics.Stopwatch.StartNew()
match compileWL json with
| Error e ->
    sw.Stop()
    printfn "FAIL (%d ms): %A" sw.ElapsedMilliseconds e
    exit 1
| Ok (grid, placed, pins) ->
    sw.Stop()
    printfn "OK: compileWL took %d ms" sw.ElapsedMilliseconds
    printfn "  Gates: %d total" placed.Length
    printfn "  Grid cells: %d" (grid |> Map.count)
    printfn "  Pins: %d" (pins |> Map.count)
    printfn "\nSUCCESS"
