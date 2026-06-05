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

| ゲート | 実装状況 | 方式 | 留意点 |
|--------|---------|------|--------|
| BUF (DELAY_n) | ✅ 検証済み | 直線導線 | `makeDelay` で動的生成 |
| OR2 | ✅ 検証済み | 2 導線を対角合流 | 同 tick に 2 Head → 2 近傍 → fires (WW 規則 OK) |
| SPLIT | ✅ 検証済み | Y 字対角分岐 | (1,1) から (2,0)/(2,2) へ同時分岐 |
| NOT1 | 🔲 スタブ | クロックループ + 3-Head 抑制 | ループ周期と data タイミングの整合が課題 |
| AND2 | 🔲 スタブ | NAND(A,B,clock) + NOT | A=1,B=0 の誤発火防止に diode が必要 |
| XOR | 🔲 未着手 | OR − AND (NAND 2 段) | AND2 確定後に設計 |
| DFF | 🔲 未着手 | ループ型ラッチ + クロックゲート | NOT1 確定後に設計 |
| Crossover | 🔲 スタブ | タイミング分離型 7×7 | 後述 §2.4 |

#### OR2 パターン詳細 (5×3, Latency=4)

```
###..   y=0  入力 A  x=0..2
...##   y=1  合流+出力  x=3..4
###..   y=2  入力 B  x=0..2
```

(2,0) と (3,1) は対角隣接 (Δx=1, Δy=1)。Head 1〜2 個で (3,1) が発火し (4,1) から出力。
`CellTest.verifyLatency` と `verifySymmetry` で確認済み。

#### SPLIT パターン詳細 (5×3, Latency=4)

```
..###   y=0  出力 A 方向  x=2..4
##...   y=1  入力+折れ点  x=0..1
..###   y=2  出力 B 方向  x=2..4
```

(1,1) と (2,0), (2,2) は対角隣接。入力 Head が (1,1) に到達すると同時に両方へ分岐。
`CellTest.verifyAllOutputs` で確認済み。

#### NOT1 設計方針 (未実装)

WireWorld の NOT には **クロック信号** が必須。
- 理由: `Wire → Head` の条件は「1 or 2 Head 近傍」のみ。data=1 単独でも出力が発火してしまうため、data=1 のときに発火を抑制する「3 Head = 不発火」メカニズムが必要。
- アプローチ: period-P クロックループの電子を常時出力方向へ流し、data=1 のとき合流点で 3 Head を作り出力を止める。
- 課題: clock Head と data Head が合流点に「同時」に届くタイミング設計。

#### AND2 設計方針 (未実装)

AND の本質的困難: `Wire → Head` が「1 or 2 Head」で発火するため、A=1,B=0 でも合流点が発火してしまう(= OR と同じ)。

解決策: **NAND + NOT の組み合わせ**
```
NAND(A,B,clock): 3 Head → 不発火 (NAND出力=0)、1-2 Head → 発火 (NAND出力=1)
AND(A,B) = NOT(NAND(A,B))
```

ただし NAND は「常時 clock が来ている」前提。clock + A + B = 3 Head が揃ったときのみ出力を止める。A=1 のみでも clock + A = 2 Head → 発火してしまう → AND(1,0)=1 になり誤動作。

**根本解決**: electron diode (電子ダイオード) を組み込み、A 単独では合流点に到達できないようにする。ダイオードパターンの設計が NOT1 の後続タスク。

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
