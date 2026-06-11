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
    let private pitchX = 16
    let private pitchY = 12

    /// ゲートを正方格子に、プライマリ入力ピンを左端列 (x=0) に置く。
    let placeWL (nl: Netlist) : WlPlaced list * Map<NetId, Coord> =
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

    // --- 終端割り当て ---------------------------------------------------

    /// ゲートの入力ネット → 終端セル割り当てと、強制空白にすべき側面セル。
    /// ゲートは東向き前提: W=背面, N/S=側面, E=出力。
    /// $_DFF_P_ の Inputs はポート名アルファベット順で [C; D]。
    let private gateTerminals (p: WlPlaced) : (NetId * Coord) list * Coord list =
        let w = toward p.Coord W
        let n = toward p.Coord N
        let s = toward p.Coord S
        match p.Gate.Kind, p.Gate.Inputs with
        | Dff, [clkNet; dNet] -> [ (dNet, w); (clkNet, s) ], [ n ]
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
                    | None -> []
                else
                    [ for t in tapCells do
                        for d in [E; W; N; S] do
                            yield (toward t d, d) ]

            // 探索範囲: シードとゴールの bbox + マージン
            let pts = goal :: (seeds |> List.map fst)
            let margin = 30
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
                         // 他ネットの直線セルは直交方向に通過可 (Cross 化)
                         n2 <> netId && straight && perpendicular nd f2 && c <> goal
                     | Some _ -> false)

            // A*: 状態 = (セル, 進入方向)。交差セル上では直進のみ許可。
            let pq = System.Collections.Generic.PriorityQueue<Coord * Dir, int>()
            let gScore = System.Collections.Generic.Dictionary<Coord * Dir, int>()
            let prev = System.Collections.Generic.Dictionary<Coord * Dir, (Coord * Dir) option>()
            let closed = System.Collections.Generic.HashSet<Coord * Dir>()
            let h (c: Coord) = abs (c.X - goal.X) + abs (c.Y - goal.Y)
            for (c, d) in seeds do
                if passOk c d && not (gScore.ContainsKey ((c, d))) then
                    gScore.[(c, d)] <- 1
                    prev.[(c, d)] <- None
                    pq.Enqueue ((c, d), 1 + h c)
            let mutable goalState = None
            while goalState.IsNone && pq.Count > 0 do
                let (c, d) = pq.Dequeue ()
                if closed.Add ((c, d)) then
                    if c = goal then goalState <- Some (c, d)
                    else
                        let dirs = if isCrossingCell c then [d] else [E; W; N; S]
                        let gc = gScore.[(c, d)]
                        for nd in dirs do
                            let c' = toward c nd
                            if passOk c' nd && not (closed.Contains ((c', nd))) then
                                let ng = gc + 1
                                if not (gScore.ContainsKey ((c', nd))) || ng < gScore.[(c', nd)] then
                                    gScore.[(c', nd)] <- ng
                                    prev.[(c', nd)] <- Some (c, d)
                                    pq.Enqueue ((c', nd), ng + h c')

            match goalState with
            | None -> Error (RoutingCongestion netId)
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
                        addNetCell netId c)
                Ok ()

        // 全ゲートの全入力終端を順に配線
        let terminals =
            placed |> List.collect (fun p -> fst (gateTerminals p))
        terminals
        |> List.fold (fun acc (nid, goal) ->
            acc |> Result.bind (fun () -> routeOne nid goal))
            (Ok ())
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

    /// yosys JSON → WireLevel グリッド。
    /// 戻り値: (グリッド, 配置, ピン座標)。出力ネットの観測は駆動ゲートのセルで行う。
    let compileWL (src: string)
        : Result<LGrid * WlPlaced list * Map<NetId, Coord>, CompileError> =
        frontend src
        |> Result.bind (fun nl ->
            match nl.Gates |> List.tryFind (fun g -> not (mappable g.Kind)) with
            | Some g -> Error (UnmappableGate g.Kind)
            | None ->
                let placed, pins = placeWL nl
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
