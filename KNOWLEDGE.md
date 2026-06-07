# WwHdl 実装知見録

設計判断・デバッグ過程・非自明な発見を記録する。DESIGN.md は「現在の仕様」、本ファイルは「なぜそうなったか・何が失敗したか」を残す。

---

## M1 — セルライブラリ

### JUNC3 パターン設計: 2 度の失敗と最終形

#### 試行 1: クロス (+) 形状 (失敗)

```
..#..   y=0  入力 B
..#..   y=1
#####   y=2  入力 A(x=0), junction(2,2), 出力(3,2)-(4,2)
..#..   y=3
..#..   y=4  入力 C
```

**欠陥**: `(2,3)` と `(3,2)` が対角隣接 (距離=√2)。  
入力 C のパルスが `(2,3)` を通るとき、`(3,2)` を **junction をバイパスして直接発火** させる。  
3 入力ブロックケースでも `(2,1)` と `(2,3)` が `(3,2)` の 2 Head 近傍になり、必ず出力が出てしまう。

根本的な幾何の問題: 水平出力ライン `(J.x+1, J.y)` は上下垂直入力の中間セル `(J.x, J.y±1)` と必ず対角隣接する。この位相は回避不能。

#### 試行 2: 上下入力を x=1 にずらした形 (失敗)

```
.#...   y=0  入力 B (x=1)
.#...   y=1
#####   y=2  入力 A(x=0), junction(2,2), 出力(3,2)-(4,2)
.#...   y=3
.#...   y=4  入力 C (x=1)
```

(3,2) と (1,1)/(1,3) の距離を 2 に広げて対角ショートカットを排除。  
**しかし別の欠陥**: 左入力 `(0,2)` が `(1,1)` と `(1,3)` の両方と対角隣接。  
A=1, C=1 のとき A のパルスが `(1,1)` を誤発火 → junction が 3 Head を検出してブロック → NAND(1,0,clk)=1 が通らない。

#### 最終形: 全入力を左列に集約 (5×3)

```
#....   y=0  A=(0,0)  junction と対角隣接
#####   y=1  B=(0,1), junction=(1,1), 出力=(2,1)-(4,1)
#....   y=2  C=(0,2)  junction と対角隣接
```

**設計原則**:  
- 3 入力すべてを左列 (x=0) に集める
- junction を (1,1) に置く
- 出力経路の先頭 (2,1) の 8 近傍には A・C が含まれない (距離=2)  
  → ショートカット完全排除

**動作**:  
t=0: A/B/C にパルス入力  
t=1: junction (1,1) が隣接 Head 数を評価 (A/C は対角、B は直接)  
- 1〜2 個 → fires → t=4 で (4,1) = 出力  
- 3 個    → no fire → 出力なし  

**Latency=4** (入力ポートから出力ポートまで 4 世代)

---

### JUNC3 テスト関数のバグ

`testJunc3` 関数が出力座標 `{X=4; Y=2}` をハードコードしていた。  
セルのポート定義を変更しても自動で追従しない。

**修正**: `junc3.Ports |> List.find (fun p -> p.Role = Out)` を使って動的に取得する。  
ハードコード座標に依存するテストは回帰バグの温床になる。

---

### Latency の決定方法

`verifyLatency` で「In ポートに Head を置いて `Latency` 世代後に Out ポートに Head が来るか」を確認する。  
**注意**: Clock ポートを持つセルはこの方法では検証できない (信号が到達しない)。  
Clock 付きセルは `testJunc3` のような専用テスト関数が必要。

各セルの確定 Latency:

| セル    | Size  | Latency | 備考 |
|--------|-------|---------|------|
| BUF_h4 | 5×1   | 4       | 直線導線 |
| OR2    | 5×3   | 4       | 対角合流 |
| SPLIT  | 5×3   | 4       | Y 字分岐 |
| JUNC3  | 5×3   | 4       | 左列集約形、3 入力 |
| NOT1   | 5×3   | 4       | JUNC3 エイリアス |
| DIODE  | 4×3   | 3       | Quinapalus 公式設計 |

---

### DIODE: 単一電子使用時の注意

