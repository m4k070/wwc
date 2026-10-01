// NetlistSim の速度計測 (issue #12)。golden の直列化を含めず、純粋なシミュレーション速度だけを測る。
// 前提: dotnet build src/WwHdl.fsproj -c Release (Release DLL を参照する。Debug だと数倍遅い)
// 使い方: dotnet fsi src/BenchNetlistSim.fsx [<program.json>]
//   既定: routed/bootrom_minimal.json (6000 周期) を sm83_full のネットリストで実行
// 最適化の前後を比べるときは、同じプログラム・同じハーネス・同じビルド構成で測ること。
// 回路 (verilog/sm83_full.json) とプログラムの両方が速度に効く (入力の活動率が変わるため)。
#r "bin/Release/net8.0/WwHdl.dll"
open System
open System.Diagnostics
open System.IO
open WwHdl
open WwHdl.Testbench
open WwHdl.NetlistSim

let programRel =
    if fsi.CommandLineArgs.Length > 1 then fsi.CommandLineArgs.[1]
    else "routed/bootrom_minimal.json"

let fail (msg: string) = eprintfn "ERROR: %s" msg; exit 2

let unwrap (label: string) (r: Result<'a, TestbenchError>) =
    match r with
    | Ok v -> v
    | Error e -> fail (sprintf "%s: %s" label (describeTestbenchError e))

let json = File.ReadAllText "verilog/sm83_full.json"

let net, ports =
    match Pipeline.frontend json, RoutedArtifact.parseYosysPorts json with
    | Ok nl, Ok p ->
        (match NetlistSim.compile nl with
         | Ok c -> c, p
         | Error e -> fail (describeSimError e))
    | Error e, _ -> fail (sprintf "frontend: %A" e)
    | _, Error e -> fail (RoutedArtifact.describeError e)

let bus = unwrap "バス" (resolveBus ports)
let program = unwrap "プログラム" (parseProgram programRel (File.ReadAllText programRel))
let rom = unwrap "ROM" (loadRom program.Rom)

let memory =
    match program.BootRom with
    | None -> createMemory rom program.Memory
    | Some src -> createMemoryWithBootRom rom (unwrap "ブート ROM" (loadRom src)) program.Memory

// 計測はシミュレーション本体だけ (コンパイルとメモリ構築を含めない)。
// CPU の周波数変動などで 1 回の計測は ±10% ばらつくため、複数回の中央値を採る。
// 他の重い処理 (テストスイート等) と同時に走らせると絶対値が大きくずれるので、
// 比較するときは同じ条件 (同じプログラム・同じ負荷) で測ること。
let repeat = if fsi.CommandLineArgs.Length > 2 then int fsi.CommandLineArgs.[2] else 3

let times =
    [| for _ in 1 .. repeat ->
         let sw = Stopwatch.StartNew ()
         let r = unwrap "NetlistSim" (run net ports bus memory program.RstPulses program.Cycles)
         sw.Stop ()
         ignore r.Memory
         sw.Elapsed.TotalSeconds |]

let sorted = Array.sort times
let median = sorted.[sorted.Length / 2]
printfn "program=%s  cycles=%d  repeat=%d" programRel program.Cycles repeat
printfn "  best=%.3f s (%.0f 周期/秒)  median=%.3f s (%.0f 周期/秒)  worst=%.3f s"
    sorted.[0] (float program.Cycles / sorted.[0])
    median (float program.Cycles / median)
    sorted.[sorted.Length - 1]
printfn "組合せゲート %d / ネット %d (1 周期 = apply 3 回)"
    net.CombGates.Length net.NetIndex.Count
