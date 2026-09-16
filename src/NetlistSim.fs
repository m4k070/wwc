namespace WwHdl

// ---------------------------------------------------------------------
// 11. NetlistSim — ゲートレベルの周期シミュレータ (TODO Step B-1, DESIGN-VERIFY.md §5.5)
//
// 合成済みネットリスト (yosys JSON → Pipeline.frontend) を WireLevel CA と同じ規則で評価する。
// CA との違いはクロックスキューがない (理想形) ことだけにする。これにより
// 「同じネットリストで CA と食い違う = 配線か CA の問題」と切り分けられる。
//
// CA と一致させる規則 (WireLevel.step):
//   * NAND / NOT: 接続された入力の AND の否定。入力 1 本なら NOT、0 本なら 0。
//     frontend が yosys の定数入力 ("0"/"1") を落としたゲートもこの規則で評価する
//     (定数 0 入力が落ちると RTL と食い違うが CA も同じ。RTL との照合は B-5 で行う)
//   * DFF: クロックの立ち上がりで q := D。初期値 q=0。$_DFF_PP0_ の R は無視
//
// apply 1 回 = CA の「ピンを書いて settle」1 回:
//   1. クロック入力が 0→1 に変わるなら、全 DFF が直前の (収束済み) D を取り込む
//   2. 入力を反映し、組合せゲートをトポロジカル順に 1 パスで評価する
// クロックの立ち上がりと他の入力の変更を同じ apply で行わないこと (CA の setup 前提と同じ)。
// ---------------------------------------------------------------------
module NetlistSim =
    open Netlist
    open RoutedArtifact

    type SimError =
        | UnsupportedGate of gateId: int * kind: GateKind
        | UnexpectedArity of gateId: int * kind: GateKind * inputCount: int
        | MultipleDrivers of net: NetId
        | UndrivenNet of gateId: int * net: NetId
        | GatedClock of gateId: int * clockInput: NetId
        | CombinationalLoop of nets: NetId list
        | NotPrimaryInput of net: NetId
        | UndrivenOutput of port: string * bitIndex: int * net: NetId

    let describeSimError (e: SimError) : string =
        let netNo (NetId n) = n
        match e with
        | UnsupportedGate (id, kind) -> sprintf "gate %d: 未対応の種別 %A (NAND/NOT/DFF のみ)" id kind
        | UnexpectedArity (id, kind, n) -> sprintf "gate %d: %A の入力数 %d は想定外" id kind n
        | MultipleDrivers net -> sprintf "net %d に駆動元が複数ある" (netNo net)
        | UndrivenNet (id, net) -> sprintf "gate %d の入力 net %d に駆動元がない" id (netNo net)
        | GatedClock (id, clk) -> sprintf "DFF gate %d のクロック入力 net %d が主クロックではない" id (netNo clk)
        | CombinationalLoop nets ->
            let shown = nets |> List.truncate 10 |> List.map (netNo >> string) |> String.concat ", "
            sprintf "組合せ回路に閉路がある (%d nets: %s%s)" nets.Length shown (if nets.Length > 10 then ", …" else "")
        | NotPrimaryInput net -> sprintf "net %d は外部入力ではないので値を書けない" (netNo net)
        | UndrivenOutput (port, i, net) -> sprintf "出力 %s[%d] (net %d) に駆動元がない" port i (netNo net)

    /// 組合せゲート (NAND/NOT)。インデックスは CompiledNetlist.NetIndex の密な番号。
    type SimGate = { Output: int; Inputs: int[] }

    type SimDff = { GateId: int; Output: int; D: int }

    /// 評価順などを前計算したネットリスト。
    type CompiledNetlist =
        { NetIndex: Map<NetId, int>
          PrimaryInputs: Set<NetId>
          /// 主クロックのネットと密インデックス (DFF がなければ None)
          Clock: (NetId * int) option
          /// トポロジカル順に並べた組合せゲート
          CombGates: SimGate[]
          Dffs: SimDff[] }

    /// ネット値 (密インデックス)。DFF の q も出力ネットの値として持つ。
    type SimState = { Values: bool[] }

    let private traverse (f: 'a -> Result<'b, 'e>) (xs: 'a list) : Result<'b list, 'e> =
        let folder x acc =
            match f x, acc with
            | Ok y, Ok ys -> Ok (y :: ys)
            | Error e, _ -> Error e
            | _, Error e -> Error e
        List.foldBack folder xs (Ok [])

    // --- コンパイル ---------------------------------------------------------

    /// CA のゲート終端割り当て (PipelineWL.gateTerminals) が扱える形か。
    let private checkGate (g: Gate) : Result<Gate, SimError> =
        match g.Kind, g.Inputs.Length with
        | (Nand | Not), n when n <= 3 -> Ok g
        | Dff, (2 | 3) -> Ok g   // [C; D] または [C; D; R] (R は無視)
        | (Nand | Not | Dff), n -> Error (UnexpectedArity (g.Id, g.Kind, n))
        | kind, _ -> Error (UnsupportedGate (g.Id, kind))

    let private buildDrivers (nl: Netlist) : Result<Set<NetId>, SimError> =
        let driven = nl.PrimaryInputs @ (nl.Gates |> List.map (fun g -> g.Output))
        let duplicate =
            driven
            |> List.countBy id
            |> List.tryFind (fun (_, n) -> n > 1)
        match duplicate with
        | Some (net, _) -> Error (MultipleDrivers net)
        | None -> Ok (Set.ofList driven)

    let private checkInputsDriven (driven: Set<NetId>) (g: Gate) : Result<Gate, SimError> =
        match g.Inputs |> List.tryFind (fun net -> not (driven.Contains net)) with
        | Some net -> Error (UndrivenNet (g.Id, net))
        | None -> Ok g

    let private checkClock (clockNet: NetId option) (g: Gate) : Result<Gate, SimError> =
        match g.Kind, g.Inputs, clockNet with
        | Dff, c :: _, Some clk when c = clk -> Ok g
        | Dff, c :: _, _ -> Error (GatedClock (g.Id, c))
        | _ -> Ok g

    /// 組合せゲートを Kahn 法で並べる。DFF 出力と外部入力が source になる。
    let private topoSort (index: Map<NetId, int>) (combGates: Gate list) : Result<SimGate[], SimError> =
        let gates = combGates |> Array.ofList
        let combDriver =
            gates |> Array.mapi (fun i g -> g.Output, i) |> Map.ofArray
        // 入力ごとに「組合せゲートが駆動しているなら依存」を数える (同じネットの重複入力も数える)
        let consumers = System.Collections.Generic.Dictionary<int, ResizeArray<int>> ()
        let pending = Array.zeroCreate gates.Length
        gates |> Array.iteri (fun i g ->
            for net in g.Inputs do
                match Map.tryFind net combDriver with
                | Some src ->
                    pending.[i] <- pending.[i] + 1
                    match consumers.TryGetValue src with
                    | true, list -> list.Add i
                    | _ -> consumers.[src] <- ResizeArray [ i ]
                | None -> ())
        let ready = System.Collections.Generic.Queue<int> ()
        pending |> Array.iteri (fun i n -> if n = 0 then ready.Enqueue i)
        let order = ResizeArray<int> ()
        while ready.Count > 0 do
            let i = ready.Dequeue ()
            order.Add i
            match consumers.TryGetValue i with
            | true, list ->
                for j in list do
                    pending.[j] <- pending.[j] - 1
                    if pending.[j] = 0 then ready.Enqueue j
            | _ -> ()
        if order.Count < gates.Length then
            let inOrder = Set.ofSeq order
            let loopNets =
                gates
                |> Array.indexed
                |> Array.filter (fun (i, _) -> not (inOrder.Contains i))
                |> Array.map (fun (_, g) -> g.Output)
                |> List.ofArray
            Error (CombinationalLoop loopNets)
        else
            order
            |> Seq.map (fun i ->
                let g = gates.[i]
                { Output = index.[g.Output]; Inputs = g.Inputs |> List.map (fun n -> index.[n]) |> Array.ofList })
            |> Array.ofSeq
            |> Ok

    let compile (nl: Netlist) : Result<CompiledNetlist, SimError> =
        nl.Gates
        |> traverse checkGate
        |> Result.bind (fun gates ->
            buildDrivers nl
            |> Result.bind (fun driven ->
                gates
                |> traverse (checkInputsDriven driven)
                |> Result.bind (traverse (checkClock nl.ClockNet))
                |> Result.bind (fun gates ->
                    let index = driven |> Seq.mapi (fun i net -> net, i) |> Map.ofSeq
                    let dffGates, combGates = gates |> List.partition (fun g -> g.Kind = Dff)
                    topoSort index combGates
                    |> Result.map (fun sorted ->
                        let dffs =
                            dffGates
                            |> List.map (fun g ->
                                // Inputs は [C; D] または [C; D; R]
                                { GateId = g.Id; Output = index.[g.Output]; D = index.[List.item 1 g.Inputs] })
                            |> Array.ofList
                        let clock =
                            if dffs.Length = 0 then None
                            else nl.ClockNet |> Option.map (fun clk -> clk, index.[clk])
                        { NetIndex = index
                          PrimaryInputs = Set.ofList nl.PrimaryInputs
                          Clock = clock
                          CombGates = sorted
                          Dffs = dffs }))))

    // --- 実行 ---------------------------------------------------------------

    /// 全ネット 0 (CA の初期グリッドと同じ。組合せ回路は最初の apply で評価される)。
    let initial (c: CompiledNetlist) : SimState =
        { Values = Array.zeroCreate c.NetIndex.Count }

    /// 入力を書いて収束させる (CA の setPin + settle に対応)。書かなかった入力は前の値を保つ。
    let apply (c: CompiledNetlist) (inputs: Map<NetId, bool>) (s: SimState) : Result<SimState, SimError> =
        match inputs |> Map.tryFindKey (fun net _ -> not (c.PrimaryInputs.Contains net)) with
        | Some net -> Error (NotPrimaryInput net)
        | None ->
            let values = Array.copy s.Values
            let isRisingEdge =
                match c.Clock with
                | Some (clkNet, clkIndex) ->
                    let nextClock = inputs |> Map.tryFind clkNet |> Option.defaultValue s.Values.[clkIndex]
                    not s.Values.[clkIndex] && nextClock
                | None -> false
            if isRisingEdge then
                for dff in c.Dffs do
                    values.[dff.Output] <- s.Values.[dff.D]
            for KeyValue (net, v) in inputs do
                values.[c.NetIndex.[net]] <- v
            for g in c.CombGates do
                values.[g.Output] <-
                    if g.Inputs.Length = 0 then false
                    else not (g.Inputs |> Array.forall (fun i -> values.[i]))
            Ok { Values = values }

    /// 入力ポートに整数値を書くためのネット割り当て (LSB first)。定数ビットは無視する。
    let portInputs (port: YosysPortBits) (value: uint64) : Map<NetId, bool> =
        port.Bits
        |> List.indexed
        |> List.choose (fun (i, bit) ->
            match bit with
            | NetBit net -> Some (net, (value >>> i) &&& 1UL = 1UL)
            | ConstBit _ -> None)
        |> Map.ofList

    /// ポートの値を読む (LSB first、定数ビット込み)。
    let readPort (c: CompiledNetlist) (s: SimState) (port: YosysPortBits) : Result<uint64, SimError> =
        port.Bits
        |> List.indexed
        |> List.fold
            (fun acc (i, bit) ->
                acc
                |> Result.bind (fun value ->
                    let withBit b = if b then value ||| (1UL <<< i) else value
                    match bit with
                    | ConstBit b -> Ok (withBit b)
                    | NetBit net ->
                        match Map.tryFind net c.NetIndex with
                        | Some idx -> Ok (withBit s.Values.[idx])
                        | None -> Error (UndrivenOutput (port.Name, i, net))))
            (Ok 0UL)
