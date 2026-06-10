namespace WwHdl

// ---------------------------------------------------------------------
// 7. クロック注入付きシミュレーター
//    STA 結果から各ゲートへのクロック注入時刻を自動計算し、
//    step-wise でシミュレーションを走らせる。
// ---------------------------------------------------------------------
module Sim =
    open Units
    open Domain
    open Netlist
    open Library
    open Place
    open Route
    open Sta
    open Rule

    /// 配置済みゲートのクロックポート絶対座標を返す。
    /// Gate.Inputs.Length 番目以降の In ポートをクロック用とみなす。
    let clockCoords (p: Placed) : Coord list =
        let inPorts = p.Cell.Ports |> List.filter (fun port -> port.Role = In)
        let nLogical = p.Gate.Inputs.Length
        inPorts
        |> List.mapi (fun i port -> i, port)
        |> List.choose (fun (i, port) ->
            if i >= nLogical then Some (portCoord p port) else None)

    /// ゲート G のクロック注入時刻。
    /// クロックはデータ信号と同じタイミングでポートに注入される必要がある。
    /// ただし、ポート間の内部遅延差がある場合（junc3_Ab3等）は、
    /// クロックポートの遅延に合わせて注入時刻を調整する。
    ///
    /// clockTime = max(arrival_i + wireDelay_i + portDelay_i) - clockPortDelay
    let clockTimeOf (p: Placed) (arrivals: ArrivalMap) (wires: Wire list) : int<gen> =
        if p.Gate.Inputs.IsEmpty then 0<gen>
        else
            let wireDelayByConsumer =
                wires |> List.map (fun w -> (w.Net, w.Consumer), w.Delay) |> Map.ofList
            let inPortDelays =
                p.Cell.PortDelays
                |> List.truncate p.Gate.Inputs.Length
            let padCount = p.Gate.Inputs.Length - inPortDelays.Length
            let portDelays =
                inPortDelays @ List.replicate padCount p.Cell.Latency
            let clockPortDelay =
                let clockIdx = p.Gate.Inputs.Length
                if clockIdx < p.Cell.PortDelays.Length
                then p.Cell.PortDelays.[clockIdx]
                else p.Cell.Latency
            let junctionTime =
                p.Gate.Inputs |> List.mapi (fun i n ->
                    (arrivals |> Map.tryFind n |> Option.defaultValue 0<gen>)
                    + (wireDelayByConsumer |> Map.tryFind (n, p.Gate.Output) |> Option.defaultValue 0<gen>)
                    + portDelays.[i])
                |> List.max
            junctionTime - clockPortDelay

    /// ★ クロック配線 ★
    /// 各ゲートのクロックポートにクロック信号を配線する。
    /// クロックソースから各ゲートのクロックポートへ配線し、
    /// clockTimeOf に基づいて配線長を調整する。
    let routeClocks (placement: Placement) (dataWires: Wire list) : Result<Wire list, CompileError> =
        // クロックソースの位置（配置の左側）
        let clockSource = { X = -20; Y = 0 }
        
        // クロックポート座標を取得（各ゲートの最後のInポート = クロックポート）
        let clockPorts =
            placement |> List.choose (fun p ->
                let clocks = clockCoords p
                match clocks with
                | [] -> None
                | last :: _ -> Some (p.Gate.Output, last))
        
        if clockPorts.IsEmpty then Ok [] else
        
        // クロック配線のベースグリッド（データ配線を含む）
        let baseGrid = buildGrid placement
        let dataGrid =
            dataWires |> List.fold (fun g w ->
                w.Path |> List.fold (fun g2 c ->
                    match Map.tryFind c baseGrid with
                    | Some Blocked -> g2
                    | _ -> Map.add c (Routed w.Net) g2) g) baseGrid
        
        // 全ポート座標（クロック配線時の干渉回避用）
        let allPorts =
            placement |> List.collect (fun p ->
                p.Cell.Ports |> List.map (portCoord p))
            |> Set.ofList
        
        // クロックネットID（データ配線と区別するため、大きな値を使用）
        let clockNetId = NetId 10000
        
        // arrival map を1回だけ計算
        let arrivals = computeArrival placement dataWires
        
        // 各クロックポートに対して配線
        let routeOneClock (grid: RoutingGrid) (wires: Wire list) (gateOutput: NetId, clockPort: Coord) =
            let placed = placement |> List.find (fun p -> p.Gate.Output = gateOutput)
            let targetDelay = clockTimeOf placed arrivals dataWires
            
            match leePathImpl grid clockSource clockPort (Some clockNetId) Set.empty false true with
            | None ->
                Error (RoutingCongestion clockNetId)
            | Some path ->
                let actualDelay = measureDelay path
                let slack = targetDelay - actualDelay
                let extendedPath =
                    if slack > 0<gen> then extendPath slack path
                    else path
                
                let newGrid =
                    extendedPath |> List.fold (fun g c ->
                        match Map.tryFind c baseGrid with
                        | Some Blocked -> g
                        | _ -> Map.add c (Routed clockNetId) g) grid
                
                let wire = ofPath clockNetId gateOutput extendedPath
                Ok (newGrid, wire :: wires)
        
        clockPorts
        |> List.fold (fun acc clockPort ->
            acc |> Result.bind (fun (g, ws) -> routeOneClock g ws clockPort))
            (Ok (dataGrid, []))
        |> Result.map (fun (_, wires) -> List.rev wires)

    /// 注入マップ (世代 → 座標リスト) を使って `steps` 世代進める。
    /// 各イテレーション: 該当世代の Head を注入してから Rule.step を呼ぶ。
    let runWithInjections (injections: Map<int<gen>, Coord list>) (g: Grid) (steps: int) : Grid =
        let mutable state = g
        for idx in 0 .. steps - 1 do
            let t = idx * 1<gen>
            match Map.tryFind t injections with
            | Some coords ->
                state <- coords |> List.fold (fun acc c -> Map.add c Head acc) state
            | None -> ()
            state <- step state
        state

    /// クロック自動注入付き WireWorld シミュレーション。
    ///   placement: 配置情報 (クロックポート位置取得に使用)
    ///   arrivals:  STA 計算済み到達時刻マップ
    ///   wires:     遅延情報付き配線リスト
    ///   dataInj:   データ信号手動注入: (座標, 注入世代) list
    ///   grid:      コンパイル済み Grid (emit 済み)
    ///   steps:     シミュレーション世代数
    let runWithClocks
        (placement: Placement)
        (arrivals: ArrivalMap)
        (wires: Wire list)
        (dataInj: (Coord * int<gen>) list)
        (grid: Grid)
        (steps: int)
        : Grid =
        let allEntries =
            [ yield! placement |> List.collect (fun p ->
                let t = clockTimeOf p arrivals wires
                clockCoords p |> List.map (fun c -> t, c))
              yield! dataInj |> List.map (fun (c, t) -> t, c) ]
            |> List.groupBy fst
            |> List.map (fun (t, pairs) -> t, List.map snd pairs)
            |> Map.ofList

        runWithInjections allEntries grid steps

    /// テストユーティリティ: プライマリ入力の注入座標とタイミングを計算する。
    /// gateDrivenNets (ゲート出力で駆動されるネット) はスキップし、
    /// プライマリ入力のみを各消費ゲートの clockTimeOf タイミングで注入する。
    let makePrimaryInjections
        (placement: Placement)
        (arrivals: ArrivalMap)
        (wires: Wire list)
        (primaryValues: Map<NetId, bool>)
        : (Coord * int<gen>) list =
        let gateDrivenNets = placement |> List.map (fun p -> p.Gate.Output) |> Set.ofList
        placement |> List.collect (fun p ->
            let t = clockTimeOf p arrivals wires
            let inPorts = p.Cell.Ports |> List.filter (fun port -> port.Role = In)
            p.Gate.Inputs
            |> List.mapi (fun i n ->
                if Set.contains n gateDrivenNets then []
                else
                    let coord = portCoord p inPorts.[i]
                    match Map.tryFind n primaryValues with
                    | Some true -> [(coord, t)]
                    | _ -> [])
            |> List.concat)


