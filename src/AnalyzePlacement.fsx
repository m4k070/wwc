#r "bin/Debug/net8.0/WwHdl.dll"
// AnalyzePlacement.fsx — 配置の「配線密度」を配線前に見積もる分析スクリプト
//
// 使い方:
//   dotnet fsi src/AnalyzePlacement.fsx <circuit> [--pitch X Y] [--margin N] [--lcut N]
//                                       [--place rowmajor|anneal] [--moves N] [--seed N]
//                                       [--backward P] [--t0 T] [--t1 T]
//     <circuit>  verilog/<circuit>.json を読む (例: sm83_subset)
//     --pitch    配置ピッチを固定 (省略時は PipelineWL.placeWL の自動決定)
//     --place    配置戦略 (既定 rowmajor)。anneal = アーク距離のアニーリング最適化
//     --moves / --seed / --backward / --t0 / --t1
//                anneal のパラメータ (既定は GatePlacement.defaultAnnealConfig)
//     --margin   切断密度の容量に数える探索マージン (既定 960 = PipelineWL の上限)
//     --lcut     ローカル切断密度 (tile 境界跨越) のタイル辺長。例: 64 (省略時は出さない)
//     --tile     タイル別需要予測 (L字直接配線) も出す。例: 64 (省略時は出さない)
//
// 目的: 配線失敗の原因が「配置の質 (= 無駄に長い配線)」か「面積不足」かを切り分ける。
//   1) 総 HPWL / 総アーク距離 / 面積 → グローバル密度 (概算)
//   2) 切断密度 (cut density) → 経路に依存しない厳密な下界。需要 > 容量なら配線不能が証明される。
//
// 終了コード: 0=分析成功 / 1=読込またはフロントエンドの失敗 / 2=引数エラー
open System
open System.IO
open WwHdl
open WwHdl.Netlist
open WwHdl.Domain

/// 「長いネット」と見なす閾値 (セル)。この本数・割合が配置の質の指標になる。
let LongNetThresholdCells = 1000

/// HPWL に対する実配線長の経験的な膨張率の目安 (迂回・rip-up 分)。報告の補助にのみ使う。
let TypicalDetourFactor = 1.3

/// アーク距離 → 実セグメント需要 (Wire + 2×Cross) の換算係数。
/// 唯一の完走実績 routed/sm83_subset.bin からのキャリブレーション:
///   総アーク距離 1,353,484 → 実測セグメント需要 1,407,487 = 1.040 倍
/// 1 サンプルのみなので実測 (MeasureRouted.fsx) を追加するたび更新する。
let SegmentNeedFactor = 1.040

/// 完走グリッドの実占用密度の実績 (routed/sm83_subset.bin)。
/// 面積比 (需要/面積) がこの値を大きく超える配置は、完走した実績と同じ詰まり方をする。
let RoutedOccupancyReference = 0.7987

/// 完走グリッドの需要/面積の実績: セグメント需要 1,407,487 / 面積 1,347,822 = 1.044。
/// この値を超える配置は「subset より詰まったグリッド」になる。
let RoutedNeedRatio = 1407487.0 / 1347822.0

/// CLI で選ぶ配置戦略。anneal のパラメータは個別オプションで上書きする。
type PlaceChoice =
    | PlaceRowMajor
    | PlaceAnneal

type Options =
    { Circuit: string
      Pitch: (int * int) option
      Place: PlaceChoice
      Anneal: GatePlacement.AnnealConfig
      Margin: int
      Lcut: int option
      Tile: int option }

/// ネット 1 本の配線長見積もり。
type NetMetrics =
    { Net: NetId
      TerminalCount: int
      Hpwl: int }

/// 配置の「配線密度」を配線前に見積もる分析スクリプトの2本目の指標。
/// 既存記録 (TODO.md の「推定総配線長 1,297 万」など、旧 PlaceCompare.fsx) と同じ
/// アーク毎距離合計。HPWL はネット単位の下界、こちらはドライバ→各入力ピンの
/// マンハッタン距離を全アークで足し上げた値で、ファンアウト分だけ重複数える。

let repoRoot = Path.GetFullPath (Path.Combine (__SOURCE_DIRECTORY__, ".."))

