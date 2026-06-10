namespace WwHdl

// ---------------------------------------------------------------------
// 6. 静的タイミング解析 (STA)
//    到達時刻の計算と DELAY_n セル挿入によるタイミング均等化。
// ---------------------------------------------------------------------
module Sta =
    open Units
    open Domain
    open Netlist
    open Place
    open Route

    /// ネットごとの信号到達世代。
    type ArrivalMap = Map<NetId, int<gen>>

    /// トポロジカル順で各ネットの到達時刻を計算する。
    ///   arrival(primary_input) = 0<gen>
    ///   arrival(gate_output)   = max(arrival(input_i) + wire_i.Delay + portDelay_i) + Latency - maxPortDelay
    ///
    /// portDelay は各 In ポートの内部遅延 (ポート→junction)。
    /// junc3_Ab3 のようにポートごとに遅延が異なるセルを正しく扱う。
    ///
    /// 実装: 全入力の到達時刻が確定したゲートを繰り返し処理する。
    ///   組合せ回路 (DAG) なら必ず収束する。
    let computeArrival (placement: Placement) (wires: Wire list) : ArrivalMap =
        let wireDelayByConsumer =
            wires |> List.map (fun w -> (w.Net, w.Consumer), w.Delay) |> Map.ofList

        let gateDrivenNets =
            placement |> List.map (fun p -> p.Gate.Output) |> Set.ofList

        // 一次入力ネット (いずれのゲートも駆動しないネット) の到達時刻 = 0
        let primaryArrivals =
            placement
            |> List.collect (fun p -> p.Gate.Inputs)
            |> List.filter (fun n -> not (Set.contains n gateDrivenNets))
            |> List.distinct
            |> List.map (fun n -> n, 0<gen>)
            |> Map.ofList

        // 全入力の到達時刻が確定したゲートを順次処理して伝播する。
        let rec propagate (arr: ArrivalMap) (remaining: Placed list) =
            if List.isEmpty remaining then arr
            else
                let ready, notReady =
                    remaining |> List.partition (fun p ->
                        p.Gate.Inputs |> List.forall (fun n -> Map.containsKey n arr))
                if List.isEmpty ready then arr  // 進捗なし (サイクル or 孤立)
                else
                    let arr' =
                        ready |> List.fold (fun acc p ->
                            let inPortDelays =
                                p.Cell.PortDelays
                                |> List.truncate p.Gate.Inputs.Length
                            let padCount = p.Gate.Inputs.Length - inPortDelays.Length
                            let portDelays =
                                inPortDelays @ List.replicate padCount p.Cell.Latency
                            let maxPortDelay =
                                if portDelays.IsEmpty then p.Cell.Latency
                                else List.max portDelays
                            let inputTimes =
                                p.Gate.Inputs |> List.mapi (fun i n ->
                                    (Map.tryFind n acc |> Option.defaultValue 0<gen>)
                                    + (Map.tryFind (n, p.Gate.Output) wireDelayByConsumer |> Option.defaultValue 0<gen>)
                                    + portDelays.[i])
                            let junctionTime =
                                if List.isEmpty inputTimes then 0<gen>
                                else List.max inputTimes
                            Map.add p.Gate.Output (junctionTime + p.Cell.Latency - maxPortDelay) acc) arr
                    propagate arr' notReady

        propagate primaryArrivals placement

    /// 各 Wire のスラック（余裕世代）を計算する。
    ///   target(gate)  = max { arrival(input_i) + wireDelay(input_i) + portDelay(input_i) }
    ///   slack(net_i, consumer)  = target(gate) - arrival(net_i) - wireDelay(net_i) - portDelay(input_i)
    /// スラック > 0 のネットは target に合わせて遅延を追加する必要がある。
    /// 戻り値は Map<NetId * NetId, int<gen>> (key = (net, consumer_gate_output))。
    let computeSlack (placement: Placement) (wires: Wire list) (arrivals: ArrivalMap)
        : Map<NetId * NetId, int<gen>> =
        let wireDelayByConsumer =
            wires |> List.map (fun w -> (w.Net, w.Consumer), w.Delay) |> Map.ofList

        let inputArrivalAt (n: NetId) (consumer: NetId) (portDelay: int<gen>) =
            (Map.tryFind n arrivals |> Option.defaultValue 0<gen>)
            + (Map.tryFind (n, consumer) wireDelayByConsumer |> Option.defaultValue 0<gen>)
            + portDelay

        placement
        |> List.collect (fun p ->
            if List.isEmpty p.Gate.Inputs then []
            else
                let inPortDelays =
                    p.Cell.PortDelays
                    |> List.truncate p.Gate.Inputs.Length
                let padCount = p.Gate.Inputs.Length - inPortDelays.Length
                let portDelays =
                    inPortDelays @ List.replicate padCount p.Cell.Latency
                let target =
                    p.Gate.Inputs |> List.mapi (fun i n -> inputArrivalAt n p.Gate.Output portDelays.[i])
                    |> List.max
                p.Gate.Inputs |> List.mapi (fun i n ->
                    (n, p.Gate.Output), target - inputArrivalAt n p.Gate.Output portDelays.[i]))
        |> Map.ofList

    /// パスに extra 世代分のジグザグ迂回を物理的に挿入する。
    ///
    /// 戦略: pivot から -Y へ N/2 歩、pivot.X+1 列へ横断、+Y へ N/2 歩して dst へ。
    ///   旧パターン (pivot 往復) は pivot を3方向ジャンクションにしてショートカットが生じたため廃止。
    ///   新パターンは横断型U字で各セルが2接続のみ → 3方向ジャンクションなし。
    ///   奇数の extra は N+1 に切り上げる (STA は 1 gen 余裕を持って吸収)。
    let extendPath (extra: int<gen>) (path: Coord list) : Coord list =
        let n = int extra
        if n <= 0 || path.Length < 2 then path
        else
            let n' = if n % 2 = 0 then n else n + 1  // 偶数に切り上げ
            let halfN = n' / 2
            let pivot = List.item (path.Length - 2) path
            let dst   = List.last path
            let initPath = path |> List.take (path.Length - 2)
            // pivot から -Y へ halfN 歩 (上側 arm)
            let up = [ for i in 1 .. halfN -> { X = pivot.X; Y = pivot.Y - i } ]
            // 上側 arm 末尾 (pivot.Y-halfN) から dst.X まで横断
            let crossX = [ for x in pivot.X + 1 .. dst.X -> { X = x; Y = pivot.Y - halfN } ]
            // dst.X 列を (pivot.Y-halfN+1) から dst.Y まで戻る (下側 arm、dst を含む)
            let down = [ for y in pivot.Y - halfN + 1 .. dst.Y -> { X = dst.X; Y = y } ]
            initPath @ [pivot] @ up @ crossX @ down

    /// スラックが正の Wire に遅延を物理的に付加して均等化する。
    ///
    /// 戦略 (優先順):
    ///   ① ウェイポイントルーティング: src から K セル直線延長 (K=2..15, +Y/-Y) 後、
    ///      leePathFanout で dst まで再ルーティング。measureDelay が目標一致したら採用。
    ///      理由: U 字末端は pivot-dst 隣接ショートカットで遅延増加不可。
    ///      src からの直線 K セルは L ターン 1 個分だけのショートカットが起き、
    ///      K-1 gen の追加遅延を確実に実現できる。
    ///   ② U 字末端延長 (halfN=1..20, ±Y): 目標一致 → 最小オーバーシュート →
    ///      最短衝突なし候補の順で採用。
    ///   ③ 候補なし: 元パスを保持し遅延を実測値に更新する。
    ///
    /// placement は allPorts と baseGrid の生成に使用する。不要な場合は [] を渡す。
    let insertDelays (placement: Placement) (slack: Map<NetId * NetId, int<gen>>) (wires: Wire list) : Wire list =
        let allPorts =
            placement |> List.collect (fun p ->
                p.Cell.Ports |> List.map (portCoord p))
            |> Set.ofList

        // 各ゲートの絶対座標パターングリッド (コンテキスト付き遅延計測に使用)。
        // 各ゲートの絶対座標パターングリッド (コンテキスト付き遅延計測と対角ショートカット検出に使用)。
        let gateGridByNet : Map<NetId, Grid> =
            placement |> List.map (fun p ->
                let gateGrid =
                    p.Cell.Pattern
                    |> Map.toList
                    |> List.map (fun (localCoord, state) ->
                        { X = p.Origin.X + localCoord.X; Y = p.Origin.Y + localCoord.Y }, state)
                    |> Map.ofList
                p.Gate.Output, gateGrid)
            |> Map.ofList

        let baseGrid = buildGrid placement

        let addWire (w: Wire) (g: RoutingGrid) =
            w.Path |> List.fold (fun g2 c ->
                match Map.tryFind c baseGrid with
                | Some Blocked -> g2
                | _ -> Map.add c (Routed w.Net) g2) g

        let removeWire (w: Wire) (g: RoutingGrid) =
            w.Path |> List.fold (fun g2 c ->
                match Map.tryFind c baseGrid with
                | Some Blocked -> g2
                | _ -> Map.remove c g2) g

        let mutable routingGrid =
            wires |> List.fold (fun g w -> addWire w g) baseGrid

        // src から yDir へ K セル直進後 dst まで BFS、目標遅延一致のパスを返す。
        let tryWaypoint (w: Wire) (K: int) (yDir: int) (target: int<gen>) =
            let src = List.head w.Path
            let dst = List.last w.Path
            let straight = [ for i in 1..K -> { X = src.X; Y = src.Y + yDir * i } ]
            let waypoint = List.last straight
            let ok = straight |> List.forall (fun c ->
                match Map.tryFind c routingGrid with
                | None | Some Free -> true
                | Some (Routed n) -> n = w.Net
                | _ -> false)
            if not ok then None
            else
                // leePathFanout は同一ネットの既配線セルを再利用するため、
                // 迂回路がオリジナル経路に合流して遅延が増えない場合がある。
                // leePath を使い同一ネット再利用を禁止して独立した迂回路を探す。
                match leePath routingGrid waypoint dst allPorts true with
                | None -> None
                | Some wptPath ->
                    let full = (src :: straight) @ List.tail wptPath
                    let gateGrid = gateGridByNet |> Map.tryFind w.Net |> Option.defaultValue Map.empty
                    let d = (ofPathInContext w.Net w.Consumer full gateGrid).Delay
                    if d = target then Some (full, d) else None

        wires |> List.map (fun w ->
            routingGrid <- removeWire w routingGrid

            let result =
                match Map.tryFind (w.Net, w.Consumer) slack with
                | Some s when s > 0<gen> ->
                    let target = w.Delay + s
                    let waypointMaxK = 15
                    let uShapeMaxHalfN = 20
                    let pivotBackMax = 4

                    // ① ウェイポイントルーティング
                    let waypointBest =
                        [ for K in 2..waypointMaxK do
                            for yDir in [1; -1] do
                                match tryWaypoint w K yDir target with
                                | Some r -> yield r
                                | None -> () ]
                        |> List.tryHead

                    // ② U 字末端延長 (フォールバック)
                    // pivotBack: 末尾から何番目の要素を pivot にするか (2=second-to-last, 3=third-to-last, ...)
                    // path が短い場合や standard pivot で目標遅延に達しない場合は deeper pivot を試みる。
                    let uShapeCandidates (pivotBack: int) =
                        if w.Path.Length < pivotBack + 1 then []
                        else
                            let pivot    = List.item (w.Path.Length - pivotBack) w.Path
                            let dst      = List.last w.Path
                            let initPath = w.Path |> List.take (w.Path.Length - pivotBack)
                            [ for halfN in 1..uShapeMaxHalfN do
                                for yDir in [-1; 1] do
                                    let midY     = pivot.Y + yDir * halfN
                                    let upArm    = [ for i in 1..halfN -> { X = pivot.X; Y = pivot.Y + yDir * i } ]
                                    let crossArm = [ for x in pivot.X + 1 .. dst.X -> { X = x; Y = midY } ]
                                    let downArm  =
                                        if yDir = -1 then
                                            [ for y in midY + 1 .. dst.Y -> { X = dst.X; Y = y } ]
                                        else
                                            [ for y in midY - 1 .. -1 .. dst.Y -> { X = dst.X; Y = y } ]
                                    let newCells = upArm @ crossArm @ downArm
                                    if not newCells.IsEmpty then
                                        // 既存パスとの重複セルを除く (deeper pivot では initPath が短くなるため)
                                        let initSet = initPath |> Set.ofList
                                        let hasDupe = newCells |> List.exists (fun c -> Set.contains c initSet)
                                        let path' = initPath @ [pivot] @ newCells
                                        if not hasDupe then
                                            let collides =
                                                newCells |> List.exists (fun c ->
                                                    c <> dst &&
                                                    match Map.tryFind c routingGrid with
                                                    | None | Some Free -> false
                                                    | Some (Routed n) -> n <> w.Net
                                                    | _ -> true)
                                            if not collides then
                                                let gateGrid = gateGridByNet |> Map.tryFind w.Net |> Option.defaultValue Map.empty
                                                let d = (ofPathInContext w.Net w.Consumer path' gateGrid).Delay
                                                yield path', d ]

                    let uShapeBest () =
                        if w.Path.Length < 2 then None
                        else
                            // pivotBack=2 (standard) → 3 (one step deeper) → 4 の順で候補を収集。
                            // Deeper pivot は standard pivot が目標に届かない場合のフォールバック。
                            let allCandidates =
                                [ for back in 2..pivotBackMax do yield! uShapeCandidates back ]
                            allCandidates |> List.tryFind (fun (_, d) -> d = target)
                            |> Option.orElse (
                                allCandidates
                                |> List.filter (fun (_, d) -> d > target)
                                |> List.sortBy (fun (_, d) -> int d)
                                |> List.tryHead)
                            |> Option.orElse (
                                allCandidates
                                |> List.sortBy (fun (p, _) -> p.Length)
                                |> List.tryHead)

                    let best = waypointBest |> Option.orElse (uShapeBest ())

                    match best with
                    | Some (path', delay') -> { w with Path = path'; Delay = delay' }
                    | None ->
                        let gateGrid = gateGridByNet |> Map.tryFind w.Net |> Option.defaultValue Map.empty
                        { w with Delay = (ofPathInContext w.Net w.Consumer w.Path gateGrid).Delay }
                | _ -> w

            routingGrid <- addWire result routingGrid
            result)

