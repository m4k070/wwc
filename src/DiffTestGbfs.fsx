#r "bin/Debug/net8.0/WwHdl.dll"
#r "../../gbfs/src/gbfs.Lib/bin/Release/net8.0/gbfs.Lib.dll"
// DiffTestGbfs.fsx — sm83_full のネットリストを gbfs (../gbfs、F# 製 GB エミュレータ) の CPU と差分テストする
//
// 前提: dotnet build src/WwHdl.fsproj と
//       (cd ../gbfs && nix develop -c dotnet build src/gbfs.Lib/gbfs.Lib.fsproj -c Release)
//
// 使い方: dotnet fsi src/DiffTestGbfs.fsx [--variants N] [--seed S] [--only 0x86,0xCB46,...] [--interrupts N]
//
// 方式:
//   各 opcode (通常命令のうち比較可能なもの + CB 256 命令) について、乱数でレジスタを初期化する前置きと
//   テスト対象の命令を ROM に置き、gbfs (HALT まで) と NetlistSim + Testbench (固定周期) で実行して
//   A/F/B/C/D/E/H/L/SP/PC と WRAM (0xC000-0xDFFF) を比べる。
//
//   ROM (32KB) は空き領域をすべて HALT (0x76) で埋めるので、JP/CALL/JR/RET/RST の飛び先は必ず HALT に着地する。
//     0x0100: JP 0x0400
//     0x0400: 前置き LD SP,nn / LD BC,AF値; PUSH BC; POP AF / LD BC,nn / LD DE,nn / LD HL,nn / JP 0x0600
//     0x0600: テスト対象の命令 (直後は HALT)
//
//   比較から除外する命令: STOP、HALT (終端として使う)、未定義 opcode。LDH (E0/F0/E2/F2) の番地は HRAM に向ける
//
//   割込みシナリオ (--interrupts N、既定 200): IE / IF / A を乱数で設定し、EI / DI / NOP / HALT / INC A /
//   「IF に書いて要因を立てる」を乱数で並べ、最後に DI; XOR A; LDH (0x0F),A; HALT で要因を消して止める。
//   ベクタ 0x40/48/50/58/60 には INC B/C/D/E/H; RETI を置き、どの割込みが何回走ったかをレジスタで観測する。
//   gbfs は途中の HALT で止まらないよう固定命令数だけ step し、IF / IE / HRAM も比べる
//
// gbfs 自体は外部テスト ROM で検証されていないため、最初に手書き仕様テスト (routed/sm83_full_*.json) を
// gbfs でも実行し、参照モデルとしての最低限の正しさを確かめる。食い違いの最終判定は SM83 仕様 (Pan Docs) で行う。
open System
open System.IO
open WwHdl
open WwHdl.RoutedArtifact
open WwHdl.NetlistSim
open WwHdl.Testbench

let repoRoot = Path.GetFullPath (Path.Combine (__SOURCE_DIRECTORY__, ".."))

// --- 引数 -------------------------------------------------------------------

type Options =
    { Variants: int
      Seed: int
      Only: Set<int> option
      /// 一致したケースを routed/sm83_full_diff_<opcode>.json の仕様テストとして書き出す命令
      /// (期待値は gbfs の結果。SM83 仕様で判定済みの命令にだけ使う)
      Export: Set<int>
      /// 割込みシナリオの本数 (0 で省略)
      Interrupts: int }

let parseArgs (args: string list) : Result<Options, string> =
    let parseHex (s: string) =
        let t = s.Trim ()
        let digits = if t.StartsWith "0x" || t.StartsWith "0X" then t.Substring 2 else t
        Int32.Parse (digits, Globalization.NumberStyles.HexNumber)
    let rec go opts rest =
        match rest with
        | [] -> Ok opts
        | "--variants" :: n :: tail -> go { opts with Variants = int n } tail
        | "--seed" :: n :: tail -> go { opts with Seed = int n } tail
        | "--only" :: list :: tail -> go { opts with Only = Some (list.Split ',' |> Array.map parseHex |> Set.ofArray) } tail
        | "--export" :: list :: tail -> go { opts with Export = list.Split ',' |> Array.map parseHex |> Set.ofArray } tail
        | "--interrupts" :: n :: tail -> go { opts with Interrupts = int n } tail
        | other :: _ -> Error (sprintf "不明な引数: %s" other)
    go { Variants = 4; Seed = 20260917; Only = None; Export = Set.empty; Interrupts = 200 } args

