#r "bin/Debug/net8.0/WwHdl.dll"
// MeasureSpill.fsx — 完走グリッドが配置 bbox の外に何割のセルを散らしたかを実測する
//
// 使い方:
//   dotnet fsi src/MeasureSpill.fsx <circuit> [--pitch X Y]
//     <circuit>  verilog/<circuit>.json (配置) と routed/<circuit>.bin (実績) を突き合わせる
//     --pitch    完走時の配置ピッチ (省略時は placeWL の自動決定)。口径を実績に合わせるため必須寄り
//     --rowmajor 行優先配置で再現する (ヒルベルト以前の旧方式。sm83_subset 完走時はこちら)
//
// 目的: AnalyzePlacement.fsx の「面積比 (bbox)」が過大評価かどうかを検証する。
//   ルータは bbox 外の探索マージン (最大 ±960) にも経路を伸ばせるため、
//   完走グリッドが bbox 外に散らせる割合 = マージン拡張の実効寄与分。
//   これを知らず bbox 固定で需要を数えると「面積不足」を過大評価する。
//
// 終了コード: 0=成功 / 1=失敗 / 2=引数エラー
open System
open System.IO
open System.Text.Json
open WwHdl
open WwHdl.Domain
open WwHdl.Netlist
open WwHdl.WireLevel
open WwHdl.PipelineWL

let repoRoot = Path.GetFullPath (Path.Combine (__SOURCE_DIRECTORY__, ".."))

let analyze (circuit: string) (pitch: (int * int) option) (rowMajor: bool) : int =
    let srcPath = Path.Combine (repoRoot, "verilog", circuit + ".json")
    let binPath = Path.Combine (repoRoot, "routed", circuit + ".bin")
    let metaPath = Path.Combine (repoRoot, "routed", circuit + ".meta.json")
    if not (File.Exists srcPath) then eprintfn "ERROR: %s がない" srcPath; 2
    elif not (File.Exists binPath) then eprintfn "ERROR: %s がない" binPath; 2
    elif not (File.Exists metaPath) then eprintfn "ERROR: %s がない" metaPath; 2
    else
        let json = File.ReadAllText srcPath
        match Pipeline.frontend json with
        | Error e -> eprintfn "FRONTEND ERROR: %A" e; 1
        | Ok nl ->
            let placed, pins =
                if rowMajor then
                    // 旧 placeWL (行優先): ゲート i → (i % ncols, i / ncols)。ncols = ceil(sqrt n)。
                    // subset 完走時 (commit 614625e) の配置を再現する。ピッチは --pitch を使う。
                    let px, py = (defaultArg pitch (20, 14))
                    let n = nl.Gates.Length
                    let ncols = int (ceil (sqrt (float n)))
                    let placed =
                        nl.Gates
                        |> List.mapi (fun i g ->
                            { Gate = g
                              Coord = { X = 12 + (i % ncols) * px; Y = 2 + (i / ncols) * py }
                              Dir = E })
                    // 外部入力ピンは左端列 (旧 placeWL と同じく X=0) に縦に並べる
                    let pins =
                        nl.PrimaryInputs
                        |> List.mapi (fun i netId -> netId, { X = 0; Y = 2 + i * py })
                        |> Map.ofList
                    placed, pins
                else
                    match pitch with
                    | Some (px, py) -> PipelineWL.placeWLWithPitch px py nl
                    | None -> PipelineWL.placeWL nl
            let placedCoords = (placed |> List.map (fun p -> p.Coord)) @ (pins |> Map.toList |> List.map snd)
            let bx0 = (placedCoords |> List.map (fun c -> c.X) |> List.min)
            let bx1 = (placedCoords |> List.map (fun c -> c.X) |> List.max)
            let by0 = (placedCoords |> List.map (fun c -> c.Y) |> List.min)
            let by1 = (placedCoords |> List.map (fun c -> c.Y) |> List.max)

            let doc = JsonDocument.Parse (File.ReadAllText metaPath)
            let meta = doc.RootElement
            let originX = meta.GetProperty("origin").GetProperty("x").GetInt32()
            let originY = meta.GetProperty("origin").GetProperty("y").GetInt32()

            let bytes = File.ReadAllBytes binPath
            let w = int bytes.[0] ||| (int bytes.[1] <<< 8) ||| (int bytes.[2] <<< 16) ||| (int bytes.[3] <<< 24)
            let h = int bytes.[4] ||| (int bytes.[5] <<< 8) ||| (int bytes.[6] <<< 16) ||| (int bytes.[7] <<< 24)

            let mutable inside = 0
            let mutable outside = 0
            let mutable gx0 = Int32.MaxValue
            let mutable gx1 = Int32.MinValue
            let mutable gy0 = Int32.MaxValue
            let mutable gy1 = Int32.MinValue
            for y in 0 .. h - 1 do
                for x in 0 .. w - 1 do
                    if bytes.[8 + y * w + x] <> 0uy then
                        let ax = originX + x
                        let ay = originY + y
                        if ax < gx0 then gx0 <- ax
                        if ax > gx1 then gx1 <- ax
                        if ay < gy0 then gy0 <- ay
                        if ay > gy1 then gy1 <- ay
                        if ax >= bx0 && ax <= bx1 && ay >= by0 && ay <= by1 then inside <- inside + 1
                        else outside <- outside + 1
            let total = inside + outside
            let bboxArea = int64 (bx1 - bx0 + 1) * int64 (by1 - by0 + 1)
            let gridArea = int64 w * int64 h
            printfn ""
            printfn "=== %s スピル実測 ===" circuit
            printfn "配置 bbox         : x %d..%d, y %d..%d = %s セル" bx0 bx1 by0 by1 (bboxArea.ToString "N0")
            printfn "実グリッド        : x %d..%d, y %d..%d = %s セル (%d x %d)"
                gx0 gx1 gy0 gy1 (gridArea.ToString "N0") w h
            printfn "非空セル          : %s (内 bbox 内 %s / bbox 外 %s)"
                (total.ToString "N0") (inside.ToString "N0") (outside.ToString "N0")
            printfn "★ スピル率        : %.1f%% (bbox 外のセル割合)" (100.0 * float outside / float total)
            printfn "  bbox 内密度     : %.3f" (float inside / float bboxArea)
            printfn "  グリッド全体密度: %.3f" (float total / float gridArea)
            printfn "bbox → 実grid    : %.3f 倍" (float gridArea / float bboxArea)
            0

let args = fsi.CommandLineArgs |> Array.skip 1 |> Array.toList

let rec parse (circuit: string option) (pitch: (int * int) option) (rowMajor: bool) (rest: string list) =
    match rest with
    | [] -> (circuit, pitch, rowMajor)
    | "--pitch" :: x :: y :: tail ->
        parse circuit (Some (int x, int y)) rowMajor tail
    | "--rowmajor" :: tail -> parse circuit pitch true tail
    | name :: tail when circuit.IsNone -> parse (Some name) pitch rowMajor tail
    | _ -> (None, None, false)

let circuit, pitch, rowMajor = parse None None false args

match circuit with
| None ->
    eprintfn "使い方: dotnet fsi src/MeasureSpill.fsx <circuit> [--pitch X Y] [--rowmajor]"
    exit 2
| Some c -> exit (analyze c pitch rowMajor)