let parseArgs (args: string list) : Result<Options, string> =
    let rec go (opts: Options) (rest: string list) =
        match rest with
        | [] -> Ok opts
        | "--pitch" :: x :: y :: tail ->
            match Int32.TryParse x, Int32.TryParse y with
            | (true, px), (true, py) when px > 0 && py > 0 -> go { opts with Pitch = Some (px, py) } tail
            | _ -> Error (sprintf "--pitch には正の整数が 2 つ必要: %s %s" x y)
        | "--pitch" :: rest -> Error (sprintf "--pitch の引数が足りない: %s" (String.concat " " rest))
        | "--margin" :: n :: tail ->
            match Int32.TryParse n with
            | true, v when v >= 0 -> go { opts with Margin = v } tail
            | _ -> Error (sprintf "--margin には 0 以上の整数が必要: %s" n)
        | "--margin" :: rest -> Error "--margin の引数が足りない"
        | "--lcut" :: n :: tail ->
            match Int32.TryParse n with
            | true, v when v > 0 -> go { opts with Lcut = Some v } tail
            | _ -> Error (sprintf "--lcut には正の整数が必要: %s" n)
        | "--lcut" :: rest -> Error "--lcut の引数が足りない"
        | "--tile" :: n :: tail ->
            match Int32.TryParse n with
            | true, v when v > 0 -> go { opts with Tile = Some v } tail
            | _ -> Error (sprintf "--tile には正の整数が必要: %s" n)
        | "--tile" :: rest -> Error "--tile の引数が足りない"
        | "--place" :: "rowmajor" :: tail -> go { opts with Place = PlaceRowMajor } tail
        | "--place" :: "anneal" :: tail -> go { opts with Place = PlaceAnneal } tail
        | "--place" :: v :: _ -> Error (sprintf "--place は rowmajor|anneal: %s" v)
        | "--moves" :: n :: tail ->
            match Int32.TryParse n with
            | true, v when v >= 0 -> go { opts with Anneal = { opts.Anneal with Moves = v } } tail
            | _ -> Error (sprintf "--moves には 0 以上の整数が必要: %s" n)
        | "--seed" :: n :: tail ->
            match UInt64.TryParse n with
            | true, v -> go { opts with Anneal = { opts.Anneal with Seed = v } } tail
            | _ -> Error (sprintf "--seed には 0 以上の整数が必要: %s" n)
        | "--backward" :: p :: tail ->
            match Double.TryParse (p, Globalization.NumberStyles.Float, Globalization.CultureInfo.InvariantCulture) with
            | true, v when v >= 0.0 -> go { opts with Anneal = { opts.Anneal with BackwardPenalty = v } } tail
            | _ -> Error (sprintf "--backward には 0 以上の数が必要: %s" p)
        | "--t0" :: t :: tail ->
            match Double.TryParse (t, Globalization.NumberStyles.Float, Globalization.CultureInfo.InvariantCulture) with
            | true, v when v > 0.0 -> go { opts with Anneal = { opts.Anneal with InitialTemperature = v } } tail
            | _ -> Error (sprintf "--t0 には正の数が必要: %s" t)
        | "--t1" :: t :: tail ->
            match Double.TryParse (t, Globalization.NumberStyles.Float, Globalization.CultureInfo.InvariantCulture) with
            | true, v when v > 0.0 -> go { opts with Anneal = { opts.Anneal with FinalTemperature = v } } tail
            | _ -> Error (sprintf "--t1 には正の数が必要: %s" t)
        | ("--place" | "--moves" | "--seed" | "--backward" | "--t0" | "--t1") as o :: [] ->
            Error (sprintf "%s の引数が足りない" o)
        | arg :: _ when arg.StartsWith "--" -> Error (sprintf "不明なオプション: %s" arg)
        | name :: tail when opts.Circuit = "" -> go { opts with Circuit = name } tail
        | extra :: _ -> Error (sprintf "余分な引数: %s" extra)
    let defaults =
        { Circuit = ""; Pitch = None; Place = PlaceRowMajor; Anneal = GatePlacement.defaultAnnealConfig
          Margin = 960; Lcut = None; Tile = None }
    match go defaults args with
    | Ok opts when opts.Circuit = "" -> Error "回路名が必要 (例: sm83_subset)"
    | result -> result

