# WwHdl 基礎設計書

HDL → WireWorld コンパイラの実装設計。型・アルゴリズム・セルパターン方針を定める。

---

## 1. 実装マイルストーン

| # | 名称 | 完了条件 |
|---|------|---------|
| M1 | **セルライブラリ完成** | NOT/AND/OR/XOR/DFF/DELAY_n/Crossover を `Rule.run` 単体テスト済み |
| M2 | **フロントエンド** | Yosys JSON → Netlist 変換。AND-NOT 2 ゲート回路が通る |
| M3 | **ルーティング** | Lee 法で全ネット配線。Crossover 自動挿入。4 ゲート回路が Grid になる |
| M4 | **タイミング均等化** | STA + DELAY_n 挿入。生成 Grid を `Rule.run` で実行し正しい論理値を確認 |
| M5 | **E2E 検証** | カウンタ → ALU → 乗算器を HDL から Golly RLE まで通す |

---

## 2. セルライブラリ設計 (M1)

### 2.1 確定フロー

```
ofAscii でパターン手書き
  → Rule.run で実行
  → 出力 Head の出現世代を確認
  → Latency = 出力到達世代 - 入力到達世代 として登録
```

### 2.2 DELAY_n

1 セル = 1 gen なので **DELAY_n は n+1 セルの導線**。

```
DELAY_1:  ##            Size 2×1, Latency 1<gen>
DELAY_4:  #####         Size 5×1, Latency 4<gen>  (= buf と同一)
DELAY_9:  #####         蛇行 (4+折返1+4), Size 5×3, Latency 9<gen>
              #
          #####
```

`makeDelay (n: int<gen>)` が動的生成。n ≤ 16 は直線、n > 16 は蛇行に切り替える（TODO）。

### 2.3 基本ゲート

| ゲート | 実装状況 | 方式 |
|--------|---------|------|
| BUF / DELAY_n | ✅ 検証済み | 直線導線、`makeDelay` で動的生成 |
| OR2 | ✅ 検証済み | 2 導線を対角合流 (5×3) |
| SPLIT | ✅ 検証済み | Y 字対角分岐 (5×3) |
| JUNC3 | ✅ パターン確定・CellTest 通過待ち | 5×5 十字合流点 — NOT/NAND の核 |
| NOT1 | ✅ JUNC3 エイリアス | `{ junc3 with Kind=Not }` |
| AND2 | 🔲 設計確定、Pattern 未実装 | JUNC3×2 直列 (NAND + NOT), Latency=8 |
| DIODE | ✅ パターン確定・CellTest 通過待ち | Quinapalus 公式設計 4×3、Latency=3 (§2.5 参照) |
| XOR | 🔲 未着手 | AND2 確定後に設計 |
| DFF | 🔲 未着手 | NOT1 確定後に設計 |
| Crossover | 🔲 スタブ | タイミング分離型 7×7 (§2.4 参照) |

#### OR2 パターン詳細 (5×3, Latency=4)

```
###..   y=0  入力 A  x=0..2
...##   y=1  合流+出力  x=3..4
###..   y=2  入力 B  x=0..2
```

(2,0) と (3,1) は対角隣接 (Δx=1, Δy=1)。Head 1〜2 個で (3,1) が発火し (4,1) から出力。

#### SPLIT パターン詳細 (5×3, Latency=4)

```
..###   y=0  出力 A 方向  x=2..4
##...   y=1  入力+折れ点  x=0..1
..###   y=2  出力 B 方向  x=2..4
```

(1,1) と (2,0), (2,2) は対角隣接。入力 Head が (1,1) に到達すると同時に両方へ分岐。

#### JUNC3 — NOT/NAND の核 (5×5, Latency=4)

```
..#..   y=0  入力 C 根本
..#..   y=1  入力 C 経路
#####   y=2  左=A, (2,2)=junction, 右=output
..#..   y=3  入力 B 経路
..#..   y=4  入力 B 根本
```

**動作**:

