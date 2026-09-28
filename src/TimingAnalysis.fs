namespace WwHdl

// ---------------------------------------------------------------------
// TimingAnalysis — 配線済み WireLevel グリッドのクリティカルパス (最長経路) 静的解析
//
// src/AnalyzeTiming.fsx (CLI) の解析部分。HoldAnalysis.fs の DenseGrid / sinksOf
// (pull 関係の順方向グラフ) を土台にする。HoldAnalysis が「最短経路 (hold)」を求めるのに
// 対し、こちらは「収束にかかる最長経路 (setup / 組合せ論理の収束時間)」を求める。
// Issue #7 (c) 「クリティカルパスを考慮した配置」の効果を事前に見積もるための道具。
//
// 3 つの収束窓 (2 相クロック、GPU --trace の 3 区間に対応。AGENTS.md 参照):
//   DataInWindow: 外部入力ピン (data_in など、クロック以外) → 組合せ → マスター D
//   PhaseAWindow: clk_a↑ 後、マスター Q → スレーブ D (直結なので短いはず)
//   PhaseBWindow: clk_b↑ 後、スレーブ Q → 組合せ → マスター D (次周期のデータ経路)
//
// アルゴリズム: 組合せ部分 (Wire/Cross/NAND) は閉路がないはず (DFF が唯一のフィードバック
// 断点であり、D/CLK 入力はこのグラフでは末端 (Sink) なので現れない)。よってトポロジカル順の
// 1 パス DP で最長経路 (セル数) を求められる。閉路が見つかったら Error を返す (握りつぶさない)。
//
// 到達しない先行ノード (この窓では変化しない = 既に安定した入力) は寄与させない。
// 「0 世代とみなして足す」のではなく「経路の候補から除外する」ことに注意 (論理的マスキングは
// 考慮しないが、窓をまたいだ無関係な安定入力までは経路に含めない)。これにより実測
// (GPU --trace の最大値) は静的予測以下になるはず — 破れたら見積りのどこかに誤りがある。
// ---------------------------------------------------------------------
module TimingAnalysis =
    open System.Collections.Generic
    open Domain
    open WireLevel
    open HoldAnalysis
    open RoutedArtifact

    // --- 0. 経路の内訳 ---------------------------------------------------------------

    /// 経路が通過するセルの種別 (内訳集計用)。CombNode のターゲットは HoldAnalysis.sinkOf の
    /// 規則により必ず LWire / LNand / Cross のいずれかになる。
    type ArcKind =
        | WireArc
        | CrossArc
        /// ゲート段 (NAND) を 1 段通過。段数 = 論理段数
        | NandArc

    /// 経路長の内訳 (セル数)。解析対象は数百万ノードになるため struct にしてヒープ確保を避ける。
    [<Struct>]
    type Breakdown =
        { Wire: int
          Cross: int
          Nand: int }
        member b.Total = b.Wire + b.Cross + b.Nand

    let zeroBreakdown : Breakdown = { Wire = 0; Cross = 0; Nand = 0 }

    let private addArc (kind: ArcKind) (b: Breakdown) : Breakdown =
        match kind with
        | WireArc -> { b with Wire = b.Wire + 1 }
        | CrossArc -> { b with Cross = b.Cross + 1 }
        | NandArc -> { b with Nand = b.Nand + 1 }

    let combineBreakdown (a: Breakdown) (b: Breakdown) : Breakdown =
        { Wire = a.Wire + b.Wire; Cross = a.Cross + b.Cross; Nand = a.Nand + b.Nand }

    let private arcKindOfNode (dg: DenseGrid) (node: int) : ArcKind =
        match dg.Cells.[node / ChannelsPerCell] with
        | LNand _ -> NandArc
        | Cross _ -> CrossArc
        | _ -> WireArc

    let private isOccupied (dg: DenseGrid) (node: int) : bool =
        dg.Cells.[node / ChannelsPerCell] <> LEmpty

    // --- 1. 組合せグラフ全体のトポロジカル順 (窓に依らず共有) -------------------------

    /// 組合せグラフに閉路が見つかった (設計上あってはならない — 握りつぶさず報告する)。
    type TimingError =
        | CombinationalCycle of sampleCells: Coord list * stuckCount: int

    let describeTimingError (e: TimingError) : string =
        match e with
        | CombinationalCycle (sample, count) ->
            let coords = sample |> List.map (fun c -> sprintf "(%d,%d)" c.X c.Y) |> String.concat " "
            sprintf "組合せ経路に閉路がある (%d ノードがトポロジカル順で確定しなかった。例: %s)" count coords

    [<Literal>]
    let private CycleSampleSize = 20

    /// 組合せグラフ (Pin / DFF-Q を起点、Wire/Cross/NAND を経由し、DFF の D/CLK 入力を
    /// 末端とするグラフ) 全体のトポロジカル順を Kahn 法で求める。
    let buildTopoOrder (dg: DenseGrid) : Result<int[], TimingError> =
        let nodeCount = dg.Cells.Length * ChannelsPerCell
        let remaining = Array.zeroCreate<int> nodeCount
        let mutable occupiedCount = 0
        for u in 0 .. nodeCount - 1 do
            if isOccupied dg u then
                occupiedCount <- occupiedCount + 1
                for sink in sinksOf dg u do
                    match sink with
                    | CombNode (v, _) -> remaining.[v] <- remaining.[v] + 1
                    | DffData _ | DffClock _ -> ()
        let queue = Queue<int> ()
        for u in 0 .. nodeCount - 1 do
            if isOccupied dg u && remaining.[u] = 0 then queue.Enqueue u
        let order = ResizeArray<int> occupiedCount
        while queue.Count > 0 do
            let u = queue.Dequeue ()
            order.Add u
            for sink in sinksOf dg u do
                match sink with
                | CombNode (v, _) ->
                    remaining.[v] <- remaining.[v] - 1
                    if remaining.[v] = 0 then queue.Enqueue v
                | DffData _ | DffClock _ -> ()
        if order.Count = occupiedCount then
            Ok (order.ToArray ())
        else
            let stuck =
                [ for u in 0 .. nodeCount - 1 do
                    if isOccupied dg u && remaining.[u] > 0 then yield coordOf dg (u / ChannelsPerCell) ]
                |> List.distinct
            Error (CombinationalCycle (List.truncate CycleSampleSize stuck, stuck.Length))

    // --- 2. 窓ごとの最長経路 DP -----------------------------------------------------

    /// 収束を待つ 3 つの窓 (2 相クロック、GPU --trace の 3 区間に対応)。
    type ConvergenceWindow =
        | DataInWindow
        | PhaseAWindow
        | PhaseBWindow

    let windowLabel (w: ConvergenceWindow) : string =
        match w with
        | DataInWindow -> "data_in"
        | PhaseAWindow -> "phaseA"
        | PhaseBWindow -> "phaseB"

    /// ノードごとの最長到達情報。Reached=false は「この窓では変化しない (安定)」を表し、
    /// 後続の DP では寄与させない (0 世代とみなすのではなく、経路の候補から除外する)。
    [<Struct>]
    type ArrivalInfo =
        { Reached: bool
          /// 起点からのセル数 (起点自身は 0)
          Distance: int
          Breakdown: Breakdown
          /// この値の起点ノード (Pin または DFF-Q のセル番号 × ChannelsPerCell)。Reached=false なら無意味
          Origin: int
          /// この値を運んできた直前のノード (経路復元用)。起点自身は自分自身を指す
          /// (「Pred = 自身」を「起点に着いた」の終了条件にする。Reached=false なら無意味)
          Pred: int }

    let private unreached : ArrivalInfo =
        { Reached = false; Distance = 0; Breakdown = zeroBreakdown; Origin = -1; Pred = -1 }

    /// 起点ノード集合から、事前計算したトポロジカル順で最長経路 DP を行う (multi-source longest path)。
    /// 起点でないノードの到達可否は「到達した先行ノードが 1 つでもあるか」で決まる。
    /// 論理的マスキングは考慮しない (安全側の見積もり。HoldAnalysis の最短経路と同じ方針)。
    let longestPathsFrom (dg: DenseGrid) (topo: int[]) (sources: int list) : ArrivalInfo[] =
        let nodeCount = dg.Cells.Length * ChannelsPerCell
        let info = Array.create nodeCount unreached
        for s in sources do
            info.[s] <- { Reached = true; Distance = 0; Breakdown = zeroBreakdown; Origin = s; Pred = s }
        for u in topo do
            let ui = info.[u]
            if ui.Reached then
                for sink in sinksOf dg u do
                    match sink with
                    | CombNode (v, _) ->
                        let candidate = ui.Distance + 1
                        if not info.[v].Reached || candidate > info.[v].Distance then
                            info.[v] <-
                                { Reached = true
                                  Distance = candidate
                                  Breakdown = addArc (arcKindOfNode dg v) ui.Breakdown
                                  Origin = ui.Origin
                                  Pred = u }
                    | DffData _ | DffClock _ -> ()
        info

    // --- 3. DFF の D 入力・出力プローブへの到達 --------------------------------------

    /// DFF (セル番号) → その D 入力として読まれるノード。窓に依らず共有できる構造情報なので
    /// 一度だけ求める (sinksOf 走査の副産物)。
    let buildDffDataNodes (dg: DenseGrid) : Map<int, int> =
        let nodeCount = dg.Cells.Length * ChannelsPerCell
        [ for u in 0 .. nodeCount - 1 do
            if isOccupied dg u then
                for sink in sinksOf dg u do
                    match sink with
                    | DffData dff -> yield dff, u
                    | CombNode _ | DffClock _ -> () ]
        |> Map.ofList

    /// coord にあるセルの「代表ノード」(WireLevel.levelOf と同じ規約: Cross は水平チャネル)。
    let mainNodeAt (dg: DenseGrid) (c: Coord) : int option =
        indexOf dg c |> Option.map (fun idx -> idx * ChannelsPerCell + MainOrHorizontal)

    /// meta の出力プローブのうち、DFF セルを直接指さないもの (組合せ出力)。
    /// DFF を直接指すプローブは Q の到達 (クロック到達 + DffClockToQ) で決まり、
    /// 追加の組合せ遅延がないのでここでは対象外にする (要件の「実質 DFF の D が主」)。
    let combinationalOutputProbes (dg: DenseGrid) (meta: RoutedMeta) : (Coord * string list) list =
        [ for KeyValue (port, probes) in meta.Outputs do
            for (i, probe) in List.indexed probes do
                match probe with
                | CellProbe c ->
                    match indexOf dg c with
                    | Some idx ->
                        match dg.Cells.[idx] with
                        | LDff _ -> ()
                        | _ -> yield c, sprintf "%s[%d]" port i
                    | None -> ()
                | ConstProbe _ | Unobservable _ -> () ]
        |> List.groupBy fst
        |> List.map (fun (c, xs) -> c, xs |> List.map snd)

    /// meta のクロック以外の外部入力ピン座標 (2 相では clk は inputs に含まれず、
    /// TwoPhaseClocking のフィールドに分離されている)。
    let nonClockInputCoords (meta: RoutedMeta) : Coord list =
        meta.Inputs |> Map.toList |> List.collect snd

    // --- 4. 経路の報告 ---------------------------------------------------------------

    /// 経路の終点。DFF なら D 入力、それ以外は出力プローブなど任意の観測点。
    type PathEndTarget =
        | DffD of dff: int
        | ProbePoint of coord: Coord * names: string list

    type CriticalPath =
        { Window: ConvergenceWindow
          /// 起点 (Pin または DFF-Q) のセル座標
          Source: Coord
          Target: PathEndTarget
          /// 経路長 (セル数、クロック到達を含まない)
          PathLength: int
          Breakdown: Breakdown
          /// 起点のクロック到達世代 (DataInWindow は 0)
          OriginClockArrival: int
          /// クロック到達 + 経路長 (この窓の開始からの予測世代数)
          PredictedGen: int }

    /// 窓ごとの解析結果。Paths は PredictedGen の降順。
    type WindowReport =
        { Window: ConvergenceWindow
          Paths: CriticalPath list
          /// クロック網自体の収束にかかる下限 (ClockFloor の説明を参照)。DataInWindow は 0
          ClockFloor: int
          /// クロック到達も含めたこの窓の予測最大世代数 (= max ClockFloor (Paths の PredictedGen))
          PredictedMax: int
          /// この窓の起点ノード集合 (longestPathsFrom への入力)。pathBreakdownsForWindow が
          /// DP をもう一度 (1 回だけ) 走らせてゲート単位の内訳を作るときに使う
          Sources: int list }

    /// クロック網の最大到達世代 (Map が空なら 0)。
    let private maxClockArrival (c: ClockAnalysis) : int =
        c.Arrival |> Map.toList |> List.map snd |> function [] -> 0 | xs -> List.max xs

    /// この窓の「経路が無くても必ずかかる」下限。settle はグリッド全体が不動点に達するまで
    /// 待つので、DFF の Q がどこにも配線されていなくても、クロック網自体 (Wire セル) が
    /// 立ち上がりを伝搬し終える世代 (ClockAnalysis.MaxArrival) + その DFF の Q が確定する
    /// 1 世代 (DffClockToQ) までは変化が続く。dffPaths / probePaths だけでは
    /// 「Q がどこにも読まれない DFF」の寄与が漏れる (reg8 の phaseB で実測 60〜62g に対し
    /// 予測 0g になっていた不具合の原因)。
    let private clockFloorFor (window: ConvergenceWindow) (clockA: ClockAnalysis) (clockB: ClockAnalysis) : int =
        match window with
        | DataInWindow -> 0
        | PhaseAWindow -> maxClockArrival clockA + DffClockToQ
        | PhaseBWindow -> maxClockArrival clockB + DffClockToQ

    let describePathTarget (dg: DenseGrid) (t: PathEndTarget) : string =
        match t with
        | DffD dff ->
            let c = coordOf dg dff
            sprintf "DFF(%d,%d).D" c.X c.Y
        | ProbePoint (c, ns) -> sprintf "probe(%d,%d)[%s]" c.X c.Y (String.concat "," ns)

    /// 全経路の内訳を合算する (「経路長に占める NAND の割合」の分子分母に使う)。
    let totalBreakdown (paths: CriticalPath list) : Breakdown =
        paths |> List.fold (fun acc p -> combineBreakdown acc p.Breakdown) zeroBreakdown

    /// 昇順ソート済み配列から百分位数 (0.0-1.0) を最近傍法で求める。空なら 0。
    let percentile (p: float) (sorted: int[]) : int =
        if sorted.Length = 0 then 0
        else
            let idx = int (round (p * float (sorted.Length - 1)))
            sorted.[max 0 (min (sorted.Length - 1) idx)]

    /// 起点のクロック到達 (窓ごとの規約): DataIn は 0 (ホストが書いた瞬間を基準とする)。
    /// PhaseA/PhaseB は起点 DFF のクロック到達 + DffClockToQ (Q が確定するまでの 1 世代)。
    let private originArrival (window: ConvergenceWindow) (clockA: ClockAnalysis) (clockB: ClockAnalysis) (originCell: int) : int =
        match window with
        | DataInWindow -> 0
        | PhaseAWindow -> (Map.tryFind originCell clockA.Arrival |> Option.defaultValue 0) + DffClockToQ
        | PhaseBWindow -> (Map.tryFind originCell clockB.Arrival |> Option.defaultValue 0) + DffClockToQ

    /// 1 つの窓を解析する。probeTargets は DFF 以外の出力プローブ (座標, 名前)。
    let analyzeWindow
            (dg: DenseGrid)
            (topo: int[])
            (clockA: ClockAnalysis)
            (clockB: ClockAnalysis)
            (dffDataNodes: Map<int, int>)
            (probeTargets: (Coord * string list) list)
            (window: ConvergenceWindow)
            (sources: int list)
        : WindowReport =
        let info = longestPathsFrom dg topo sources
        let pathAt (target: PathEndTarget) (node: int) : CriticalPath option =
            let ai = info.[node]
            if not ai.Reached then None
            else
                let originCell = ai.Origin / ChannelsPerCell
                let clockArrival = originArrival window clockA clockB originCell
                Some { Window = window
                       Source = coordOf dg originCell
                       Target = target
                       PathLength = ai.Distance
                       Breakdown = ai.Breakdown
                       OriginClockArrival = clockArrival
                       PredictedGen = clockArrival + ai.Distance }
        let dffPaths =
            [ for KeyValue (dff, node) in dffDataNodes do
                match pathAt (DffD dff) node with
                | Some p -> yield p
                | None -> () ]
        let probePaths =
            [ for (c, names) in probeTargets do
                match mainNodeAt dg c with
                | None -> ()
                | Some node ->
                    match pathAt (ProbePoint (c, names)) node with
                    | Some p -> yield p
                    | None -> () ]
        let paths = dffPaths @ probePaths |> List.sortByDescending (fun p -> p.PredictedGen)
        let clockFloor = clockFloorFor window clockA clockB
        let pathsMax = paths |> List.map (fun p -> p.PredictedGen) |> function [] -> 0 | xs -> List.max xs
        { Window = window
          Paths = paths
          ClockFloor = clockFloor
          PredictedMax = max clockFloor pathsMax
          Sources = sources }

    // --- 4b. 経路のゲート単位分解 (配置 vs 配線の切り分け) --------------------------
    //
    // 経路長 (セル数) の大半は配線・交差セルで、NAND は数% しかない (issue #7 (c) の
    // 実測。sm83_full の data_in で NAND 0.3〜0.4%)。これが「ゲートが平面上で散らばって
    // いる」(配置の問題) のか「A* が混雑を避けて回り道している」(配線の問題) のかを
    // 切り分けるため、経路を NAND / DFF / ピンの並び (ゲート列) に落とし、隣り合う
    // ゲート間を次の 3 値に分解する:
    //   配置距離 = 2 ゲートのセル間のマンハッタン距離 (配置がどうであれ必要な下限)
    //   実配線長 = その間の実際のホップ数 (HoldAnalysis.ClockLatency の MaxArrival と同じ
    //              規約: 1 ホップ = 隣接セルへの 1 手)
    //   回り道   = 実配線長 − 配置距離
    // グリッド上は 1 手で 1 セルしか進めないので、どんな経路のホップ数もその両端の
    // マンハッタン距離を下回れない → 回り道は論理的に常に ≥ 0 になる
    // (TimingAnalysisTest で検証)。

    let private manhattan (a: Coord) (b: Coord) : int = abs (a.X - b.X) + abs (a.Y - b.Y)

    let private isNandNode (dg: DenseGrid) (node: int) : bool =
        match dg.Cells.[node / ChannelsPerCell] with
        | LNand _ -> true
        | _ -> false

    /// PathEndTarget から、longestPathsFrom の info 配列を引くノード ID を復元する
    /// (analyzeWindow の pathAt と同じ規則の裏返し)。窓に依らない純粋な変換。
    let nodeOfTarget (dg: DenseGrid) (dffDataNodes: Map<int, int>) (target: PathEndTarget) : int option =
        match target with
        | DffD dff -> Map.tryFind dff dffDataNodes
        | ProbePoint (c, _) -> mainNodeAt dg c

    /// 経路上のゲート区切り点 (NAND / DFF / ピン) の座標と、起点からの累積ホップ数
    /// (起点は 0)。DFF ターゲットは info 配列に乗らない (DffData は末端 Sink な組合せ
    /// グラフ外なので) — 直前の組合せノードの累積ホップ数 + 1 (DFF 自身へ渡る、
    /// 追跡していない最後の 1 ホップ) を使う。
    type Waypoint = { Coord: Coord; Hops: int }

    /// 隣り合うゲート 1 区間の内訳。
    type Segment =
        { From: Coord
          To: Coord
          /// 実配線長 (ホップ数)
          WireHops: int
          /// 配置距離 (マンハッタン距離。配置で決まる下限)
          PlacementDistance: int
          /// 回り道 = WireHops − PlacementDistance (常に ≥ 0)
          Detour: int }

    type PathBreakdown =
        { Waypoints: Waypoint list
          Segments: Segment list
          TotalWireHops: int
          TotalPlacementDistance: int
          TotalDetour: int
          /// 始点・終点間の直接マンハッタン距離。Segments の PlacementDistance 合計とは別物
          /// (経路が折れ曲がっていれば後者のほうが大きい) — どう配置しても避けられない
          /// 下限の目安として使う
          EndToEndManhattan: int }

    /// info の Pred を辿って、起点から node までのノード列 (昇順) を復元する。
    /// buildTopoOrder が閉路なしを保証しているので必ず有限段で起点に達する。
    let private reconstructChain (info: ArrivalInfo[]) (node: int) : int list =
        let rec go (n: int) (acc: int list) =
            if info.[n].Origin = n then n :: acc
            else go info.[n].Pred (n :: acc)
        go node []

    /// info・target・(そこへ到達した) node から経路のゲート単位分解を作る。
    /// node は nodeOfTarget が返したもの (info.[node].Reached であること)。
    let pathBreakdown (dg: DenseGrid) (info: ArrivalInfo[]) (target: PathEndTarget) (node: int) : PathBreakdown option =
        let ai = info.[node]
        if not ai.Reached then None
        else
            match reconstructChain info node with
            | [] -> None
            | source :: rest ->
                let sourceCoord = coordOf dg (source / ChannelsPerCell)
                // 中間の区切り点は NAND だけ (Wire/Cross は「回り道」の材料であって区切り点にしない)
                let midWaypoints =
                    rest
                    |> List.choose (fun n ->
                        if isNandNode dg n then
                            Some { Coord = coordOf dg (n / ChannelsPerCell); Hops = info.[n].Distance }
                        else None)
                let lastHops = ai.Distance
                let finalWaypoint =
                    match target with
                    | DffD dff -> { Coord = coordOf dg dff; Hops = lastHops + 1 }
                    | ProbePoint (c, _) -> { Coord = c; Hops = lastHops }
                // node 自身が NAND で ProbePoint がその座標を指す場合、midWaypoints の末尾と
                // finalWaypoint が同じ点になる — 二重に数えない
                let midWithoutDup =
                    match List.tryLast midWaypoints with
                    | Some last when last.Coord = finalWaypoint.Coord && last.Hops = finalWaypoint.Hops ->
                        midWaypoints |> List.rev |> List.tail |> List.rev
                    | _ -> midWaypoints
                let waypoints = { Coord = sourceCoord; Hops = 0 } :: midWithoutDup @ [ finalWaypoint ]
                let segments =
                    waypoints
                    |> List.pairwise
                    |> List.map (fun (a, b) ->
                        let wireHops = b.Hops - a.Hops
                        let placementDistance = manhattan a.Coord b.Coord
                        { From = a.Coord
                          To = b.Coord
                          WireHops = wireHops
                          PlacementDistance = placementDistance
                          Detour = wireHops - placementDistance })
                let totalWireHops = segments |> List.sumBy (fun s -> s.WireHops)
                let totalPlacementDistance = segments |> List.sumBy (fun s -> s.PlacementDistance)
                Some { Waypoints = waypoints
                       Segments = segments
                       TotalWireHops = totalWireHops
                       TotalPlacementDistance = totalPlacementDistance
                       TotalDetour = totalWireHops - totalPlacementDistance
                       EndToEndManhattan = manhattan sourceCoord finalWaypoint.Coord }

    /// window の longestPathsFrom をもう一度 (1 回だけ) 走らせて、指定した経路群の
    /// ゲート単位分解をまとめて作る。全経路ぶん info を保持し続けるとメモリを食う
    /// (グリッドサイズ × チャンネル数の配列) ので、表示対象 (上位 N 本など) を
    /// 絞ってから呼ぶこと。
    let pathBreakdownsForWindow
            (dg: DenseGrid)
            (topo: int[])
            (dffDataNodes: Map<int, int>)
            (r: WindowReport)
            (paths: CriticalPath list)
        : (CriticalPath * PathBreakdown option) list =
        let info = longestPathsFrom dg topo r.Sources
        paths
        |> List.map (fun p ->
            match nodeOfTarget dg dffDataNodes p.Target with
            | None -> p, None
            | Some node -> p, pathBreakdown dg info p.Target node)

    // --- 5. 2 相クロック回路全体の解析 -------------------------------------------------

    /// sm83_full 相当 (2 相クロック) の 3 窓すべてを解析する。
    let analyzeTwoPhaseTiming (dg: DenseGrid) (meta: RoutedMeta) (clkAIdx: int) (clkBIdx: int)
        : Result<WindowReport list, TimingError> =
        buildTopoOrder dg
        |> Result.map (fun topo ->
            let clockA = analyzeClock dg clkAIdx
            let clockB = analyzeClock dg clkBIdx
            let phaseReport = analyzeTwoPhase dg clkAIdx clkBIdx
            let dffDataNodes = buildDffDataNodes dg
            let probes = combinationalOutputProbes dg meta
            let dffsOfPhase (phase: ClockPhase) =
                phaseReport.PhaseOf
                |> Map.toList
                |> List.choose (fun (d, p) -> if p = phase then Some (d * ChannelsPerCell) else None)
            let dataInSources =
                nonClockInputCoords meta
                |> List.choose (indexOf dg)
                |> List.map (fun i -> i * ChannelsPerCell)
            [ analyzeWindow dg topo clockA clockB dffDataNodes probes DataInWindow dataInSources
              analyzeWindow dg topo clockA clockB dffDataNodes probes PhaseAWindow (dffsOfPhase PhaseA)
              analyzeWindow dg topo clockA clockB dffDataNodes probes PhaseBWindow (dffsOfPhase PhaseB) ])

    // --- 6. マスター→スレーブ間の配置距離分布 (issue #7 (c) の外れ値調査) -------------
    //
    // Clocking.toTwoPhase は元の DFF 1 個をマスター (clk_a) + スレーブ (clk_b) に分け、
    // マスターの Q (新しいネット) はそのスレーブの D 以外には使われない (1 対 1 の直結)。
    // よってマスターから HoldAnalysis.reachFromDff で辿れる DFF データ終端は必ずスレーブ
    // ちょうど 1 個のはず。この配置距離 (マンハッタン距離) の分布は、annealing が
    // master→slave アークをどれだけ短く保てているかを表す。PhaseAWindow の外れ値
    // (マスター→スレーブの直結配線が突出して長い組) を見つけるのに使う。

    /// マスター → その唯一のスレーブへの配置距離。
    type MasterSlaveDistance =
        { Master: Coord
          Slave: Coord
          /// 配置距離 (マンハッタン距離)
          PlacementDistance: int }

    /// 全マスター DFF について、スレーブへの配置距離を求める。マスター→スレーブが
    /// 1 対 1 の直結 (不変条件) でない組 (スレーブが 0 個/複数個見つかった) は除く。
    let masterSlaveDistances (dg: DenseGrid) (clkAIdx: int) (clkBIdx: int) : MasterSlaveDistance list =
        let clockA = analyzeClock dg clkAIdx
        let clockB = analyzeClock dg clkBIdx
        let dffs = findDffs dg
        let phaseCandidates (dff: int) =
            [ if Map.containsKey dff clockA.Arrival then yield PhaseA
              if Map.containsKey dff clockB.Arrival then yield PhaseB ]
        let masters = dffs |> List.filter (fun d -> phaseCandidates d = [ PhaseA ])
        let nodeCount = dg.Cells.Length * ChannelsPerCell
        let dist = Array.zeroCreate<int> nodeCount
        let stamp = Array.zeroCreate<int> nodeCount
        masters
        |> List.mapi (fun i master -> master, reachFromDff dg dist stamp (i + 1) master)
        |> List.choose (fun (master, reach) ->
            match reach.ToData |> Map.toList with
            | [ (slave, _minData) ] ->
                let m, s = coordOf dg master, coordOf dg slave
                Some { Master = m; Slave = s; PlacementDistance = manhattan m s }
            | _ -> None)