/// ネット → 端子座標リスト。
/// 端子の定義: (1) そのネットを出力するゲートの座標 (2) そのネットを入力に持つゲートの座標
///             (3) そのネットが PrimaryInput なら対応するピンの座標
/// 同一ゲートが同じネットを 2 回入力に取る場合も、端子としては 2 個と数える (配線作業量に比例するため)。
let collectNetTerminals
    (placed: PipelineWL.WlPlaced list)
    (pins: Map<NetId, Coord>)
    : Map<NetId, Coord list> =
    let addTerminal (acc: Map<NetId, Coord list>) (net: NetId) (coord: Coord) =
        let existing = acc |> Map.tryFind net |> Option.defaultValue []
        acc |> Map.add net (coord :: existing)
    let fromGates =
        placed
        |> List.fold (fun acc p ->
            let withOutput = addTerminal acc p.Gate.Output p.Coord
            p.Gate.Inputs |> List.fold (fun a net -> addTerminal a net p.Coord) withOutput)
            Map.empty
    pins |> Map.fold (fun acc net coord -> addTerminal acc net coord) fromGates

/// bounding box の半周長。端子が 1 個以下なら 0。
let halfPerimeterWireLength (coords: Coord list) : int =
    match coords with
    | [] | [ _ ] -> 0
    | _ ->
        let xs = coords |> List.map (fun c -> c.X)
        let ys = coords |> List.map (fun c -> c.Y)
        (List.max xs - List.min xs) + (List.max ys - List.min ys)

/// アーク毎距離合計 (既存記録の「推定総配線長」と同じ定義)。
/// 各ゲートの入力ネットについて、そのネットのドライバゲートとのマンハッタン距離を数える。
/// 外部入力ピン由来のネット (ドライバがゲートでないもの) は数えない。
let arcLengthSum (placed: PipelineWL.WlPlaced list) : Result<int64 * int, string> =
    match placed with
    | [] -> Error "配置ゲートが 1 つもない"
    | _ ->
        let driverOf = placed |> List.map (fun p -> p.Gate.Output, p.Coord) |> Map.ofList
        let mutable total = 0L
        let mutable arcs = 0
        for p in placed do
            for net in p.Gate.Inputs do
                match Map.tryFind net driverOf with
                | Some src when src <> p.Coord ->
                    total <- total + int64 (abs (src.X - p.Coord.X) + abs (src.Y - p.Coord.Y))
                    arcs <- arcs + 1
                | _ -> ()
        Ok (total, arcs)

let measureNets (terminals: Map<NetId, Coord list>) : NetMetrics list =
    terminals
    |> Map.toList
    |> List.map (fun (net, coords) ->
        { Net = net
          TerminalCount = List.length coords
          Hpwl = halfPerimeterWireLength coords })

/// 配置された全座標 (ゲート + ピン) を囲む矩形の幅・高さ。
/// 実際の配線グリッドはここに配線用マージンが加わるため、これは面積の下界にあたる。
let placementBoundingBox (placed: PipelineWL.WlPlaced list) (pins: Map<NetId, Coord>) : Result<int * int, string> =
    let coords =
        (placed |> List.map (fun p -> p.Coord)) @ (pins |> Map.toList |> List.map snd)
    match coords with
    | [] -> Error "配置座標が 1 つもない (ゲートも外部入力ピンもない)"
    | _ ->
        let xs = coords |> List.map (fun c -> c.X)
        let ys = coords |> List.map (fun c -> c.Y)
        let width = List.max xs - List.min xs + 1
        let height = List.max ys - List.min ys + 1
        Ok (width, height)

/// 配置座標から実効ピッチを逆算する (格子配置なので隣接する座標の最小差がピッチ)。
/// --pitch 省略時 (placeWL の自動決定) でも正規化指標を出せるようにするため。
let inferPitch (placed: PipelineWL.WlPlaced list) : Result<int * int, string> =
    let minimumGap (values: int list) =
        let sorted = values |> List.distinct |> List.sort
        match sorted with
        | [] | [ _ ] -> None
        | _ -> sorted |> List.pairwise |> List.map (fun (a, b) -> b - a) |> List.min |> Some
    let xs = placed |> List.map (fun p -> p.Coord.X)
    let ys = placed |> List.map (fun p -> p.Coord.Y)
    match minimumGap xs, minimumGap ys with
    | Some px, Some py -> Ok (px, py)
    | _ -> Error "ゲートが 1 行または 1 列しかなく、ピッチを逆算できない"

