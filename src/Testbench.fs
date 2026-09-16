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
//   1 周期:   clk=0 で settle (最初の周期は rst=0 も反映) → addr/mem_read/mem_write/data_out を読む
//             → mem_write なら書込 → mem_read なら data_in=mem[addr]、そうでなければ 0
//             → data_in を書いて settle → clk=1 で settle → 全出力を読む
//   メモリ:   ROM (0 から ROM 長) → RAM 窓 (ramBase から ramSize) → それ以外は 0xFF。書込は RAM 窓のみ
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

    type MemoryImage =
        { Rom: byte[]
          Ram: byte[]
          Config: MemoryConfig }

    let createMemory (rom: byte[]) (config: MemoryConfig) : MemoryImage =
        { Rom = Array.copy rom
          Ram = Array.zeroCreate config.RamSize
          Config = config }

    let private ramOffset (m: MemoryImage) (addr: uint16) : int option =
        let a = int addr
        if a >= m.Config.RamBase && a < m.Config.RamBase + m.Ram.Length then Some (a - m.Config.RamBase)
        else None

    let readMemory (m: MemoryImage) (addr: uint16) : byte =
        if int addr < m.Rom.Length then m.Rom.[int addr]
        else
            match ramOffset m addr with
            | Some i -> m.Ram.[i]
            | None -> 0xFFuy

    /// RAM 窓への書込だけ反映した新しいイメージを返す (ROM への書込は無視)。
    let writeMemory (m: MemoryImage) (addr: uint16) (value: byte) : MemoryImage =
        match ramOffset m addr with
        | Some i ->
            let ram = Array.copy m.Ram
            ram.[i] <- value
            { m with Ram = ram }
        | None -> m

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
        | SimulationFailed of SimError

    let describeTestbenchError (e: TestbenchError) : string =
        match e with
        | ProgramParseFailed (path, reason) -> sprintf "プログラム JSON のパースに失敗 (%s): %s" path reason
        | RomLoadFailed reason -> sprintf "ROM の読込に失敗: %s" reason
        | MissingBusPort port -> sprintf "バスポート %s が回路にない" port
        | BusPortDirection (port, expected) -> sprintf "バスポート %s の方向が %A ではない" port expected
        | BusPortWidth (port, expected, actual) -> sprintf "バスポート %s の幅が %d ではなく %d" port expected actual
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

    type BusPorts =
        { Clock: YosysPortBits
          Reset: YosysPortBits
          DataIn: YosysPortBits
          Addr: YosysPortBits
          DataOut: YosysPortBits
          MemRead: YosysPortBits
          MemWrite: YosysPortBits }

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
        |> Result.map (fun resolved ->
            match resolved with
            | [ clk; rst; dataIn; addr; dataOut; memRead; memWrite ] ->
                { Clock = clk; Reset = rst; DataIn = dataIn; Addr = addr
                  DataOut = dataOut; MemRead = memRead; MemWrite = memWrite }
            | other -> invalidOp (sprintf "traverse は入力と同数を返すはず (got %d)" other.Length))

    // --- 実行 ---------------------------------------------------------------

    type CycleRecord =
        { /// この周期で書いた data_in
          DataIn: uint64
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
                match applyPorts c lowWrites s |> Result.bind (fun sLow -> readBus c bus sLow |> Result.map (fun b -> sLow, b)) with
                | Error e -> Error e
                | Ok (sLow, (addr, memRead, memWrite, dataOut)) ->
                    let mem' = if memWrite then writeMemory mem addr dataOut else mem
                    let dataIn = if memRead then uint64 (readMemory mem' addr) else 0UL
                    let high =
                        sLow
                        |> applyPorts c [ bus.DataIn, dataIn ]
                        |> Result.bind (applyPorts c [ bus.Clock, 1UL ])
                        |> Result.bind (fun sHigh -> readOutputs c ports sHigh |> Result.map (fun o -> sHigh, o))
                    match high with
                    | Error e -> Error e
                    | Ok (sHigh, outputs) -> loop (k + 1) sHigh mem' ({ DataIn = dataIn; Outputs = outputs } :: acc)

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
            w.WriteStartObject "outputs"
            for KeyValue (name, value) in cycle.Outputs do
                w.WriteNumber (name, value)
            w.WriteEndObject ()
            w.WriteEndObject ()
        w.WriteEndArray ()
        w.WriteEndObject ()
        w.Flush ()
        Text.Encoding.UTF8.GetString (stream.ToArray ())
