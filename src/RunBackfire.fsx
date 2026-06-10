#r "bin/Debug/net8.0/WwHdl.dll"
// バックファイア検証: junc3 を NOT(0) として発火させたとき、
// 入力 A の配線に電子が逆流するかを観測する。
open WwHdl
open WwHdl.Domain
open WwHdl.Library

// junc3 パターン + A ポート (0,0) から左へ 12 セルの入力配線
let aWire = [ for x in -12 .. -1 -> { X = x; Y = 0 }, Wire ] |> Map.ofList
let grid0 =
    junc3.Pattern
    |> Map.fold (fun acc k v -> Map.add k v acc) aWire
    // クロック注入: B(2,0) と C(0,2) に Head (NOT の評価、A=0)
    |> Map.add { X = 2; Y = 0 } Head
    |> Map.add { X = 0; Y = 2 } Head

let show (g: Grid) (t: int) =
    printfn "t=%d:" t
    for y in -1 .. 3 do
        let row =
            [ for x in -12 .. 5 ->
                match get g { X = x; Y = y } with
                | Empty -> '.' | Head -> 'H' | Tail -> 't' | Wire -> '#' ]
            |> System.String.Concat
        printfn "  %s" row

let mutable g = grid0
show g 0
for t in 1 .. 16 do
    g <- Rule.step g
    show g t

// 判定: A 配線 (x < 0) に Head が出現したか
let mutable g2 = grid0
let mutable backfire = false
for _ in 1 .. 20 do
    g2 <- Rule.step g2
    for x in -12 .. -1 do
        if get g2 { X = x; Y = 0 } = Head then backfire <- true
printfn ""
printfn "A配線への逆流: %s" (if backfire then "発生 (backfire confirmed)" else "なし")