入力 A(x=0,y=2), B(x=2,y=0), C(x=2,y=4) が t=0 に Head:
- t=1: 中間セル (1,2),(2,1),(2,3) が Head
- t=2: junction (2,2) が隣接 Head 数を評価
  - 1〜2個 → fires → t=4 で (4,2) = 出力
  - 3個    → no fire → 出力なし

**用途別配線**:

```
NOT(A)   : left=A, top=clock1, bottom=clock2
  A=0 → 2 Head → fires → output=1 ✓
  A=1 → 3 Head → no fire → output=0 ✓

NAND(A,B): left=A, top=B, bottom=clock
  A∧B=0 → 1-2 Head → fires → NAND=1 ✓
  A∧B=1 → 3 Head → no fire → NAND=0 ✓
```

**AND の正しい動作 (NAND + NOT)**:

NAND(A,B,clock) の出力を NOT(nand_out, clock', clock') に通す。
「NAND fires」= 電子が出る = NOT への input=1。

```
A=0,B=0: NAND 1 Head → fires → NOT input=1 + 2 clocks = 3 Head → no fire → AND=0 ✓
A=1,B=0: NAND 2 Head → fires → NOT input=1 + 2 clocks = 3 Head → no fire → AND=0 ✓
A=0,B=1: 同上 → AND=0 ✓
A=1,B=1: NAND 3 Head → no fire → NOT input=0 + 2 clocks = 2 Head → fires → AND=1 ✓
```

diode は不要だった。NAND+NOT が正しく AND を実現する。

### 2.5 DIODE パターン詳細 (4×3, Latency=3)

出典: https://www.quinapalus.com/wi-diode.gif (Brian Silverman の公式 WireWorld コンピュータより)

```
.##.   y=0  アーム上
##.#   y=1  入力(x=0) / 中間(x=1) / ギャップ(x=2=Empty!) / 出力(x=3)
.##.   y=2  アーム下
```

**動作原理 — ギャップが非対称性を生む**:

ギャップ (2,1) が存在することで、(1,1) の Head が対角の (2,0)(2,2) へ伝播し、その 2 つが次に (3,1) を対角合流で発火させる。逆方向では (2,0)(2,2) が (1,0)(1,1)(1,2) の 3 本を同時発火させ、その 3 本が (0,1) を 3 Head 近傍にして阻止する。

| 方向 | t+1 | t+2 | t+3 | 結果 |
|------|-----|-----|-----|------|
| 順 (→) H at (0,1) | (1,0)(1,1)(1,2)=H | (2,0)(2,2)=H, (0,1)=3Heads→blocked | (3,1)=H | 通過 Latency=3 ✓ |
| 逆 (←) H at (3,1) | (2,0)(2,2)=H | (1,0)(1,1)(1,2)=H | (0,1)=3Heads→no fire | 遮断 ✓ |

**注意事項**:
- 単一電子では t+3 以降に内部発振が生じる (ジャンクション内部でのバウンス)。
- 同期回路でクロック周期を十分長く設定すること (期間 ≥ 8<gen> を推奨)。
- 遮断用の代替として junc3(data=backward, clock, clock) の方がノイズレス。

### 2.4 Crossover

2 信号を干渉なく交差させるパターン。

**方式 1: タイミング分離型** (まず試みる)

水平信号を odd tick、垂直信号を even tick で通過させ、合流点で Head が 2 個隣接しないようにする。
目標サイズ 7×7。参考: suzuki-navi/domino の `cross` ノード設計。

**方式 2: Wireworld++ 型** (方式 1 が 20×20 超の場合)

5 状態拡張ルールで方向性付き導線を使う。Golly 組み込みルール `WireWorldPlus` で動作。
→ `Library` 全体にルール名フィールド (`Rule: string`) を追加して切り替える。

---

## 3. フロントエンド設計 (M2)

### 3.1 Yosys コマンド

```bash
read_verilog design.v
synth -flatten
abc -g AND,NOT    # AND と NOT のみに正規化 (機能的完全系)
write_json design.json
```

AND と NOT の 2 種があれば全ブール関数を表現できる。M2 ではこの 2 種のみをライブラリに用意すれば十分。

