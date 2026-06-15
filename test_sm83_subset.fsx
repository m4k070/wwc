#r "src/bin/Debug/net8.0/WwHdl.dll"

open WwHdl
open WwHdl.Domain
open WwHdl.Netlist
open WwHdl.PipelineWL
open System.IO

let jsonPath = "verilog/sm83_subset.json"
let json = File.ReadAllText jsonPath

printfn "=== sm83_subset compileWL (default pitch, sorted, 3M explore) ==="
let sw = System.Diagnostics.Stopwatch.StartNew()
match compileWLWithPitch 48 32 json with
| Error e ->
    sw.Stop()
    printfn "FAILED after %d ms: %A" sw.ElapsedMilliseconds e
    exit 1
| Ok (grid, placed, pins) ->
    sw.Stop()
    let nand = placed |> List.filter (fun p -> p.Gate.Kind = Nand) |> List.length
    let not_ = placed |> List.filter (fun p -> p.Gate.Kind = Not) |> List.length
    let dff  = placed |> List.filter (fun p -> p.Gate.Kind = Dff) |> List.length
    printfn "OK in %d ms" sw.ElapsedMilliseconds
    printfn "Gates: %d (NAND=%d NOT=%d DFF=%d)" placed.Length nand not_ dff
    printfn "Grid: %d cells, Pins: %d" (Map.count grid) (Map.count pins)

    // Check positions of NetId 37 producer/consumer
    let net37 = NetId 37
    let producer = placed |> List.tryFind (fun p -> p.Gate.Output = net37)
    let consumer = placed |> List.tryFind (fun p -> p.Gate.Inputs |> List.contains net37)
    match producer, consumer with
    | Some p, Some c ->
        let dist = abs(p.Coord.X - c.Coord.X) + abs(p.Coord.Y - c.Coord.Y)
        printfn "Net 37: DFF at %A, NOT at %A, dist=%d" p.Coord c.Coord dist
    | _ -> ()
    printfn "Ports:"
    for kv in pins do
        printfn "  NetId %d = %A" (kv.Key |> fun (NetId n) -> n) kv.Value
