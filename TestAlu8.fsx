#r "src/bin/Debug/net8.0/WwHdl.dll"

open System.IO
open WwHdl.Netlist
open WwHdl.Domain
open WwHdl.PipelineWL
open WwHdl.WireLevel

let jsonPath = "verilog/alu8.json"
let json = File.ReadAllText jsonPath

printfn "Compiling..."
let sw = System.Diagnostics.Stopwatch.StartNew()
match compileWL json with
| Error e -> printfn "FAIL: %A" e; exit 1
| Ok (grid0, placed0, pins0) ->
    sw.Stop()
    let grid : LGrid = grid0
    let placed : WlPlaced list = placed0
    let pins : Map<NetId, Coord> = pins0
    printfn "Compile: %d ms, grid=%d cells, %d gates, %d pins"
        sw.ElapsedMilliseconds (grid |> Map.count) placed.Length (pins |> Map.count)

    let pinOf (n: int) : Coord = pins.[NetId n]
    let outCoordOf (n: int) : Coord = (placed |> List.find (fun (p: WlPlaced) -> p.Gate.Output = NetId n)).Coord

    let opBits   = [2;3;4]
    let aBits    = [5..12]
    let bBits    = [13..20]
    let flagsIn  = [21..24]
    let resBits  = [25..32]
    let flagsOut = [33..36]

    let setBits (grid: LGrid) (bits: int list) (value: int) : LGrid =
        let mutable g = grid
        for i in 0 .. bits.Length-1 do
            g <- setPin (pinOf bits.[i]) (((value >>> i) &&& 1) = 1) g
        g

    let readBits (grid: LGrid) (bits: int list) : int =
        let mutable v = 0
        for i in 0 .. bits.Length-1 do
            let c = outCoordOf bits.[i]
            if levelOf grid c then v <- v ||| (1 <<< i)
        v

    let testCase op a b cin =
        let g = setBits (setBits (setBits (setBits grid opBits op) aBits a) bBits b) flagsIn cin
        let settled, gens = settle 10000 g
        let res = readBits settled resBits
        let flg = readBits settled flagsOut
        (res, flg, gens)

    printfn "\n=== ALU Tests ===\n"

    let results = [
        ("ADD 1,1",       0, 1, 1, 0, 2,      0x00)
        ("ADD 0x80,0x80", 0, 0x80, 0x80, 0, 0, 0x90)
        ("SUB 5,3",       2, 5, 3, 0, 2,      0x40)
        ("SUB 3,5",       2, 3, 5, 0, (3-5&&&0xFF), 0xD0)
        ("ADC 1,1 (C=1)", 1, 1, 1, 1, 3,      0x00)
        ("AND 0xFF,0x0F", 4, 0xFF, 0x0F, 0, 0x0F, 0x20)
        ("XOR 0xFF,0x0F", 5, 0xFF, 0x0F, 0, 0xF0, 0x00)
        ("OR 0xF0,0x0F",  6, 0xF0, 0x0F, 0, 0xFF, 0x00)
        ("CP 5,3",        7, 5, 3, 0, 0xFE,    0x40)
    ]

    let mutable passed = 0
    for (name, op, a, b, cin, expRes, expFlags) in results do
        printf "%s ... " name
        let t = System.Diagnostics.Stopwatch.StartNew()
        let res, flg, gens = testCase op a b cin
        t.Stop()
        let resOk = res = expRes
        let flgOk = flg = expFlags
        if resOk && flgOk then
            printfn "PASS (res=%d, flags=0x%X, %d gens, %d ms)" res flg gens t.ElapsedMilliseconds
            passed <- passed + 1
        else
            printfn "FAIL (res=%d/%d, flags=0x%X/%X, %d gens, %d ms)" res expRes flg expFlags gens t.ElapsedMilliseconds

    printfn "\n%d/%d passed" passed results.Length
