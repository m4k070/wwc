#r "bin/Debug/net8.0/WwHdl.dll"
open WwHdl.Domain
open WwHdl.Library
open WwHdl.Place
open WwHdl.Route
open WwHdl.Sta
open WwHdl.Sim
open WwHdl.Pipeline

let twoNotJson = """
{"modules":{"top":{"ports":{"a":{"direction":"input","bits":[2]},"y":{"direction":"output","bits":[4]}},
"cells":{"u0":{"type":"$_NOT_","port_directions":{"A":"input","Y":"output"},"connections":{"A":[2],"Y":[3]}},
"u1":{"type":"$_NOT_","port_directions":{"A":"input","Y":"output"},"connections":{"A":[3],"Y":[4]}}}}}}"""

let lib = WwHdl.Library.defaultLib
let (grid, placement, wires) =
    match compileFull lib twoNotJson with Ok x -> x | Error e -> failwithf "%A" e

let arrivals = computeArrival placement wires
let wireDelay = wires |> List.map (fun w -> w.Net, w.Delay) |> Map.ofList

// Verify initial grid has no Heads
let initHeads = grid |> Map.filter (fun _ v -> v = Head)
printfn "Initial grid Heads: %d (should be 0)" (Map.count initHeads)

// Print the routing wire path
let wire = wires |> List.head
printfn "Routing path (%d cells): %A" wire.Path.Length wire.Path

// Run step-by-step, printing ALL head positions each step
let clockEntries =
    placement |> List.collect (fun p ->
        let t = clockTimeOf p arrivals wireDelay
        clockCoords p |> List.map (fun c -> t, c))
    |> List.groupBy fst |> List.map (fun (t, ps) -> t, List.map snd ps)
    |> Map.ofList

printfn "\nWire delays: %A" (wires |> List.map (fun w -> w.Net, w.Delay))
printfn "Arrivals: %A" (arrivals |> Map.toList)
printfn "Clock injection times: %A" (clockEntries |> Map.toList |> List.map (fun (t,cs) -> t, cs))

let mutable state = grid
for idx in 0..15 do
    let t = idx * 1<WwHdl.Units.gen>
    match Map.tryFind t clockEntries with
    | Some coords -> for c in coords do state <- Map.add c Head state
    | None -> ()
    let heads = state |> Map.filter (fun _ v -> v = Head) |> Map.keys |> List.ofSeq |> List.sortBy (fun c -> c.X)
    printfn "t=%02d: %A" idx heads
    state <- WwHdl.Rule.step state
