/// WireWorld Grid → Golly RLE エクスポーター
/// 使い方: dotnet fsi ExportRLE.fsx
/// 出力: ../golly/ ディレクトリに .rle ファイルを生成
#load "WwHdl.fs"
open WwHdl
open Domain
open Library
open Pipeline
open Sta
open Sim

// WireWorld の Golly RLE エンコーディング (多状態 CA):
//   b = 状態0 (Empty)
//   A = 状態1 (Electron Head)
//   B = 状態2 (Electron Tail)
//   C = 状態3 (Conductor/Wire)
let encodeState = function
    | Empty -> 'b'
    | Head  -> 'A'
    | Tail  -> 'B'
    | Wire  -> 'C'

/// Grid → RLE 文字列 (Golly WireWorld 形式)
let gridToRLE (grid: Grid) : string =
    if Map.isEmpty grid then "b!"
    else
        let xs = grid |> Map.keys |> Seq.map (fun c -> c.X)
        let ys = grid |> Map.keys |> Seq.map (fun c -> c.Y)
        let minX, maxX = Seq.min xs, Seq.max xs
        let minY, maxY = Seq.min ys, Seq.max ys
        let w = maxX - minX + 1
        let h = maxY - minY + 1

        let rows =
            [ for y in minY..maxY ->
                [ for x in minX..maxX ->
                    Map.tryFind { X=x; Y=y } grid
                    |> Option.defaultValue Empty
                    |> encodeState ] ]

        // RLE ランレングス圧縮
        let encodeRow (row: char list) : string =
            let sb = System.Text.StringBuilder()
            let mutable cur = row.[0]
            let mutable count = 1
            for i in 1..row.Length-1 do
                if row.[i] = cur then count <- count + 1
                else
                    if count > 1 then sb.Append(string count) |> ignore
                    sb.Append(string cur) |> ignore
                    cur <- row.[i]
                    count <- 1
            // 末尾の 'b' (Empty) はトリム可能だが今は書く
            if count > 1 then sb.Append(string count) |> ignore
            sb.Append(string cur) |> ignore
            sb.ToString()

        let body =
            rows
            |> List.map encodeRow
            |> String.concat "$"
        sprintf "x = %d, y = %d, rule = WireWorld\n%s!" w h body

/// Grid をファイルに書き出す
let writeRLE (path: string) (desc: string) (grid: Grid) =
    let rle = gridToRLE grid
    let content = sprintf "# %s\n%s" desc rle
    System.IO.File.WriteAllText(path, content)
    printfn "Wrote: %s" path

// ────────────────────────────────────────────────────────────────────────────
// 出力ディレクトリ
// ────────────────────────────────────────────────────────────────────────────
let outDir = "../golly"
System.IO.Directory.CreateDirectory(outDir) |> ignore

// ────────────────────────────────────────────────────────────────────────────
// 1. 個別セルのパターン
// ────────────────────────────────────────────────────────────────────────────
let cells =
    [ "buf",      Library.buf
      "or2",      Library.or2
      "splitter", Library.splitter
      "junc3",    Library.junc3
      "diode",    Library.diode ]

for (name, cell) in cells do
    writeRLE (sprintf "%s/cell_%s.rle" outDir name) (sprintf "%s pattern" cell.Name) cell.Pattern

// ────────────────────────────────────────────────────────────────────────────
// 2. JUNC3 NANDテスト: 4真理値 × 各パターンをシミュレーション済みグリッドで出力
// ────────────────────────────────────────────────────────────────────────────
let testJunc3 (a: bool) (b: bool) =
    let inPorts = Library.junc3.Ports |> List.filter (fun p -> p.Role = In)
    let outPort = Library.junc3.Ports |> List.find (fun p -> p.Role = Out)
    let initial =
        [ for i, inInput in List.indexed [a; b; true] do  // C=clock=true
            if inInput then yield inPorts.[i].Offset, Head ]
        |> List.fold (fun g (c, s) -> Map.add c s g) Library.junc3.Pattern
    let result = Rule.run (int Library.junc3.Latency) initial
    let outVal = Domain.get result outPort.Offset
    printfn "JUNC3 NAND(%d,%d) → out=%A (expected %A)"
        (if a then 1 else 0) (if b then 1 else 0)
        outVal (if not (a && b) then Head else Wire)
    initial  // 初期状態 (Head 注入済み) を返す