let median (sorted: int list) : float =
    match sorted with
    | [] -> 0.0
    | _ ->
        let arr = List.toArray sorted
        let n = arr.Length
        if n % 2 = 1 then float arr.[n / 2]
        else (float arr.[n / 2 - 1] + float arr.[n / 2]) / 2.0

/// 切断密度 (cut density) — 経路に依存しない厳密な配線可能性の下界。
///
/// 任意の垂直切断線 x=c について、「端子が c の両側にあるネット」はすべて c を
/// 横切らざるを得ない (マンハッタン経路なら必ず 1 セルを使う)。よって
///   需要 D(c) = bbox が c を跨ぐネット数
///   容量 C(c) = その線上で使えるセル数 = 配置領域の高さ + 上下マージン分 (経路は
///              マージン内でだけ拡張できる) + タップ/ピン占有分の調整
/// に対して D(c) > C(c) となる線が 1 本でもあれば、その配置では配線不能。
/// 水平線についても同様。
///
/// マージン既定 960 は PipelineWL の探索上限 (margin = 60 * exploreMult, mult<=16)。
let cutDensity
    (terminals: Map<NetId, Coord list>)
    (clockNet: NetId option)
    (margin: int)
    : Result<((int * int) * int) * ((int * int) * int) * int, string> =
    if Map.isEmpty terminals then Error "ネットが 1 つもない"
    else
        let bboxes =
            terminals
            |> Map.toList
            |> List.map (fun (_, cs) ->
                let xs = cs |> List.map (fun c -> c.X)
                let ys = cs |> List.map (fun c -> c.Y)
                (List.min xs, List.max xs, List.min ys, List.max ys))
        let minX = bboxes |> List.map (fun (a, _, _, _) -> a) |> List.min
        let maxX = bboxes |> List.map (fun (_, b, _, _) -> b) |> List.max
        let minY = bboxes |> List.map (fun (_, _, a, _) -> a) |> List.min
        let maxY = bboxes |> List.map (fun (_, _, _, b) -> b) |> List.max
        // 各ネットの bbox 中身を差分配列で累積: 跨ぎ線は (minX, maxX] の整数線
        let vertical = Array.zeroCreate (maxX - minX + 2)
        let horizontal = Array.zeroCreate (maxY - minY + 2)
        for (x0, x1, y0, y1) in bboxes do
            if x1 > x0 then
                vertical.[x0 - minX + 1] <- vertical.[x0 - minX + 1] + 1
                vertical.[x1 - minX + 1] <- vertical.[x1 - minX + 1] - 1
            if y1 > y0 then
                horizontal.[y0 - minY + 1] <- horizontal.[y0 - minY + 1] + 1
                horizontal.[y1 - minY + 1] <- horizontal.[y1 - minY + 1] - 1
        let scan (diff: int[]) (offset: int) (capacity: int) : (int * int) * int =
            let mutable d = 0
            let mutable worst = 0
            let mutable worstAt = 0
            let mutable over = 0
            for i in 0 .. diff.Length - 2 do
                d <- d + diff.[i]
                if d > capacity then over <- over + 1
                if d > worst then
                    worst <- d
                    worstAt <- offset + i
            ((worst, worstAt), over)
        // 垂直線: 長さ = 配置高 + 上下マージン、水平線: 長さ = 配置幅 + 左右マージン
        let hCap = (maxY - minY + 1) + 2 * margin
        let vCap = (maxX - minX + 1) + 2 * margin
        let vRes = scan vertical minX hCap
        let hRes = scan horizontal minY vCap
        Ok (vRes, hRes, max hCap vCap)

