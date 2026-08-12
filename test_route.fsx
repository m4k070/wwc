#r "src/bin/Debug/net8.0/WwHdl.dll"
open WwHdl
open WwHdl.Domain
open WwHdl.WireLevel
open WwHdl.PipelineWL
open System.IO

let sw = System.Diagnostics.Stopwatch()
let name = fsi.CommandLineArgs |> Array.tryItem 1 |> Option.defaultValue "sm83_core"

let src = File.ReadAllText (sprintf "verilog/%s.json" name)
sw.Start()
match compileWL src with
| Error e -> printfn "FAIL %s %A (%d ms)" name e sw.ElapsedMilliseconds
| Ok (grid, placed, pins) ->
    sw.Stop()
    let gateCells = placed |> List.length
    let wc, cc, pc, nc, dc = ref 0, ref 0, ref 0, ref 0, ref 0
    for kv in grid do
        match kv.Value with LWire _ -> incr wc | Cross _ -> incr cc | Pin _ -> incr pc | LNand _ -> incr nc | LDff _ -> incr dc | _ -> ()
    let total = wc.Value + cc.Value + pc.Value + nc.Value + dc.Value
    let gridW = (grid |> Seq.map (fun kv -> kv.Key.X) |> Seq.max)+1
    let gridH = (grid |> Seq.map (fun kv -> kv.Key.Y) |> Seq.max)+1
    printfn "OK %s %d cells (nand=%d dff=%d wire=%d cross=%d pin=%d) grid=%dx%d crossRatio=%.1f%% %dms"
        name total nc.Value dc.Value wc.Value cc.Value pc.Value gridW gridH
        (if wc.Value+cc.Value>0 then float cc.Value/float(wc.Value+cc.Value)*100.0 else 0.0)
        sw.ElapsedMilliseconds