// --- CPU 状態のスナップショット ------------------------------------------------

type Snapshot =
    { Regs: (string * int) list   // A F B C D E H L SP PC の順
      Wram: byte[]
      Hram: byte[]
      InterruptFlag: int
      InterruptEnable: int }

/// 比較対象のメモリ (WRAM / IF / HRAM / IE) を番地で読む。それ以外は None
let snapshotMemory (s: Snapshot) (addr: int) : int option =
    if addr >= 0xC000 && addr < 0xE000 then Some (int s.Wram.[addr - 0xC000])
    elif addr = 0xFF0F then Some s.InterruptFlag
    elif addr = 0xFFFF then Some s.InterruptEnable
    elif addr >= 0xFF80 && addr < 0xFFFF then Some (int s.Hram.[addr - 0xFF80])
    else None

let regNames = [ "A"; "F"; "B"; "C"; "D"; "E"; "H"; "L"; "SP"; "PC" ]

let diffSnapshots (expected: Snapshot) (actual: Snapshot) : string list =
    let regDiffs =
        List.zip expected.Regs actual.Regs
        |> List.choose (fun ((name, e), (_, a)) ->
            if e = a then None
            else
                let width = if name = "SP" || name = "PC" then 4 else 2
                Some (sprintf "%s: gbfs=0x%0*X netlist=0x%0*X" name width e width a))
    let wramDiffs =
        [ for i in 0 .. expected.Wram.Length - 1 do
            if expected.Wram.[i] <> actual.Wram.[i] then
                yield sprintf "mem[0x%04X]: gbfs=0x%02X netlist=0x%02X" (0xC000 + i) expected.Wram.[i] actual.Wram.[i] ]
    let ioDiffs =
        [ if expected.InterruptFlag <> actual.InterruptFlag then
              yield sprintf "IF: gbfs=0x%02X netlist=0x%02X" expected.InterruptFlag actual.InterruptFlag
          if expected.InterruptEnable <> actual.InterruptEnable then
              yield sprintf "IE: gbfs=0x%02X netlist=0x%02X" expected.InterruptEnable actual.InterruptEnable
          for i in 0 .. expected.Hram.Length - 1 do
              if expected.Hram.[i] <> actual.Hram.[i] then
                  yield sprintf "mem[0x%04X]: gbfs=0x%02X netlist=0x%02X" (0xFF80 + i) expected.Hram.[i] actual.Hram.[i] ]
    let memDiffs = wramDiffs @ ioDiffs
    let shownMem = memDiffs |> List.truncate 4
    let more = if memDiffs.Length > 4 then [ sprintf "... メモリ差分ほか %d 件" (memDiffs.Length - 4) ] else []
    regDiffs @ shownMem @ more

// --- gbfs ------------------------------------------------------------------

let gbfsMaxSteps = 1000

let gbfsSnapshot (s: gbfs.Lib.Decoder.CpuState) : Snapshot =
    let r8 reg = int (gbfs.Lib.Cpu.getRegisterValue (gbfs.Lib.Cpu.R8 reg) s.Regs)
    { Regs =
        [ "A", r8 gbfs.Lib.Cpu.Reg8.A; "F", r8 gbfs.Lib.Cpu.Reg8.F
          "B", r8 gbfs.Lib.Cpu.Reg8.B; "C", r8 gbfs.Lib.Cpu.Reg8.C
          "D", r8 gbfs.Lib.Cpu.Reg8.D; "E", r8 gbfs.Lib.Cpu.Reg8.E
          "H", r8 gbfs.Lib.Cpu.Reg8.H; "L", r8 gbfs.Lib.Cpu.Reg8.L
          "SP", int s.Regs.SP; "PC", int s.Regs.PC ]
      Wram = Array.copy s.Mem.Wram
      Hram = Array.copy s.Mem.Hram
      InterruptFlag = int s.Mem.Io.[0x0F]
      InterruptEnable = int s.Mem.Ie }

let loadGbfs (rom: byte[]) =
    gbfs.Lib.Decoder.createState () |> gbfs.Lib.Decoder.loadRomToState rom

/// HALT に着くまで実行する (命令単位の差分テスト用)
let runGbfs (rom: byte[]) : Snapshot * bool =
    let s = gbfs.Lib.Decoder.run gbfsMaxSteps (loadGbfs rom)
    gbfsSnapshot s, s.Halted

