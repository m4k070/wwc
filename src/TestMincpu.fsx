#r "bin/Debug/net8.0/WwHdl.dll"

open WwHdl
open WwHdl.Domain
open WwHdl.Netlist
open WwHdl.WireLevel
open WwHdl.PipelineWL
open WwHdl.Pipeline

let json = System.IO.File.ReadAllText "verilog/mincpu.json"

let sw = System.Diagnostics.Stopwatch.StartNew()
match compileWL json with
| Error e -> printfn "COMPILE ERROR: %A" e
| Ok (grid, placed, pins) ->
    printfn "OK: grid=%d cells, placed=%d gates (%.1fs)" (Map.count grid) placed.Length sw.Elapsed.TotalSeconds
    let outOf n =
        placed |> List.find (fun p -> p.Gate.Output = NetId n) |> fun p -> p.Coord
    let value g =
        (match Pipeline.parseYosysJson json with
         | Ok m -> m.Ports.["out"].Bits
         | _ -> [])
        |> List.mapi (fun i n -> if levelOf g (outOf n) then 1 <<< i else 0)
        |> List.sum
    let clkPin = pins.[NetId 2]
    let limit = 5000

    sw.Restart()
    let g0, t0 = settle limit grid
    printfn "init: %d gen v=%d (%.1fs)" t0 (value g0) sw.Elapsed.TotalSeconds

    let totalSw = System.Diagnostics.Stopwatch.StartNew()
    let mutable g = g0
    let mutable cyc = 0
    let mutable done_ = false
    while not done_ do
        cyc <- cyc + 1
        totalSw.Restart()
        let g1, t1 = settle limit (setPin clkPin true g)
        let g2, t2 = settle limit (setPin clkPin false g1)
        g <- g2
        printfn "cyc%d: h=%d l=%d v=%d (%.0fs)" cyc t1 t2 (value g) totalSw.Elapsed.TotalSeconds
        if cyc >= 12 then done_ <- true
