#r "bin/Release/net8.0/WwHdl.dll"
#r "/home/makoto/work/gbfs/src/gbfs.Lib/bin/Release/net8.0/gbfs.Lib.dll"
#load "/home/makoto/work/gbfs/tools/TestRomVerdict.fs"
// CoSimGbfs.fsx — sm83_full の RTL (NetlistSim) を gbfs の周辺回路につないで公開テスト ROM を流す
//
// 目的 (信頼の連鎖): 公開テスト ROM → RTL (NetlistSim + gbfs の周辺回路)。RTL ≡ CA は golden 照合で確認済み。
//
// 前提:
//   dotnet build src/WwHdl.fsproj -c Release        (NetlistSim は Debug だと遅いので Release DLL を使う)
//   (cd /home/makoto/work/gbfs && nix develop -c dotnet build src/gbfs.Lib/gbfs.Lib.fsproj -c Release)
//
// 使い方:
//   dotnet fsi src/CoSimGbfs.fsx [options] <rom.gb>...
//   options:
//     --max-cycles N     RTL の周期上限 (既定 40,000,000)。到達したら TIMEOUT
//     --tsv PATH         結果を TSV に 1 行ずつ追記する (ファイルがなければヘッダも書く)
//     --gbfs-tsv PATH    gbfs 単体の結果 TSV (比較列に使う。既定は results/gbfs-16dafd2-blargg.tsv)
//     --lockstep         命令境界ごとに gbfs の CPU (参照) と レジスタ・WRAM・HRAM を比べる
//     --max-mismatches N ロックステップの食い違いを何件まで詳しく出すか (既定 20)
//     --progress N       N 周期ごとに進捗を出す (既定 200,000。0 で出さない)
//     --strict-phases    DESIGN-VERIFY.md §5.2 どおり 1 周期 3 回 settle する (既定は 2 回。下記)
//     --trace FROM:TO    その周期範囲で 1 周期ごとのバス・レジスタ・phase を出す (開始時に PC 周辺のメモリも出す)
//
// 1 周期 (DESIGN-VERIFY.md §5.2 の単相手順。メモリは gbfs の Memory.read / Memory.write):
//   出力 addr/mem_read/mem_write/data_out/int_ack を読む → 書込 (シリアル観測を含む) → int_ack で IF を下ろす
//   → 読出 → irq = IE & IF & 0x1F → data_in/irq を書いて settle → clk=1 で settle
//   → 周辺回路を 4 T サイクル進める (Timer.step 4、Ppu.step 4、Joypad.sync。APU は省略)
//
//   sm83_full のバス出力はすべて posedge でラッチされるレジスタ (output reg) なので、§5.2 の手順 1 (clk=0 で settle)
//   の後に読む値は直前の clk=1 settle 後の値と同じ。既定では「clk=0 と data_in/irq を 1 回の apply で書く」ことで
//   settle を 1 周期 2 回に減らす (DFF が取り込む D は同じなので結果は変わらない。--strict-phases で確認できる)
//
// 仮定: 1 周期 (RTL の 1 バスサイクル) = 4 T サイクル。sm83_full は内部処理の M サイクルを持たず、
//   フェッチに 2 周期かかるなど実機の M サイクル数とは一致しないので、周辺回路から見た時間はサイクル精度ではない。
//
// シリアル: SC (0xFF02) に 0x81 が書かれたら SB (0xFF01) を 1 文字として拾い、相手なしの転送完了として
//   SB=0xFF、SC の bit7 を下ろし、IF の bit3 を立てる (gbfs の tools/run_test_roms.fsx と同じ扱い)。
//
// ロックステップ (--lockstep):
//   命令境界 = 「直前の状態が PHASE_FETCH で、この posedge で mem_read=1 かつ int_ack=0 かつ cb_prefix=0」。
//   phase は yosys が one-hot に再符号化しているので、リセット直後に立っているビットを PHASE_FETCH と学習する。
//   割込み受付 = 「直前が PHASE_FETCH で int_ack≠0」。
//   境界 (命令開始 / 割込み受付) ごとに、参照 CPU (gbfs Decoder) のレジスタと RTL のレジスタ、
//   参照が書いた WRAM / HRAM / IE と RTL 側メモリを比べてから、RTL 側メモリのコピーの上で参照を 1 step 進める。
//   参照はメモリを RTL 側からもらうので、周辺回路の時間のずれは入らない。ただし I/O を読む命令は
//   読む瞬間が 1〜2 周期ずれるので食い違うことがある (その場合は「I/O 読出あり」と表示する)。
//   食い違ったら参照を RTL の状態に合わせ直して続ける。
open System
open System.IO
open System.Text.Json
open WwHdl
open WwHdl.Netlist
open WwHdl.RoutedArtifact
open WwHdl.NetlistSim
open WwHdl.Testbench
open gbfs.Lib
open TestRomVerdict

let repoRoot = Path.GetFullPath (Path.Combine (__SOURCE_DIRECTORY__, ".."))

// --- 定数 --------------------------------------------------------------------

