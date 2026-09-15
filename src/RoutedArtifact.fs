namespace WwHdl

// ---------------------------------------------------------------------
// 10. RoutedArtifact — 配線済みグリッドの保存・再読込
//
// 大規模回路の compileWL は数十分〜数時間かかる (sm83_subset 約 100 分、
// sm83_full 5〜8 時間見込み)。結果を「grid .bin + meta JSON」の組として保存し、
// 検証段 (GPU / F# settle) は再配線せずにこれを読み込む。
//
// 成果物 (<dir>/<circuit>.bin, <dir>/<circuit>.meta.json):
//   * .bin  — WireLevel.exportGrid 形式 (GPU と共通)。座標は (0,0) 基点に正規化
//   * .meta — ポート名 → ビット毎の座標 (LSB first、.bin と同じ正規化座標)、
//             元 verilog JSON の SHA-256 (陳腐化検出)、生成時の git commit (監査用)
//
// yosys の bits 配列は数値ネットと定数文字列 ("0"/"1") が混在する。
// Pipeline.parseYosysJson は定数を捨てるためビット位置がずれる → ここでは位置を保って保持する。
// ---------------------------------------------------------------------
module RoutedArtifact =
    open System
    open System.IO
    open System.Text.Json
    open Domain
    open Netlist
    open WireLevel
    open PipelineWL

    /// meta JSON の形式バージョン。互換性のない変更で上げる。
    [<Literal>]
    let CurrentFormatVersion = 1

    type PortDirection =
        | InputPort
        | OutputPort

    /// yosys ポートの 1 ビットの値源。
    type PortBit =
        | NetBit of NetId
        | ConstBit of bool

    type YosysPortBits =
        { Name: string
          Direction: PortDirection
          /// LSB first。定数ビットも位置を保って含む。
          Bits: PortBit list }

    /// 出力ビットを grid 上で読む方法。
    type OutputProbe =
        /// セルのレベル bit0 を読む (駆動ゲートの LNand/LDff、または入力直結の Pin)。
        | CellProbe of Coord
        /// yosys が定数に畳んだビット。grid 上に実体はない。
        | ConstProbe of bool
        /// 駆動元が grid 上に存在しない (未駆動ネット)。
        | Unobservable of NetId

    type RoutedMeta =
        { FormatVersion: int
          Circuit: string
          SourceSha256: string
          GitCommit: string
          CreatedAtUtc: DateTimeOffset
          Width: int
          Height: int
          /// 配線時の座標系で .bin の (0,0) にあたる点。
          Origin: Coord
          GateCount: int
          DffCount: int
          /// 入力ポート名 → ピンセル座標 (正規化、LSB first)。
          Inputs: Map<string, Coord list>
          /// 出力ポート名 → ビット毎の観測方法 (正規化、LSB first)。
          Outputs: Map<string, OutputProbe list> }

    /// 生成時の環境情報。時刻・git の取得 (副作用) は呼び出し側で行って渡す。
    type Provenance =
        { GitCommit: string
          CreatedAtUtc: DateTimeOffset }

    type ArtifactError =
        | SourceParseFailed of reason: string
        | UnsupportedPortDirection of port: string * direction: string
        | ConstantInputBit of port: string * bitIndex: int
        | MissingInputPin of port: string * net: NetId
        | FileMissing of path: string
        | MetaParseFailed of path: string * reason: string
        | FormatVersionMismatch of expected: int * actual: int
        | GridSizeMismatch of meta: (int * int) * bin: (int * int)
        | BinTruncated of path: string * expectedBytes: int * actualBytes: int
        | ProbeCellMismatch of port: string * bitIndex: int * at: Coord * found: LCell
        | StaleSource of circuit: string * metaSha: string * currentSha: string

    let describeError (e: ArtifactError) : string =
        let short (s: string) = if s.Length > 12 then s.Substring (0, 12) else s
        match e with
        | SourceParseFailed reason -> sprintf "verilog JSON のパースに失敗: %s" reason
        | UnsupportedPortDirection (port, dir) -> sprintf "ポート %s の方向 %s は未対応 (input/output のみ)" port dir
        | ConstantInputBit (port, i) -> sprintf "入力ポート %s[%d] が定数に畳まれている" port i
        | MissingInputPin (port, NetId n) -> sprintf "入力ポート %s のネット %d にピンが配置されていない" port n
        | FileMissing path -> sprintf "ファイルがない: %s" path
        | MetaParseFailed (path, reason) -> sprintf "meta JSON のパースに失敗 (%s): %s" path reason
        | FormatVersionMismatch (expected, actual) -> sprintf "meta 形式バージョン不一致: 期待 %d / 実際 %d" expected actual
        | GridSizeMismatch ((mw, mh), (bw, bh)) -> sprintf "meta の寸法 %dx%d と .bin の寸法 %dx%d が一致しない" mw mh bw bh
        | BinTruncated (path, expected, actual) -> sprintf ".bin が不完全: %s (期待 %d byte / 実際 %d byte)" path expected actual
        | ProbeCellMismatch (port, i, c, cell) -> sprintf "%s[%d] の座標 (%d,%d) のセルが想定外: %A" port i c.X c.Y cell
        | StaleSource (circuit, metaSha, currentSha) ->
            sprintf "%s の配線結果は現在と異なる verilog JSON から生成されている (meta %s… / 現在 %s…)"
                circuit (short metaSha) (short currentSha)

    let private traverse (f: 'a -> Result<'b, 'e>) (xs: 'a list) : Result<'b list, 'e> =
        let folder x acc =
            match f x, acc with
            | Ok y, Ok ys -> Ok (y :: ys)
            | Error e, _ -> Error e
            | _, Error e -> Error e
        List.foldBack folder xs (Ok [])

    /// ファイルのバイト列の SHA-256 (小文字 hex)。`sha256sum` の出力と一致する。
    let sourceSha256 (sourceBytes: byte[]) : string =
        Security.Cryptography.SHA256.HashData sourceBytes
        |> Convert.ToHexString
        |> fun hex -> hex.ToLowerInvariant ()

    // --- yosys ポート解析 -------------------------------------------------

    let private parseBit (el: JsonElement) : Result<PortBit, string> =
        match el.ValueKind with
        | JsonValueKind.Number -> Ok (NetBit (NetId (el.GetInt32 ())))
        | JsonValueKind.String ->
            match el.GetString () with
            | "0" -> Ok (ConstBit false)
            | "1" -> Ok (ConstBit true)
            | other -> Error (sprintf "unsupported constant bit %A (x/z は未対応)" other)
        | kind -> Error (sprintf "unexpected bit kind %A" kind)

    let private parseDirection (port: string) (dir: string) : Result<PortDirection, ArtifactError> =
        match dir with
        | "input" -> Ok InputPort
        | "output" -> Ok OutputPort
        | other -> Error (UnsupportedPortDirection (port, other))

    /// yosys JSON のトップモジュールのポートを、定数ビットの位置を保って読む。
    /// モジュール選択は Pipeline.parseYosysJson と同じ ("top" 優先、なければ先頭)。
    let parseYosysPorts (json: string) : Result<YosysPortBits list, ArtifactError> =
        try
            use doc = JsonDocument.Parse json
            let modules = doc.RootElement.GetProperty("modules").EnumerateObject () |> Array.ofSeq
            let topModule =
                modules
                |> Array.tryFind (fun m -> m.Name = "top")
                |> Option.orElse (Array.tryHead modules)
            match topModule with
            | None -> Error (SourceParseFailed "no modules in JSON")
            | Some top ->
                top.Value.GetProperty("ports").EnumerateObject ()
                |> List.ofSeq
                |> traverse (fun port ->
                    let bits =
                        port.Value.GetProperty("bits").EnumerateArray ()
                        |> List.ofSeq
                        |> traverse parseBit
                        |> Result.mapError (fun reason -> SourceParseFailed (sprintf "port %s: %s" port.Name reason))
                    parseDirection port.Name (port.Value.GetProperty("direction").GetString ())
                    |> Result.bind (fun dir ->
                        bits
                        |> Result.map (fun bs -> ({ Name = port.Name; Direction = dir; Bits = bs } : YosysPortBits))))
        with ex ->
            Error (SourceParseFailed ex.Message)

    // --- meta 構築 --------------------------------------------------------

    /// exportGrid と同じ正規化基点と寸法 (origin, width, height)。
    let gridBounds (g: LGrid) : Coord * int * int =
        if Map.isEmpty g then { X = 0; Y = 0 }, 0, 0 else
        let coords = g |> Map.toSeq |> Seq.map fst |> Array.ofSeq
        let minX = coords |> Array.map (fun c -> c.X) |> Array.min
        let maxX = coords |> Array.map (fun c -> c.X) |> Array.max
        let minY = coords |> Array.map (fun c -> c.Y) |> Array.min
        let maxY = coords |> Array.map (fun c -> c.Y) |> Array.max
        { X = minX; Y = minY }, maxX - minX + 1, maxY - minY + 1

    /// compileWL の結果とポート情報から meta を組み立てる (純粋関数)。
    let buildMeta
        (circuit: string)
        (sourceSha: string)
        (provenance: Provenance)
        (ports: YosysPortBits list)
        (grid: LGrid)
        (placed: WlPlaced list)
        (pins: Map<NetId, Coord>)
        : Result<RoutedMeta, ArtifactError> =
        let origin, width, height = gridBounds grid
        let normalize (c: Coord) = { X = c.X - origin.X; Y = c.Y - origin.Y }
        let driverCoord = placed |> List.map (fun p -> p.Gate.Output, p.Coord) |> Map.ofList

        let inputCoords (port: YosysPortBits) : Result<Coord list, ArtifactError> =
            port.Bits
            |> List.indexed
            |> traverse (fun (i, bit) ->
                match bit with
                | ConstBit _ -> Error (ConstantInputBit (port.Name, i))
                | NetBit net ->
                    match Map.tryFind net pins with
                    | Some c -> Ok (normalize c)
                    | None -> Error (MissingInputPin (port.Name, net)))

        // 出力はゲート出力セルで観測する。入力直結 (パススルー) ならピンセル。
        let outputProbe (bit: PortBit) : OutputProbe =
            match bit with
            | ConstBit v -> ConstProbe v
            | NetBit net ->
                match Map.tryFind net driverCoord, Map.tryFind net pins with
                | Some c, _ -> CellProbe (normalize c)
                | None, Some c -> CellProbe (normalize c)
                | None, None -> Unobservable net

        let inputPorts = ports |> List.filter (fun p -> p.Direction = InputPort)
        let outputPorts = ports |> List.filter (fun p -> p.Direction = OutputPort)

        inputPorts
        |> traverse (fun p -> inputCoords p |> Result.map (fun cs -> p.Name, cs))
        |> Result.map (fun inputs ->
            let outputs = outputPorts |> List.map (fun p -> p.Name, p.Bits |> List.map outputProbe)
            let dffCount = placed |> List.filter (fun p -> p.Gate.Kind = Dff) |> List.length
            ({ FormatVersion = CurrentFormatVersion
               Circuit = circuit
               SourceSha256 = sourceSha
               GitCommit = provenance.GitCommit
               CreatedAtUtc = provenance.CreatedAtUtc
               Width = width
               Height = height
               Origin = origin
               GateCount = placed.Length
               DffCount = dffCount
               Inputs = Map.ofList inputs
               Outputs = Map.ofList outputs } : RoutedMeta))

    // --- meta JSON --------------------------------------------------------

    let private writeCoord (w: Utf8JsonWriter) (c: Coord) =
        w.WriteStartObject ()
        w.WriteNumber ("x", c.X)
        w.WriteNumber ("y", c.Y)
        w.WriteEndObject ()

    let private writeProbe (w: Utf8JsonWriter) (probe: OutputProbe) =
        match probe with
        | CellProbe c -> writeCoord w c
        | ConstProbe v ->
            w.WriteStartObject ()
            w.WriteNumber ("const", (if v then 1 else 0))
            w.WriteEndObject ()
        | Unobservable (NetId net) ->
            w.WriteStartObject ()
            w.WriteNumber ("unobservable", net)
            w.WriteEndObject ()

    let private writeMeta (w: Utf8JsonWriter) (meta: RoutedMeta) =
        w.WriteStartObject ()
        w.WriteNumber ("formatVersion", meta.FormatVersion)
        w.WriteString ("circuit", meta.Circuit)
        w.WriteString ("sourceSha256", meta.SourceSha256)
        w.WriteString ("gitCommit", meta.GitCommit)
        w.WriteString ("createdAtUtc", meta.CreatedAtUtc)
        w.WriteNumber ("width", meta.Width)
        w.WriteNumber ("height", meta.Height)
        w.WritePropertyName "origin"
        writeCoord w meta.Origin
        w.WriteNumber ("gateCount", meta.GateCount)
        w.WriteNumber ("dffCount", meta.DffCount)
        w.WriteStartObject "inputs"
        for KeyValue (name, coords) in meta.Inputs do
            w.WriteStartArray name
            for c in coords do
                writeCoord w c
            w.WriteEndArray ()
        w.WriteEndObject ()
        w.WriteStartObject "outputs"
        for KeyValue (name, probes) in meta.Outputs do
            w.WriteStartArray name
            for p in probes do
                writeProbe w p
            w.WriteEndArray ()
        w.WriteEndObject ()
        w.WriteEndObject ()

    let metaToJson (meta: RoutedMeta) : string =
        use stream = new MemoryStream ()
        use w = new Utf8JsonWriter (stream, JsonWriterOptions (Indented = true))
        writeMeta w meta
        w.Flush ()
        Text.Encoding.UTF8.GetString (stream.ToArray ())

    let private readCoord (el: JsonElement) : Coord =
        { X = el.GetProperty("x").GetInt32 (); Y = el.GetProperty("y").GetInt32 () }

    let private readProbe (el: JsonElement) : OutputProbe =
        let mutable value = Unchecked.defaultof<JsonElement>
        if el.TryGetProperty ("const", &value) then ConstProbe (value.GetInt32 () = 1)
        elif el.TryGetProperty ("unobservable", &value) then Unobservable (NetId (value.GetInt32 ()))
        else CellProbe (readCoord el)

    let private readBitMap (root: JsonElement) (name: string) (readItem: JsonElement -> 'T) : Map<string, 'T list> =
        root.GetProperty(name).EnumerateObject ()
        |> Seq.map (fun p -> p.Name, (p.Value.EnumerateArray () |> Seq.map readItem |> List.ofSeq))
        |> Map.ofSeq

    /// meta JSON を読む。source はエラーメッセージ用のラベル (通常はファイルパス)。
    let metaOfJson (source: string) (json: string) : Result<RoutedMeta, ArtifactError> =
        try
            use doc = JsonDocument.Parse json
            let root = doc.RootElement
            let version = root.GetProperty("formatVersion").GetInt32 ()
            if version <> CurrentFormatVersion then
                Error (FormatVersionMismatch (CurrentFormatVersion, version))
            else
                Ok ({ FormatVersion = version
                      Circuit = root.GetProperty("circuit").GetString ()
                      SourceSha256 = root.GetProperty("sourceSha256").GetString ()
                      GitCommit = root.GetProperty("gitCommit").GetString ()
                      CreatedAtUtc = root.GetProperty("createdAtUtc").GetDateTimeOffset ()
                      Width = root.GetProperty("width").GetInt32 ()
                      Height = root.GetProperty("height").GetInt32 ()
                      Origin = readCoord (root.GetProperty "origin")
                      GateCount = root.GetProperty("gateCount").GetInt32 ()
                      DffCount = root.GetProperty("dffCount").GetInt32 ()
                      Inputs = readBitMap root "inputs" readCoord
                      Outputs = readBitMap root "outputs" readProbe } : RoutedMeta)
        with ex ->
            Error (MetaParseFailed (source, ex.Message))

    // --- 整合性・鮮度 -------------------------------------------------------

    /// meta の座標が grid 上の想定セルを指しているか (入力=Pin、出力=Pin/LNand/LDff)。
    let validateAgainstGrid (grid: LGrid) (meta: RoutedMeta) : Result<RoutedMeta, ArtifactError> =
        let inputErrors =
            meta.Inputs
            |> Map.toSeq
            |> Seq.collect (fun (name, coords) ->
                coords
                |> Seq.mapi (fun i c ->
                    match getL grid c with
                    | Pin _ -> None
                    | cell -> Some (ProbeCellMismatch (name, i, c, cell))))
        let outputErrors =
            meta.Outputs
            |> Map.toSeq
            |> Seq.collect (fun (name, probes) ->
                probes
                |> Seq.mapi (fun i probe ->
                    match probe with
                    | ConstProbe _ | Unobservable _ -> None
                    | CellProbe c ->
                        match getL grid c with
                        | Pin _ | LNand _ | LDff _ -> None
                        | cell -> Some (ProbeCellMismatch (name, i, c, cell))))
        match Seq.append inputErrors outputErrors |> Seq.choose id |> Seq.tryHead with
        | Some e -> Error e
        | None -> Ok meta

    /// 保存済み成果物が現在の verilog JSON から作られたものか。
    let ensureFresh (currentSha: string) (meta: RoutedMeta) : Result<RoutedMeta, ArtifactError> =
        if meta.SourceSha256 = currentSha then Ok meta
        else Error (StaleSource (meta.Circuit, meta.SourceSha256, currentSha))

    // --- ファイル入出力 (副作用) ---------------------------------------------

    let binPath (dir: string) (circuit: string) = Path.Combine (dir, circuit + ".bin")
    let metaPath (dir: string) (circuit: string) = Path.Combine (dir, circuit + ".meta.json")

    /// 一時ファイルに書いてから置き換える。書込途中で中断しても既存の成果物を壊さない。
    let private writeAtomically (path: string) (write: string -> unit) =
        let tmp = path + ".tmp"
        write tmp
        File.Move (tmp, path, true)

    /// .bin → .meta.json の順に書く。meta が新しければ対応する .bin も書き終わっている。
    let save (dir: string) (meta: RoutedMeta) (grid: LGrid) : unit =
        Directory.CreateDirectory dir |> ignore
        writeAtomically (binPath dir meta.Circuit) (fun p -> File.WriteAllBytes (p, exportGrid grid))
        writeAtomically (metaPath dir meta.Circuit) (fun p -> File.WriteAllText (p, metaToJson meta))

    let private readBinSize (data: byte[]) : int * int =
        if data.Length < 8 then 0, 0 else
        let le i =
            int data.[i] ||| (int data.[i + 1] <<< 8) ||| (int data.[i + 2] <<< 16) ||| (int data.[i + 3] <<< 24)
        le 0, le 4

    /// 成果物を読み込み、寸法・ファイル長・プローブ座標の整合性を確認する。
    /// 返す grid は正規化座標系 (meta の座標と同じ)。鮮度確認は ensureFresh で別途行う。
    let load (dir: string) (circuit: string) : Result<LGrid * RoutedMeta, ArtifactError> =
        let mp = metaPath dir circuit
        let bp = binPath dir circuit
        if not (File.Exists mp) then Error (FileMissing mp)
        elif not (File.Exists bp) then Error (FileMissing bp)
        else
            metaOfJson mp (File.ReadAllText mp)
            |> Result.bind (fun meta ->
                let data = File.ReadAllBytes bp
                let binW, binH = readBinSize data
                let expectedBytes = 8 + binW * binH
                if (binW, binH) <> (meta.Width, meta.Height) then
                    Error (GridSizeMismatch ((meta.Width, meta.Height), (binW, binH)))
                elif data.Length <> expectedBytes then
                    Error (BinTruncated (bp, expectedBytes, data.Length))
                else
                    let grid = importGrid data
                    validateAgainstGrid grid meta |> Result.map (fun m -> grid, m))
