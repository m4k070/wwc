#r "bin/Debug/net8.0/WwHdl.dll"
// PipelineWL デバッグ: 半加算器を WireLevel にコンパイルして真理値表を確認
open WwHdl
open WwHdl.Domain
open WwHdl.Netlist
open WwHdl.WireLevel
open WwHdl.PipelineWL

let haJson = """
{
  "modules": { "top": {
    "ports": {
      "a":     {"direction":"input", "bits":[2]},
      "b":     {"direction":"input", "bits":[3]},
      "carry": {"direction":"output","bits":[5]},
      "sum":   {"direction":"output","bits":[8]}
    },
    "cells": {
      "u0": {"type":"$_NAND_","port_directions":{"A":"input","B":"input","Y":"output"},"connections":{"A":[2],"B":[3],"Y":[4]}},
      "u1": {"type":"$_NOT_", "port_directions":{"A":"input","Y":"output"},            "connections":{"A":[4],"Y":[5]}},
      "u2": {"type":"$_NAND_","port_directions":{"A":"input","B":"input","Y":"output"},"connections":{"A":[2],"B":[4],"Y":[6]}},
      "u3": {"type":"$_NAND_","port_directions":{"A":"input","B":"input","Y":"output"},"connections":{"A":[3],"B":[4],"Y":[7]}},
      "u4": {"type":"$_NAND_","port_directions":{"A":"input","B":"input","Y":"output"},"connections":{"A":[6],"B":[7],"Y":[8]}}
    }
  }}
}"""

match compileWL haJson with
| Error e -> printfn "COMPILE ERROR: %A" e
| Ok (grid, placed, pins) ->
    printfn "%s" (dumpAscii grid)
    let outOf n =
        placed |> List.find (fun p -> p.Gate.Output = NetId n) |> fun p -> p.Coord
    let pinA, pinB = pins.[NetId 2], pins.[NetId 3]
    let sumC, carryC = outOf 8, outOf 5
    for a in [false; true] do
        for b in [false; true] do
            let g, t = grid |> setPin pinA a |> setPin pinB b |> settle 1000
            let s, c = levelOf g sumC, levelOf g carryC
            let okS = s = (a <> b)
            let okC = c = (a && b)
            printfn "a=%b b=%b -> sum=%b carry=%b  (settle=%d) %s"
                a b s c t (if okS && okC then "OK" else "** NG **")

// --- トグル FF: q <= ~q ($_DFF_P_ 経由) ---
let toggleJson = """
{
  "modules": { "top": {
    "ports": {
      "clk": {"direction":"input","bits":[2]},
      "q":   {"direction":"output","bits":[3]}
    },
    "cells": {
      "u0": {"type":"$_NOT_","port_directions":{"A":"input","Y":"output"},"connections":{"A":[3],"Y":[4]}},
      "u1": {"type":"$_DFF_P_","port_directions":{"C":"input","D":"input","Q":"output"},"connections":{"C":[2],"D":[4],"Q":[3]}}
    }
  }}
}"""

printfn ""
match compileWL toggleJson with
| Error e -> printfn "TOGGLE COMPILE ERROR: %A" e
| Ok (grid, placed, pins) ->
    printfn "%s" (dumpAscii grid)
    let qC = placed |> List.find (fun p -> p.Gate.Output = NetId 3) |> fun p -> p.Coord
    let clkPin = pins.[NetId 2]
    let halfP = 64
    let mutable g = grid |> stepN halfP   // 初期収束 (clk=0)
    for k in 1 .. 4 do
        g <- g |> setPin clkPin true  |> stepN halfP
        g <- g |> setPin clkPin false |> stepN halfP
        printfn "cycle %d: q=%b (expect %b)" k (levelOf g qC) (k % 2 = 1)
