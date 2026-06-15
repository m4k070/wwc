#r "bin/Debug/net8.0/WwHdl.dll"
open WwHdl
open WwHdl.Domain
open WwHdl.Netlist
open WwHdl.WireLevel
open WwHdl.PipelineWL

let json = System.IO.File.ReadAllText "verilog/sm83_min.json"
match compileWL json with
| Error e -> printfn "Error: %A" e
| Ok (grid, placed, pins) ->
    printfn "Compiled: %d cells, %d gates" (Map.count grid) placed.Length

    let clkPin  = pins.[NetId 2]
    let rstPin  = pins.[NetId 3]
    let instPins = [4..11] |> List.map (fun n -> pins.[NetId n])

    let dffCoord (netId: int) : Coord =
        placed |> List.find (fun p -> match p.Gate.Output with NetId n -> n = netId) |> fun p -> p.Coord
    let aCoords   = [20..27] |> List.map dffCoord
    let bCoords   = [28..33] |> List.map dffCoord
    let pcCoords  = [12..19] |> List.map dffCoord
    let regValue coords (g: LGrid) =
        coords |> List.mapi (fun i c -> if levelOf g c then 1 <<< i else 0) |> List.sum

    let setInst (v: int) (g: LGrid) =
        let mutable gr = g
        for i in 0..7 do
            gr <- setPin instPins.[i] ((v >>> i) &&& 1 = 1) gr
        gr

    let S = 800  // クロック伝搬に十分な世代数

    printfn "=== LD A, 5 ==="
    let sw = System.Diagnostics.Stopwatch.StartNew()

    // 初期状態: rst=1, clk=0
    let g0 = grid |> setPin clkPin false |> setPin rstPin true |> setInst 0
    let g1, n1 = settle S g0
    printfn "  init (gen=%d, %dms): A=%d B=%d PC=%d"
        n1 sw.ElapsedMilliseconds (regValue aCoords g1) (regValue bCoords g1) (regValue pcCoords g1)

    // rst=0, inst=5 (LD A, #imm), clk=0 → データ伝播
    let g2, n2 = settle S (g1 |> setPin rstPin false |> setInst 0x05)
    printfn "  data setup (gen=%d, %dms): A=%d B=%d PC=%d"
        n2 sw.ElapsedMilliseconds (regValue aCoords g2) (regValue bCoords g2) (regValue pcCoords g2)

    // clk↑
    let g3, n3 = settle S (setPin clkPin true g2)
    printfn "  clk↑ (gen=%d, %dms):  A=%d B=%d PC=%d"
        n3 sw.ElapsedMilliseconds (regValue aCoords g3) (regValue bCoords g3) (regValue pcCoords g3)

    // clk↓
    let g4, n4 = settle S (setPin clkPin false g3)
    printfn "  clk↓ (gen=%d, %dms):  A=%d B=%d PC=%d"
        n4 sw.ElapsedMilliseconds (regValue aCoords g4) (regValue bCoords g4) (regValue pcCoords g4)

    printfn "\nDone"
