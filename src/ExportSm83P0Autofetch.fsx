#r "bin/Debug/net8.0/WwHdl.dll"
// SM83 P0 autofetch GPU golden test 用エクスポート
// 出力: grid .bin + メタデータ JSON + プログラム全命令実行後の F# reference .bin
open System.IO
open System.Text.Json
open WwHdl
open WwHdl.Domain
open WwHdl.Netlist
open WwHdl.WireLevel
open WwHdl.PipelineWL
open WwHdl.Pipeline

let json = File.ReadAllText "verilog/sm83_p0.json"
let sw = System.Diagnostics.Stopwatch.StartNew()

match compileWL json with
| Error e -> eprintfn "COMPILE ERROR: %A" e
| Ok (grid, placed, pins) ->
    printfn "OK: grid=%d cells, placed=%d gates (%.1fs)" (Map.count grid) placed.Length sw.Elapsed.TotalSeconds

    let outDir = Path.GetFullPath (Path.Combine(__SOURCE_DIRECTORY__, "..", "web"))

    // 正規化オフセット
    let coords = grid |> Map.keys |> Seq.toList
    let minX = coords |> List.map (fun c -> c.X) |> List.min
    let minY = coords |> List.map (fun c -> c.Y) |> List.min

    let norm (c: Coord) = {| x = c.X - minX; y = c.Y - minY |}

    // 出力ゲート座標 (NetId → 正規化座標)
    let gateCoord (netId: int) =
        placed |> List.find (fun p -> p.Gate.Output = NetId netId) |> fun p -> norm p.Coord

    let meta = {|
        width = (coords |> List.map (fun c -> c.X) |> List.max) - minX + 1
        height = (coords |> List.map (fun c -> c.Y) |> List.max) - minY + 1
        pins = {|
            clk = norm pins.[NetId 2]
            rst = norm pins.[NetId 3]
            inst = [| for i in 0..7 -> norm pins.[NetId (4 + i)] |]
            data_in = [| for i in 0..7 -> norm pins.[NetId (12 + i)] |]
        |}
        regs = {|
            pc  = [| for i in 0..7 -> gateCoord (20 + i) |]
            a   = [| for i in 0..7 -> gateCoord (28 + i) |]
            b   = [| for i in 0..7 -> gateCoord (36 + i) |]
            c   = [| for i in 0..7 -> gateCoord (44 + i) |]
            d   = [| for i in 0..7 -> gateCoord (52 + i) |]
        |}
    |}

    let metaJson = JsonSerializer.Serialize(meta, JsonSerializerOptions(WriteIndented = true))
    let metaPath = Path.Combine(outDir, "sm83p0_autofetch_meta.json")
    File.WriteAllText(metaPath, metaJson)
    printfn "Exported %s" metaPath

    // 初期 grid: rst=1 (リセット) → rst=0 (解除) → 実行可能状態
    let afterRst, _ = grid |> setPin pins.[NetId 3] true |> settle 2500
    let initGrid, _ = afterRst |> setPin pins.[NetId 3] false |> settle 2500
    let initBin = exportGrid initGrid
    let initPath = Path.Combine(outDir, "sm83p0_autofetch_init.bin")
    File.WriteAllBytes(initPath, initBin)
    printfn "Exported %s (%d bytes)" initPath initBin.Length

    // F# で全命令を実行し reference を生成
    let clkPin = pins.[NetId 2]
    let rstPin = pins.[NetId 3]
    let instPins = [| for i in 0..7 -> pins.[NetId (4 + i)] |]
    let dataInPins = [| for i in 0..7 -> pins.[NetId (12 + i)] |]

    let setInst (v: int) (g: LGrid) =
        let mutable g = g
        for i in 0..7 do g <- setPin instPins.[i] ((v >>> i) &&& 1 = 1) g
        g

    let setDataIn (v: int) (g: LGrid) =
        let mutable g = g
        for i in 0..7 do g <- setPin dataInPins.[i] ((v >>> i) &&& 1 = 1) g
        g

    // cycle: clk=0 で inst/data 設定 → settle → clk=1 → settle
    let cycle (g: LGrid) (inst: int) (data: int) =
        let g = g |> setInst inst |> setDataIn data |> setPin clkPin false
        let g, _ = settle 2500 g
        let g = setPin clkPin true g
        let g, _ = settle 2500 g
        g

    // 最小検証: NOP + LD A,#42 (2 命令 = 4 settles)
    let program = [|
        0xE0, 0,   "NOP"
        0x00, 42,  "LD A,#42"
    |]

    let mutable g = initGrid
    for (inst, data, desc) in program do
        let sw2 = System.Diagnostics.Stopwatch.StartNew()
        g <- cycle g inst data
        printfn "  %s (%.1fs)" desc sw2.Elapsed.TotalSeconds

    let refBin = exportGrid g
    let refPath = Path.Combine(outDir, "sm83p0_autofetch_ref.bin")
    File.WriteAllBytes(refPath, refBin)
    printfn "\nExported %s (%d bytes)" refPath refBin.Length
    printfn "Total: %.1fs" sw.Elapsed.TotalSeconds
