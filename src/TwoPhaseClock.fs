namespace WwHdl

// ---------------------------------------------------------------------
// 2 相ノンオーバーラップクロック (WireLevel コンパイル経路専用)
//
// 背景: WireLevel ではクロックの立ち上がりが配線上を波として伝わるため、DFF ごとに
// 到達時刻がずれる (skew)。近い DFF の新しい Q が、遠い DFF の D にそのクロックより
// 先に届くと hold 違反になる。単相 (SingleEdge) ではクロック木の長さを揃えて対処するが、
// 密な配置では蛇行の余地がなく破綻する (sm83_full + anneal で hold 違反 144 組)。
//
// 2 相 (TwoPhase) では各 DFF をマスター / スレーブに分け、別々のクロックで駆動する:
//
//     d ──▶ [マスター DFF] ── m ──▶ [スレーブ DFF] ──▶ q (元の Q ネット)
//              ▲ clk_a                  ▲ clk_b
//
// ホストは「clk_a を上げて settle → clk_a を下げ clk_b を上げて settle」の順に駆動する。
//   * clk_a の相: マスターの D はスレーブ Q (と外部入力) の組合せ関数。スレーブは動かない
//     (clk_b = 0) ので、マスターがいつクロックを見ても D は安定している
//   * clk_b の相: スレーブの D はマスター Q そのもの。マスターは動かない (clk_a は下がるだけ)
// 相の間に settle があるので、skew がいくら大きくても hold 違反は構造的に起こらない。
//
// 変換はフロントエンドの論理 Netlist (NetlistSim / Testbench / golden が使う) には
// 適用しない。WL の配置配線の直前でだけ toTwoPhase を通す。
// ---------------------------------------------------------------------
module Clocking =
    open Netlist

    /// 順序回路のクロック方式。
    type ClockingScheme =
        /// 単相: 全 DFF を 1 本のクロックの立ち上がりで駆動する (従来方式)。
        /// hold はクロック木の skew 均等化で守る。
        | SingleEdge
        /// 2 相ノンオーバーラップ: 各 DFF をマスター (clk_a) / スレーブ (clk_b) に分ける。
        /// hold は相の間の settle で構造的に守られる。
        | TwoPhase

    /// 元の 1 個の DFF から作ったマスター / スレーブの組。
    type MasterSlavePair =
        { OriginalGateId: int
          /// Inputs = [clkA; d]、Output = 新ネット (マスター Q)
          Master: Gate
          /// Inputs = [clkB; マスター Q]、Output = 元の Q ネット
          Slave: Gate }

    /// 2 相化したネットリスト。
    type TwoPhaseNetlist =
        { /// 変換後のネットリスト。クロックは 2 本あるので ClockNet は None
          /// (クロックは ClockA / ClockB が持つ)。PrimaryInputs は元のクロックを
          /// ClockA / ClockB で置き換えたもの
          Netlist: Netlist
          /// 変換前のクロックネット (外部入力ピン)。変換後のネットリストには現れない
          OriginalClock: NetId
          /// マスターを駆動するクロック (外部入力ピン)
          ClockA: NetId
          /// スレーブを駆動するクロック (外部入力ピン)
          ClockB: NetId
          /// 元の DFF の宣言順
          Pairs: MasterSlavePair list }

    /// 2 相化できない回路。黙って変換せず、理由を返す。
    type TwoPhaseError =
        /// DFF がない (組合せ回路に 2 相クロックは意味がない)
        | NoFlipFlops
        /// DFF のクロックが複数のネットに分かれている
        | MultipleClockNets of clocks: NetId list
        /// DFF のクロックがゲート出力 (ゲーテッドクロック・分周クロック)
        | ClockDrivenByGate of clock: NetId * gateId: int
        /// DFF のクロックが外部入力ピンでない (未駆動ネット)
        | ClockNotPrimaryInput of clock: NetId
        /// クロックがデータとしても使われている (ゲート入力 / DFF の D)。
        /// 2 相化で元のクロックは消えるため、そのゲートの入力が宙に浮く
        | ClockUsedAsData of clock: NetId * gateId: int
        /// DFF の入力が [C; D] / [C; D; R] のどちらでもない
        | UnsupportedDffInputs of gateId: int * inputCount: int

    let describeTwoPhaseError (e: TwoPhaseError) : string =
        match e with
        | NoFlipFlops -> "DFF がないので 2 相化できない"
        | MultipleClockNets clocks ->
            sprintf "DFF のクロックが複数ある (%s)" (clocks |> List.map (fun (NetId n) -> string n) |> String.concat ", ")
        | ClockDrivenByGate (NetId n, gateId) -> sprintf "クロック (ネット %d) がゲート %d の出力で駆動されている" n gateId
        | ClockNotPrimaryInput (NetId n) -> sprintf "クロック (ネット %d) が外部入力ピンでない" n
        | ClockUsedAsData (NetId n, gateId) -> sprintf "クロック (ネット %d) がゲート %d のデータ入力に使われている" n gateId
        | UnsupportedDffInputs (gateId, count) -> sprintf "DFF (ゲート %d) の入力が %d 本 ([C; D] または [C; D; R] のみ対応)" gateId count

    /// DFF の (クロック, D)。R (非同期リセット) は WireLevel では配線しない (従来どおり無視)。
    let private dffPorts (g: Gate) : Result<NetId * NetId, TwoPhaseError> =
        match g.Inputs with
        | [ clk; d ] -> Ok (clk, d)
        | [ clk; d; _reset ] -> Ok (clk, d)
        | ins -> Error (UnsupportedDffInputs (g.Id, ins.Length))

    let private traverse (f: 'a -> Result<'b, 'e>) (xs: 'a list) : Result<'b list, 'e> =
        let folder x acc =
            match f x, acc with
            | Ok y, Ok ys -> Ok (y :: ys)
            | Error e, _ -> Error e
            | _, Error e -> Error e
        List.foldBack folder xs (Ok [])

    /// 唯一のクロックを特定し、外部入力ピン由来でデータに使われていないことを確かめる。
    let private validateClock (nl: Netlist) (dffClocks: (Gate * NetId * NetId) list) : Result<NetId, TwoPhaseError> =
        match dffClocks |> List.map (fun (_, clk, _) -> clk) |> List.distinct with
        | [] -> Error NoFlipFlops
        | _ :: _ :: _ as clocks -> Error (MultipleClockNets clocks)
        | [ clk ] ->
            let drivingGate = nl.Gates |> List.tryFind (fun g -> g.Output = clk)
            let dataUse =
                nl.Gates |> List.tryFind (fun g ->
                    match g.Kind with
                    | Dff -> dffClocks |> List.exists (fun (dff, _, d) -> dff.Id = g.Id && d = clk)
                    | _ -> List.contains clk g.Inputs)
            match drivingGate, dataUse with
            | Some g, _ -> Error (ClockDrivenByGate (clk, g.Id))
            | None, _ when not (List.contains clk nl.PrimaryInputs) -> Error (ClockNotPrimaryInput clk)
            | None, Some g -> Error (ClockUsedAsData (clk, g.Id))
            | None, None -> Ok clk

    /// ネットリスト中で使われている最大の NetId (新ネットの採番の基点)。
    let private maxNetId (nl: Netlist) : int =
        let nets =
            [ yield! nl.PrimaryInputs
              yield! nl.PrimaryOutputs
              yield! Option.toList nl.ClockNet
              for g in nl.Gates do
                  yield g.Output
                  yield! g.Inputs ]
        nets |> List.map (fun (NetId n) -> n) |> List.fold max 0

    /// 各 DFF をマスター (clk_a) + スレーブ (clk_b) に分ける (純粋関数)。
    ///   * 新しいネット (clk_a, clk_b, 各マスターの Q) は既存の最大 NetId より後ろに採番する
    ///   * ゲートの並びは元の順序を保ち、DFF の位置にマスター → スレーブの順で 2 個入れる
    ///     (行優先配置でマスターとスレーブが隣り合う)。Gate.Id は新しい並びの添字で振り直す
    ///   * DFF 以外のゲートとクロック以外のネットは変えない
    let toTwoPhase (nl: Netlist) : Result<TwoPhaseNetlist, TwoPhaseError> =
        nl.Gates
        |> List.filter (fun g -> g.Kind = Dff)
        |> traverse (fun g -> dffPorts g |> Result.map (fun (clk, d) -> g, clk, d))
        |> Result.bind (fun dffClocks ->
            validateClock nl dffClocks
            |> Result.map (fun originalClock ->
                let firstFree = maxNetId nl + 1
                let clockA = NetId firstFree
                let clockB = NetId (firstFree + 1)
                let firstMasterNet = firstFree + 2
                let dataOf = dffClocks |> List.map (fun (g, _, d) -> g.Id, d) |> Map.ofList
                // DFF の宣言順 k → マスター Q のネット firstMasterNet + k
                let masterNetOf =
                    dffClocks |> List.mapi (fun k (g, _, _) -> g.Id, NetId (firstMasterNet + k)) |> Map.ofList
                let expanded =
                    nl.Gates |> List.collect (fun g ->
                        match g.Kind with
                        | Dff ->
                            let masterQ = masterNetOf.[g.Id]
                            [ Some g.Id, { g with Inputs = [ clockA; dataOf.[g.Id] ]; Output = masterQ }
                              None,      { g with Inputs = [ clockB; masterQ ] } ]
                        | _ -> [ None, g ])
                    |> List.mapi (fun i (origin, g) -> origin, { g with Id = i })
                let gates = expanded |> List.map snd
                let pairs =
                    expanded
                    |> List.pairwise
                    |> List.choose (fun ((origin, master), (_, slave)) ->
                        origin |> Option.map (fun id -> { OriginalGateId = id; Master = master; Slave = slave }))
                let primaryInputs =
                    nl.PrimaryInputs |> List.collect (fun n -> if n = originalClock then [ clockA; clockB ] else [ n ])
                { Netlist =
                    { Gates = gates
                      PrimaryInputs = primaryInputs
                      PrimaryOutputs = nl.PrimaryOutputs
                      ClockNet = None }
                  OriginalClock = originalClock
                  ClockA = clockA
                  ClockB = clockB
                  Pairs = pairs }))


