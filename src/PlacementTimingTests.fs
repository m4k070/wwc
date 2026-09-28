namespace WwHdl

// ---------------------------------------------------------------------
// PLACE-TIMING: PlacementTiming (配置段階の静的タイミング解析・タイミング駆動配置) のテスト
//
//   * 手で作った小さな 2 相ネットリストで、3 窓の最長経路・アークごとの経路長・
//     クリティカリティ・重みを手計算と照合する
//   * TimingNetwork のアーク列が GatePlacement.buildArcs と 1 対 1 に並ぶ (重みをそのまま渡せる)
//   * 重み付きアニーリングの増分評価コスト = 全再計算のコスト
//   * 既定の anneal が変わっていない (変更前に記録した結果と一致)
//   * 同じシードのタイミング駆動配置は同じ結果になり、選ばれる解は初期解より悪くならない
//   * 単相の回路にはタイミング駆動配置を適用せず Error を返す
// ---------------------------------------------------------------------
module PlacementTimingTest =
    open Domain
    open Netlist
    open GatePlacement
    open PlacementTiming

    let private verilogPath (name: string) =
        System.IO.Path.Combine (__SOURCE_DIRECTORY__, "..", "verilog", name + ".json")

    // --- 1. 手計算との照合 --------------------------------------------------------
    //
    // 元の回路: clk, a が外部入力。
    //   g0 = NAND(a, q)  g1 = NOT(g0)  DFF(clk, g1) → q  gx = NOT(a) → 外部出力 (probe)
    // 2 相化後のゲート番号: 0 = NAND, 1 = NOT, 2 = マスター, 3 = スレーブ, 4 = gx
    // 座標 (マンハッタン距離を手で数えやすい値):
    //   g0 (10,0)  g1 (20,5)  マスター (30,5)  スレーブ (30,25)  gx (5,0)
    //   a (0,0)  clk_a (30,0)  clk_b (0,25)
    // アーク (buildArcs と同じ並び):
    //   #0 a→g0 = 10   #1 スレーブ→g0 = 20+25 = 45   #2 g0→g1 = 10+5 = 15
    //   #3 g1→マスター = 10   #4 マスター→スレーブ = 20   #5 a→gx = 5
    // クロック到達: マスター |30-30|+|0-5| = 5、スレーブ |0-30|+0 = 30。ClockToQ = 1
    //   data_in: g0 = 10, g1 = 25, マスター D = 35, gx (probe) = 5 → 最大 35
    //            アーク経路長 #0 #2 #3 = 35、#5 = 5、#1 #4 = -1 (この窓では変化しない)
    //   phaseA : マスター Q = 6, スレーブ D = 26、クロック下限 6 → 最大 26。#4 = 26
    //   phaseB : スレーブ Q = 31, g0 = 76, g1 = 91, マスター D = 101、下限 31 → 最大 101
    //            #1 #2 #3 = 101
    //   クリティカリティ: #5 = 5/35、他は 1

    let private handNetlist : Netlist =
        let n = NetId
        { Gates =
            [ { Id = 0; Kind = Nand; Inputs = [ n 2; n 10 ]; Output = n 3 }
              { Id = 1; Kind = Not; Inputs = [ n 3 ]; Output = n 4 }
              { Id = 2; Kind = Dff; Inputs = [ n 1; n 4 ]; Output = n 10 }
              { Id = 3; Kind = Not; Inputs = [ n 2 ]; Output = n 6 } ]
          PrimaryInputs = [ n 1; n 2 ]
          PrimaryOutputs = [ n 6 ]
          ClockNet = Some (n 1) }

    let private handCoords : Coord[] =
        [| { X = 10; Y = 0 }; { X = 20; Y = 5 }; { X = 30; Y = 5 }; { X = 30; Y = 25 }; { X = 5; Y = 0 } |]

    let private windowOf (w: Window) (ws: WindowTiming list) : WindowTiming option =
        ws |> List.tryFind (fun x -> x.Window = w)

    let private handTests () : (string * bool) list =
        match Clocking.toTwoPhase handNetlist with
        | Error e -> [ sprintf "PLACE-TIMING: hand netlist two-phase (%s)" (Clocking.describeTwoPhaseError e), false ]
        | Ok tp ->
            match buildNetwork tp with
            | Error e -> [ sprintf "PLACE-TIMING: hand network (%s)" (describePlacementTimingError e), false ]
            | Ok net ->
                let pins =
                    Map.ofList [ NetId 2, { X = 0; Y = 0 }; tp.ClockA, { X = 30; Y = 0 }; tp.ClockB, { X = 0; Y = 25 } ]
                let rolesOk = net.Roles = [| Logic; Logic; Master; Slave; Logic |]
                let expectedArcs =
                    [| FromInputPin (NetId 2), 0; FromGateOutput 3, 0; FromGateOutput 0, 1
                       FromGateOutput 1, 2; FromGateOutput 2, 3; FromInputPin (NetId 2), 4 |]
                let arcsOk = (net.Arcs |> Array.map (fun a -> a.Source, a.Sink)) = expectedArcs
                let clockNetOfGate (g: int) = if net.Roles.[g] = Master then tp.ClockA else tp.ClockB
                match analyze defaultTimingModel net (fun g -> handCoords.[g]) pins clockNetOfGate with
                | Error e -> [ sprintf "PLACE-TIMING: hand analyze (%s)" (describePlacementTimingError e), false ]
                | Ok ws ->
                    let check (w: Window) (predicted: int) (floor: int) (lengths: int[]) (endpoints: (int * int) list) =
                        match windowOf w ws with
                        | None -> false
                        | Some x ->
                            x.PredictedMax = predicted && x.ClockFloor = floor
                            && x.ArcPathLength = lengths && x.EndpointArrival = Map.ofList endpoints
                    let crit = criticalities net.Arcs.Length ws
                    let expectedCrit = [| 1.0; 1.0; 1.0; 1.0; 1.0; 5.0 / 35.0 |]
                    let critOk = Array.forall2 (fun (a: float) b -> abs (a - b) < 1e-12) crit expectedCrit
                    let cfg = { defaultTimingDrivenConfig with Alpha = 10.0; Beta = 1.0; MasterSlaveWeight = 2.0 }
                    let weights = arcWeights cfg net crit
                    // #4 はマスター→スレーブ: 2 + 10·1 = 12。#5: 1 + 10·(5/35) = 2.428571… → 2429
                    let weightsOk = weights = [| 11000L; 11000L; 11000L; 11000L; 12000L; 2429L |]
                    let smoothed = smoothCriticalities 0.75 (Some [| 1.0; 0.0 |]) [| 0.0; 1.0 |]
                    [ "PLACE-TIMING: hand roles (Logic/Master/Slave)", rolesOk
                      "PLACE-TIMING: hand arcs in buildArcs order", arcsOk
                      "PLACE-TIMING: hand data_in = 35 (arcs 35/-1/35/35/-1/5)",
                        check Window.DataIn 35 0 [| 35; -1; 35; 35; -1; 5 |] [ 2, 35; 4, 5 ]
                      "PLACE-TIMING: hand phaseA = 26 (floor 6)",
                        check Window.PhaseA 26 6 [| -1; -1; -1; -1; 26; -1 |] [ 3, 26 ]
                      "PLACE-TIMING: hand phaseB = 101 (floor 31)",
                        check Window.PhaseB 101 31 [| -1; 101; 101; 101; -1; -1 |] [ 2, 101 ]
                      "PLACE-TIMING: hand summary cycle = 35+26+101", (summarize ws).Cycle = 162
                      "PLACE-TIMING: hand criticalities (L/D, max over windows)", critOk
                      "PLACE-TIMING: hand weights (base + α·c^β, master→slave base 2)", weightsOk
                      "PLACE-TIMING: criticality memory (EMA)", smoothed = [| 0.75; 0.25 |] ]

    // --- 2. 中規模回路 (sm83_min、配線しない) ------------------------------------

    let private unitCircuit = "sm83_min"
    let private unitPitchX, unitPitchY = 20, 14

    /// 既定 anneal の回帰検出用に、変更前 (1534a1c) の anneal で記録した結果。
    /// sm83_min (単相)、seed 7、200k 手、BackwardPenalty 0.5、ピッチ 20x14、余剰 2 行。
    [<Literal>]
    let private GoldenBestCost = 31512000L

    [<Literal>]
    let private GoldenAssignmentHash = 12786781593965121634UL

    let private goldenConfig : AnnealConfig =
        { defaultAnnealConfig with Moves = 200_000; Seed = 7UL; BackwardPenalty = 0.5 }

    /// FNV-1a 風の割り当てハッシュ (記録値との比較用)。
    let private assignmentHash (a: Assignment) : uint64 =
        a |> Array.fold (fun (acc: uint64) s -> (acc ^^^ uint64 s) * 1099511628211UL) 14695981039346656037UL

    /// 増分評価の検証: 高温でほぼ全部受理させる。
    let private incrementalMoves = 2_000

    /// タイミング駆動の単体テストの手数 (小回路なので少なくてよい)。
    let private unitTimingConfig : TimingDrivenConfig =
        { defaultTimingDrivenConfig with Rounds = 3; MovesPerRound = 50_000 }

    let private unitAnnealConfig : AnnealConfig =
        { defaultAnnealConfig with Moves = 100_000; Seed = 3UL }

    let private goldenTests (nl: Netlist) : (string * bool) list =
        let n = nl.Gates.Length
        let square = squareSlotGrid n { X = 12; Y = 2 } unitPitchX unitPitchY
        let grid = { square with Rows = square.Rows + 2 }
        let pins =
            nl.PrimaryInputs |> List.mapi (fun i net -> net, { X = 0; Y = 2 + i * unitPitchY }) |> Map.ofList
        let arcs = buildArcs nl pins
        match anneal goldenConfig grid n arcs (rowMajorAssignment n) with
        | Error e -> [ sprintf "PLACE-TIMING: golden anneal runs (%s)" (describeConfigError e), false ]
        | Ok o ->
            [ "PLACE-TIMING: default anneal unchanged (best cost = recorded)", o.BestCost = GoldenBestCost
              "PLACE-TIMING: default anneal unchanged (assignment hash = recorded)", assignmentHash o.Best = GoldenAssignmentHash ]

    let private twoPhaseTests (nl: Netlist) : (string * bool) list =
        match PipelineWL.prepareCircuit Clocking.TwoPhase nl with
        | Error e -> [ sprintf "PLACE-TIMING: %s two-phase (%A)" unitCircuit e, false ]
        | Ok circuit ->
        match circuit.Clocking with
        | PipelineWL.SingleEdgeClock _ -> [ sprintf "PLACE-TIMING: %s two-phase clocking" unitCircuit, false ]
        | PipelineWL.TwoPhaseClock tp ->
        match buildNetwork tp with
        | Error e -> [ sprintf "PLACE-TIMING: %s network (%s)" unitCircuit (describePlacementTimingError e), false ]
        | Ok net ->
            let grid = PipelineWL.slotGridOf unitPitchX unitPitchY circuit
            let n = circuit.Netlist.Gates.Length
            let resolvePins (a: Assignment) =
                let placed, pins, _ = PipelineWL.placeCircuitFromAssignment grid circuit a 1
                let placedArr = List.toArray placed
                pins, (fun (g: int) -> List.head placedArr.[g].Gate.Inputs)
            let initial = rowMajorAssignment n
            // (a) アーク列が annealArcsOf (buildArcs) と同じ並び
            let arcsMatch =
                // annealArcsOf は左端ピン、こちらはミニマックス中心のピンなので固定端子の座標は
                // 比べない (並び・受け手・駆動元ゲート・固定端子かどうかを比べる)
                match toPlacementArcs net (fst (resolvePins initial)) with
                | Error _ -> false
                | Ok timingArcs ->
                    let pinsOfArcs = PipelineWL.annealArcsOf unitPitchY circuit
                    timingArcs.Length = pinsOfArcs.Length
                    && Array.forall2 (fun (x: Arc) (y: Arc) ->
                        x.Sink = y.Sink
                        && (match x.Source, y.Source with
                            | FromGate a, FromGate b -> a = b
                            | FromFixed _, FromFixed _ -> true
                            | _ -> false)) timingArcs pinsOfArcs
            // (b) 重み付きの増分評価 = 全再計算
            let incremental =
                match toPlacementArcs net (fst (resolvePins initial)) with
                | Error e -> Error (describePlacementTimingError e)
                | Ok arcs ->
                    // 決定的な擬似乱数の重み (1.0〜8.999)
                    let weights = Array.init arcs.Length (fun i -> WeightScale + int64 ((i * 7919) % 8000))
                    let hot = { defaultAnnealConfig with Moves = incrementalMoves; Seed = 11UL; InitialTemperature = 1000.0; FinalTemperature = 500.0; BackwardPenalty = 0.25 }
                    annealWeighted hot grid n arcs (Weighted weights) initial
                    |> Result.mapError describeConfigError
                    |> Result.map (fun o ->
                        o.AcceptedMoves > incrementalMoves / 2,
                        o.LastCostIncremental = totalWeightedCost grid arcs 0.25 (Weighted weights) o.Last,
                        o.BestCost = totalWeightedCost grid arcs 0.25 (Weighted weights) o.Best)
            // (c) 同じシードで同じ結果、初期解より悪くならない
            let run () = timingDrivenAnneal defaultTimingModel unitAnnealConfig unitTimingConfig grid net resolvePins initial
            let determinism =
                match run (), run () with
                | Ok a, Ok b -> Ok (a, b)
                | Error e, _ | _, Error e -> Error (describePlacementTimingError e)
            let weightErrors =
                match toPlacementArcs net (fst (resolvePins initial)) with
                | Error _ -> false
                | Ok arcs ->
                    match annealWeighted unitAnnealConfig grid n arcs (Weighted [| 1L |]) initial with
                    | Error (WeightCountMismatch _) -> true
                    | _ -> false
            [ "PLACE-TIMING: network arcs align with annealArcsOf (buildArcs)", arcsMatch
              (match incremental with
               | Ok (enough, _, _) -> sprintf "PLACE-TIMING: weighted anneal accepted > %d moves" (incrementalMoves / 2), enough
               | Error e -> sprintf "PLACE-TIMING: weighted anneal runs (%s)" e, false)
              (match incremental with
               | Ok (_, lastOk, _) -> "PLACE-TIMING: weighted incremental cost = full recompute (last)", lastOk
               | Error _ -> "PLACE-TIMING: weighted incremental cost = full recompute (last)", false)
              (match incremental with
               | Ok (_, _, bestOk) -> "PLACE-TIMING: weighted best cost = full recompute", bestOk
               | Error _ -> "PLACE-TIMING: weighted best cost = full recompute", false)
              "PLACE-TIMING: weight count mismatch is an error", weightErrors
              (match determinism with
               | Ok (a, b) -> "PLACE-TIMING: timing-driven is deterministic (same seed → same assignment)", a.Best = b.Best && a.Rounds = b.Rounds
               | Error e -> sprintf "PLACE-TIMING: timing-driven runs (%s)" e, false)
              (match determinism with
               | Ok (a, _) ->
                    let r0 = a.Rounds |> List.head
                    let chosen = a.Rounds |> List.find (fun r -> r.Round = a.BestRound)
                    "PLACE-TIMING: chosen round cycle <= initial (round 0)",
                    r0.Round = 0 && a.Rounds.Length = unitTimingConfig.Rounds + 1 && chosen.Summary.Cycle <= r0.Summary.Cycle
               | Error _ -> "PLACE-TIMING: chosen round cycle <= initial (round 0)", false) ]

    let private singleEdgeRejected (nl: Netlist) : (string * bool) list =
        let strategy = TimingDriven ({ unitAnnealConfig with Moves = 1_000 }, { unitTimingConfig with Rounds = 1; MovesPerRound = 1_000 })
        match PipelineWL.placeWLWithStrategy strategy unitPitchX unitPitchY nl with
        | Error (Route.InvalidPlacementConfig _) -> [ "PLACE-TIMING: single-edge rejects timing-driven", true ]
        | Error e -> [ sprintf "PLACE-TIMING: single-edge rejects timing-driven (unexpected %A)" e, false ]
        | Ok _ -> [ "PLACE-TIMING: single-edge rejects timing-driven", false ]

    let runAll () : (string * bool) list =
        let path = verilogPath unitCircuit
        let unit =
            if not (System.IO.File.Exists path) then [ sprintf "PLACE-TIMING: %s.json present" unitCircuit, false ]
            else
                match Pipeline.frontend (System.IO.File.ReadAllText path) with
                | Error e -> [ sprintf "PLACE-TIMING: %s frontend (%A)" unitCircuit e, false ]
                | Ok nl -> goldenTests nl @ twoPhaseTests nl @ singleEdgeRejected nl
        handTests () @ unit
