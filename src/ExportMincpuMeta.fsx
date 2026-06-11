#r "bin/Debug/net8.0/WwHdl.dll"
// mincpu の出力ポート座標を JSON にエクスポート (デモ用)
open System.IO
open System.Text.Json
open WwHdl.Domain
open WwHdl.Netlist
open WwHdl.WireLevel
open WwHdl.PipelineWL
open WwHdl

let json = File.ReadAllText (Path.Combine(__SOURCE_DIRECTORY__, "..", "verilog/mincpu.json"))

match compileWL json with
| Error e -> eprintfn "compile error: %A" e
| Ok (grid, placed, pins) ->
    let minX = grid |> Map.keys |> Seq.map (fun c -> c.X) |> Seq.min
    let minY = grid |> Map.keys |> Seq.map (fun c -> c.Y) |> Seq.min

    // 出力ポート out[0..7] の DFF 座標 (正規化後)
    let yosysModule = WwHdl.Pipeline.parseYosysJson json |> function Ok m -> m | _ -> failwith "parse error"
    let outNetIds = yosysModule.Ports.["out"].Bits
    let outPorts =
        outNetIds
        |> List.mapi (fun i n ->
            let p = placed |> List.find (fun p -> p.Gate.Output = NetId n)
            let c = p.Coord
            // DFF の Q 出力は DFF セル自身。正規化後の座標は (c.X - minX, c.Y - minY)
            {| bit = i; x = c.X - minX; y = c.Y - minY |})

    let meta = {|
        width = (grid |> Map.keys |> Seq.map (fun c -> c.X) |> Seq.max) - minX + 1
        height = (grid |> Map.keys |> Seq.map (fun c -> c.Y) |> Seq.max) - minY + 1
        cellCount = Map.count grid
        gateCount = placed.Length
        outPorts = outPorts
        clkPin = match Map.tryFind (NetId 2) pins with Some c -> {| x = c.X - minX; y = c.Y - minY |} | None -> {| x = 0; y = 0 |}
    |}

    let jsonStr = JsonSerializer.Serialize(meta, JsonSerializerOptions(WriteIndented = true))
    let outPath = Path.Combine(__SOURCE_DIRECTORY__, "..", "web", "mincpu-meta.json")
    File.WriteAllText(outPath, jsonStr)
    printfn "Exported %s" outPath
    printfn "%s" jsonStr
