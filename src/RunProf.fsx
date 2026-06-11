#r "bin/Debug/net8.0/WwHdl.dll"
open System
open WwHdl

// Test 50-gate chain
let json = LargeCircuitTest.runAll() |> ignore
