// verify_clock.fsx — クロックスキュー検証: 指定回路を指定ピッチでコンパイルし、
// リセット動作・クロックトグル・クロック配線の経路長 (skew) を確認する。
// 使い方: dotnet fsi verify_clock.fsx <circuit> <pitchX> <pitchY>
#r "src/bin/Debug/net8.0/WwHdl.dll"
open WwHdl
open WwHdl.Domain
open WwHdl.Netlist
open WwHdl.WireLevel
open WwHdl.PipelineWL
open System.IO

// PipelineWL の addC / toward は private のため、ここで再定義する
let private addC (a: Coord) (b: Coord) = { X = a.X + b.X; Y = a.Y + b.Y }
let private toward (c: Coord) (d: Dir) = addC c (delta d)

let name = fsi.CommandLineArgs |> Array.tryItem 1 |> Option.defaultValue "sm83_subset"
let pitchX = fsi.CommandLineArgs |> Array.tryItem 2 |> Option.map int |> Option.defaultValue 20
let pitchY = fsi.CommandLineArgs |> Array.tryItem 3 |> Option.map int |> Option.defaultValue 14

let src = File.ReadAllText (sprintf "verilog/%s.json" name)
let sw = System.Diagnostics.Stopwatch.StartNew()

match compileWLWithPitch pitchX pitchY src with
| Error e -> printfn "COMPILE FAIL: %A (%d ms)" e sw.ElapsedMilliseconds
| Ok (grid, placed, pins) ->
    sw.Stop()
    printfn "COMPILE OK [%s %dx%d]: %d cells, %d gates (%.1f min)" name pitchX pitchY (Map.count grid) placed.Length (sw.Elapsed.TotalMinutes)

    // --- ポート構成 (回路名で切り替え) ---
    // sm83_min: a_out=20-27, pc_out=12-19 / sm83_subset: a_out=38-45, pc_out=46-61
    let aOutNets, pcOutNets =
        if name = "sm83_min" then
            [| for i in 0..7 -> NetId (20 + i) |], [| for i in 0..7 -> NetId (12 + i) |]
        else
            [| for i in 0..7 -> NetId (38 + i) |], [| for i in 0..15 -> NetId (46 + i) |]
    let clkPin = pins.[NetId 2]
    let rstPin = pins.[NetId 3]
    let aOutPins  = aOutNets  |> Array.map (fun n -> placed |> List.find (fun p -> p.Gate.Output = n) |> fun p -> p.Coord)
    let pcOutPins = pcOutNets |> Array.map (fun n -> placed |> List.find (fun p -> p.Gate.Output = n) |> fun p -> p.Coord)

    let readReg (pins: Coord[]) (g: LGrid) : int =
        pins |> Array.mapi (fun i c -> if levelOf g c then 1 <<< i else 0) |> Array.sum

    let settlePhase (limit: int) (g: LGrid) =
        let sw2 = System.Diagnostics.Stopwatch.StartNew()
        let gSettled, t = settle limit g
        printfn "    settle: %d gen (%.1fs)" t sw2.Elapsed.TotalSeconds
        gSettled

    // --- リセット ---
    printfn "\n=== RESET (rst=true, clk=false) ==="
    let afterRst = grid |> setPin rstPin true |> setPin clkPin false |> settlePhase 5000
    printfn "  after rst: a=%d pc=%d" (readReg aOutPins afterRst) (readReg pcOutPins afterRst)

    // --- rst 解除 ---
    printfn "\n=== RST 解除 (clk=false のまま) ==="
    let g0 = afterRst |> setPin rstPin false |> settlePhase 5000
    printfn "  after rst-release: a=%d pc=%d" (readReg aOutPins g0) (readReg pcOutPins g0)

    // --- クロック 1 トグル (clk=true) ---
    printfn "\n=== CLK HIGH ==="
    let g1 = g0 |> setPin clkPin true |> settlePhase 5000
    printfn "  clk-high: a=%d pc=%d" (readReg aOutPins g1) (readReg pcOutPins g1)

    // --- クロック 2 トグル (clk=false) ---
    printfn "\n=== CLK LOW ==="
    let g2 = g1 |> setPin clkPin false |> settlePhase 5000
    printfn "  clk-low: a=%d pc=%d" (readReg aOutPins g2) (readReg pcOutPins g2)

    // --- クロック配線の経路長分析 ---
    // クロックネット (NetId 2) の OccWire を駆動源から BFS で辿り、
    // 各 DFF のクロック入力までの距離を測定して skew を実測する。
    printfn "\n=== クロック配線の経路長 ==="
    let dffCoords =
        placed
        |> List.choose (fun p ->
            match p.Gate.Kind with
            | Netlist.Dff -> Some p.Coord
            | _ -> None)
    printfn "  DFF 数: %d" dffCoords.Length

    // 駆動源 (clk ピン) から BFS: 各セルまでの最小距離を測定
    let startCoord = clkPin
    let dist = System.Collections.Generic.Dictionary<Coord, int>()
    let queue = System.Collections.Generic.Queue<Coord>()
    dist.[startCoord] <- 0
    queue.Enqueue startCoord
    while queue.Count > 0 do
        let c = queue.Dequeue ()
        let d = dist.[c]
        for nd in [E; W; N; S] do
            let c' = toward c nd
            if not (dist.ContainsKey c') then
                match Map.tryFind c' grid with
                | Some (LWire _) | Some (Cross _) ->
                    dist.[c'] <- d + 1
                    queue.Enqueue c'
                | _ -> ()
    // DFF の近傍 (クロック入力側面 S) にクロック配線が届いているか
    let dffDists =
        dffCoords
        |> List.choose (fun dc ->
            let s = toward dc S   // DFF の clk 入力は S 側
            match dist.TryGetValue s with
            | true, d -> Some (dc, d)
            | _ -> None)
    if dffDists.IsEmpty then
        printfn "  (DFF 近傍にクロック配線が届いていない — 配線構造の確認が必要)"
    else
        let maxD = dffDists |> List.map snd |> List.max
        let minD = dffDists |> List.map snd |> List.min
        printfn "  DFF クロック経路長: min=%d max=%d skew=%d (DFF %d 個中 %d 個に到達)"
            minD maxD (maxD - minD) dffCoords.Length dffDists.Length
