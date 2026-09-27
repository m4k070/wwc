#r "bin/Debug/net8.0/WwHdl.dll"
// MeasureRouted.fsx — 配線済みグリッド (routed/<circuit>.bin) の「実占用密度」を測る
//
// 使い方:
//   dotnet fsi src/MeasureRouted.fsx <circuit> [--tile N]
//     <circuit>  routed/<circuit>.bin を読む (例: sm83_subset)
//     --tile     ローカル密度を出すタイル辺長 (既定 64 セル)
//
// 目的: AnalyzePlacement.fsx の「配線前に見積もる密度」を、完走した実績と突き合わせる。
//   実占用密度 = 非空セル数 / (width * height)
//   実配線長   = LWire セル数、セグメント需要 = LWire + 2×Cross (Cross は 2 本が交差)
//               → AnalyzePlacement.fsx の「総アーク距離」と同口径で比較できる
//   ローカル密度 = タイル毎の非空割合。全体平均が低くても特定タイルが埋まれば
//                  そこで輻輳失敗するため、壁の正体 (面積不足 or 局所混雑) の切り分けに使う。
//
// 終了コード: 0=成功 / 1=読込失敗 / 2=引数エラー
open System
open System.IO
open WwHdl
open WwHdl.Domain
open WwHdl.WireLevel

type Options = { Circuit: string; Tile: int }

let repoRoot = Path.GetFullPath (Path.Combine (__SOURCE_DIRECTORY__, ".."))

let parseArgs (args: string list) : Result<Options, string> =
    let rec go (opts: Options) (rest: string list) =
        match rest with
        | [] -> Ok opts
        | "--tile" :: n :: tail ->
            match Int32.TryParse n with
            | true, v when v > 0 -> go { opts with Tile = v } tail
            | _ -> Error (sprintf "--tile には正の整数が必要: %s" n)
        | "--tile" :: rest -> Error "--tile の引数が足りない"
        | arg :: _ when arg.StartsWith "--" -> Error (sprintf "不明なオプション: %s" arg)
        | name :: tail when opts.Circuit = "" -> go { opts with Circuit = name } tail
        | extra :: _ -> Error (sprintf "余分な引数: %s" extra)
    match go { Circuit = ""; Tile = 64 } args with
    | Ok opts when opts.Circuit = "" -> Error "回路名が必要 (例: sm83_subset)"
    | result -> result

let percentile (sorted: int[]) (p: float) : int =
    if sorted.Length = 0 then 0
    else sorted.[min (sorted.Length - 1) (int (float sorted.Length * p))]

let analyze (opts: Options) : int =
    let binPath = Path.Combine (repoRoot, "routed", opts.Circuit + ".bin")
    if not (File.Exists binPath) then
        eprintfn "ERROR: %s がない" binPath
        2
    else
        let bytes = File.ReadAllBytes binPath
        if bytes.Length < 8 then
            eprintfn "ERROR: ファイルが 8 byte 未満"
            1
        else
            let width = int bytes.[0] ||| (int bytes.[1] <<< 8) ||| (int bytes.[2] <<< 16) ||| (int bytes.[3] <<< 24)
            let height = int bytes.[4] ||| (int bytes.[5] <<< 8) ||| (int bytes.[6] <<< 16) ||| (int bytes.[7] <<< 24)
            if width <= 0 || height <= 0 || bytes.Length < 8 + width * height then
                eprintfn "ERROR: ヘッダ不正 (w=%d h=%d, file=%d bytes)" width height bytes.Length
                1
            else
                let mutable nEmpty = 0
                let mutable nPin = 0
                let mutable nWire = 0
                let mutable nNand = 0
                let mutable nCross = 0
                let mutable nDff = 0
                let tw = (width + opts.Tile - 1) / opts.Tile
                let th = (height + opts.Tile - 1) / opts.Tile
                let tileNonEmpty = Array.zeroCreate (tw * th)
                for y in 0 .. height - 1 do
                    for x in 0 .. width - 1 do
                        let b = bytes.[8 + y * width + x]
                        if b <> 0uy then
                            match int (b >>> 5) with
                            | 1 -> nPin <- nPin + 1
                            | 2 -> nWire <- nWire + 1
                            | 3 -> nNand <- nNand + 1
                            | 4 -> nCross <- nCross + 1
                            | 5 -> nDff <- nDff + 1
                            | _ -> nEmpty <- nEmpty + 1
                            let ti = (y / opts.Tile) * tw + (x / opts.Tile)
                            tileNonEmpty.[ti] <- tileNonEmpty.[ti] + 1
                let area = int64 width * int64 height
                let nonEmpty = nPin + nWire + nNand + nCross + nDff
                let tileArea = opts.Tile * opts.Tile
                let sorted = tileNonEmpty |> Array.sort
                printfn ""
                printfn "=== %s 実測 (routed/%s.bin, tile %d) ===" opts.Circuit opts.Circuit opts.Tile
                printfn "グリッド            : %d x %d = %s セル" width height (area.ToString "N0")
                printfn "セル内訳            : Wire %s / Nand %s / Cross %s / Dff %s / Pin %s"
                    (nWire.ToString "N0") (nNand.ToString "N0") (nCross.ToString "N0") (nDff.ToString "N0") (nPin.ToString "N0")
                printfn "★ 実占用密度        : %.4f  (非空 %s / 面積)"
                    (float nonEmpty / float area) (nonEmpty.ToString "N0")
                printfn "  実配線長 (Wire)   : %s セル (Cross %s セルは交差)" (nWire.ToString "N0") (nCross.ToString "N0")
                printfn "  セグメント需要     : %s セル (Wire + 2×Cross。Cross は 2 本が交差するため)"
                    ((nWire + 2 * nCross).ToString "N0")
                printfn "  配線占有率        : %.4f  ((Wire+Cross) / 面積)"
                    (float (nWire + nCross) / float area)
                printfn "ローカル密度 (tile %d, %d 枚):" opts.Tile (tileNonEmpty.Length)
                printfn "  中央値            : %.4f" (float (percentile sorted 0.5) / float tileArea)
                printfn "  90%%              : %.4f" (float (percentile sorted 0.9) / float tileArea)
                printfn "  99%%              : %.4f" (float (percentile sorted 0.99) / float tileArea)
                printfn "  最大              : %.4f" (float sorted.[sorted.Length - 1] / float tileArea)
                printfn "  完全に埋まった枚数: %d / %d" (tileNonEmpty |> Array.filter (fun c -> c >= tileArea) |> Array.length) tileNonEmpty.Length
                0

let exitCode =
    match parseArgs (fsi.CommandLineArgs |> Array.toList |> List.tail) with
    | Error msg ->
        eprintfn "ERROR: %s" msg
        eprintfn "使い方: dotnet fsi src/MeasureRouted.fsx <circuit> [--tile N]"
        2
    | Ok opts -> analyze opts

exit exitCode