/// 固定命令数だけ step する (途中の HALT で止めない。HALT 中の step は割込み要因を待つ)
let runGbfsSteps (steps: int) (rom: byte[]) : Snapshot * bool =
    let s = Seq.fold (fun st _ -> gbfs.Lib.Decoder.step st) (loadGbfs rom) (seq { 1 .. steps })
    gbfsSnapshot s, s.Halted

// --- NetlistSim ------------------------------------------------------------

let loadFull () =
    let json = File.ReadAllText (Path.Combine (repoRoot, "verilog", "sm83_full.json"))
    let nl = match Pipeline.frontend json with Ok n -> n | Error e -> failwithf "frontend: %A" e
    let ports = match parseYosysPorts json with Ok p -> p | Error e -> failwith (RoutedArtifact.describeError e)
    let c = match compile nl with Ok c -> c | Error e -> failwith (describeSimError e)
    let bus = match resolveBus ports with Ok b -> b | Error e -> failwith (describeTestbenchError e)
    c, ports, bus

let netlistCycles = 150
let interruptNetlistCycles = 300
let interruptGbfsSteps = 400

let runNetlistCycles (cycles: int) (c, ports, bus) (rom: byte[]) : Result<Snapshot, string> =
    match run c ports bus (createMemory rom defaultMemoryConfig) 2 cycles with
    | Error e -> Error (describeTestbenchError e)
    | Ok result ->
        let o name = int result.FinalOutputs.[name]
        Ok { Regs =
               [ "A", o "a_out"; "F", o "f_out"; "B", o "b_out"; "C", o "c_out"; "D", o "d_out"
                 "E", o "e_out"; "H", o "h_out"; "L", o "l_out"; "SP", o "sp_out"; "PC", o "pc_out" ]
             Wram = result.Memory.Ram
             Hram = result.Memory.Hram
             InterruptFlag = int result.Memory.InterruptFlag
             InterruptEnable = int result.Memory.InterruptEnable }

let runNetlist circuit rom = runNetlistCycles netlistCycles circuit rom

// --- 参照モデル (gbfs) の確認: 手書き仕様テストを gbfs でも実行する ------------------

let portToReg =
    Map.ofList [ "a_out", "A"; "f_out", "F"; "b_out", "B"; "c_out", "C"; "d_out", "D"
                 "e_out", "E"; "h_out", "H"; "l_out", "L"; "sp_out", "SP"; "pc_out", "PC" ]

let checkGbfsAgainstSpecPrograms () : bool =
    printfn "=== 参照モデルの確認: routed/sm83_full_*.json (SM83 仕様から手で導いた期待値) を gbfs で実行 ==="
    let paths =
        Directory.GetFiles (Path.Combine (repoRoot, "routed"), "sm83_full_*.json")
        |> Array.filter (fun p -> not (p.EndsWith ".golden.json"))
        |> Array.sort
    let results =
        [ for path in paths do
            match parseProgram path (File.ReadAllText path) |> Result.bind (fun p -> loadRom p.Rom |> Result.map (fun r -> p, r)) with
            | Error e -> yield Path.GetFileNameWithoutExtension path, [ describeTestbenchError e ]
            | Ok (program, rom) ->
                let padded = Array.append rom (Array.zeroCreate (max 0 (0x8000 - rom.Length)))
                // 途中に HALT を持つプログラム (割込み) もあるので固定命令数だけ進める。どれも最後は要因のない HALT で止まる
                let snapshot, halted = runGbfsSteps interruptGbfsSteps padded
                let regs = Map.ofList snapshot.Regs
                let problems =
                    [ if not halted then yield "HALT に到達しない"
                      for KeyValue (port, expected) in program.Expect do
                          let got = regs.[portToReg.[port]]
                          if uint64 got <> expected then yield sprintf "%s: expected 0x%X gbfs 0x%X" port expected got
                      for KeyValue (addr, expected) in program.ExpectMem do
                          match snapshotMemory snapshot (int addr) with
                          | None -> yield sprintf "mem[0x%04X]: 比較対象外の番地" addr
                          | Some got when got <> int expected -> yield sprintf "mem[0x%04X]: expected 0x%02X gbfs 0x%02X" addr expected got
                          | Some _ -> () ]
                yield program.ProgramName, problems ]
    for (name, problems) in results do
        if problems.IsEmpty then printfn "  OK    %s" name
        else printfn "  FAIL  %s: %s" name (String.concat "; " problems)
    let ok = results |> List.forall (snd >> List.isEmpty)
    printfn "  → gbfs は仕様テスト %d/%d 本に合格\n" (results |> List.filter (snd >> List.isEmpty) |> List.length) results.Length
    ok

