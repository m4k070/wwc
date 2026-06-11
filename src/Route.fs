namespace WwHdl

module Route =
    open Units
    open Domain
    open Netlist
    open Place
    open Rule

    /// コンパイルエラー型
    type CompileError =
        | ParseError      of string
        | UnmappableGate  of GateKind
        | PlacementOverflow
        | RoutingCongestion of NetId
        | TimingViolation of NetId * expected: int<gen> * actual: int<gen>
        | ClockSkewUnresolved of NetId * residual: int

    /// 1 本の配線。長さ (= Path のセル数) がそのまま遅延になる。
    /// Consumer は、この Wire を消費するゲートの Output NetId。
    /// fan-out で同一 Net を持つ複数の Wire を区別するために使う。
    type Wire =
        { Net: NetId
          Consumer: NetId
          Path: Coord list
          Delay: int<gen> }

    /// パスをシミュレーションして実効遅延を実測する。
    /// src に Head を置き、dst が Head になるまでの世代数を返す。
    /// 解析的な計算 (N-1-turns 等) は L ターンが連続する場合に誤るため、実測する。
    /// 配線経路中の対角ショートカットも考慮する。
    let measureDelay (path: Coord list) : int<gen> =
        match path with
        | [] | [_] -> 0<gen>
        | _ ->
            let src = List.head path
            let dst = List.last path
            let wireGrid = path |> List.map (fun c -> c, Wire) |> Map.ofList
            let initial = wireGrid |> Map.add src Head
            let limit = (List.length path) * 2
            let rec find (g: Grid) (t: int) =
                if t >= limit then (List.length path - 1) * 1<gen>   // fallback
                elif Domain.get g dst = Head then t * 1<gen>
                else find (Rule.step g) (t + 1)
            find initial 0

    /// コンテキスト付き遅延計測: ソースゲートのパターンを利用して対角ショートカットを検出する。
    ///
    /// JUNC3 など一部ゲートでは、出力ポート (path[0]) が Head になる 1 ステップ前に
    /// 出力ポートに隣接するゲート内部セル (例: local (3,2)) が Head になる。
    /// このセルが wire の 2 セル目 (path[1]) とも対角隣接していると、
    /// path[1] は path[0] と同一ステップで Head になる「対角ショートカット」が生じる。
    ///
    /// このショートカットを検出した場合、初期状態に path[0] と path[1] の両方を Head として
    /// 測定することで実効遅延を正確に計算する。
    let private measureDelayInContext (path: Coord list) (gateGrid: Grid) : int<gen> =
        match path with
        | [] | [_] -> 0<gen>
        | [_; _] -> measureDelay path
        | _ ->
            let src = path.[0]
            let secondCell = path.[1]
            let dst = List.last path

            // path[0] (= ゲート出力ポート) に隣接するゲート内部 Wire セルを収集する。
            let gateNeighbors =
                gateGrid |> Map.toList
                |> List.filter (fun (gateCoord, _) ->
                    gateCoord <> src &&
                    abs (gateCoord.X - src.X) <= 1 &&
                    abs (gateCoord.Y - src.Y) <= 1)
                |> List.map fst

            // いずれかのゲート内部隣接セルが path[1] とも隣接 (対角含む) していれば
            // ショートカットが発生する。
            let hasShortcut =
                gateNeighbors |> List.exists (fun gn ->
                    abs (gn.X - secondCell.X) <= 1 &&
                    abs (gn.Y - secondCell.Y) <= 1)

            let wireGrid = path |> List.map (fun c -> c, Wire) |> Map.ofList
            // ショートカットがある場合は path[0] と path[1] の両方を Head で開始する。
            let initial =
                if hasShortcut then
                    wireGrid |> Map.add src Head |> Map.add secondCell Head
                else
                    wireGrid |> Map.add src Head
            let limit = (List.length path) * 2
            let rec find (g: Grid) (t: int) =
                if t >= limit then (List.length path - 1) * 1<gen>   // fallback
                elif Domain.get g dst = Head then t * 1<gen>
                else find (Rule.step g) (t + 1)
            find initial 0

    let ofPath (net: NetId) (consumer: NetId) (path: Coord list) : Wire =
        { Net      = net
          Consumer = consumer
          Path     = path
          Delay    = measureDelay path }

    /// ソースゲートのパターンを考慮したコンテキスト付き Wire 生成。
    /// gateGrid はソースゲートの Wire パターンの絶対座標グリッド。
    let ofPathInContext (net: NetId) (consumer: NetId) (path: Coord list) (gateGrid: Grid) : Wire =
        { Net      = net
          Consumer = consumer
          Path     = path
          Delay    = measureDelayInContext path gateGrid }

    type RoutingCell =
        | Free              // 配線可能
        | Blocked           // セルが占有または禁止領域
        | Routed of NetId   // 既配線済みのネット

    type RoutingGrid = Map<Coord, RoutingCell>

    /// Placement からルーティンググリッドを構築する。
    /// 各セルの bounding box 内を Blocked にし、それ以外は Free (= Map に存在しない) とする。
    let buildGrid (placement: Placement) : RoutingGrid =
        placement
        |> List.collect (fun p ->
            [ for x in 0 .. p.Cell.Size.X - 1 do
                for y in 0 .. p.Cell.Size.Y - 1 do
                    yield { X = p.Origin.X + x; Y = p.Origin.Y + y }, Blocked ])
        |> Map.ofList

    /// BFS の探索範囲を src/dst から margin セル内に制限する bounding box を返す。
    let private bboxOf (src: Coord) (dst: Coord) (margin: int) =
        let minX = min src.X dst.X - margin
        let maxX = max src.X dst.X + margin
        let minY = min src.Y dst.Y - margin
        let maxY = max src.Y dst.Y + margin
        minX, maxX, minY, maxY

    /// Lee 法 BFS の共通実装 (private)。
    /// sameNet = Some n のとき、Routed n セルも通過可能 (fan-out 再利用)。
    /// 探索を src/dst を囲む bounding box + margin セルに制限し、無限空間の探索を防ぐ。
    /// allPorts: 全ゲートの全ポート座標集合。src/dst 以外のポートに隣接するセルは通過禁止。
    let leePathImpl
        (grid: RoutingGrid)
        (src: Coord)
        (dst: Coord)
        (sameNet: NetId option)
        (allPorts: Set<Coord>)
        (tight: bool)
        (allowOtherNets: bool)
        : Coord list option =
        if src = dst then Some [src]
        else
            let dist = abs (src.X - dst.X) + abs (src.Y - dst.Y)
            let bfsMarginExtra = 10
            let bfsMarginMin = 30
            let margin = max (dist + bfsMarginExtra) bfsMarginMin
            let minX, maxX, minY, maxY = bboxOf src dst margin

            let inBounds c = c.X >= minX && c.X <= maxX && c.Y >= minY && c.Y <= maxY

            // 事前計算: Grid を Dictionary に変換して O(1) ルックアップ
            let gridDict = System.Collections.Generic.Dictionary<Coord, RoutingCell>(grid.Count)
            for kv in grid do gridDict.Add(kv.Key, kv.Value)

            // ポート隣接マップ: 毎回 allPorts を走査する O(|allPorts|) → O(1) に削減
            let portAdjSet = System.Collections.Generic.Dictionary<Coord, Set<Coord>>()
            for p in allPorts do
                for dx in -1..1 do
                    for dy in -1..1 do
                        if dx <> 0 || dy <> 0 then
                            let c = { X = p.X + dx; Y = p.Y + dy }
                            match portAdjSet.TryGetValue c with
                            | true, ports -> portAdjSet.[c] <- Set.add p ports
                            | false, _ -> portAdjSet.[c] <- Set.singleton p

            let isAdjacentToOtherPort (c: Coord) =
                match portAdjSet.TryGetValue c with
                | false, _ -> false
                | true, ports ->
                    let isAdjacentToDst = abs (c.X - dst.X) <= 1 && abs (c.Y - dst.Y) <= 1
                    if isAdjacentToDst then
                        Set.exists (fun p -> p <> dst) ports
                    else
                        Set.exists (fun p -> p <> src && p <> dst) ports

            let isAdjacentToOtherNet (c: Coord) =
                tight && (  // 広い配置ではクロストークリスク低いのでスキップ
                [ for dx in -1 .. 1 do
                    for dy in -1 .. 1 do
                        if dx <> 0 || dy <> 0 then
                            yield { X = c.X + dx; Y = c.Y + dy } ]
                |> List.exists (fun nb ->
                    match gridDict.TryGetValue nb with
                    | true, Routed n -> sameNet <> Some n
                    | _ -> false))

            let passable c =
                let isNearDst = abs (c.X - dst.X) <= 1 && abs (c.Y - dst.Y) <= 1
                inBounds c && (
                    c = src || c = dst ||
                    (not (isAdjacentToOtherPort c) &&
                     (isNearDst || not (isAdjacentToOtherNet c)) &&
                     match gridDict.TryGetValue c with
                     | false, _ | true, Free -> true
                     | true, Routed n -> sameNet = Some n || allowOtherNets
                     | _ -> false))

            let tightDirs = [| {X=1;Y=0}; {X= -1;Y=0}; {X=0;Y=1}; {X=0;Y= -1} |]
            let wideDirs = [| {X=0;Y=1}; {X=0;Y= -1}; {X= -1;Y=0}; {X=1;Y=0} |]
            let dirs =
                if tight then tightDirs
                else wideDirs
            // A*: PriorityQueue + マンハッタン距離ヒューリスティック
            // BFS よりはるかに少ない探索セル数で経路を発見する。
            // 上限: 経路が存在しない場合の全探索を防止 (100万セル or バウンディングボックスの面積)
            let maxExplore = min ((maxX - minX + 1) * (maxY - minY + 1)) 500000
            let prev  = System.Collections.Generic.Dictionary<Coord, Coord>()
            let gScore = System.Collections.Generic.Dictionary<Coord, int>()
            let closed = System.Collections.Generic.HashSet<Coord>()
            let queue = System.Collections.Generic.PriorityQueue<Coord, int>()
            prev.[src] <- src
            gScore.[src] <- 0
            queue.Enqueue(src, abs (src.X - dst.X) + abs (src.Y - dst.Y))

            let mutable found = false
            let mutable explored = 0
            while queue.Count > 0 && not found && explored < maxExplore do
                let c = queue.Dequeue()
                if not (closed.Add c) then ()  // stale entry
                elif c = dst then found <- true
                else
                    explored <- explored + 1
                    let g = gScore.[c]
                    for d in dirs do
                        let n = { X = c.X + d.X; Y = c.Y + d.Y }
                        let tentativeG = g + 1
                        if passable n && not (closed.Contains n) then
                            if not (gScore.ContainsKey n) || tentativeG < gScore.[n] then
                                gScore.[n] <- tentativeG
                                prev.[n] <- c
                                queue.Enqueue(n, tentativeG + abs (n.X - dst.X) + abs (n.Y - dst.Y))

            if not found then
                None
            else
                let path = System.Collections.Generic.List<Coord>()
                let mutable cur = dst
                while cur <> src do
                    path.Insert(0, cur)
                    cur <- prev.[cur]
                path.Insert(0, src)
                Some (List.ofSeq path)

    /// Lee 法 BFS で src から dst への最短経路を返す。到達不能なら None。
    /// src/dst は Blocked 領域内 (セルのポート座標) でも通過できる。
    /// 中間セルは Free (grid に存在しない) のみ通過可。
    /// allPorts: 全ポート座標集合。src/dst 以外のポートに隣接するセルを通過禁止にする。
    let leePath (grid: RoutingGrid) (src: Coord) (dst: Coord) (allPorts: Set<Coord>) (tight: bool) : Coord list option =
        leePathImpl grid src dst None allPorts tight false

    /// fan-out 用 Lee 法 BFS。
    /// 同一 netId の既配線セルを通過可能にした単始点 BFS で最短総パスを探す。
    /// 多始点 BFS より単純で、分岐長ではなく src→dst 総パス長を最適化する。
    /// allPorts: 全ポート座標集合。src/dst 以外のポートに隣接するセルを通過禁止にする。
    let leePathFanout (grid: RoutingGrid) (netId: NetId) (src: Coord) (dst: Coord) (allPorts: Set<Coord>) (tight: bool) : Coord list option =
        leePathImpl grid src dst (Some netId) allPorts tight false

    /// Wire リストから 2 本以上の経路が共有するセルを衝突として列挙する。
    let findConflicts (wires: Wire list) : (Coord * NetId * NetId) list =
        wires
        |> List.collect (fun w -> w.Path |> List.map (fun c -> c, w.Net))
        |> List.groupBy fst
        |> List.choose (fun (coord, entries) ->
            match List.map snd entries |> List.distinct with
            | a :: b :: _ -> Some (coord, a, b)
            | _            -> None)

    /// 衝突点に Crossover セルを挿入し両ネットの経路を張り替える。
    let insertCrossovers
        (_conflicts: (Coord * NetId * NetId) list)
        (wires: Wire list)
        : Wire list =
        // TODO: Crossover StdCell を配置し Path を入出力ポートに接続しなおす
        wires

