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

let all = m1 @ m2 @ m3 @ m4 @ m5 @ ms
let fails = all |> List.filter (snd >> not)
printfn "\nTotal: %d/%d passed" (all.Length - fails.Length) all.Length