// --- テストケース生成 ---------------------------------------------------------

let excludedOpcodes =
    set [ 0x10; 0x76; 0xCB                                                   // STOP, HALT (終端), CB は別扱い
          0xD3; 0xDB; 0xDD; 0xE3; 0xE4; 0xEB; 0xEC; 0xED; 0xF4; 0xFC; 0xFD ] // 未定義

let immediate8 =
    set [ 0x06; 0x0E; 0x16; 0x1E; 0x26; 0x2E; 0x36; 0x3E
          0xC6; 0xCE; 0xD6; 0xDE; 0xE6; 0xEE; 0xF6; 0xFE; 0xE8; 0xF8 ]
let relativeJumps = set [ 0x18; 0x20; 0x28; 0x30; 0x38 ]
let loadImmediate16 = set [ 0x01; 0x11; 0x21; 0x31 ]
let jumpTargets16 = set [ 0xC2; 0xC3; 0xC4; 0xCA; 0xCC; 0xCD; 0xD2; 0xD4; 0xDA; 0xDC ]
let memoryAddress16 = set [ 0x08; 0xEA; 0xFA ]
/// BC / DE / HL が指すメモリを読み書きする命令 (ポインタを WRAM に向ける)
let dereferences =
    set [ 0x02; 0x0A; 0x12; 0x1A; 0x22; 0x2A; 0x32; 0x3A; 0x34; 0x35; 0x36
          0x46; 0x4E; 0x56; 0x5E; 0x66; 0x6E; 0x7E; 0x70; 0x71; 0x72; 0x73; 0x74; 0x75; 0x77
          0x86; 0x8E; 0x96; 0x9E; 0xA6; 0xAE; 0xB6; 0xBE ]

type TestCase =
    { Opcode: int          // 通常命令は 0x00-0xFF、CB 命令は 0xCB00-0xCBFF
      Instruction: byte[]
      Preset: string
      Rom: byte[] }

/// フラグの境界になりやすい値。純粋な乱数では結果 0 やニブル桁上がりがほとんど出ないため混ぜる
let edgeBytes = [| 0x00; 0x01; 0x0F; 0x10; 0x7F; 0x80; 0xFE; 0xFF |]

let buildCase (rng: Random) (opcode: int) : TestCase =
    let byteR () = if rng.Next 3 = 0 then edgeBytes.[rng.Next edgeBytes.Length] else rng.Next 256
    let wramPointer () = rng.Next (0xC010, 0xDFE0)
    let romTarget () = rng.Next (0x0800, 0x8000)
    let isCb = opcode >= 0xCB00
    let cbTouchesHl = isCb && (opcode &&& 0x07) = 0x06
    let pointsToWram = isCb && cbTouchesHl || dereferences.Contains opcode || rng.Next 2 = 0
    let sp = rng.Next (0xC100, 0xDF00)
    let a, f = byteR (), byteR () &&& 0xF0
    let bc, de = (if pointsToWram then wramPointer (), wramPointer () else rng.Next 65536, rng.Next 65536)
    // LDH (C) 系は C を HRAM (0xFF80-0xFFFE) に向ける。IF / IE に書くと HALT が要因で起き続けるため避ける
    let bc = if opcode = 0xE2 || opcode = 0xF2 then (bc &&& 0xFF00) ||| rng.Next (0x80, 0xFF) else bc
    let hl = if opcode = 0xE9 then romTarget () elif pointsToWram then wramPointer () else rng.Next 65536
    let lo (v: int) = byte (v &&& 0xFF)
    let hi (v: int) = byte ((v >>> 8) &&& 0xFF)
    let instruction =
        if isCb then [| 0xCBuy; byte (opcode &&& 0xFF) |]
        elif immediate8.Contains opcode then [| byte opcode; byte (byteR ()) |]
        elif opcode = 0xE0 || opcode = 0xF0 then [| byte opcode; byte (rng.Next (0x80, 0xFF)) |]   // LDH (n): HRAM
        elif relativeJumps.Contains opcode then
            // 命令自身 (0x0600-0x0601) に戻る JR -2 / JR -1 は無限ループになるので避ける
            let offsets = [| for e in -128 .. 127 do if e <> -2 && e <> -1 then yield e |]
            [| byte opcode; byte (sbyte offsets.[rng.Next offsets.Length]) |]
        elif loadImmediate16.Contains opcode then let v = rng.Next 65536 in [| byte opcode; lo v; hi v |]
        elif jumpTargets16.Contains opcode then let v = romTarget () in [| byte opcode; lo v; hi v |]
        elif memoryAddress16.Contains opcode then let v = wramPointer () in [| byte opcode; lo v; hi v |]
        else [| byte opcode |]
    let prelude =
        [| 0x31uy; lo sp; hi sp                        // LD SP,sp
           0x01uy; byte f; byte a; 0xC5uy; 0xF1uy      // LD BC,a:f; PUSH BC; POP AF
           0x01uy; lo bc; hi bc                        // LD BC,bc
           0x11uy; lo de; hi de                        // LD DE,de
           0x21uy; lo hl; hi hl                        // LD HL,hl
           0xC3uy; 0x00uy; 0x06uy |]                   // JP 0x0600
    let rom = Array.create 0x8000 0x76uy
    Array.blit [| 0xC3uy; 0x00uy; 0x04uy |] 0 rom 0x0100 3
    Array.blit prelude 0 rom 0x0400 prelude.Length
    Array.blit instruction 0 rom 0x0600 instruction.Length
    { Opcode = opcode
      Instruction = instruction
      Preset = sprintf "A=%02X F=%02X BC=%04X DE=%04X HL=%04X SP=%04X" a f bc de hl sp
      Rom = rom }

