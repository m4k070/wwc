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
//   メモリ:   ROM (0 から ROM 長) → RAM 窓 (ramBase から ramSize) → I/O (IF 0xFF0F、HRAM 0xFF80-0xFFFE、
//             IE 0xFFFF) → それ以外は 0xFF。書込は RAM 窓と I/O のみ
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

    type MemoryImage =
        { Rom: byte[]
          Ram: byte[]
          Config: MemoryConfig
          /// 0xFF80-0xFFFE
          Hram: byte[]
          /// IF (0xFF0F)。書いたバイトをそのまま保持する (gbfs と同じ。実機は上位 3bit が 1 で読める)
          InterruptFlag: byte
          /// IE (0xFFFF)
          InterruptEnable: byte }

    let createMemory (rom: byte[]) (config: MemoryConfig) : MemoryImage =
        { Rom = Array.copy rom
          Ram = Array.zeroCreate config.RamSize
          Config = config
          Hram = Array.zeroCreate HramSize
          InterruptFlag = 0uy
          InterruptEnable = 0uy }

    let private ramOffset (m: MemoryImage) (addr: uint16) : int option =
        let a = int addr
        if a >= m.Config.RamBase && a < m.Config.RamBase + m.Ram.Length then Some (a - m.Config.RamBase)
        else None

    let private isHram (a: int) = a >= HramBase && a < HramBase + HramSize

    let readMemory (m: MemoryImage) (addr: uint16) : byte =
        let a = int addr
        if a < m.Rom.Length then m.Rom.[a]
        else
            match ramOffset m addr with
            | Some i -> m.Ram.[i]
            | None when a = InterruptFlagAddress -> m.InterruptFlag
            | None when a = InterruptEnableAddress -> m.InterruptEnable
            | None when isHram a -> m.Hram.[a - HramBase]
            | None -> 0xFFuy

    /// RAM 窓と I/O (IF / HRAM / IE) への書込を反映した新しいイメージを返す (ROM への書込は無視)。
    /// RAM 窓が I/O 領域と重なる場合は RAM 窓を優先する。
    let writeMemory (m: MemoryImage) (addr: uint16) (value: byte) : MemoryImage =
        let a = int addr
        match ramOffset m addr with
        | Some i ->
            let ram = Array.copy m.Ram
            ram.[i] <- value
            { m with Ram = ram }
        | None when a = InterruptFlagAddress -> { m with InterruptFlag = value }
        | None when a = InterruptEnableAddress -> { m with InterruptEnable = value }
        | None when isHram a ->
            let hram = Array.copy m.Hram
            hram.[a - HramBase] <- value
            { m with Hram = hram }
        | None -> m

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

    type CycleRecord =
        { /// この周期で書いた data_in
          DataIn: uint64
          /// この周期で書いた irq (割込みポートがなければ 0)
          Irq: uint64
          /// clk=1 で settle した後の全出力ポート
          Outputs: Map<string, uint64> }

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
        let resetPulse (s: SimState) =
            s
            |> applyPorts c [ bus.Reset, 1UL; bus.Clock, 0UL ]
            |> Result.bind (applyPorts c [ bus.Clock, 1UL ])

        let rec loop (k: int) (s: SimState) (mem: MemoryImage) (acc: CycleRecord list) =
            if k = cycles then
                readOutputs c ports s
                |> Result.map (fun final -> { Cycles = List.rev acc; FinalOutputs = final; Memory = mem })
            else
                // runner は rst=0 を書いた直後に最初の clk=0 settle を行う
                let lowWrites = if k = 0 then [ bus.Reset, 0UL; bus.Clock, 0UL ] else [ bus.Clock, 0UL ]
                let readLow sLow =
                    readBus c bus sLow
                    |> Result.bind (fun b ->
                        match bus.Interrupt with
                        | Some ports -> readPort c sLow ports.IntAck |> Result.map (fun ack -> sLow, b, byte ack)
                        | None -> Ok (sLow, b, 0uy))
                match applyPorts c lowWrites s |> Result.bind readLow with
                | Error e -> Error e
                | Ok (sLow, (addr, memRead, memWrite, dataOut), intAck) ->
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
                    let high =
                        sLow
                        |> applyPorts c inputWrites
                        |> Result.bind (applyPorts c [ bus.Clock, 1UL ])
                        |> Result.bind (fun sHigh -> readOutputs c ports sHigh |> Result.map (fun o -> sHigh, o))
                    match high with
                    | Error e -> Error e
                    | Ok (sHigh, outputs) ->
                        loop (k + 1) sHigh mem'' ({ DataIn = dataIn; Irq = recordedIrq; Outputs = outputs } :: acc)

        [ 1 .. rstPulses ]
        |> List.fold (fun acc _ -> Result.bind resetPulse acc) (Ok (initial c))
        |> Result.bind (fun s -> loop 0 s memory [])
        |> Result.mapError SimulationFailed

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
        w.WriteNumber ("rstPulses", info.RstPulses)
        w.WriteStartArray "cycles"
        for cycle in cycles do
            w.WriteStartObject ()
            w.WriteNumber ("dataIn", cycle.DataIn)
            w.WriteNumber ("irq", cycle.Irq)
            w.WriteStartObject "outputs"
            for KeyValue (name, value) in cycle.Outputs do
                w.WriteNumber (name, value)
            w.WriteEndObject ()
            w.WriteEndObject ()
        w.WriteEndArray ()
        w.WriteEndObject ()
        w.Flush ()
        Text.Encoding.UTF8.GetString (stream.ToArray ())
