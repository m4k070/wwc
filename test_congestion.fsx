// test_congestion.fsx — single circuit test
#r "src/bin/Debug/net8.0/WwHdl.dll"
open WwHdl
open WwHdl.Domain
open WwHdl.WireLevel
open WwHdl.PipelineWL
open System.IO

let sw = System.Diagnostics.Stopwatch()

let name = "sm83_min"
let pitchX, pitchY = 24, 16
let jsonPath = sprintf "verilog/%s.json" name
printfn "Loading %s ..." jsonPath
let src = File.ReadAllText jsonPath
sw.Start()
match compileWLWithPitch pitchX pitchY src with
| Error e ->
    printfn "FAIL: %A (%d ms)" e sw.ElapsedMilliseconds
| Ok (grid, placed, pins) ->
    sw.Stop()
    let gateCells = placed |> List.length
    let mutable wireCells, crossCells, pinCells, dffCells, nandCells = 0,0,0,0,0
    for kv in grid do
        match kv.Value with
        | LEmpty -> ()
        | LWire _ -> wireCells <- wireCells + 1
        | Cross _ -> crossCells <- crossCells + 1
        | Pin _ -> pinCells <- pinCells + 1
        | LNand _ -> nandCells <- nandCells + 1
        | LDff _ -> dffCells <- dffCells + 1
    let allCells = wireCells + crossCells + nandCells + dffCells + pinCells
    let wireCellsTotal = wireCells + crossCells
    let gridW = (grid |> Seq.map (fun kv -> kv.Key.X) |> Seq.max) + 1
    let gridH = (grid |> Seq.map (fun kv -> kv.Key.Y) |> Seq.max) + 1
    let density = float allCells / float (gridW * gridH) * 100.0
    let crossRatio = float crossCells / float wireCellsTotal * 100.0
    printfn "OK: %d cells (nand=%d dff=%d wire=%d cross=%d pin=%d)" allCells nandCells dffCells wireCells crossCells pinCells
    printfn "Grid: %dx%d, density=%.1f%%, crossRatio=%.1f%%, overhead=%.1fx" gridW gridH density crossRatio (float allCells / float gateCells)
    printfn "Time: %d ms" sw.ElapsedMilliseconds
