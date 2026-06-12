#r "bin/Debug/net8.0/WwHdl.dll"

open WwHdl
open WwHdl.Domain
open WwHdl.Netlist
open WwHdl.WireLevel
open WwHdl.PipelineWL
open WwHdl.Pipeline

let json = System.IO.File.ReadAllText "verilog/sm83_p0.json"
let sw = System.Diagnostics.Stopwatch.StartNew()

match compileWL json with
| Error e -> printfn "COMPILE ERROR: %A" e
| Ok (grid, placed, pins) ->
    printfn "OK: grid=%d cells, placed=%d gates (%.1fs)" (Map.count grid) placed.Length sw.Elapsed.TotalSeconds
    let clkPin = pins.[NetId 2]
    let rstPin = pins.[NetId 3]
    let instPins = [| for i in 0..7 -> pins.[NetId (4 + i)] |]
    let dataInPins = [| for i in 0..7 -> pins.[NetId (12 + i)] |]
    let pcOut n = placed |> List.find (fun p -> p.Gate.Output = NetId (20 + n)) |> fun p -> p.Coord
    let aOut  n = placed |> List.find (fun p -> p.Gate.Output = NetId (28 + n)) |> fun p -> p.Coord
    let bOut  n = placed |> List.find (fun p -> p.Gate.Output = NetId (36 + n)) |> fun p -> p.Coord
    let cOut  n = placed |> List.find (fun p -> p.Gate.Output = NetId (44 + n)) |> fun p -> p.Coord
    let dOut  n = placed |> List.find (fun p -> p.Gate.Output = NetId (52 + n)) |> fun p -> p.Coord

    let setInst (v: int) (g: LGrid) =
        let mutable g = g
        for i in 0..7 do
            g <- setPin instPins.[i] ((v >>> i) &&& 1 = 1) g
        g

    let setDataIn (v: int) (g: LGrid) =
        let mutable g = g
        for i in 0..7 do
            g <- setPin dataInPins.[i] ((v >>> i) &&& 1 = 1) g
        g

    let readReg (regOut: int -> Coord) (g: LGrid) =
        List.sumBy (fun i -> if levelOf g (regOut i) then 1 <<< i else 0) [0..7]

    let pcOf g = readReg pcOut g
    let aOf  g = readReg aOut g
    let bOf  g = readReg bOut g
    let cOf  g = readReg cOut g
    let dOf  g = readReg dOut g

    let cycle (g: LGrid) (inst: int) (data: int) =
        let g = g |> setInst inst |> setDataIn data |> setPin clkPin false
        let g, _ = settle 1000 g
        let g = setPin clkPin true g
        let g, _ = settle 1000 g
        g

    let stepSw = System.Diagnostics.Stopwatch()

    stepSw.Restart()
    let g0, _ = grid |> setPin rstPin true |> settle 1000
    printfn "reset:  PC=%3d A=%3d B=%3d C=%3d D=%3d (%.1fs)"
        (pcOf g0) (aOf g0) (bOf g0) (cOf g0) (dOf g0) stepSw.Elapsed.TotalSeconds

    stepSw.Restart()
    let g1, _ = g0 |> setPin rstPin false |> settle 500
    printfn "rel:    PC=%3d A=%3d B=%3d C=%3d D=%3d (%.1fs)"
        (pcOf g1) (aOf g1) (bOf g1) (cOf g1) (dOf g1) stepSw.Elapsed.TotalSeconds

    let program = [|
        // (inst, data_in, expPc, expA, expB, expC, expD, desc)
        0xE0, 0,   1,  0,  0,  0,  0, "NOP"
        0x00, 42,  2, 42,  0,  0,  0, "LD A,#42"
        0x20, 17,  3, 42, 17,  0,  0, "LD B,#17"
        0x30, 0,   4, 42, 17, 42,  0, "MOV C,A"
        0x38, 0,   5, 42, 17, 42, 17, "MOV D,B"
        0x28, 0,   6, 42, 42, 42, 17, "MOV B,A"
        0x42, 0,   7, 84, 42, 42, 17, "ADD A,B (42+42)"
        0x4A, 0,   8, 42, 42, 42, 17, "SUB A,B (84-42)"
        0x00, 0,   9,  0, 42, 42, 17, "LD A,#0"
        0x20, 1,  10,  0,  1, 42, 17, "LD B,#1"
        0x52, 0,  11,  0,  1, 42, 17, "AND A,B (0&1)"
        0x5A, 0,  12,  1,  1, 42, 17, "XOR A,B (0^1)"
        0x00, 2,  13,  2,  1, 42, 17, "LD A,#2"
        0x62, 0,  14,  3,  1, 42, 17, "OR A,B (2|1=3)"
        0x00, 0,  15,  0,  1, 42, 17, "LD A,#0"
        0x20, 2,  16,  0,  2, 42, 17, "LD B,#2"
        0x6A, 0,  17,  0,  2, 42, 17, "CP A,B"
        0x80, 0,  18,  1,  2, 42, 17, "INC A"
        0x94, 0,  19,  1,  1, 42, 17, "DEC B"
        0xE0, 0,  20,  1,  1, 42, 17, "NOP"
    |]

    let mutable g = g1
    let mutable allOk = true
    for (inst, di, epc, ea, eb, ec, ed, desc) in program do
        stepSw.Restart()
        g <- cycle g inst di
        let pc = pcOf g
        let a  = aOf g
        let b  = bOf g
        let c  = cOf g
        let d  = dOf g
        let ok = pc = epc && a = ea && b = eb && c = ec && d = ed
        if not ok then allOk <- false
        printfn "%s: PC=%3d A=%3d B=%3d C=%3d D=%3d (%.1fs) %s"
            desc pc a b c d stepSw.Elapsed.TotalSeconds
            (if ok then "✓" else sprintf "✗ (exp %d %d %d %d %d)" epc ea eb ec ed)

    printfn "\nTotal: %.1fs — %s" sw.Elapsed.TotalSeconds (if allOk then "ALL OK ✓" else "FAILURES ✗")
