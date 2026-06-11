namespace WwHdl

// ---------------------------------------------------------------------
// 1.5 WireLevel — 独自 CA ルール (WireWorld の後継ターゲット)
//
// WireWorld の根本制約 (2026-06-11 実証) を解消するために設計した
// デジタル回路専用のセルオートマトン:
//   * バックファイア: junc3 発火時に電子が入力配線を逆流する
//     (RunBackfire.fsx で実証)。閉ループ順序回路ではサイクル毎に
//     上流ゲートを誤発火させるため、全入力に DIODE が必要になる。
//   * 厳密タイミング: パルス方式は全ゲート入力の 1gen 精度整合が必須。
//     5 ゲートの半加算器ですら sum(1,0) が未解決。
//
// WireLevel の設計原則:
//   * レベル駆動: セルは bool レベルを保持し、信号は「値」として伝播する。
//     グリッチは実ハードウェア同様に自然収束する → 厳密 STA 不要 (収束待ちのみ)。
//   * pull 型有向配線: Wire(dir) は「背面 (dir の反対側)」の隣セルの提示値を
//     毎世代取り込む。読み出しは元セルへ影響しない → 逆流が構造的に不可能。
//     複数セルが同一セルを読める → ファンアウト自由。
//   * 専用交差セル: Cross が水平・垂直を独立に通す → 配線輻輳が消える。
//   * 専用 DFF セル: クロック立ち上がりで D をサンプル → 順序回路がプリミティブ。
//   * von Neumann 近傍 (4近傍)・全状態 ≤ 64 → GPU シェーダー / Golly ruletable 両対応。
//
// 配置制約 (ルーターが保証すべきこと):
//   * NAND / DFF は出力方向以外の非空隣接セルをすべて入力として読む。
//     無関係な配線をゲートに隣接させないこと (1 セルのクリアランス)。
//   * ファンイン (OR 合流) は暗黙にはできない。必ずゲートで合成する。
// ---------------------------------------------------------------------
module WireLevel =
    open Domain   // Coord を共有

    /// 配線・ゲートの向き = 信号の進行方向。N は -Y (画面上方向)、S は +Y。
    type Dir = E | W | N | S

    let delta = function
        | E -> { X =  1; Y =  0 }
        | W -> { X = -1; Y =  0 }
        | N -> { X =  0; Y = -1 }
        | S -> { X =  0; Y =  1 }

    let opposite = function E -> W | W -> E | N -> S | S -> N

    /// WireLevel のセル状態。レベル (bool) はセル自身が保持する。
    type LCell =
        /// 背景。何も提示しない。
        | LEmpty
        /// 外部入力ピン。ホスト (テストベンチ / GPU ホスト) が値を書き、規則では不変。
        | Pin   of level: bool
        /// 有向配線。背面の提示値を取り込み、自レベルを全方向に提示する。
        | LWire of dir: Dir * level: bool
        /// NAND ゲート。出力方向以外の全隣接提示値の AND の否定を取る。
        /// 入力 1 本なら NOT として働く (yosys の NAND+NOT 分解にそのまま対応)。
        | LNand of dir: Dir * level: bool
        /// 交差。水平チャネル (hDir ∈ {E,W}) と垂直チャネル (vDir ∈ {N,S}) を
        /// 独立に通す。hDir 側へは hLevel のみ、vDir 側へは vLevel のみ提示する。
        | Cross of hDir: Dir * vDir: Dir * hLevel: bool * vLevel: bool
        /// D フリップフロップ。D は背面から、CLK は側面 (出力方向と直交) から取る。
        /// CLK 立ち上がり (prevClk=0 → clk=1) で q := D。
        | LDff  of dir: Dir * q: bool * prevClk: bool

    type LGrid = Map<Coord, LCell>

    let getL (g: LGrid) (c: Coord) : LCell =
        Map.tryFind c g |> Option.defaultValue LEmpty

    /// セルが toward 方向の隣セルに提示するレベル。提示しなければ None。
    /// Cross 以外は全方向に同じレベルを提示する (読み手側が pull を選択する)。
    let presentedTo (cell: LCell) (toward: Dir) : bool option =
        match cell with
        | LEmpty -> None
        | Pin v | LWire (_, v) | LNand (_, v) -> Some v
        | LDff (_, q, _) -> Some q
        | Cross (hd, vd, hv, vv) ->
            if toward = hd then Some hv
            elif toward = vd then Some vv
            else None

    /// 座標 c が side 方向の隣セルから引き込めるレベル。
    let pullFrom (g: LGrid) (c: Coord) (side: Dir) : bool option =
        let d = delta side
        let nb = getL g { X = c.X + d.X; Y = c.Y + d.Y }
        presentedTo nb (opposite side)

    let private allSides = [E; W; N; S]

    /// 1 世代進める。各セルの次状態は自セルと 4 近傍のみで決まる (von Neumann)。
    let step (g: LGrid) : LGrid =
        g |> Map.map (fun c cell ->
            match cell with
            | LEmpty | Pin _ -> cell
            | LWire (dir, _) ->
                let level = pullFrom g c (opposite dir) |> Option.defaultValue false
                LWire (dir, level)
            | LNand (dir, _) ->
                let inputs =
                    allSides
                    |> List.filter (fun s -> s <> dir)
                    |> List.choose (pullFrom g c)
                // 全入力の AND の否定。入力なしは 0 (非接続ゲートは沈黙)。
                let level =
                    match inputs with
                    | [] -> false
                    | _  -> not (inputs |> List.forall id)
                LNand (dir, level)
            | Cross (hd, vd, _, _) ->
                let hv = pullFrom g c (opposite hd) |> Option.defaultValue false
                let vv = pullFrom g c (opposite vd) |> Option.defaultValue false
                Cross (hd, vd, hv, vv)
            | LDff (dir, q, prevClk) ->
                let dIn = pullFrom g c (opposite dir) |> Option.defaultValue false
                let clkSides = match dir with E | W -> [N; S] | N | S -> [E; W]
                let clk =
                    clkSides |> List.exists (fun s ->
                        pullFrom g c s |> Option.defaultValue false)
                let q' = if clk && not prevClk then dIn else q
                LDff (dir, q', clk))

    let stepN (n: int) (g: LGrid) : LGrid =
        Seq.fold (fun acc _ -> step acc) g (seq { 1 .. n })

    /// 組合せ回路の収束待ち: グリッドが変化しなくなるまで進める (上限 limit)。
    /// 戻り値は (収束後グリッド, 要した世代数)。limit 到達時は (グリッド, limit)。
    let settle (limit: int) (g: LGrid) : LGrid * int =
        let rec go (cur: LGrid) (t: int) =
            if t >= limit then cur, limit
            else
                let next = step cur
                if next = cur then cur, t
                else go next (t + 1)
        go g 0

    /// ピン値の書き込み (ホスト操作)。セル種別を保持し、レベルだけ更新する。
    let setPin (c: Coord) (v: bool) (g: LGrid) : LGrid =
        match getL g c with
        | LEmpty        -> Map.add c (Pin v) g
        | Pin _         -> Map.add c (Pin v) g
        | LWire (d, _)  -> Map.add c (LWire (d, v)) g
        | LNand (d, _)  -> Map.add c (LNand (d, v)) g
        | Cross (hd, vd, _, q) -> Map.add c (Cross (hd, vd, v, q)) g
        | LDff (d, _, pc)      -> Map.add c (LDff (d, v, pc)) g

    /// セルの保持レベルを読む (テスト・観測用)。Cross は水平チャネル。
    let levelOf (g: LGrid) (c: Coord) : bool =
        match getL g c with
        | LEmpty -> false
        | Pin v | LWire (_, v) | LNand (_, v) -> v
        | Cross (_, _, hv, _) -> hv
        | LDff (_, q, _) -> q

    /// 各 DFF のクロック到達世代 (= クロック側終端から駆動源までのセル数) を測る。
    /// クロックスキュー検証用。配線セル 1 個 = 1 世代なので、パス長がそのまま遅延。
    /// 戻り値: (DFF 座標, 到達世代)。クロック未接続の DFF は含まない。
    let clockArrivals (g: LGrid) : (Coord * int) list =
        let addD (c: Coord) (d: Dir) = { X = c.X + (delta d).X; Y = c.Y + (delta d).Y }
        let subD (c: Coord) (d: Dir) = { X = c.X - (delta d).X; Y = c.Y - (delta d).Y }
        let cap = Map.count g + 1
        g |> Map.toList |> List.choose (fun (c, cell) ->
            match cell with
            | LDff (dir, _, _) ->
                let clkSides = match dir with E | W -> [N; S] | N | S -> [E; W]
                clkSides
                |> List.tryPick (fun s ->
                    let t = addD c s
                    match getL g t with
                    | LWire (d, _) -> Some (t, d)
                    | _ -> None)
                |> Option.map (fun (t, d0) ->
                    // 終端から駆動源 (Pin/ゲート) まで逆走。Cross は直進チャネルを辿る。
                    let rec go (cur: Coord) (dIn: Dir) (n: int) =
                        if n > cap then n
                        else
                            let prev = subD cur dIn
                            match getL g prev with
                            | LWire (d2, _) -> go prev d2 (n + 1)
                            | Cross _ -> go prev dIn (n + 1)
                            | _ -> n
                    c, go t d0 1)
            | _ -> None)

    /// ASCII アートから LGrid を組む。
    ///   '.' = 空, '>' '<' '^' 'v' = Wire E/W/N/S (向き = 信号の進行方向)
    ///   'E' 'W' 'N' 'S' = NAND (出力方向)
    ///   '+' = Cross (水平 E, 垂直 S)
    ///   'F' = DFF (出力 E)
    ///   '0' '1' = Pin (初期レベル)
    /// 全セルはレベル 0 で初期化される (Pin '1' を除く)。
    let ofAsciiL (rows: string list) : LGrid =
        rows
        |> List.mapi (fun y row ->
            row |> Seq.mapi (fun x ch ->
                let cell =
                    match ch with
                    | '>' -> LWire (E, false)
                    | '<' -> LWire (W, false)
                    | '^' -> LWire (N, false)
                    | 'v' -> LWire (S, false)
                    | 'E' -> LNand (E, false)
                    | 'W' -> LNand (W, false)
                    | 'N' -> LNand (N, false)
                    | 'S' -> LNand (S, false)
                    | '+' -> Cross (E, S, false, false)
                    | 'F' -> LDff (E, false, false)
                    | '0' -> Pin false
                    | '1' -> Pin true
                    | _   -> LEmpty
                { X = x; Y = y }, cell)
            |> List.ofSeq)
        |> List.concat
        |> List.filter (fun (_, s) -> s <> LEmpty)
        |> Map.ofList

    // -----------------------------------------------------------------
    // GPU エクスポート用 byte エンコーディング (DESIGN-CA2.md §4 参照)
    //   bit 7-5: 種別 (0=Empty 1=Pin 2=Wire 3=Nand 4=Cross 5=Dff)
    //   bit 4-3: 方向 (E=0 W=1 N=2 S=3) / Cross は bit4=hDir(E=0,W=1), bit3=vDir(N=0,S=1)
    //   bit 1-0: レベル (Wire/Nand/Pin: bit0) / Cross: bit1=v, bit0=h / Dff: bit1=prevClk, bit0=q
    // -----------------------------------------------------------------
    let private dirCode = function E -> 0 | W -> 1 | N -> 2 | S -> 3

    let encodeCell (cell: LCell) : byte =
        let b (v: bool) = if v then 1 else 0
        match cell with
        | LEmpty -> 0uy
        | Pin v -> byte (0b001_00_000 ||| b v)
        | LWire (d, v) -> byte (0b010_00_000 ||| (dirCode d <<< 3) ||| b v)
        | LNand (d, v) -> byte (0b011_00_000 ||| (dirCode d <<< 3) ||| b v)
        | Cross (hd, vd, hv, vv) ->
            byte (0b100_00_000
                  ||| ((if hd = W then 1 else 0) <<< 4)
                  ||| ((if vd = S then 1 else 0) <<< 3)
                  ||| (b vv <<< 1) ||| b hv)
        | LDff (d, q, pc) ->
            byte (0b101_00_000 ||| (dirCode d <<< 3) ||| (b pc <<< 1) ||| b q)

    /// LGrid を grid.bin 形式にエクスポートする (GPU 実行用)。
    /// 形式: [width:i32 LE][height:i32 LE][cell_0_0][cell_1_0]...[cell_w-1_h-1]
    /// 各セルは encodeCell の 1 byte、行優先、空セルは 0x00。
    /// 座標は (0,0) から正規化して出力する。
    let exportGrid (g: LGrid) : byte[] =
        let coords = g |> Map.toList |> List.map fst
        if coords.IsEmpty then
            Array.zeroCreate 8
        else
            let minX = coords |> List.map (fun c -> c.X) |> List.min
            let maxX = coords |> List.map (fun c -> c.X) |> List.max
            let minY = coords |> List.map (fun c -> c.Y) |> List.min
            let maxY = coords |> List.map (fun c -> c.Y) |> List.max
            let w = maxX - minX + 1
            let h = maxY - minY + 1
            let buf = Array.zeroCreate (8 + w * h)
            let le (v: int) (i: int) =
                buf.[i] <- byte (v &&& 0xFF)
                buf.[i+1] <- byte ((v >>> 8) &&& 0xFF)
                buf.[i+2] <- byte ((v >>> 16) &&& 0xFF)
                buf.[i+3] <- byte ((v >>> 24) &&& 0xFF)
            le w 0
            le h 4
            for y in 0 .. h - 1 do
                for x in 0 .. w - 1 do
                    let cell = getL g { X = minX + x; Y = minY + y }
                    buf.[8 + y * w + x] <- encodeCell cell
            buf

    /// grid.bin 形式から LGrid を復元する。
    let importGrid (data: byte[]) : LGrid =
        if data.Length < 8 then Map.empty else
        let w = (int data.[0]) ||| (int data.[1] <<< 8) ||| (int data.[2] <<< 16) ||| (int data.[3] <<< 24)
        let h = (int data.[4]) ||| (int data.[5] <<< 8) ||| (int data.[6] <<< 16) ||| (int data.[7] <<< 24)
        let dirs = [| E; W; N; S |]
        let mutable g = Map.empty
        for y in 0 .. h - 1 do
            for x in 0 .. w - 1 do
                let b = data.[8 + y * w + x]
                if b <> 0uy then
                    let kind = int (b >>> 5)
                    let dcode = int (b >>> 3) &&& 3
                    let l0 = (b &&& 1uy) <> 0uy
                    let l1 = ((b >>> 1) &&& 1uy) <> 0uy
                    let cell =
                        match kind with
                        | 1 -> Pin l0
                        | 2 -> LWire (dirs.[dcode], l0)
                        | 3 -> LNand (dirs.[dcode], l0)
                        | 4 ->
                            let hd = if (b >>> 4 &&& 1uy) = 0uy then E else W
                            let vd = if (b >>> 3 &&& 1uy) = 0uy then N else S
                            Cross (hd, vd, l0, l1)
                        | 5 -> LDff (dirs.[dcode], l0, l1)
                        | _ -> LEmpty
                    g <- Map.add { X = x; Y = y } cell g
        g

    /// grid.bin 形式から LGrid を復元する (オフセット付き)。
    let importGridWithOffset (offset: Coord) (data: byte[]) : LGrid =
        if data.Length < 8 then Map.empty else
        let w = (int data.[0]) ||| (int data.[1] <<< 8) ||| (int data.[2] <<< 16) ||| (int data.[3] <<< 24)
        let h = (int data.[4]) ||| (int data.[5] <<< 8) ||| (int data.[6] <<< 16) ||| (int data.[7] <<< 24)
        let dirs = [| E; W; N; S |]
        let mutable g = Map.empty
        for y in 0 .. h - 1 do
            for x in 0 .. w - 1 do
                let b = data.[8 + y * w + x]
                if b <> 0uy then
                    let kind = int (b >>> 5)
                    let dcode = int (b >>> 3) &&& 3
                    let l0 = (b &&& 1uy) <> 0uy
                    let l1 = ((b >>> 1) &&& 1uy) <> 0uy
                    let cell =
                        match kind with
                        | 1 -> Pin l0
                        | 2 -> LWire (dirs.[dcode], l0)
                        | 3 -> LNand (dirs.[dcode], l0)
                        | 4 ->
                            let hd = if (b >>> 4 &&& 1uy) = 0uy then E else W
                            let vd = if (b >>> 3 &&& 1uy) = 0uy then N else S
                            Cross (hd, vd, l0, l1)
                        | 5 -> LDff (dirs.[dcode], l0, l1)
                        | _ -> LEmpty
                    g <- Map.add { X = offset.X + x; Y = offset.Y + y } cell g
        g
