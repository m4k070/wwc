namespace WwHdl

// ---------------------------------------------------------------------
// WL-2PH: 2 相ノンオーバーラップクロック (Clocking.TwoPhase)
//   * toTwoPhase の単体 (DFF の分割、ネットの採番、エラー)
//   * settleIncremental が settle と同じ結果を返すこと (CA レベル検証の高速化の前提)
//   * counter4 / reg8 を 2 相でコンパイルし、2 相の手順で WireLevel を駆動して論理が正しいこと
//   * skew を意図的に大きくしたシフトレジスタで、単相は hold 違反で誤動作し 2 相は正しく動くこと
//   * sm83_min を 2 相 + anneal でコンパイルし、20 命令の期待値 (GPU 検証済み) と CA レベルで一致すること
//   * meta (formatVersion 2) の clocking の往復と、旧形式 (formatVersion 1) の読込
// ---------------------------------------------------------------------
module WlTwoPhaseTest =
    open System
    open System.IO
    open Domain
    open Netlist
    open WireLevel
    open Clocking
    open PipelineWL
    open RoutedArtifact
    open HoldAnalysis

    let private verilogPath (name: string) =
        Path.Combine (__SOURCE_DIRECTORY__, "..", "verilog", name + ".json")

    let private gate id kind inputs output =
        { Id = id; Kind = kind; Inputs = inputs |> List.map NetId; Output = NetId output }

    let private netlistOf gates inputs outputs clock =
        { Gates = gates
          PrimaryInputs = inputs |> List.map NetId
          PrimaryOutputs = outputs |> List.map NetId
          ClockNet = clock |> Option.map NetId }

    /// 収束待ちの上限 (世代)。sm83_min の 1 相は 1000 世代前後で収束する。
    let private settleLimit = 20000

    /// CA レベルの駆動は settleIncremental (settle と同値であることを incrementalTests で確認する)。
    let private settler : ClockDrive.Settler = settleIncremental

    /// anneal の手数 (WlPlacementTest の E2E と同じ。小回路なので十分最適化される)。
    let private annealMoves = 100_000

    let private annealed = GatePlacement.Annealed { GatePlacement.defaultAnnealConfig with Moves = annealMoves }

    // --- toTwoPhase 単体 ---------------------------------------------------

    // clk=2, d=3, rst=4。g0: NAND(d, q1) → 10、g1: DFF [clk; 10] → q1=11、
    // g2: DFF [clk; q1; rst] → q2=12 (非同期リセット付き)、g3: NOT q2 → 13
    let private sampleNetlist =
        netlistOf
            [ gate 0 Nand [ 3; 11 ] 10
              gate 1 Dff [ 2; 10 ] 11
              gate 2 Dff [ 2; 11; 4 ] 12
              gate 3 Not [ 12 ] 13 ]
            [ 2; 3; 4 ] [ 12; 13 ] (Some 2)

    let private unitTests () : (string * bool) list =
        match toTwoPhase sampleNetlist with
        | Error e -> [ sprintf "WL-2PH: toTwoPhase succeeds (%s)" (describeTwoPhaseError e), false ]
        | Ok tp ->
            let out = tp.Netlist
            let dffsBefore = sampleNetlist.Gates |> List.filter (fun g -> g.Kind = Dff)
            let dffsAfter = out.Gates |> List.filter (fun g -> g.Kind = Dff)
            let readersOf (net: NetId) = out.Gates |> List.filter (fun g -> List.contains net g.Inputs)
            let masterFeedsOnlyItsSlave =
                tp.Pairs |> List.forall (fun p ->
                    readersOf p.Master.Output = [ p.Slave ]
                    && p.Slave.Inputs = [ tp.ClockB; p.Master.Output ]
                    && not (List.contains p.Master.Output out.PrimaryOutputs))
            let mastersClockedByA =
                tp.Pairs |> List.forall (fun p -> List.tryHead p.Master.Inputs = Some tp.ClockA && p.Master.Inputs.Length = 2)
            let masterDataKept =
                List.zip tp.Pairs dffsBefore
                |> List.forall (fun (p, original) -> p.Master.Inputs.[1] = original.Inputs.[1] && p.OriginalGateId = original.Id)
            let slaveOutputsAreOriginalQ =
                (tp.Pairs |> List.map (fun p -> p.Slave.Output)) = (dffsBefore |> List.map (fun g -> g.Output))
            let combOf (nl: Netlist) =
                nl.Gates |> List.filter (fun g -> g.Kind <> Dff) |> List.map (fun g -> g.Kind, g.Inputs, g.Output)
            let usedNets (nl: Netlist) =
                nl.Gates |> List.collect (fun g -> g.Output :: g.Inputs) |> Set.ofList
            let originalNets = usedNets sampleNetlist |> Set.remove tp.OriginalClock |> Set.remove (NetId 4)  // rst は WL で無視
            let newNets = Set.ofList (tp.ClockA :: tp.ClockB :: (tp.Pairs |> List.map (fun p -> p.Master.Output)))
            let maxOriginal = usedNets sampleNetlist |> Set.map (fun (NetId n) -> n) |> Set.maxElement
            let freshNets =
                newNets.Count = 2 + tp.Pairs.Length
                && newNets |> Set.forall (fun (NetId n) -> n > maxOriginal)
            let idsSequential = out.Gates |> List.mapi (fun i g -> g.Id = i) |> List.forall id
            let adjacent =
                tp.Pairs |> List.forall (fun p -> p.Slave.Id = p.Master.Id + 1)
            [ sprintf "WL-2PH: DFF count doubles (%d -> %d)" dffsBefore.Length dffsAfter.Length,
              dffsAfter.Length = 2 * dffsBefore.Length && tp.Pairs.Length = dffsBefore.Length
              "WL-2PH: each master output feeds exactly its slave ([clkB; m])", masterFeedsOnlyItsSlave
              "WL-2PH: masters are [clkA; d] (d kept, async reset dropped)", mastersClockedByA && masterDataKept
              "WL-2PH: slave outputs are the original Q nets", slaveOutputsAreOriginalQ
              "WL-2PH: combinational gates and non-clock nets unchanged",
              combOf out = combOf sampleNetlist
              && Set.isSubset originalNets (usedNets out)
              && out.PrimaryOutputs = sampleNetlist.PrimaryOutputs
              "WL-2PH: new nets (clkA, clkB, master Q) do not collide", freshNets
              "WL-2PH: clk replaced by clkA/clkB in primary inputs, ClockNet = None",
              out.PrimaryInputs = [ tp.ClockA; tp.ClockB; NetId 3; NetId 4 ] && out.ClockNet = None
              "WL-2PH: gate ids renumbered, master/slave adjacent", idsSequential && adjacent ]

    let private errorTests () : (string * bool) list =
        let expectError (nl: Netlist) (isExpected: TwoPhaseError -> bool) =
            match toTwoPhase nl with
            | Error e -> isExpected e
            | Ok _ -> false
        // クロックがゲート出力 (NOT で反転したクロック)
        let gatedClock =
            netlistOf [ gate 0 Not [ 2 ] 10; gate 1 Dff [ 10; 3 ] 11 ] [ 2; 3 ] [ 11 ] (Some 2)
        let twoClocks =
            netlistOf [ gate 0 Dff [ 2; 4 ] 10; gate 1 Dff [ 3; 4 ] 11 ] [ 2; 3; 4 ] [ 10; 11 ] (Some 2)
        let combinational = netlistOf [ gate 0 Nand [ 2; 3 ] 10 ] [ 2; 3 ] [ 10 ] None
        let clockAsData =
            netlistOf [ gate 0 Nand [ 2; 3 ] 10; gate 1 Dff [ 2; 10 ] 11 ] [ 2; 3 ] [ 11 ] (Some 2)
        let undrivenClock = netlistOf [ gate 0 Dff [ 9; 3 ] 11 ] [ 3 ] [ 11 ] None
        let oneInputDff = netlistOf [ gate 0 Dff [ 2 ] 11 ] [ 2 ] [ 11 ] (Some 2)
        let pipelineRejects =
            match compileNetlistWL { defaultCompileOptions with Clocking = TwoPhase } gatedClock with
            | Error (Route.UnsupportedClocking (ClockDrivenByGate _)) -> true
            | _ -> false
        [ "WL-2PH: rejects gate-driven clock",
          expectError gatedClock (function ClockDrivenByGate (NetId 10, 0) -> true | _ -> false)
          "WL-2PH: rejects multiple clock nets",
          expectError twoClocks (function MultipleClockNets [ NetId 2; NetId 3 ] -> true | _ -> false)
          "WL-2PH: rejects circuit without DFF", expectError combinational ((=) NoFlipFlops)
          "WL-2PH: rejects clock used as data",
          expectError clockAsData (function ClockUsedAsData (NetId 2, 0) -> true | _ -> false)
          "WL-2PH: rejects clock that is not a primary input",
          expectError undrivenClock (function ClockNotPrimaryInput (NetId 9) -> true | _ -> false)
          "WL-2PH: rejects DFF with unsupported inputs",
          expectError oneInputDff (function UnsupportedDffInputs (0, 1) -> true | _ -> false)
          "WL-2PH: compileNetlistWL reports UnsupportedClocking", pipelineRejects ]

    // --- settleIncremental = settle ---------------------------------------------

    let private incrementalTests () : (string * bool) list =
        let path = verilogPath "counter4"
        if not (File.Exists path) then [ "WL-2PH: counter4.json present", false ] else
        match compileWL (File.ReadAllText path) with
        | Error e -> [ sprintf "WL-2PH: counter4 compiles (%A)" e, false ]
        | Ok (grid, _, pins) ->
            let clk = pins |> Map.toList |> List.head |> snd
            // 初期状態・立ち上がり・立ち下がりを数周期ぶん両方の実装で進めて比べる
            let limit = 2000
            let steps = [ false; true; false; true; false; true ]
            let rec compare (g: LGrid) (levels: bool list) =
                match levels with
                | [] -> true
                | v :: rest ->
                    let input = setPin clk v g
                    let reference = settle limit input
                    let incremental = settleIncremental limit input
                    reference = incremental && compare (fst reference) rest
            // 上限到達時も同じ (グリッド, limit) を返すこと
            let capped =
                let input = setPin clk true grid
                settle 3 input = settleIncremental 3 input
            [ "WL-2PH: settleIncremental equals settle (counter4, 6 phases)", compare grid steps
              "WL-2PH: settleIncremental equals settle when the limit is hit", capped ]

    // --- counter4 / reg8 を 2 相で ----------------------------------------------

    let private qBitsOf (json: string) =
        match Pipeline.parseYosysJson json with
        | Ok m -> m.Ports.["q"].Bits
        | Error _ -> []

    /// 2 相の不変条件 (同相 DFF 間経路 0 本) をグリッドで確かめる。
    let private invariantHolds (c: WlCompiled) : bool =
        match clockPinsOf c with
        | Some (ClockDrive.TwoPhasePins (a, b)) ->
            let dg = toDense c.Grid
            match indexOf dg a, indexOf dg b with
            | Some ai, Some bi ->
                let r = analyzeTwoPhase dg ai bi
                if not (twoPhaseInvariantHolds r) then
                    printfn "  WL_2PH_INVARIANT: unclocked=%d doubly=%d samePhase=%d gated=%d"
                        r.Unclocked.Length r.DoublyClocked.Length r.SamePhasePaths.Length r.GatedClocks.Length
                twoPhaseInvariantHolds r && r.AToB > 0
            | _ -> false
        | _ -> false

    let private logicTests () : (string * bool) list =
        [ for circuit in [ "counter4"; "reg8" ] do
            for placeLabel, placement in [ "rowmajor", GatePlacement.RowMajor; "anneal", annealed ] do
                let path = verilogPath circuit
                if not (File.Exists path) then
                    yield sprintf "WL-2PH: %s.json present" circuit, false
                else
                    let json = File.ReadAllText path
                    let opts = { defaultCompileOptions with Placement = placement; Clocking = TwoPhase }
                    match compileWLWithOptions opts json with
                    | Error e ->
                        printfn "  WL_2PH_ERR (%s %s): %A" circuit placeLabel e
                        yield sprintf "WL-2PH: %s %s compiles two-phase" circuit placeLabel, false
                    | Ok c ->
                        match clockPinsOf c with
                        | Some (ClockDrive.TwoPhasePins _ as clocks) ->
                            let qBits = qBitsOf json
                            let init0, ok =
                                match circuit with
                                | "counter4" -> WlCounterTest.verifyCountingWith settler clocks qBits c.Grid c.Placed
                                | _ -> WlReg8Test.verifyWriteReadWith settler clocks qBits c.Grid c.Placed c.Pins
                            let dffCount = c.Placed |> List.filter (fun p -> p.Gate.Kind = Dff) |> List.length
                            yield sprintf "WL-2PH: %s %s two-phase grid (%d DFF) keeps the two-phase invariant"
                                      circuit placeLabel dffCount, invariantHolds c
                            yield sprintf "WL-2PH: %s %s two-phase initial value 0" circuit placeLabel, init0
                            yield sprintf "WL-2PH: %s %s two-phase logic test passes" circuit placeLabel, ok
                        | _ -> yield sprintf "WL-2PH: %s %s has clk_a/clk_b pins" circuit placeLabel, false ]

    // --- hold に強いことの対比 ----------------------------------------------------
    //
    // 8 段のシフトレジスタ (d → q0 → q1 → … → q7) を行優先で並べ、クロックピンを左端列に置いて
    // skew 均等化なし (ShortestOnly) で配線する。左の DFF ほどクロックが早く届くので、
    // 左 → 右へ流れる段では「前段の新しい Q が、まだクロックの届かない後段の D に先に着く」。
    //   * 単相: hold 違反 (HoldAnalysis) があり、1 クロックで複数段を値が素通りして誤動作する
    //   * 2 相: 同じ悪条件 (ピンを左端に置き均等化なし) でも不変条件が成り立ち、正しくシフトする

    let private shiftStages = 8
    let private shiftPitchX, shiftPitchY = 24, 16

    /// clk=2, d=3, q_i = 10+i。g_i: DFF [clk; q_{i-1}] (q_{-1} = d)
    let private shiftRegister =
        netlistOf
            [ for i in 0 .. shiftStages - 1 -> gate i Dff [ 2; (if i = 0 then 3 else 9 + i) ] (10 + i) ]
            [ 2; 3 ] [ for i in 0 .. shiftStages - 1 -> 10 + i ] (Some 2)

    /// シフトイン する値の列 (0/1 が混ざり、連続する 1 もある)
    let private shiftPattern = [ true; false; true; true; false; false; true; false; true; true; true; false ]

    /// ピンを左端列 (従来の外部入力ピン位置) に戻し、均等化なしで配線する。
    let private compileSkewed (scheme: ClockingScheme) : Result<WlCompiled, string> =
        prepareCircuit scheme shiftRegister
        |> Result.mapError (sprintf "%A")
        |> Result.bind (fun circuit ->
            placeCircuitWithStrategy GatePlacement.RowMajor shiftPitchX shiftPitchY circuit
            |> Result.mapError (sprintf "%A")
            |> Result.bind (fun p ->
                // 左端列: i 番目の外部入力は (0, 2 + i*pitchY)。クロックもここに置く (重心に置かない)
                let leftEdge =
                    circuit.Netlist.PrimaryInputs
                    |> List.mapi (fun i net -> net, { X = 0; Y = 2 + i * shiftPitchY })
                    |> Map.ofList
                let pins = p.Pins |> Map.map (fun net c -> Map.tryFind net leftEdge |> Option.defaultValue c)
                routeWLWith ShortestOnly p.Placed pins
                |> Result.mapError (sprintf "%A")
                |> Result.map (fun occ ->
                    { Grid = emitWL p.Placed pins occ; Placed = p.Placed; Pins = pins; Clocking = circuit.Clocking })))

    /// パターンをシフトインし、各周期の q が期待どおりか (全周期一致なら true)。
    let private shiftsCorrectly (c: WlCompiled) : Result<bool, string> =
        match clockPinsOf c with
        | None -> Error "クロックピンがない"
        | Some clocks ->
            let dPin = c.Pins.[NetId 3]
            let qCoord i = c.Placed |> List.find (fun p -> p.Gate.Output = NetId (10 + i)) |> fun p -> p.Coord
            let expected (k: int) (i: int) = if k - i >= 0 then shiftPattern.[k - i] else false
            let folder (state: Result<LGrid * bool, string>) (k: int, bit: bool) =
                state
                |> Result.bind (fun (g, ok) ->
                    ClockDrive.cycle settler settleLimit clocks (setPin dPin bit g)
                    |> Result.mapError ClockDrive.describeDriveError
                    |> Result.map (fun next ->
                        let matches = [ 0 .. shiftStages - 1 ] |> List.forall (fun i -> levelOf next (qCoord i) = expected k i)
                        next, ok && matches))
            // cycle は「直前が settleLow か cycle」を前提とする (2 相はクロックの立ち下がりを
            // 次の cycle に畳み込むため)。コンパイル直後の生グリッドから始めるので、最初に
            // 1 回だけ明示的に settleLow する
            ClockDrive.settleLow settler settleLimit clocks c.Grid
            |> Result.mapError ClockDrive.describeDriveError
            |> Result.map (fun g -> g, true)
            |> fun initial -> List.indexed shiftPattern |> List.fold folder initial
            |> Result.map snd

    let private holdContrastTests () : (string * bool) list =
        match compileSkewed SingleEdge, compileSkewed TwoPhase with
        | Error e, _ | _, Error e -> [ sprintf "WL-2PH: skewed shift register compiles (%s)" e, false ]
        | Ok single, Ok twoPhase ->
            let singleViolations =
                match clockPinsOf single with
                | Some (ClockDrive.SingleEdgePins clk) ->
                    let dg = toDense single.Grid
                    indexOf dg clk |> Option.map (fun i -> holdViolations (analyzeHold dg i) |> List.length)
                | _ -> None
            // 対照: 同じ回路を通常の単相 (ピンを重心・skew 均等化あり) で配線すれば正しく動く
            // → 単相の誤動作は回路ではなく skew (hold) が原因
            let balancedSim =
                compileNetlistWL { defaultCompileOptions with Pitch = FixedPitch (shiftPitchX, shiftPitchY) } shiftRegister
                |> Result.mapError (sprintf "%A")
                |> Result.bind shiftsCorrectly
            let singleSim = shiftsCorrectly single
            let twoPhaseSim = shiftsCorrectly twoPhase
            let skewOf (c: WlCompiled) =
                let arrivals = clockArrivals c.Grid |> List.map snd
                if arrivals.IsEmpty then 0 else List.max arrivals - List.min arrivals
            printfn "  WL_2PH_HOLD: single skew=%d violations=%A sim=%A / two-phase skew=%d sim=%A"
                (skewOf single) singleViolations singleSim (skewOf twoPhase) twoPhaseSim
            [ sprintf "WL-2PH: skewed single-edge shift register has hold violations (%s)"
                  (singleViolations |> Option.map string |> Option.defaultValue "n/a"),
              (match singleViolations with Some n -> n > 0 | None -> false)
              "WL-2PH: control: balanced single-edge shift register shifts correctly", balancedSim = Ok true
              "WL-2PH: skewed single-edge shift register misbehaves in CA", singleSim = Ok false
              "WL-2PH: skewed two-phase shift register keeps the invariant", invariantHolds twoPhase
              "WL-2PH: skewed two-phase shift register shifts correctly in CA", twoPhaseSim = Ok true ]

    // --- sm83_min (380 ゲート) を 2 相 + anneal で -------------------------------

    type private Sm83Step = { Desc: string; Inst: int; A: int; B: int; Pc: int; Flags: int }

    let private loadProgram (path: string) : Sm83Step list =
        use doc = Text.Json.JsonDocument.Parse (File.ReadAllText path)
        [ for step in doc.RootElement.GetProperty("steps").EnumerateArray () do
            let expect = step.GetProperty "expect"
            let v (name: string) = expect.GetProperty(name).GetInt32 ()
            yield { Desc = step.GetProperty("desc").GetString ()
                    Inst = step.GetProperty("pins").GetProperty("inst").GetInt32 ()
                    A = v "a"; B = v "b"; Pc = v "pc"; Flags = v "flags" } ]

    /// 最大到達時間が下限 (ピン → 最遠のクロック端子のマンハッタン距離) の何倍まで許すか。
    /// 最短経路木 (PipelineWL.MinLatency) なら遠回りは障害物を避ける分だけ
    let private maxLatencyRatio = 1.1

    /// 2 相のクロック木: 各クロックの最大到達時間が下限に近いこと (最短経路木配線)、
    /// ピンが端子群の L1 ミニマックス中心の近くにあること (下限が理想の下限に近い)。
    let private latencyTests (c: WlCompiled) : (string * bool) list =
        match clockPinsOf c with
        | Some (ClockDrive.TwoPhasePins (a, b)) ->
            let dg = toDense c.Grid
            [ for (label, pin) in [ "clk_a", a; "clk_b", b ] do
                match indexOf dg pin |> Option.bind (fun i -> clockLatency dg i (analyzeClock dg i)) with
                | None -> yield sprintf "WL-2PH: sm83_min %s reaches DFFs" label, false
                | Some l ->
                    printfn "  WL_2PH_LATENCY: %s max %d / bound %d (ratio %.3f) / ideal-pin bound %d"
                        label l.MaxArrival l.LowerBound (latencyRatio l) l.IdealPinBound
                    yield sprintf "WL-2PH: sm83_min %s max clock arrival %d within %.1fx of bound %d"
                              label l.MaxArrival maxLatencyRatio l.LowerBound,
                          latencyRatio l <= maxLatencyRatio
                    // ピンは格子の隙間にスナップするので、理想の下限から 1 ピッチ分 (X+Y、大きめの 24x16 で見込む) まで許す
                    let pitchSlack = 24 + 16
                    yield sprintf "WL-2PH: sm83_min %s pin near L1 minimax center (bound %d, ideal %d)"
                              label l.LowerBound l.IdealPinBound,
                          l.LowerBound <= l.IdealPinBound + pitchSlack ]
        | _ -> [ "WL-2PH: sm83_min has clk_a/clk_b pins (latency)", false ]

    /// NetlistSimTest.sm83MinTest / wgpu-runner --program と同じ手順を 2 相で行う:
    ///   初期化: rst=1 で settleLow → rst=0 で settleLow
    ///   各命令: inst を書き settleLow → clockEdge (clk_a → clk_b) → レジスタ読出
    let private sm83MinTests () : (string * bool) list =
        let path = verilogPath "sm83_min"
        let programPath = Path.Combine (__SOURCE_DIRECTORY__, "..", "web", "sm83_min_program.json")
        if not (File.Exists path && File.Exists programPath) then
            [ "WL-2PH: sm83_min.json and web/sm83_min_program.json present", false ]
        else
            let json = File.ReadAllText path
            let opts = { defaultCompileOptions with Placement = annealed; Clocking = TwoPhase }
            match compileWLWithOptions opts json, parseYosysPorts json with
            | Error e, _ -> [ sprintf "WL-2PH: sm83_min compiles two-phase + anneal (%A)" e, false ]
            | _, Error e -> [ sprintf "WL-2PH: sm83_min ports (%s)" (describeError e), false ]
            | Ok c, Ok ports ->
                let port name = ports |> List.find (fun p -> p.Name = name)
                let driver = c.Placed |> List.map (fun p -> p.Gate.Output, p.Coord) |> Map.ofList
                let readPort (name: string) (g: LGrid) =
                    (port name).Bits
                    |> List.mapi (fun i bit ->
                        let v =
                            match bit with
                            | ConstBit b -> b
                            | NetBit net -> levelOf g driver.[net]
                        if v then 1 <<< i else 0)
                    |> List.sum
                let writePort (name: string) (value: int) (g: LGrid) =
                    (port name).Bits
                    |> List.indexed
                    |> List.fold (fun acc (i, bit) ->
                        match bit with
                        | NetBit net -> setPin c.Pins.[net] ((value >>> i) &&& 1 = 1) acc
                        | ConstBit _ -> acc) g
                let steps = loadProgram programPath
                match clockPinsOf c with
                | Some (ClockDrive.TwoPhasePins _ as clocks) ->
                    let low g = ClockDrive.settleLow settler settleLimit clocks g
                    let afterReset =
                        low (writePort "rst" 1 c.Grid) |> Result.bind (writePort "rst" 0 >> low)
                    let run (state: Result<LGrid, ClockDrive.DriveError>, passed: int) (step: Sm83Step) =
                        match state with
                        | Error e -> Error e, passed
                        | Ok g ->
                            // クロックには触れず inst の伝播だけ収束させる (2 周期目以降は
                            // clockEdge が前周期の clkB=1 の立ち下がりも兼ねる)
                            let next =
                                ClockDrive.settleData settler settleLimit "inst" (writePort "inst" step.Inst g)
                                |> Result.bind (ClockDrive.clockEdge settler settleLimit clocks)
                            match next with
                            | Error e -> Error e, passed
                            | Ok g' ->
                                let got = readPort "a_out" g', readPort "b_out" g', readPort "pc_out" g', readPort "flags_out" g'
                                let ok = got = (step.A, step.B, step.Pc, step.Flags)
                                if not ok then
                                    printfn "  WL_2PH_SM83MIN: %s expected a=%d b=%d pc=%d flags=0x%X, got %A"
                                        step.Desc step.A step.B step.Pc step.Flags got
                                Ok g', (if ok then passed + 1 else passed)
                    let final, passed = steps |> List.fold run (afterReset, 0)
                    (match final with
                     | Error e -> printfn "  WL_2PH_SM83MIN: %s" (ClockDrive.describeDriveError e)
                     | Ok _ -> ())
                    let dffCount = c.Placed |> List.filter (fun p -> p.Gate.Kind = Dff) |> List.length
                    [ yield! latencyTests c
                      sprintf "WL-2PH: sm83_min two-phase + anneal compiles (%d gates, %d DFF)" c.Placed.Length dffCount,
                      c.Placed.Length = 380 + 26 && dffCount = 2 * 26
                      "WL-2PH: sm83_min two-phase grid keeps the two-phase invariant", invariantHolds c
                      sprintf "WL-2PH: sm83_min two-phase CA matches GPU-verified expectations (%d/%d instructions)"
                          passed steps.Length,
                      passed = steps.Length && steps.Length = 20 ]
                | _ -> [ "WL-2PH: sm83_min has clk_a/clk_b pins", false ]

    // --- meta (formatVersion 2) ------------------------------------------------------

    let private fixedProvenance : Provenance =
        { GitCommit = "test"
          CreatedAtUtc = DateTimeOffset (2026, 9, 27, 0, 0, 0, TimeSpan.Zero) }

    let private metaTests () : (string * bool) list =
        let path = verilogPath "counter4"
        if not (File.Exists path) then [ "WL-2PH: counter4.json present", false ] else
        let sourceBytes = File.ReadAllBytes path
        let json = Text.Encoding.UTF8.GetString sourceBytes
        let compiled = compileWLWithOptions { defaultCompileOptions with Clocking = TwoPhase } json
        match compiled, parseYosysPorts json with
        | Error e, _ -> [ sprintf "WL-2PH: counter4 two-phase compiles (%A)" e, false ]
        | _, Error e -> [ sprintf "WL-2PH: counter4 ports (%s)" (describeError e), false ]
        | Ok c, Ok ports ->
            match buildMetaOfCompiled "counter4" (sourceSha256 sourceBytes) fixedProvenance ports c with
            | Error e -> [ sprintf "WL-2PH: two-phase buildMeta (%s)" (describeError e), false ]
            | Ok meta ->
                let grid = importGrid (exportGrid c.Grid)
                let roundTrip = metaOfJson "test" (metaToJson meta)
                let validated = validateAgainstGrid grid meta |> Result.isOk
                let clkDropped = not (Map.containsKey "clk" meta.Inputs)
                // 読み込んだ meta の clkA / clkB と出力 probe だけで 2 相駆動してカウントできること
                let countsFromMeta =
                    match meta.Clocking, Map.tryFind "q" meta.Outputs with
                    | TwoPhaseClocking (_, a, b), Some probes ->
                        let cells = probes |> List.choose (function CellProbe p -> Some p | _ -> None)
                        let value g = cells |> List.mapi (fun i p -> if levelOf g p then 1 <<< i else 0) |> List.sum
                        let clocks = ClockDrive.TwoPhasePins (a, b)
                        // cycle は「直前が settleLow か cycle」を前提とするので、
                        // インポート直後の生グリッドからはまず settleLow で始める
                        ClockDrive.settleLow settler 2000 clocks grid
                        |> Result.map (fun g -> g, [])
                        |> fun initial ->
                            [ 1 .. 3 ]
                            |> List.fold (fun acc _ ->
                                acc |> Result.bind (fun (g, values) ->
                                    ClockDrive.cycle settler 2000 clocks g |> Result.map (fun g' -> g', values @ [ value g' ])))
                                initial
                        |> Result.map snd = Ok [ 1; 2; 3 ]
                    | _ -> false
                // 旧形式 (formatVersion 1、clocking なし) は単相として読む
                let legacyJson =
                    (metaToJson { meta with Clocking = SingleEdgeClocking })
                        .Replace("\"formatVersion\": 2", "\"formatVersion\": 1")
                let legacy =
                    match metaOfJson "legacy" legacyJson with
                    | Ok m -> m.Clocking = SingleEdgeClocking && m.FormatVersion = LegacyFormatVersion
                    | Error _ -> false
                let unknownVersion =
                    match metaOfJson "v9" (legacyJson.Replace ("\"formatVersion\": 1", "\"formatVersion\": 9")) with
                    | Error (FormatVersionMismatch (CurrentFormatVersion, 9)) -> true
                    | _ -> false
                let existingSubset =
                    let subsetMeta = Path.Combine (__SOURCE_DIRECTORY__, "..", "routed", "sm83_subset.meta.json")
                    File.Exists subsetMeta
                    && (match metaOfJson subsetMeta (File.ReadAllText subsetMeta) with
                        | Ok m -> m.Clocking = SingleEdgeClocking
                        | Error _ -> false)
                [ "WL-2PH: two-phase meta has clocking twoPhase and no clk input",
                  clkDropped && (match meta.Clocking with TwoPhaseClocking ("clk", _, _) -> true | _ -> false)
                  "WL-2PH: two-phase meta survives JSON round trip", roundTrip = Ok meta
                  "WL-2PH: two-phase meta validates against grid (clkA/clkB are pins)", validated
                  "WL-2PH: counter4 counts 1,2,3 driven only by meta clkA/clkB", countsFromMeta
                  "WL-2PH: formatVersion 1 meta (no clocking) reads as single-edge", legacy
                  "WL-2PH: unknown formatVersion is rejected", unknownVersion
                  "WL-2PH: existing routed/sm83_subset.meta.json still loads as single-edge", existingSubset ]

    let runAll () : (string * bool) list =
        unitTests ()
        @ errorTests ()
        @ incrementalTests ()
        @ logicTests ()
        @ holdContrastTests ()
        @ sm83MinTests ()
        @ metaTests ()
