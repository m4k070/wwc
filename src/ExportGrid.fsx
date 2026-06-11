#r "bin/Debug/net8.0/WwHdl.dll"
// コンパイルした WireLevel 回路を grid.bin にエクスポート (GPU 実行用)
open System.IO
open WwHdl.Domain
open WwHdl.Netlist
open WwHdl.WireLevel
open WwHdl.PipelineWL

let outDir = Path.Combine(__SOURCE_DIRECTORY__, "..", "web")
Directory.CreateDirectory(outDir) |> ignore

// トグル FF (NOT + DFF)
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

match compileWL toggleJson with
| Error e -> eprintfn "toggle FF compile error: %A" e
| Ok (grid, placed, pins) ->
    let clkPin = pins.[NetId 2]
    let outDir = Path.Combine(__SOURCE_DIRECTORY__, "..", "web")
    
    // 初期グリッドをエクスポート (clk=0)
    let initBin = exportGrid grid
    File.WriteAllBytes(Path.Combine(outDir, "toggle_init.bin"), initBin)
    printfn "toggle_init.bin: %d bytes (grid %d cells)" initBin.Length (grid |> Map.count)

    // clk=1 設定直後 (未収束) — GPU ゴールデンテストの初期状態。
    // GPU で N 世代回すと toggle_clk1.bin (収束状態) に一致するはず
    let g1init = setPin clkPin true grid
    File.WriteAllBytes(Path.Combine(outDir, "toggle_clk1_init.bin"), exportGrid g1init)
    printfn "toggle_clk1_init.bin: %d bytes" (exportGrid g1init).Length

    // clk=1 に設定して settle → エクスポート
    let g1 = fst (settle 2000 (setPin clkPin true grid))
    let step1Bin = exportGrid g1
    File.WriteAllBytes(Path.Combine(outDir, "toggle_clk1.bin"), step1Bin)
    printfn "toggle_clk1.bin: %d bytes" step1Bin.Length

    // clk=0 に戻す → 次のサイクル準備
    let g2 = fst (settle 2000 (setPin clkPin false g1))
    let step2Bin = exportGrid g2
    File.WriteAllBytes(Path.Combine(outDir, "toggle_clk0.bin"), step2Bin)
    printfn "toggle_clk0.bin: %d bytes" step2Bin.Length

// 半加算器
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
| Error e -> eprintfn "HA compile error: %A" e
| Ok (grid, placed, pins) ->
    let haInit = exportGrid grid
    File.WriteAllBytes(Path.Combine(outDir, "ha_init.bin"), haInit)
    printfn "ha_init.bin: %d bytes (grid %d cells)" haInit.Length (grid |> Map.count)

    // a=1,b=1 設定直後 (未収束) — GPU ゴールデンテストの初期状態
    let gInit = grid |> setPin pins.[NetId 2] true |> setPin pins.[NetId 3] true
    File.WriteAllBytes(Path.Combine(outDir, "ha_11_init.bin"), exportGrid gInit)
    printfn "ha_11_init.bin: %d bytes" (exportGrid gInit).Length

    // a=1,b=1 → carry=1, sum=0 を settle で確認
    let g = gInit |> settle 1000 |> fst
    let ha11 = exportGrid g
    File.WriteAllBytes(Path.Combine(outDir, "ha_11.bin"), ha11)
    printfn "ha_11.bin: %d bytes" ha11.Length

// ALU (yosys 合成済み JSON から)
let exportAlu (name: string) =
    let path = Path.Combine(__SOURCE_DIRECTORY__, "..", "verilog", name + ".json")
    match compileWL (File.ReadAllText path) with
    | Error e -> eprintfn "%s compile error: %A" name e
    | Ok (grid, _, _) ->
        let bin = exportGrid grid
        File.WriteAllBytes(Path.Combine(outDir, name + "_init.bin"), bin)
        printfn "%s_init.bin: %d bytes (grid %d cells)" name bin.Length (grid |> Map.count)

exportAlu "alu2"
exportAlu "alu4"

printfn "\nDone. Run 'python3 -m http.server' in web/ to view."
