// pitch_bench.fsx — ピッチ別の grid 面積・ルーティング時間比較 (測定用一時スクリプト)
#r "src/bin/Debug/net8.0/WwHdl.dll"
open WwHdl
open WwHdl.Domain
open WwHdl.WireLevel
open WwHdl.PipelineWL
open System.IO

let name = fsi.CommandLineArgs |> Array.tryItem 1 |> Option.defaultValue "sm83_min"
let pitches = [ (24, 16); (20, 14); (16, 12); (12, 10) ]

let src = File.ReadAllText (sprintf "verilog/%s.json" name)

for (px, py) in pitches do
    let sw = System.Diagnostics.Stopwatch.StartNew()
    match compileWLWithPitch px py src with
    | Error e ->
        printfn "%-12s [%dx%d] FAIL %A (%d ms)" name px py e sw.ElapsedMilliseconds
    | Ok (grid, placed, _) ->
        sw.Stop()
        let gateCells = placed |> List.length
        let wc, cc = ref 0, ref 0
        for kv in grid do
            match kv.Value with LWire _ -> incr wc | Cross _ -> incr cc | _ -> ()
        let gridW = (grid |> Seq.map (fun kv -> kv.Key.X) |> Seq.max)+1
        let gridH = (grid |> Seq.map (fun kv -> kv.Key.Y) |> Seq.max)+1
        let crossRatio = if wc.Value+cc.Value>0 then float cc.Value/float(wc.Value+cc.Value)*100.0 else 0.0
        printfn "%-12s [%dx%d] %d gates grid=%dx%d area=%d cross=%.1f%% %dms"
            name px py gateCells gridW gridH (gridW*gridH) crossRatio sw.ElapsedMilliseconds
