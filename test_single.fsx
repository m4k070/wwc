#r "src/bin/Debug/net8.0/WwHdl.dll"
open WwHdl
open WwHdl.Domain
open WwHdl.WireLevel
open WwHdl.PipelineWL
open System.IO

let sw = System.Diagnostics.Stopwatch()
let name = fsi.CommandLineArgs |> Array.tryItem 1 |> Option.defaultValue "sm83_core"

let src = File.ReadAllText (sprintf "verilog/%s.json" name)
printfn "Compiling %s ..." name
sw.Start()
match compileWL src with
| Error e ->
    sw.Stop()
    printfn "FAIL: %A (%d ms)" e sw.ElapsedMilliseconds
| Ok (grid, placed, pins) ->
    sw.Stop()
    let gateCount = placed.Length
    let wc = ref 0
    let cc = ref 0
    let pc = ref 0
    let nc = ref 0
    let dc = ref 0
    for kv in grid do
        match kv.Value with
        | LWire _ -> wc.Value <- wc.Value + 1
        | Cross _ -> cc.Value <- cc.Value + 1
        | Pin _ -> pc.Value <- pc.Value + 1
        | LNand _ -> nc.Value <- nc.Value + 1
        | LDff _ -> dc.Value <- dc.Value + 1
        | _ -> ()
    let total = wc.Value + cc.Value + pc.Value + nc.Value + dc.Value
    let gridW = (grid |> Seq.map (fun kv -> kv.Key.X) |> Seq.max) + 1
    let gridH = (grid |> Seq.map (fun kv -> kv.Key.Y) |> Seq.max) + 1
    let cr = if wc.Value + cc.Value > 0 then float cc.Value / float (wc.Value + cc.Value) * 100.0 else 0.0
    printfn "OK %s [24x16]: %d cells (gates=%d nand=%d dff=%d wire=%d cross=%d pin=%d) grid=%dx%d cross=%.1f%% %dms"
        name total gateCount nc.Value dc.Value wc.Value cc.Value pc.Value gridW gridH cr sw.ElapsedMilliseconds