let opcodeLabel (opcode: int) = if opcode >= 0xCB00 then sprintf "CB %02X" (opcode &&& 0xFF) else sprintf "%02X" opcode

/// 一致したケースを TestbenchTest が読む仕様テスト (routed/sm83_full_diff_<opcode>.json) として保存する。
/// ROM は HALT 以外の領域 (0x0100-0x0102、前置き、命令) だけを持てば同じ動作になるが、
/// 飛び先の HALT が必要なので 32KB をそのまま保存する (routed/ に置く他の ROM と同じ扱い)
let exportSpecProgram (case: TestCase) (expected: Snapshot) =
    let label = (opcodeLabel case.Opcode).Replace(" ", "").ToLowerInvariant()
    let name = sprintf "sm83_full_diff_%s" label
    let routed = Path.Combine (repoRoot, "routed")
    File.WriteAllBytes (Path.Combine (routed, sprintf "rom_%s.bin" name), case.Rom)
    let portOf = Map.ofList [ "A", "a_out"; "F", "f_out"; "B", "b_out"; "C", "c_out"; "D", "d_out"
                              "E", "e_out"; "H", "h_out"; "L", "l_out"; "SP", "sp_out"; "PC", "pc_out" ]
    let expect = expected.Regs |> List.map (fun (reg, v) -> sprintf "\"%s\": %d" portOf.[reg] v) |> String.concat ", "
    let expectMem =
        [ for i in 0 .. expected.Wram.Length - 1 do
            if expected.Wram.[i] <> 0uy then yield sprintf "\"0x%04X\": %d" (0xC000 + i) expected.Wram.[i] ]
        |> String.concat ", "
    let bytes = case.Instruction |> Array.map (sprintf "%02X") |> String.concat " "
    let json =
        String.concat "\n"
            [ "{"
              "  \"circuit\": \"sm83_full\","
              sprintf "  \"comment\": \"DiffTestGbfs.fsx で生成: 命令 %s、初期値 %s。期待値は gbfs の結果 (SM83 仕様で判定済みの命令)\"," bytes case.Preset
              "  \"meta\": \"sm83_full.meta.json\","
              "  \"init\": \"sm83_full.bin\","
              sprintf "  \"memory\": { \"rom\": \"rom_%s.bin\", \"ramBase\": 49152, \"ramSize\": 8192 }," name
              "  \"rstPulses\": 2,"
              sprintf "  \"cycles\": %d," netlistCycles
              "  \"maxStepsPerPhase\": 40000,"
              "  \"checkInterval\": 256,"
              sprintf "  \"expect\": { %s }," expect
              sprintf "  \"expectMem\": { %s }," expectMem
              sprintf "  \"golden\": \"%s.golden.json\"," name
              "  \"trace\": true"
              "}"
              "" ]
    File.WriteAllText (Path.Combine (routed, name + ".json"), json)
    printfn "  exported routed/%s.json" name

