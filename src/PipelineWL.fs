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

    /// 回路規模 (ゲート数) に応じた配置ピッチ。
    /// 大規模回路では grid 面積爆発を防ぐため縮小する。
    /// 下限はゲート間の配線チャネルが確保できる範囲。
    let private pitchFor (nGates: int) : int * int =
        if nGates <= 200 then 24, 16
        elif nGates <= 1000 then 20, 14
        elif nGates <= 3000 then 16, 12
        else 12, 10

    /// ゲートを JSON 宣言順に正方格子に配置する (ピッチ指定版)。
    let placeWLWithPitch (pitchX: int) (pitchY: int) (nl: Netlist) : WlPlaced list * Map<NetId, Coord> =
        let n = max 1 nl.Gates.Length
        let ncols = int (ceil (sqrt (float n)))
        let placed =
            nl.Gates |> List.mapi (fun i g ->
                let col = i % ncols
                let row = i / ncols
                { Gate = g
                  Coord = { X = gateX0 + col * pitchX; Y = 2 + row * pitchY }
                  Dir = E })
        let pins =
            nl.PrimaryInputs
            |> List.mapi (fun i netId -> netId, { X = 0; Y = 2 + i * pitchY })
            |> Map.ofList
        placed, pins

    /// 回路規模に応じたピッチで配置する。
    let placeWL (nl: Netlist) : WlPlaced list * Map<NetId, Coord> =
        let px, py = pitchFor nl.Gates.Length
        placeWLWithPitch px py nl

    // --- 終端割り当て ---------------------------------------------------

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

    // --- 配線 -----------------------------------------------------------

    /// 全ネットを配線して占有グリッドを返す。
    /// 各終端へは「既配線セルからのタップ (ファンアウト)」または
    /// 「駆動ゲートの出力先頭セル」から (Coord, Dir) 状態の A* で配線する。
    let routeWL (placed: WlPlaced list) (pins: Map<NetId, Coord>) : Result<OccGrid, CompileError> =
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

        // ネット → タップ可能セル (Cross 化されたセルは除外していく)
        let netCells = System.Collections.Generic.Dictionary<NetId, ResizeArray<Coord>>()
        let addNetCell n c =
            match netCells.TryGetValue n with
            | true, l -> l.Add c
            | _ -> let l = ResizeArray<Coord>() in l.Add c; netCells.[n] <- l
        for KeyValue (n, c) in pins do addNetCell n c

        let routeOne (netId: NetId) (goal: Coord) : Result<unit, CompileError> =
            let tapCells =
                match netCells.TryGetValue netId with
                | true, l -> List.ofSeq l
                | _ -> []
            let seeds =
                if tapCells.IsEmpty then
                    match Map.tryFind netId driver with
                    | Some p -> [ (toward p.Coord p.Dir, p.Dir) ]
                    | None ->
                        match Map.tryFind netId pins with
                        | Some c -> [ for d in [E; W; N; S] do yield toward c d, d ]
                        | None -> []
                else
                    [ for t in tapCells do
                        for d in [E; W; N; S] do
                            yield (toward t d, d) ]

            let isCrossingCell (c: Coord) =
                match Map.tryFind c occ with
                | Some (OccWire (n2, _, _)) -> n2 <> netId
                | _ -> false

            // リトライループ: 探索範囲 (bbox マージン) を拡大しながら A* を再試行する。
            // 転回ペナルティ: コーナーは交差不可なので直線経路を優先し、後続ネットが
            // 交差できるセルを増やす (輻輳対策)。
            let h (c: Coord) = abs (c.X - goal.X) + abs (c.Y - goal.Y)
            let pts = goal :: (seeds |> List.map fst)
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
                let maxExplore = min (bboxArea * 8) 5000000
                // 転回ペナルティ: コーナーは交差不可なので直線経路を優先し、
                // 後続ネットが交差できるセルを増やす (輻輳対策)。
                // リトライが進むほど下げる (4→2→1) — 初回は直線優先で交差余地を
                // 温存し、輻輳が深刻なリトライでは柔軟な経路選択を許容する。
                let turnPenalty = max 1 (4 / exploreMult)
                let pq = System.Collections.Generic.PriorityQueue<Coord * Dir, int>()
                let gScore = System.Collections.Generic.Dictionary<Coord * Dir, int>()
                let prev = System.Collections.Generic.Dictionary<Coord * Dir, (Coord * Dir) option>()
                let closed = System.Collections.Generic.HashSet<Coord * Dir>()
                for (c, d) in seeds do
                    if passOk c d && not (gScore.ContainsKey ((c, d))) then
                        gScore.[(c, d)] <- 1
                        prev.[(c, d)] <- None
                        pq.Enqueue ((c, d), 1 + h c)
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
                                if passOk c' nd && not (closed.Contains ((c', nd))) then
                                    let ng = gc + 1 + (if nd <> d then turnPenalty else 0)
                                    if not (gScore.ContainsKey ((c', nd))) || ng < gScore.[(c', nd)] then
                                        gScore.[(c', nd)] <- ng
                                        prev.[(c', nd)] <- Some (c, d)
                                        pq.Enqueue ((c', nd), ng + h c')
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
            match result with
            | Some r ->
                if exploreMult > 1 then
                    eprintfn "[route] NetId %A ok after mult=%d" netId exploreMult
                r
            | None -> Error (RoutingCongestion netId)

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
                paths
                |> List.fold (fun acc path ->
                    acc |> Result.bind (fun () ->
                        let need = (tMax - List.length path) / 2 * 2
                        if need = 0 then Ok ()
                        else
                            let leafEdge =
                                path |> List.skipWhile (fun (c, _) -> ownerCount.[c] > 1)
                            let added, edge' = padEdge netId leafEdge need
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
                                           Ok ()))
                    (Ok ())

        let balanceClocks () : Result<unit, CompileError> =
            placed
            |> List.choose (fun p ->
                match p.Gate.Kind, p.Gate.Inputs with
                | Dff, [clkNet; _] -> Some (clkNet, toward p.Coord S)
                | Dff, [cNet; _; _] -> Some (cNet, toward p.Coord S)
                | _ -> None)
            |> List.groupBy fst
            |> List.fold (fun acc (net, terms) ->
                acc |> Result.bind (fun () ->
                    balanceClockNet net (terms |> List.map snd)))
                (Ok ())

        // 全ゲートの全入力終端を順に配線 (短いネット優先で輻輳軽減)。
        // routeOne が輻輳失敗した場合は Rip-up & reroute (残課題 2): そのネットの
        // bbox 内の先行ネットを最大 10 個撤去して再ルーティングする。
        let routeSw = System.Diagnostics.Stopwatch.StartNew()
        let terminals =
            placed |> List.collect (fun p -> fst (gateTerminals p))
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
        let maxRipsPerNet = 3
        let maxTotalRips = 1000
        let mutable totalRips = 0

        let ripUpAndReroute (nid: NetId) (goal: Coord) : Result<NetId list, CompileError> =
            if totalRips >= maxTotalRips then Error (RoutingCongestion nid)
            else
                // 失敗ネットの bbox (駆動源 / ピン / 既配線タップセル / ゴール + margin)。
                // routeOne が最後 (mult=16) まで失敗した時点での探索範囲は margin 960 なので、
                // ブロッカーはその範囲内にいる。タップセル (ファンアウトの既配線部分) を
                // 含めることで、routeOne の実際の探索中心に合わせる。
                let pts =
                    [ yield goal
                      match netCells.TryGetValue nid with
                      | true, l -> for c in l do yield c
                      | _ -> ()
                      match Map.tryFind nid driver with
                      | Some p -> yield toward p.Coord p.Dir
                      | None -> ()
                      match Map.tryFind nid pins with
                      | Some c -> yield c
                      | None -> () ]
                let margin = 960
                let minX = (pts |> List.map (fun c -> c.X) |> List.min) - margin
                let maxX = (pts |> List.map (fun c -> c.X) |> List.max) + margin
                let minY = (pts |> List.map (fun c -> c.Y) |> List.min) - margin
                let maxY = (pts |> List.map (fun c -> c.Y) |> List.max) + margin
                let inB (c: Coord) = c.X >= minX && c.X <= maxX && c.Y >= minY && c.Y <= maxY

                // 1 パスで「ネット→全セル」と「bbox 内占有数」を同時に集計する。
                let cellsByNet = System.Collections.Generic.Dictionary<NetId, ResizeArray<Coord * OccCell>>()
                let bboxCount = System.Collections.Generic.Dictionary<NetId, int>()
                let addToResize n x =
                    match cellsByNet.TryGetValue n with
                    | true, l -> l.Add x
                    | _ -> let l = ResizeArray<Coord * OccCell>() in l.Add x; cellsByNet.[n] <- l
                let bump n =
                    match bboxCount.TryGetValue n with
                    | true, k -> bboxCount.[n] <- k + 1
                    | _ -> bboxCount.[n] <- 1
                for KeyValue (c, cell) in occ do
                    match cell with
                    | OccWire (n, _, _) ->
                        addToResize n (c, cell)
                        if inB c && n <> nid then bump n
                    | OccCross (hN, _, vN, _) ->
                        addToResize hN (c, cell); addToResize vN (c, cell)
                        if inB c then
                            if hN <> nid then bump hN
                            if vN <> nid then bump vN
                    | _ -> ()

                // ブロッカー候補: bbox 占有が多い順に、未達上限・リップアップ可能
                // (タップ元を含まない = ファンアウト枝のない) ネットを最大 10 個。
                // タップ元を持つネットは ripUpEdge が false を返すため事前に除外する
                // (大きな共有ネットは占有数トップに来やすいが撤去できない)。
                let candidates =
                    bboxCount
                    |> Seq.sortByDescending (fun (KeyValue (_, k)) -> k)
                    |> Seq.map (fun (KeyValue (n, _)) -> n)
                    |> Seq.filter (fun n ->
                        match ripCount.TryGetValue n with
                        | true, k -> k < maxRipsPerNet
                        | _ -> true)
                    |> Seq.filter (fun n ->
                        match cellsByNet.TryGetValue n with
                        | true, cells -> cells |> Seq.forall (fun (c, _) -> not (tapSources.Contains c))
                        | _ -> false)
                    |> Seq.truncate 30
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
                    | Error e ->
                        // 撤去前の occ を復元して元のエラーを返す。失敗は致命的 (コンパイル中断)
                        // なので netCells の復元は不要 (occ と一緒に破棄される)。
                        for (_, cellsList) in ripped do
                            for (c, cell) in cellsList do occ <- Map.add c cell occ
                        Error e

        // 終端を短いネット優先でキューへ投入。routeOne 失敗時は rip-up & reroute。
        let queue = System.Collections.Generic.Queue<NetId * Coord>()
        for (nid, goal) in terminals |> List.sortBy (fun (nid, goal) -> netLen nid goal) do
            queue.Enqueue (nid, goal)
        let totalTerminals = queue.Count
        let mutable processed = 0
        let mutable result : Result<unit, CompileError> = Ok ()
        while (match result with Ok _ -> true | _ -> false) && queue.Count > 0 do
            let (nid, goal) = queue.Dequeue ()
            processed <- processed + 1
            if processed % 100 = 0 then
                eprintfn "[route] %d/%d (NetId %A) %d s"
                    processed totalTerminals nid (int routeSw.Elapsed.TotalSeconds)
            match routeOne nid goal with
            | Ok () -> ()
            | Error (RoutingCongestion _) ->
                match ripUpAndReroute nid goal with
                | Ok blockers ->
                    for b in blockers do
                        match Map.tryFind b netToTerminals with
                        | Some goals -> for g in goals do queue.Enqueue (b, g)
                        | None -> ()
                | Error e -> result <- Error e
            | Error e -> result <- Error e
        eprintfn "[route] done: %d terminals processed, %d rip-ups" processed totalRips
        result
        |> Result.bind (fun () ->
            match balanceClocks () with
            | Ok x -> Ok x
            | Error e ->
                eprintfn "WARN: %A — クロックスキュー非調整で続行" e
                Ok ())
        |> Result.map (fun () -> occ)

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

    /// yosys JSON → WireLevel グリッド (ピッチ指定版)。
    /// 戻り値: (グリッド, 配置, ピン座標)。出力ネットの観測は駆動ゲートのセルで行う。
    let compileWLWithPitch (pitchX: int) (pitchY: int) (src: string)
        : Result<LGrid * WlPlaced list * Map<NetId, Coord>, CompileError> =
        frontend src
        |> Result.bind (fun nl ->
            match nl.Gates |> List.tryFind (fun g -> not (mappable g.Kind)) with
            | Some g -> Error (UnmappableGate g.Kind)
            | None ->
                let placed, pins = placeWLWithPitch pitchX pitchY nl
                routeWL placed pins
                |> Result.map (fun occ -> emitWL placed pins occ, placed, pins))

    /// yosys JSON → WireLevel グリッド。ピッチは回路規模から自動決定する。
    let compileWL (src: string)
        : Result<LGrid * WlPlaced list * Map<NetId, Coord>, CompileError> =
        frontend src
        |> Result.bind (fun nl ->
            match nl.Gates |> List.tryFind (fun g -> not (mappable g.Kind)) with
            | Some g -> Error (UnmappableGate g.Kind)
            | None ->
                let px, py = pitchFor nl.Gates.Length
                let placed, pins = placeWLWithPitch px py nl
                routeWL placed pins
                |> Result.map (fun occ -> emitWL placed pins occ, placed, pins))

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
