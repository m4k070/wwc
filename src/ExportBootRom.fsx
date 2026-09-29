#r "bin/Debug/net8.0/WwHdl.dll"
// ExportBootRom.fsx — CA デモ用の最小ブート ROM プログラム (カートリッジ + program JSON) を生成する
//
// 段階 0 (2026-09-29): 「CA がブート ROM を走らせてロゴを VRAM に書き、LCD を有効化する」の検証用。
//
// 作るもの:
//   1. カートリッジ ROM (32 KB)
//        0x0100  trampoline `jp $0000`   ← RTL のリセット PC が 0x0100 のため (TODO 参照)
//        0x0104  ロゴ 48 バイト (1bpp)    ← ブート ROM がここから読んで VRAM へ展開する
//        0x0134  タイトル + 0x014D ヘッダチェックサム (0x0134-0x014C の和)
//        0x0200  プログラム本体 (マーカーを RAM に書いて spin)
//   2. program JSON (memory.bootRom = ビルド済みの routed/bootrom_minimal.bin)
//        expectMem: 展開後の VRAM 192 バイト + タイルマップ + I/O レジスタ + RAM のマーカー
//        (期待値はこのスクリプトが F# で独立に計算する。ROM のテーブルと一致することも検査する)
//
// 使い方:
//   dotnet fsi src/ExportBootRom.fsx [--logo-file <48byte.bin>] [--cycles N] [--out DIR]
//     --logo-file  ロゴ 48 バイトを差し替える (省略時は自作の "WWC" パターン)。
//                  実機の Nintendo ロゴを使う場合もここに 48 バイトを渡す (リポジトリには入れない)。
//
// プログラム名を sm83_full_*.json にしない理由: GPU スイート (memory-test.sh) の glob は
// sm83_full_*.json で、この ROM は 1 サイクル 46.6 ms (他は 16.5 ms) なので 1 本で +163 秒かかる。
// 通常のスイートを 78 秒に保ち、この 1 本は個別に回す:
//   nix develop -c ./wgpu-runner/target/release/wgpu-runner --memory routed/bootrom_minimal.json
// F# 側の仕様テスト (TestbenchTests) は bootrom_*.json も拾う (NetlistSim で 3.5 秒)。
//
// 前提: ブート ROM は bootrom/minimal.asm を rgbasm でビルドしておく:
//   nix shell nixpkgs#rgbds -c 'rgbasm -o /tmp/bootrom_minimal.o bootrom/minimal.asm \
//       && rgblink -x -o routed/bootrom_minimal.bin /tmp/bootrom_minimal.o'
open System
open System.IO
open System.Text.Json

let repoRoot = Path.GetFullPath (Path.Combine (__SOURCE_DIRECTORY__, ".."))

// --- ロゴ (48×8 の 1bpp ビットマップ = 48 バイト) -------------------------------

let private glyphs =
    dict [ 'W', [ "10001"; "10001"; "10001"; "10101"; "10101"; "11011"; "10001"; "00000" ]
           'C', [ "01110"; "10001"; "10000"; "10000"; "10000"; "10001"; "01110"; "00000" ] ]

/// "WWC" を 48×8 に中央寄せした自作パターン (Nintendo のロゴではない)
let private customLogo () : byte[] =
    let width = 48
    let rows = Array.zeroCreate 8
    let text = "WWC"
    let glyphWidth = 5
    let total = text.Length * glyphWidth + (text.Length - 1)
    let start = (width - total) / 2
    for row in 0 .. 7 do
        let mutable col = start
        for ch in text do
            let g = glyphs.[ch]
            for k in 0 .. glyphWidth - 1 do
                if g.[row].[k] = '1' then rows.[row] <- rows.[row] ||| (1 <<< (width - 1 - col))
                col <- col + 1
            col <- col + 1
    // 48 バイトへ詰める (バイト i = 行 i/6 の列 (i%6)*8 .. +7、bit 7 が左端)
    Array.init 48 (fun i ->
        let mutable b = 0
        for k in 0 .. 7 do
            if (rows.[i / 6] >>> (47 - ((i % 6) * 8 + k))) &&& 1 = 1 then b <- b ||| (1 <<< (7 - k))
        byte b)

/// ニブルの各ビットを 2 ビットに複製する (4 ピクセル → 8 ピクセル)
let private duplicateBits (nibble: int) : int =
    let mutable out = 0
    for k in 0 .. 3 do
        if (nibble &&& (1 <<< k)) <> 0 then out <- out ||| (0b11 <<< (2 * k))
    out