// --- 実行 ---------------------------------------------------------------------

// --- 割込みシナリオ -------------------------------------------------------------

/// ベクタごとに「対応するレジスタを +1 して RETI」。どの割込みが何回走ったかをレジスタで観測する
let interruptHandlers =
    [ 0x40, [| 0x04uy; 0xD9uy |]   // INC B; RETI
      0x48, [| 0x0Cuy; 0xD9uy |]   // INC C; RETI
      0x50, [| 0x14uy; 0xD9uy |]   // INC D; RETI
      0x58, [| 0x1Cuy; 0xD9uy |]   // INC E; RETI
      0x60, [| 0x24uy; 0xD9uy |] ] // INC H; RETI

let buildInterruptCase (rng: Random) (index: int) : TestCase =
    let sp = rng.Next (0xC100, 0xDF00)
    // IE の上位 3bit は irq に効かないことも確かめる
    let ie = rng.Next 32 ||| (if rng.Next 4 = 0 then 0xE0 else 0)
    let iflag = rng.Next 32
    let a = rng.Next 256
    let pick () =
        match rng.Next 6 with
        | 0 -> [| 0xFBuy |]                                         // EI
        | 1 -> [| 0xF3uy |]                                         // DI
        | 2 -> [| 0x00uy |]                                         // NOP
        | 3 -> [| 0x76uy |]                                         // HALT
        | 4 -> [| 0x3Cuy |]                                         // INC A
        | _ -> [| 0x3Euy; byte (rng.Next 32); 0xE0uy; 0x0Fuy |]     // LD A,n; LDH (0x0F),A (要因を立てる)
    let sequence = Array.concat [ for _ in 1 .. rng.Next (1, 7) -> pick () ]
    // 要因を消してから止める。要因が残ると IME=0 でも HALT がすぐ起きて先へ進んでしまう
    let terminator = [| 0xF3uy; 0xAFuy; 0xE0uy; 0x0Fuy; 0x76uy |]   // DI; XOR A; LDH (0x0F),A; HALT
    let lo (v: int) = byte (v &&& 0xFF)
    let hi (v: int) = byte ((v >>> 8) &&& 0xFF)
    let prelude =
        [| 0x31uy; lo sp; hi sp                       // LD SP,sp
           0x3Euy; byte ie; 0xE0uy; 0xFFuy            // LD A,ie; LDH (0xFF),A
           0x3Euy; byte iflag; 0xE0uy; 0x0Fuy         // LD A,if; LDH (0x0F),A
           0x3Euy; byte a                             // LD A,a
           0xC3uy; 0x00uy; 0x06uy |]                  // JP 0x0600
    let rom = Array.create 0x8000 0x76uy
    for (vector, handler) in interruptHandlers do
        Array.blit handler 0 rom vector handler.Length
    Array.blit [| 0xC3uy; 0x00uy; 0x04uy |] 0 rom 0x0100 3
    Array.blit prelude 0 rom 0x0400 prelude.Length
    let body = Array.append sequence terminator
    Array.blit body 0 rom 0x0600 body.Length
    { Opcode = index
      Instruction = sequence
      Preset = sprintf "IE=%02X IF=%02X A=%02X SP=%04X" ie iflag a sp
      Rom = rom }

