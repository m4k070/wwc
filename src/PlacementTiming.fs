namespace WwHdl

// ---------------------------------------------------------------------
// PlacementTiming — 配置段階の静的タイミング解析とタイミング駆動の再アニーリング
// (issue #7 (c)。純粋な計算。I/O・時刻取得なし)
//
// 背景: 配線済みグリッドの解析 (TimingAnalysis.fs) で、sm83_full のクリティカルパスの
// 約 97% が「配置距離」(経路上で隣り合うゲート間のマンハッタン距離) だと分かった。
// 配線はほぼまっすぐ引けているので、配線前の配置だけから経路長をよく予測できるはず。
//
// 遅延モデル (TimingAnalysis と同じ世代の数え方):
//   * アーク (駆動元 → 受け手) の遅延 = 2 つのセルのマンハッタン距離。WireLevel は
//     1 セル = 1 世代で、受け手のゲートセル自身に着く 1 手もこの距離に含まれる
//     (配線済み解析の「配置距離」と同じ定義)。よってゲート段の追加遅延は既定 0
//     (TimingModel.GateDelay)。NAND は経路長の 0.3〜0.4% しかないので影響も小さい
//   * レジスタ (DFF) の Q = そのクロックの到達 + ClockToQ (1 世代)。クロックの到達は
//     クロックピンから DFF セルまでのマンハッタン距離で近似する (2 相のクロック網は
//     最短経路木で引くので、ほぼマンハッタン距離どおりになる)
//
// 3 つの収束窓 (TimingAnalysis.ConvergenceWindow と同じ区間):
//   DataIn: 外部入力ピン (クロック以外) → 組合せ → マスター D
//   PhaseA: マスター Q → スレーブ D
//   PhaseB: スレーブ Q → 組合せ → マスター D
// 窓の予測最大 = max (クロック網の下限, 終点 (DFF の D・組合せ出力) への最長到達)。
//
// クリティカリティ (VPR 流): アーク i を通る最長経路長 L_i、窓の予測最大 D として
//   slack_i = D − L_i、criticality_i = 1 − slack_i / D = L_i / D
// を窓ごとに求め、3 窓の最大を取る。重み w_i = 1 + α · criticality_i^β。
// ---------------------------------------------------------------------
module PlacementTiming =
    open Domain
    open Netlist
    open GatePlacement

    // --- タイミンググラフ -------------------------------------------------

    /// ゲートの役割。2 相化したネットリストの DFF はマスター (clk_a) かスレーブ (clk_b)。
    type GateRole =
        | Logic
        | Master
        | Slave

    /// 収束窓。TimingAnalysis.ConvergenceWindow と同じ区間 (名前の衝突を避けて修飾必須にする)。
    [<RequireQualifiedAccess>]
    type Window =
        | DataIn
        | PhaseA
        | PhaseB

    let allWindows : Window list = [ Window.DataIn; Window.PhaseA; Window.PhaseB ]

    let windowLabel (w: Window) : string =
        match w with
        | Window.DataIn -> "data_in"
        | Window.PhaseA -> "phaseA"
        | Window.PhaseB -> "phaseB"

    /// アークの駆動元。外部入力ピンはネットで持つ (座標は配置ごとに変わるため。
    /// 2 相では配置後にピンを受け手群のミニマックス中心へ移す — issue #7 (a))。
    type TimingSource =
        | FromGateOutput of gate: int
        | FromInputPin of net: NetId

    type TimingArc =
        { Source: TimingSource
          Sink: int }

    /// 配置段階のタイミンググラフ。Arcs の並びは GatePlacement.buildArcs と同じ
    /// (toPlacementArcs で 1 対 1 に対応させ、重みをそのまま渡せるようにする)。
    type TimingNetwork =
        { Roles: GateRole[]
          Arcs: TimingArc[]
          /// 出力が外部出力ポートになる組合せゲート (配線済み解析の ProbePoint に相当する終点)
          IsOutputProbe: bool[]
          ClockA: NetId
          ClockB: NetId
          /// 組合せゲート (Logic) のトポロジカル順
          LogicOrder: int[]
          /// ゲート → 入力アーク / 出力アークの CSR 索引
          InOffsets: int[]
          InArcs: int[]
          OutOffsets: int[]
          OutArcs: int[] }

    type PlacementTimingError =
        /// 組合せゲートだけで閉路がある (設計上あってはならない)
        | CombinationalCycle of stuckGates: int
        /// 外部入力ピンの座標が与えられていない
        | MissingPin of NetId
        | AnnealFailed of AnnealConfigError
        | InvalidTimingConfig of string

    let describePlacementTimingError (e: PlacementTimingError) : string =
        match e with
        | CombinationalCycle n -> sprintf "組合せゲートに閉路がある (%d ゲートがトポロジカル順で確定しなかった)" n
        | MissingPin (NetId n) -> sprintf "外部入力ピン (ネット %d) の座標がない" n
        | AnnealFailed e -> sprintf "焼き直しに失敗: %s" (describeConfigError e)
        | InvalidTimingConfig msg -> sprintf "タイミング駆動の設定が不正: %s" msg

    /// 配線対象のデータ入力 (GatePlacement.routedSignalInputs と同じ規則 + 2 相のクロック除外)。
    let private dataInputs (clocks: Set<NetId>) (g: Gate) : NetId list =
        let routed =
            match g.Kind, g.Inputs with
            | Dff, [ c; d; _r ] -> [ c; d ]
            | _, ins -> ins
        routed |> List.filter (fun n -> not (Set.contains n clocks))

    /// (from, to) の組の列から CSR 索引を作る (添字 = from、値 = アーク番号)。
    let private buildCsr (nodeCount: int) (keys: int[]) : int[] * int[] =
        let offsets = Array.zeroCreate<int> (nodeCount + 1)
        for k in keys do
            if k >= 0 then offsets.[k + 1] <- offsets.[k + 1] + 1
        for i in 0 .. nodeCount - 1 do
            offsets.[i + 1] <- offsets.[i + 1] + offsets.[i]
        let cursor = Array.copy offsets
        let index = Array.zeroCreate<int> offsets.[nodeCount]
        keys
        |> Array.iteri (fun arcIndex k ->
            if k >= 0 then
                index.[cursor.[k]] <- arcIndex
                cursor.[k] <- cursor.[k] + 1)
        offsets, index

    /// 組合せゲートのトポロジカル順 (Kahn 法)。DFF は閉路の断点なので辺に数えない。
    let private logicTopoOrder (roles: GateRole[]) (arcs: TimingArc[]) (outOffsets: int[]) (outArcs: int[])
        : Result<int[], PlacementTimingError> =
        let gateCount = roles.Length
        let remaining = Array.zeroCreate<int> gateCount
        for arc in arcs do
            match arc.Source with
            | FromGateOutput src when roles.[src] = Logic && roles.[arc.Sink] = Logic ->
                remaining.[arc.Sink] <- remaining.[arc.Sink] + 1
            | FromGateOutput _ | FromInputPin _ -> ()
        let queue = System.Collections.Generic.Queue<int> ()
        for g in 0 .. gateCount - 1 do
            if roles.[g] = Logic && remaining.[g] = 0 then queue.Enqueue g
        let order = ResizeArray<int> gateCount
        while queue.Count > 0 do
            let g = queue.Dequeue ()
            order.Add g
            for k in outOffsets.[g] .. outOffsets.[g + 1] - 1 do
                let sink = arcs.[outArcs.[k]].Sink
                if roles.[sink] = Logic then
                    remaining.[sink] <- remaining.[sink] - 1
                    if remaining.[sink] = 0 then queue.Enqueue sink
        let logicCount = roles |> Array.filter (fun r -> r = Logic) |> Array.length
        if order.Count = logicCount then Ok (order.ToArray ())
        else Error (CombinationalCycle (logicCount - order.Count))

    /// 2 相化したネットリストからタイミンググラフを作る。
    let buildNetwork (tp: Clocking.TwoPhaseNetlist) : Result<TimingNetwork, PlacementTimingError> =
        let nl = tp.Netlist
        let gates = nl.Gates |> List.toArray
        let clocks = set [ tp.ClockA; tp.ClockB ]
        let roles =
            gates
            |> Array.map (fun g ->
                match g.Kind, List.tryHead g.Inputs with
                | Dff, Some c when c = tp.ClockA -> Master
                | Dff, Some c when c = tp.ClockB -> Slave
                | _ -> Logic)
        let driverIndex = gates |> Array.mapi (fun i g -> g.Output, i) |> Map.ofArray
        let pinNets = nl.PrimaryInputs |> List.filter (fun n -> not (Set.contains n clocks)) |> Set.ofList
        // 並びは GatePlacement.buildArcs と同じ (受け手の宣言順 → 入力の順)
        let arcs =
            gates
            |> Array.mapi (fun sinkIndex g ->
                dataInputs clocks g
                |> List.choose (fun net ->
                    match Map.tryFind net driverIndex with
                    | Some src when src = sinkIndex -> None
                    | Some src -> Some ({ Source = FromGateOutput src; Sink = sinkIndex } : TimingArc)
                    | None when Set.contains net pinNets -> Some ({ Source = FromInputPin net; Sink = sinkIndex } : TimingArc)
                    | None -> None)
                |> List.toArray)
            |> Array.concat
        let outputs = set nl.PrimaryOutputs
        let isOutputProbe = gates |> Array.mapi (fun i g -> roles.[i] = Logic && Set.contains g.Output outputs)
        let gateCount = gates.Length
        let inOffsets, inArcs = buildCsr gateCount (arcs |> Array.map (fun a -> a.Sink))
        let outOffsets, outArcs =
            buildCsr gateCount (arcs |> Array.map (fun a -> match a.Source with FromGateOutput g -> g | FromInputPin _ -> -1))
        logicTopoOrder roles arcs outOffsets outArcs
        |> Result.map (fun order ->
            { Roles = roles
              Arcs = arcs
              IsOutputProbe = isOutputProbe
              ClockA = tp.ClockA
              ClockB = tp.ClockB
              LogicOrder = order
              InOffsets = inOffsets
              InArcs = inArcs
              OutOffsets = outOffsets
              OutArcs = outArcs })

    /// ピン座標を与えて GatePlacement のアーク列に変換する (並びは Arcs と 1 対 1)。
    let toPlacementArcs (net: TimingNetwork) (pins: Map<NetId, Coord>) : Result<Arc[], PlacementTimingError> =
        let missing =
            net.Arcs
            |> Array.tryPick (fun a ->
                match a.Source with
                | FromInputPin n when not (Map.containsKey n pins) -> Some n
                | FromInputPin _ | FromGateOutput _ -> None)
        match missing with
        | Some n -> Error (MissingPin n)
        | None ->
            net.Arcs
            |> Array.map (fun (a: TimingArc) ->
                let source =
                    match a.Source with
                    | FromGateOutput g -> FromGate g
                    | FromInputPin n -> FromFixed (Map.find n pins)
                ({ Source = source; Sink = a.Sink } : Arc))
            |> Ok

    // --- 静的タイミング解析 ------------------------------------------------

    type TimingModel =
        { /// 組合せゲート 1 段の追加遅延 (アーク遅延に含まれない分)。既定 0 (冒頭の説明)
          GateDelay: int
          /// クロック到達から Q が確定するまで (HoldAnalysis.DffClockToQ と同じ 1 世代)
          ClockToQ: int }

    let defaultTimingModel : TimingModel = { GateDelay = 0; ClockToQ = 1 }

    /// 未到達 (この窓では変化しない) を表す番兵。
    [<Literal>]
    let private Unreached = -1

    /// 窓 1 つの解析結果。
    type WindowTiming =
        { Window: Window
          /// 経路が無くても必ずかかる下限 (クロック網の最大到達 + ClockToQ)。DataIn は 0
          ClockFloor: int
          /// 終点への最長到達 (クロック到達込み)。経路が無ければ 0
          PathMax: int
          /// = max ClockFloor PathMax。クリティカリティの分母
          PredictedMax: int
          /// アークごとの「そのアークを通る最長経路長」(窓の開始から、クロック到達込み)。
          /// この窓でアークが変化しない・どの終点にも届かないなら -1
          ArcPathLength: int[]
          /// 終点 (DFF の D・組合せ出力) → 到達世代。この窓で到達するものだけ
          EndpointArrival: Map<int, int> }

    let private manhattan (a: Coord) (b: Coord) : int = abs (a.X - b.X) + abs (a.Y - b.Y)

    /// アーク i の遅延 (マンハッタン距離)。
    let private arcDelay (net: TimingNetwork) (coordOf: int -> Coord) (pins: Map<NetId, Coord>) (i: int) : int =
        let arc = net.Arcs.[i]
        let src =
            match arc.Source with
            | FromGateOutput g -> coordOf g
            | FromInputPin n -> Map.find n pins
        manhattan src (coordOf arc.Sink)

    let private isSourceRole (window: Window) (role: GateRole) : bool =
        match window, role with
        | Window.PhaseA, Master -> true
        | Window.PhaseB, Slave -> true
        | _ -> false

    /// 1 つの窓を解析する。delays は arcDelay の前計算、clockArrival はゲートごとのクロック到達
    /// (Logic は無意味)。
    let private analyzeWindow
            (model: TimingModel)
            (net: TimingNetwork)
            (delays: int[])
            (clockArrival: int[])
            (window: Window)
        : WindowTiming =
        let gateCount = net.Roles.Length
        let arcCount = net.Arcs.Length
        // 前向き: ゲート出力の最長到達 (Unreached = この窓で変化しない)
        let outArrival = Array.create gateCount Unreached
        for g in 0 .. gateCount - 1 do
            if isSourceRole window net.Roles.[g] then
                outArrival.[g] <- clockArrival.[g] + model.ClockToQ
        let sourceArrival (i: int) : int =
            match net.Arcs.[i].Source with
            | FromInputPin _ -> if window = Window.DataIn then 0 else Unreached
            | FromGateOutput g -> outArrival.[g]
        /// ゲート g の入力側の最長到達 (到達するアークが無ければ Unreached)
        let inArrival (g: int) : int =
            let mutable best = Unreached
            for k in net.InOffsets.[g] .. net.InOffsets.[g + 1] - 1 do
                let i = net.InArcs.[k]
                let s = sourceArrival i
                if s <> Unreached then best <- max best (s + delays.[i])
            best
        for g in net.LogicOrder do
            let a = inArrival g
            if a <> Unreached then outArrival.[g] <- a + model.GateDelay
        // 終点: 全 DFF の D と組合せ出力
        let endpoints =
            [ for g in 0 .. gateCount - 1 do
                match net.Roles.[g] with
                | Master | Slave ->
                    let a = inArrival g
                    if a <> Unreached then yield g, a
                | Logic ->
                    if net.IsOutputProbe.[g] && outArrival.[g] <> Unreached then yield g, outArrival.[g] ]
            |> Map.ofList
        // 後ろ向き: ゲート出力から終点までの最長 (Unreached = どの終点にも届かない)
        let tail = Array.create gateCount Unreached
        /// アーク i の受け手の入力から終点までの最長
        let sinkTail (i: int) : int =
            let sink = net.Arcs.[i].Sink
            match net.Roles.[sink] with
            | Master | Slave -> 0
            | Logic -> if tail.[sink] = Unreached then Unreached else model.GateDelay + tail.[sink]
        let computeTail (g: int) =
            let mutable best = if net.Roles.[g] = Logic && net.IsOutputProbe.[g] then 0 else Unreached
            for k in net.OutOffsets.[g] .. net.OutOffsets.[g + 1] - 1 do
                let i = net.OutArcs.[k]
                let t = sinkTail i
                if t <> Unreached then best <- max best (delays.[i] + t)
            tail.[g] <- best
        for idx in net.LogicOrder.Length - 1 .. -1 .. 0 do
            computeTail net.LogicOrder.[idx]
        // レジスタの tail (起点として使う窓でだけ意味がある)。組合せの tail が確定してから求める
        for g in 0 .. gateCount - 1 do
            if net.Roles.[g] <> Logic then computeTail g
        let arcPathLength =
            Array.init arcCount (fun i ->
                let s = sourceArrival i
                let t = sinkTail i
                if s = Unreached || t = Unreached then -1 else s + delays.[i] + t)
        let clockFloor =
            match window with
            | Window.DataIn -> 0
            | Window.PhaseA | Window.PhaseB ->
                let arrivals =
                    [ for g in 0 .. gateCount - 1 do
                        if isSourceRole window net.Roles.[g] then yield clockArrival.[g] + model.ClockToQ ]
                match arrivals with
                | [] -> 0
                | xs -> List.max xs
        let pathMax = if Map.isEmpty endpoints then 0 else endpoints |> Map.toSeq |> Seq.map snd |> Seq.max
        { Window = window
          ClockFloor = clockFloor
          PathMax = pathMax
          PredictedMax = max clockFloor pathMax
          ArcPathLength = arcPathLength
          EndpointArrival = endpoints }

    /// 配置 (ゲート → 座標) とピン座標 (クロックピンを含む)、クロックピン分割
    /// (issue #7 (b): clockNetOfGate g = ゲート g (Master/Slave のみ) が実際に配線される
    /// 区画ネット。分割していなければ常に net.ClockA / net.ClockB) から 3 窓を解析する。
    /// クロック到達は「その DFF が担当するピン」までのマンハッタン距離で近似する。
    let analyze
            (model: TimingModel)
            (net: TimingNetwork)
            (coordOf: int -> Coord)
            (pins: Map<NetId, Coord>)
            (clockNetOfGate: int -> NetId)
        : Result<WindowTiming list, PlacementTimingError> =
        let requiredPins =
            [ for g in 0 .. net.Roles.Length - 1 do
                  match net.Roles.[g] with
                  | Master | Slave -> yield clockNetOfGate g
                  | Logic -> ()
              for a in net.Arcs do
                  match a.Source with
                  | FromInputPin n -> yield n
                  | FromGateOutput _ -> () ]
        match requiredPins |> List.tryFind (fun n -> not (Map.containsKey n pins)) with
        | Some n -> Error (MissingPin n)
        | None ->
            let coords = Array.init net.Roles.Length coordOf
            let coordAt (g: int) = coords.[g]
            let delays = Array.init net.Arcs.Length (arcDelay net coordAt pins)
            let clockArrival =
                net.Roles
                |> Array.mapi (fun g role ->
                    match role with
                    | Master | Slave -> manhattan (Map.find (clockNetOfGate g) pins) coords.[g]
                    | Logic -> 0)
            Ok (allWindows |> List.map (analyzeWindow model net delays clockArrival))

    /// 窓の予測最大の要約 (data_in, phaseA, phaseB)。
    type TimingSummary =
        { DataIn: int
          PhaseA: int
          PhaseB: int }
        /// 1 周期 (2 相) の予測 = 3 窓の和
        member s.Cycle = s.DataIn + s.PhaseA + s.PhaseB

    let summarize (windows: WindowTiming list) : TimingSummary =
        let maxOf (w: Window) =
            windows |> List.tryFind (fun x -> x.Window = w) |> Option.map (fun x -> x.PredictedMax) |> Option.defaultValue 0
        { DataIn = maxOf Window.DataIn; PhaseA = maxOf Window.PhaseA; PhaseB = maxOf Window.PhaseB }

    // --- クリティカリティと重み ---------------------------------------------

    /// アークのクリティカリティ (0〜1)。3 窓それぞれの L_i / D の最大。どの窓にも乗らないアークは 0。
    let criticalities (arcCount: int) (windows: WindowTiming list) : float[] =
        let crit = Array.zeroCreate<float> arcCount
        for w in windows do
            if w.PredictedMax > 0 then
                let d = float w.PredictedMax
                for i in 0 .. arcCount - 1 do
                    let l = w.ArcPathLength.[i]
                    if l >= 0 then crit.[i] <- max crit.[i] (min 1.0 (float l / d))
        crit

    /// マスター → スレーブのアークか (L1 の縮退を崩す基底重みの対象)。
    let isMasterSlaveArc (net: TimingNetwork) (i: int) : bool =
        let arc = net.Arcs.[i]
        match arc.Source with
        | FromGateOutput m -> net.Roles.[m] = Master && net.Roles.[arc.Sink] = Slave
        | FromInputPin _ -> false

    /// 重み w = base + α · c^β を固定小数点 (GatePlacement.WeightScale) にする。
    /// base はマスター → スレーブのアークだけ cfg.MasterSlaveWeight、他は 1。
    let arcWeights (cfg: TimingDrivenConfig) (net: TimingNetwork) (crit: float[]) : int64[] =
        crit
        |> Array.mapi (fun i c ->
            let baseWeight = if isMasterSlaveArc net i then cfg.MasterSlaveWeight else 1.0
            int64 (System.Math.Round (float WeightScale * (baseWeight + cfg.Alpha * (c ** cfg.Beta)))))

    /// クリティカリティの記憶 (指数移動平均)。previous が無い (最初のラウンド) なら current そのもの。
    let smoothCriticalities (memory: float) (previous: float[] option) (current: float[]) : float[] =
        match previous with
        | None -> current
        | Some prev -> Array.map2 (fun p c -> memory * p + (1.0 - memory) * c) prev current

    // --- タイミング駆動の再アニーリング ---------------------------------------

    /// 1 ラウンドの記録 (Round 0 = 初期解)。
    type RoundReport =
        { Round: int
          Summary: TimingSummary
          /// 重みなしの総アーク距離 (セル。ピンからのアークを含む)
          ArcDistance: int64
          /// 焼き直しで受理された手数 (Round 0 は 0)
          AcceptedMoves: int }

    type TimingDrivenOutcome =
        { /// 選んだ解 (全ラウンドのうち 1 周期の予測 = 3 窓の和が最小のもの)
          Best: Assignment
          BestRound: int
          Rounds: RoundReport list }

    let private validateTimingConfig (cfg: TimingDrivenConfig) : Result<unit, PlacementTimingError> =
        if cfg.Rounds < 0 then Error (InvalidTimingConfig (sprintf "Rounds は 0 以上: %d" cfg.Rounds))
        elif cfg.Alpha < 0.0 then Error (InvalidTimingConfig (sprintf "Alpha は 0 以上: %g" cfg.Alpha))
        elif cfg.Beta <= 0.0 then Error (InvalidTimingConfig (sprintf "Beta は正: %g" cfg.Beta))
        elif cfg.MasterSlaveWeight < 0.0 then
            Error (InvalidTimingConfig (sprintf "MasterSlaveWeight は 0 以上: %g" cfg.MasterSlaveWeight))
        elif cfg.CriticalityMemory < 0.0 || cfg.CriticalityMemory >= 1.0 then
            Error (InvalidTimingConfig (sprintf "CriticalityMemory は [0, 1): %g" cfg.CriticalityMemory))
        else Ok ()

    /// 1 つの配置を評価する (解析 + 総アーク距離)。resolvePins はピン座標に加えて、
    /// ゲート (Master/Slave) → 実際に配線される区画クロックネットも返す (issue #7 (b))。
    let evaluate
            (model: TimingModel)
            (grid: SlotGrid)
            (net: TimingNetwork)
            (resolvePins: Assignment -> Map<NetId, Coord> * (int -> NetId))
            (assignment: Assignment)
        : Result<WindowTiming list * Arc[] * int64, PlacementTimingError> =
        let pins, clockNetOfGate = resolvePins assignment
        analyze model net (fun g -> slotCoord grid assignment.[g]) pins clockNetOfGate
        |> Result.bind (fun windows ->
            toPlacementArcs net pins
            |> Result.map (fun arcs ->
                let distance = totalCost grid arcs 0.0 assignment / CostScale
                windows, arcs, distance))

    /// タイミング駆動の再アニーリング。initial (通常は総アーク距離のアニーリング結果) から
    /// 「解析 → 重み更新 → 低温から焼き直し」を cfg.Rounds 回繰り返す。
    /// resolvePins は割り当てからピン座標 (クロックピン・外部入力ピン) を決める関数
    /// (PipelineWL の配置と同じ規則を渡す)。ピンは各ラウンドの焼き直し中は固定する。
    /// ラウンド r の乱数シードは anneal.Seed + r (決定的)。
    let timingDrivenAnneal
            (model: TimingModel)
            (anneal: AnnealConfig)
            (cfg: TimingDrivenConfig)
            (grid: SlotGrid)
            (net: TimingNetwork)
            (resolvePins: Assignment -> Map<NetId, Coord> * (int -> NetId))
            (initial: Assignment)
        : Result<TimingDrivenOutcome, PlacementTimingError> =
        let roundAnneal (round: int) : AnnealConfig =
            { anneal with
                Seed = anneal.Seed + uint64 round
                Moves = cfg.MovesPerRound
                InitialTemperature = cfg.InitialTemperature
                FinalTemperature = cfg.FinalTemperature }
        let report (round: int) (accepted: int) (windows: WindowTiming list) (distance: int64) : RoundReport =
            { Round = round; Summary = summarize windows; ArcDistance = distance; AcceptedMoves = accepted }
        let rec loop
                (round: int)
                (current: Assignment)
                (evaluated: WindowTiming list * Arc[] * int64)
                (previousCrit: float[] option)
                (acc: (RoundReport * Assignment) list) =
            if round > cfg.Rounds then Ok (List.rev acc)
            else
                let windows, arcs, _ = evaluated
                let crit = smoothCriticalities cfg.CriticalityMemory previousCrit (criticalities arcs.Length windows)
                let weights = arcWeights cfg net crit
                annealWeighted (roundAnneal round) grid current.Length arcs (Weighted weights) current
                |> Result.mapError AnnealFailed
                |> Result.bind (fun outcome ->
                    evaluate model grid net resolvePins outcome.Best
                    |> Result.bind (fun next ->
                        let nextWindows, _, nextDistance = next
                        let r = report round outcome.AcceptedMoves nextWindows nextDistance
                        loop (round + 1) outcome.Best next (Some crit) ((r, outcome.Best) :: acc)))
        validateTimingConfig cfg
        |> Result.bind (fun () -> evaluate model grid net resolvePins initial)
        |> Result.bind (fun first ->
            let firstWindows, _, firstDistance = first
            let r0 = report 0 0 firstWindows firstDistance
            loop 1 initial first None [ r0, initial ])
        |> Result.map (fun rounds ->
            // 同点なら早いラウンド (総アーク距離の増加が少ない側) を選ぶ
            let bestReport, bestAssignment = rounds |> List.minBy (fun (r, _) -> r.Summary.Cycle, r.Round)
            { Best = Array.copy bestAssignment
              BestRound = bestReport.Round
              Rounds = rounds |> List.map fst })
