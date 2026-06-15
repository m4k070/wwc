#r "bin/Debug/net8.0/WwHdl.dll"
printfn "Starting SM83 test..."
System.Console.Out.Flush()
let results = WwHdl.WlSm83Test.runAll ()
printfn "Done: %d tests" results.Length
for (name, passed) in results do
    printfn "%s  %s" (if passed then "PASS" else "FAIL") name
System.Console.Out.Flush()
