# wwc — HDL → WireWorld Compiler

任意の HDL（Verilog 等）で記述した論理回路を、セルオートマトン **WireWorld** 上で動作するパターンへコンパイルする実験的プロジェクト。F# 製。

> **ステータス: M1〜クロック配線まで実装完了。E2E テスト 59/59 通過。**

---

## これは何か

WireWorld は 4 状態（Empty / Electron Head / Electron Tail / Conductor）のセルオートマトンで、導線・論理ゲート・メモリを構築でき、チューリング完全。本プロジェクトは「HDL で書いた回路を WireWorld グリッドへ自動変換する」コンパイラを目指す。

通常の論理合成と決定的に違うのは、**WireWorld では「配線長 = 信号遅延」** という点。ゲートの全入力が同じ世代（tick）に到達しないと誤動作する。この制約をコンパイラがどう扱うかが本質的な課題になる。

## アーキテクチャ

```
HDL source
   │  frontend        (Verilog/Yosys JSON → Netlist)        ✅
   ▼
Netlist (テクノロジ非依存)
   │  techMap         (Gate → WireWorld StdCell)            ✅
   ▼
(Gate × StdCell) list
   │  place           (グリッドへ配置)                       ✅
   ▼
Placement
   │  route           (Lee 法 BFS 配線)                     ✅
   ▼
Wire list
   │  Sta.computeArrival / insertDelays (タイミング均等化)   ✅
   │  emit            (配置 + 配線を 1 枚の Grid に合成)      ✅
   ▼
WireWorld Grid → Golly RLE                                  ✅
```

### 設計上の主要判断

- **段ごとに別の型を返す** — 各コンパイル段の中間表現を別の型にし、段の取り違えをコンパイル時に弾く。
- **タイミングを型に載せる** — `[<Measure>] type gen`（世代）を導入し、`StdCell.Latency: int<gen>` と `Wire.Delay: int<gen>` を同じ次元で扱う。
- **railway-oriented pipeline** — 各段が `'a -> Result<'b, CompileError>` を返し `>>=`（bind）で連結。どこで落ちても `CompileError` で伝播する。
- **疎なグリッド** — `Map<Coord, CellState>`。Empty は「キー不在」として表現。

## ビルド

```bash
dotnet build src/WwHdl.fsproj
```

## 動かし方

### Yosys で論理合成

```bash
# design.v を Yosys で NAND+NOT に正規化して JSON 出力
read_verilog design.v
synth -flatten
abc -g NAND,NOT
write_json design.json
```

### F# でコンパイル → Grid 生成

```fsharp
open WwHdl.Library
open WwHdl.Pipeline

let json = System.IO.File.ReadAllText "design.json"

// compile: Grid を返す
match compile defaultLib json with
| Ok grid ->
    printfn "%s" (Rule.toRle grid)   // Golly RLE 出力
| Error e ->
    printfn "Error: %A" e
```

### E2E シミュレーション (Rule.run)

```fsharp
// compileFull: Grid + Placement + Wire list を返す詳細版
let Ok (grid, placement, wires) = compileFull defaultLib json

// 入力ポートに Head を注入して N 世代進める
let arrivals = Sta.computeArrival placement wires
let g        = grid |> inject inputPorts
let result   = Rule.run steps g

// 出力ポートで Head の有無を確認 (1=Head, 0=それ以外)
let out = Rule.get result outPort = Head
```

### テスト実行

```bash
dotnet fsi src/RunTests.fsx
```

## ロードマップ

### ✅ M1 — セルライブラリ

- [x] JUNC3 (NAND の核): 2 回の失敗を経て 5×3 左列集約形に確定
- [x] NOT1 / OR2 / SPLIT / BUF_h4 / DIODE を `Rule.run` で単体テスト (CellTest 13/13)
- [x] AND2 / XOR は Yosys が NAND+NOT に自動分解 — モノリシック実装不要

### ✅ M2 — フロントエンド

- [x] Yosys JSON スキーマ確定 (`$_NAND_` / `$_NOT_`)
- [x] `parseYosysJson` + `yosysToNetlist` 実装
- [x] 定数ビット (`"0"` / `"1"` 文字列) への対応
- [x] AND-NOT 2 ゲート回路でパース結果を検証

### ✅ M3 — ルーティング

- [x] `buildGrid`: セルの bounding box を Blocked にマーク
- [x] `leePath`: Lee 法 BFS 最短経路 (src/dst は Blocked 内でも通過)
- [x] `routeAll`: 全ネット配線、`Routed(netId)` でマーク
- [x] 4 ゲート回路が Grid になることを確認

### ✅ M4 — タイミング均等化

- [x] `computeArrival`: iterative propagation で ArrivalMap を計算
- [x] `computeSlack` + `insertDelays`: スラックを `extendPath` で物理延長
- [x] `extendPath`: パス終端から -Y 方向へジグザグ延長 (y<0 空間を使用)

### ✅ M5 — E2E 検証インフラ

- [x] `compileFull` で Grid + Placement + Wire list を取得
- [x] クロックポート識別 (`Gate.Inputs.Length` 番目以降の In ポート)
- [x] `runWithClocks`: クロック注入タイミングを STA の target に合わせて自動計算
- [x] `measureDelay`: L ターンによる遅延ショートカットを WireWorld 実測で補正
- [x] 多段 NOT チェーン (2-NOT / 3-NOT) E2E テスト 59/59 全通過

### 🔲 M6 — 大規模回路検証

- [ ] カウンタ (4 bit)
- [ ] レジスタ (8 bit)
- [ ] ALU (加算器)
- [ ] 乗算器

各回路: HDL 記述 → `compile` → `toRle` → Golly 目視 + `Rule.run` 回帰テスト。

### 🔲 M7 — DFF / フィードバック

- [ ] DFF (D フリップフロップ): クロックゲート型 NAND+NOT 2 段で実装
- [ ] 循環ネットのルーティング対応
- [ ] シーケンシャル回路の E2E 検証

## ライセンス

MIT

## 参考

- Conway's Game of Life / WireWorld のチューリング完全性
- QFT（Quest For Tetris）プロジェクト — CA 上の汎用計算機構築
- Golly — セルオートマトンシミュレータ
- [suzuki-navi/domino](https://github.com/suzuki-navi/domino) — 独自 CA による論理回路ビジュアルシミュレータ。crossover セル・遅延素子・ALU まで実装済み。交差処理と遅延挿入の設計参考。
- https://www.quinapalus.com/wi-index.html
