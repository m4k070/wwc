namespace WwHdl

// ---------------------------------------------------------------------
// 12. Testbench — メモリバス付き CPU を NetlistSim で動かすテストベンチ
//     (TODO Step B-2, DESIGN-VERIFY.md §5.2〜§6)
//
// wgpu-runner `--memory` と同じプログラム JSON を読み、同じ手順・同じメモリ規則で周期を回す。
// 周期ごとの data_in と全出力を golden として書き出し、runner (B-3b) が GPU 上の結果と照合する。
//
// 契約 (runner の memory_program.rs / memory.rs と一致させる):
//   リセット: rstPulses 回「rst=1, clk=0 で settle → clk=1 で settle」
//   1 周期:   clk=0 で settle (最初の周期は rst=0 も反映) → addr/mem_read/mem_write/data_out/int_ack を読む
//             → mem_write なら書込 → int_ack のビットを IF から下ろす
//             → mem_read なら data_in=mem[addr]、そうでなければ 0。irq = IE & IF & 0x1F
//             → data_in と irq を書いて settle → clk=1 で settle → 全出力を読む
//   メモリ:   ブート ROM (0x0000-0x00FF、0xFF50 への書込で解除) → ROM (0 から ROM 長) →
//             VRAM (0x8000-0x9FFF) → RAM 窓 (ramBase から ramSize) → I/O (IF 0xFF0F、HRAM 0xFF80-0xFFFE、
//             IE 0xFFFF、0xFF00-0xFF7F のレジスタ、LY 0xFF44 は M サイクルから算出) → それ以外は 0xFF。
//             書込は VRAM / RAM 窓 / I/O のみ (ROM と LY への書込は無視)
//   割込み:   IE / IF は CPU の外 (このメモリモデル) に置く。回路に irq / int_ack ポートがなければ扱わない
// ---------------------------------------------------------------------
module Testbench =
    open System
    open System.IO
    open System.Text.Json
    open RoutedArtifact
    open NetlistSim

    // --- メモリモデル -------------------------------------------------------

    type MemoryConfig = { RamBase: int; RamSize: int }

    let defaultMemoryConfig : MemoryConfig = { RamBase = 0xC000; RamSize = 8192 }

    [<Literal>]
    let InterruptFlagAddress = 0xFF0F
    [<Literal>]
    let InterruptEnableAddress = 0xFFFF
    [<Literal>]
    let HramBase = 0xFF80
    [<Literal>]
    let HramSize = 0x7F
    [<Literal>]
    let VramBase = 0x8000
    [<Literal>]
    let VramSize = 0x2000
    [<Literal>]
    let IoBase = 0xFF00
    [<Literal>]
    let IoSize = 0x80
    /// LY (0xFF44)。読み出し専用で、M サイクル番号から算出する
    [<Literal>]
    let LcdYAddress = 0xFF44
    /// ブート ROM のマップ解除 (0xFF50 に 1 を書く)
    [<Literal>]
    let BootRomDisableAddress = 0xFF50
    /// 1 フレーム = 154 ライン × 456 サイクル (LY の算出用)
    [<Literal>]
    let FrameCycles = 70224
    [<Literal>]
    let LineCycles = 456

    type MemoryImage =
        { Rom: byte[]
          Ram: byte[]
          Config: MemoryConfig
          /// 0xFF80-0xFFFE
          Hram: byte[]
          /// IF (0xFF0F)。書いたバイトをそのまま保持する (gbfs と同じ。実機は上位 3bit が 1 で読める)
          InterruptFlag: byte
          /// IE (0xFFFF)
          InterruptEnable: byte
          /// 0x8000-0x9FFF。ブート ROM がロゴを書く先
          Vram: byte[]
          /// 0xFF00-0xFF7F のレジスタ。未使用分は 0xFF で初期化 (従来の「未モデルは 0xFF」挙動と同じ)
          Io: byte[]
          /// 0x0000-0x00FF に重ねるブート ROM (None なら重ねない)
          BootRom: byte[] option
          /// ブート ROM のマップ状態。0xFF50 に 1 を書くと解除される
          BootRomEnabled: bool }

    let createMemory (rom: byte[]) (config: MemoryConfig) : MemoryImage =
        { Rom = Array.copy rom
          Ram = Array.zeroCreate config.RamSize
          Config = config
          Hram = Array.zeroCreate HramSize
          InterruptFlag = 0uy
          InterruptEnable = 0uy
          Vram = Array.zeroCreate VramSize
          Io = Array.init IoSize (fun i -> if i = LcdYAddress - IoBase then 0uy else 0xFFuy)
          BootRom = None
          BootRomEnabled = false }

    /// ブート ROM を 0x0000-0x00FF に重ねたメモリを作る (0xFF50 への書込で解除される)。
    let createMemoryWithBootRom (rom: byte[]) (bootRom: byte[]) (config: MemoryConfig) : MemoryImage =
        { createMemory rom config with
            BootRom = Some (Array.copy bootRom)
            BootRomEnabled = true }

    let private ramOffset (m: MemoryImage) (addr: uint16) : int option =
        let a = int addr
        if a >= m.Config.RamBase && a < m.Config.RamBase + m.Ram.Length then Some (a - m.Config.RamBase)
        else None

    let private isHram (a: int) = a >= HramBase && a < HramBase + HramSize

    let readMemory (m: MemoryImage) (addr: uint16) : byte =
        let a = int addr
        match m.BootRom with
        | Some bootRom when m.BootRomEnabled && a < bootRom.Length -> bootRom.[a]
        | _ ->
            if a < m.Rom.Length then m.Rom.[a]
            elif a >= VramBase && a < VramBase + VramSize then m.Vram.[a - VramBase]
            else
                match ramOffset m addr with
                | Some i -> m.Ram.[i]
                | None when a = InterruptFlagAddress -> m.InterruptFlag
                | None when a = InterruptEnableAddress -> m.InterruptEnable
                | None when isHram a -> m.Hram.[a - HramBase]
                | None when a >= IoBase && a < IoBase + IoSize -> m.Io.[a - IoBase]
                | None -> 0xFFuy

    /// RAM 窓と I/O (IF / HRAM / IE / 0xFF00-0xFF7F / VRAM) への書込を反映した新しいイメージを返す
    /// (ROM と LY への書込は無視)。RAM 窓が I/O 領域と重なる場合は RAM 窓を優先する。
    let writeMemory (m: MemoryImage) (addr: uint16) (value: byte) : MemoryImage =
        let a = int addr
        if a >= VramBase && a < VramBase + VramSize then
            let vram = Array.copy m.Vram
            vram.[a - VramBase] <- value
            { m with Vram = vram }
        else
            match ramOffset m addr with
            | Some i ->
                let ram = Array.copy m.Ram
                ram.[i] <- value
                { m with Ram = ram }
            | None when a = InterruptFlagAddress -> { m with InterruptFlag = value }
            | None when a = InterruptEnableAddress -> { m with InterruptEnable = value }
            | None when a = BootRomDisableAddress -> { m with BootRomEnabled = (value &&& 1uy) = 0uy }
            | None when a = LcdYAddress -> m
            | None when isHram a ->
                let hram = Array.copy m.Hram
                hram.[a - HramBase] <- value
                { m with Hram = hram }
            | None when a >= IoBase && a < IoBase + IoSize ->
                let io = Array.copy m.Io
                io.[a - IoBase] <- value
                { m with Io = io }
            | None -> m

    /// M サイクル番号から LY (0xFF44) を更新する (1 フレーム = 154 ライン × 456 サイクル)。
    /// ホスト側 (TB / runner) が周期の先頭で呼ぶ。LY は読み出し専用なので writeMemory では扱わない。
    let setLcdY (m: MemoryImage) (cycle: int) : MemoryImage =
        let ly = byte ((cycle % FrameCycles) / LineCycles)
        if m.Io.[LcdYAddress - IoBase] = ly then m
        else
            let io = Array.copy m.Io
            io.[LcdYAddress - IoBase] <- ly
            { m with Io = io }

    /// CPU の irq 入力に渡す値 (IE & IF の割込み要因 5bit)。
    let pendingInterrupts (m: MemoryImage) : byte =
        m.InterruptEnable &&& m.InterruptFlag &&& 0x1Fuy

    /// CPU が受け付けた割込み (int_ack の one-hot) のビットを IF から下ろす。
    let acknowledgeInterrupts (m: MemoryImage) (ack: byte) : MemoryImage =
        if ack = 0uy then m else { m with InterruptFlag = m.InterruptFlag &&& ~~~ack }

    // --- プログラム JSON ------------------------------------------------------

    type RomSource =
        | RomFile of path: string
        | RomBase64 of data: string

    /// wgpu-runner `--memory` のプログラム JSON。パスはプログラム JSON のディレクトリからの相対で解決済み。
    type MemoryProgram =
        { ProgramName: string
          Circuit: string option
          MetaPath: string
          InitPath: string
          Rom: RomSource
          /// memory.bootRom (省略可)。0x0000-0x00FF に重ねるブート ROM
          BootRom: RomSource option
          Memory: MemoryConfig
          RstPulses: int
          Cycles: int
          Expect: Map<string, uint64>
          ExpectMem: Map<uint16, byte>
          /// 省略時は <プログラムのディレクトリ>/<プログラム名>.golden.json
          GoldenPath: string }

    type TestbenchError =
        | ProgramParseFailed of path: string * reason: string
        | RomLoadFailed of reason: string
        | MissingBusPort of port: string
        | BusPortDirection of port: string * expected: PortDirection
        | BusPortWidth of port: string * expected: int * actual: int
        | IncompleteInterruptPorts of present: string
        | SimulationFailed of SimError

    let describeTestbenchError (e: TestbenchError) : string =
        match e with
        | ProgramParseFailed (path, reason) -> sprintf "プログラム JSON のパースに失敗 (%s): %s" path reason
        | RomLoadFailed reason -> sprintf "ROM の読込に失敗: %s" reason
        | MissingBusPort port -> sprintf "バスポート %s が回路にない" port
        | BusPortDirection (port, expected) -> sprintf "バスポート %s の方向が %A ではない" port expected
        | BusPortWidth (port, expected, actual) -> sprintf "バスポート %s の幅が %d ではなく %d" port expected actual
        | IncompleteInterruptPorts present -> sprintf "割込みポートは irq と int_ack の両方が必要 (%s だけがある)" present
        | SimulationFailed e -> sprintf "シミュレーション失敗: %s" (describeSimError e)

    let private traverse (f: 'a -> Result<'b, 'e>) (xs: 'a list) : Result<'b list, 'e> =
        let folder x acc =
            match f x, acc with
            | Ok y, Ok ys -> Ok (y :: ys)
            | Error e, _ -> Error e
            | _, Error e -> Error e
        List.foldBack folder xs (Ok [])

    /// runner と同じ解釈: "49152" (10 進) または "0xC000" (16 進)。
    let parseAddress (spec: string) : Result<uint16, string> =
        let s = spec.Trim ()
        let hex = if s.StartsWith "0x" || s.StartsWith "0X" then Some (s.Substring 2) else None
        match hex with
        | Some digits ->
            match UInt16.TryParse (digits, Globalization.NumberStyles.HexNumber, Globalization.CultureInfo.InvariantCulture) with
            | true, v -> Ok v
            | _ -> Error (sprintf "bad addr '%s'" spec)
        | None ->
            match UInt16.TryParse (s, Globalization.NumberStyles.None, Globalization.CultureInfo.InvariantCulture) with
            | true, v -> Ok v
            | _ -> Error (sprintf "bad addr '%s'" spec)

    let private tryProperty (el: JsonElement) (name: string) : JsonElement option =
        match el.TryGetProperty name with
        | true, v -> Some v
        | _ -> None

    /// プログラム JSON を読む。既定値は runner と同じ (rstPulses=2, ramBase=0xC000, ramSize=8192)。
    let parseProgram (programPath: string) (json: string) : Result<MemoryProgram, TestbenchError> =
        let fullPath = Path.GetFullPath programPath
        let dir = Path.GetDirectoryName fullPath
        let name = Path.GetFileNameWithoutExtension fullPath
        let resolve (p: string) = if Path.IsPathRooted p then p else Path.GetFullPath (Path.Combine (dir, p))
        let fail reason = ProgramParseFailed (programPath, reason)
        try
            use doc = JsonDocument.Parse json
            let root = doc.RootElement
            let memory = root.GetProperty "memory"
            let intOr (el: JsonElement) (prop: string) (fallback: int) =
                tryProperty el prop |> Option.map (fun v -> v.GetInt32 ()) |> Option.defaultValue fallback
            let romSpec = memory.GetProperty("rom").GetString ()
            let rom =
                if romSpec.StartsWith "base64:" then RomBase64 (romSpec.Substring "base64:".Length)
                else RomFile (resolve romSpec)
            let bootRom =
                match tryProperty memory "bootRom" with
                | None -> None
                | Some v ->
                    let spec = v.GetString ()
                    if spec.StartsWith "base64:" then Some (RomBase64 (spec.Substring "base64:".Length))
                    else Some (RomFile (resolve spec))
            let expect =
                match tryProperty root "expect" with
                | Some e -> e.EnumerateObject () |> Seq.map (fun p -> p.Name, p.Value.GetUInt64 ()) |> Map.ofSeq
                | None -> Map.empty
            let expectMem =
                match tryProperty root "expectMem" with
                | None -> Ok Map.empty
                | Some e ->
                    e.EnumerateObject ()
                    |> List.ofSeq
                    |> traverse (fun p -> parseAddress p.Name |> Result.map (fun addr -> addr, p.Value.GetByte ()))
                    |> Result.map Map.ofList
                    |> Result.mapError fail
            expectMem
            |> Result.map (fun expectMem ->
                ({ ProgramName = name
                   Circuit = tryProperty root "circuit" |> Option.map (fun v -> v.GetString ())
                   MetaPath = resolve (root.GetProperty("meta").GetString ())
                   InitPath = resolve (root.GetProperty("init").GetString ())
                   Rom = rom
                   BootRom = bootRom
                   Memory = { RamBase = intOr memory "ramBase" defaultMemoryConfig.RamBase
                              RamSize = intOr memory "ramSize" defaultMemoryConfig.RamSize }
                   RstPulses = intOr root "rstPulses" 2
                   Cycles = root.GetProperty("cycles").GetInt32 ()
                   Expect = expect
                   ExpectMem = expectMem
                   GoldenPath =
                     tryProperty root "golden"
                     |> Option.map (fun v -> resolve (v.GetString ()))
                     |> Option.defaultValue (Path.Combine (dir, name + ".golden.json")) } : MemoryProgram))
        with ex ->
            Error (fail ex.Message)

    let loadRom (source: RomSource) : Result<byte[], TestbenchError> =
        try
            match source with
            | RomFile path -> Ok (File.ReadAllBytes path)
            | RomBase64 data -> Ok (Convert.FromBase64String (data.Trim ()))
        with ex ->
            Error (RomLoadFailed ex.Message)

    // --- バス ---------------------------------------------------------------

    type InterruptPorts =
        { /// 入力: IE & IF (5bit)
          Irq: YosysPortBits
          /// 出力: 受け付けた割込みの one-hot (1 周期だけ立つ)
          IntAck: YosysPortBits }

    type BusPorts =
        { Clock: YosysPortBits
          Reset: YosysPortBits
          DataIn: YosysPortBits
          Addr: YosysPortBits
          DataOut: YosysPortBits
          MemRead: YosysPortBits
          MemWrite: YosysPortBits
          /// 割込みを持たない回路 (sm83_subset) では None
          Interrupt: InterruptPorts option }

    /// runner (memory_program.rs) と同じポート名で、方向と幅を確認して解決する。
    let resolveBus (ports: YosysPortBits list) : Result<BusPorts, TestbenchError> =
        let find (name: string, direction: PortDirection, width: int) =
            match ports |> List.tryFind (fun p -> p.Name = name) with
            | None -> Error (MissingBusPort name)
            | Some p when p.Direction <> direction -> Error (BusPortDirection (name, direction))
            | Some p when p.Bits.Length <> width -> Error (BusPortWidth (name, width, p.Bits.Length))
            | Some p -> Ok p
        [ "clk", InputPort, 1
          "rst", InputPort, 1
          "data_in", InputPort, 8
          "addr", OutputPort, 16
          "data_out", OutputPort, 8
          "mem_read", OutputPort, 1
          "mem_write", OutputPort, 1 ]
        |> traverse find
        |> Result.bind (fun resolved ->
            let hasPort name = ports |> List.exists (fun p -> p.Name = name)
            let interrupt =
                match hasPort "irq", hasPort "int_ack" with
                | false, false -> Ok None
                | true, true ->
                    find ("irq", InputPort, 5)
                    |> Result.bind (fun irq -> find ("int_ack", OutputPort, 5) |> Result.map (fun ack -> Some { Irq = irq; IntAck = ack }))
                | true, false -> Error (IncompleteInterruptPorts "irq")
                | false, true -> Error (IncompleteInterruptPorts "int_ack")
            interrupt
            |> Result.map (fun interrupt ->
                match resolved with
                | [ clk; rst; dataIn; addr; dataOut; memRead; memWrite ] ->
                    { Clock = clk; Reset = rst; DataIn = dataIn; Addr = addr
                      DataOut = dataOut; MemRead = memRead; MemWrite = memWrite; Interrupt = interrupt }
                | other -> invalidOp (sprintf "traverse は入力と同数を返すはず (got %d)" other.Length)))

    // --- 実行 ---------------------------------------------------------------

    // --- ポートの密解決 -------------------------------------------------------
    // ホットパス (1 周期あたり読み書きが 100 回前後ある) では Map を引かず、
    // 起動時に解決した密インデックスを配列として持つ。リスト・クロージャ・Result を
    // ビットごとに作らないことが要点。

    /// 解決済みポート。Bits.[i] は LSB first で
    /// -1 = 定数 0 / -2 = 定数 1 / >= 0 = SimState.Values の密インデックス
    type PortPlan =
        { PlanName: string
          Bits: int[] }

    let private planPort (c: CompiledNetlist) (p: YosysPortBits) : Result<PortPlan, SimError> =
        p.Bits
        |> List.mapi (fun i bit ->
            match bit with
            | ConstBit b -> Ok (if b then -2 else -1)
            | NetBit net ->
                match Map.tryFind net c.NetIndex with
                | Some idx -> Ok idx
                | None -> Error (UndrivenOutput (p.Name, i, net)))
        |> traverse id
        |> Result.map (fun bits -> { PlanName = p.Name; Bits = List.toArray bits })

    /// 解決済みポートの値を読む (定数ビット込み、LSB first)。ホットパスなので割り当てしない。
    let private readPlanned (s: SimState) (plan: PortPlan) : uint64 =
        let bits = plan.Bits
        let mutable acc = 0UL
        for i = 0 to bits.Length - 1 do
            let b = bits.[i]
            if b >= 0 then
                if s.Values.[b] then acc <- acc ||| (1UL <<< i)
            elif b = -2 then
                acc <- acc ||| (1UL <<< i)
        acc

    /// 出力ポートの計画。名前は Map と同じ昇順に並べる (golden の出力順を変えないため)
    type OutputPlan =
        { Names: string[]
          Ports: PortPlan[] }

    let private planOutputs (c: CompiledNetlist) (ports: YosysPortBits list) : Result<OutputPlan, SimError> =
        ports
        |> List.filter (fun p -> p.Direction = OutputPort)
        |> List.sortBy (fun p -> p.Name)
        |> List.map (planPort c)
        |> traverse id
        |> Result.map (fun planned ->
            { Names = planned |> List.map (fun p -> p.PlanName) |> List.toArray
              Ports = List.toArray planned })

    let private readOutputsPlanned (s: SimState) (plan: OutputPlan) : (string * uint64)[] =
        Array.init plan.Ports.Length (fun i -> plan.Names.[i], readPlanned s plan.Ports.[i])

    /// バス読み出しの計画 (1 周期に 1 回まとめて読む)
    type BusPlan =
        { Addr: PortPlan
          MemRead: PortPlan
          MemWrite: PortPlan
          DataOut: PortPlan
          /// 割込みを持たない回路では None
          IntAck: PortPlan option }

    let private planBus (c: CompiledNetlist) (bus: BusPorts) : Result<BusPlan, SimError> =
        let ack =
            match bus.Interrupt with
            | Some p -> planPort c p.IntAck |> Result.map Some
            | None -> Ok None
        ack
        |> Result.bind (fun ack ->
            planPort c bus.Addr
            |> Result.bind (fun addr ->
                planPort c bus.MemRead
                |> Result.bind (fun memRead ->
                    planPort c bus.MemWrite
                    |> Result.bind (fun memWrite ->
                        planPort c bus.DataOut
                        |> Result.map (fun dataOut ->
                            { Addr = addr; MemRead = memRead; MemWrite = memWrite; DataOut = dataOut; IntAck = ack })))))

    type CycleRecord =
        { /// この周期で書いた data_in
          DataIn: uint64
          /// この周期で書いた irq (割込みポートがなければ 0)
          Irq: uint64
          /// clk=1 で settle した後の全出力ポート (ポート名の昇順)
          Outputs: (string * uint64)[] }

    type TestbenchRun =
        { Cycles: CycleRecord list
          FinalOutputs: Map<string, uint64>
          Memory: MemoryImage }

    let private applyPorts (c: CompiledNetlist) (writes: (YosysPortBits * uint64) list) (s: SimState) =
        let inputs =
            writes
            |> List.map (fun (port, value) -> portInputs port value)
            |> List.fold (fun acc m -> Map.fold (fun a k v -> Map.add k v a) acc m) Map.empty
        apply c inputs s

    let private readOutputs (c: CompiledNetlist) (ports: YosysPortBits list) (s: SimState) =
        ports
        |> List.filter (fun p -> p.Direction = OutputPort)
        |> traverse (fun p -> readPort c s p |> Result.map (fun v -> p.Name, v))
        |> Result.map Map.ofList

    let private readBus (c: CompiledNetlist) (bus: BusPorts) (s: SimState) =
        match readPort c s bus.Addr, readPort c s bus.MemRead, readPort c s bus.MemWrite, readPort c s bus.DataOut with
        | Ok addr, Ok memRead, Ok memWrite, Ok dataOut ->
            let isRead = (memRead = 1UL)
            let isWrite = (memWrite = 1UL)
            Ok (uint16 addr, isRead, isWrite, byte dataOut)
        | Error e, _, _, _
        | _, Error e, _, _
        | _, _, Error e, _
        | _, _, _, Error e -> Error e

    /// DESIGN-VERIFY.md §5.2 の手順でリセットと cycles 周期を実行する。
    let run
        (c: CompiledNetlist)
        (ports: YosysPortBits list)
        (bus: BusPorts)
        (memory: MemoryImage)
        (rstPulses: int)
        (cycles: int)
        : Result<TestbenchRun, TestbenchError> =
        let runCore () =
            match planOutputs c ports, planBus c bus with
            | Error e, _ | _, Error e -> Error e
            | Ok outputPlan, Ok busPlan ->
                let resetPulse (s: SimState) =
                    s
                    |> applyPorts c [ bus.Reset, 1UL; bus.Clock, 0UL ]
                    |> Result.bind (applyPorts c [ bus.Clock, 1UL ])

                let rec loop (k: int) (s: SimState) (mem: MemoryImage) (acc: CycleRecord list) =
                    if k = cycles then
                        Ok { Cycles = List.rev acc
                             FinalOutputs = readOutputsPlanned s outputPlan |> Map.ofArray
                             Memory = mem }
                    else
                        // この周期の LY (0xFF44) を反映してからバスを見る
                        let mem = setLcdY mem k
                        // runner は rst=0 を書いた直後に最初の clk=0 settle を行う
                        let lowWrites = if k = 0 then [ bus.Reset, 0UL; bus.Clock, 0UL ] else [ bus.Clock, 0UL ]
                        // バスは解決済みインデックスから直接読む (Map を引かない)
                        let readLow (sLow: SimState) =
                            let addr = readPlanned sLow busPlan.Addr
                            let memRead = readPlanned sLow busPlan.MemRead
                            let memWrite = readPlanned sLow busPlan.MemWrite
                            let dataOut = readPlanned sLow busPlan.DataOut
                            let intAck = match busPlan.IntAck with Some p -> readPlanned sLow p | None -> 0UL
                            (sLow, (uint16 addr, memRead = 1UL, memWrite = 1UL, byte dataOut), byte intAck)
                        match applyPorts c lowWrites s with
                        | Error e -> Error e
                        | Ok sLow0 ->
                            let sLow, (addr, memRead, memWrite, dataOut), intAck = readLow sLow0
                            // 書込 → 割込み受付で IF を下ろす → 読出 (runner の memory_program.rs と同じ順序)
                            let mem' = if memWrite then writeMemory mem addr dataOut else mem
                            let mem'' = acknowledgeInterrupts mem' intAck
                            let dataIn = if memRead then uint64 (readMemory mem'' addr) else 0UL
                            let irq = uint64 (pendingInterrupts mem'')
                            let inputWrites =
                                match bus.Interrupt with
                                | Some ports -> [ bus.DataIn, dataIn; ports.Irq, irq ]
                                | None -> [ bus.DataIn, dataIn ]
                            let recordedIrq = if bus.Interrupt.IsSome then irq else 0UL
                            match sLow |> applyPorts c inputWrites |> Result.bind (applyPorts c [ bus.Clock, 1UL ]) with
                            | Error e -> Error e
                            | Ok sHigh ->
                                let outputs = readOutputsPlanned sHigh outputPlan
                                loop (k + 1) sHigh mem'' ({ DataIn = dataIn; Irq = recordedIrq; Outputs = outputs } :: acc)

                [ 1 .. rstPulses ]
                |> List.fold (fun acc _ -> Result.bind resetPulse acc) (Ok (initial c))
                |> Result.bind (fun s -> loop 0 s memory [])
        runCore () |> Result.mapError SimulationFailed

    /// expect / expectMem を最終状態と照合し、不一致の説明を返す (空なら合格)。
    let checkExpectations (program: MemoryProgram) (result: TestbenchRun) : string list =
        [ for KeyValue (port, expected) in program.Expect do
            match Map.tryFind port result.FinalOutputs with
            | None -> yield sprintf "%s: 出力ポートがない" port
            | Some got when got <> expected -> yield sprintf "%s: expected 0x%X got 0x%X" port expected got
            | Some _ -> ()
          for KeyValue (addr, expected) in program.ExpectMem do
            let got = readMemory result.Memory addr
            if got <> expected then
                yield sprintf "mem[0x%04X]: expected 0x%02X got 0x%02X" addr expected got ]

    // --- golden JSON ----------------------------------------------------------

    [<Literal>]
    let GoldenFormat = "wwc-golden/1"

    type GoldenInfo =
        { GoldenCircuit: string
          GoldenProgram: string
          /// 回路の verilog JSON の SHA-256 (routed meta の sourceSha256 と一致するはず)
          SourceSha256: string
          /// ROM バイト列の SHA-256
          RomSha256: string
          /// ブート ROM バイト列の SHA-256 (ブート ROM を使うプログラムのみ)
          BootRomSha256: string option
          RstPulses: int }

    let goldenToJson (info: GoldenInfo) (cycles: CycleRecord list) : string =
        use stream = new MemoryStream ()
        use w = new Utf8JsonWriter (stream, JsonWriterOptions (Indented = true))
        w.WriteStartObject ()
        w.WriteString ("format", GoldenFormat)
        w.WriteString ("circuit", info.GoldenCircuit)
        w.WriteString ("program", info.GoldenProgram)
        w.WriteString ("sourceSha256", info.SourceSha256)
        w.WriteString ("romSha256", info.RomSha256)
        match info.BootRomSha256 with
        | Some sha -> w.WriteString ("bootRomSha256", sha)
        | None -> ()
        w.WriteNumber ("rstPulses", info.RstPulses)
        w.WriteStartArray "cycles"
        for cycle in cycles do
            w.WriteStartObject ()
            w.WriteNumber ("dataIn", cycle.DataIn)
            w.WriteNumber ("irq", cycle.Irq)
            w.WriteStartObject "outputs"
            for (name, value) in cycle.Outputs do
                w.WriteNumber (name, value)
            w.WriteEndObject ()
            w.WriteEndObject ()
        w.WriteEndArray ()
        w.WriteEndObject ()
        w.Flush ()
        Text.Encoding.UTF8.GetString (stream.ToArray ())