/// DMG の CPU クロック (T サイクル / 秒)
let CpuHz = 4194304L
/// 仮定: RTL の 1 周期 = 4 T サイクル
let TCyclesPerRtlCycle = 4
let DefaultMaxCycles = 40_000_000L
let DefaultProgressInterval = 200_000L
let DefaultGbfsTsv = "/home/makoto/work/gb-test-roms/results/gbfs-16dafd2-blargg.tsv"
/// 判定関数を呼ぶ間隔 (周期)。毎周期シリアル文字列を走査しないため
let JudgeIntervalCycles = 4096L
/// blargg の終了語を見つけた後、後続の文字を拾うために追加で回す周期 (gbfs ランナーの CpuHz/4 T は RTL では長すぎる)
let BlarggGraceCycles = 20_000L
let ResetPulses = 2

let SerialDataAddr = 0xFF01us
let SerialControlAddr = 0xFF02us
let InterruptFlagAddr = 0xFF0Fus
let InterruptEnableAddr = 0xFFFFus
let SerialInterruptBit = 0x08uy
let SerialNoPartnerByte = 0xFFuy
let IoBase = 0xFF00
let HramBase = 0xFF80

// --- 引数 --------------------------------------------------------------------

type Options =
    { Roms: string list
      MaxCycles: int64
      TsvPath: string option
      GbfsTsvPath: string
      Lockstep: bool
      MaxMismatches: int
      ProgressInterval: int64
      StrictPhases: bool
      /// この周期範囲 (両端を含む) で 1 周期ごとのバスとレジスタを出す
      Trace: (int64 * int64) option }

let parseArgs (args: string list) : Result<Options, string> =
    let rec go opts rest =
        match rest with
        | [] -> Ok { opts with Roms = List.rev opts.Roms }
        | "--" :: tail -> go opts tail
        | "--max-cycles" :: n :: tail -> go { opts with MaxCycles = int64 n } tail
        | "--tsv" :: p :: tail -> go { opts with TsvPath = Some p } tail
        | "--gbfs-tsv" :: p :: tail -> go { opts with GbfsTsvPath = p } tail
        | "--lockstep" :: tail -> go { opts with Lockstep = true } tail
        | "--max-mismatches" :: n :: tail -> go { opts with MaxMismatches = int n } tail
        | "--progress" :: n :: tail -> go { opts with ProgressInterval = int64 n } tail
        | "--strict-phases" :: tail -> go { opts with StrictPhases = true } tail
        | "--trace" :: range :: tail ->
            match range.Split ':' with
            | [| a; b |] -> go { opts with Trace = Some (int64 a, int64 b) } tail
            | _ -> Error (sprintf "--trace は FROM:TO (周期) で指定する: %s" range)
        | a :: _ when a.StartsWith "--" -> Error (sprintf "不明な引数: %s" a)
        | p :: tail -> go { opts with Roms = p :: opts.Roms } tail
    go { Roms = []; MaxCycles = DefaultMaxCycles; TsvPath = None; GbfsTsvPath = DefaultGbfsTsv
         Lockstep = false; MaxMismatches = 20; ProgressInterval = DefaultProgressInterval; StrictPhases = false; Trace = None } args

// --- 回路 --------------------------------------------------------------------

/// ロックステップで使う内部レジスタ
type InternalProbes =
    { /// phase (one-hot、14 ビット)
      Phase: int[]
      CbPrefix: int
      Ime: int
      EiDelay: int
      Halted: int
      HaltBug: int }

type Circuit =
    { Compiled: CompiledNetlist
      Bus: BusPorts
      Interrupt: InterruptPorts
      /// A F B C D E H L SP PC の順の出力ポート
      RegisterPorts: (string * YosysPortBits) list
      Probes: InternalProbes }

/// yosys JSON の netnames から名前 → ビット (NetId) を引く (定数ビットは -1)
let parseNetnames (json: string) : Map<string, int[]> =
    use doc = JsonDocument.Parse json
    let modules = doc.RootElement.GetProperty "modules"
    let top = modules.EnumerateObject () |> Seq.head
    top.Value.GetProperty("netnames").EnumerateObject ()
    |> Seq.map (fun p ->
        let bits =
            p.Value.GetProperty("bits").EnumerateArray ()
            |> Seq.map (fun b -> if b.ValueKind = JsonValueKind.Number then b.GetInt32 () else -1)
            |> Array.ofSeq
        p.Name, bits)
    |> Map.ofSeq

let resolveProbes (c: CompiledNetlist) (netnames: Map<string, int[]>) : Result<InternalProbes, string> =
    let indexOf (name: string) (bit: int) =
        match Map.tryFind (NetId bit) c.NetIndex with
        | Some i -> Ok i
        | None -> Error (sprintf "内部ネット %s (bit %d) が NetlistSim にない" name bit)
    let bitsOf (name: string) =
        match Map.tryFind name netnames with
        | None -> Error (sprintf "netnames に %s がない" name)
        | Some bits when bits |> Array.exists (fun b -> b < 0) -> Error (sprintf "%s に定数ビットがある" name)
        | Some bits ->
            bits
            |> Array.fold (fun acc b -> acc |> Result.bind (fun xs -> indexOf name b |> Result.map (fun i -> Array.append xs [| i |]))) (Ok [||])
    let single (name: string) =
        bitsOf name
        |> Result.bind (fun xs -> if xs.Length = 1 then Ok xs.[0] else Error (sprintf "%s は 1 ビットのはず (%d)" name xs.Length))
    match bitsOf "phase", single "cb_prefix", single "ime", single "ei_delay", single "halted", single "halt_bug" with
    | Ok phase, Ok cb, Ok ime, Ok ei, Ok halted, Ok haltBug ->
        Ok { Phase = phase; CbPrefix = cb; Ime = ime; EiDelay = ei; Halted = halted; HaltBug = haltBug }
    | Error e, _, _, _, _, _ | _, Error e, _, _, _, _ | _, _, Error e, _, _, _
    | _, _, _, Error e, _, _ | _, _, _, _, Error e, _ | _, _, _, _, _, Error e -> Error e

