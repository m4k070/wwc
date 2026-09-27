namespace WwHdl

// ---------------------------------------------------------------------
// HoldAnalysis — 配線済み WireLevel グリッドの hold タイミング静的解析
//
// src/AnalyzeHold.fsx (CLI) の解析部分。テストからも使えるようにライブラリに置く。
//
// 遅延モデル (WireLevel.step の意味論から導出。世代 = step 1 回)
//
//   時刻の基準: ホストが clk ピンを 0→1 に書いた直後の状態を gen 0 とする。
//   セル x の「到達時刻」 = x が新しい値を保持する最初の世代。
//
//   * LWire / LNand / Cross の各チャネル: 次世代の値 = 現世代の隣接提示値の関数
//       → 入力側隣セルの到達時刻 + 1 (CellDelay)。Pin は gen 0 で既に新値。
//   * LDff: step の中で「現世代の clk 側隣セル値」と「前世代の clk (prevClk)」から
//       立ち上がりを検知し、そのとき「現世代の D 側隣セル値」を q' に取り込む。
//       clk 側隣セルの到達時刻を T とすると:
//         - 捕捉される D は gen T の D 側隣セル値            → CaptureWindow = 0
//         - 新しい q が見えるのは gen T+1                    → DffClockToQ   = 1
//   * DFF A の Q から DFF B の D 側隣セルまでの最短経路のセル数を minData(A→B)
//     (A のセル自身が B の D 側隣セルなら 0) とすると、B の D 側隣セルが A の新値を
//     持つ最初の世代は clkArrival(A) + DffClockToQ + minData(A→B)。
//   * hold 違反 ⇔ その世代 ≤ clkArrival(B) + CaptureWindow
//     (B が捕捉する世代に、既に A の新値が見えてしまう)。
//     slack = dataArrival − (captureGen + 1) と定義し、slack < 0 を違反とする
//     (slack = 0 は「捕捉の 1 世代後に新値が届く」ぎりぎり安全)。
//
//   最短経路は論理的マスキングを無視する (NAND の他入力で値が変わらない場合も
//   「変わり得る」とみなす) ので、安全側 (違反を多めに出す側) の見積もりになる。
//   これらの定数が step と一致していることは AnalyzeHold.fsx --selftest で実シミュレーションと照合している。
//
// 2 相クロック (Clocking.TwoPhase) の不変条件 (analyzeTwoPhase):
//   各 DFF はちょうど 1 本のクロック (clk_a または clk_b) で駆動され、
//   同じ相の DFF → DFF の組合せデータ経路が 0 本であること。
//   存在してよいのはマスター (A) → スレーブ (B) と スレーブ (B) → (組合せ) → マスター (A) だけ。
//   これが成り立てば、相の間に settle を挟む駆動では skew に関係なく hold 違反は起こらない。
// ---------------------------------------------------------------------
module HoldAnalysis =
    open System.Collections.Generic
    open Domain
    open WireLevel
    open RoutedArtifact

    /// Wire / NAND / Cross チャネル 1 段の遅延 (世代)
    [<Literal>]
    let CellDelay = 1
    /// DFF がクロック立ち上がりを見た世代から Q が新値になるまで (世代)
    [<Literal>]
    let DffClockToQ = 1
    /// DFF がクロック立ち上がりを見た世代から、D をサンプルする世代までのずれ
    [<Literal>]
    let CaptureWindow = 0
    /// Cross は 1 セルに 2 チャネル (水平/垂直) を持つ。ノード ID = セル番号 × 2 + チャネル
    [<Literal>]
    let ChannelsPerCell = 2
    [<Literal>]
    let MainOrHorizontal = 0
    [<Literal>]
    let Vertical = 1

    // --- 密グリッド (解析用の配列表現) -------------------------------------------

    type DenseGrid =
        { Origin: Coord
          Width: int
          Height: int
          Cells: LCell[] }

    let toDense (g: LGrid) : DenseGrid =
        let origin, width, height = gridBounds g
        let cells = Array.create (width * height) LEmpty
        for KeyValue (c, cell) in g do
            cells.[(c.Y - origin.Y) * width + (c.X - origin.X)] <- cell
        { Origin = origin; Width = width; Height = height; Cells = cells }

    let indexOf (dg: DenseGrid) (c: Coord) : int option =
        let x = c.X - dg.Origin.X
        let y = c.Y - dg.Origin.Y
        let inside = x >= 0 && y >= 0 && x < dg.Width && y < dg.Height
        if inside then Some (y * dg.Width + x) else None

    let coordOf (dg: DenseGrid) (idx: int) : Coord =
        { X = dg.Origin.X + idx % dg.Width; Y = dg.Origin.Y + idx / dg.Width }

    let neighborIndex (dg: DenseGrid) (idx: int) (side: Dir) : int option =
        let c = coordOf dg idx
        let d = delta side
        indexOf dg { X = c.X + d.X; Y = c.Y + d.Y }

    let allDirs = [| E; W; N; S |]

    let clockSides (dir: Dir) : Dir list =
        match dir with
        | E | W -> [ N; S ]
        | N | S -> [ E; W ]

    // --- 信号の流れ (pull 関係) の順方向グラフ -------------------------------------

    /// ノードの値を読む側 (読み手) の種類。
    type Sink =
        /// 組合せ要素の入力 (Wire / Cross チャネル / NAND)。値が伝わり、さらに先へ進む
        | CombNode of node: int * isNand: bool
        /// DFF の D 入力 (背面) として読まれる
        | DffData of dffCell: int
        /// DFF のクロック入力 (側面) として読まれる
        | DffClock of dffCell: int

    /// セル cell のチャネル ch が toward 方向の隣へ値を提示するか (WireLevel.presentedTo と同じ規則)。
    let presents (cell: LCell) (ch: int) (toward: Dir) : bool =
        match cell with
        | LEmpty -> false
        | Pin _ | LWire _ | LNand _ | LDff _ -> ch = MainOrHorizontal
        | Cross (hd, vd, _, _) ->
            if ch = MainOrHorizontal then toward = hd else toward = vd

    /// 座標 readerIdx のセルが、自分の side 側の隣から値を読むなら、その読み手を返す
    /// (WireLevel.step の pullFrom の使われ方と同じ規則)。
    let sinkOf (reader: LCell) (readerIdx: int) (side: Dir) : Sink option =
        match reader with
        | LWire (d, _) when side = opposite d -> Some (CombNode (readerIdx * ChannelsPerCell, false))
        | LNand (d, _) when side <> d -> Some (CombNode (readerIdx * ChannelsPerCell, true))
        | Cross (hd, vd, _, _) ->
            if side = opposite hd then Some (CombNode (readerIdx * ChannelsPerCell + MainOrHorizontal, false))
            elif side = opposite vd then Some (CombNode (readerIdx * ChannelsPerCell + Vertical, false))
            else None
        | LDff (d, _, _) ->
            if side = opposite d then Some (DffData readerIdx)
            elif List.contains side (clockSides d) then Some (DffClock readerIdx)
            else None
        | _ -> None

    /// ノードの値を読む読み手をすべて列挙する。
    let sinksOf (dg: DenseGrid) (node: int) : Sink list =
        let idx = node / ChannelsPerCell
        let ch = node % ChannelsPerCell
        let cell = dg.Cells.[idx]
        [ for toward in allDirs do
            if presents cell ch toward then
                match neighborIndex dg idx toward with
                | Some nIdx ->
                    match sinkOf dg.Cells.[nIdx] nIdx (opposite toward) with
                    | Some sink -> yield sink
                    | None -> ()
                | None -> () ]

    // --- 1. クロック到達時刻 -------------------------------------------------------

    type ClockAnalysis =
        { /// DFF セル番号 → clk 側隣セルに立ち上がりが届く世代
          Arrival: Map<int, int>
          /// クロック網が NAND の入力に入っている箇所 (極性が変わるため追跡しない)
          NandLoads: int list
          /// クロック網が DFF の D 入力に入っている箇所
          DataLoads: int list }

    /// クロックピンから、Wire / Cross だけを通って前進する幅優先探索。
    /// 1 段 = CellDelay 世代なので、BFS の距離がそのまま到達世代になる。
    let analyzeClock (dg: DenseGrid) (clkPinIdx: int) : ClockAnalysis =
        let dist = Dictionary<int, int>()
        let queue = Queue<int>()
        let pinNode = clkPinIdx * ChannelsPerCell
        dist.[pinNode] <- 0
        queue.Enqueue pinNode
        let mutable arrival = Map.empty
        let nandLoads = ResizeArray<int>()
        let dataLoads = ResizeArray<int>()
        while queue.Count > 0 do
            let u = queue.Dequeue ()
            let du = dist.[u]
            for sink in sinksOf dg u do
                match sink with
                | CombNode (_, true) -> nandLoads.Add (u / ChannelsPerCell)
                | CombNode (v, false) ->
                    if not (dist.ContainsKey v) then
                        dist.[v] <- du + CellDelay
                        queue.Enqueue v
                | DffClock dff ->
                    // BFS は距離の昇順に取り出すので、最初に記録した値が最短
                    if not (Map.containsKey dff arrival) then arrival <- Map.add dff du arrival
                | DffData dff -> dataLoads.Add dff
        { Arrival = arrival
          NandLoads = List.ofSeq nandLoads |> List.distinct
          DataLoads = List.ofSeq dataLoads |> List.distinct }

    // --- 1b. クロック木の最大到達時間と下限 -------------------------------------------

    /// クロック木の最大到達時間 (latency) と、その下限。
    /// 配線は 1 セル 1 世代なので、どんな木でも DFF i の到達 ≥ dist(ピン, i のクロック端子)
    /// (マンハッタン距離)。よって max_i 到達 ≥ max_i dist = LowerBound。
    type ClockLatency =
        { /// 最大到達時間 (世代)
          MaxArrival: int
          /// 最大到達時間の下限: ピンから最も遠いクロック端子までのマンハッタン距離
          LowerBound: int
          /// ピンの位置も自由に選べるとしたときの下限: 端子群の L1 ミニマックス半径
          IdealPinBound: int
          /// DFF ごとの (到達 − ピンからのマンハッタン距離) の最大 = 最大の遠回り
          MaxDetour: int }

    /// MaxArrival / LowerBound (下限との比。1.0 が最適)。
    let latencyRatio (l: ClockLatency) : float =
        if l.LowerBound <= 0 then 1.0 else float l.MaxArrival / float l.LowerBound

    /// DFF のクロック端子の候補 (クロック側の隣の空でないセル)。Wire はどの向きでも
    /// 隣へ値を提示するので、向きでは絞らない。
    let clockTerminalCandidates (dg: DenseGrid) (dffIdx: int) : int list =
        match dg.Cells.[dffIdx] with
        | LDff (d, _, _) ->
            clockSides d
            |> List.choose (neighborIndex dg dffIdx)
            |> List.filter (fun n -> dg.Cells.[n] <> LEmpty)
        | _ -> []

    /// clkPinIdx から届いた DFF について最大到達時間と下限を求める。到達が 1 つもなければ None。
    let clockLatency (dg: DenseGrid) (clkPinIdx: int) (c: ClockAnalysis) : ClockLatency option =
        let pin = coordOf dg clkPinIdx
        let manhattan (a: Coord) = abs (a.X - pin.X) + abs (a.Y - pin.Y)
        // 到達はクロック側の隣のどれかから届くので、候補のうちピンに近いほうの距離が下限
        let reached =
            [ for KeyValue (dff, arrival) in c.Arrival do
                match clockTerminalCandidates dg dff |> List.map (coordOf dg) with
                | [] -> ()
                | candidates -> yield candidates |> List.minBy manhattan, arrival ]
        match reached with
        | [] -> None
        | _ ->
            let terminals = reached |> List.map fst
            let span (f: Coord -> int) =
                let vs = terminals |> List.map f
                List.max vs - List.min vs
            let spanU = span (fun t -> t.X + t.Y)
            let spanV = span (fun t -> t.X - t.Y)
            Some { MaxArrival = reached |> List.map snd |> List.max
                   LowerBound = terminals |> List.map manhattan |> List.max
                   IdealPinBound = (max spanU spanV + 1) / 2
                   MaxDetour = reached |> List.map (fun (t, a) -> a - manhattan t) |> List.max }

    // --- 2. DFF 間の最短データ遅延 ---------------------------------------------------

    type DataReach =
        { /// 捕捉側 DFF セル番号 → minData (発射側 DFF セル自身を 0 とする経路長)
          ToData: Map<int, int>
          /// データ経路が到達した DFF のクロック入力 (ゲーテッドクロック。hold 解析の前提外)
          ToClock: int list }

    /// DFF (セル番号 src) の Q から、組合せ要素 (Wire / Cross / NAND) だけを通って前進し、
    /// 各 DFF の D 側隣セルまでの最短段数を求める。DFF は通り抜けない (順序境界)。
    /// dist / stamp は呼び出し間で使い回す作業領域 (全ノード分の配列を毎回確保しないため)。
    let reachFromDff (dg: DenseGrid) (dist: int[]) (stamp: int[]) (stampId: int) (src: int) : DataReach =
        let queue = Queue<int>()
        let start = src * ChannelsPerCell
        dist.[start] <- 0
        stamp.[start] <- stampId
        queue.Enqueue start
        let mutable toData = Map.empty
        let toClock = ResizeArray<int>()
        while queue.Count > 0 do
            let u = queue.Dequeue ()
            let du = dist.[u]
            for sink in sinksOf dg u do
                match sink with
                | CombNode (v, _) ->
                    if stamp.[v] <> stampId then
                        stamp.[v] <- stampId
                        dist.[v] <- du + CellDelay
                        queue.Enqueue v
                | DffData dff ->
                    // du = D 側隣セル (u) までの段数。BFS 順なので最初が最短
                    if not (Map.containsKey dff toData) then toData <- Map.add dff du toData
                | DffClock dff -> toClock.Add dff
        { ToData = toData; ToClock = List.ofSeq toClock |> List.distinct }

    // --- 3. hold 判定 -------------------------------------------------------------

    type HoldPair =
        { Launch: int
          Capture: int
          LaunchClk: int
          CaptureClk: int
          MinData: int
          /// dataArrival − (captureGen + 1)。負なら違反
          Slack: int }

    let holdSlack (launchClk: int) (captureClk: int) (minData: int) : int =
        let dataArrival = launchClk + DffClockToQ + minData
        let captureGen = captureClk + CaptureWindow
        dataArrival - (captureGen + 1)

    type HoldReport =
        { Clock: ClockAnalysis
          Dffs: int list
          Pairs: HoldPair list
          /// クロック未到達の DFF
          Unclocked: int list
          /// データ経路がクロック入力に入る (発射 DFF, 被駆動 DFF)
          GatedClocks: (int * int) list }

    let findDffs (dg: DenseGrid) : int list =
        dg.Cells
        |> Array.indexed
        |> Array.choose (fun (i, cell) ->
            match cell with
            | LDff _ -> Some i
            | _ -> None)
        |> List.ofArray

    let analyzeHold (dg: DenseGrid) (clkPinIdx: int) : HoldReport =
        let clock = analyzeClock dg clkPinIdx
        let dffs = findDffs dg
        let nodeCount = dg.Cells.Length * ChannelsPerCell
        let dist = Array.zeroCreate<int> nodeCount
        let stamp = Array.zeroCreate<int> nodeCount
        let reaches =
            dffs |> List.mapi (fun i dff -> dff, reachFromDff dg dist stamp (i + 1) dff)
        let pairs =
            [ for (launch, reach) in reaches do
                match Map.tryFind launch clock.Arrival with
                | None -> ()
                | Some launchClk ->
                    for KeyValue (capture, minData) in reach.ToData do
                        match Map.tryFind capture clock.Arrival with
                        | None -> ()
                        | Some captureClk ->
                            yield { Launch = launch
                                    Capture = capture
                                    LaunchClk = launchClk
                                    CaptureClk = captureClk
                                    MinData = minData
                                    Slack = holdSlack launchClk captureClk minData } ]
        { Clock = clock
          Dffs = dffs
          Pairs = pairs |> List.sortBy (fun p -> p.Slack)
          Unclocked = dffs |> List.filter (fun d -> not (Map.containsKey d clock.Arrival))
          GatedClocks = [ for (launch, reach) in reaches do for c in reach.ToClock do yield launch, c ] }

    /// 既存の WireLevel.clockArrivals (終端から逆走して数える) と BFS の結果を照合する。
    /// 両者は独立な数え方なので、一致すれば到達時刻の基準 (Pin = 0) が揃っていることの確認になる。
    let crossCheckClock (g: LGrid) (dg: DenseGrid) (r: HoldReport) : string list =
        let reference = clockArrivals g |> Map.ofList
        [ for KeyValue (dff, t) in r.Clock.Arrival do
            let c = coordOf dg dff
            match Map.tryFind c reference with
            | Some t' when t' = t -> ()
            | other -> yield sprintf "(%d,%d): BFS %d / clockArrivals %A" c.X c.Y t other ]

    // --- 4. 2 相クロックの不変条件 ---------------------------------------------------

    /// DFF を駆動するクロックの相。
    type ClockPhase =
        /// clk_a (マスター)
        | PhaseA
        /// clk_b (スレーブ)
        | PhaseB

    /// 同じ相の DFF 間の組合せデータ経路 (2 相の不変条件違反)。
    type SamePhasePath =
        { Launch: int
          Capture: int
          Phase: ClockPhase
          MinData: int }

    type TwoPhaseReport =
        { ClockA: ClockAnalysis
          ClockB: ClockAnalysis
          Dffs: int list
          /// DFF セル番号 → 相 (ちょうど 1 本のクロックが届く DFF のみ)
          PhaseOf: Map<int, ClockPhase>
          /// どちらのクロックも届かない DFF
          Unclocked: int list
          /// clk_a と clk_b の両方が届く DFF
          DoublyClocked: int list
          /// 同じ相の DFF → DFF の組合せデータ経路 (0 本であるべき)
          SamePhasePaths: SamePhasePath list
          /// マスター (A) → スレーブ (B) の経路数
          AToB: int
          /// スレーブ (B) → マスター (A) の経路数
          BToA: int
          /// データ経路が DFF のクロック入力に入る (発射 DFF, 被駆動 DFF)
          GatedClocks: (int * int) list }

    /// 2 相クロックのグリッドを解析する。clkAIdx / clkBIdx はクロックピンのセル番号。
    let analyzeTwoPhase (dg: DenseGrid) (clkAIdx: int) (clkBIdx: int) : TwoPhaseReport =
        let clockA = analyzeClock dg clkAIdx
        let clockB = analyzeClock dg clkBIdx
        let dffs = findDffs dg
        let phaseCandidates (dff: int) =
            [ if Map.containsKey dff clockA.Arrival then yield PhaseA
              if Map.containsKey dff clockB.Arrival then yield PhaseB ]
        let phaseOf =
            dffs
            |> List.choose (fun d ->
                match phaseCandidates d with
                | [ phase ] -> Some (d, phase)
                | _ -> None)
            |> Map.ofList
        let nodeCount = dg.Cells.Length * ChannelsPerCell
        let dist = Array.zeroCreate<int> nodeCount
        let stamp = Array.zeroCreate<int> nodeCount
        let reaches = dffs |> List.mapi (fun i dff -> dff, reachFromDff dg dist stamp (i + 1) dff)
        let phasePairs =
            [ for (launch, reach) in reaches do
                for KeyValue (capture, minData) in reach.ToData do
                    match Map.tryFind launch phaseOf, Map.tryFind capture phaseOf with
                    | Some pl, Some pc -> yield launch, capture, pl, pc, minData
                    | _ -> () ]
        { ClockA = clockA
          ClockB = clockB
          Dffs = dffs
          PhaseOf = phaseOf
          Unclocked = dffs |> List.filter (fun d -> List.isEmpty (phaseCandidates d))
          DoublyClocked = dffs |> List.filter (fun d -> (phaseCandidates d).Length > 1)
          SamePhasePaths =
            [ for (l, c, pl, pc, minData) in phasePairs do
                if pl = pc then yield { Launch = l; Capture = c; Phase = pl; MinData = minData } ]
          AToB = phasePairs |> List.filter (fun (_, _, pl, pc, _) -> pl = PhaseA && pc = PhaseB) |> List.length
          BToA = phasePairs |> List.filter (fun (_, _, pl, pc, _) -> pl = PhaseB && pc = PhaseA) |> List.length
          GatedClocks = [ for (launch, reach) in reaches do for c in reach.ToClock do yield launch, c ] }

    /// 2 相の不変条件が成り立つか: 全 DFF がちょうど 1 相、同相経路 0 本、
    /// クロック網がデータに混ざらない (NAND 入力・D 入力・ゲーテッドクロックがない)。
    let twoPhaseInvariantHolds (r: TwoPhaseReport) : bool =
        List.isEmpty r.Unclocked
        && List.isEmpty r.DoublyClocked
        && List.isEmpty r.SamePhasePaths
        && List.isEmpty r.GatedClocks
        && List.isEmpty r.ClockA.NandLoads && List.isEmpty r.ClockB.NandLoads
        && List.isEmpty r.ClockA.DataLoads && List.isEmpty r.ClockB.DataLoads

    /// 単相の hold 違反 (slack < 0) の組。
    let holdViolations (r: HoldReport) : HoldPair list =
        r.Pairs |> List.filter (fun p -> p.Slack < 0)