### 3.2 JSON スキーマ (抜粋)

```json
{
  "modules": {
    "top": {
      "ports": {
        "a": { "direction": "input",  "bits": [2] },
        "y": { "direction": "output", "bits": [5] }
      },
      "cells": {
        "u0": {
          "type": "$_NOT_",
          "port_directions": { "A": "input", "Y": "output" },
          "connections": { "A": [2], "Y": [3] }
        },
        "u1": {
          "type": "$_AND_",
          "port_directions": { "A": "input", "B": "input", "Y": "output" },
          "connections": { "A": [2], "B": [3], "Y": [5] }
        }
      }
    }
  }
}
```

`connections` のビット番号が `NetId` に直接対応する。

### 3.3 型マッピング

| Yosys type  | GateKind |
|-------------|----------|
| `$_NOT_`    | `Not`    |
| `$_AND_`    | `And`    |
| `$_OR_`     | `Or`     |
| `$_XOR_`    | `Xor`    |
| `$_NAND_`   | `Nand`   |
| `$_NOR_`    | `Nor`    |
| `$_DFF_P_`  | `Dff`    |
| `$_BUF_`    | `Buf`    |

---

## 4. ルーティング設計 (M3)

### 4.1 RoutingGrid

```
Placement から RoutingGrid を構築:
  各セルの bounding box 内のセルを Blocked にする
  それ以外は Free
```

### 4.2 Lee 法 BFS

```
leePath grid src dst:
  1. dist[src] = 0、queue に src を追加
  2. queue が空になるまで繰り返す:
       c = dequeue
       上下左右の隣接セル n について:
         if grid[n] = Free && dist[n] 未確定:
           dist[n] = dist[c] + 1
           enqueue n
           if n = dst: goto 3
  3. dst から src へ逆追跡 (dist が 1 減る方向を辿る)
  4. Path = 逆追跡結果
```

配線済みのセルは `Routed(netId)` でマーク。後続ネットは迂回する。
配線順: 扇出の多いネットを優先すると混雑が減る。

### 4.3 Crossover 挿入

```
findConflicts wires:
  Wire.Path を Coord → NetId にフラット化
  同一 Coord に複数 NetId → (coord, netA, netB) として列挙

insertCrossovers conflicts wires:
  衝突点ごとに Crossover StdCell を配置
  両ネットの Path を Crossover の入出力ポートに接続しなおす
  → 遅延差は後段 STA で吸収
```

---

## 5. STA / タイミング均等化設計 (M4)

### 5.1 到達時刻計算

```
ArrivalMap: NetId → int<gen>

arrival(primary_input) = 0<gen>
arrival(gate_output)   = max { arrival(input_i) + wire(input_i).Delay }
                           + gate.Latency
```

ゲートをトポロジカル順（入力が全確定してから出力を計算）に処理する。

### 5.2 DELAY_n 挿入

```
target(gate) = max { arrival(input_i) + wire(input_i).Delay }

for each input wire w of gate:
  need = target - (arrival(src(w)) + w.Delay)
  if need > 0:
    insert DELAY_{need} セルを src と gate の間に配置
    wire.Delay += need
```

DELAY セルは `makeDelay need` で生成して Placement に追加。`emit` でまとめて Grid に合成。

### 5.3 タイミング違反の対処

`need < 0`（到達が遅すぎる）は迂回では解消できない。対処:
1. Place 段階でゲート間距離を縮める（再配置）
2. パイプラインステージを挿入（DFF で 1 クロック借用）

---

## 6. F# 型拡張まとめ

| モジュール | 追加内容 |
|-----------|---------|
| `Library` | `makeDelay`, `crossover` stub |
| `Route`   | `RoutingCell`, `RoutingGrid`, `buildGrid`, `leePath`, `findConflicts`, `insertCrossovers` |
| `Sta`     | 新規: `ArrivalMap`, `computeArrival`, `computeSlack`, `insertDelays` |
| `Pipeline`| `YosysPort`, `YosysCell`, `YosysModule`, `parseYosysJson`, `yosysToNetlist`, `frontend` 更新 |