/// ローカル切断密度 (tile 境界跨越需要) — グローバル切断より一段細かい経路独立の下界。
///
/// 各 tile (k×k) について、「tile 内に端子があり、かつ tile 外にも端子があるネット」は
/// tile の境界を必ず跨ぐ → 需要 D(t) = そのようなネット数。
/// 容量 C(t) = 境界 4 辺のセル数 ≈ 4k (境界セルは 1 セルに 1 本、Cross で最大 2 本)。
/// D(t) > C(t) の tile が在れば、その tile 周辺は輻輳で配線不能 (経路に依存しない下界)。
///
/// 帰結の読み方: 完走実績のある回路 (sm83_subset) で測った最悪比を「実現可能域の上限」とし、
/// 失敗する回路 (sm83_full) の最悪比と比べる。グローバル切断 (cutDensity) が余裕でも
/// こちらだけ高ければ、壁はローカル混雑である。
let localCutDensity
    (terminals: Map<NetId, Coord list>)
    (clockNet: NetId option)
    (tile: int)
    : Result<((int * (int * int)) * (int * int)) * (int * int), string> =
    if Map.isEmpty terminals then Error "ネットが 1 つもない"
    else
        let all = terminals |> Map.toList |> List.map snd
        let minX = all |> List.map (fun cs -> cs |> List.map (fun c -> c.X) |> List.min) |> List.min
        let minY = all |> List.map (fun cs -> cs |> List.map (fun c -> c.Y) |> List.min) |> List.min
        let maxX = all |> List.map (fun cs -> cs |> List.map (fun c -> c.X) |> List.max) |> List.max
        let maxY = all |> List.map (fun cs -> cs |> List.map (fun c -> c.Y) |> List.max) |> List.max
        let tw = (maxX - minX) / tile + 1
        let th = (maxY - minY) / tile + 1
        let demand = Array.zeroCreate (tw * th)
        let capacity = 4 * tile
        let mutable withClockExtra = 0
        for (net, cs) in (terminals |> Map.toList) do
            let isClock = clockNet = Some net
            let tiles =
                cs
                |> List.map (fun c -> ((c.Y - minY) / tile) * tw + ((c.X - minX) / tile))
                |> List.distinct
            if tiles.Length >= 2 then
                for t in tiles do
                    demand.[t] <- demand.[t] + 1
                    if isClock then withClockExtra <- withClockExtra + 1
        let sorted = demand |> Array.sort
        let worstIdx = Array.maxBy id demand
        let worstTile = (worstIdx % tw, worstIdx / tw)
        let overCount = demand |> Array.filter (fun d -> d > capacity) |> Array.length
        Ok (((sorted.[sorted.Length - 1], worstTile), (overCount, demand.Length)), (withClockExtra, capacity))

/// タイル別のローカル需要を「L字直接配線 (横→縦)」で見積もる (配線前に出せる唯一の分布予測)。
/// 各アークを driver から sink へ L 字に歩かせ、通ったセルをタイルに加算する。
/// 実配線は Cross 共有・迂回・ステム共有があるため過大に出る。実測 (MeasureRouted.fsx) と
/// 比べるときも同じ換算を通す: subset で L字中央値 1.33 → 実測 0.85 (係数 ~0.64)。
/// 用途: full の予測分布 × 0.64 が subset の実測分布を超えるかで「ローカル混雑」仮説を切り分ける。
let localTileDemand (terminals: Map<NetId, Coord list>) (tile: int) : Result<int[], string> =
    if Map.isEmpty terminals then Error "ネットが 1 つもない"
    else
        let all = terminals |> Map.toList |> List.map snd
        let minX = all |> List.map (fun cs -> cs |> List.map (fun c -> c.X) |> List.min) |> List.min
        let minY = all |> List.map (fun cs -> cs |> List.map (fun c -> c.Y) |> List.min) |> List.min
        let maxX = all |> List.map (fun cs -> cs |> List.map (fun c -> c.X) |> List.max) |> List.max
        let maxY = all |> List.map (fun cs -> cs |> List.map (fun c -> c.Y) |> List.max) |> List.max
        let tw = (maxX - minX) / tile + 1
        let th = (maxY - minY) / tile + 1
        let counts = Array.zeroCreate (tw * th)
        let bump (x: int) (y: int) =
            let tj = (x - minX) / tile
            let ti = (y - minY) / tile
            if tj >= 0 && tj < tw && ti >= 0 && ti < th then counts.[ti * tw + tj] <- counts.[ti * tw + tj] + 1
        for (_, cs) in (terminals |> Map.toList) do
            match cs with
            | [] | [ _ ] -> ()
            | _ ->
                let xs = cs |> List.map (fun c -> c.X)
                let ys = cs |> List.map (fun c -> c.Y)
                let x0 = List.min xs
                let x1 = List.max xs
                let y0 = List.min ys
                let y1 = List.max ys
                // ネットの spanning L 字 (bbox 半周長ぶん) を代表端子位置から歩かせる
                let sx = x0
                let sy = y0
                let mutable x = sx
                while x <= x1 do bump x sy; x <- x + 1
                let mutable y = sy
                while y <= y1 do bump sx y; y <- y + 1
        Ok counts

