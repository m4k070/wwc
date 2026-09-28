namespace WwHdl

// ---------------------------------------------------------------------
// TIMING: TimingAnalysis (クリティカルパス静的解析) のテスト
//
//   * counter4 / reg8 を 2 相 (rowmajor / anneal) でコンパイルし、正規化した grid+meta
//     (AnalyzeHold.fsx --compile と同じ往復: importGrid (exportGrid c.Grid)) で
//     3 窓 (data_in / phaseA / phaseB) の予測最大世代数を求める
//   * 複数のデータパターンで数周期実際に駆動し (settleIncremental)、各窓の実測世代数の
//     最大と比べる: 静的解析は「最悪の入力で全経路が変化した場合」の上界のはずなので
//     実測 ≤ 予測 が常に成り立つこと (破れたら見積りのどこかに誤りがある)
//   * 予測が実測から極端に乖離していない (見積りロジックの暴走を検出する緩いガード)
//   * buildTopoOrder が閉路なしで成功すること (組合せ部分に閉路があってはならない)
//
// reg8 は Q 同士の組合せ経路が無い (D は外部入力 "d" のみ) ので phaseB の予測・実測は
// どちらも 0 になるはず — これも「安全側に倒れすぎていない」ことの確認になる。
// ---------------------------------------------------------------------
module TimingAnalysisTest =
    open System
    open System.IO
    open Domain
    open WireLevel
    open Clocking
    open PipelineWL
    open RoutedArtifact
    open HoldAnalysis
    open TimingAnalysis

    let private verilogPath (name: string) =
        Path.Combine (__SOURCE_DIRECTORY__, "..", "verilog", name + ".json")

    let private provenance : Provenance =
        { GitCommit = "test"; CreatedAtUtc = DateTimeOffset (2026, 9, 28, 0, 0, 0, TimeSpan.Zero) }

    [<Literal>]
    let private SettleLimit = 20000

    /// anneal の手数 (WL-2PH の小回路テストと同じ。小回路なので十分最適化される)。
    [<Literal>]
    let private AnnealMoves = 100_000

    let private annealed = GatePlacement.Annealed { GatePlacement.defaultAnnealConfig with Moves = AnnealMoves }

    let private settleG (g: LGrid) : LGrid * int = settleIncremental SettleLimit g

    /// data 入力に書き込むストレスパターン (全 0 / 全 1 / 市松 / 端のビットなど)。
    /// 静的解析は論理的マスキングを無視する安全側の見積りなので、これで実測の
    /// 最悪ケースに厳密に一致しなくてもよい (下回っていれば十分)。
    let private stressPatterns = [ 0; 0xFF; 0x55; 0xAA; 0x0F; 0xF0; 0x01; 0xFE ]

    /// 予測が実測から極端に乖離していないか (見積りロジックの暴走を検出する緩いガード)。
    let private notWildlyOverestimated (predicted: int) (actual: int) : bool =
        predicted <= actual * 3 + 50

    let private writeBus (coords: Coord list) (value: int) (g: LGrid) : LGrid =
        coords |> List.indexed |> List.fold (fun acc (i, c) -> setPin c ((value >>> i) &&& 1 = 1) acc) g

    /// counter4 / reg8 を 2 相 + 指定配置でコンパイルし、TimingAnalysis の予測と
    /// 実際の CA 駆動 (settleIncremental) の実測を突き合わせる。
    let private checkCircuit (circuit: string) (placeLabel: string) (placement: GatePlacement.PlacementStrategy) (dataPort: string option) : (string * bool) list =
        let path = verilogPath circuit
        if not (File.Exists path) then [ sprintf "TIMING: %s.json present" circuit, false ] else
        let json = File.ReadAllText path
        let opts = { defaultCompileOptions with Placement = placement; Clocking = TwoPhase }
        match compileWLWithOptions opts json, parseYosysPorts json with
        | Error e, _ -> [ sprintf "TIMING: %s %s compiles two-phase (%A)" circuit placeLabel e, false ]
        | _, Error e -> [ sprintf "TIMING: %s ports (%s)" circuit (describeError e), false ]
        | Ok c, Ok ports ->
            match buildMetaOfCompiled circuit (sourceSha256 (File.ReadAllBytes path)) provenance ports c with
            | Error e -> [ sprintf "TIMING: %s buildMeta (%s)" circuit (describeError e), false ]
            | Ok meta ->
                // meta の座標は正規化座標なので、grid も .bin と同じ往復で正規化する
                // (AnalyzeHold.fsx --compile と同じ手順)。
                let grid = importGrid (exportGrid c.Grid)
                let dg = toDense grid
                match meta.Clocking with
                | SingleEdgeClocking -> [ sprintf "TIMING: %s %s compiled two-phase" circuit placeLabel, false ]
                | TwoPhaseClocking (_, clkA, clkB) ->
                    match indexOf dg clkA, indexOf dg clkB with
                    | None, _ | _, None -> [ sprintf "TIMING: %s %s clkA/clkB pins in grid" circuit placeLabel, false ]
                    | Some ai, Some bi ->
                        match buildTopoOrder dg with
                        | Error e ->
                            [ sprintf "TIMING: %s %s combinational graph is acyclic (%s)" circuit placeLabel (describeTimingError e), false ]
                        | Ok _ ->
                            match analyzeTwoPhaseTiming dg meta ai bi with
                            | Error e ->
                                [ sprintf "TIMING: %s %s analyzeTwoPhaseTiming (%s)" circuit placeLabel (describeTimingError e), false ]
                            | Ok reports ->
                                let predicted = reports |> List.map (fun r -> r.Window, r.PredictedMax) |> Map.ofList
                                let dataCoords = dataPort |> Option.bind (fun p -> Map.tryFind p meta.Inputs)
                                let g0, _ = settleG (grid |> setPin clkA false |> setPin clkB false)
                                let cycle (g: LGrid, _) (v: int) =
                                    let gd = match dataCoords with Some cs -> writeBus cs v g | None -> g
                                    let afterData, dGens = settleG gd
                                    let afterA, aGens = settleG (afterData |> setPin clkA true |> setPin clkB false)
                                    let afterB, bGens = settleG (afterA |> setPin clkA false |> setPin clkB true)
                                    afterB, (dGens, aGens, bGens)
                                let results = stressPatterns |> List.scan cycle (g0, (0, 0, 0)) |> List.tail |> List.map snd
                                let actualMax (select: int * int * int -> int) = results |> List.map select |> List.max
                                let actual =
                                    [ DataInWindow, actualMax (fun (d, _, _) -> d)
                                      PhaseAWindow, actualMax (fun (_, a, _) -> a)
                                      PhaseBWindow, actualMax (fun (_, _, b) -> b) ]
                                    |> Map.ofList
                                [ for w in [ DataInWindow; PhaseAWindow; PhaseBWindow ] do
                                    let p = predicted.[w]
                                    let a = actual.[w]
                                    printfn "  TIMING: %s %s %s predicted=%d actual=%d" circuit placeLabel (windowLabel w) p a
                                    yield sprintf "TIMING: %s %s %s actual (%d) <= predicted (%d)" circuit placeLabel (windowLabel w) a p, a <= p
                                    yield sprintf "TIMING: %s %s %s predicted (%d) not wildly over actual (%d)" circuit placeLabel (windowLabel w) p a,
                                          notWildlyOverestimated p a ]

    let runAll () : (string * bool) list =
        [ yield! checkCircuit "counter4" "rowmajor" GatePlacement.RowMajor None
          yield! checkCircuit "counter4" "anneal" annealed None
          yield! checkCircuit "reg8" "rowmajor" GatePlacement.RowMajor (Some "d")
          yield! checkCircuit "reg8" "anneal" annealed (Some "d") ]
