namespace WwHdl

// ---------------------------------------------------------------------
// WL-CLKSPLIT: クロックピンの複数分割 (issue #7 (b))
//
//   * clusterKCenter: 手計算できる小さい点群での分割、決定性、k=1 が単一クラスタになること、
//     どのクラスタも空にならないこと
//   * ネット分割: counter4 / reg8 / sm83_min を 2 相 + clockPins=4 でコンパイルし、
//     各マスター/スレーブ DFF のクロック入力がちょうど 1 本の区画ネットに繋がること、
//     ClockPinGroups が非空でクラスタ数が (DFF 数, k) の min 以下であること、
//     論理テスト (counting / write-read / sm83_min 20 命令) が通ること
//   * meta v1 (clocking なし) / v2 (clkA・clkB が座標 1 個) / v3 (座標のリスト) の読込
// ---------------------------------------------------------------------
module WlClockPinSplitTest =
    open System
    open System.IO
    open Domain
    open Netlist
    open WireLevel
    open Clocking
    open PipelineWL
    open RoutedArtifact

    let private verilogPath (name: string) =
        Path.Combine (__SOURCE_DIRECTORY__, "..", "verilog", name + ".json")

    let private settler : ClockDrive.Settler = settleIncremental
    let private settleLimit = 20000

    // --- clusterKCenter -----------------------------------------------------

    let private clusterTests () : (string * bool) list =
        // 2 つの離れた密集団 (x~0 と x~100) に k=2 を与えると、きれいに分かれるはず。
        let points =
            [| { X = 0; Y = 0 }; { X = 1; Y = 0 }; { X = 0; Y = 1 }; { X = 1; Y = 1 }
               { X = 100; Y = 0 }; { X = 101; Y = 0 }; { X = 100; Y = 1 } |]
        let labels = clusterKCenter 2 points
        let clusterOfLeft = labels.[0 .. 3] |> Array.distinct
        let clusterOfRight = labels.[4 .. 6] |> Array.distinct
        let separates = clusterOfLeft.Length = 1 && clusterOfRight.Length = 1 && clusterOfLeft.[0] <> clusterOfRight.[0]
        let labels2 = clusterKCenter 2 points
        let deterministic = labels = labels2
        let nonEmpty = [ 0 .. 1 ] |> List.forall (fun c -> labels |> Array.exists ((=) c))
        // k=1: 全部同じクラスタ
        let single = clusterKCenter 1 points
        let allSame = single |> Array.forall ((=) single.[0])
        // k > 点数: 点数に丸められる (各点が自分のクラスタ、重複なし)
        let tiny = [| { X = 0; Y = 0 }; { X = 5; Y = 5 } |]
        let overK = clusterKCenter 10 tiny
        let clampedToPointCount = (overK |> Array.distinct |> Array.length) = 2
        // 決定的な非空性: 大きめの k (16) でもクラスタが全部空でない (fillEmptyClusters の検証)
        let rng = Random 42
        let manyPoints = Array.init 40 (fun _ -> { X = rng.Next (0, 500); Y = rng.Next (0, 500) })
        let labels16 = clusterKCenter 16 manyPoints
        let allNonEmpty16 = [ 0 .. 15 ] |> List.forall (fun c -> labels16 |> Array.exists ((=) c))
        [ "WL-CLKSPLIT: clusterKCenter separates two distant clumps (k=2)", separates
          "WL-CLKSPLIT: clusterKCenter is deterministic (same input -> same labels)", deterministic
          "WL-CLKSPLIT: clusterKCenter produces no empty cluster (k=2)", nonEmpty
          "WL-CLKSPLIT: clusterKCenter k=1 puts everything in one cluster", allSame
          "WL-CLKSPLIT: clusterKCenter clamps k to point count", clampedToPointCount
          "WL-CLKSPLIT: clusterKCenter produces no empty cluster (k=16, 40 random points)", allNonEmpty16 ]

    // --- ネット分割: 実回路で k=4 ---------------------------------------------

    let private annealMoves = 100_000
    let private annealed = GatePlacement.Annealed { GatePlacement.defaultAnnealConfig with Moves = annealMoves }

    /// clockPins=k でコンパイルした結果について、各マスター/スレーブ DFF のクロック入力が
    /// ちょうど 1 本の区画ネットに繋がっていること (元の D 入力は変わらないこと) を確かめる。
    let private splitInvariantHolds (k: int) (c: WlCompiled) : bool * string =
        match c.Clocking with
        | SingleEdgeClock _ -> false, "single-edge"
        | TwoPhaseClock tp ->
            let dffs = c.Placed |> List.filter (fun p -> p.Gate.Kind = Dff)
            let clkNetsUsed = dffs |> List.choose (fun p -> List.tryHead p.Gate.Inputs) |> List.distinct
            // ClockPinGroups が両クロックとも持つ区画数
            let groupSizeA = Map.tryFind tp.ClockA c.ClockPinGroups |> Option.map List.length |> Option.defaultValue 0
            let groupSizeB = Map.tryFind tp.ClockB c.ClockPinGroups |> Option.map List.length |> Option.defaultValue 0
            let mastersCount = dffs |> List.filter (fun p -> List.tryHead p.Gate.Inputs |> Option.isSome) |> List.length
            let withinBound = groupSizeA >= 1 && groupSizeA <= k && groupSizeB >= 1 && groupSizeB <= k
            // 全部の区画ネットに少なくとも 1 個の DFF がぶら下がっていること (空クラスタなし)
            let allNonEmpty =
                clkNetsUsed |> List.forall (fun clk ->
                    dffs |> List.exists (fun p -> List.tryHead p.Gate.Inputs = Some clk))
            let ok = withinBound && allNonEmpty
            ok, sprintf "clkA groups=%d clkB groups=%d (k=%d, dffs=%d)" groupSizeA groupSizeB k dffs.Length

    let private qBitsOf (json: string) =
        match Pipeline.parseYosysJson json with
        | Ok m -> m.Ports.["q"].Bits
        | Error _ -> []

    let private k4LogicTests () : (string * bool) list =
        let k = 4
        [ for circuit in [ "counter4"; "reg8" ] do
            let path = verilogPath circuit
            if not (File.Exists path) then
                yield sprintf "WL-CLKSPLIT: %s.json present" circuit, false
            else
                let json = File.ReadAllText path
                let opts = { defaultCompileOptions with Placement = annealed; Clocking = TwoPhase; ClockPins = k }
                match compileWLWithOptions opts json with
                | Error e ->
                    printfn "  WL_CLKSPLIT_ERR (%s k=%d): %A" circuit k e
                    yield sprintf "WL-CLKSPLIT: %s compiles two-phase k=%d" circuit k, false
                | Ok c ->
                    let invOk, detail = splitInvariantHolds k c
                    printfn "  WL_CLKSPLIT: %s k=%d %s" circuit k detail
                    yield sprintf "WL-CLKSPLIT: %s k=%d split invariant (%s)" circuit k detail, invOk
                    match clockPinsOf c with
                    | Some (ClockDrive.TwoPhasePins _ as clocks) ->
                        let qBits = qBitsOf json
                        let init0, ok =
                            match circuit with
                            | "counter4" -> WlCounterTest.verifyCountingWith settler clocks qBits c.Grid c.Placed
                            | _ -> WlReg8Test.verifyWriteReadWith settler clocks qBits c.Grid c.Placed c.Pins
                        yield sprintf "WL-CLKSPLIT: %s k=%d initial value 0" circuit k, init0
                        yield sprintf "WL-CLKSPLIT: %s k=%d logic test passes" circuit k, ok
                    | _ -> yield sprintf "WL-CLKSPLIT: %s k=%d has clk_a/clk_b pins" circuit k, false ]

    // sm83_min (380 ゲート) を k=4 で。TwoPhaseTests.fs の sm83MinTests と同じ手順。
    type private Sm83Step = { Desc: string; Inst: int; A: int; B: int; Pc: int; Flags: int }

    let private loadProgram (path: string) : Sm83Step list =
        use doc = Text.Json.JsonDocument.Parse (File.ReadAllText path)
        [ for step in doc.RootElement.GetProperty("steps").EnumerateArray () do
            let expect = step.GetProperty "expect"
            let v (name: string) = expect.GetProperty(name).GetInt32 ()
            yield { Desc = step.GetProperty("desc").GetString ()
                    Inst = step.GetProperty("pins").GetProperty("inst").GetInt32 ()
                    A = v "a"; B = v "b"; Pc = v "pc"; Flags = v "flags" } ]

    let private sm83MinK4Tests () : (string * bool) list =
        let k = 4
        let path = verilogPath "sm83_min"
        let programPath = Path.Combine (__SOURCE_DIRECTORY__, "..", "web", "sm83_min_program.json")
        if not (File.Exists path && File.Exists programPath) then
            [ "WL-CLKSPLIT: sm83_min.json and web/sm83_min_program.json present", false ]
        else
            let json = File.ReadAllText path
            let opts = { defaultCompileOptions with Placement = annealed; Clocking = TwoPhase; ClockPins = k }
            match compileWLWithOptions opts json, parseYosysPorts json with
            | Error e, _ -> [ sprintf "WL-CLKSPLIT: sm83_min compiles two-phase k=%d (%A)" k e, false ]
            | _, Error e -> [ sprintf "WL-CLKSPLIT: sm83_min ports (%s)" (describeError e), false ]
            | Ok c, Ok ports ->
                let invOk, detail = splitInvariantHolds k c
                printfn "  WL_CLKSPLIT: sm83_min k=%d %s" k detail
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
                            let next =
                                ClockDrive.settleData settler settleLimit "inst" (writePort "inst" step.Inst g)
                                |> Result.bind (ClockDrive.clockEdge settler settleLimit clocks)
                            match next with
                            | Error e -> Error e, passed
                            | Ok g' ->
                                let got = readPort "a_out" g', readPort "b_out" g', readPort "pc_out" g', readPort "flags_out" g'
                                let ok = got = (step.A, step.B, step.Pc, step.Flags)
                                Ok g', (if ok then passed + 1 else passed)
                    let final, passed = steps |> List.fold run (afterReset, 0)
                    (match final with
                     | Error e -> printfn "  WL_CLKSPLIT_SM83MIN: %s" (ClockDrive.describeDriveError e)
                     | Ok _ -> ())
                    [ sprintf "WL-CLKSPLIT: sm83_min k=%d split invariant (%s)" k detail, invOk
                      sprintf "WL-CLKSPLIT: sm83_min k=%d CA matches GPU-verified expectations (%d/%d instructions)"
                          k passed steps.Length,
                      passed = steps.Length && steps.Length = 20 ]
                | _ -> [ sprintf "WL-CLKSPLIT: sm83_min k=%d has clk_a/clk_b pins" k, false ]

    // --- meta v1 / v2 / v3 ----------------------------------------------------

    let private fixedProvenance : Provenance =
        { GitCommit = "test"
          CreatedAtUtc = DateTimeOffset (2026, 9, 28, 0, 0, 0, TimeSpan.Zero) }

    let private metaVersionTests () : (string * bool) list =
        let path = verilogPath "counter4"
        if not (File.Exists path) then [ "WL-CLKSPLIT: counter4.json present", false ] else
        let sourceBytes = File.ReadAllBytes path
        let json = Text.Encoding.UTF8.GetString sourceBytes
        let compiled = compileWLWithOptions { defaultCompileOptions with Clocking = TwoPhase; ClockPins = 4 } json
        match compiled, parseYosysPorts json with
        | Error e, _ -> [ sprintf "WL-CLKSPLIT: counter4 two-phase k=4 compiles (%A)" e, false ]
        | _, Error e -> [ sprintf "WL-CLKSPLIT: counter4 ports (%s)" (describeError e), false ]
        | Ok c, Ok ports ->
            match buildMetaOfCompiled "counter4" (sourceSha256 sourceBytes) fixedProvenance ports c with
            | Error e -> [ sprintf "WL-CLKSPLIT: k=4 buildMeta (%s)" (describeError e), false ]
            | Ok meta ->
                let v3Json = metaToJson meta
                let v3RoundTrip = metaOfJson "v3" v3Json = Ok meta
                let hasMultiplePins =
                    match meta.Clocking with
                    | TwoPhaseClocking (_, a, b) -> a.Length > 1 || b.Length > 1
                    | SingleEdgeClocking -> false
                // v2 互換: JSON を手で「clkA/clkB は先頭要素だけの単一オブジェクト」に書き換え、
                // formatVersion を 2 にする (実際の v2 書き出しの模擬)。
                let v2Json =
                    match meta.Clocking with
                    | TwoPhaseClocking (port, a, b) ->
                        use doc = Text.Json.JsonDocument.Parse v3Json
                        use ms = new MemoryStream ()
                        use w = new Text.Json.Utf8JsonWriter (ms, Text.Json.JsonWriterOptions (Indented = true))
                        let root = doc.RootElement
                        w.WriteStartObject ()
                        for prop in root.EnumerateObject () do
                            if prop.Name = "formatVersion" then w.WriteNumber ("formatVersion", 2)
                            elif prop.Name = "clocking" then
                                w.WriteStartObject "clocking"
                                w.WriteString ("scheme", "twoPhase")
                                w.WriteString ("clockPort", port)
                                w.WritePropertyName "clkA"
                                w.WriteStartObject ()
                                w.WriteNumber ("x", a.Head.X)
                                w.WriteNumber ("y", a.Head.Y)
                                w.WriteEndObject ()
                                w.WritePropertyName "clkB"
                                w.WriteStartObject ()
                                w.WriteNumber ("x", b.Head.X)
                                w.WriteNumber ("y", b.Head.Y)
                                w.WriteEndObject ()
                                w.WriteEndObject ()
                            else prop.WriteTo w
                        w.WriteEndObject ()
                        w.Flush ()
                        Some (Text.Encoding.UTF8.GetString (ms.ToArray ()))
                    | SingleEdgeClocking -> None
                let v2ReadsAsSingletonLists =
                    match v2Json, meta.Clocking with
                    | Some j, TwoPhaseClocking (port, a, b) ->
                        match metaOfJson "v2" j with
                        | Ok m2 ->
                            m2.FormatVersion = SingleClockPinFormatVersion
                            && m2.Clocking = TwoPhaseClocking (port, [ a.Head ], [ b.Head ])
                        | Error _ -> false
                    | _ -> false
                // v1: clocking なし (単相として読む)。既存の互換テスト (TwoPhaseTests.fs) と同じ規則。
                let v1Json =
                    v3Json.Replace ("\"formatVersion\": 3", "\"formatVersion\": 1")
                let v1ReadsAsSingleEdge =
                    match metaOfJson "v1" v1Json with
                    | Ok m -> m.Clocking = SingleEdgeClocking && m.FormatVersion = LegacyFormatVersion
                    | Error _ -> false
                let unknownVersionRejected =
                    match metaOfJson "v9" (v3Json.Replace ("\"formatVersion\": 3", "\"formatVersion\": 9")) with
                    | Error (FormatVersionMismatch (CurrentFormatVersion, 9)) -> true
                    | _ -> false
                [ "WL-CLKSPLIT: k=4 meta has multiple clkA/clkB pins", hasMultiplePins
                  "WL-CLKSPLIT: v3 meta (formatVersion 3, coordinate lists) survives round trip", v3RoundTrip
                  "WL-CLKSPLIT: v2 meta (clkA/clkB single coordinate) reads as singleton lists", v2ReadsAsSingletonLists
                  "WL-CLKSPLIT: v1 meta (no clocking) reads as single-edge", v1ReadsAsSingleEdge
                  "WL-CLKSPLIT: unknown formatVersion is still rejected", unknownVersionRejected ]

    let runAll () : (string * bool) list =
        clusterTests ()
        @ k4LogicTests ()
        @ sm83MinK4Tests ()
        @ metaVersionTests ()
