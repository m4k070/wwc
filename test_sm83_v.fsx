#r "src/bin/Debug/net8.0/WwHdl.dll"
open WwHdl
open WwHdl.Domain
open WwHdl.Netlist
open WwHdl.PipelineWL
open WwHdl.Pipeline
open System.IO

let json = File.ReadAllText "verilog/sm83_subset.json"
printfn "Parsing..."
let nl = Pipeline.frontend json
match nl with
| Error e -> printfn "FAIL: %A" e; exit 1
| Ok nl ->
    printfn "Gates: %d, inputs: %d" nl.Gates.Length nl.PrimaryInputs.Length
    let ncols = int (ceil (sqrt (float nl.Gates.Length)))
    printfn "ncols=%d, grid size: %dx%d" ncols (ncols * 48 + 24) ((nl.Gates.Length / ncols + 1) * 32 + 2)
    
    printfn "Placing..."
    let placed, pins = placeWLWithPitch 48 32 nl
    printfn "Placed: %d gates, %d pins" placed.Length pins.Count
    
    let terminals = placed |> List.collect (fun p ->
        match p.Gate.Kind, p.Gate.Inputs with
        | Dff, [clkNet; dNet] -> [ (dNet, { X = p.Coord.X - 1; Y = p.Coord.Y }); (clkNet, { X = p.Coord.X; Y = p.Coord.Y + 1 }) ]
        | Dff, [cNet; dNet; _rNet] -> [ (dNet, { X = p.Coord.X - 1; Y = p.Coord.Y }); (cNet, { X = p.Coord.X; Y = p.Coord.Y + 1 }) ]
        | _, [a] -> [ (a, { X = p.Coord.X - 1; Y = p.Coord.Y }) ]
        | _, [a; b] -> [ (a, { X = p.Coord.X - 1; Y = p.Coord.Y }); (b, { X = p.Coord.X; Y = p.Coord.Y - 1 }) ]
        | _, [a; b; c] -> [ (a, { X = p.Coord.X - 1; Y = p.Coord.Y }); (b, { X = p.Coord.X; Y = p.Coord.Y - 1 }); (c, { X = p.Coord.X; Y = p.Coord.Y + 1 }) ]
        | _, ins -> ins |> List.mapi (fun i nid -> nid, [{ X = p.Coord.X - 1; Y = p.Coord.Y }; { X = p.Coord.X; Y = p.Coord.Y - 1 }; { X = p.Coord.X; Y = p.Coord.Y + 1 }].[i % 3]) )
    printfn "Terminals: %d" terminals.Length
    
    // Show first 10 terminals
    terminals |> List.take (min 10 terminals.Length) |> List.iteri (fun i (nid, c) ->
        printfn "  terminal[%d]: NetId %d at %A" i (let (NetId n) = nid in n) c)
    
    // Show unique nets
    let uniqueNets = terminals |> List.map fst |> Set.ofList
    printfn "Unique nets: %d" uniqueNets.Count
    
    // Show clock nets
    let clkNets = placed |> List.filter (fun p -> p.Gate.Kind = Dff)
                         |> List.choose (fun p -> p.Gate.Inputs |> List.tryItem 1)
                         |> Set.ofList
    printfn "Clock nets: %A" (clkNets |> Set.toList |> List.map (fun (NetId n) -> n))
