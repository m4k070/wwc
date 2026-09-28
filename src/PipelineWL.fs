namespace WwHdl

// ---------------------------------------------------------------------
// 9. PipelineWL — yosys Netlist → WireLevel コンパイルパイプライン
//
// WireWorld 版 Pipeline との違い:
//   * ゲートは 1 セル (LNand/LDff)。テクノロジマッピングは事実上不要。
//   * 配線は pull 型有向ワイヤ。パスの各セルの dir = 進行方向。
//   * 交差は回避せず Cross セル化する → 輻輳問題が構造的に消える。
//   * STA なし。レベルは自然収束するので settle で待つだけ。
//
// 配置・配線の不変条件 (WireLevel.fs 冒頭の配置制約に対応):
//   * LNand は出力方向以外の非空隣接セルをすべて入力として読む。
//     → ゲートの4近傍は「入力終端 / 出力先頭 / 強制空白」のみに限定する。
//   * LDff は背面=D、片側面=CLK。残り側面は強制空白。
//   * 終端セル・タップ済みセル・コーナーは Cross 化できない (直線セルのみ可)。
// ---------------------------------------------------------------------
module PipelineWL =
    open Domain
    open Netlist
    open WireLevel
    open Route      // CompileError
    open Pipeline   // frontend (yosys JSON → Netlist)

    /// WireLevel 上の配置済みゲート。v1 は全ゲート東向き。
    type WlPlaced =
        { Gate: Gate
          Coord: Coord
          Dir: Dir }

    /// ルーティング占有グリッド。
    type OccCell =
        | OccGate  of NetId
        | OccWire  of net: NetId * flow: Dir * straight: bool
        | OccCross of hNet: NetId * hDir: Dir * vNet: NetId * vDir: Dir

    type OccGrid = Map<Coord, OccCell>

    let private addC (a: Coord) (b: Coord) = { X = a.X + b.X; Y = a.Y + b.Y }
    let private toward (c: Coord) (d: Dir) = addC c (delta d)

    let private perpendicular (a: Dir) (b: Dir) =
        match a, b with
        | (E | W), (N | S) | (N | S), (E | W) -> true
        | _ -> false

    // --- 配置 ---------------------------------------------------------

    let private gateX0 = 12

    /// ピッチ拡大シーケンス (狭い→広い)。compileWL が基本ピッチから開始し、
    /// 輻輳失敗時は一段ずつ広げて再試行する。
    let private pitchSequence : (int * int) list =
        // 10k ゲート級 (sm83_full) は 24x16 でも輻輳で通らなかった (2026-09-18: 15,153/18,037 端子)
        // ため、さらに広いピッチを用意する。グリッド面積は増えるが、A* の同点処理を入れてからは
        // 探索コストが距離に比例する程度で収まる
        [ (12, 10); (16, 12); (20, 14); (24, 16); (28, 20); (32, 24) ]

    /// 回路規模 (ゲート数) に応じた配置ピッチ (基本ピッチ)。
    /// 大規模回路では grid 面積爆発を防ぐため縮小する。
    /// 12x10 は飽和が早すぎるため大規模の基本ピッチには使わない
    /// (失敗時は pitchSequence に沿って自動拡大される)。
    let private pitchFor (nGates: int) : int * int =
        if nGates <= 200 then 24, 16
        elif nGates <= 1000 then 20, 14
        else 16, 12

    /// ゲート格子の左上 (外部入力ピン列 X=0 との間に配線用の余白を取る)。
    let private gateOrigin : Coord = { X = gateX0; Y = 2 }

    /// 外部入力ピン (クロックを含む) の左端列の座標。i 番目のピンは Y = 2 + i*pitchY。
    let private leftEdgePins (pitchY: int) (nl: Netlist) : Map<NetId, Coord> =
        nl.PrimaryInputs
        |> List.mapi (fun i netId -> netId, { X = 0; Y = 2 + i * pitchY })
        |> Map.ofList

    // --- 終端割り当て ---------------------------------------------------
    //
    // clockPinCoords / inputPinCoords (下記) が「ネットの受け手群」を必要とするため、
    // 配線コード本体 (routeWLWith) より前に定義する。

    /// ゲートの入力ネット → 終端セル割り当てと、強制空白にすべき側面セル。
    /// ゲートは東向き前提: W=背面, N/S=側面, E=出力。
    /// $_DFF_P_ の Inputs はポート名アルファベット順で [C; D]。
    /// $_DFF_PP0_ の Inputs は [C; D; R] (R = async reset, 無視)。
    let private gateTerminals (p: WlPlaced) : (NetId * Coord) list * Coord list =
        let w = toward p.Coord W
        let n = toward p.Coord N
        let s = toward p.Coord S
        match p.Gate.Kind, p.Gate.Inputs with
        | Dff, [clkNet; dNet] -> [ (dNet, w); (clkNet, s) ], [ n ]
        | Dff, [cNet; dNet; _rNet] -> [ (dNet, w); (cNet, s) ], [ n ]
        | _, [a]              -> [ (a, w) ], [ n; s ]
        | _, [a; b]           -> [ (a, w); (b, n) ], [ s ]
        | _, [a; b; c]        -> [ (a, w); (b, n); (c, s) ], []
        | _, ins ->
            // 4 入力以上は v1 未対応 (yosys NAND/NOT 分解では発生しない)
            (ins |> List.mapi (fun i nid -> nid, [w; n; s].[i % 3])), []

    /// 配置済みの全ゲートについて、ネット → 受け手 (ゲート入力終端) 座標一覧。
    /// クロックピン・外部入力ピンの配置 (L1 ミニマックス中心) と、その検証テストの両方が使う。
    let externalInputTerminals (placed: WlPlaced list) : Map<NetId, Coord list> =
        placed
        |> List.collect (fun p -> fst (gateTerminals p))
        |> List.groupBy fst
        |> List.map (fun (netId, terms) -> netId, terms |> List.map snd)
        |> Map.ofList

    // --- クロック方式 ---------------------------------------------------

    /// 配置配線が扱うクロック網 (クロック方式の変換後)。
    type CircuitClocking =
        /// 単相。クロックは元のネットリストの ClockNet (組合せ回路なら None)
        | SingleEdgeClock of clock: NetId option
        /// 2 相。配置配線するのは TwoPhaseNetlist.Netlist (マスター / スレーブ分割後)
        | TwoPhaseClock of Clocking.TwoPhaseNetlist

    /// クロック終端 (DFF のクロック端子) を配線する順番。
    type ClockTerminalOrder =
        /// ピンから遠い終端から (幹を先に引き、近い終端はそこからタップする)
        | FarthestFirst
        /// ピンに近い終端から (木を内側から外へ育てる)。
        /// sm83_subset (2 相 anneal) で FarthestFirst と最大到達時間は同じ (下限どおり) で、
        /// 木のセル数が約 25% 少なく (9,520 vs 12,688)、配線も速い (13 秒 vs 33 秒)。
        /// 遠い終端が先に育った内側の木から枝を伸ばせるため
        | NearestFirst

    /// クロック網の配線方針。
    type ClockRouting =
        /// クロック優先配線 + 終端近傍のタップ禁止 + skew 均等化 (単相の hold 対策)
        | BalancedSkew
        /// クロック優先配線のみ。新しく引く配線の長さを最小にする (skew も latency も揃えない)
        | ShortestOnly
        /// クロック優先配線を最短経路木 (shortest-path tree) で行う: 各終端への経路を
        /// 「ピンからの到達時間」最小で選ぶ (同点なら新しく引く配線が短いほう)。
        /// 2 相は相の間の settle で hold を守るので、クロックに要るのは最大到達時間の短さだけ
        | MinLatency of order: ClockTerminalOrder

    /// 配置配線にかける回路 (クロック方式の変換を済ませたもの)。
    type PreparedCircuit =
        { Netlist: Netlist
          Clocking: CircuitClocking }

    /// クロックネット (ピンを DFF 群の重心に置く対象) と、それぞれが駆動する DFF の判定。
    let clockNetsOf (clocking: CircuitClocking) : NetId list =
        match clocking with
        | SingleEdgeClock clock -> Option.toList clock
        | TwoPhaseClock tp -> [ tp.ClockA; tp.ClockB ]

    let clockRoutingOf (clocking: CircuitClocking) : ClockRouting =
        match clocking with
        | SingleEdgeClock _ -> BalancedSkew
        | TwoPhaseClock _ -> MinLatency NearestFirst

    /// クロック方式に応じてネットリストを変換する (純粋関数)。
    let prepareCircuit (scheme: Clocking.ClockingScheme) (nl: Netlist) : Result<PreparedCircuit, CompileError> =
        match scheme with
        | Clocking.SingleEdge -> Ok { Netlist = nl; Clocking = SingleEdgeClock nl.ClockNet }
        | Clocking.TwoPhase ->
            Clocking.toTwoPhase nl
            |> Result.mapError UnsupportedClocking
            |> Result.map (fun tp -> { Netlist = tp.Netlist; Clocking = TwoPhaseClock tp })

    /// 端子群の L1 連続ミニマックス中心 (格子にスナップする前) と、その半径 (理論下限)。
    /// u = x+y と v = x−y に変換すると L1 球は軸平行の正方形になり、各軸で独立に
    /// 最小・最大の中点を取れる (中心からの最大距離 = max のレンジの半分)。
    /// minimaxGapPin (配置) と WL-2PH 入力ピンテスト (下限との比の検証) の両方が使う。
    let minimaxCenter (terminals: Coord list) : (float * float) * float =
        let range (f: Coord -> int) =
            let vs = terminals |> List.map f
            List.min vs, List.max vs
        let uLo, uHi = range (fun t -> t.X + t.Y)
        let vLo, vHi = range (fun t -> t.X - t.Y)
        let uc = float (uLo + uHi) / 2.0
        let vc = float (vLo + vHi) / 2.0
        let cx = (uc + vc) / 2.0
        let cy = (uc - vc) / 2.0
        let bound = max (float (uHi - uLo)) (float (vHi - vLo)) / 2.0
        (cx, cy), bound

    /// クロック端子群 terminals への最大マンハッタン距離が最小になる格子の隙間 (taken 以外)。
    /// 連続ミニマックス中心 (minimaxCenter) を格子の隙間 (ピッチの半分ずらし) にスナップし、
    /// 周囲の隙間から最大距離が最小のものを選ぶ。
    let private minimaxGapPin
        (grid: GatePlacement.SlotGrid)
        (terminals: Coord list)
        (taken: Set<Coord>)
        : Coord =
        let maxDist (c: Coord) =
            terminals |> List.map (fun t -> abs (t.X - c.X) + abs (t.Y - c.Y)) |> List.max
        let (cx, cy), _ = minimaxCenter terminals
        // 隙間 k 番目の座標 = origin + k*pitch + pitch/2 (k = 0 .. slots-1)
        let gapIndex (v: float) (pitch: int) (origin: int) =
            int (floor ((v - float origin - float (pitch / 2)) / float pitch))
        let gapAt (k: int) (pitch: int) (origin: int) = origin + k * pitch + pitch / 2
        let kx = gapIndex cx grid.PitchX grid.Origin.X
        let ky = gapIndex cy grid.PitchY grid.Origin.Y
        let clampTo (hi: int) (k: int) = max 0 (min (hi - 1) k)
        // 半径 SearchRadius 個分の隙間を候補にする (中心が塞がっていても近くに置ける)
        let searchRadius = 3
        [ for dx in -searchRadius .. searchRadius do
            for dy in -searchRadius .. searchRadius do
                yield { X = gapAt (clampTo grid.Columns (kx + dx)) grid.PitchX grid.Origin.X
                        Y = gapAt (clampTo grid.Rows (ky + dy)) grid.PitchY grid.Origin.Y } ]
        |> List.distinct
        |> List.filter (fun c -> not (Set.contains c taken))
        |> List.minBy (fun c -> maxDist c, abs (float c.X - cx) + abs (float c.Y - cy))

    /// クロックピンの座標。単相は各クロックのピンを、そのクロックが駆動する DFF 群の重心に置く:
    /// 左端からだと DFF までの距離差がそのままクロックスキューになるため (sm83_full の行優先配置で
    /// skew 494〜578、hold 違反の許容は約 316)。重心はゲート格子の隙間 (ピッチの半分ずらし) に取り、
    /// ゲートや終端と重ならないようにする。2 相でマスター群とスレーブ群の重心が同じ隙間に
    /// 落ちたら、後のクロックを 1 ピッチずつ右へずらす。
    /// 2 相は最大到達時間の下限 (ピンから最も遠い端子までの距離) を最小にするため、
    /// 端子群の L1 ミニマックス中心 (minimaxGapPin) に置く。
    let private clockPinCoords
        (grid: GatePlacement.SlotGrid)
        (placed: WlPlaced list)
        (clocking: CircuitClocking)
        : (NetId * Coord) list =
        let dffs = placed |> List.filter (fun p -> p.Gate.Kind = Dff)
        let drivenBy (clk: NetId) =
            match clocking with
            // 単相は従来どおり全 DFF の重心 (DFF の C が ClockNet 以外でも同じ位置になるように)
            | SingleEdgeClock _ -> dffs
            | TwoPhaseClock _ -> dffs |> List.filter (fun p -> List.tryHead p.Gate.Inputs = Some clk)
        let snap (v: int) (pitch: int) (origin: int) =
            origin + ((v - origin) / pitch) * pitch + pitch / 2
        clockNetsOf clocking
        |> List.fold (fun (acc: (NetId * Coord) list) clk ->
            match drivenBy clk with
            | [] -> acc
            | group ->
                let taken = acc |> List.map snd |> Set.ofList
                let pin =
                    match clocking with
                    | SingleEdgeClock _ ->
                        let cx = group |> List.averageBy (fun p -> float p.Coord.X) |> int
                        let cy = group |> List.averageBy (fun p -> float p.Coord.Y) |> int
                        let centroid = { X = snap cx grid.PitchX grid.Origin.X; Y = snap cy grid.PitchY grid.Origin.Y }
                        let rec freeFrom (c: Coord) =
                            if Set.contains c taken then freeFrom { c with X = c.X + grid.PitchX } else c
                        freeFrom centroid
                    | TwoPhaseClock _ ->
                        minimaxGapPin grid (group |> List.map (fun p -> toward p.Coord S)) taken
                acc @ [ clk, pin ])
            []

    /// クロック以外の外部入力ピン (data_in などビットごと) の座標。2 相のときだけ、
    /// 左端 (X=0) からそのネットの受け手 (ゲート入力終端) 群への L1 ミニマックス中心へ移す
    /// (クロックピンと同じ考え方。issue #7 (a))。単相・行優先の既存経路は変えない —
    /// SingleEdgeClock では常に None を返し、呼び出し側は左端のまま使う。
    /// ビットごとに 1 つずつ確定し、既に確定した他のピン (クロックピン含む) を taken に積んで
    /// 重ならない隙間へスナップする。受け手が無い (未使用) 入力は None のまま左端に残す。
    let private nonClockInputPinCoords
        (grid: GatePlacement.SlotGrid)
        (placed: WlPlaced list)
        (clocking: CircuitClocking)
        (nl: Netlist)
        (basePins: Map<NetId, Coord>)
        : Map<NetId, Coord> =
        match clocking with
        | SingleEdgeClock _ -> basePins
        | TwoPhaseClock _ ->
            let clockNets = clockNetsOf clocking |> Set.ofList
            let terminalsByNet = externalInputTerminals placed
            nl.PrimaryInputs
            |> List.filter (fun netId -> not (Set.contains netId clockNets))
            |> List.fold (fun (acc: Map<NetId, Coord>) netId ->
                match Map.tryFind netId terminalsByNet with
                | None | Some [] -> acc  // 受け手が無い (未使用) 入力は左端のまま
                | Some terminals ->
                    let taken = acc |> Map.toList |> List.map snd |> Set.ofList
                    Map.add netId (minimaxGapPin grid terminals taken) acc)
                basePins

    /// 割り当て (ゲート → スロット) から配置とピン座標を作る。
    let private placeFromAssignment
        (grid: GatePlacement.SlotGrid)
        (circuit: PreparedCircuit)
        (assignment: GatePlacement.Assignment)
        : WlPlaced list * Map<NetId, Coord> =
        let nl = circuit.Netlist
        let placed =
            nl.Gates |> List.mapi (fun i g ->
                { Gate = g
                  Coord = GatePlacement.slotCoord grid assignment.[i]
                  Dir = E })
        let withClockPins =
            clockPinCoords grid placed circuit.Clocking
            |> List.fold (fun acc (clk, c) -> Map.add clk c acc) (leftEdgePins grid.PitchY nl)
        let pins =
            nonClockInputPinCoords grid placed circuit.Clocking nl withClockPins
        placed, pins

    /// 配置結果。Annealing は Annealed / TimingDriven 戦略のときだけ Some (総アーク距離の
    /// アニーリングの前後コスト)。TimingDriven は TimingDriven に焼き直しの記録が入る。
    type WlPlacement =
        { Placed: WlPlaced list
          Pins: Map<NetId, Coord>
          Annealing: GatePlacement.AnnealOutcome option
          TimingDriven: PlacementTiming.TimingDrivenOutcome option }

    /// 回路とピッチから配置のスロット格子を作る (placeCircuitWithStrategy と同じ格子)。
    let slotGridOf (pitchX: int) (pitchY: int) (circuit: PreparedCircuit) : GatePlacement.SlotGrid =
        GatePlacement.squareSlotGrid circuit.Netlist.Gates.Length gateOrigin pitchX pitchY

    /// 割り当てから配置とピン座標を作る (配置段階の解析スクリプト・テスト用に公開)。
    let placeCircuitFromAssignment
        (grid: GatePlacement.SlotGrid)
        (circuit: PreparedCircuit)
        (assignment: GatePlacement.Assignment)
        : WlPlaced list * Map<NetId, Coord> =
        placeFromAssignment grid circuit assignment

    /// 総アーク距離のアニーリングで使うアーク (外部入力ピンは左端列の固定端子、クロックは除外)。
    let annealArcsOf (pitchY: int) (circuit: PreparedCircuit) : GatePlacement.Arc[] =
        let nl = circuit.Netlist
        // クロックは固定端子にしない (ピンは配置後に DFF 群の重心へ動かすため)。
        // 固定端子に無いネットのアークは buildArcs が捨てる
        let fixedPins =
            clockNetsOf circuit.Clocking
            |> List.fold (fun acc clk -> Map.remove clk acc) (leftEdgePins pitchY nl)
        GatePlacement.buildArcs nl fixedPins

    /// タイミング駆動の再アニーリング (2 相のみ)。ピンは各ラウンドの配置から
    /// placeFromAssignment と同じ規則 (ミニマックス中心) で決め直す。
    let timingDrivenFrom
        (tdCfg: GatePlacement.TimingDrivenConfig)
        (annealCfg: GatePlacement.AnnealConfig)
        (grid: GatePlacement.SlotGrid)
        (circuit: PreparedCircuit)
        (initial: GatePlacement.Assignment)
        : Result<PlacementTiming.TimingDrivenOutcome, CompileError> =
        match circuit.Clocking with
        | SingleEdgeClock _ ->
            Error (InvalidPlacementConfig "タイミング駆動配置は 2 相クロック (--clocking two-phase) のみ対応")
        | TwoPhaseClock tp ->
            let resolvePins (a: GatePlacement.Assignment) = snd (placeFromAssignment grid circuit a)
            PlacementTiming.buildNetwork tp
            |> Result.bind (fun net ->
                PlacementTiming.timingDrivenAnneal
                    PlacementTiming.defaultTimingModel annealCfg tdCfg grid net resolvePins initial)
            |> Result.mapError (PlacementTiming.describePlacementTimingError >> InvalidPlacementConfig)

    /// クロック方式変換済みの回路を配置する。RowMajor は placeWLWithPitch と同一の結果 (単相時)。
    /// Annealed は行優先を初期解にアーク距離を最小化する (クロックネット除外、
    /// 外部入力ピンは左端列の固定端子としてコストに含める)。
    let placeCircuitWithStrategy
        (strategy: GatePlacement.PlacementStrategy)
        (pitchX: int)
        (pitchY: int)
        (circuit: PreparedCircuit)
        : Result<WlPlacement, CompileError> =
        let nl = circuit.Netlist
        let grid = slotGridOf pitchX pitchY circuit
        let initial = GatePlacement.rowMajorAssignment nl.Gates.Length
        let annealed (cfg: GatePlacement.AnnealConfig) =
            GatePlacement.anneal cfg grid nl.Gates.Length (annealArcsOf pitchY circuit) initial
            |> Result.mapError (GatePlacement.describeConfigError >> InvalidPlacementConfig)
        match strategy with
        | GatePlacement.RowMajor ->
            let placed, pins = placeFromAssignment grid circuit initial
            Ok { Placed = placed; Pins = pins; Annealing = None; TimingDriven = None }
        | GatePlacement.Annealed cfg ->
            annealed cfg
            |> Result.map (fun outcome ->
                let placed, pins = placeFromAssignment grid circuit outcome.Best
                { Placed = placed; Pins = pins; Annealing = Some outcome; TimingDriven = None })
        | GatePlacement.TimingDriven (cfg, tdCfg) ->
            annealed cfg
            |> Result.bind (fun outcome ->
                timingDrivenFrom tdCfg cfg grid circuit outcome.Best
                |> Result.map (fun td ->
                    let placed, pins = placeFromAssignment grid circuit td.Best
                    { Placed = placed; Pins = pins; Annealing = Some outcome; TimingDriven = Some td }))

    /// 配置戦略を指定して配置する (単相)。
    let placeWLWithStrategy
        (strategy: GatePlacement.PlacementStrategy)
        (pitchX: int)
        (pitchY: int)
        (nl: Netlist)
        : Result<WlPlacement, CompileError> =
        placeCircuitWithStrategy strategy pitchX pitchY { Netlist = nl; Clocking = SingleEdgeClock nl.ClockNet }

    /// ゲートを JSON 宣言順に正方格子に配置する (ピッチ指定版、行優先、単相)。
    let placeWLWithPitch (pitchX: int) (pitchY: int) (nl: Netlist) : WlPlaced list * Map<NetId, Coord> =
        let grid = GatePlacement.squareSlotGrid nl.Gates.Length gateOrigin pitchX pitchY
        placeFromAssignment grid { Netlist = nl; Clocking = SingleEdgeClock nl.ClockNet }
            (GatePlacement.rowMajorAssignment nl.Gates.Length)

    /// 回路規模に応じたピッチで配置する。
    let placeWL (nl: Netlist) : WlPlaced list * Map<NetId, Coord> =
        let px, py = pitchFor nl.Gates.Length
        placeWLWithPitch px py nl

    // --- 配線 -----------------------------------------------------------

    /// 1 終端の A* で何を最小化するか。
    type private TapCost =
        /// 新しく引く配線の長さ (+ 転回ペナルティ)。データネットと従来のクロック配線
        | MinNewWire
        /// ネットの駆動源からの到達時間 (セル数 = 世代) を第 1 キー、新しく引く配線を第 2 キーにする。
        /// arrivals は既存の木の各セルの到達時間 (駆動源 = 0)。経路を確定したら新セルを書き足す
        | MinArrival of arrivals: System.Collections.Generic.Dictionary<Coord, int>

    /// MinArrival で到達時間 1 世代に掛ける重み。新配線の長さ (+ 転回) は常にこれより小さいので、
    /// 到達時間が辞書式の第 1 キーになる
    [<Literal>]
    let private ArrivalWeight = 65536L

    /// 全ネットを配線して占有グリッドを返す。
    /// 各終端へは「既配線セルからのタップ (ファンアウト)」または
    /// 「駆動ゲートの出力先頭セル」から (Coord, Dir) 状態の A* で配線する。
    /// clockRouting: BalancedSkew = 単相 (タップ禁止 + skew 均等化)、ShortestOnly = 2 相 (均等化しない)。
    let routeWLWith (clockRouting: ClockRouting) (placed: WlPlaced list) (pins: Map<NetId, Coord>) : Result<OccGrid, CompileError> =
        let mutable occ : OccGrid =
            Map.ofList
                [ for p in placed do yield p.Coord, OccGate p.Gate.Output
                  for KeyValue (netId, c) in pins do yield c, OccGate netId ]

        // 強制空白セル (ゲートの未使用側面)
        let forbidden =
            placed |> List.collect (fun p -> snd (gateTerminals p)) |> Set.ofList

        // 予約セル: (座標 → ネット, 出力先頭セルか)。
        //   終端は該当ネットのゴールとしてのみ進入可。
        //   出力先頭セルは該当ネットなら通過可 (初回ルートのシード)。
        let reserved : Map<Coord, NetId * bool> =
            Map.ofList
                [ for p in placed do
                    for (nid, c) in fst (gateTerminals p) do yield c, (nid, false)
                  for p in placed do yield toward p.Coord p.Dir, (p.Gate.Output, true) ]

        let driver = placed |> List.map (fun p -> p.Gate.Output, p) |> Map.ofList

        // DFF クロック終端セル。タップ禁止: 終端経由の数珠つなぎ分配になると
        // 後段 DFF の到達 = 前段到達 + 枝長となり、スキュー均等化が原理的に不可能。
        let clkTerminalCells =
            placed
            |> List.choose (fun p ->
                match p.Gate.Kind with
                | Dff -> Some (toward p.Coord S)
                | _ -> None)
            |> Set.ofList

        // タップ元セル (分岐の読み出し元)。クロック均等化のリップアップ対象から除外する。
        let tapSources = System.Collections.Generic.HashSet<Coord>()

        // タップ禁止半径と、その対象となる終端群 (0 なら制限なし)。
        // クロック配線で使う: タップが終端の直前まで寄ると専有部分 (リーフ edge) が
        // 1〜3 セルしか残らず、スキュー均等化のバンプを 1 つも打てなくなる。
        // 自分のゴールだけでなく「すべてのクロック終端」の近傍を禁止する。
        // 後から配線する枝が先の枝の終端手前にタップすると、その枝の専有部分を奪うため。
        // バンプは 1 か所で 2h (h ≤ 512) 伸ばせるので、数セルの直線区間があれば足りる。
        let mutable tapGuard = 0
        let mutable tapGuardPoints : Coord list = []

        // ネット → タップ可能セル (Cross 化されたセルは除外していく)
        let netCells = System.Collections.Generic.Dictionary<NetId, ResizeArray<Coord>>()
        let addNetCell n c =
            match netCells.TryGetValue n with
            | true, l -> l.Add c
            | _ -> let l = ResizeArray<Coord>() in l.Add c; netCells.[n] <- l
        for KeyValue (n, c) in pins do addNetCell n c

        let routeOneWith (tapCost: TapCost) (netId: NetId) (goal: Coord) : Result<unit, NetId list> =
            // 探索中に passOk false となったセルを占有する他ネットをブロック回数付きで記録。
            // 失敗時にこれを返し、rip-up が「実際に経路を塞いだネット」を撤去できるようにする。
            let blockedCount = System.Collections.Generic.Dictionary<NetId, int>()
            let bumpBlock n =
                match blockedCount.TryGetValue n with
                | true, k -> blockedCount.[n] <- k + 1
                | _ -> blockedCount.[n] <- 1
            let tapCells =
                match netCells.TryGetValue netId with
                | true, l ->
                    let cells = List.ofSeq l
                    if tapGuard <= 0 then cells
                    else
                        let farEnough (t: Coord) =
                            tapGuardPoints
                            |> List.forall (fun g -> abs (t.X - g.X) + abs (t.Y - g.Y) >= tapGuard)
                        match cells |> List.filter farEnough with
                        | [] -> cells   // 候補が消えるなら制限しない (接続を優先)
                        | filtered -> filtered
                | _ -> []
            // タップ元セルの到達時間 (MinArrival のみ意味を持つ。駆動源は 0)
            let arrivalOf (t: Coord) =
                match tapCost with
                | MinArrival arrivals ->
                    match arrivals.TryGetValue t with
                    | true, a -> a
                    | _ -> 0
                | MinNewWire -> 0
            // シード: (セル, 進入方向, そのセルでの到達時間)
            let seeds =
                if tapCells.IsEmpty then
                    match Map.tryFind netId driver with
                    | Some p -> [ (toward p.Coord p.Dir, p.Dir, 1) ]
                    | None ->
                        match Map.tryFind netId pins with
                        | Some c -> [ for d in [E; W; N; S] do yield toward c d, d, 1 ]
                        | None -> []
                else
                    [ for t in tapCells do
                        for d in [E; W; N; S] do
                            yield (toward t d, d, arrivalOf t + 1) ]

            let isCrossingCell (c: Coord) =
                match Map.tryFind c occ with
                | Some (OccWire (n2, _, _)) -> n2 <> netId
                | _ -> false

            // リトライループ: 探索範囲 (bbox マージン) を拡大しながら A* を再試行する。
            // 転回ペナルティ: コーナーは交差不可なので直線経路を優先し、後続ネットが
            // 交差できるセルを増やす (輻輳対策)。
            let h (c: Coord) = abs (c.X - goal.X) + abs (c.Y - goal.Y)
            let pts = goal :: (seeds |> List.map (fun (c, _, _) -> c))
            // 1 歩あたりの到達時間の重み (MinNewWire では 0 = 従来どおり新配線の長さだけ)
            let arrivalWeight =
                match tapCost with
                | MinArrival _ -> ArrivalWeight
                | MinNewWire -> 0L
            let mutable exploreMult = 1
            let mutable result = None
            while result.IsNone && exploreMult <= 16 do
                let margin = 60 * exploreMult
                let minX = (pts |> List.map (fun c -> c.X) |> List.min) - margin
                let maxX = (pts |> List.map (fun c -> c.X) |> List.max) + margin
                let minY = (pts |> List.map (fun c -> c.Y) |> List.min) - margin
                let maxY = (pts |> List.map (fun c -> c.Y) |> List.max) + margin
                let inB (c: Coord) = c.X >= minX && c.X <= maxX && c.Y >= minY && c.Y <= maxY
                let passOk (c: Coord) (nd: Dir) =
                    if not (inB c) || Set.contains c forbidden then false
                    else
                        let resOk =
                            match Map.tryFind c reserved with
                            | Some (n, isFirst) -> n = netId && (isFirst || c = goal)
                            | None -> true
                        resOk &&
                        (match Map.tryFind c occ with
                         | None -> true
                         | Some (OccWire (n2, f2, straight)) ->
                             // 他ネットの直線セルは直交方向に通過可 (Cross 化)
                             n2 <> netId && straight && perpendicular nd f2 && c <> goal
                         | Some _ -> false)
                let bboxArea = (maxX - minX + 1) * (maxY - minY + 1)
                // 探索上限: 経路が存在しないネットは上限まで探索してから失敗するため、
                // 上限を下げると「早く諦めて rip-up に回す」ことができ、最悪コストが 1/10 になる。
                // (経路が存在するネットはヒューリスティックが効き、上限に達しない)
                let maxExplore = min (bboxArea * 8) 2000000
                // 優先度: f = g + h。同じ f ならゴールに近い (h の小さい) 状態を先に見る。
                // 序盤のグリッドはほぼ空で同コストの経路が大量にあり、素の f だけだと
                // その「平地」を一様に広げて探索が爆発する (24x16 のクロックネットで
                // 1 終端 12 分・上限 500 万到達)。同点処理を入れても経路の最適性は保たれる。
                // g は arrivalWeight × 到達時間 + 新配線の長さ (+ 転回)。1 歩で g は
                // arrivalWeight + 1 以上増えるので、h × (arrivalWeight + 1) は許容的かつ一貫的
                let inline priorityOf (g: int64) (hv: int) =
                    (g + int64 hv * (arrivalWeight + 1L)) * 1024L + int64 hv
                // 転回ペナルティ: コーナーは交差不可なので直線経路を優先し、
                // 後続ネットが交差できるセルを増やす (輻輳対策)。
                // リトライが進むほど下げる (4→2→1) — 初回は直線優先で交差余地を
                // 温存し、輻輳が深刻なリトライでは柔軟な経路選択を許容する。
                let turnPenalty = max 1 (4 / exploreMult)
                let pq = System.Collections.Generic.PriorityQueue<Coord * Dir, int64>()
                let gScore = System.Collections.Generic.Dictionary<Coord * Dir, int64>()
                let prev = System.Collections.Generic.Dictionary<Coord * Dir, (Coord * Dir) option>()
                let closed = System.Collections.Generic.HashSet<Coord * Dir>()
                for (c, d, arrival) in seeds do
                    let g0 = arrivalWeight * int64 arrival + 1L
                    if passOk c d && (not (gScore.ContainsKey ((c, d))) || g0 < gScore.[(c, d)]) then
                        gScore.[(c, d)] <- g0
                        prev.[(c, d)] <- None
                        pq.Enqueue ((c, d), priorityOf g0 (h c))
                let mutable explored = 0
                let mutable goalState = None
                while goalState.IsNone && pq.Count > 0 && explored < maxExplore do
                    let (c, d) = pq.Dequeue ()
                    if closed.Add ((c, d)) then
                        explored <- explored + 1
                        if c = goal then goalState <- Some (c, d)
                        else
                            let dirs = if isCrossingCell c then [d] else [E; W; N; S]
                            let gc = gScore.[(c, d)]
                            for nd in dirs do
                                let c' = toward c nd
                                if not (closed.Contains ((c', nd))) then
                                    if passOk c' nd then
                                        let ng = gc + arrivalWeight + 1L + (if nd <> d then int64 turnPenalty else 0L)
                                        if not (gScore.ContainsKey ((c', nd))) || ng < gScore.[(c', nd)] then
                                            gScore.[(c', nd)] <- ng
                                            prev.[(c', nd)] <- Some (c, d)
                                            pq.Enqueue ((c', nd), priorityOf ng (h c'))
                                    else
                                        // ブロッカー記録 (occ によるブロックのみ)
                                        match Map.tryFind c' occ with
                                        | Some (OccWire (n2, _, _)) when n2 <> netId -> bumpBlock n2
                                        | Some (OccCross (hN, _, vN, _)) ->
                                            if hN <> netId then bumpBlock hN
                                            if vN <> netId then bumpBlock vN
                                        | _ -> ()
                match goalState with
                | Some s ->
                    let rec back acc st =
                        match prev.[st] with
                        | None -> st :: acc
                        | Some p -> back (st :: acc) p
                    let path = back [] s |> Array.ofList
                    // タップ元セルを非交差化:
                    // 分岐はタップセルの全方位提示に依存するため、後から Cross 化されると壊れる。
                    let (c0, d0) = path.[0]
                    let tapC = { X = c0.X - (delta d0).X; Y = c0.Y - (delta d0).Y }
                    (match Map.tryFind tapC occ with
                     | Some (OccWire (n2, f2, _)) ->
                         occ <- Map.add tapC (OccWire (n2, f2, false)) occ
                         tapSources.Add tapC |> ignore
                     | _ -> ())
                    // 新セルの到達時間 = タップ元 + 1, +2, … (Cross も 1 世代。WireLevel.clockArrivals と同じ数え方)
                    (match tapCost with
                     | MinArrival arrivals ->
                         let tapArrival = arrivalOf tapC
                         path |> Array.iteri (fun i (c, _) -> arrivals.[c] <- tapArrival + i + 1)
                     | MinNewWire -> ())
                    path |> Array.iteri (fun i (c, d) ->
                        let straight = i < path.Length - 1 && snd path.[i + 1] = d
                        match Map.tryFind c occ with
                        | Some (OccWire (n2, f2, _)) ->
                            // 直交通過 → Cross 化。元ネットはこのセルをタップ不可に。
                            let (hN, hD), (vN, vD) =
                                if d = E || d = W then (netId, d), (n2, f2)
                                else (n2, f2), (netId, d)
                            occ <- Map.add c (OccCross (hN, hD, vN, vD)) occ
                            (match netCells.TryGetValue n2 with
                             | true, l -> l.Remove c |> ignore
                             | _ -> ())
                        | _ ->
                            occ <- Map.add c (OccWire (netId, d, straight)) occ
                            if not (c = goal && Set.contains c clkTerminalCells) then
                                addNetCell netId c)
                    result <- Some (Ok ())
                | None ->
                    exploreMult <- exploreMult * 2
                    if exploreMult >= 4 then
                        eprintfn "[route] NetId %A 探索拡大 mult=%d (bbox %d cells, 上限 %d)"
                            netId exploreMult bboxArea maxExplore
            match result with
            | Some r ->
                if exploreMult > 1 then
                    eprintfn "[route] NetId %A ok after mult=%d" netId exploreMult
                r
            | None -> Error (blockedCount |> Seq.sortByDescending (fun (KeyValue (_, k)) -> k) |> Seq.map (fun (KeyValue (n, _)) -> n) |> List.ofSeq)

        let routeOne (netId: NetId) (goal: Coord) : Result<unit, NetId list> =
            routeOneWith MinNewWire netId goal

        // --- クロックスキュー均等化 (P1: hold 対策) -----------------------
        // WireLevel は配線セル 1 個 = 1 世代なので、クロック枝の長さを揃えれば
        // スキューが消える。クロック木 (タップ = 分岐点) をボトムアップに辿り、
        // 短い枝の直線部分をコの字バンプ (+2h セル) に置換して枝長を加算する。
        // 経路長のパリティは端点で固定されるため、分岐点ごとに残差 1 がありうる。

        /// 終端セルから駆動源 (Pin/ゲート) まで逆走し、経路を駆動源直後 → 終端の
        /// 順で返す。タップ経由の枝は親経路に合流してそのまま根まで遡る。
        let backwalk (terminal: Coord) : (Coord * Dir) list =
            let rec go (c: Coord) (dIn: Dir) acc =
                let acc = (c, dIn) :: acc
                let prev = { X = c.X - (delta dIn).X; Y = c.Y - (delta dIn).Y }
                match Map.tryFind prev occ with
                | Some (OccWire (_, d2, _)) -> go prev d2 acc
                | Some (OccCross _) -> go prev dIn acc   // 直進チャネル
                | _ -> acc                               // OccGate (Pin / 駆動ゲート)
            match Map.tryFind terminal occ with
            | Some (OccWire (_, d, _)) -> go terminal d []
            | _ -> []

        let freeCell (c: Coord) =
            not (Map.containsKey c occ)
            && not (Set.contains c forbidden)
            && not (Map.containsKey c reserved)

        /// edge の直線 run にコの字バンプを 1 つ挿入する (長さ +2h, h ≤ need/2)。
        /// バンプ可能なのは OccWire(net, d, straight=true) のみ:
        /// straight=false はコーナーかタップ元、Cross は他ネット同居なので動かせない。
        let tryBump (netId: NetId) (edge: (Coord * Dir) list) (need: int)
            : ((Coord * Dir) list * int) option =
            let arr = Array.ofList edge
            let n = arr.Length
            let plainAt i =
                let (c, d) = arr.[i]
                match Map.tryFind c occ with
                | Some (OccWire (nid, d2, true)) -> nid = netId && d2 = d
                | _ -> false
            let mul (dd: Dir) k (c: Coord) =
                { X = c.X + (delta dd).X * k; Y = c.Y + (delta dd).Y * k }
            // バンプ新セル: 上り脚 h + 上段 (s-1) + 下り脚 (h-1)
            let bumpCells (a: int) (s: int) (u: Dir) (h: int) =
                let rA = fst arr.[a]
                let rEnd = fst arr.[a + s - 1]
                [ for k in 1 .. h -> mul u k rA
                  for j in 1 .. s - 1 -> mul u h (fst arr.[a + j])
                  for k in 1 .. h - 1 -> mul u (h - k) rEnd ]
            let mutable best = None   // (a, s, u, h)
            let bestH () = match best with Some (_, _, _, h) -> h | None -> 0
            let hCap = min (need / 2) 512
            let mutable i = 0
            let mutable congested = false
            let failLimit = 100
            while i < n && not congested do
                if not (plainAt i) then i <- i + 1
                else
                    let d = snd arr.[i]
                    let mutable j = i
                    while j + 1 < n && plainAt (j + 1) && snd arr.[j + 1] = d do
                        j <- j + 1
                    let perp = match d with E | W -> [N; S] | N | S -> [E; W]
                    let mutable failStreak = 0
                    for a in i .. j - 1 do
                        for s in 2 .. j - a + 1 do
                            for u in perp do
                                if not congested && bestH () < hCap then
                                    let mutable h = hCap
                                    while h > bestH ()
                                          && not (bumpCells a s u h |> List.forall freeCell) do
                                        h <- h - 1
                                    if h > bestH () then
                                        best <- Some (a, s, u, h)
                                        failStreak <- 0
                                    else
                                        failStreak <- failStreak + 1
                                        if failStreak > failLimit then congested <- true
                    i <- j + 1
            best
            |> Option.map (fun (a, s, u, h) ->
                let d = snd arr.[a]
                let u' = opposite u
                let rA = fst arr.[a]
                let rEnd = fst arr.[a + s - 1]
                // 中間セルを空ける (straight=true のみなのでタップ元ではない)
                for t in a + 1 .. a + s - 2 do
                    occ <- Map.remove (fst arr.[t]) occ
                let seg =
                    [ yield rA, d
                      for k in 1 .. h -> mul u k rA, u
                      for j in 1 .. s - 1 -> mul u h (fst arr.[a + j]), d
                      for k in 1 .. h - 1 -> mul u (h - k) rEnd, u'
                      yield rEnd, u' ]
                seg |> List.iteri (fun t (c, dd) ->
                    // 終端 rEnd の次は元の後続 (方向 d ≠ u') なので straight=false
                    let straight = t < seg.Length - 1 && snd seg.[t + 1] = dd
                    occ <- Map.add c (OccWire (netId, dd, straight)) occ)
                let edge' =
                    List.ofArray arr.[0 .. a - 1] @ seg @ List.ofArray arr.[a + s .. n - 1]
                edge', 2 * h)

        /// edge を need 分 (偶数) バンプで延長する。
        /// 戻り値: (実際に加算できた長さ, 変形後の edge)。
        let rec padEdge (netId: NetId) (edge: (Coord * Dir) list) (need: int)
            : int * (Coord * Dir) list =
            if need < 2 then 0, edge
            else
                match tryBump netId edge need with
                | None -> 0, edge
                | Some (edge', added) ->
                    let more, fin = padEdge netId edge' (need - added)
                    added + more, fin

        /// 経路セル列を occ に書き込む (routeOne の書き込みと同じ Cross 化規則)。
        let writePath (netId: NetId) (path: (Coord * Dir) list) =
            let arr = Array.ofList path
            arr |> Array.iteri (fun i (c, d) ->
                let straight = i < arr.Length - 1 && snd arr.[i + 1] = d
                match Map.tryFind c occ with
                | Some (OccWire (n2, f2, _)) ->
                    let (hN, hD), (vN, vD) =
                        if d = E || d = W then (netId, d), (n2, f2)
                        else (n2, f2), (netId, d)
                    occ <- Map.add c (OccCross (hN, hD, vN, vD)) occ
                    (match netCells.TryGetValue n2 with
                     | true, l -> l.Remove c |> ignore
                     | _ -> ())
                | _ -> occ <- Map.add c (OccWire (netId, d, straight)) occ)

        /// リーフ edge を撤去する。Cross は他ネットチャネルを直線 Wire に復元する。
        /// タップ元セルを含む場合は他分岐が壊れるため撤去不可 (false)。
        let ripUpEdge (netId: NetId) (edge: (Coord * Dir) list) : bool =
            if edge |> List.exists (fun (c, _) -> tapSources.Contains c) then false
            else
                for (c, _) in edge do
                    match Map.tryFind c occ with
                    | Some (OccWire (n, _, _)) when n = netId -> occ <- Map.remove c occ
                    | Some (OccCross (hN, hD, vN, vD)) ->
                        if hN = netId then occ <- Map.add c (OccWire (vN, vD, true)) occ
                        elif vN = netId then occ <- Map.add c (OccWire (hN, hD, true)) occ
                    | _ -> ()
                true

        /// seeds (セル, 進入方向, そのセルでの到達世代) から goal まで
        /// 「到達世代がちょうど target」になる経路を DFS で探す。
        /// パリティと残距離で枝刈りする。goal の通過 (数珠つなぎ) は許可しない。
        let routeExactLen (netId: NetId) (seeds: (Coord * Dir * int) list)
                          (goal: Coord) (target: int)
            : (Coord * Dir) list option =
            let pts = goal :: (seeds |> List.map (fun (c, _, _) -> c))
            let margin = min 500 (max 60 (target / 2 + 4))
            let minX = (pts |> List.map (fun c -> c.X) |> List.min) - margin
            let maxX = (pts |> List.map (fun c -> c.X) |> List.max) + margin
            let minY = (pts |> List.map (fun c -> c.Y) |> List.min) - margin
            let maxY = (pts |> List.map (fun c -> c.Y) |> List.max) + margin
            let inB (c: Coord) = c.X >= minX && c.X <= maxX && c.Y >= minY && c.Y <= maxY
            let isCrossingCell (c: Coord) =
                match Map.tryFind c occ with
                | Some (OccWire (n2, _, _)) -> n2 <> netId
                | _ -> false
            let passOk (c: Coord) (nd: Dir) =
                if not (inB c) || Set.contains c forbidden then false
                else
                    let resOk =
                        match Map.tryFind c reserved with
                        | Some (n, isFirst) -> n = netId && (isFirst || c = goal)
                        | None -> true
                    resOk &&
                    (match Map.tryFind c occ with
                     | None -> true
                     | Some (OccWire (n2, f2, straight)) ->
                         n2 <> netId && straight && perpendicular nd f2 && c <> goal
                     | Some _ -> false)
            let visited = System.Collections.Generic.HashSet<Coord * Dir * int>()
            let onPath = System.Collections.Generic.HashSet<Coord>()
            let mutable budget = 500000
            let manhattan (c: Coord) = abs (c.X - goal.X) + abs (c.Y - goal.Y)
            let rec dfs (c: Coord) (d: Dir) (len: int) (acc: (Coord * Dir) list) =
                if budget <= 0 then None
                else
                    budget <- budget - 1
                    let rem = target - len
                    let dist = manhattan c
                    if dist > rem || (rem - dist) % 2 <> 0 then None
                    elif c = goal then
                        if rem = 0 then Some (List.rev ((c, d) :: acc)) else None
                    elif not (visited.Add ((c, d, len))) then None
                    else
                        onPath.Add c |> ignore
                        let dirs =
                            if isCrossingCell c then [d]
                            else
                                // 直進優先 + スラック消費 (ゴールから離れる方向を先に)。
                                // 余長を蛇行で使い切ってから戻る探索になる。
                                [E; W; N; S]
                                |> List.sortBy (fun nd ->
                                    (if nd = d then 0 else 1), -(manhattan (toward c nd)))
                        let result =
                            dirs |> List.tryPick (fun nd ->
                                let c' = toward c nd
                                // 自経路との重複は禁止 (writePath が二重書きになる)
                                if passOk c' nd && not (onPath.Contains c') then
                                    dfs c' nd (len + 1) ((c, d) :: acc)
                                else None)
                        if result.IsNone then onPath.Remove c |> ignore
                        result
            // スラックが小さい (蛇行が少なくて済む) シードから試す
            seeds
            |> List.filter (fun (c, d, len) -> passOk c d && len + manhattan c <= target)
            |> List.sortBy (fun (c, _, len) -> target - len - manhattan c)
            |> List.tryPick (fun (c, d, len) -> dfs c d len [])

        /// クロックネット 1 本のスキュー均等化。
        /// 到達 = パス長なので、各終端の専有サフィックス (リーフ edge) を
        /// 最長到達に合わせて延長すればよい。リーフ edge への加算はその終端の
        /// 到達だけを変えるため、木の再帰均等化は不要。残差はパリティ分の ≤1。
        let balanceClockNet (netId: NetId) (terminals: Coord list)
            : Result<unit, CompileError> =
            let paths = terminals |> List.map backwalk |> List.filter (List.isEmpty >> not)
            if paths.Length < 2 then Ok ()
            else
                let tMax = paths |> List.map List.length |> List.max
                // 複数終端パスに共有されるセル (幹) — リーフ edge はその先の専有部分
                let ownerCount =
                    paths
                    |> Seq.collect (List.map fst)
                    |> Seq.countBy id
                    |> Map.ofSeq
                // 幹上の再タップ候補: セル → 到達世代 (パス内インデックス + 1)。
                // 幹は撤去対象にならないので、リーフ間の処理順に依存しない。
                let trunkArrivals =
                    paths
                    |> Seq.collect (List.mapi (fun i (c, _) -> c, i + 1))
                    |> Seq.filter (fun (c, _) ->
                        ownerCount.[c] > 1
                        && (match Map.tryFind c occ with
                            | Some (OccWire (n, _, _)) -> n = netId
                            | _ -> false))
                    |> Seq.distinctBy fst
                    |> List.ofSeq
                // クロック源そのもの (Pin / 駆動ゲート) は到達 0 のタップ候補
                let sourceTaps =
                    [ match Map.tryFind netId pins with
                      | Some c -> yield c, 0
                      | None -> ()
                      match Map.tryFind netId driver with
                      | Some p -> yield p.Coord, 0
                      | None -> () ]
                let tapCandidates = sourceTaps @ trunkArrivals
                // 終端ごとの均等化は互いに独立。1 本が失敗しても残りの終端は延長を続け、
                // 最悪の残差だけを報告する。Result.bind で連鎖させると最初の失敗で
                // 残り全部の延長が飛ばされ、WARN の値 (その 1 本の残差) より実際の skew が
                // 桁違いに大きくなる (sm83_full: WARN 112 / 実測 skew 2892、hold 違反 144 組)。
                let worseSkew (acc: Result<unit, CompileError>) (r: Result<unit, CompileError>) =
                    match acc, r with
                    | Ok (), _ -> r
                    | Error (ClockSkewUnresolved (n, a)), Error (ClockSkewUnresolved (_, b)) ->
                        Error (ClockSkewUnresolved (n, max a b))
                    | Error _, _ -> acc
                paths
                |> List.fold (fun acc path ->
                    worseSkew acc ((fun () ->
                        let need = (tMax - List.length path) / 2 * 2
                        if need = 0 then Ok ()
                        else
                            let leafEdge =
                                path |> List.skipWhile (fun (c, _) -> ownerCount.[c] > 1)
                            let added, edge' = padEdge netId leafEdge need
                            let g = fst (List.last path)
                            eprintfn "[skew] 終端 (%d,%d): パス長 %d / 目標 %d / 必要 %d / バンプ追加 %d / リーフ edge %d セル"
                                g.X g.Y (List.length path) tMax need added (List.length leafEdge)
                            if added >= need then Ok ()
                            else
                                // バンプで不足 → リーフ edge を撤去し、幹の任意点から
                                // 「到達 = tMax (パリティ次第で tMax-1)」で引き直す
                                let failure = ClockSkewUnresolved (netId, need - added)
                                let goal = fst (List.last path)
                                // 撤去前の occ を退避。再配線に失敗したら復元し、
                                // 「スキュー未解消だが接続は保つ」状態でエラーを返す。
                                // 復元しないと DFF のクロックが未接続のままグリッドが
                                // 合成され、そのレジスタビットはリセット値に固着する。
                                let saved = edge' |> List.map (fun (c, _) -> c, Map.tryFind c occ)
                                let restoreEdge () =
                                    for (c, entry) in saved do
                                        match entry with
                                        | Some v -> occ <- Map.add c v occ
                                        | None -> occ <- Map.remove c occ
                                if not (ripUpEdge netId edge') then Error failure
                                else
                                    let seeds =
                                        [ for (q, arr) in tapCandidates do
                                            for d in [E; W; N; S] do
                                                yield toward q d, d, arr + 1 ]
                                    [tMax; tMax - 1]
                                    |> List.tryPick (fun target ->
                                        routeExactLen netId seeds goal target
                                        |> Option.map (fun p -> p, target))
                                    |> function
                                       | None ->
                                           eprintfn "[skew] 終端 (%d,%d): 指定長 %d/%d での引き直しも失敗 (タップ候補 %d 個)"
                                               goal.X goal.Y tMax (tMax - 1) tapCandidates.Length
                                           restoreEdge ()
                                           Error failure
                                       | Some (newPath, _) ->
                                           writePath netId newPath
                                           // 新タップ元を非交差化
                                           (match newPath with
                                            | (c0, d0) :: _ ->
                                                let tapC =
                                                    { X = c0.X - (delta d0).X
                                                      Y = c0.Y - (delta d0).Y }
                                                (match Map.tryFind tapC occ with
                                                 | Some (OccWire (n, dq, _)) when n = netId ->
                                                     occ <- Map.add tapC (OccWire (n, dq, false)) occ
                                                     tapSources.Add tapC |> ignore
                                                 | _ -> ())
                                            | [] -> ())
                                           Ok ()) ()))
                    (Ok ())

        let balanceClocks () : Result<unit, CompileError> =
            let nets =
                placed
                |> List.choose (fun p ->
                    match p.Gate.Kind, p.Gate.Inputs with
                    | Dff, [clkNet; _] -> Some (clkNet, toward p.Coord S)
                    | Dff, [cNet; _; _] -> Some (cNet, toward p.Coord S)
                    | _ -> None)
                |> List.groupBy fst
            eprintfn "[clock] スキュー均等化: %d ネット" nets.Length
            nets
            |> List.fold (fun acc (net, terms) ->
                acc |> Result.bind (fun () ->
                    let sw = System.Diagnostics.Stopwatch.StartNew ()
                    let r = balanceClockNet net (terms |> List.map snd)
                    eprintfn "[clock] 均等化 NetId %A 終端 %d 本 %.0f s" net terms.Length sw.Elapsed.TotalSeconds
                    r))
                (Ok ())

        // 全ゲートの全入力終端を順に配線 (短いネット優先で輻輳軽減)。
        // routeOne が輻輳失敗した場合は Rip-up & reroute (残課題 2): そのネットの
        // bbox 内の先行ネットを最大 10 個撤去して再ルーティングする。
        // クロック終端の手前に残す専有部分の長さ (バンプを打つ余地)。
        // 均等化しない (2 相) ならバンプも打たないので制限しない
        let clockTapGuard =
            match clockRouting with
            | BalancedSkew -> 12
            | ShortestOnly | MinLatency _ -> 0
        let routeSw = System.Diagnostics.Stopwatch.StartNew()
        let terminals =
            placed |> List.collect (fun p -> fst (gateTerminals p))

        // --- クロック優先配線 (スキュー均等化のためのスペース確保) ---
        // クロック終端 (Dff の S 側) を最初に配線してから balanceClocks で均等化する。
        // 通常配線の後に均等化すると、データ配線に使われた残りスペースしかなく、
        // 延長 (バンプ挿入) が不足して ClockSkewUnresolved になる (sm83_subset 20x14 で skew 1068)。
        let clockTerminals =
            placed
            |> List.collect (fun p ->
                match p.Gate.Kind, p.Gate.Inputs with
                | Dff, (clkNet :: _) -> [ clkNet, toward p.Coord S ]
                | _ -> [])
        let clockNetIds = clockTerminals |> List.map fst |> Set.ofList
        // MinLatency: クロックネットごとの「既存の木のセル → ピンからの到達時間」(ピン = 0)
        let clockArrivalMaps =
            match clockRouting with
            | MinLatency _ ->
                clockNetIds
                |> Set.toList
                |> List.map (fun nid ->
                    let arrivals = System.Collections.Generic.Dictionary<Coord, int>()
                    match Map.tryFind nid pins with
                    | Some c -> arrivals.[c] <- 0
                    | None -> ()
                    nid, arrivals)
                |> Map.ofList
            | BalancedSkew | ShortestOnly -> Map.empty
        let clockTapCost (nid: NetId) : TapCost =
            match Map.tryFind nid clockArrivalMaps with
            | Some arrivals -> MinArrival arrivals
            | None -> MinNewWire
        // 配線順。MinLatency は ピンからのマンハッタン距離で並べ替える (同距離は配置順のまま)
        let orderedClockTerminals =
            let distFromPin (nid: NetId, goal: Coord) =
                match Map.tryFind nid pins with
                | Some c -> abs (goal.X - c.X) + abs (goal.Y - c.Y)
                | None -> 0
            match clockRouting with
            | MinLatency FarthestFirst -> clockTerminals |> List.sortByDescending distFromPin
            | MinLatency NearestFirst -> clockTerminals |> List.sortBy distFromPin
            | BalancedSkew | ShortestOnly -> clockTerminals
        // クロック終端をまとめて配線する。guard > 0 なら終端近傍でのタップを禁止して
        // スキュー均等化用の専有部分を残す。失敗したら呼び出し側が guard=0 で引き直す。
        let routeClockTerminals (guard: int) : Result<unit, CompileError> =
            eprintfn "[clock] 終端 %d 本の配線を開始 (タップ禁止半径 %d)" clockTerminals.Length guard
            tapGuard <- guard
            tapGuardPoints <- if guard > 0 then clockTerminals |> List.map snd else []
            let mutable cr : Result<unit, CompileError> = Ok ()
            let mutable clockDone = 0
            for (nid, goal) in orderedClockTerminals do
                let sw = System.Diagnostics.Stopwatch.StartNew ()
                match routeOneWith (clockTapCost nid) nid goal with
                | Ok () -> ()
                | Error _ ->
                    eprintfn "[clock] 配線失敗: NetId %A (%d/%d)" nid (clockDone + 1) clockTerminals.Length
                    match cr with
                    | Ok () -> cr <- Error (RoutingCongestion nid)   // 最初の失敗を記録する
                    | Error _ -> ()
                clockDone <- clockDone + 1
                if sw.Elapsed.TotalSeconds >= 10.0 then
                    eprintfn "[clock] 遅い終端: NetId %A %.0f s (%d/%d)" nid sw.Elapsed.TotalSeconds clockDone clockTerminals.Length
                if clockDone % 20 = 0 then
                    eprintfn "[clock] %d/%d 本 %d s" clockDone clockTerminals.Length (int routeSw.Elapsed.TotalSeconds)
            eprintfn "[clock] 配線完了 %d s" (int routeSw.Elapsed.TotalSeconds)
            for KeyValue (nid, arrivals) in clockArrivalMaps do
                let terminalArrivals =
                    clockTerminals
                    |> List.filter (fun (n, _) -> n = nid)
                    |> List.choose (fun (_, g) ->
                        match arrivals.TryGetValue g with
                        | true, a -> Some a
                        | _ -> None)
                if not terminalArrivals.IsEmpty then
                    // arrivals はピン (到達 0) と木の全セルを持つ
                    eprintfn "[clock] NetId %A 到達 min %d / max %d 世代 (終端 %d 本、木 %d セル)"
                        nid (List.min terminalArrivals) (List.max terminalArrivals) terminalArrivals.Length
                        (arrivals.Count - 1)
            tapGuard <- 0
            tapGuardPoints <- []
            cr

        let clockResult =
            let savedOcc = occ
            let savedTaps = tapSources |> List.ofSeq
            match routeClockTerminals clockTapGuard with
            | Ok () -> Ok ()
            | Error e when clockTapGuard > 0 ->
                // タップ禁止で配線できない場合は制限なしでやり直す (接続を優先し、
                // スキューは均等化できなければ WARN で続行する)
                eprintfn "[clock] タップ禁止半径 %d では配線できず (%A) — 制限なしで引き直す" clockTapGuard e
                occ <- savedOcc
                tapSources.Clear ()
                for c in savedTaps do tapSources.Add c |> ignore
                netCells.Clear ()
                for KeyValue (c, cell) in occ do
                    match cell with
                    | OccWire (n, _, true) -> addNetCell n c
                    | _ -> ()
                routeClockTerminals 0
            | Error e -> Error e
            |> Result.bind (fun () ->
                match clockRouting with
                | ShortestOnly | MinLatency _ -> Ok ()
                | BalancedSkew ->
                    match balanceClocks () with
                    | Ok x -> Ok x
                    | Error e ->
                        eprintfn "WARN: %A — クロックスキュー非調整で続行" e
                        Ok ())
        let netLen (nid: NetId) (goal: Coord) =
            match Map.tryFind nid driver with
            | Some p ->
                let src = toward p.Coord p.Dir
                abs (goal.X - src.X) + abs (goal.Y - src.Y)
            | None -> System.Int32.MaxValue

        // ネット → 全終端 (再ルーティング待ちキューへ投入する単位)
        let netToTerminals : Map<NetId, Coord list> =
            terminals
            |> List.groupBy fst
            |> List.map (fun (nid, ts) -> nid, ts |> List.map snd)
            |> Map.ofList

        // --- Rip-up & reroute ---------------------------------------------
        // 失敗ネット (nid, goal) の bbox 内の先行ネットを最大 maxRipNets 個撤去し、
        // 失敗ネットを解放された空間で再ルーティングする。撤去したネットの ID を
        // 返し、呼び出し側が再ルーティング待ちキューへ入れる。
        // 再ルーティングも失敗した場合は撤去前の occ を復元して元のエラーを返す
        // (balanceClockNet の restoreEdge と同じ思想)。ループ防止のため再試行回数
        // 上限 (ネット単位 / 全体) を設ける。
        let ripCount = System.Collections.Generic.Dictionary<NetId, int>()
        // 10k ゲート級では 30 本撤去しても通らないネットが残ったため上限を緩める
        // (2026-09-18 の 24x16: NetId 165 ほか 5 本が 30 本撤去 × 3 回で失敗)
        let maxRipsPerNet = 8
        let maxTotalRips = 5000
        let maxRipNets = 80
        let mutable totalRips = 0

        let ripUpAndReroute (nid: NetId) (goal: Coord) (blockers: NetId list) : Result<NetId list, CompileError> =
            if totalRips >= maxTotalRips then Error (RoutingCongestion nid)
            else
                // ネット → 全セル (撤去時に使用)
                let cellsByNet = System.Collections.Generic.Dictionary<NetId, ResizeArray<Coord * OccCell>>()
                let addToResize n x =
                    match cellsByNet.TryGetValue n with
                    | true, l -> l.Add x
                    | _ -> let l = ResizeArray<Coord * OccCell>() in l.Add x; cellsByNet.[n] <- l
                for KeyValue (c, cell) in occ do
                    match cell with
                    | OccWire (n, _, _) -> addToResize n (c, cell)
                    | OccCross (hN, _, vN, _) -> addToResize hN (c, cell); addToResize vN (c, cell)
                    | _ -> ()

                // ブロッカー候補: routeOne が探索中に実際にブロックしたネット (ブロック回数降順)。
                // タップ元を持つネットは ripUpEdge が false を返すため事前に除外する。
                let candidates =
                    blockers
                    |> Seq.filter (fun n -> n <> nid)
                    |> Seq.filter (fun n ->
                        match ripCount.TryGetValue n with
                        | true, k -> k < maxRipsPerNet
                        | _ -> true)
                    |> Seq.filter (fun n ->
                        match cellsByNet.TryGetValue n with
                        | true, cells -> cells |> Seq.forall (fun (c, _) -> not (tapSources.Contains c))
                        | _ -> false)
                    |> Seq.truncate maxRipNets
                    |> List.ofSeq

                // 撤去。タップ元を含むネットは ripUpEdge が false を返すのでスキップ。
                let mutable ripped : (NetId * (Coord * OccCell) list) list = []
                for n2 in candidates do
                    match cellsByNet.TryGetValue n2 with
                    | true, cells when cells.Count > 0 ->
                        let cellsList = List.ofSeq cells
                        let rippable =
                            cellsList |> List.forall (fun (c, _) -> not (tapSources.Contains c))
                        if rippable then
                            // ripUpEdge は Coord のみ参照する (Dir は未使用)
                            let edge = cellsList |> List.map (fun (c, _) -> c, E)
                            if ripUpEdge n2 edge then
                                ripCount.[n2] <-
                                    (match ripCount.TryGetValue n2 with true, k -> k | _ -> 0) + 1
                                totalRips <- totalRips + 1
                                netCells.Remove n2 |> ignore
                                ripped <- (n2, cellsList) :: ripped
                                eprintfn "[ripup] NetId %A 撤去 (%d cells, NetId %A の輻輳回避)"
                                    n2 cellsList.Length nid
                    | _ -> ()

                let ripped = List.rev ripped
                if List.isEmpty ripped then Error (RoutingCongestion nid)
                else
                    match routeOne nid goal with
                    | Ok () ->
                        eprintfn "[ripup] NetId %A ok (rip-up 後再ルーティング成功)" nid
                        Ok (ripped |> List.map fst)
                    | Error _ ->
                        // 撤去前の occ を復元して元のエラーを返す。失敗は致命的 (コンパイル中断)
                        // なので netCells の復元は不要 (occ と一緒に破棄される)。
                        for (_, cellsList) in ripped do
                            for (c, cell) in cellsList do occ <- Map.add c cell occ
                        Error (RoutingCongestion nid)

        // 終端を短いネット優先でキューへ投入。routeOne 失敗時は rip-up & reroute。
        // クロック終端は既に配線済み (clockResult) なので除外する。
        let queue = System.Collections.Generic.Queue<NetId * Coord>()
        for (nid, goal) in terminals
                            |> List.filter (fun (nid, _) -> not (clockNetIds.Contains nid))
                            |> List.sortBy (fun (nid, goal) -> netLen nid goal) do
            queue.Enqueue (nid, goal)
        let totalTerminals = queue.Count
        eprintfn "[route] データ終端 %d 本の配線を開始 (%d s)" totalTerminals (int routeSw.Elapsed.TotalSeconds)
        let mutable processed = 0
        let mutable result : Result<unit, CompileError> = Ok ()
        while (match result with Ok _ -> true | _ -> false) && queue.Count > 0 do
            let (nid, goal) = queue.Dequeue ()
            processed <- processed + 1
            if processed % 100 = 0 then
                eprintfn "[route] %d/%d (NetId %A) %d s"
                    processed totalTerminals nid (int routeSw.Elapsed.TotalSeconds)
            let termSw = System.Diagnostics.Stopwatch.StartNew ()
            match routeOne nid goal with
            | Ok () -> ()
            | Error blockers ->
                match ripUpAndReroute nid goal blockers with
                | Ok ripped ->
                    for b in ripped do
                        match Map.tryFind b netToTerminals with
                        | Some goals -> for g in goals do queue.Enqueue (b, g)
                        | None -> ()
                | Error e -> result <- Error e
            if termSw.Elapsed.TotalSeconds >= 20.0 then
                eprintfn "[route] 遅い終端: NetId %A %.0f s (%d/%d)" nid termSw.Elapsed.TotalSeconds processed totalTerminals
        eprintfn "[route] done: %d terminals processed, %d rip-ups" processed totalRips
        result
        |> Result.bind (fun () -> clockResult)
        |> Result.map (fun () -> occ)

    /// 全ネットを配線する (単相: クロック skew 均等化あり)。
    let routeWL (placed: WlPlaced list) (pins: Map<NetId, Coord>) : Result<OccGrid, CompileError> =
        routeWLWith BalancedSkew placed pins

    // --- 合成 -----------------------------------------------------------

    /// 配置 + 占有グリッドを LGrid に合成する。
    let emitWL (placed: WlPlaced list) (pins: Map<NetId, Coord>) (occ: OccGrid) : LGrid =
        let mutable g : LGrid = Map.empty
        for KeyValue (c, o) in occ do
            match o with
            | OccWire (_, d, _) -> g <- Map.add c (LWire (d, false)) g
            | OccCross (_, hd, _, vd) -> g <- Map.add c (Cross (hd, vd, false, false)) g
            | OccGate _ -> ()
        for p in placed do
            let cell =
                match p.Gate.Kind with
                | Dff -> LDff (p.Dir, false, false)
                | _   -> LNand (p.Dir, false)
            g <- Map.add p.Coord cell g
        for KeyValue (_, c) in pins do
            g <- Map.add c (Pin false) g
        g

    // --- トップレベル ---------------------------------------------------

    let private mappable (k: GateKind) =
        match k with
        | Not | Nand | Dff -> true
        | _ -> false

    let private ensureMappable (nl: Netlist) : Result<Netlist, CompileError> =
        match nl.Gates |> List.tryFind (fun g -> not (mappable g.Kind)) with
        | Some g -> Error (UnmappableGate g.Kind)
        | None -> Ok nl

    /// 配置ピッチの選び方。
    type PitchChoice =
        /// 回路規模から基本ピッチを決め、輻輳で失敗したら pitchSequence に沿って広げて再試行する
        | AutoPitch
        /// このピッチで 1 回だけ試す
        | FixedPitch of pitchX: int * pitchY: int

    /// WireLevel コンパイルのオプション。
    type WlCompileOptions =
        { Placement: GatePlacement.PlacementStrategy
          Pitch: PitchChoice
          Clocking: Clocking.ClockingScheme }

    /// 既定: 行優先・自動ピッチ・単相 (従来の compileWL と同じ)。
    let defaultCompileOptions : WlCompileOptions =
        { Placement = GatePlacement.RowMajor
          Pitch = AutoPitch
          Clocking = Clocking.SingleEdge }

    /// コンパイル結果。Placed / Pins は配置配線した (クロック方式変換後の) ネットリストのもの。
    /// 出力ネットの観測は駆動ゲートのセルで行う (2 相でも元の Q ネットはスレーブが駆動する)。
    type WlCompiled =
        { Grid: LGrid
          Placed: WlPlaced list
          Pins: Map<NetId, Coord>
          Clocking: CircuitClocking }

    /// 配置 → 配線 → グリッド生成 (1 ピッチ分)。
    let private placeAndRoute
        (strategy: GatePlacement.PlacementStrategy)
        (pitchX: int)
        (pitchY: int)
        (circuit: PreparedCircuit)
        : Result<WlCompiled, CompileError> =
        placeCircuitWithStrategy strategy pitchX pitchY circuit
        |> Result.bind (fun p ->
            routeWLWith (clockRoutingOf circuit.Clocking) p.Placed p.Pins
            |> Result.map (fun occ ->
                { Grid = emitWL p.Placed p.Pins occ
                  Placed = p.Placed
                  Pins = p.Pins
                  Clocking = circuit.Clocking }))

    /// ピッチを自動決定し、輻輳失敗時はより広いピッチで自動再試行する (pitchSequence)。
    /// 配置の最適化はピッチごとにやり直す (スロット座標が変わるため)。
    let private placeAndRouteAutoPitch
        (strategy: GatePlacement.PlacementStrategy)
        (circuit: PreparedCircuit)
        : Result<WlCompiled, CompileError> =
        let startPitch = pitchFor circuit.Netlist.Gates.Length
        let rec tryPitches (remaining: (int * int) list) =
            match remaining with
            | [] -> Error (RoutingCongestion (NetId 0))
            | (px, py) :: rest ->
                match placeAndRoute strategy px py circuit with
                | Ok compiled -> Ok compiled
                | Error (InvalidPlacementConfig _ as e) -> Error e
                | Error e ->
                    match rest with
                    | [] -> Error e
                    | _ ->
                        eprintfn "[pitch] %dx%d 輻輳失敗 (%A) — 広いピッチで再試行" px py e
                        tryPitches rest
        let pitches =
            pitchSequence
            |> List.skipWhile (fun p -> p <> startPitch)
        tryPitches pitches

    /// 論理ネットリスト → WireLevel グリッド。クロック方式の変換 (2 相化) はここで行い、
    /// 呼び出し側の論理ネットリスト (NetlistSim / Testbench / golden が使うもの) は変えない。
    let compileNetlistWL (opts: WlCompileOptions) (nl: Netlist) : Result<WlCompiled, CompileError> =
        ensureMappable nl
        |> Result.bind (prepareCircuit opts.Clocking)
        |> Result.bind (fun circuit ->
            match opts.Pitch with
            | FixedPitch (px, py) -> placeAndRoute opts.Placement px py circuit
            | AutoPitch -> placeAndRouteAutoPitch opts.Placement circuit)

    /// yosys JSON → WireLevel グリッド (オプション指定版)。
    let compileWLWithOptions (opts: WlCompileOptions) (src: string) : Result<WlCompiled, CompileError> =
        frontend src
        |> Result.bind (compileNetlistWL opts)

    /// ホストが駆動するクロックピン (ClockDrive 用)。単相で DFF のない回路は None。
    let clockPinsOf (c: WlCompiled) : ClockDrive.ClockPins option =
        match c.Clocking with
        | SingleEdgeClock clock ->
            clock
            |> Option.bind (fun clk -> Map.tryFind clk c.Pins)
            |> Option.map ClockDrive.SingleEdgePins
        | TwoPhaseClock tp ->
            match Map.tryFind tp.ClockA c.Pins, Map.tryFind tp.ClockB c.Pins with
            | Some a, Some b -> Some (ClockDrive.TwoPhasePins (a, b))
            | _ -> None

    let private asTuple (c: WlCompiled) : LGrid * WlPlaced list * Map<NetId, Coord> =
        c.Grid, c.Placed, c.Pins

    /// yosys JSON → WireLevel グリッド (ピッチ・配置戦略指定版、単相)。
    let compileWLWithPitchAndStrategy
        (strategy: GatePlacement.PlacementStrategy)
        (pitchX: int)
        (pitchY: int)
        (src: string)
        : Result<LGrid * WlPlaced list * Map<NetId, Coord>, CompileError> =
        compileWLWithOptions
            { defaultCompileOptions with Placement = strategy; Pitch = FixedPitch (pitchX, pitchY) } src
        |> Result.map asTuple

    /// yosys JSON → WireLevel グリッド (ピッチ指定版、行優先配置、単相)。
    /// 戻り値: (グリッド, 配置, ピン座標)。出力ネットの観測は駆動ゲートのセルで行う。
    let compileWLWithPitch (pitchX: int) (pitchY: int) (src: string)
        : Result<LGrid * WlPlaced list * Map<NetId, Coord>, CompileError> =
        compileWLWithPitchAndStrategy GatePlacement.RowMajor pitchX pitchY src

    /// yosys JSON → WireLevel グリッド (配置戦略指定版、単相)。ピッチは回路規模から自動決定し、
    /// 輻輳失敗時はより広いピッチで自動再試行する (pitchSequence)。
    let compileWLWithStrategy (strategy: GatePlacement.PlacementStrategy) (src: string)
        : Result<LGrid * WlPlaced list * Map<NetId, Coord>, CompileError> =
        compileWLWithOptions { defaultCompileOptions with Placement = strategy } src
        |> Result.map asTuple

    /// yosys JSON → WireLevel グリッド (行優先配置、単相)。ピッチは自動決定・自動拡大。
    let compileWL (src: string)
        : Result<LGrid * WlPlaced list * Map<NetId, Coord>, CompileError> =
        compileWLWithStrategy GatePlacement.RowMajor src

    /// デバッグ用: LGrid を ASCII ダンプする (構造のみ、レベルは大文字/記号で表現しない)。
    let dumpAscii (g: LGrid) : string =
        if Map.isEmpty g then "(empty)"
        else
            let coords = g |> Map.toList |> List.map fst
            let minX = coords |> List.map (fun c -> c.X) |> List.min
            let maxX = coords |> List.map (fun c -> c.X) |> List.max
            let minY = coords |> List.map (fun c -> c.Y) |> List.min
            let maxY = coords |> List.map (fun c -> c.Y) |> List.max
            let sb = System.Text.StringBuilder()
            for y in minY .. maxY do
                for x in minX .. maxX do
                    let ch =
                        match getL g { X = x; Y = y } with
                        | LEmpty -> '.'
                        | Pin v -> if v then '1' else '0'
                        | LWire (E, _) -> '>'
                        | LWire (W, _) -> '<'
                        | LWire (N, _) -> '^'
                        | LWire (S, _) -> 'v'
                        | LNand (E, _) -> 'E'
                        | LNand (W, _) -> 'W'
                        | LNand (N, _) -> 'N'
                        | LNand (S, _) -> 'S'
                        | Cross _ -> '+'
                        | LDff _ -> 'F'
                    sb.Append ch |> ignore
                sb.Append '\n' |> ignore
            sb.ToString ()