/// ロゴ 48 バイト (1bpp、6 バイト = 1 行) を VRAM の 2bpp タイル列へ展開する。
/// ニブル (4 ピクセル) を 8 ピクセルに拡大してタイル 1 枚の 1 行にする。
/// 48 バイト × 2 ニブル = 96 行 = 12 タイル × 8 行 → VRAM $8010-$80CF にちょうど収まる。
/// 展開の順序 (ニブル番号 n → タイル n%12+1 の n/12 行) は bootrom/minimal.asm と同じ。
let private expandLogo (logo: byte[]) : (int * byte) list =
    [ for i in 0 .. logo.Length - 1 do
        let hi = duplicateBits (int logo.[i] >>> 4)
        let lo = duplicateBits (int logo.[i] &&& 0x0F)
        for (nibble, value) in [ 2 * i, hi; 2 * i + 1, lo ] do
            let tile = nibble % 12 + 1
            let row = nibble / 12
            let addr = 0x8000 + tile * 16 + row * 2
            yield (addr, byte value)       // ロープレーン
            yield (addr + 1, byte value) ] // 1bpp なのでハイプレーンも同じ値

/// 1bpp の 1 バイト (8 px) を 2bpp の 4 バイトへ展開する (旧レイアウト。比較用に残す)
let private expandByte (h: byte) : byte[] =
    let hi = duplicateBits (int h >>> 4)
    let lo = duplicateBits (int h &&& 0x0F)
    [| byte hi; byte hi; byte lo; byte lo |]

// --- カートリッジ ROM ---------------------------------------------------------

let private buildCartridge (logo: byte[]) : byte[] =
    if logo.Length <> 48 then failwithf "ロゴは 48 バイト必要 (got %d)" logo.Length
    let rom = Array.zeroCreate 0x8000
    // 0x0000: ブート ROM 解除後に見えるカートリッジ側の入口
    rom.[0x0000] <- 0xC3uy   // jp $0200
    rom.[0x0001] <- 0x00uy
    rom.[0x0002] <- 0x02uy
    // 0x0100: リセット直後の入口 (RTL は PC=0x0100 でリセットする) → ブート ROM へ入る
    rom.[0x0100] <- 0xC3uy   // jp $0000
    rom.[0x0101] <- 0x00uy
    rom.[0x0102] <- 0x00uy
    Array.blit logo 0 rom 0x0104 logo.Length
    let title = Text.Encoding.ASCII.GetBytes "WWCBOOT"
    Array.blit title 0 rom 0x0134 title.Length
    let mutable sum = 0
    for a in 0x0134 .. 0x014C do
        sum <- (sum + int rom.[a]) &&& 0xFF
    rom.[0x014D] <- byte sum   // ヘッダチェックサム
    // 0x0200: プログラム本体 (マーカー → 解除の確認 → spin)
    let prog =
        [| 0x3Euy; 0x42uy;             // ld a,$42
           0xEAuy; 0x01uy; 0xC0uy;     // ld [$C001],a  ← ブート ROM から戻ってきた証拠
           0xFAuy; 0x00uy; 0x00uy;     // ld a,[$0000]  ← 解除後はカートリッジ (0xC3 = jp)
           0xEAuy; 0x02uy; 0xC0uy;     // ld [$C002],a
           0xFAuy; 0xE0uy; 0x00uy;     // ld a,[$00E0]  ← ブート ROM の表はもう見えない (0x00)
           0xEAuy; 0x03uy; 0xC0uy;     // ld [$C003],a
           0x18uy; 0xFEuy |]           // jr -2 (spin)
    Array.blit prog 0 rom 0x0200 prog.Length
    rom

// --- program JSON ------------------------------------------------------------

let private writeProgramJson (path: string) (romName: string) (bootRomName: string) (cycles: int)
    (expectMem: (uint16 * byte) list) : unit =
    use stream = new MemoryStream ()
    use w = new Utf8JsonWriter (stream, JsonWriterOptions (Indented = true))
    w.WriteStartObject ()
    w.WriteString ("circuit", "sm83_full")
    w.WriteString ("comment", "CA デモ: 自作の最小ブート ROM を 0x0000 に重ねて走らせる (段階 0)")
    w.WriteString ("meta", "sm83_full.meta.json")
    w.WriteString ("init", "sm83_full.bin")
    w.WriteStartObject "memory"
    w.WriteString ("rom", romName)
    w.WriteString ("bootRom", bootRomName)
    w.WriteNumber ("ramBase", 49152)
    w.WriteNumber ("ramSize", 8192)
    w.WriteEndObject ()
    w.WriteNumber ("rstPulses", 2)
    w.WriteNumber ("cycles", cycles)
    w.WriteNumber ("maxStepsPerPhase", 12000)
    w.WriteNumber ("checkInterval", 64)
    w.WriteStartObject "expectMem"
    for (addr, value) in expectMem do
        w.WriteNumber (sprintf "0x%04X" addr, int value)
    w.WriteEndObject ()
    w.WriteString ("golden", Path.GetFileNameWithoutExtension path + ".golden.json")
    w.WriteBoolean ("trace", false)
    w.WriteEndObject ()
    w.Flush ()
    File.WriteAllText (path, Text.Encoding.UTF8.GetString (stream.ToArray ()))