Quinapalus 設計の DIODE は **単一電子では t+3 以降に内部発振** が生じる。  
同期回路ではクロック周期を ≥8<gen> に設定して内部発振が干渉しないようにする。  
代替として `junc3(data=backward, clock, clock)` の方がノイズレス。

---

### AND2 モノリシック実装の断念

AND2 を直接 StdCell として実装しようとすると、JUNC3 を NAND として使った後に NOT を直列接続する際、NAND の出力ワイヤーが隣接 JUNC3 の clock/B ポートに対角隣接してスプリアス信号が入る。

**対策**: `abc -g NAND,NOT` で Yosys に AND2 を NAND+NOT に分解させる。コンパイラは NAND/NOT の 2 種類だけを知っていれば良い。XOR も同様に 4 NAND に分解される。

---

## M2 — フロントエンド

### Yosys JSON の `bits` フィールド

Yosys の `connections` の値は通常整数 (ネット番号) だが、定数値 `"0"` / `"1"` を **文字列** で埋め込む場合がある。  
`System.Text.Json` で `GetInt32()` を直接呼ぶとここで例外。

**対策**: `JsonValueKind.Number` かどうかを確認してから取得する:
```fsharp
if b.ValueKind = System.Text.Json.JsonValueKind.Number
then Some (b.GetInt32())
else None  // "0"/"1" 定数はスキップ
```

### モジュール選択

Yosys JSON は複数モジュールを含む可能性がある。`"top"` という名前のモジュールを優先し、なければ先頭を使う。

### 入力ポートのソート順

Gate.Inputs リストの順序は Yosys の JSON 出力順に依存するため不安定になりがち。  
`port_directions` を **アルファベット順でソート** (A→B→C) して Inputs を構築することで再現性を確保する。

---

## M3 — ルーティング

### leePath: src/dst は Blocked 領域内でも許可

セルのポート座標はセルの bounding box (Blocked 領域) 内に存在する。  
通常の「Free のみ通過」ルールを src/dst にも適用すると、BFS が初手で行き詰まる。

**対策**:
```fsharp
let passable c =
    c = src || c = dst ||   // 端点は常に通過可
    match Map.tryFind c grid with
    | None | Some Free -> true
    | _ -> false
```

### leePath: BFS の境界について

routing grid に明示的な境界はなく、BFS は無限グリッドを探索できる。  
障害物が「矩形ブロック」でも、その外側を回り込んで到達できる場合は None にならない。  

「到達不能」テストケースを書くには、**src の全 4 近傍をブロック** することで確実に到達不能にする (外を迂回できないので)。矩形ブロックだけでは迂回されてしまう。

### クロックポートの扱い (M3 では未接続)

JUNC3/NOT1 の 3 番目の入力ポートはクロック。  
Yosys から来る Gate.Inputs は 2 個 (NAND) または 1 個 (NOT) だが、Cell.In.Ports は 3 個ある。  

`Seq.zip Gate.Inputs Cell.In.Ports` で短い方に合わせると、クロックポートは自動的に未接続になる。  
クロック配線は M4 タイミング均等化フェーズで追加予定。

### ルーティング後の grid 更新

経路上の各セルを `Routed(netId)` でマークするとき、bounding box 内 (Blocked) のセル (= ポート座標) は上書きしない。  
そうしないと、次のネットの BFS が「Blocked のはずのセルが Routed になっている」という不整合を起こす。

```fsharp
match Map.tryFind c baseGrid with
| Some Blocked -> g           // ポート座標は元のまま
| _ -> Map.add c (Routed netId) g
```

### gate 間スペーシング

現在の `place` 実装は `size.X + 4` の間隔で水平配置する。  
`gap = 4` は「配線が 2 本通れる最小幅」の目安。配線が込み合う場合は増やす。

---

## M4 — STA / タイミング均等化

### `|>` と `+` の演算子優先順位

F# では `|>` が `+` より優先順位が **低い**。次のコードは意図通りに動かない:

```fsharp
// NG: A |> (f + B) と解釈される
Map.tryFind n m |> Option.defaultValue 0<gen>
+ (Map.tryFind n w |> Option.defaultValue 0<gen>)

// OK: 括弧で明示する
(Map.tryFind n m |> Option.defaultValue 0<gen>)
+ (Map.tryFind n w |> Option.defaultValue 0<gen>)
```

### 一次入力の検出 (Placement のみから)

Netlist を持たなくても、Placement から一次入力ネットを導出できる:
- ゲート駆動ネット = `{ p.Gate.Output for p in placement }`
- 一次入力ネット  = `{ n in p.Gate.Inputs } ∖ ゲート駆動ネット`

### `computeArrival` iterative propagation

Kahn のトポロジカルソートの代わりに「全入力が確定したゲートを繰り返し処理する」iterative 方式を採用。実装がシンプルで、組合せ回路 (DAG) なら必ず収束する。O(n²) だが小回路では十分。

### `insertDelays` の物理実装は未完 (M5 以降)

`insertDelays` は現在 Wire.Delay フィールドを更新するだけで、Path を実際に延長しない。物理的なメアンダリング (蛇行配線) は M5 以降で実装予定。STA の計算は正確だが、生成 Grid の実際の動作検証はまだできない。

---

## M5 — E2E シミュレーション

### `[<Struct>]` 型に `with` 構文は使えない

F# の `{ x with Field = v }` はレコード型専用。`[<Struct>]` 型には使えない:

```fsharp
// NG: Coord は [<Struct>] なのでコンパイルエラー
{ pivot with Y = pivot.Y - i }

// OK: フィールドを全て明示する
{ X = pivot.X; Y = pivot.Y - i }
```

### `extendPath` の設計

- ジグザグは「パス終端直前の点 (pivot) から -Y 方向へ N/2 歩往復」
- -Y (y<0) 空間は直線配置では常に空き (ゲートは y≥0 に配置されるため)
- 奇数の extra delay は N+1 に切り上げ (STA の誤差として 1gen 余裕が生じる)
- パスの連続性: down の末尾が pivot 自身 (i=0 で Y-0=pivot) → 次のセルへ自然に接続

### E2E テストの設計パターン

コンパイラの生成 Grid を Rule.run で検証する最小パターン:

```fsharp
// 1. compileFull で Grid + Placement を取得
let Ok (grid, placement, _) = compileFull lib json

// 2. ポート座標に直接 Head を注入
let g = grid |> inject [clockPort1; clockPort2]   // クロック注入

// 3. Rule.run で Latency 世代進める
let result = run (int latency) g

// 4. 出力ポートで Head の有無を確認
get result outPort = Head  // true なら 1, false なら 0
```

### クロックポートの識別

JUNC3/NOT1 は Clock ポートを持たず、全て In ロールで定義されている。  
クロックポートは `Gate.Inputs.Length` 番目以降の In ポートとして識別する:

```fsharp
let clockCoords (p: Placed) =
    let inPorts = p.Cell.Ports |> List.filter (fun port -> port.Role = In)
    let nLogical = p.Gate.Inputs.Length
    inPorts |> List.mapi (fun i port -> i, port)
            |> List.choose (fun (i, port) -> if i >= nLogical then Some (portCoord p port) else None)
```

### 単一ゲート E2E はクロック接続不要

単一 NOT ゲートのコンパイル結果には内部ルーティングワイヤーが存在しない  
(primary input → gate: 内部ネットなし)。Gate のポートに直接 Head を注入できるため、  
クロック配線インフラなしでも E2E 検証が可能。

---

## クロック配線 / 多段 E2E

### L ターンの対角ショートカットとワイヤ遅延

ルーティングパスに L ターン (水平→垂直 または 垂直→水平) があると、ターン直前のセルがターン後のセルと対角隣接する。これにより信号が経由セルをスキップして 1 ステップ早着する。

例: パス `(7,1)→(8,1)→(8,0)→(9,0)` (L ターン 2 回)
- (7,1) が Head → (8,1) と (8,0) が同時に Head (t=8)
- (8,0), (8,1) が Head → (9,0), (9,1), (9,2) が同時に Head (t=9)

L ターンが連続する場合、節約ステップ数は単純な加算にならない。**`N-1-turns` の計算式は不正確**。