/// 命令ごとの差分テスト。食い違いがなければ true
let runOpcodeDiffTest (opts: Options) circuit (rng: Random) : bool =
    let opcodes =
        [ for op in 0x00 .. 0xFF do if not (excludedOpcodes.Contains op) then yield op
          for op in 0x00 .. 0xFF do yield 0xCB00 + op ]
        |> List.filter (fun op -> opts.Only |> Option.forall (fun only -> only.Contains op))
    printfn "=== 差分テスト: %d 命令 × %d パターン (seed=%d)、netlist %d 周期 ===" opcodes.Length opts.Variants opts.Seed netlistCycles
    let sw = Diagnostics.Stopwatch.StartNew ()
    let failures =
        [ for opcode in opcodes do
            let cases = [ for _ in 1 .. opts.Variants -> buildCase rng opcode ]
            let failing =
                cases
                |> List.choose (fun case ->
                    let expected, halted = runGbfs case.Rom
                    match runNetlist circuit case.Rom with
                    | Error msg -> Some (case, [ sprintf "netlist error: %s" msg ])
                    | Ok actual ->
                        let diffs = diffSnapshots expected actual
                        let diffs = if halted then diffs else "gbfs が HALT に到達しない" :: diffs
                        if diffs.IsEmpty then None else Some (case, diffs))
            if opts.Export.Contains opcode && failing.IsEmpty then
                // 全パターン一致した命令だけ書き出す (最初のパターンを使う)
                exportSpecProgram cases.Head (fst (runGbfs cases.Head.Rom))
            if not failing.IsEmpty then
                yield opcode, failing.Length, List.head failing ]
    printfn "所要 %.1f 秒\n" sw.Elapsed.TotalSeconds
    if failures.IsEmpty then
        printfn "全 %d 命令が gbfs と一致\n" opcodes.Length
        true
    else
        printfn "食い違い: %d / %d 命令" failures.Length opcodes.Length
        for (opcode, count, (case, diffs)) in failures do
            let bytes = case.Instruction |> Array.map (sprintf "%02X") |> String.concat " "
            printfn "  [%s] %d/%d パターン  命令 %s  初期値 %s" (opcodeLabel opcode) count opts.Variants bytes case.Preset
            for d in diffs do
                printfn "        %s" d
        let failedLabels = failures |> List.map (fun (op, _, _) -> opcodeLabel op) |> String.concat ", "
        printfn "\n食い違った命令: %s\n" failedLabels
        false

/// 割込みシナリオの差分テスト。食い違いがなければ true
let runInterruptDiffTest (opts: Options) circuit (rng: Random) : bool =
    printfn "=== 割込みシナリオ: %d 本 (gbfs %d 命令、netlist %d 周期) ===" opts.Interrupts interruptGbfsSteps interruptNetlistCycles
    let sw = Diagnostics.Stopwatch.StartNew ()
    let failures =
        [ for index in 1 .. opts.Interrupts do
            let case = buildInterruptCase rng index
            let expected, _ = runGbfsSteps interruptGbfsSteps case.Rom
            match runNetlistCycles interruptNetlistCycles circuit case.Rom with
            | Error msg -> yield case, [ sprintf "netlist error: %s" msg ]
            | Ok actual ->
                let diffs = diffSnapshots expected actual
                if not diffs.IsEmpty then yield case, diffs ]
    printfn "所要 %.1f 秒\n" sw.Elapsed.TotalSeconds
    if failures.IsEmpty then
        printfn "全 %d シナリオが gbfs と一致" opts.Interrupts
        true
    else
        let withEi = failures |> List.filter (fun (case, _) -> case.Instruction |> Array.contains 0xFBuy) |> List.length
        printfn "食い違い: %d / %d シナリオ (うち命令列に EI を含むもの %d)" failures.Length opts.Interrupts withEi
        for (case, diffs) in failures |> List.truncate 12 do
            let bytes = case.Instruction |> Array.map (sprintf "%02X") |> String.concat " "
            printfn "  [#%d] 命令列 %s  初期値 %s" case.Opcode bytes case.Preset
            for d in diffs do
                printfn "        %s" d
        if failures.Length > 12 then printfn "  ... ほか %d シナリオ" (failures.Length - 12)
        false

let runDiffTest (opts: Options) : int =
    let gbfsOk = checkGbfsAgainstSpecPrograms ()
    if not gbfsOk then
        printfn "WARN: gbfs が手書き仕様テストに合格しない。以降の差分は gbfs 側の誤りも疑うこと\n"
    let circuit = loadFull ()
    let rng = Random opts.Seed
    let opcodesOk = runOpcodeDiffTest opts circuit rng
    let interruptsOk = opts.Interrupts = 0 || runInterruptDiffTest opts circuit (Random (opts.Seed + 1))
    if opcodesOk && interruptsOk then 0 else 1

let exitCode =
    match parseArgs (fsi.CommandLineArgs |> Array.toList |> List.tail) with
    | Error msg ->
        eprintfn "ERROR: %s" msg
        eprintfn "使い方: dotnet fsi src/DiffTestGbfs.fsx [--variants N] [--seed S] [--only 0x86,0xCB46] [--interrupts N]"
        2
    | Ok opts -> runDiffTest opts

exit exitCode
