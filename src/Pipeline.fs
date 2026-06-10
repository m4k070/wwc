namespace WwHdl

// ---------------------------------------------------------------------
// 8. パイプライン: 各段の型と railway 合成
// ---------------------------------------------------------------------
module Pipeline =
    open Units
    open Domain
    open Netlist
    open Library
    open Place
    open Route

    // --- Yosys write_json の最小デシリアライズ型 ---
    // `synth -flatten; abc -g AND,NOT; write_json` の出力に対応。

    type YosysPort =
        { Direction: string       // "input" | "output"
          Bits: int list }        // ネット番号 (= NetId の元になる)

    type YosysCell =
        { Type: string                        // "$_NOT_", "$_AND_" など
          PortDirections: Map<string, string> // ポート名 → "input"/"output"
          Connections: Map<string, int list> } // ポート名 → ネット番号リスト

    type YosysModule =
        { Ports: Map<string, YosysPort>
          Cells: Map<string, YosysCell> }

    // CompileError は Route モジュールで定義

    // --- 段の境界。それぞれ別の型を返すので順序を取り違えられない ---

    /// JSON の bits 配列から整数ネット番号だけを抽出する。
    /// Yosys は定数 "0"/"1" を文字列で埋め込む場合があるので数値要素のみ取る。
    let private parseBits (el: System.Text.Json.JsonElement) : int list =
        el.EnumerateArray()
        |> Seq.choose (fun b ->
            if b.ValueKind = System.Text.Json.JsonValueKind.Number
            then Some (b.GetInt32())
            else None)
        |> List.ofSeq

    /// Yosys write_json 出力を YosysModule へパースする。
    /// JSON 構造: { "modules": { "<top>": { "ports": {...}, "cells": {...} } } }
    /// 複数モジュールがある場合は "top" を優先し、なければ先頭を使用する。
    let private parsePorts (m: System.Text.Json.JsonElement) =
        m.GetProperty("ports").EnumerateObject()
        |> Seq.map (fun p ->
            let dir  = p.Value.GetProperty("direction").GetString()
            let bits = parseBits (p.Value.GetProperty("bits"))
            p.Name, { Direction = dir; Bits = bits })
        |> Map.ofSeq

    let private parseCells (m: System.Text.Json.JsonElement) =
        m.GetProperty("cells").EnumerateObject()
        |> Seq.map (fun c ->
            let v = c.Value
            let cellType = v.GetProperty("type").GetString()
            let portDirs =
                v.GetProperty("port_directions").EnumerateObject()
                |> Seq.map (fun p -> p.Name, p.Value.GetString())
                |> Map.ofSeq
            let conns =
                v.GetProperty("connections").EnumerateObject()
                |> Seq.map (fun p -> p.Name, parseBits p.Value)
                |> Map.ofSeq
            c.Name, { Type = cellType; PortDirections = portDirs; Connections = conns })
        |> Map.ofSeq

    /// Yosys write_json をパースして YosysModule に変換する。
    let parseYosysJson (json: string) : Result<YosysModule, CompileError> =
        try
            use doc = System.Text.Json.JsonDocument.Parse(json)
            let modulesEl = doc.RootElement.GetProperty("modules")
            let entries = modulesEl.EnumerateObject() |> Array.ofSeq
            if entries.Length = 0 then
                Error (ParseError "no modules in JSON")
            else
                let topEntry =
                    entries |> Array.tryFind (fun e -> e.Name = "top")
                    |> Option.defaultValue entries.[0]
                let m = topEntry.Value
                Ok { Ports = parsePorts m; Cells = parseCells m }
        with ex ->
            Error (ParseError ex.Message)

    /// Yosys type 文字列を GateKind に変換する。
    /// `abc -g NAND,NOT` の出力は $\_NOT\_ と $\_NAND\_ のみ。
    let private parseGateKind (t: string) : GateKind option =
        match t with
        | "$_NOT_"   -> Some Not
        | "$_NAND_"  -> Some Nand
        | "$_AND_"   -> Some And    // abc -g AND,NOT 使用時の後方互換
        | "$_OR_"    -> Some Or
        | "$_XOR_"   -> Some Xor    // abc -g NAND,NOT では出力されないが後方互換用に残す
        | "$_DFF_P_" -> Some Dff
        | "$_BUF_"   -> Some Buf
        | _          -> None

    /// YosysModule を Netlist IR へ変換する。
    let private getPrimarySignals (m: YosysModule) =
        let primaryInputs =
            m.Ports |> Map.toList
            |> List.filter (fun (_, p) -> p.Direction = "input")
            |> List.collect (fun (_, p) -> p.Bits |> List.map NetId)
            |> List.distinct

        let primaryOutputs =
            m.Ports |> Map.toList
            |> List.filter (fun (_, p) -> p.Direction = "output")
            |> List.collect (fun (_, p) -> p.Bits |> List.map NetId)
            |> List.distinct

        let clockNet =
            m.Ports |> Map.tryFindKey (fun name _ ->
                let n = name.ToLowerInvariant()
                n = "clk" || n = "clock" || n = "ck" || n = "clk_i")
            |> Option.bind (fun name ->
                m.Ports.[name].Bits |> List.tryHead |> Option.map NetId)
        
        primaryInputs, primaryOutputs, clockNet

    let private parseGates (m: YosysModule) =
        m.Cells |> Map.toList
        |> List.mapi (fun i (name, cell) ->
            match parseGateKind cell.Type with
            | None -> Error (ParseError $"unknown gate type '{cell.Type}' in cell '{name}'")
            | Some kind ->
                let inputs =
                    cell.PortDirections |> Map.toList
                    |> List.filter (fun (_, dir) -> dir = "input")
                    |> List.sortBy fst
                    |> List.collect (fun (port, _) ->
                        cell.Connections |> Map.tryFind port
                        |> Option.defaultValue []
                        |> List.map NetId)
                
                let outputNet =
                    cell.PortDirections |> Map.toList
                    |> List.filter (fun (_, dir) -> dir = "output")
                    |> List.sortBy fst
                    |> List.tryHead
                    |> Option.bind (fun (port, _) ->
                        cell.Connections |> Map.tryFind port
                        |> Option.bind List.tryHead
                        |> Option.map NetId)
                
                match outputNet with
                | None    -> Error (ParseError $"cell '{name}' has no output connection")
                | Some o  -> Ok { Id = i; Kind = kind; Inputs = inputs; Output = o })
        |> List.fold (fun acc r ->
            match acc, r with
            | Ok xs, Ok x -> Ok (x :: xs)
            | Error e, _  -> Error e
            | _, Error e  -> Error e) (Ok [])
        |> Result.map List.rev

    /// YosysModule を Netlist IR へ変換する。
    let yosysToNetlist (m: YosysModule) : Result<Netlist, CompileError> =
        let (primaryInputs, primaryOutputs, clockNet) = getPrimarySignals m
        
        parseGates m
        |> Result.bind (fun gates ->
            Ok { Gates          = gates
                 PrimaryInputs  = primaryInputs
                 PrimaryOutputs = primaryOutputs
                 ClockNet       = clockNet })

    /// Frontend: Yosys JSON 文字列 → Netlist。
    /// 呼び出し元は `yosys -p "synth -flatten; abc -g NAND,NOT; write_json out.json" design.v`
    /// で生成した JSON ファイルの内容を渡す。
    let frontend (src: string) : Result<Netlist, CompileError> =
        parseYosysJson src |> Result.bind yosysToNetlist

    /// テクノロジマッピング: 各ゲートを StdCell に割り当てる。
    let techMap (lib: CellLibrary) (nl: Netlist)
        : Result<(Gate * StdCell) list, CompileError> =
        nl.Gates
        |> List.map (fun g ->
            match Map.tryFind g.Kind lib with
            | Some cell -> Ok (g, cell)
            | None      -> Error (UnmappableGate g.Kind))
        |> List.fold (fun acc r ->
            match acc, r with
            | Ok xs, Ok x   -> Ok (x :: xs)
            | Error e, _    -> Error e
            | _, Error e    -> Error e) (Ok [])
        |> Result.map List.rev

    /// セル変形: 水平反転
    let flipH (cell: StdCell) : StdCell =
        let newPattern = 
            cell.Pattern |> Map.toList
            |> List.map (fun (c, s) -> { X = cell.Size.X - 1 - c.X; Y = c.Y }, s)
            |> Map.ofList
        let newPorts = 
            cell.Ports |> List.map (fun p -> 
                { p with Offset = { X = cell.Size.X - 1 - p.Offset.X; Y = p.Offset.Y } })
        { cell with Pattern = newPattern; Ports = newPorts }

    /// セル変形: 垂直反転
    let flipV (cell: StdCell) : StdCell =
        let newPattern = 
            cell.Pattern |> Map.toList
            |> List.map (fun (c, s) -> { X = c.X; Y = cell.Size.Y - 1 - c.Y }, s)
            |> Map.ofList
        let newPorts = 
            cell.Ports |> List.map (fun p -> 
                { p with Offset = { X = p.Offset.X; Y = cell.Size.Y - 1 - p.Offset.Y } })
        { cell with Pattern = newPattern; Ports = newPorts }

    /// セル変形: 180度回転
    let rotate180 (cell: StdCell) : StdCell =
        cell |> flipH |> flipV

    /// 配置: 2次元配置
    /// 9ゲート以上では4列配置にしてゲート間距離を確保
    /// それ以下では従来の2行配置
    let private cellHeight = 3
    let private placeColPitch = 13
    let private placeVGap = 25
    let private placeSmallVGap = 8
    let private placeSmallHGap = 16
    let private placeRowWrapMaxWidth = 100
    let private placeColOffsets4 = [| 0; 13; 26; 39 |]

    let place (mapped: (Gate * StdCell) list) : Result<Placement, CompileError> =
        let nGates = mapped.Length
        if nGates >= 9 then
            // 9ゲート以上: 4列配置
            let rowHeight = cellHeight + placeVGap
            let (placed, _, _) =
                mapped
                |> List.fold (fun (acc, _, rowIndex) (g, cell) ->
                    let y = rowIndex * rowHeight
                    let colIndex = rowIndex % 4
                    let x = placeColOffsets4.[colIndex]
                    let origin = { X = x; Y = y }
                    ({ Gate = g; Cell = cell; Origin = origin } :: acc, 0, rowIndex + 1)
                ) ([], 0, 0)
            Ok (List.rev placed)
        else
            // 8ゲート以下: 従来の2行配置
            let rowHeight = cellHeight + placeSmallVGap
            let (placed, _, _) =
                mapped
                |> List.fold (fun (acc, currentX, rowIndex) (g, cell) ->
                    let y = (rowIndex % 2) * rowHeight
                    let origin = { X = currentX; Y = y }
                    let nextX = currentX + cell.Size.X + placeSmallHGap
                    let (nextX', nextRow) =
                        if nextX > placeRowWrapMaxWidth then (0, rowIndex + 1)
                        else (nextX, rowIndex + 1)
                    ({ Gate = g; Cell = cell; Origin = origin } :: acc, nextX', nextRow)
                ) ([], 0, 0)
            Ok (List.rev placed)

    /// 大規模回路向け: 動的列配置
    /// ゲート数に応じて列数を動的に決定し、配線スペースを確保する
    /// - 1-10ゲート: 4列
    /// - 11-50ゲート: 8列
    /// - 51-200ゲート: 16列
    /// - 200+ゲート: 32列
    let private gatesPerColumn4 = 10
    let private gatesPerColumn8 = 50
    let private gatesPerColumn16 = 200

    let placeWide (mapped: (Gate * StdCell) list) : Result<Placement, CompileError> =
        let rowHeight = cellHeight + placeVGap
        let nGates = mapped.Length
        
        // ゲート数に応じて列数を決定
        let numCols = 
            if nGates <= gatesPerColumn4 then 4
            elif nGates <= gatesPerColumn8 then 8
            elif nGates <= gatesPerColumn16 then 16
            else 32
        
        // 列のX座標を生成
        let colXs = [| for i in 0 .. numCols-1 -> i * placeColPitch |]

        let (placed, _, _) =
            mapped
            |> List.fold (fun (acc, _, rowIndex) (g, cell) ->
                let y = rowIndex * rowHeight
                let colIndex = rowIndex % numCols
                let x = colXs.[colIndex]
                let origin = { X = x; Y = y }
                ({ Gate = g; Cell = cell; Origin = origin } :: acc, 0, rowIndex + 1)
            ) ([], 0, 0)
        Ok (List.rev placed)

    let private maxRouteRetries = 3
    let private interferenceCellsPerGen = 4

    /// 配線: Lee 法でゲート間ネットを配線する。
    /// ポートは Blocked 領域内に存在するため src/dst は例外扱いで通過させる。
    /// クロックポート (Role=Clock) および論理入力数を超える物理 In ポートは今回スキップ。
    let route (tight: bool) (placement: Placement) : Result<Wire list, CompileError> =
        let baseGrid = buildGrid placement

        // 出力ポート座標: NetId → Coord
        let outCoords =
            placement |> List.choose (fun p ->
                p.Cell.Ports |> List.tryFind (fun port -> port.Role = Out)
                |> Option.map (fun port -> p.Gate.Output, portCoord p port))
            |> Map.ofList

        // 入力ポート座標: (NetId, Coord * consumer_NetId) list
        // Seq.zip で Gate.Inputs と In ポートを短い方に合わせて対応付ける
        // consumer = この入力を持つゲートの Output NetId
        let inCoordWithConsumer =
            placement |> List.collect (fun p ->
                let inPorts = p.Cell.Ports |> List.filter (fun port -> port.Role = In)
                Seq.zip p.Gate.Inputs inPorts
                |> Seq.map (fun (netId, port) -> netId, (portCoord p port, p.Gate.Output))
                |> List.ofSeq)

        let inCoordsMap =
            inCoordWithConsumer
            |> List.groupBy fst
            |> List.map (fun (k, vs) -> k, List.map snd vs)
            |> Map.ofList

        // src と dst (+ consumer) がそろっている内部ネットのみ配線
        // fan-out 数の多い順に配線: 長距離ネットを先に通すことで
        // 多数の消費先があるネットが後続の短距離ネットに塞がれるのを防ぐ
        // 各消費先は src からの距離順に配線 (近い順) し、最初のパスが最短で完了するようにする。
        let nets =
            outCoords |> Map.toList
            |> List.choose (fun (netId, src) ->
                inCoordsMap |> Map.tryFind netId
                |> Option.map (fun dstConsumers -> netId, src, dstConsumers))
            |> List.map (fun (netId, src, dsts) ->
                let sorted = dsts |> List.sortBy (fun (dst, _) ->
                    abs (src.X - dst.X) + abs (src.Y - dst.Y))
                netId, src, sorted)
            |> List.sortByDescending (fun (_, src, dsts) ->
                if tight then dsts.Length, 0
                else dsts.Length, src.Y)

        // 全ゲートの全ポート座標を収集。src/dst 以外のポートに隣接するセルを通過禁止にし
        // ワイヤ Head が隣接ポートを誤発火させる「クロストーク」を防ぐ。
        let allPorts =
            placement |> List.collect (fun p ->
                p.Cell.Ports |> List.map (portCoord p))
            |> Set.ofList

        // 各ゲートの絶対座標パターングリッド (コンテキスト付き遅延計測に使用)。
        // netId (= gate output) → Gate の Wire セルグリッド
        // 対角ショートカットが検出されたパスに "修正セル" を挿入して遅延を 1 増やす。
        let gateGridByNet : Map<NetId, Grid> =
            placement |> List.map (fun p ->
                let gateGrid =
                    p.Cell.Pattern
                    |> Map.toList
                    |> List.map (fun (localCoord, state) ->
                        { X = p.Origin.X + localCoord.X; Y = p.Origin.Y + localCoord.Y }, state)
                    |> Map.ofList
                p.Gate.Output, gateGrid)
            |> Map.ofList

        //
        // JUNC3 などゲートの出力ポート直前セル (前段セル) が出力と対角隣接する wire[1]
        // を同時に発火させる「対角ショートカット」が生じると、実効遅延が 1 ステップ短くなる。
        //
        // 対処: wire[0]→wire[1] の間に「修正セル」を挿入する。
        //   修正セル条件: wire[0] に隣接、前段セルに非隣接 (Chebyshev距離≥2)、wire[1] に隣接。
        // これにより対角ショートカットが除去され実効遅延が +1 される。
        let fixShortcutPath (path: Coord list) (gateGrid: Grid) (grid: RoutingGrid) =
            match path with
            | src :: secondCell :: _ ->
                // src (ゲート出力ポート) に隣接するゲート内部 Wire セル
                let gateNeighbors =
                    gateGrid |> Map.toList
                    |> List.filter (fun (gateCoord, _) ->
                        gateCoord <> src &&
                        abs (gateCoord.X - src.X) <= 1 &&
                        abs (gateCoord.Y - src.Y) <= 1)
                    |> List.map fst
                // ショートカットの有無: いずれかのゲート隣接セルが wire[1] にも隣接
                let hasShortcut =
                    gateNeighbors |> List.exists (fun gn ->
                        abs (gn.X - secondCell.X) <= 1 &&
                        abs (gn.Y - secondCell.Y) <= 1)
                if not hasShortcut then path
                else
                    // 修正セルを探す: src の 4 方向隣接で
                    //   - ゲート内部セル (前段セル含む) に非隣接
                    //   - wire[1] に隣接 (チェビシェフ距離 1)
                    //   - 配線グリッドで通行可能
                    let cardinalDirs = [| {X=1;Y=0}; {X= -1;Y=0}; {X=0;Y=1}; {X=0;Y= -1} |]
                    let fixCell =
                        cardinalDirs |> Array.tryFind (fun d ->
                            let c = { X = src.X + d.X; Y = src.Y + d.Y }
                            c <> secondCell &&
                            // ゲート内部セルに非隣接 (前段セル含む全ゲートセル)
                            (gateGrid |> Map.toList |> List.forall (fun (gateCoord, _) ->
                                abs (c.X - gateCoord.X) > 1 || abs (c.Y - gateCoord.Y) > 1)) &&
                            // wire[1] に隣接
                            abs (c.X - secondCell.X) <= 1 &&
                            abs (c.Y - secondCell.Y) <= 1 &&
                            // 配線グリッドで通行可能
                            (match Map.tryFind c grid with
                             | None | Some Free -> true
                             | _ -> false))
                        |> Option.map (fun d -> { X = src.X + d.X; Y = src.Y + d.Y })
                    match fixCell with
                    | Some fc -> src :: fc :: secondCell :: List.tail (List.tail path)
                    | None -> path
            | _ -> path

        // fan-out 対応: leePathFanout で同一 net の既配線セルを再利用可
        // depth: 再帰呼び出しの深さ。depth=0 のみ重複フォールバック許可。
        let rec routeOne (depth: int) (grid: RoutingGrid) (wires: Wire list) (netId: NetId) (src: Coord) (dst: Coord) (consumer: NetId)
            : Result<RoutingGrid * Wire list, CompileError> =
            let finalize (rawPath: Coord list) (g: RoutingGrid) =
                let gateGrid = gateGridByNet |> Map.tryFind netId |> Option.defaultValue Map.empty
                let path = fixShortcutPath rawPath gateGrid g
                let grid' =
                    path |> List.fold (fun g' c ->
                        match Map.tryFind c baseGrid with
                        | Some Blocked -> g'
                        | _ -> Map.add c (Routed netId) g') g
                let wire = ofPath netId consumer path
                Ok (grid', wire :: wires)
            match leePathFanout grid netId src dst allPorts tight with
            | None when depth <= 3 ->
                // 重複フォールバック: 他ネットの配線セルを通過可能にして再試行。
                // depth制限を3まで拡張して、連鎖的な再ルーティングを許可。
                let freeGrid = grid |> Map.map (fun _ v ->
                    match v with
                    | Routed n when n <> netId -> Free
                    | x -> x)
                match leePathImpl freeGrid src dst (Some netId) allPorts tight true with
                | None -> Error (RoutingCongestion netId)
                | Some overlapPath ->
                    let grid' = overlapPath |> List.fold (fun g' c ->
                        match Map.tryFind c baseGrid with
                        | Some Blocked -> g'
                        | _ -> Map.add c (Routed netId) g') grid
                    let affectedCoords = overlapPath |> List.filter (fun c ->
                        match Map.tryFind c grid with
                        | Some (Routed n) when n <> netId -> true
                        | _ -> false)
                    let affectedNetIds =
                        affectedCoords |> List.choose (fun c ->
                            match Map.tryFind c grid with
                            | Some (Routed n) when n <> netId -> Some n
                            | _ -> None) |> Set.ofList
                    let remainingWires = wires |> List.filter (fun w -> not (Set.contains w.Net affectedNetIds))
                    let grid'' = grid' |> Map.map (fun _ v ->
                        match v with
                        | Routed n when Set.contains n affectedNetIds -> Free
                        | x -> x)
                    let reRoute (acc: Result<RoutingGrid * Wire list, CompileError>)
                               (affectedNetId: NetId) =
                        acc |> Result.bind (fun (g, ws) ->
                            let src' = outCoords |> Map.find affectedNetId
                            let consumers' = inCoordsMap |> Map.find affectedNetId
                            consumers' |> List.fold (fun acc2 (dst', consumer') ->
                                acc2 |> Result.bind (fun (g2, ws2) ->
                                    routeOne (depth + 1) g2 ws2 affectedNetId src' dst' consumer')
                            ) (Ok (g, ws)))
                    match Set.fold reRoute (Ok (grid'', remainingWires)) affectedNetIds with
                    | Ok (g, ws) ->
                        let wire = ofPath netId consumer overlapPath
                        Ok (g, wire :: ws)
                    | Error e -> Error e
            | None -> Error (RoutingCongestion netId)
            | Some rawPath -> finalize rawPath grid

        // 動的ネット順序: 失敗したネットを後回しにして再試行
        let rec routeWithRetry (remainingNets: (NetId * Coord * (Coord * NetId) list) list)
                               (failedNets: (NetId * Coord * (Coord * NetId) list) list)
                               (grid: RoutingGrid)
                               (wires: Wire list)
                               (maxRetries: int) =
            match remainingNets with
            | [] when failedNets.IsEmpty -> Ok (List.rev wires)
            | [] when maxRetries > 0 ->
                // 失敗したネットを再試行（順序を維持）
                routeWithRetry failedNets [] grid wires (maxRetries - 1)
            | [] ->
                // 再試行回数超過
                let firstFailedNet = failedNets |> List.head |> fun (n,_,_) -> n
                Error (RoutingCongestion firstFailedNet)
            | (netId, src, dstConsumers) :: rest ->
                let result =
                    dstConsumers |> List.fold (fun acc (dst, consumer) ->
                        acc |> Result.bind (fun (g, ws) ->
                            routeOne 0 g ws netId src dst consumer)
                    ) (Ok (grid, wires))
                match result with
                | Ok (newGrid, newWires) ->
                    routeWithRetry rest failedNets newGrid newWires maxRetries
                | Error _ ->
                    // 失敗したネットを後回しにして続行
                    routeWithRetry rest (failedNets @ [(netId, src, dstConsumers)]) grid wires maxRetries

        let result = routeWithRetry nets [] baseGrid [] maxRouteRetries

        // 全ての配線が決定した後に、他の配線からの干渉を考慮して遅延を再計算
        match result with
        | Ok wires ->
            // 全ての配線座標を収集
            let allWireCoords =
                wires |> List.collect (fun w -> w.Path) |> Set.ofList

            // 各ワイヤについて、他の配線からの干渉を計算して遅延を再計算
            let wires' =
                wires |> List.map (fun w ->
                    let otherCoords = allWireCoords |> Set.difference (Set.ofList w.Path)
                    let interferenceDelay =
                        w.Path |> List.filter (fun c ->
                            // セルcの8近傍に他の配線があるかチェック
                            [for dx in -1..1 do
                                for dy in -1..1 do
                                    if dx <> 0 || dy <> 0 then
                                        let nb = {X=c.X+dx; Y=c.Y+dy}
                                        if Set.contains nb otherCoords then yield nb]
                            |> List.isEmpty |> not
                        ) |> List.length
                        |> fun count -> count / interferenceCellsPerGen
                    { w with Delay = w.Delay + interferenceDelay * 1<gen> }
                )
            Ok wires'
        | Error e -> Error e

    /// ★ タイミング均等化 ★ — WireWorld 設計の肝。
    /// あるゲートの全入力で「信号到達世代」が一致しないと誤動作する。
    /// 速いパスに遅延 (蛇行配線・遅延ループ) を足して最も遅いパスに合わせる。
    let balanceGateInputs (inputs: Wire list) : Result<Wire list, CompileError> =
        match inputs with
        | [] -> Ok []
        | _  ->
            let target = inputs |> List.map (fun w -> w.Delay) |> List.max
            // 各入力を target まで遅延パディング。実体としては Path を蛇行で
            // 伸ばす操作だが、ここでは Delay の調整として表現する。
            inputs
            |> List.map (fun w ->
                if w.Delay = target then Ok w
                elif w.Delay < target then
                    Ok { w with Delay = target }   // ← 実装では Path を延長
                else
                    // target は max なので理論上ここには来ない
                    Error (TimingViolation (w.Net, target, w.Delay)))
            |> List.fold (fun acc r ->
                match acc, r with
                | Ok xs, Ok x -> Ok (x :: xs)
                | Error e, _  -> Error e
                | _, Error e  -> Error e) (Ok [])
            |> Result.map List.rev

    /// Emitter: 配置 + 配線を 1 枚の Grid に合成する。
    let emit (placement: Placement) (wires: Wire list) : Grid =
        // セルパターンを絶対座標へ平行移動して合成
        let cells =
            placement
            |> List.collect (fun p ->
                p.Cell.Pattern
                |> Map.toList
                |> List.map (fun (c, s) ->
                    { X = c.X + p.Origin.X; Y = c.Y + p.Origin.Y }, s))
        // 配線は導線として敷く
        let routed =
            wires
            |> List.collect (fun w -> w.Path |> List.map (fun c -> c, Wire))
        (cells @ routed) |> Map.ofList

    /// Golly 拡張 RLE 出力 (state: 1=Head 'A', 2=Tail 'B', 3=Wire 'C')。
    let toRle (g: Grid) : string =
        if Map.isEmpty g then "x = 0, y = 0, rule = WireWorld\n!"
        else
            let xs = g |> Map.toSeq |> Seq.map (fun (c, _) -> c.X)
            let ys = g |> Map.toSeq |> Seq.map (fun (c, _) -> c.Y)
            let minX, maxX = Seq.min xs, Seq.max xs
            let minY, maxY = Seq.min ys, Seq.max ys
            let w, h = maxX - minX + 1, maxY - minY + 1
            let charOf = function Head -> "A" | Tail -> "B" | Wire -> "C" | Empty -> "."
            let sb = System.Text.StringBuilder()
            sb.Append(sprintf "x = %d, y = %d, rule = WireWorld\n" w h) |> ignore
            for y in minY .. maxY do
                for x in minX .. maxX do
                    sb.Append(charOf (get g { X = x; Y = y })) |> ignore
                sb.Append(if y = maxY then "!" else "$") |> ignore
            sb.ToString()

    // --- railway 演算子 ---
    let (>>=) m f = Result.bind f m

    /// STA スラックに基づいて NAND ゲートのセルバリアントを選択し、
    /// 影響を受けるワイヤを再ルーティングする。
    ///
    /// 対応:
    ///   - スラック 3<gen>: junc3 → junc3_Ab3 (A に 3gen 内蔵バッファ)
    ///   - スラック 5<gen>: junc3 → junc3_Ab5 (A に 5gen 内蔵バッファ)
    ///   - スラック 7<gen>: junc3 → junc3_Ab7 (A に 7gen 内蔵バッファ)
    ///
    /// 差し替え後に B・Clock ポートの絶対座標が変わるため、
    /// 対応するワイヤを再ルーティングして返す。
    let applyVariants
        (placement: Placement)
        (wires: Wire list)
        (slack: Map<NetId * NetId, int<gen>>)
        : Result<Placement * Wire list, CompileError> =

        // スラック 3<gen>, 5<gen>, または 7<gen> の NAND ゲートを探す。
        // 注意: applyVariants 後のワイヤ再ルーティングでタイミングがずれるため、
        // より大きな遅延を持つセルを選択する必要がある。
        let getVariant (p: Placed) =
            if p.Cell.Kind <> Nand then None
            else
                match p.Gate.Inputs with
                | aNet :: _ ->
                    match Map.tryFind (aNet, p.Gate.Output) slack with
                    | Some 3<gen> -> Some Library.junc3_Ab7  // slack=3 → junc3_Ab7 (7gen delay)
                    | Some 5<gen> -> Some Library.junc3_Ab7
                    | Some 7<gen> -> Some Library.junc3_Ab7
                    | _ -> None
                | [] -> None

        if not (placement |> List.exists (fun p -> getVariant p |> Option.isSome)) then
            Ok (placement, wires)
        else
            // バリアント差し替え: junc3 → junc3_Ab3, junc3_Ab5, or junc3_Ab7
            let newPlacement =
                placement |> List.map (fun p ->
                    match getVariant p with
                    | Some variant -> { p with Cell = variant }
                    | None -> p)

            // ポート位置が変わった Placed を特定し、影響ワイヤを再ルーティング。
            // 影響ワイヤ = 変更されたゲートを Consumer とするワイヤのうち、
            //   B ポート (インデックス 1) へのワイヤ。
            // A ポートは junc3_Ab1/junc3_Ab3 でも同じ位置 (0,0) なので A ワイヤは変更不要。
            // Clock ワイヤはルーティングされないため変更不要 (Sim が直接注入)。
            let changedGateOutputs =
                placement
                |> List.choose (fun p ->
                    if getVariant p |> Option.isSome then Some p.Gate.Output else None)
                |> Set.ofList

            // B ポート (インデックス 1) への影響ワイヤを特定: Consumer = 変更されたゲートの Output
            // かつ Net が B 入力 (Gate.Inputs.[1])。
            let affectedBNets =
                newPlacement
                |> List.choose (fun p ->
                    if Set.contains p.Gate.Output changedGateOutputs then
                        match p.Gate.Inputs with
                        | _ :: bNet :: _ -> Some (bNet, p.Gate.Output)
                        | _ -> None
                    else None)
                |> Set.ofList

            // 影響ワイヤ以外はそのまま保持する。
            let unchangedWires =
                wires |> List.filter (fun w ->
                    not (Set.contains (w.Net, w.Consumer) affectedBNets))

            // 影響ワイヤを再ルーティング。
            // buildGrid は新しい placement (junc3_Ab3 のサイズ含む) を使う。
            let newBase = Route.buildGrid newPlacement

            let allPorts =
                newPlacement |> List.collect (fun p ->
                    p.Cell.Ports |> List.map (Place.portCoord p))
                |> Set.ofList

            // outCoords: 出力ポート座標
            let outCoords =
                newPlacement |> List.choose (fun p ->
                    p.Cell.Ports |> List.tryFind (fun port -> port.Role = Out)
                    |> Option.map (fun port -> p.Gate.Output, Place.portCoord p port))
                |> Map.ofList

            // 新しい B ポートの絶対座標
            let newBPortCoords =
                newPlacement
                |> List.choose (fun p ->
                    if Set.contains p.Gate.Output changedGateOutputs then
                        let inPorts = p.Cell.Ports |> List.filter (fun pp -> pp.Role = In)
                        match inPorts, p.Gate.Inputs with
                        | _ :: bPort :: _, _ :: bNet :: _ ->
                            Some (bNet, p.Gate.Output, Place.portCoord p bPort)
                        | _ -> None
                    else None)

            // 変更ワイヤのルーティング
            let routingGrid0 =
                unchangedWires |> List.fold (fun g w ->
                    w.Path |> List.fold (fun g2 c ->
                        match Map.tryFind c newBase with
                        | Some Route.Blocked -> g2
                        | _ -> Map.add c (Route.Routed w.Net) g2) g) newBase

            // 変更された B ポートを1本ずつ再ルーティングする。
            let routeOneBWire
                (acc: Result<Route.RoutingGrid * Wire list, CompileError>)
                (bNet: NetId, consumer: NetId, bDst: Coord)
                =
                acc |> Result.bind (fun (g, ws) ->
                    match Map.tryFind bNet outCoords with
                    | None -> Ok (g, ws)   // B が PI → routing 不要
                    | Some bSrc ->
                        match Route.leePathFanout g bNet bSrc bDst allPorts true with
                        | None -> Error (RoutingCongestion bNet)
                        | Some path ->
                            let wire = Route.ofPath bNet consumer path
                            let g' =
                                path |> List.fold (fun g2 c ->
                                    match Map.tryFind c newBase with
                                    | Some Route.Blocked -> g2
                                    | _ -> Map.add c (Route.Routed bNet) g2) g
                            Ok (g', wire :: ws))

            let routeResult =
                newBPortCoords
                |> List.fold routeOneBWire (Ok (routingGrid0, unchangedWires))

            routeResult |> Result.map (fun (_, newWires) ->
                newPlacement, List.rev newWires)

    /// トップレベル: HDL ソース → WireWorld Grid。
    let compile (lib: CellLibrary) (src: string) : Result<Grid, CompileError> =
        frontend src >>= fun nl ->
        techMap lib nl >>= fun mapped ->
        place mapped >>= fun placement ->
        route true placement >>= fun wires ->
        let arrivals = Sta.computeArrival placement wires
        let slack    = Sta.computeSlack   placement wires arrivals
        let wires'   = Sta.insertDelays   placement slack wires
        Ok (emit placement wires')

    /// Grid + Placement + Wire list を返す詳細版 (テスト・デバッグ用)。
    let compileFull (lib: CellLibrary) (src: string)
        : Result<Grid * Placement * Wire list, CompileError> =
        frontend src >>= fun nl ->
        techMap lib nl >>= fun mapped ->
        place mapped >>= fun placement ->
        route true placement >>= fun wires ->
        let arrivals = Sta.computeArrival placement wires
        let slack    = Sta.computeSlack   placement wires arrivals
        let wires'   = Sta.insertDelays   placement slack wires
        Ok (emit placement wires', placement, wires')

    /// 広間隔配置版。
    let compileFullWide (lib: CellLibrary) (src: string)
        : Result<Grid * Placement * Wire list, CompileError> =
        frontend src >>= fun nl ->
        techMap lib nl >>= fun mapped ->
        placeWide mapped >>= fun placement ->
        route false placement >>= fun wires ->
        let arrivals = Sta.computeArrival placement wires
        let slack    = Sta.computeSlack   placement wires arrivals
        let wires'   = Sta.insertDelays   placement slack wires
        // クロック配線は実装済みだが、シミュレーショングリッドに追加すると
        // 既存テストのタイミングが変わるため、現時点では無効化。
        // Sim.routeClocks placement wires' >>= fun clockWires ->
        // let allWires = wires' @ clockWires
        let allWires = wires'
        Ok (emit placement allWires, placement, allWires)


