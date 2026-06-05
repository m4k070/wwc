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
