#r "src/bin/Debug/net8.0/WwHdl.dll"
open WwHdl
open WwHdl.Domain
open WwHdl.PipelineWL
open System.IO

let json = File.ReadAllText "verilog/sm83_subset.json"
printfn "=== sm83_subset compileWL (debug) ==="
let sw = System.Diagnostics.Stopwatch.StartNew()
let result = compileWLWithPitch 48 32 json
sw.Stop()
match result with
| Error e -> printfn "FAIL at %dms: %A" sw.ElapsedMilliseconds e
| Ok (grid, placed, pins) -> printfn "OK in %dms, grid=%d" sw.ElapsedMilliseconds (Map.count grid)
