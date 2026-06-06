#r "bin/Debug/net8.0/WwHdl.dll"
open WwHdl
open WwHdl.Pipeline

let halfAdderJson = """{"modules":{"top":{"ports":{"a":{"direction":"input","bits":[2]},"b":{"direction":"input","bits":[3]},"sum":{"direction":"output","bits":[8]},"carry":{"direction":"output","bits":[7]}},"cells":{"u0":{"type":"$_NAND_","port_directions":{"A":"input","B":"input","Y":"output"},"connections":{"A":[2],"B":[3],"Y":[4]}},"u1":{"type":"$_NAND_","port_directions":{"A":"input","B":"input","Y":"output"},"connections":{"A":[2],"B":[4],"Y":[5]}},"u2":{"type":"$_NAND_","port_directions":{"A":"input","B":"input","Y":"output"},"connections":{"A":[3],"B":[4],"Y":[6]}},"u3":{"type":"$_NAND_","port_directions":{"A":"input","B":"input","Y":"output"},"connections":{"A":[5],"B":[6],"Y":[8]}},"u4":{"type":"$_NOT_","port_directions":{"A":"input","Y":"output"},"connections":{"A":[4],"Y":[7]}}}}}}"""

let lib = Library.defaultLib
let r = compileFull lib halfAdderJson
printfn "Result: %A" r

// Check routing step individually
let r2 =
    frontend halfAdderJson
    |> Result.bind (techMap lib)
    |> Result.bind place
    |> Result.bind (fun pl ->
        printfn "Placement: %d gates" pl.Length
        for p in pl do
            printfn "  gate output=%A" p.Gate.Output
        route pl |> Result.map (fun ws ->
            printfn "Wires: %d" ws.Length
            for w in ws do
                printfn "  net=%A consumer=%A delay=%A pathLen=%d" w.Net w.Consumer w.Delay w.Path.Length
            pl, ws))
printfn "Step result: %A" (r2 |> Result.map (fun (pl, ws) -> pl.Length, ws.Length))