let report (circuit: string) (pitchLabel: string) (width: int) (height: int) (metrics: NetMetrics list) (clockNet: NetId option) (arcTotal: int64) (arcCount: int) (cuts: (((int * int) * int) * ((int * int) * int) * int) option) (margin: int) (localCut: (((int * (int * int)) * (int * int)) * (int * int)) option) (tile: int) (tileDemand: int[] option) =
    let isClock (m: NetMetrics) =
        match clockNet with
        | Some clk -> m.Net = clk
        | None -> false
    let clockMetrics = metrics |> List.filter isClock
    let signalMetrics = metrics |> List.filter (isClock >> not)
    let area = int64 width * int64 height
    let signalHpwl = signalMetrics |> List.sumBy (fun m -> int64 m.Hpwl)
    let clockHpwl = clockMetrics |> List.sumBy (fun m -> int64 m.Hpwl)
    let terminalCount = metrics |> List.sumBy (fun m -> m.TerminalCount)
    let lengths = signalMetrics |> List.map (fun m -> m.Hpwl) |> List.sort
    let longNets = lengths |> List.filter (fun len -> len > LongNetThresholdCells)
    let density = float signalHpwl / float area
    let densityWithClock = float (signalHpwl + clockHpwl) / float area

    printfn ""
    printfn "=== %s (pitch %s) ===" circuit pitchLabel
    printfn "グリッド面積        : %d x %d = %s セル (配置 bbox)" width height (area.ToString "N0")
    printfn "ネット数            : %d (信号 %d / クロック %d)" metrics.Length signalMetrics.Length clockMetrics.Length
    printfn "端子数              : %s" (terminalCount.ToString "N0")
    printfn "総 HPWL (信号のみ)  : %s セル" (signalHpwl.ToString "N0")
    printfn "クロックネット HPWL : %s セル" (clockHpwl.ToString "N0")
    printfn "総アーク距離        : %s セル (%s アーク、既存記録と同じ定義)" (arcTotal.ToString "N0") (arcCount.ToString "N0")
    let segmentNeed = int64 (float arcTotal * SegmentNeedFactor)
    let needPerArea = float segmentNeed / float area
    // 実配線グリッドは bbox に探索マージン (margin で最大 +2*margin 各辺) を足して拡張される。
    // subset は bbox 77.3万 → 実 134.7万 (1.74 倍) まで広げて完走した。同効果を再現するため両方出す。
    let maxW = int64 (width + 2 * margin)
    let maxH = int64 (height + 2 * margin)
    let maxArea = maxW * maxH
    let needPerMaxArea = float segmentNeed / float maxArea
    printfn "セグメント需要 (予測): %s セル (アーク x%.3f、subset 実測からの換算)" (segmentNeed.ToString "N0") SegmentNeedFactor
    printfn "★ 面積比 (bbox)     : %.3f   (bbox %s セル)" needPerArea (area.ToString "N0")
    printfn "★ 面積比 (拡張上限) : %.3f   (マージン %d で %s セルまで拡張可)" needPerMaxArea margin (maxArea.ToString "N0")
    printfn "  完走実績 subset    : %.3f (実グリッド 134.7万 = bbox の 1.74 倍に拡張して完走)" RoutedNeedRatio
    if needPerMaxArea > RoutedNeedRatio then
        printfn "  → 拡張しても完走実績より %.1f 倍詰まる。面積不足の可能性が高い" (needPerMaxArea / RoutedNeedRatio)
    elif needPerArea > RoutedNeedRatio then
        printfn "  → bbox だけでは不足。マージンへの拡張 (exploreMult が効くか) 次第"
    printfn "★ 配線密度 (信号)   : %.4f  (総 HPWL / 面積)" density
    printfn "  配線密度 (アーク) : %.4f  (総アーク距離 / 面積)" (float arcTotal / float area)
    printfn "  配線密度 (ク込み) : %.4f" densityWithClock
    printfn "  迂回 x%.1f 換算    : %.4f  (実配線長はHPWLより長い)" TypicalDetourFactor (density * TypicalDetourFactor)
    printfn "ネット長分布 (信号, セル):"
    printfn "  中央値            : %.1f" (median lengths)
    printfn "  平均              : %.1f" (if signalMetrics.IsEmpty then 0.0 else float signalHpwl / float signalMetrics.Length)
    printfn "  最大              : %d" (if lengths.IsEmpty then 0 else List.max lengths)
    printfn "  %d セル超        : %d 本 (%.1f%%)"
        LongNetThresholdCells
        longNets.Length
        (if signalMetrics.IsEmpty then 0.0 else 100.0 * float longNets.Length / float signalMetrics.Length)
    match cuts with
    | None -> ()
    | Some (((vWorst, vAt), _), ((hWorst, hAt), _), _) ->
        let vCap = height + 2 * margin
        let hCap = width + 2 * margin
        printfn "切断密度 (マージン %d、経路依存の下界):" margin
        printfn "  垂直線 最悪      : %d / 容量 %d = %.3f  (x=%d)" vWorst vCap (float vWorst / float vCap) vAt
        printfn "  水平線 最悪      : %d / 容量 %d = %.3f  (y=%d)" hWorst hCap (float hWorst / float hCap) hAt
        let verdict (d: int) (c: int) = if d > c then "超過 → 配線不能" else "許容内"
        printfn "  判定 (垂直)      : %s" (verdict vWorst vCap)
        printfn "  判定 (水平)      : %s" (verdict hWorst hCap)
    match localCut with
    | None -> ()
    | Some (((worst, worstTile), (overCount, tileCount)), (_, capacity)) ->
        printfn "ローカル切断密度 (tile %d 境界跨越、経路独立の下界):" tile
        printfn "  容量 (境界 4 边) : %d セル/tile" capacity
        printfn "  最悪 tile        : %d 需要 = %.2f 倍  (tile x=%d y=%d)"
            worst (float worst / float capacity) (fst worstTile) (snd worstTile)
        printfn "  容量超過 tile    : %d / %d" overCount tileCount
        printfn "  判定             : %s" (if overCount > 0 then "超過 → ローカル輻輳 (面積不足とは別要因)" else "許容内 (グローバル切断と同結論)")
    match tileDemand with
    | None -> ()
    | Some counts ->
        let tileArea = tile * tile
        let sorted = counts |> Array.sort
        let pct (p: float) = float sorted.[min (sorted.Length - 1) (int (float sorted.Length * p))] / float tileArea
        let pmax = float sorted.[sorted.Length - 1] / float tileArea
        printfn "ローカル需要予測 (L字直接配線, tile %d, %d 枚):" tile counts.Length
        printfn "  中央値 %.3f / 90%% %.3f / 99%% %.3f / 最大 %.3f  (要/面積)"
            (pct 0.5) (pct 0.9) (pct 0.99) pmax
        printfn "  参考: subset 実測 中央値 0.854 / 90%% 0.938 / 99%% 0.990 / 最大 1.000"

