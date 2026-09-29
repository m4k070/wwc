#r "bin/Release/net8.0/WwHdl.dll"
#r "/home/makoto/work/gbfs/src/gbfs.Lib/bin/Release/net8.0/gbfs.Lib.dll"
// RenderBootRom.fsx — ブート ROM プログラムの最終状態 (VRAM / LCDC / BGP / SCY) を gbfs の PPU で描く
//
// CA (wgpu-runner) には PPU が無いので、「LCD に何が出るか」は gbfs の PPU に VRAM とレジスタを
// 食わせて描く。VRAM は NetlistSim でプログラムを最後まで実行して取り出した最終メモリを使う
// (NetlistSim は CA と同じ規則で動くので、GPU 照合が通っていれば CA の最終状態と一致する)。
//
// 使い方:
//   dotnet fsi src/RenderBootRom.fsx [--program routed/bootrom_minimal.json] [--scy N] [--scale 4] [--out FILE]
//     --scy N    SCY を N で上書きする。省略時はメモリの値 (この ROM は $64)。
//                実機のブート ROM はロゴをスクロールで見せる (アニメは段階 2)。SCY=$64 のままだと
//                ロゴ (BG y=8..15) は画面外なので、ロゴを見るには --scy 0 を使う。
//     --scale N  拡大倍率 (既定 4、nearest neighbor)
//
// 前提:
//   dotnet build src/WwHdl.fsproj -c Release
//   (cd /home/makoto/work/gbfs && nix develop -c dotnet build src/gbfs.Lib/gbfs.Lib.fsproj -c Release)
//   ffmpeg (PNG 変換に使う)
open System
open System.Diagnostics
open System.IO
open WwHdl
open WwHdl.Testbench
open WwHdl.NetlistSim
open gbfs.Lib

let repoRoot = Path.GetFullPath (Path.Combine (__SOURCE_DIRECTORY__, ".."))
let repoPath (relative: string) = Path.GetFullPath (Path.Combine (repoRoot, relative))

type private Options =
    { Program: string
      Scy: int option
      Scale: int
      Out: string option }

let private parseArgs (argv: string list) =
    let rec go args acc =
        match args with
        | "--program" :: v :: rest -> go rest { acc with Program = v }
        | "--scy" :: v :: rest -> go rest { acc with Scy = Some (int v) }
        | "--scale" :: v :: rest -> go rest { acc with Scale = int v }
        | "--out" :: v :: rest -> go rest { acc with Out = Some v }
        | [] -> acc
        | other -> failwithf "不明な引数: %A" other
    go argv { Program = "routed/bootrom_minimal.json"; Scy = None; Scale = 4; Out = None }

/// 実機 DMG の 4 階調 (0 = 白 ... 3 = 黒)
let private palette =
    [| 0xE0uy, 0xF8uy, 0xD0uy   // 0: 最も明るい (DMG の緑がかった白)
       0x88uy, 0xC0uy, 0x70uy   // 1
       0x34uy, 0x68uy, 0x56uy   // 2
       0x08uy, 0x18uy, 0x20uy |] // 3: 最も暗い

let private writeRawRgb (path: string) (fb: byte[]) (scale: int) =
    let w, h = 160 * scale, 144 * scale
    let rgb = Array.zeroCreate (w * h * 3)
    for y in 0 .. h - 1 do
        for x in 0 .. w - 1 do
            let shade = int fb.[(y / scale) * 160 + (x / scale)] &&& 3
            let (r, g, b) = palette.[shade]
            let i = (y * w + x) * 3
            rgb.[i] <- r
            rgb.[i + 1] <- g
            rgb.[i + 2] <- b
    File.WriteAllBytes (path, rgb)

let private toPng (rawPath: string) (pngPath: string) (scale: int) =
    let w, h = 160 * scale, 144 * scale
    let psi = ProcessStartInfo ("ffmpeg", sprintf "-y -loglevel error -f rawvideo -pix_fmt rgb24 -s %dx%d -i %s -frames:v 1 %s" w h rawPath pngPath)
    psi.UseShellExecute <- false
    use p = Process.Start psi
    p.WaitForExit ()
    p.ExitCode = 0

/// 画面を文字で見る (ロゴ領域 y=56..79, x=24..135)
let private printAscii (fb: byte[]) =
    let chars = [| '.'; ':'; 'o'; '#' |]
    for y in 56 .. 79 do
        let sb = Text.StringBuilder ()
        for x in 24 .. 135 do
            sb.Append (chars.[int fb.[y * 160 + x] &&& 3]) |> ignore
        printfn "  %s" (sb.ToString ())

