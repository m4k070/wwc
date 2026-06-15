#r "src/bin/Debug/net8.0/WwHdl.dll"

open WwHdl
open WwHdl.Domain
open WwHdl.Netlist
open WwHdl.Pipeline
open System.IO

let jsonPath = "verilog/sm83_subset.json"
let json = File.ReadAllText jsonPath

match parseYosysJson json with
| Error e -> printfn "parse err %A" e
| Ok m ->
    // Find the NOT and DFF connected to NetId 37
    printfn "=== NetId 37 analysis ==="
    for (name, cell) in m.Cells do
        for kv in cell.Connections do
            if kv.Value |> List.contains 37 then
                printfn "  %s .%s = %A (type=%s)" name kv.Key kv.Value cell.Type

    // Parse Netlist to see gate order
    let nlResult = WwHdl.Pipeline.yosysToNetlist m
    match nlResult with
    | Error e -> printfn "Netlist err %A" e
    | Ok nl ->
        printfn "\n=== Netlist summary ==="
        printfn "Gates: %d" nl.Gates.Length
        printfn "PrimaryInputs: %A" nl.PrimaryInputs
        printfn "PrimaryOutputs: %A" nl.PrimaryOutputs

        // Find gates related to NetId 37
        printfn "\n=== Gates with NetId 37 ==="
        for g in nl.Gates do
            if g.Output = NetId 37 || (g.Inputs |> List.contains (NetId 37)) then
                printfn "  Gate idx=%d: kind=%A inputs=%A output=%A" 
                    (nl.Gates |> List.findIndex (fun x -> x = g))
                    g.Kind g.Inputs g.Output

        // Show the first 30 gates to understand layout
        printfn "\n=== First 30 gates (declaration order) ==="
        for i in 0..min 29 (nl.Gates.Length - 1) do
            let g = nl.Gates.[i]
            printfn "  [%d] kind=%A inputs=%A output=NetId(%d)" i g.Kind g.Inputs (g.Output |> fun (NetId n) -> n)

        // Show the last 30 gates
        printfn "\n=== Last 30 gates ==="
        for i in max 0 (nl.Gates.Length - 30)..(nl.Gates.Length - 1) do
            let g = nl.Gates.[i]
            printfn "  [%d] kind=%A inputs=%A output=NetId(%d)" i g.Kind g.Inputs (g.Output |> fun (NetId n) -> n)

        // Find gates producing outputs
        printfn "\n=== Output port gate producers ==="
        for outNet in nl.PrimaryOutputs do
            let found = nl.Gates |> List.tryFind (fun g -> g.Output = outNet)
            match found with
            | Some g -> 
                let idx = nl.Gates |> List.findIndex (fun x -> x = g)
                printfn "  NetId %d: [%d] kind=%A" (outNet |> fun (NetId n) -> n) idx g.Kind
            | None -> printfn "  NetId %d: no gate drives this (it's a primary input?)" (outNet |> fun (NetId n) -> n)

        // Count terminals per net
        printfn "\n=== Terminal count per net (top 10) ==="
        let termCounts =
            nl.Gates
            |> List.collect (fun g -> g.Inputs)
            |> List.countBy id
            |> List.sortByDescending snd
            |> List.truncate 10
        for (net, count) in termCounts do
            printfn "  NetId %d: %d terminals" (net |> fun (NetId n) -> n) count

        // Show DFFs and their connections for output ports
        printfn "\n=== Output port DFFs ==="
        for outNet in nl.PrimaryOutputs do
            let g = nl.Gates |> List.tryFind (fun g -> g.Output = outNet)
            match g with
            | Some g when g.Kind = Dff ->
                let idx = nl.Gates |> List.findIndex (fun x -> x = g)
                printfn "  NetId %d (output): DFF at [%d], inputs=%A" 
                    (outNet |> fun (NetId n) -> n) idx g.Inputs
            | _ -> ()
