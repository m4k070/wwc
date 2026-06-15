#r "src/bin/Debug/net8.0/WwHdl.dll"
open WwHdl
open WwHdl.Domain
open WwHdl.PipelineWL
open System.IO

let json = File.ReadAllText "verilog/sm83_subset.json"
for (px, py) in [48,32; 64,48; 96,64] do
  printfn "\n=== pitch %d x %d ===" px py
  let sw = System.Diagnostics.Stopwatch.StartNew()
  match compileWLWithPitch px py json with
  | Error e ->
      sw.Stop()
      printfn "  FAIL at %dms: %A" sw.ElapsedMilliseconds e
  | Ok (grid, placed, pins) ->
      sw.Stop()
      printfn "  OK in %dms, grid=%d cells, %d gates" sw.ElapsedMilliseconds (Map.count grid) placed.Length