let loadCircuit () : Result<Circuit, string> =
    let json = File.ReadAllText (Path.Combine (repoRoot, "verilog", "sm83_full.json"))
    let frontend = Pipeline.frontend json |> Result.mapError (sprintf "frontend: %A")
    let ports = parseYosysPorts json |> Result.mapError RoutedArtifact.describeError
    match frontend, ports with
    | Error e, _ | _, Error e -> Error e
    | Ok nl, Ok ports ->
        match compile nl with
        | Error e -> Error (describeSimError e)
        | Ok compiled ->
            match resolveBus ports with
            | Error e -> Error (describeTestbenchError e)
            | Ok { Interrupt = None } -> Error "sm83_full に irq / int_ack がない"
            | Ok ({ Interrupt = Some interrupt } as bus) ->
                let regNames = [ "A", "a_out"; "F", "f_out"; "B", "b_out"; "C", "c_out"; "D", "d_out"
                                 "E", "e_out"; "H", "h_out"; "L", "l_out"; "SP", "sp_out"; "PC", "pc_out" ]
                let regPorts =
                    regNames |> List.choose (fun (reg, port) -> ports |> List.tryFind (fun p -> p.Name = port) |> Option.map (fun p -> reg, p))
                if regPorts.Length <> regNames.Length then Error "レジスタ出力ポートが足りない"
                else
                    resolveProbes compiled (parseNetnames json)
                    |> Result.map (fun probes ->
                        { Compiled = compiled; Bus = bus; Interrupt = interrupt; RegisterPorts = regPorts; Probes = probes })

// --- NetlistSim の操作 ---------------------------------------------------------