// --- main --------------------------------------------------------------------

type private Args = { LogoFile: string option; Cycles: int; OutDir: string option }

let private parseArgs (argv: string list) =
    let rec go args acc =
        match args with
        | "--logo-file" :: v :: rest -> go rest { acc with LogoFile = Some v }
        | "--cycles" :: v :: rest -> go rest { acc with Cycles = int v }
        | "--out" :: v :: rest -> go rest { acc with OutDir = Some v }
        | [] -> acc
        | other -> failwithf "不明な引数: %A" other
    go argv { LogoFile = None; Cycles = 2600; OutDir = None }

let exitCode =
    try
        let args = parseArgs (fsi.CommandLineArgs |> Array.toList |> List.tail)
        let outDir = Path.GetFullPath (Path.Combine (repoRoot, defaultArg args.OutDir "routed"))
        let bootRomPath = Path.Combine (outDir, "bootrom_minimal.bin")
        if not (File.Exists bootRomPath) then
            eprintfn "ERROR: %s がない。先に bootrom/minimal.asm を rgbasm でビルドする" bootRomPath
            2
        else
            let bootRom = File.ReadAllBytes bootRomPath
            if bootRom.Length <> 0x100 then
                eprintfn "ERROR: ブート ROM は 256 バイトのはず (got %d)。rgblink -x を使う" bootRom.Length
                2
            else
                // ROM 内の展開テーブル (0x00E0-0x00EF) と F# 側の計算が一致することを確認する
                let tableOk =
                    [ 0 .. 15 ] |> List.forall (fun n -> int bootRom.[0xE0 + n] = duplicateBits n)
                if not tableOk then
                    eprintfn "ERROR: ブート ROM の展開テーブル (0x00E0) が F# 側の計算と一致しない"
                    1
                else
                    let logo =
                        match args.LogoFile with
                        | Some f -> File.ReadAllBytes f
                        | None -> customLogo ()
                    let logoName = if args.LogoFile.IsSome then "外部ファイル" else "自作パターン (WWC)"
                    let cartridge = buildCartridge logo
                    let romPath = Path.Combine (outDir, "rom_bootrom_minimal.bin")
                    File.WriteAllBytes (romPath, cartridge)
                    // expectMem: VRAM の展開結果 + タイルマップ + I/O + RAM マーカー
                    let vramExpect =
                        expandLogo logo |> List.map (fun (a, b) -> uint16 a, b)
                    let tilemapExpect =
                        [ for i in 0 .. 11 do yield (uint16 (0x9904 + i), byte (i + 1)) ]
                    let ioExpect =
                        [ 0xFF40us, 0x91uy;   // LCDC (LCD on | BG on | tile data $8000)
                          0xFF42us, 0x64uy;   // SCY
                          0xFF47us, 0xFCuy ]  // BGP
                    let ramExpect =
                        [ 0xC001us, 0x42uy;   // カートリッジ側プログラムのマーカー
                          0xC002us, 0xC3uy;   // 解除後の 0x0000 = カートリッジ (jp のオペコード)
                          0xC003us, 0x00uy ]  // 解除後の 0x00E0 = ブート ROM の表は見えない
                    let expectMem = vramExpect @ tilemapExpect @ ioExpect @ ramExpect
                    let programPath = Path.Combine (outDir, "bootrom_minimal.json")
                    writeProgramJson programPath "rom_bootrom_minimal.bin" "bootrom_minimal.bin" args.Cycles expectMem
                    printfn "[bootrom] ブート ROM : %s (%d バイト, sha256 %s…)"
                        bootRomPath bootRom.Length (Convert.ToHexString (System.Security.Cryptography.SHA256.HashData bootRom).[0..15])
                    printfn "[bootrom] カートリッジ: %s (%d バイト)" romPath cartridge.Length
                    printfn "[bootrom] ロゴ: %s (48 バイト) → VRAM $8010-$80CF に 192 バイト展開" logoName
                    printfn "[bootrom] プログラム: %s (%d 周期, expectMem %d 件)"
                        programPath args.Cycles expectMem.Length
                    printfn "[bootrom] 次: dotnet fsi src/ExportGolden.fsx %s" programPath
                    0
    with ex ->
        eprintfn "ERROR: %s" ex.Message
        2

exit exitCode