let exitCode =
    try
        let args = parseArgs (fsi.CommandLineArgs |> Array.toList |> List.tail)
        let programPath = repoPath args.Program
        // 1. プログラムを NetlistSim で実行して最終メモリを得る
        let json = File.ReadAllText (repoPath "verilog/sm83_full.json")
        let loaded =
            match Pipeline.frontend json, RoutedArtifact.parseYosysPorts json with
            | Ok nl, Ok ports ->
                compile nl |> Result.mapError describeSimError |> Result.map (fun c -> c, ports)
            | Error e, _ -> Error (sprintf "frontend: %A" e)
            | _, Error e -> Error (RoutedArtifact.describeError e)
        match loaded with
        | Error msg ->
            eprintfn "ERROR: sm83_full の読み込みに失敗: %s" msg
            2
        | Ok (net, ports) ->
            match resolveBus ports with
            | Error e ->
                eprintfn "ERROR: バスの解決に失敗: %s" (describeTestbenchError e)
                2
            | Ok bus ->
                let program = parseProgram programPath (File.ReadAllText programPath)
                match program with
                | Error e ->
                    eprintfn "ERROR: プログラムの解析に失敗: %s" (describeTestbenchError e)
                    2
                | Ok program ->
                    let memoryResult =
                        match program.BootRom with
                        | None -> loadRom program.Rom |> Result.map (fun rom -> createMemory rom program.Memory)
                        | Some src ->
                            loadRom program.Rom
                            |> Result.bind (fun rom -> loadRom src |> Result.map (fun boot -> createMemoryWithBootRom rom boot program.Memory))
                    match memoryResult with
                    | Error e ->
                        eprintfn "ERROR: ROM の読み込みに失敗: %s" (describeTestbenchError e)
                        2
                    | Ok memory ->
                        match run net ports bus memory program.RstPulses program.Cycles with
                        | Error e ->
                            eprintfn "ERROR: NetlistSim の実行に失敗: %s" (describeTestbenchError e)
                            2
                        | Ok result ->
                            let image = result.Memory
                            printfn "[render] %s を %d 周期実行 (最終メモリから VRAM/LCDC/BGP/SCY を取り出す)"
                                (Path.GetFileName programPath) program.Cycles
                            // 2. gbfs の PPU に VRAM とレジスタを食わせる
                            let romBytes = match loadRom program.Rom with Ok r -> r | Error _ -> Array.zeroCreate 0x8000
                            let state = Decoder.createState () |> Decoder.loadRomToState romBytes
                            let mutable mem = state.Mem
                            // LCD を切ってから VRAM を書く (gbfs は LCD 有効中の VRAM 書込を無視する)
                            mem <- Memory.write 0xFF40us 0uy mem
                            for a in 0x8000 .. 0x9FFF do
                                mem <- Memory.write (uint16 a) (readMemory image (uint16 a)) mem
                            let scy =
                                match args.Scy with
                                | Some v -> v
                                | None -> int (readMemory image 0xFF42us)
                            let lcdc = readMemory image 0xFF40us
                            let bgp = readMemory image 0xFF47us
                            // 未書込の I/O はモデル上オープンバス (0xFF) なので、そのまま描くと
                            // BG が画面外へずれる。書かれていないスクロールレジスタは 0 とみなす
                            // (実機のブート ROM もスクロール完了時は 0 を書いている)。
                            let scx =
                                let v = int (readMemory image 0xFF43us)
                                if v = 0xFF then 0 else v
                            mem <- Memory.write 0xFF40us lcdc mem
                            mem <- Memory.write 0xFF41us 0uy mem
                            mem <- Memory.write 0xFF42us (byte scy) mem
                            mem <- Memory.write 0xFF43us (byte scx) mem
                            mem <- Memory.write 0xFF47us bgp mem
                            printfn "[render] LCDC=$%02X BGP=$%02X SCY=$%02X%s SCX=$%02X" lcdc bgp (byte scy)
                                (if args.Scy.IsSome then " (上書き)" else "") scx
                            // gbfs の PPU は Decoder と同じ粒度 (4 T サイクル) で進める必要がある。
                            // まとめて 1 フレーム分渡すとモード遷移が 1 回しか起きず描画されない。
                            let mutable ppu = state.Ppu
                            for _ in 1 .. 2 * 70224 / 4 do
                                let p, m = Ppu.step 4 ppu mem
                                ppu <- p
                                mem <- m
                            let fb = ppu.FrameBuffer
                            printfn "[render] フレームバッファ: %d バイト (160x144)、階調の内訳 0=%d 1=%d 2=%d 3=%d"
                                fb.Length
                                (fb |> Array.filter (fun v -> v = 0uy) |> Array.length)
                                (fb |> Array.filter (fun v -> v = 1uy) |> Array.length)
                                (fb |> Array.filter (fun v -> v = 2uy) |> Array.length)
                                (fb |> Array.filter (fun v -> v = 3uy) |> Array.length)
                            printAscii fb
                            // 3. PNG へ
                            let outPath = repoPath (defaultArg args.Out "routed/bootrom_minimal_frame.png")
                            let rawPath = Path.ChangeExtension (outPath, ".raw")
                            writeRawRgb rawPath fb args.Scale
                            if toPng rawPath outPath args.Scale then
                                printfn "[render] 保存: %s (%dx%d, x%d)" outPath (160 * args.Scale) (144 * args.Scale) args.Scale
                                File.Delete rawPath
                                0
                            else
                                eprintfn "ERROR: ffmpeg での PNG 変換に失敗 (raw: %s)" rawPath
                                1
    with ex ->
        eprintfn "ERROR: %s" ex.Message
        2

exit exitCode