**正しい対処**: `measureDelay` — パスをシミュレーションして実測。

```fsharp
let private measureDelay (path: Coord list) : int<gen> =
    // src に Head を置き dst が Head になるまでの世代数を実測
    let initial = wireGrid |> Map.add src Head
    let rec find g t = if get g dst = Head then t * 1<gen> else find (step g) (t+1)
    find initial 0
```

### ルーティングターンがクロック配線を兼ねる (重要な設計知見)

2-NOT チェーンで実証: ルーティングワイヤ `(8,0),(8,1)` が同時 Head になることで、
u1 のクロックポート `(9,1),(9,2)` が自動的に信号を受け取る。

- **a=0 の場合** (u0 fires): データ `(9,0)` + 自動クロック `(9,1),(9,2)` = 3 Heads → u1 ブロック
- **a=1 の場合** (u0 blocks): データなし、手動注入クロック `(9,1),(9,2)` のみ = 2 Heads → u1 発火

ルーティングワイヤの折れ曲がりが意図せずクロック分配を行う。これは **WireWorld 回路設計の本質的な性質** であり、同期設計を成立させる。

### シミュレーションの totalSteps は arrival 時刻に合わせる

`runWithClocks` で `totalSteps` を指定する際、出力ポートが Head になる世代 (`arrival(output)`) と一致させる必要がある。多く回すと Head→Tail になって検出できなくなる。

```fsharp
let totalSteps = arrivals |> Map.tryFind output_net |> Option.map int |> Option.defaultValue fallback
```

### クロック注入タイミング = target (データ到達時刻)

`clockTimeOf(G) = max { arrival(input_i) + wireDelay(input_i) }` = STA の target。

この時刻にクロックポートに Head を注入すれば、データとクロックが junction に同時到達する。
`measureDelay` で正確な wireDelay を求めることがこの計算の前提。

---

## M6 — fan-out ルーティングと MultiGate E2E

### Wire.Consumer フィールドと leePathFanout

fan-out 時に同一ネットが複数ゲートを駆動する場合、STA が per-consumer の wireDelay を
正確に保持するために `Wire.Consumer` フィールドを追加した。

```fsharp
type Wire = { Net: NetId; Consumer: NetId; Path: Coord list; Delay: int<gen> }
```

`wireDelayByConsumer = Map<NetId * NetId, int<gen>>` で `(net, consumer)` をキーに検索する。

fan-out 配線は `leePathFanout` で同一ネットの既配線セルを再利用しながら
各コンシューマへ経路を延伸する (`sameNet = Some netId` で Routed セルも通過可)。

### BFS の bounding box 制限

`leePathImpl` に src/dst を囲む bounding box + margin を設定し、無限空間への
BFS 拡散を防いだ。margin = ManDist(src,dst) + 10 で十分な迂回空間を確保。

### F# WIP 構文バグ 2 件

**`|> (List.rev, _)` は構文エラー** — パイプ演算子にタプルを渡せない。
`let (result, _) = ... in Ok (List.rev result)` で代替。

**`Result.sequence` は F# 標準に存在しない** — `List.fold` で手動実装が必要:
```fsharp
|> List.fold (fun acc r ->
    match acc, r with
    | Ok xs, Ok x -> Ok (x :: xs)
    | Error e, _  -> Error e
    | _, Error e  -> Error e) (Ok [])
|> Result.map List.rev
```

---

## junc3 の物理制約 (M6 で発見・M7 で一部解決)

### 問題 1: ポートクロストーク → M7 で解決済み

旧 junc3 パターンは 3 入力ポートが x=0 列に隣接配置 (y=0/1/2) だった:
```
(0,0) A
(0,1) B  ← A と直接隣接 → ワイヤ信号が隣接ポートを誤発火
(0,2) C  ← B と直接隣接
```

**解決策 (M7)**: ポートを対角4隅に配置してポート間を全て非隣接 (チェビシェフ距離≥2) にした。