let orFail (r: Result<'a, SimError>) : 'a =
    match r with
    | Ok v -> v
    | Error e -> failwith (describeSimError e)

let applyWrites (c: CompiledNetlist) (writes: (YosysPortBits * uint64) list) (s: SimState) : SimState =
    let inputs =
        writes
        |> List.map (fun (port, value) -> portInputs port value)
        |> List.fold (fun acc m -> Map.fold (fun a k v -> Map.add k v a) acc m) Map.empty
    apply c inputs s |> orFail

let readValue (c: CompiledNetlist) (s: SimState) (port: YosysPortBits) : uint64 =
    readPort c s port |> orFail

/// 直前の clk=1 settle 後にラッチされているバス出力
type BusRequest =
    { Addr: uint16
      IsRead: bool
      IsWrite: bool
      DataOut: byte
      IntAck: byte }

let readBusRequest (circuit: Circuit) (s: SimState) : BusRequest =
    let v port = readValue circuit.Compiled s port
    { Addr = uint16 (v circuit.Bus.Addr)
      IsRead = v circuit.Bus.MemRead = 1UL
      IsWrite = v circuit.Bus.MemWrite = 1UL
      DataOut = byte (v circuit.Bus.DataOut)
      IntAck = byte (v circuit.Interrupt.IntAck) }

let readRegisters (circuit: Circuit) (s: SimState) : (string * int) list =
    circuit.RegisterPorts |> List.map (fun (reg, port) -> reg, int (readValue circuit.Compiled s port))

// --- 周辺回路 (gbfs) -------------------------------------------------------------

/// gbfs の CPU 以外の部品。Memory の配列は gbfs 同様に破壊的に更新される
type Peripherals =
    { Mem: Memory.MemoryBus
      Ppu: Ppu.PpuState
      Timer: Timer.TimerState
      Joypad: Joypad.JoypadState }

let createPeripherals (rom: byte[]) : Peripherals =
    // gbfs 単体と同じ初期状態 (Decoder.createState + loadRomToState) から周辺回路だけを取り出す
    let s = Decoder.createState () |> Decoder.loadRomToState rom
    { Mem = s.Mem; Ppu = s.Ppu; Timer = s.Timer; Joypad = s.Joypad }

/// T サイクル分だけ周辺回路を進める (gbfs Decoder の順序: Ppu → (Apu は省略) → Joypad → Timer)
let stepPeripherals (tCycles: int) (p: Peripherals) : Peripherals =
    let ppu, mem1 = Ppu.step tCycles p.Ppu p.Mem
    let joypad, mem2 = Joypad.sync p.Joypad mem1
    let timer, mem3 = Timer.step tCycles p.Timer mem2
    { Mem = mem3; Ppu = ppu; Timer = timer; Joypad = joypad }

/// SC への 0x81 書込を観測したら SB を 1 文字受け取り、相手なしの転送完了状態にする
let completeSerialTransfer (mem: Memory.MemoryBus) : Memory.MemoryBus * byte option =
    let sc = Memory.read SerialControlAddr mem
    if sc <> SerialStartInternalClock then mem, None
    else
        let value = Memory.read SerialDataAddr mem
        let ifReg = Memory.read InterruptFlagAddr mem
        let completed =
            mem
            |> Memory.write SerialDataAddr SerialNoPartnerByte
            |> Memory.write SerialControlAddr (sc &&& 0x7Fuy)
            |> Memory.write InterruptFlagAddr (ifReg ||| SerialInterruptBit)
        completed, Some value

/// blargg の結果領域 (0xA000..) を MBC の RAM 有効フラグを介さずに読む (gbfs ランナーと同じ)
let readBlarggCartRam (mem: Memory.MemoryBus) : BlarggCartRam option =
    let ram = mem.ExtRam
    let textStart = 4
    let maxTextLength = 0x1000
    if ram.Length < textStart + 1 then None
    else
        let limit = min ram.Length (textStart + maxTextLength)
        let zeroAt = Array.IndexOf (ram, 0uy, textStart, limit - textStart)
        let textEnd = if zeroAt < 0 then limit else zeroAt
        Some { Status = ram.[0]; Signature = ram.[1..3]; Text = Text.Encoding.ASCII.GetString (ram, textStart, textEnd - textStart) }

let copyMemory (m: Memory.MemoryBus) : Memory.MemoryBus =
    { m with
        Vram = Array.copy m.Vram
        ExtRam = Array.copy m.ExtRam
        Wram = Array.copy m.Wram
        Oam = Array.copy m.Oam
        Io = Array.copy m.Io
        Hram = Array.copy m.Hram }

// --- ロックステップ (参照 CPU = gbfs Decoder) ---------------------------------------

/// RTL で観測した境界の種類
type BoundaryKind =
    | InstructionStart of fetchAddr: uint16
    | InterruptDispatch of ack: byte

type Mismatch =
    { /// 何番目の境界か (0 始まり)
      BoundaryIndex: int64
      Cycle: int64
      /// 食い違った命令 (アドレスと先頭 3 バイト)。境界の種類の食い違いならその境界の説明
      Instruction: string
      /// その命令の実行中に RTL が I/O (0xFF00-0xFF7F) を読んだか
      ReadIo: bool
      Diffs: string list }

/// 境界で準備し、次の境界で実行する参照の 1 step
type PendingStep =
    { /// 境界の時点の RTL 側メモリのコピーを載せた参照 CPU
      Prepared: Decoder.CpuState
      Description: string }

type LockstepState =
    { /// 参照 CPU (直前に実行した step の結果)
      Reference: Decoder.CpuState
      Pending: PendingStep option
      Boundaries: int64
      Mismatches: Mismatch list
      MismatchCount: int64
      IoMismatchCount: int64 }

let refRegisters (s: Decoder.CpuState) : (string * int) list =
    let r8 reg = int (Cpu.getRegisterValue (Cpu.R8 reg) s.Regs)
    [ "A", r8 Cpu.Reg8.A; "F", r8 Cpu.Reg8.F; "B", r8 Cpu.Reg8.B; "C", r8 Cpu.Reg8.C
      "D", r8 Cpu.Reg8.D; "E", r8 Cpu.Reg8.E; "H", r8 Cpu.Reg8.H; "L", r8 Cpu.Reg8.L
      "SP", int s.Regs.SP; "PC", int s.Regs.PC ]

let diffRegisters (reference: (string * int) list) (rtl: (string * int) list) : string list =
    List.zip reference rtl
    |> List.choose (fun ((name, e), (_, a)) ->
        if e = a then None
        else
            let width = if name = "SP" || name = "PC" then 4 else 2
            Some (sprintf "%s: gbfs=0x%0*X rtl=0x%0*X" name width e width a))

/// 参照が書いた WRAM / HRAM / IE と RTL 側メモリの差 (I/O は周辺回路が動かすので比べない)
let diffMemory (reference: Memory.MemoryBus) (rtl: Memory.MemoryBus) : string list =
    [ for i in 0 .. reference.Wram.Length - 1 do
        if reference.Wram.[i] <> rtl.Wram.[i] then
            yield sprintf "mem[0x%04X]: gbfs=0x%02X rtl=0x%02X" (0xC000 + i) reference.Wram.[i] rtl.Wram.[i]
      for i in 0 .. reference.Hram.Length - 1 do
        if reference.Hram.[i] <> rtl.Hram.[i] then
            yield sprintf "mem[0x%04X]: gbfs=0x%02X rtl=0x%02X" (HramBase + i) reference.Hram.[i] rtl.Hram.[i]
      if reference.Ie <> rtl.Ie then yield sprintf "IE: gbfs=0x%02X rtl=0x%02X" reference.Ie rtl.Ie ]

/// RTL の内部状態 (IME / EI 遅延 / HALT / HALT バグ) を読む
type RtlControl = { Ime: bool; EiDelay: bool; Halted: bool; HaltBug: bool }

let readControl (circuit: Circuit) (s: SimState) : RtlControl =
    let p = circuit.Probes
    { Ime = s.Values.[p.Ime]; EiDelay = s.Values.[p.EiDelay]; Halted = s.Values.[p.Halted]; HaltBug = s.Values.[p.HaltBug] }

/// 参照 CPU のレジスタと制御状態を RTL に合わせる (食い違いの後に続けるため)
let resyncReference (rtlRegs: (string * int) list) (control: RtlControl) (reference: Decoder.CpuState) : Decoder.CpuState =
    let r = Map.ofList rtlRegs
    let pair hi lo = uint16 ((r.[hi] <<< 8) ||| r.[lo])
    { reference with
        Regs = { AF = pair "A" "F"; BC = pair "B" "C"; DE = pair "D" "E"; HL = pair "H" "L"
                 SP = uint16 r.["SP"]; PC = uint16 r.["PC"] }
        Ime = control.Ime
        ImeScheduled = control.EiDelay
        Halted = control.Halted
        HaltBug = control.HaltBug }

let describeInstruction (mem: Memory.MemoryBus) (addr: uint16) : string =
    let bytes = [ for i in 0 .. 2 -> sprintf "%02X" (Memory.read (addr + uint16 i) mem) ] |> String.concat " "
    sprintf "0x%04X: %s" addr bytes

/// 参照が読む I/O の値を、RTL がその命令の実行中に実際に読んだ値に差し替える。
/// IF は割込み受付の判定に使うので差し替えない (境界の時点の値のまま)
let patchIoReads (ioReads: Map<uint16, byte>) (mem: Memory.MemoryBus) : unit =
    for KeyValue (addr, value) in ioReads do
        if addr <> InterruptFlagAddr then mem.Io.[int addr - IoBase] <- value

let formatDiffs (regDiffs: string list) (memDiffs: string list) : string list =
    let shownMem = memDiffs |> List.truncate 4
    let more = if memDiffs.Length > 4 then [ sprintf "... メモリ差分ほか %d 件" (memDiffs.Length - 4) ] else []
    regDiffs @ shownMem @ more

let recordMismatch (maxMismatches: int) (m: Mismatch) (ls: LockstepState) : LockstepState =
    let shown = ls.MismatchCount < int64 maxMismatches
    if shown then
        printfn "  [lockstep] 食い違い #%d (境界 %d、周期 %d) 命令 %s%s" (ls.MismatchCount + 1L) m.BoundaryIndex m.Cycle m.Instruction
            (if m.ReadIo then " (I/O 読出あり)" else "")
        for d in m.Diffs do printfn "      %s" d
    { ls with
        Mismatches = (if shown then m :: ls.Mismatches else ls.Mismatches)
        MismatchCount = ls.MismatchCount + 1L
        IoMismatchCount = ls.IoMismatchCount + (if m.ReadIo then 1L else 0L) }

/// 境界ごとの処理:
///   1. 直前の境界で準備した参照の 1 step を、その間に RTL が読んだ I/O 値を差し込んで実行し、
///      結果のレジスタと WRAM / HRAM / IE を RTL と比べる (食い違えば参照を RTL に合わせる)
///   2. この境界で RTL がすること (命令フェッチ / 割込み受付) を参照と比べる
///   3. 現在の RTL 側メモリのコピーを載せた参照を、次の境界で実行する step として準備する
let lockstepBoundary
    (maxMismatches: int)
    (cycle: int64)
    (kind: BoundaryKind)
    (rtlRegs: (string * int) list)
    (control: RtlControl)
    (ioReads: Map<uint16, byte>)
    (peripherals: Peripherals)
    (scratchPpu: Ppu.PpuState)
    (scratchApu: Apu.ApuState)
    (ls: LockstepState)
    : LockstepState =
    // 1. 直前の命令の結果を比べる
    let afterStep =
        match ls.Pending with
        | None -> ls
        | Some pending ->
            patchIoReads ioReads pending.Prepared.Mem
            let stepped = Decoder.step pending.Prepared
            let diffs = formatDiffs (diffRegisters (refRegisters stepped) rtlRegs) (diffMemory stepped.Mem peripherals.Mem)
            // 読み違えないよう、食い違った命令の実行前の値 (RTL と一致していたもの) も添える
            let before =
                refRegisters pending.Prepared
                |> List.map (fun (name, v) -> sprintf "%s=%0*X" name (if name = "SP" || name = "PC" then 4 else 2) v)
                |> String.concat " "
            let diffs = if diffs.IsEmpty then diffs else diffs @ [ "実行前: " + before ]
            if diffs.IsEmpty then { ls with Reference = stepped }
            else
                let m = { BoundaryIndex = ls.Boundaries; Cycle = cycle; Instruction = pending.Description
                          ReadIo = not ioReads.IsEmpty; Diffs = diffs }
                { recordMismatch maxMismatches m ls with Reference = resyncReference rtlRegs control stepped }
    // 2. この境界の種類を比べる (参照を RTL 側メモリのコピーに載せてから)
    let memCopy = copyMemory peripherals.Mem
    let reference =
        { afterStep.Reference with
            Mem = memCopy
            Ppu = { peripherals.Ppu with FrameBuffer = scratchPpu.FrameBuffer }
            Apu = scratchApu
            Timer = peripherals.Timer
            Joypad = peripherals.Joypad }
    let pendingIrq = (Memory.read InterruptEnableAddr memCopy) &&& (Memory.read InterruptFlagAddr memCopy) &&& 0x1Fuy
    let refDispatches = reference.Ime && pendingIrq <> 0uy
    let refStaysHalted = reference.Halted && pendingIrq = 0uy
    let kindDiffs =
        match kind with
        | InterruptDispatch ack when not refDispatches ->
            [ sprintf "RTL は割込み受付 (int_ack=0x%02X)、gbfs は受け付けない (IME=%b IE&IF=0x%02X halted=%b)" ack reference.Ime pendingIrq reference.Halted ]
        | InstructionStart addr when refDispatches -> [ sprintf "RTL は 0x%04X をフェッチ、gbfs は割込み受付 (IE&IF=0x%02X)" addr pendingIrq ]
        | InstructionStart addr when refStaysHalted -> [ sprintf "RTL は 0x%04X をフェッチ、gbfs は HALT 中のまま" addr ]
        | InstructionStart addr ->
            let refAddr = if reference.HaltBug then reference.Regs.PC + 1us else reference.Regs.PC
            if refAddr <> addr then [ sprintf "フェッチ番地: gbfs=0x%04X rtl=0x%04X" refAddr addr ] else []
        | InterruptDispatch _ -> []
    let description =
        match kind with
        | InstructionStart addr -> describeInstruction peripherals.Mem addr
        | InterruptDispatch ack -> sprintf "割込み受付 (int_ack=0x%02X)" ack
    let afterKind, prepared =
        if kindDiffs.IsEmpty then afterStep, reference
        else
            // 参照の制御状態も RTL に合わせて、次の比較を意味のあるものにする
            let m = { BoundaryIndex = afterStep.Boundaries; Cycle = cycle; Instruction = description; ReadIo = false; Diffs = kindDiffs }
            recordMismatch maxMismatches m afterStep, resyncReference rtlRegs control reference
    // 3. 次の境界で実行する step を準備する
    { afterKind with
        Pending = Some { Prepared = prepared; Description = description }
        Boundaries = afterKind.Boundaries + 1L }

// --- 1 本の実行 ---------------------------------------------------------------------

type RunResult =
    { RomPath: string
      Verdict: Verdict
      Cycles: int64
      /// RTL が開始した命令数 (CB 命令は 1 と数える。HALT 中・割込み受付は数えない)
      Instructions: int64
      WallTime: TimeSpan
      SerialText: string
      Lockstep: LockstepState option }

let runRom (opts: Options) (circuit: Circuit) (romPath: string) : RunResult =
    let c = circuit.Compiled
    let bus = circuit.Bus
    let rom = File.ReadAllBytes romPath
    let limitDescription = sprintf "%d RTL cycles" opts.MaxCycles
    let sw = Diagnostics.Stopwatch.StartNew ()
    let serial = Text.StringBuilder ()
    let scratchPpu = Ppu.create ()
    let scratchApu = Apu.create ()

    // リセット: rst=1 のまま clk を 1 往復 × ResetPulses (周辺回路は進めない)
    let mutable sim =
        [ 1 .. ResetPulses ]
        |> List.fold (fun s _ -> s |> applyWrites c [ bus.Reset, 1UL; bus.Clock, 0UL ] |> applyWrites c [ bus.Clock, 1UL ]) (initial c)
    let phaseBits (s: SimState) = circuit.Probes.Phase |> Array.map (fun i -> s.Values.[i])
    // リセット直後に立っている phase ビット = PHASE_FETCH
    let fetchBit =
        match phaseBits sim |> Array.indexed |> Array.filter snd |> Array.map fst with
        | [| i |] -> circuit.Probes.Phase.[i]
        | other -> failwithf "リセット後の phase が one-hot ではない (立っているビット %A)" other

    let mutable peripherals = createPeripherals rom
    let mutable cycle = 0L
    let mutable instructions = 0L
    let mutable progress = InProgress
    let mutable graceUntil : int64 option = None
    let mutable finished = false
    /// 直前の境界から RTL が読んだ I/O (番地 → 最初に読んだ値)
    let mutable ioReadsSinceBoundary : Map<uint16, byte> = Map.empty
    let mutable lockstep =
        if opts.Lockstep then
            let reference = Decoder.createState () |> Decoder.loadRomToState rom
            Some { Reference = reference; Pending = None; Boundaries = 0L; Mismatches = []; MismatchCount = 0L; IoMismatchCount = 0L }
        else None

    while not finished do
        // 1. 直前の posedge でラッチされたバス出力を読む
        let wasFetchPhase = sim.Values.[fetchBit]
        let wasCbPrefix = sim.Values.[circuit.Probes.CbPrefix]
        let request = readBusRequest circuit sim
        // 境界は「FETCH 相のサイクル = その命令の開始」なので、参照と比べる RTL の状態は
        // このサイクルのクロックを打つ前 (= 直前の命令が完了した状態) でなければならない。
        // 1 フェーズ = 1 M サイクルでは 1 サイクル命令がそのサイクル内で完了するため、
        // クロック後を読むと 1 命令ずれる。
        let regsAtBoundary = readRegisters circuit sim
        let controlAtBoundary = readControl circuit sim
        // 2. 書込 (シリアルの観測を含む) → 割込み受付で IF を下ろす → 読出
        let mutable mem = peripherals.Mem
        if request.IsWrite then
            mem <- Memory.write request.Addr request.DataOut mem
            if request.Addr = SerialControlAddr then
                let completed, received = completeSerialTransfer mem
                mem <- completed
                received |> Option.iter (fun b -> serial.Append (char b) |> ignore)
        if request.IntAck <> 0uy then
            let ifReg = Memory.read InterruptFlagAddr mem
            mem <- Memory.write InterruptFlagAddr (ifReg &&& ~~~request.IntAck) mem
        let dataIn = if request.IsRead then Memory.read request.Addr mem else 0uy
        let isIoRead = request.IsRead && int request.Addr >= IoBase && int request.Addr < HramBase
        if isIoRead && not (ioReadsSinceBoundary.ContainsKey request.Addr) then
            ioReadsSinceBoundary <- ioReadsSinceBoundary.Add (request.Addr, dataIn)
        let irq = (Memory.read InterruptEnableAddr mem) &&& (Memory.read InterruptFlagAddr mem) &&& 0x1Fuy
        peripherals <- { peripherals with Mem = mem }
        // 3. data_in / irq を書いて settle → clk=1 で settle
        let lowWrites =
            [ yield bus.DataIn, uint64 dataIn
              yield circuit.Interrupt.Irq, uint64 irq
              if cycle = 0L then yield bus.Reset, 0UL ]
        let sLow =
            if opts.StrictPhases then
                let clkLow = if cycle = 0L then [ bus.Reset, 0UL; bus.Clock, 0UL ] else [ bus.Clock, 0UL ]
                sim |> applyWrites c clkLow |> applyWrites c lowWrites
            else
                sim |> applyWrites c ((bus.Clock, 0UL) :: lowWrites)
        sim <- sLow |> applyWrites c [ bus.Clock, 1UL ]
        // 命令境界の観測。位相機械は 1 フェーズ = 1 M サイクルで、FETCH 相がそのまま
        // オペコード読み出しを出す (addr/mem_read は組合せ出力)。したがって境界は
        // 「クロック前に FETCH 相だった」かつ「そのクロック前のバス要求が読み出し」で判定する。
        // クロック後のバス要求 (next) は次フェーズのものなので、旧 FSM (FETCH → FETCH2 の
        // 2 相構造) のときだけ偶然一致していた。
        // 割込みだけは例外で、FETCH 相のクロックで int_ack が立つのでクロック後 (next) を見る。
        let next = readBusRequest circuit sim
        match opts.Trace with
        | Some (fromCycle, toCycle) when cycle >= fromCycle && cycle <= toCycle ->
            let regs = readRegisters circuit sim |> Map.ofList
            if cycle = fromCycle then
                let pc = uint16 regs.["PC"]
                let start = (pc - 0x40us) &&& 0xFFF0us
                for row in 0 .. 7 do
                    let a = start + uint16 (row * 16)
                    let bytes = [ for i in 0 .. 15 -> sprintf "%02X" (Memory.read (a + uint16 i) peripherals.Mem) ] |> String.concat " "
                    printfn "  [mem] %04X: %s" a bytes
            let phaseIndex = circuit.Probes.Phase |> Array.tryFindIndex (fun i -> sim.Values.[i]) |> Option.defaultValue -1
            let control = readControl circuit sim
            printfn "  [trace] cyc=%d bus(%s%s addr=%04X din=%02X dout=%02X ack=%02X irq=%02X) -> phase=%d cb=%b a=%02X f=%02X b=%02X c=%02X d=%02X e=%02X h=%02X l=%02X sp=%04X pc=%04X ime=%b halted=%b"
                cycle (if request.IsRead then "R" else "-") (if request.IsWrite then "W" else "-") request.Addr dataIn request.DataOut request.IntAck irq
                phaseIndex sim.Values.[circuit.Probes.CbPrefix] regs.["A"] regs.["F"] regs.["B"] regs.["C"] regs.["D"] regs.["E"] regs.["H"] regs.["L"] regs.["SP"] regs.["PC"] control.Ime control.Halted
        | _ -> ()
        let boundary =
            if not wasFetchPhase then None
            elif next.IntAck <> 0uy then Some (InterruptDispatch next.IntAck)
            elif request.IsRead && not wasCbPrefix then Some (InstructionStart request.Addr)
            else None
        match boundary with
        | Some (InstructionStart _) -> instructions <- instructions + 1L
        | _ -> ()
        match boundary, lockstep with
        | Some kind, Some ls ->
            lockstep <- Some (lockstepBoundary opts.MaxMismatches cycle kind regsAtBoundary controlAtBoundary ioReadsSinceBoundary peripherals scratchPpu scratchApu ls)
        | _ -> ()
        if boundary.IsSome then ioReadsSinceBoundary <- Map.empty
        // 5. 周辺回路を 4 T サイクル進める
        peripherals <- stepPeripherals TCyclesPerRtlCycle peripherals
        cycle <- cycle + 1L

        // 判定
        if graceUntil.IsNone && cycle % JudgeIntervalCycles = 0L then
            progress <- judgeBlargg (serial.ToString ()) (readBlarggCartRam peripherals.Mem)
            match progress with
            | Concluded _ -> graceUntil <- Some (cycle + BlarggGraceCycles)
            | InProgress -> ()
        match graceUntil with
        | Some until when cycle >= until ->
            progress <- judgeBlargg (serial.ToString ()) (readBlarggCartRam peripherals.Mem)
            finished <- true
        | _ -> ()
        if cycle >= opts.MaxCycles then finished <- true
        if opts.ProgressInterval > 0L && cycle % opts.ProgressInterval = 0L then
            let regs = readRegisters circuit sim |> Map.ofList
            let rate = float cycle / sw.Elapsed.TotalSeconds
            let tail = let t = serial.ToString () in (if t.Length > 60 then t.Substring (t.Length - 60) else t).Replace ("\n", "\\n")
            printfn "  [%s] cycle=%d instr=%d pc=0x%04X %.0f cyc/s wall=%.0fs serial=\"%s\"%s"
                (Path.GetFileName romPath) cycle instructions regs.["PC"] rate sw.Elapsed.TotalSeconds tail
                (match lockstep with Some ls -> sprintf " mismatches=%d" ls.MismatchCount | None -> "")
            Console.Out.Flush ()

    sw.Stop ()
    { RomPath = romPath
      Verdict = finalizeAtLimit limitDescription progress
      Cycles = cycle
      Instructions = instructions
      WallTime = sw.Elapsed
      SerialText = serial.ToString ()
      Lockstep = lockstep }

// --- 出力 ---------------------------------------------------------------------

/// gbfs 単体の結果 TSV (tools/run_test_roms.fsx の出力) から ROM パス → (verdict, instructions, cycles)
let loadGbfsResults (path: string) : Map<string, string * int64 * int64> =
    if not (File.Exists path) then Map.empty
    else
        File.ReadAllLines path
        |> Array.skip 1
        |> Array.choose (fun line ->
            match line.Split '\t' with
            | cols when cols.Length >= 4 -> Some (Path.GetFullPath cols.[0], (cols.[1], int64 cols.[2], int64 cols.[3]))
            | _ -> None)
        |> Map.ofArray

let serialTail (maxChars: int) (text: string) : string =
    let t = text.TrimEnd ()
    let tail = if t.Length > maxChars then "..." + t.Substring (t.Length - maxChars) else t
    tail.Replace("\n", "\\n").Replace("\t", " ")

let emulatedSeconds (cycles: int64) = float (cycles * int64 TCyclesPerRtlCycle) / float CpuHz

let TsvHeader =
    "rom\tverdict\trtl_cycles\trtl_instructions\temulated_seconds\twall_seconds\tgbfs_verdict\tgbfs_instructions\tlockstep_mismatches\tserial_tail"

let tsvRow (gbfs: Map<string, string * int64 * int64>) (r: RunResult) : string =
    let gbfsVerdict, gbfsInstr =
        match Map.tryFind (Path.GetFullPath r.RomPath) gbfs with
        | Some (v, instr, _) -> v, string instr
        | None -> "", ""
    String.concat "\t"
        [ r.RomPath
          verdictLabel r.Verdict
          string r.Cycles
          string r.Instructions
          sprintf "%.3f" (emulatedSeconds r.Cycles)
          sprintf "%.1f" r.WallTime.TotalSeconds
          gbfsVerdict
          gbfsInstr
          (match r.Lockstep with Some ls -> sprintf "%d (io %d)" ls.MismatchCount ls.IoMismatchCount | None -> "")
          serialTail 300 r.SerialText ]

let appendTsv (path: string) (row: string) =
    let needsHeader = not (File.Exists path)
    File.AppendAllText (path, (if needsHeader then TsvHeader + "\n" else "") + row + "\n")

let main (argv: string list) : int =
    match parseArgs argv with
    | Error e ->
        eprintfn "ERROR: %s" e
        2
    | Ok { Roms = [] } ->
        eprintfn "使い方: dotnet fsi src/CoSimGbfs.fsx [--max-cycles N] [--tsv PATH] [--lockstep] [--strict-phases] <rom.gb>..."
        2
    | Ok opts ->
        let loadWatch = Diagnostics.Stopwatch.StartNew ()
        match loadCircuit () with
        | Error e ->
            eprintfn "ERROR: 回路の読込に失敗: %s" e
            2
        | Ok circuit ->
            printfn "回路 sm83_full: 組合せゲート %d、DFF %d (読込 %.1f 秒)" circuit.Compiled.CombGates.Length circuit.Compiled.Dffs.Length loadWatch.Elapsed.TotalSeconds
            let gbfs = loadGbfsResults opts.GbfsTsvPath
            let results =
                [ for rom in opts.Roms do
                    printfn "=== %s ===" rom
                    Console.Out.Flush ()
                    let r = runRom opts circuit rom
                    let gbfsNote =
                        match Map.tryFind (Path.GetFullPath rom) gbfs with
                        | Some (v, instr, _) -> sprintf " | gbfs: %s instr=%d" v instr
                        | None -> ""
                    printfn "%-7s %s cycles=%d instr=%d emu=%.2fs wall=%.1fs%s"
                        (verdictLabel r.Verdict) (Path.GetFileName rom) r.Cycles r.Instructions (emulatedSeconds r.Cycles) r.WallTime.TotalSeconds gbfsNote
                    printfn "        serial: %s" (serialTail 400 r.SerialText)
                    match r.Verdict with
                    | Timeout limit -> printfn "        timeout: %s" limit
                    | _ -> ()
                    r.Lockstep |> Option.iter (fun ls ->
                        printfn "        lockstep: 境界 %d、食い違い %d (うち直前の命令が I/O を読んだもの %d)" ls.Boundaries ls.MismatchCount ls.IoMismatchCount)
                    opts.TsvPath |> Option.iter (fun p -> appendTsv p (tsvRow gbfs r))
                    Console.Out.Flush ()
                    yield r ]
            if results |> List.forall (fun r -> r.Verdict = Passed) then 0 else 1

exit (main (List.ofArray fsi.CommandLineArgs |> List.tail))
