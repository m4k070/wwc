#r "bin/Debug/net8.0/WwHdl.dll"
open WwHdl.Domain
open WwHdl.Library
open WwHdl.Rule

// 3入力ケース (NAND=0 のはず) で (4,2) が出力されていないか確認
let all3 = junc3.Pattern |> Map.add {X=0;Y=2} Head |> Map.add {X=2;Y=0} Head |> Map.add {X=2;Y=4} Head
printfn "All-3-input (should BLOCK) trace:"
for t in 0..5 do
    let g = run t all3
    let state coord = match Map.tryFind coord g with Some s -> sprintf "%A" s | None -> "Empty"
    printfn "  t=%d: (2,1)=%s (2,3)=%s (2,2)=%s (3,2)=%s (4,2)=%s"
        t (state {X=2;Y=1}) (state {X=2;Y=3}) (state {X=2;Y=2}) (state {X=3;Y=2}) (state {X=4;Y=2})
