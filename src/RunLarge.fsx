#r "bin/Debug/net8.0/WwHdl.dll"
open System
open WwHdl

let sw = Diagnostics.Stopwatch.StartNew()
let results = LargeCircuitTest.runAll ()
sw.Stop()
for (name, passed) in results do
    printfn "%s  %s" (if passed then "PASS" else "FAIL") name
printfn "Elapsed: %d ms" sw.ElapsedMilliseconds