printfn "\n=== JUNC3 NAND tests ==="
for a in [false; true] do
    for b in [false; true] do
        let initGrid = testJunc3 a b
        let label = sprintf "NAND(%d,%d)" (if a then 1 else 0) (if b then 1 else 0)
        writeRLE (sprintf "%s/junc3_%s.rle" outDir label) (sprintf "JUNC3 %s t=0" label) initGrid

// ────────────────────────────────────────────────────────────────────────────
// 3. 半加算器コンパイル結果 (全入力パターン)
// ────────────────────────────────────────────────────────────────────────────
let halfAdderJson = """
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

printfn "\n=== Half-Adder compilation ==="
match compileFull Library.defaultLib halfAdderJson with
| Error e ->
    printfn "Compile error: %A" e
| Ok (grid, placement, wires) ->
    // コンパイル済みグリッド（信号なし）を出力
    writeRLE (sprintf "%s/halfadder_compiled.rle" outDir) "Half-adder compiled grid (no input)" grid

    let arrivals = computeArrival placement wires

    // STA 情報を表示
    printfn "Arrivals:"
    for kvp in arrivals do
        printfn "  net%d: %d" (let (Netlist.NetId n) = kvp.Key in n) (int kvp.Value)
    printfn "Wires:"
    for w in wires do
        let (Netlist.NetId n) = w.Net
        let (Netlist.NetId c) = w.Consumer
        printfn "  net%d→gate%d: delay=%d path_len=%d" n c (int w.Delay) w.Path.Length

    // u4 のポート座標と clockTimeOf を表示
    let u4opt = placement |> List.tryFind (fun p -> p.Gate.Output = Netlist.NetId 8)
    match u4opt with
    | None -> printfn "u4 not found"
    | Some u4 ->
        let u4ins = u4.Cell.Ports |> List.filter (fun p -> p.Role = In) |> List.map (Place.portCoord u4)
        let u4clk = clockTimeOf u4 arrivals wires
        printfn "\nu4 (sum gate):"
        printfn "  origin=(%d,%d)" u4.Origin.X u4.Origin.Y
        printfn "  in-ports=%A" (u4ins |> List.map (fun c -> sprintf "(%d,%d)" c.X c.Y))
        printfn "  clockTimeOf=%d" (int u4clk)

    // 各入力パターンでシミュレーション済みグリッドを出力
    let gateDrivenNets = placement |> List.map (fun p -> p.Gate.Output) |> Set.ofList

    let makeInjections (a: bool) (b: bool) =
        let primaryMap = Map.ofList [Netlist.NetId 2, a; Netlist.NetId 3, b]
        placement |> List.collect (fun p ->
            let t = clockTimeOf p arrivals wires
            let inPorts = p.Cell.Ports |> List.filter (fun port -> port.Role = In)
            p.Gate.Inputs
            |> List.mapi (fun i n ->
                if Set.contains n gateDrivenNets then []
                else
                    let coord = Place.portCoord p inPorts.[i]
                    match Map.tryFind n primaryMap with
                    | Some true -> [(coord, t)]
                    | _ -> [])
            |> List.concat)

    for a in [false; true] do
        for b in [false; true] do
            let label = sprintf "a%d_b%d" (if a then 1 else 0) (if b then 1 else 0)
            let inj = makeInjections a b
            // クロック注入のみで初期化したグリッド（t=0）を出力
            let initGrid =
                placement |> List.collect (fun p ->
                    Sim.clockCoords p |> List.map (fun c -> c, Wire))
                |> List.fold (fun g (c, s) -> Map.add c s g) grid
            writeRLE (sprintf "%s/halfadder_%s.rle" outDir label) (sprintf "Half-adder input a=%b b=%b (no clock injected yet)" a b) grid

    printfn "\nDone. RLE files written to %s/" outDir
