namespace WwHdl

// ---------------------------------------------------------------------
// GatePlacement — ゲート配置の最適化 (純粋な計算。I/O・時刻取得なし)
//
// 解 = ゲート → スロットの割り当て。スロットはピッチ格子上の位置で、
// スロット数 ≥ ゲート数 (空きスロットがあってよい)。
//
// 目的関数 = 信号アークのマンハッタン距離の総和。
//   アーク = 駆動元 (ゲート or 固定の外部入力ピン) → 受け手ゲートの 1 ペア。
//   sm83_subset の完走実績で「実配線需要 ≒ アーク距離 × 1.040」だった
//   (タップ共有がほぼ効かない) ため、HPWL ではなくアーク距離を最小化する。
//   クロックネットは別系統 (重心ピン + スキュー調整) なので除外する。
//
// 最適化 = シミュレーテッドアニーリング。
//   * 近傍 = ゲート a を窓内のスロットへ動かす (占有者 b がいれば交換、空きなら移動)
//   * 増分評価 = a と b に接続するアークだけを再計算する (ゲート → アーク索引を前計算)
//   * 最良解を保持して返す → 初期解より悪くならない
//   * 乱数は自前の SplitMix64 (シード固定で決定的。ランタイム実装に依存しない)
// ---------------------------------------------------------------------
module GatePlacement =
    open Domain
    open Netlist

    // --- 設定 -----------------------------------------------------------

    /// アニーリングの設定。
    /// 温度の単位は「格子 1 間隔」= 平均ピッチ (PitchX+PitchY)/2 セル。コストの差分はピッチに
    /// 比例して大きくなるため、セル単位で固定するとピッチごとに温度域がずれる。格子単位なら
    /// 同じ既定値を 20x14〜32x24 で使い回せる。
    /// 冷却は InitialTemperature → FinalTemperature への幾何減衰で、Moves 回で到達する
    /// (固定の冷却率ではなく終点温度で指定するのは、Moves を変えても同じ温度域を
    ///  なめるようにして、Moves の比較を公平にするため)。
    type AnnealConfig =
        { Seed: uint64
          Moves: int
          InitialTemperature: float
          FinalTemperature: float
          /// 受け手の X が駆動元の X より小さい (西向きに戻る) アークに、
          /// 戻る X 距離 × BackwardPenalty を加算する。ゲートは東向きで出力が東に出るため、
          /// 逆行アークは U ターン分だけ実配線が長くなる。0 で無効。
          BackwardPenalty: float }

    /// 配置戦略。RowMajor = JSON 宣言順の行優先 (従来動作)。
    type PlacementStrategy =
        | RowMajor
        | Annealed of AnnealConfig

    /// 既定値。sm83_full (10,767 ゲート) の計測で効果が飽和し始める点 (50M moves、Debug で約 30 秒)。
    /// 温度は 24x16 で T0 = 5〜200 を掃引した結果、行優先の初期解を十分「溶かす」高温始まりが良かった。
    let defaultAnnealConfig : AnnealConfig =
        { Seed = 1UL
          Moves = 50_000_000
          InitialTemperature = 50.0
          FinalTemperature = 0.1
          BackwardPenalty = 0.0 }

    type AnnealConfigError =
        | NegativeMoves of int
        | NonPositiveTemperature of initial: float * final: float
        | FinalAboveInitial of initial: float * final: float
        | NegativeBackwardPenalty of float
        | InvalidInitialAssignment of string

    let describeConfigError (e: AnnealConfigError) : string =
        match e with
        | NegativeMoves m -> sprintf "Moves は 0 以上が必要: %d" m
        | NonPositiveTemperature (t0, t1) -> sprintf "温度は正が必要: initial=%g final=%g" t0 t1
        | FinalAboveInitial (t0, t1) -> sprintf "FinalTemperature は InitialTemperature 以下が必要: initial=%g final=%g" t0 t1
        | NegativeBackwardPenalty p -> sprintf "BackwardPenalty は 0 以上が必要: %g" p
        | InvalidInitialAssignment msg -> sprintf "初期割り当てが不正: %s" msg

    // --- 問題の表現 -------------------------------------------------------

    /// スロット格子。スロット番号 s → (col = s % Columns, row = s / Columns)。
    type SlotGrid =
        { Columns: int
          Rows: int
          Origin: Coord
          PitchX: int
          PitchY: int }

    let slotCount (grid: SlotGrid) = grid.Columns * grid.Rows

    let slotCoord (grid: SlotGrid) (slot: int) : Coord =
        { X = grid.Origin.X + (slot % grid.Columns) * grid.PitchX
          Y = grid.Origin.Y + (slot / grid.Columns) * grid.PitchY }

    /// 現行配置と同じ正方寄りの格子: 列数 = ceil(sqrt n)、行数 = 全ゲートが入る最小値。
    let squareSlotGrid (gateCount: int) (origin: Coord) (pitchX: int) (pitchY: int) : SlotGrid =
        let n = max 1 gateCount
        let cols = int (ceil (sqrt (float n)))
        let rows = (n + cols - 1) / cols
        { Columns = cols; Rows = rows; Origin = origin; PitchX = pitchX; PitchY = pitchY }

    /// アークの駆動元。受け手は常にゲート。
    type ArcSource =
        | FromGate of gateIndex: int
        | FromFixed of Coord

    type Arc =
        { Source: ArcSource
          Sink: int }

    /// 割り当て: 添字 = ゲート番号 (Netlist.Gates の順)、値 = スロット番号。
    type Assignment = int[]

    /// 行優先 (従来配置): ゲート i → スロット i。
    let rowMajorAssignment (gateCount: int) : Assignment = Array.init gateCount id

    /// 配線対象になる入力ネット。PipelineWL.gateTerminals と同じ規則:
    /// $_DFF_PP0_ の R (3 番目) は配線されないので除外する。クロックネットも除外。
    let private routedSignalInputs (clockNet: NetId option) (g: Gate) : NetId list =
        let routed =
            match g.Kind, g.Inputs with
            | Dff, [ c; d; _r ] -> [ c; d ]
            | _, ins -> ins
        routed |> List.filter (fun n -> Some n <> clockNet)

    /// ネットリストからアーク集合を作る。fixedPins = 外部入力ピンの固定座標 (クロック以外)。
    /// ゲートの自己ループ (距離が常に 0) は除く。駆動元の無いネット (定数など) は無視する。
    let buildArcs (nl: Netlist) (fixedPins: Map<NetId, Coord>) : Arc[] =
        let driverIndex =
            nl.Gates |> List.mapi (fun i g -> g.Output, i) |> Map.ofList
        nl.Gates
        |> List.mapi (fun sinkIndex g ->
            routedSignalInputs nl.ClockNet g
            |> List.choose (fun net ->
                match Map.tryFind net driverIndex with
                | Some src when src = sinkIndex -> None
                | Some src -> Some { Source = FromGate src; Sink = sinkIndex }
                | None ->
                    Map.tryFind net fixedPins
                    |> Option.map (fun c -> { Source = FromFixed c; Sink = sinkIndex })))
        |> List.concat
        |> List.toArray

    // --- コスト ---------------------------------------------------------

    /// コストの固定小数点スケール。BackwardPenalty (実数) を含むコストを int64 で厳密に
    /// 足し引きし、増分評価と全再計算を完全一致させるため (浮動小数点の累積誤差を避ける)。
    [<Literal>]
    let CostScale = 1000L

    let costToCells (cost: int64) : float = float cost / float CostScale

    let private scaledPenalty (penalty: float) : int64 =
        int64 (System.Math.Round (penalty * float CostScale))

    /// 1 アークのコスト (スケール済み)。
    let private arcCostAt (penaltyScaled: int64) (sx: int) (sy: int) (dx: int) (dy: int) : int64 =
        let manhattan = int64 (abs (sx - dx) + abs (sy - dy))
        let backward = int64 (max 0 (sx - dx))
        CostScale * manhattan + penaltyScaled * backward

    /// 全アークのコストを一から計算する (検証・初期値用)。
    let totalCost (grid: SlotGrid) (arcs: Arc[]) (backwardPenalty: float) (assignment: Assignment) : int64 =
        let penaltyScaled = scaledPenalty backwardPenalty
        let coordOfGate (g: int) = slotCoord grid assignment.[g]
        arcs
        |> Array.sumBy (fun arc ->
            let src =
                match arc.Source with
                | FromGate g -> coordOfGate g
                | FromFixed c -> c
            let dst = coordOfGate arc.Sink
            arcCostAt penaltyScaled src.X src.Y dst.X dst.Y)

    /// 割り当ての妥当性: 全ゲートがちょうど 1 スロット、スロット範囲内、重複なし。
    let validateAssignment (grid: SlotGrid) (gateCount: int) (assignment: Assignment) : Result<unit, string> =
        let slots = slotCount grid
        if assignment.Length <> gateCount then
            Error (sprintf "長さ %d がゲート数 %d と一致しない" assignment.Length gateCount)
        elif gateCount > slots then
            Error (sprintf "ゲート数 %d がスロット数 %d を超える" gateCount slots)
        else
            let used = Array.zeroCreate<bool> slots
            let rec check (g: int) =
                if g = gateCount then Ok ()
                else
                    let s = assignment.[g]
                    if s < 0 || s >= slots then Error (sprintf "ゲート %d のスロット %d が範囲外 (0..%d)" g s (slots - 1))
                    elif used.[s] then Error (sprintf "スロット %d が重複 (ゲート %d)" s g)
                    else
                        used.[s] <- true
                        check (g + 1)
            check 0

    // --- アニーリング ---------------------------------------------------

    /// 窓半径の適応 (VPR 流): 受理率がこの値に近づくよう半径を伸縮させる。
    [<Literal>]
    let private TargetAcceptanceRate = 0.44

    /// 半径の下限 (スロット単位)。隣接スロットとの交換は常に試せるようにする。
    [<Literal>]
    let private MinWindowRadius = 1.0

    type AnnealOutcome =
        { /// 最良解 (≤ 初期解)
          Best: Assignment
          InitialCost: int64
          BestCost: int64
          /// 最終状態 (最良とは限らない) とその増分評価コスト。増分評価の検証用。
          Last: Assignment
          LastCostIncremental: int64
          AcceptedMoves: int
          AttemptedMoves: int }

    /// SplitMix64。状態を 1 つの uint64 で持つ最小の決定的乱数。
    type private SplitMix64(seed: uint64) =
        let mutable state = seed
        member _.NextUInt64 () : uint64 =
            state <- state + 0x9E3779B97F4A7C15UL
            let mutable z = state
            z <- (z ^^^ (z >>> 30)) * 0xBF58476D1CE4E5B9UL
            z <- (z ^^^ (z >>> 27)) * 0x94D049BB133111EBUL
            z ^^^ (z >>> 31)
        /// [0, n) の整数 (n > 0)。
        member this.NextInt (n: int) : int = int (this.NextUInt64 () % uint64 n)
        /// [0, 1) の実数 (上位 53 bit)。
        member this.NextDouble () : float = float (this.NextUInt64 () >>> 11) * (1.0 / 9007199254740992.0)

    let private validateConfig (cfg: AnnealConfig) : Result<unit, AnnealConfigError> =
        if cfg.Moves < 0 then Error (NegativeMoves cfg.Moves)
        elif cfg.InitialTemperature <= 0.0 || cfg.FinalTemperature <= 0.0 then
            Error (NonPositiveTemperature (cfg.InitialTemperature, cfg.FinalTemperature))
        elif cfg.FinalTemperature > cfg.InitialTemperature then
            Error (FinalAboveInitial (cfg.InitialTemperature, cfg.FinalTemperature))
        elif cfg.BackwardPenalty < 0.0 then Error (NegativeBackwardPenalty cfg.BackwardPenalty)
        else Ok ()

    /// ゲート → 接続アーク番号の CSR 索引 (offsets.[g] .. offsets.[g+1]-1)。
    let private buildGateArcIndex (gateCount: int) (arcs: Arc[]) : int[] * int[] =
        let degree = Array.zeroCreate<int> gateCount
        for arc in arcs do
            degree.[arc.Sink] <- degree.[arc.Sink] + 1
            match arc.Source with
            | FromGate g -> degree.[g] <- degree.[g] + 1
            | FromFixed _ -> ()
        let offsets = Array.zeroCreate<int> (gateCount + 1)
        for g in 0 .. gateCount - 1 do
            offsets.[g + 1] <- offsets.[g] + degree.[g]
        let cursor = Array.copy offsets
        let index = Array.zeroCreate<int> offsets.[gateCount]
        arcs
        |> Array.iteri (fun i arc ->
            index.[cursor.[arc.Sink]] <- i
            cursor.[arc.Sink] <- cursor.[arc.Sink] + 1
            match arc.Source with
            | FromGate g ->
                index.[cursor.[g]] <- i
                cursor.[g] <- cursor.[g] + 1
            | FromFixed _ -> ())
        offsets, index

    /// シミュレーテッドアニーリング。initial は変更しない (内部でコピーする)。
    /// 内部は配列の破壊的更新で書くが、関数としては入力 → 出力の純粋関数。
    let anneal
        (cfg: AnnealConfig)
        (grid: SlotGrid)
        (gateCount: int)
        (arcs: Arc[])
        (initial: Assignment)
        : Result<AnnealOutcome, AnnealConfigError> =
        let validated =
            validateConfig cfg
            |> Result.bind (fun () ->
                validateAssignment grid gateCount initial |> Result.mapError InvalidInitialAssignment)
        match validated with
        | Error e -> Error e
        | Ok () ->
            let slots = slotCount grid
            let penaltyScaled = scaledPenalty cfg.BackwardPenalty
            // スロット座標を前計算 (内側ループで除算しない)
            let slotX = Array.init slots (fun s -> (slotCoord grid s).X)
            let slotY = Array.init slots (fun s -> (slotCoord grid s).Y)
            // アークを SoA に展開: 駆動元ゲート (-1 = 固定端子) と固定座標
            let arcSrcGate = arcs |> Array.map (fun a -> match a.Source with FromGate g -> g | FromFixed _ -> -1)
            let arcFixedX = arcs |> Array.map (fun a -> match a.Source with FromFixed c -> c.X | FromGate _ -> 0)
            let arcFixedY = arcs |> Array.map (fun a -> match a.Source with FromFixed c -> c.Y | FromGate _ -> 0)
            let arcSink = arcs |> Array.map (fun a -> a.Sink)
            let offsets, gateArcs = buildGateArcIndex gateCount arcs

            let gateSlot = Array.copy initial
            let slotGate = Array.create slots -1
            gateSlot |> Array.iteri (fun g s -> slotGate.[s] <- g)

            let arcCost (i: int) : int64 =
                let dstSlot = gateSlot.[arcSink.[i]]
                let src = arcSrcGate.[i]
                if src >= 0 then
                    let srcSlot = gateSlot.[src]
                    arcCostAt penaltyScaled slotX.[srcSlot] slotY.[srcSlot] slotX.[dstSlot] slotY.[dstSlot]
                else
                    arcCostAt penaltyScaled arcFixedX.[i] arcFixedY.[i] slotX.[dstSlot] slotY.[dstSlot]

            /// a と b (b = -1 なら空き) に接続するアークのコスト和。a-b 間のアークは 1 回だけ数える。
            let affectedCost (a: int) (b: int) : int64 =
                let mutable sum = 0L
                for k in offsets.[a] .. offsets.[a + 1] - 1 do
                    sum <- sum + arcCost gateArcs.[k]
                if b >= 0 then
                    for k in offsets.[b] .. offsets.[b + 1] - 1 do
                        let i = gateArcs.[k]
                        let touchesA = arcSink.[i] = a || arcSrcGate.[i] = a
                        if not touchesA then sum <- sum + arcCost i
                sum

            /// a をスロット target へ動かす (占有者がいれば a の元スロットへ)。
            let moveGate (a: int) (target: int) =
                let from = gateSlot.[a]
                let b = slotGate.[target]
                gateSlot.[a] <- target
                slotGate.[target] <- a
                slotGate.[from] <- b
                if b >= 0 then gateSlot.[b] <- from

            let initialCost = totalCost grid arcs cfg.BackwardPenalty initial
            let mutable current = initialCost
            let mutable bestCost = initialCost
            let best = Array.copy initial
            let rng = SplitMix64 cfg.Seed
            let maxRadius = float (max grid.Columns grid.Rows)
            let mutable radius = maxRadius
            // 温度は Moves 回で T0 → T1 に幾何減衰する
            let coolingFactor =
                if cfg.Moves = 0 then 1.0
                else (cfg.FinalTemperature / cfg.InitialTemperature) ** (1.0 / float cfg.Moves)
            let mutable temperature = cfg.InitialTemperature
            // 温度 (格子単位) → コスト単位 (セル × CostScale) への換算
            let temperatureScale = float (grid.PitchX + grid.PitchY) / 2.0 * float CostScale
            // 最良解のスナップショットと窓半径の適応はバッチ単位 (O(n) のコピーを償却する)
            let batchSize = max 1 gateCount
            let mutable batchAccepted = 0
            let mutable batchAttempted = 0
            let mutable accepted = 0

            let endBatch () =
                if current < bestCost then
                    bestCost <- current
                    Array.blit gateSlot 0 best 0 gateCount
                if batchAttempted > 0 then
                    let rate = float batchAccepted / float batchAttempted
                    radius <- radius * (1.0 - TargetAcceptanceRate + rate) |> max MinWindowRadius |> min maxRadius
                batchAccepted <- 0
                batchAttempted <- 0

            if gateCount > 0 then
                for move in 1 .. cfg.Moves do
                    let a = rng.NextInt gateCount
                    let from = gateSlot.[a]
                    let r = int radius
                    let col = from % grid.Columns + rng.NextInt (2 * r + 1) - r
                    let row = from / grid.Columns + rng.NextInt (2 * r + 1) - r
                    let colC = col |> max 0 |> min (grid.Columns - 1)
                    let rowC = row |> max 0 |> min (grid.Rows - 1)
                    let target = rowC * grid.Columns + colC
                    batchAttempted <- batchAttempted + 1
                    if target <> from then
                        let b = slotGate.[target]
                        let before = affectedCost a b
                        moveGate a target
                        let after = affectedCost a b
                        let delta = after - before
                        let accept =
                            delta <= 0L
                            || rng.NextDouble () < exp (-(float delta) / (temperature * temperatureScale))
                        if accept then
                            current <- current + delta
                            accepted <- accepted + 1
                            batchAccepted <- batchAccepted + 1
                        else
                            // 元に戻す: a は target にいるので from へ戻せば b も target へ戻る
                            moveGate a from
                    temperature <- temperature * coolingFactor
                    if move % batchSize = 0 then endBatch ()
                endBatch ()

            Ok { Best = best
                 InitialCost = initialCost
                 BestCost = bestCost
                 Last = Array.copy gateSlot
                 LastCostIncremental = current
                 AcceptedMoves = accepted
                 AttemptedMoves = cfg.Moves }