// ---------------------------------------------------------------------
// クロック駆動 (CA レベル、ホスト操作)。テストと検証スクリプトが共有する。
// 契約は DESIGN-VERIFY.md §5.1 / §5.2.1 (2 相の短縮された手順、2026-09-27) と、
// wgpu-runner の clocking.rs と同じ。
//
// 2 相の定常状態では、周期の終わりは clkA=0, clkB=1 のまま返す (単相のように clk=0 へ
// 戻す settleLow は行わない)。次の周期の clockEdge が、その clkB=1 の立ち下がりを
// 自分の立ち上がりと同じ収束にまとめて処理する (settle_idle / settle_after_data /
// latch の分担は Rust の clocking.rs を参照)。
// ---------------------------------------------------------------------
module ClockDrive =
    open Domain
    open WireLevel

    /// grid 上のクロックピン。
    type ClockPins =
        | SingleEdgePins of clk: Coord
        | TwoPhasePins of clkA: Coord * clkB: Coord

    /// 収束しなかった相。結果は信用できないので明示的に失敗させる。
    type DriveError =
        | Unsettled of phase: string * limit: int

    let describeDriveError (e: DriveError) : string =
        match e with
        | Unsettled (phase, limit) -> sprintf "%s で %d 世代以内に収束しない" phase limit

    /// 収束待ちの関数 (WireLevel.settle と同じ契約: 収束後グリッドと世代数、上限到達なら limit)。
    type Settler = int -> LGrid -> LGrid * int

    let private settlePhase (settler: Settler) (limit: int) (phase: string) (g: LGrid) : Result<LGrid, DriveError> =
        let settled, t = settler limit g
        if t >= limit then Error (Unsettled (phase, limit)) else Ok settled

    /// クロックには一切触れず、直前に書いた入力 (data_in 相当) だけを収束させる
    /// (Rust clocking.rs の settle_after_data と同じ契約)。2 相の定常状態ではクロックは
    /// 前周期の終わり (clkA=0, clkB=1) のまま — 次の clockEdge がその立ち下がりも兼ねる。
    let settleData (settler: Settler) (limit: int) (phase: string) (g: LGrid) : Result<LGrid, DriveError> =
        settlePhase settler limit phase g

    /// 全クロックを 0 にして収束させる。シーケンスの最初 (リセット直後の 1 周期目) に
    /// 1 回だけ呼べば十分 — 以降は clockEdge がクロックの立ち下がりも兼ねる
    /// (DESIGN-VERIFY.md §5.2.1 手順 1')。
    let settleLow (settler: Settler) (limit: int) (pins: ClockPins) (g: LGrid) : Result<LGrid, DriveError> =
        let lowered =
            match pins with
            | SingleEdgePins clk -> setPin clk false g
            | TwoPhasePins (clkA, clkB) -> g |> setPin clkA false |> setPin clkB false
        settlePhase settler limit "clk=0 (setup)" lowered

    /// クロックの有効エッジを与えて収束させる。
    ///   SingleEdge: 前提は settleLow 済み (clk=0)。clk=1 → settle
    ///   TwoPhase:   前提は「clkA=0, clkB=1 (定常)」か「clkA=0, clkB=0 (settleLow 直後)」。
    ///     clkA=1 と clkB=0 を同時に書いて収束 (前周期の clkB=1 をここで下ろす。スレーブは
    ///     立ち下がりを見ないので影響を受けず、マスターの D はスレーブ Q の組合せ関数で
    ///     この収束の間は動かないので安全。すでに clkB=0 なら単なる no-op 書込)
    ///     → clkA=0 と clkB=1 を同時に書いて収束
    let clockEdge (settler: Settler) (limit: int) (pins: ClockPins) (g: LGrid) : Result<LGrid, DriveError> =
        match pins with
        | SingleEdgePins clk -> settlePhase settler limit "clk=1" (setPin clk true g)
        | TwoPhasePins (clkA, clkB) ->
            g |> setPin clkA true |> setPin clkB false
            |> settlePhase settler limit "clk_a=1, clk_b=0"
            |> Result.bind (fun g -> g |> setPin clkA false |> setPin clkB true |> settlePhase settler limit "clk_a=0, clk_b=1")

    /// 1 周期。前提: 直前の呼出しが settleLow か cycle であること。
    ///   SingleEdge (変更なし): settleLow → clockEdge → settleLow (クロックを 0 に戻して返す)
    ///   TwoPhase: data の収束 (クロックには触れない) → clockEdge (前周期の立ち下がりを
    ///     次の立ち上がりに畳み込む)。戻り値のクロックは clkA=0, clkB=1 のまま
    ///     (次の cycle 呼出しがそこから立ち下げる)。data 入力は呼び出し側であらかじめ
    ///     書いておくこと (setPin などで cycle に渡す g に反映させておく)。
    let cycle (settler: Settler) (limit: int) (pins: ClockPins) (g: LGrid) : Result<LGrid, DriveError> =
        match pins with
        | SingleEdgePins _ ->
            settleLow settler limit pins g
            |> Result.bind (clockEdge settler limit pins)
            |> Result.bind (settleLow settler limit pins)
        | TwoPhasePins _ ->
            settleData settler limit "data (clk unchanged)" g
            |> Result.bind (clockEdge settler limit pins)
