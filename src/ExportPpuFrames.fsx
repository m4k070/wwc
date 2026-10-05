#r "bin/Release/net8.0/WwHdl.dll"
#r "/home/makoto/work/gbfs/src/gbfs.Lib/bin/Release/net8.0/gbfs.Lib.dll"
// ExportPpuFrames.fsx — 同じ ROM を gbfs で走らせ、PPU のフレームバッファを一定間隔で書き出す。
//
// CA (wgpu-runner) には PPU が無いので、「LCD に何が出るか」は gbfs の PPU に描かせる。
// CA 側のフレーム (wgpu-runner --memory --frames) と周期で突き合わせて、デモ映像で並べるための素材。
// CA のリセット PC は 0x0100、gbfs もブート後状態 (PC=0x0100) から始まるので、同じ ROM なら
// 同じ経路をたどる。VRAM の初期値も両者 0 (CA のメモリモデルも gbfs もゼロ初期化)。
//
// 出力: <out>/ppu%06d.raw  (160x144 バイト、値は 0-3 の階調、行優先)
//       <out>/ppu.tsv      (ファイル番号 <TAB> バス周期)。CA 側の frames.tsv と同じ刻み
//
// 使い方:
//   dotnet fsi src/ExportPpuFrames.fsx --rom routed/rom_demo_scroll.bin --cycles 6000 --every 4 --out DIR
//     --every M   バス周期 (M サイクル) M 個ごとに 1 枚。既定 4
//
// 前提:
//   dotnet build src/WwHdl.fsproj -c Release
//   (cd /home/makoto/work/gbfs && nix develop -c dotnet build src/gbfs.Lib/gbfs.Lib.fsproj -c Release)
open System
open System.IO
open WwHdl
open gbfs.Lib

let repoRoot = Path.GetFullPath (Path.Combine (__SOURCE_DIRECTORY__, ".."))
let repoPath (relative: string) = Path.GetFullPath (Path.Combine (repoRoot, relative))

type Options =
    { Rom: string
      Cycles: int
      Every: int
      Out: string }

let private parseArgs (argv: string list) =
    let rec go args acc =
        match args with
        | "--rom" :: v :: rest -> go rest { acc with Rom = v }
        | "--cycles" :: v :: rest -> go rest { acc with Cycles = int v }
        | "--every" :: v :: rest -> go rest { acc with Every = int v }
        | "--out" :: v :: rest -> go rest { acc with Out = v }
        | [] -> acc
        | other -> failwithf "不明な引数: %A" other
    go argv { Rom = "routed/rom_demo_scroll.bin"; Cycles = 6000; Every = 4; Out = "ppu_frames" }

let args = parseArgs (List.ofArray (fsi.CommandLineArgs |> Array.skip 1))

let romPath = repoPath args.Rom
if not (File.Exists romPath) then
    eprintfn "ERROR: ROM がない: %s" romPath
    exit 2

let outDir = repoPath args.Out
Directory.CreateDirectory outDir |> ignore

let romBytes = File.ReadAllBytes romPath
// gbfs はブート後状態 (LCDC=0x91) から始まるが、CA のメモリモデルは I/O もゼロ初期化なので
// LCDC を 0 に落としてから始める (ROM もすぐ 0 を書く)。これを揃えないと最初の数周期だけ
// PPU が先行して描き始める。
let state0 = Decoder.createState () |> Decoder.loadRomToState romBytes
let mutable state =
    { state0 with Mem = Memory.write 0xFF40us 0uy state0.Mem }
let mutable mCycles = 0
let mutable nextDump = 0
let mutable count = 0

let indexPath = Path.Combine (outDir, "ppu.tsv")
let index = new StreamWriter (indexPath)

printfn "[ppu] %s を %d バス周期ぶん実行 (%d 周期ごとに 1 枚)" (Path.GetFileName romPath) args.Cycles args.Every

while mCycles < args.Cycles do
    let s, tCycles = Decoder.stepWithCycles state
    state <- s
    mCycles <- mCycles + tCycles / 4
    if mCycles >= nextDump then
        File.WriteAllBytes (Path.Combine (outDir, sprintf "ppu%06d.raw" count), state.Ppu.FrameBuffer)
        index.WriteLine (sprintf "%d\t%d" count mCycles)
        count <- count + 1
        nextDump <- nextDump + args.Every

index.Flush ()
index.Dispose ()
printfn "[ppu] %d 枚を %s に書いた (最終 %d バス周期)" count outDir mCycles
printfn "[ppu] 次: python3 wgpu-runner/render_frames.py <ca_frames> <out_dir> --side-dir %s" args.Out
