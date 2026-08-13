// pitch_route.fsx — ピッチ指定でルーティング実行 (処理終端数・grid 面積・cross 率を測定)
// 使い方: dotnet fsi pitch_route.fsx <circuit> <pitchX> <pitchY>
#r "src/bin/Debug/net8.0/WwHdl.dll"
open WwHdl
open WwHdl.Domain
open WwHdl.WireLevel
open WwHdl.PipelineWL
open System.IO

let name = fsi.CommandLineArgs |> Array.tryItem 1 |> Option.defaultValue "sm83_subset"
let pitchX = fsi.CommandLineArgs |> Array.tryItem 2 |> Option.map int |> Option.defaultValue 16
let pitchY = fsi.CommandLineArgs |> Array.tryItem 3 |> Option.map int |> Option.defaultValue 12

let sw = System.Diagnostics.Stopwatch()
let src = File.ReadAllText (sprintf "verilog/%s.json" name)
sw.Start()
match compileWLWithPitch pitchX pitchY src with
| Error e -> printfn "FAIL %s [%dx%d] %A (%d ms)" name pitchX pitchY e sw.ElapsedMilliseconds
| Ok (grid, placed, pins) ->
    sw.Stop()
    let gateCells = placed |> List.length
    let wc, cc = ref 0, ref 0
    for kv in grid do
        match kv.Value with LWire _ -> incr wc | Cross _ -> incr cc | _ -> ()
    let gridW = (grid |> Seq.map (fun kv -> kv.Key.X) |> Seq.max) + 1
    let gridH = (grid |> Seq.map (fun kv -> kv.Key.Y) |> Seq.max) + 1
    printfn "OK %s [%dx%d] %d gates grid=%dx%d area=%d cross=%.1f%% %dms"
        name pitchX pitchY gateCells gridW gridH (gridW * gridH)
        (if wc.Value + cc.Value > 0 then float cc.Value / float (wc.Value + cc.Value) * 100.0 else 0.0)
        sw.ElapsedMilliseconds
