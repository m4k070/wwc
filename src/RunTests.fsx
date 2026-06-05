#r "bin/Debug/net8.0/WwHdl.dll"
open WwHdl.CellTest
let results = runAll ()
for (name, passed) in results do
    printfn "%s  %s" (if passed then "PASS" else "FAIL") name
let fails = results |> List.filter (snd >> not)
printfn "\n%d/%d passed" (results.Length - fails.Length) results.Length
