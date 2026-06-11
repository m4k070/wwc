#r "bin/Debug/net8.0/WwHdl.dll"

let run label (results: (string * bool) list) =
    printfn "\n=== %s ===" label
    for (name, passed) in results do
        printfn "%s  %s" (if passed then "PASS" else "FAIL") name
    results

let m1 = run "M1 CellTest"     (WwHdl.CellTest.runAll ())
let m2 = run "M2 FrontendTest" (WwHdl.FrontendTest.runAll ())
let m3 = run "M3 RoutingTest"  (WwHdl.RoutingTest.runAll ())
let m4 = run "M4 StaTest"      (WwHdl.StaTest.runAll ())
let m5 = run "M5 E2eTest"      (WwHdl.E2eTest.runAll ())
let ms = run "Multi-stage E2E" (WwHdl.MultiStageTest.runAll ())
let ng = run "NAND/AND Gate"   (WwHdl.NandGateTest.runAll ())
let ha = run "MultiGate E2E"  (WwHdl.MultiGateTest.runAll ())
let ha2= run "HalfAdder E2E" (WwHdl.HalfAdderTest.runAll ())
let fa = run "FullAdder E2E" (WwHdl.FullAdderTest.runAll ())
let nc = run "NAND Chain 9" (WwHdl.NandChain9Test.runAll ())
let lc = run "Large Circuit 100" (WwHdl.LargeCircuitTest.runAll ())
let fb = run "Feedback"      (WwHdl.FeedbackTest.runAll ())
let wl = run "WireLevel CA"  (WwHdl.WireLevelTest.runAll ())
let wp = run "WL Pipeline"   (WwHdl.WlPipelineTest.runAll ())
let wc = run "WL Counter4"   (WwHdl.WlCounterTest.runAll ())
let wr = run "WL Reg8"       (WwHdl.WlReg8Test.runAll ())
let wg = run "WL Golden"     (WwHdl.WlGoldenTest.runAll ())

let all = m1 @ m2 @ m3 @ m4 @ m5 @ ms @ ng @ ha @ ha2 @ fa @ nc @ lc @ fb @ wl @ wp @ wc @ wr @ wg
let fails = all |> List.filter (snd >> not)
printfn "\nTotal: %d/%d passed" (all.Length - fails.Length) all.Length