let analyze (opts: Options) : int =
    let srcPath = Path.Combine (repoRoot, "verilog", opts.Circuit + ".json")
    if not (File.Exists srcPath) then
        eprintfn "ERROR: %s がない" srcPath
        2
    else
        let json = File.ReadAllText srcPath
        match Pipeline.frontend json with
        | Error e ->
            eprintfn "FRONTEND ERROR: %A" e
            1
        | Ok nl ->
            let px, py =
                match opts.Pitch with
                | Some p -> p
                | None ->
                    // 自動ピッチは placeWL の決定をそのまま使う (配置結果から逆算)
                    match inferPitch (fst (PipelineWL.placeWL nl)) with
                    | Ok p -> p
                    | Error msg -> failwithf "PITCH ERROR: %s" msg
            let strategy =
                match opts.Place with
                | PlaceRowMajor -> GatePlacement.RowMajor
                | PlaceAnneal -> GatePlacement.Annealed opts.Anneal
            let sw = Diagnostics.Stopwatch.StartNew ()
            match PipelineWL.placeWLWithStrategy strategy px py nl with
            | Error e ->
                eprintfn "PLACEMENT ERROR: %A" e
                1
            | Ok placement ->
            let elapsed = sw.Elapsed.TotalSeconds
            let placed, pins = placement.Placed, placement.Pins
            match placement.Annealing with
            | None -> printfn "[place] rowmajor (%.2f 秒)" elapsed
            | Some o ->
                let c = opts.Anneal
                printfn "[place] anneal: moves=%s seed=%d T0=%g T1=%g backward=%g (%.2f 秒, 受理 %s)"
                    (c.Moves.ToString "N0") c.Seed c.InitialTemperature c.FinalTemperature c.BackwardPenalty
                    elapsed (o.AcceptedMoves.ToString "N0")
                printfn "[place] 最適化コスト (ピン込みアーク距離%s): %s → %s セル (%.1f%%)"
                    (if c.BackwardPenalty > 0.0 then " + 逆行ペナルティ" else "")
                    ((GatePlacement.costToCells o.InitialCost).ToString "N0")
                    ((GatePlacement.costToCells o.BestCost).ToString "N0")
                    (100.0 * float o.BestCost / float (max 1L o.InitialCost))
            match placementBoundingBox placed pins with
            | Error msg ->
                eprintfn "PLACEMENT ERROR: %s" msg
                1
            | Ok (width, height) ->
                let placeLabel =
                    match opts.Place with
                    | PlaceRowMajor -> "rowmajor"
                    | PlaceAnneal -> "anneal"
                let pitchLabel =
                    (opts.Pitch |> Option.map (fun (x, y) -> sprintf "%dx%d" x y) |> Option.defaultValue "auto")
                    + ", " + placeLabel
                let metrics = collectNetTerminals placed pins |> measureNets
                printfn "[analyze] %s: gates=%d (DFF %d), PI=%d, PO=%d"
                    opts.Circuit
                    nl.Gates.Length
                    (nl.Gates |> List.filter (fun g -> g.Kind = Dff) |> List.length)
                    nl.PrimaryInputs.Length
                    nl.PrimaryOutputs.Length
                match arcLengthSum placed with
                | Error msg ->
                    eprintfn "ARC ERROR: %s" msg
                    1
                | Ok (arcTotal, arcCount) ->
                    let terminals = collectNetTerminals placed pins
                    let cuts =
                        match cutDensity terminals nl.ClockNet opts.Margin with
                        | Error msg -> failwithf "CUT ERROR: %s" msg
                        | Ok r -> Some r
                    let localCut =
                        opts.Lcut
                        |> Option.map (fun t ->
                            match localCutDensity terminals nl.ClockNet t with
                            | Error msg -> failwithf "LCUT ERROR: %s" msg
                            | Ok r -> r)
                    let tileDemand =
                        opts.Tile
                        |> Option.map (fun t ->
                            match localTileDemand terminals t with
                            | Error msg -> failwithf "TILE ERROR: %s" msg
                            | Ok r -> r)
                    report opts.Circuit pitchLabel width height metrics nl.ClockNet arcTotal arcCount cuts opts.Margin localCut (defaultArg opts.Lcut 64) tileDemand
                    0

let exitCode =
    match parseArgs (fsi.CommandLineArgs |> Array.toList |> List.tail) with
    | Error msg ->
        eprintfn "ERROR: %s" msg
        eprintfn "使い方: dotnet fsi src/AnalyzePlacement.fsx <circuit> [--pitch X Y] [--margin N] [--lcut N] [--tile N] [--place rowmajor|anneal] [--moves N] [--seed N] [--backward P] [--t0 T] [--t1 T]"
        2
    | Ok opts -> analyze opts

exit exitCode