新 junc3 パターン (5×3):
```
#.#..   y=0  A=(0,0) 左上, B=(2,0) 右上  (A-B 距離=2 → 非隣接)
.#...   y=1  junction=(1,1)
#.###   y=2  C=(0,2) 左下               (A-C 距離=2, B-C 距離=2)
            出力=(2,2)(3,2)(4,2)
```

設計根拠: junction(1,1) の8近傍の中で互いに非隣接な3点は対角位置のみ。
A(0,0), B(2,0), C(0,2) は全て junction の対角近傍かつ互いの距離≥2 → クロストーク排除 ✓

### 問題 2: ワイヤ経路が次ゲートのクロックポートに隣接 → 未解決

新パターンでは出力が (4,2) (y=2 下端) になったため、出力ワイヤが y=2 を流れる。
次のゲートの clk ポートも (0,2) (y=2 左下) にあるため、ワイヤが次ゲート手前を通る際に clk ポートと直接隣接するセルを踏む。

例: u0 の出力ワイヤが (12,2) を通ると u1 の clk ポート (13,2) が誤発火。
fan-out 回路 (半加算器等) では NAND=0 のはずのゲートが誤発火して sum=1 になる。

**影響を受けないケース** (安定動作):
- プライマリ入力直接注入 (ワイヤ経由でない) — NAND/AND 単体ゲートテスト
- NOT チェーン (fan-out なし) — MultiStageTest
- or2 セル — Clock ポートなし, 入力が y=0 と y=2 で非隣接

### OR テストの解決策

`NAND(NOT(a), NOT(b))` による OR 実装は 2 ワイヤ均等化が必要で失敗。
**`$_OR_` 単一セル (or2)** にマッピングすることで回避:

```json
{ "u0": {"type":"$_OR_", "connections":{"A":[2],"B":[3],"Y":[4]}} }
```

or2 は Clock ポートなし、2 入力が y=0 と y=2 の別行で非隣接 → クロストーク問題なし。

### extendPath の物理的限界

ジグザグ U ターンによる遅延挿入は「2 本のワイヤが空間を共有しない場合のみ有効」:
- 共有セルがあると信号漏れが起きる
- U ターン内で同じセルを 2 度通る → 信号がループで消滅

### 今後の修正方向

1. ~~junc3 の入力ポートを非隣接に再配置~~ → M7 で完了
2. ルーターが経路上のセルと次ゲートのクロックポートが隣接しないよう制約を追加
   - leePath の passable 関数に「destination 以外のゲートポートの近傍はコスト増」を適用
   - または Place の gap を大きくして bounding box 周辺の空きを増やす
3. マルチサイクル動作でクロック周期を延ばし誤信号の収束を待つ

---

## 設計全般

### WireWorld の根本制約: 距離 = 遅延

WireWorld では「配線 1 セル = 1 gen の遅延」が成り立つ。これが STA の基礎。  
タイミング均等化は「遅いパスに合わせるために早いパスを延長する」= 配線を蛇行させる、という物理的操作に対応する。

### F# での Railway-Oriented Pipeline

```fsharp
frontend src >>= fun nl ->
techMap lib nl >>= fun mapped ->
place mapped >>= fun placement ->
route placement >>= fun wires ->
Ok (emit placement wires)
```

`>>=` は `Result.bind` の中置演算子エイリアス。各段が `Result` を返すので、どこでエラーになっても後続は実行されず `CompileError` が伝播する。

### テスト戦略

- **M1**: `Rule.run` ベースの単体テスト。`verifyLatency` / `verifySymmetry` / `verifyAllOutputs` の 3 種類。
- **M2**: JSON 文字列 → `frontend` → アサート。実際の Yosys 出力形式に近いサンプルを使う。
- **M3**: `leePath` 単体 + end-to-end `compile` → Grid の 2 層構造。
- 共通: `runAll` が `(string * bool) list` を返す形式で統一。`RunTests.fsx` でまとめて実行。

### `let` をリストリテラル内に書けない (F#)

```fsharp
// NG: F# ではリスト内に let バインディングを書けない
[ "test",
    let x = someValue   // コンパイルエラー
    x > 0 ]

// OK: リストの外で事前に定義する
let x = someValue
[ "test", x > 0 ]
```
