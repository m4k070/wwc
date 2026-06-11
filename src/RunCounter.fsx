#r "bin/Debug/net8.0/WwHdl.dll"
// yosys 合成 4bit カウンタ → WireLevel コンパイル → カウント動作検証
open WwHdl
open WwHdl.Domain
open WwHdl.Netlist
open WwHdl.WireLevel
open WwHdl.PipelineWL

let json = System.IO.File.ReadAllText "../verilog/counter4.json"

// q ポートのネット番号を取得
let qBits =
    match Pipeline.parseYosysJson json with
    | Ok m -> m.Ports.["q"].Bits
    | Error e -> failwithf "parse error: %A" e
printfn "q bits: %A" qBits

match compileWL json with
| Error e -> printfn "COMPILE ERROR: %A" e
| Ok (grid, placed, pins) ->
    printfn "%s" (dumpAscii grid)
    let outOf n = placed |> List.find (fun p -> p.Gate.Output = NetId n) |> fun p -> p.Coord
    let qCoords = qBits |> List.map outOf
    let clkPin = pins |> Map.toList |> List.head |> snd
    printfn "clk pin: %A, DFF count: %d" clkPin
        (placed |> List.filter (fun p -> p.Gate.Kind = Dff) |> List.length)
    let value g =
        qBits |> List.mapi (fun i n -> if levelOf g (outOf n) then 1 <<< i else 0) |> List.sum
    let halfP = 512
    let mutable g = grid |> stepN halfP   // 初期収束 (clk=0)
    printfn "initial: %d" (value g)
    let mutable ok = true
    for k in 1 .. 18 do
        g <- g |> setPin clkPin true  |> stepN halfP
        g <- g |> setPin clkPin false |> stepN halfP
        let v = value g
        if v <> k % 16 then ok <- false
        printfn "cycle %2d: q=%2d (expect %2d) %s" k v (k % 16) (if v = k % 16 then "" else "** NG **")
    printfn "%s" (if ok then "COUNTER OK" else "COUNTER FAILED")
