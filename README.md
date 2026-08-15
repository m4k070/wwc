# wwc — HDL → Cellular Automaton Compiler

任意の HDL（Verilog 等）で記述した論理回路を、セルオートマトン上で動作するパターンへコンパイルする実験的プロジェクト。F# 製。

> **ステータス: WireLevel CA ルールで SM83 CPU (380 gates, 69k cells) を E2E コンパイル・検証済み。GPU (RTX 3060) で byte-exact 一致を確認。テスト 158/158 通過。**

---

## これは何か

本プロジェクトは「HDL で書いた回路を CA グリッドへ自動変換する」コンパイラを目指す。

当初は **WireWorld**（4 状態 CA）をターゲットにしていたが、順序回路でスケールしないことが判明したため、独自 CA ルール **WireLevel**（51 状態、レベル駆動・pull 型有向配線）にピボットした。詳細は [DESIGN-CA2.md](DESIGN-CA2.md) を参照。

### WireWorld での問題（なぜピボットしたか）

1. **バックファイア**: ゲート発火時に電子が全入力配線へ逆流。周期クロックの順序回路ではサイクル毎に上流ゲートを誤発火させる
2. **厳密タイミング**: パルス方式は全ゲート入力の 1gen 精度整合が必須。5ゲートの半加算器ですら sum が未解決のまま

### WireLevel の利点

- レベル駆動・pull 型有向配線 → バックファイアなし
- 専用 Cross/DFF セル → 順序回路が動作
- toggle FF (DFF+NOT ループ) の複数サイクル動作を検証済み

## アーキテクチャ

### WireLevel コンパイラ（メイン）

```
HDL source
   │  frontend        (Verilog/Yosys JSON → Netlist)
   ▼
Netlist
   │  place           (グリッドへ配置)
   ▼
Grid (Placement)
   │  route           (A* BFS 配線)
   ▼
WireLevel Grid → GPU バイナリ (.bin)
```

### WireWorld コンパイラ（レガシー、組合せ回路デモ用に維持）

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
WireWorld Grid → Golly RLE
```

### ファイル構成

```
src/
  Domain.fs      # Units, Domain, Rule, Netlist
  WireLevel.fs   # 独自CAルール (レベル駆動・pull型有向配線) — 新ターゲット
  Library.fs     # StdCell definitions, CellTest
  Place.fs       # Placement algorithm
  Route.fs       # Lee/BFS routing algorithm
  Sta.fs         # Static timing analysis
  Sim.fs         # Clock-gated simulation
  Pipeline.fs    # Yosys JSON frontend + WireWorld pipeline (legacy)
  PipelineWL.fs  # Yosys Netlist → WireLevel コンパイラ (P0)
  E2eTests.fs    # All test modules
wgpu-runner/     # Rust + wgpu GPU シミュレータ
web/             # WebGPU フロントエンド (WGSL compute)
```

### 設計上の主要判断

- **段ごとに別の型を返す** — 各コンパイル段の中間表現を別の型にし、段の取り違えをコンパイル時に弾く
- **疎なグリッド** — `Map<Coord, CellState>`。Empty は「キー不在」として表現
- **railway-oriented pipeline** — 各段が `Result<'b, CompileError>` を返し `>>=`（bind）で連結

## ビルド

```bash
dotnet build src/WwHdl.fsproj
```

## 動かし方

### Yosys で論理合成

```bash
# 組合せ回路
nix develop --command yosys -p "read_verilog design.v; synth -top top -flatten; abc -g NAND; opt_clean; write_json design.json"

# 順序回路（dffunmap が必要）
nix develop --command yosys -p "read_verilog design.v; synth -top top -flatten; dffunmap; abc -g NAND; opt_clean; write_json design.json"
```

> yosys 0.62 では `abc -g NAND,NOT` はエラー。NOT は暗黙なので `-g NAND` でよい。
> DFF は `$_DFF_P_` のみサポート。`$_DFFE_PP0P_` 等は未対応。

### F# でコンパイル → Grid 生成

```fsharp
open WwHdl.Library
open WwHdl.PipelineWL

let json = System.IO.File.ReadAllText "design.json"

// WireLevel コンパイル
match compileWL defaultLib json with
| Ok grid -> printfn "Cells: %d" (Map.count grid)
| Error e  -> printfn "Error: %A" e
```

### テスト実行

```bash
dotnet build src/WwHdl.fsproj                    # build（テスト前に必須）
dotnet fsi src/RunTests.fsx                       # F# テスト (158/158)
web/run-test.sh                                   # WebGPU golden tests (Playwright/SiftShader)
wgpu-runner/run-tests.sh                          # GPU golden tests (Rust + wgpu, RTX 3060)
```

## 開発フロー（fsx 駆動）

本プロジェクトは **fsx スクリプト駆動の開発**を採用している。F# Interactive の対話的 REPL（状態累積）ではなく、**fsx ファイル全体を毎回 `dotnet fsi` で再評価**する運用。

### なぜ fsx 再評価か

対話的 REPL は型推論が評価順に依存して固定される（`let x = 1` の後 x は `int` に固定され再定義不可）。また状態が累積して「腐る」。fsx の「ファイル再評価」はこれを根本回避する：

- 毎回クリーンな状態から**型を最初から再推論** → 型ロックなし
- **決定的・再現可能** — LLM エージェントや CI に重要
- **静的型（コンパイル時エラー）+ 実行時検証を同時に回せる** — 幻覚（存在しない関数・型ミスマッチ）を即検出

### 開発ループ

```text
1. src/*.fs に実装（検証対象）→ dotnet build で DLL 化
2. *.fsx に検証・実験コードを書く（#r で DLL 参照）
3. dotnet fsi script.fsx → 型エラー + 実行結果を同時に取得
4. 修正して再実行（1コマンドで最短ループ）
```

### fsx の分類と命名

| パターン | 用途 | 例 |
|---------|------|-----|
| `src/Run*.fsx` | 実行・一括処理 | `RunTests.fsx`（全テスト 158/158）, `RunWl.fsx`, `RunBackfire.fsx` |
| `src/Export*.fsx` | グリッド/バイナリ出力 | `ExportSm83Multi.fsx`, `ExportRLE.fsx` |
| `src/Test*.fsx` / `Test*.fsx` | 個別機能の検証 | `TestMincpu.fsx`, `TestSm83Full.fsx` |
| `test_*.fsx` / `debug_*.fsx` | 一時的な実験・デバッグ | `test_congestion.fsx`, `debug_netid37.fsx` |

### ポイント

- `#r "bin/Debug/net8.0/WwHdl.dll"` でコンパイル済み DLL を参照（`dotnet build` 後必須）
- `#time "on"` でパフォーマンス計測（`RunProf.fsx`, `pitch_bench.fsx`）
- 一時的なデバッグスクリプトはルートに置き、安定したものは `src/` に移動する

## GPU 実行

F# の `WireLevel.step` がリファレンス実装で、`encodeCell` の byte エンコーディングが GPU 側と共有される。

- **WebGPU**: ブラウザ + WGSL compute shader、ping-pong バッファ
- **Rust + wgpu**: ネイティブ CLI (`wgpu-runner/`)

GPU 結果は F# `settle` と **byte-exact 一致**。

### ベンチマーク (RTX 3060, Vulkan)

| テスト | Cells | Steps | 時間 | 比較 |
|--------|-------|-------|------|------|
| sm83-cyc0-high | 139k | 2000 | 0.41s | SwiftShader 比 44x |
| sm83p0-cyc0-high | 425k | 2500 | 0.46s | SwiftShader 比 130x |
| mincpu-clk1 | 105k | 3500 | 0.39s | F# ref 比 205x |
| sm83-mc-add-high | 139k | 2419 | 0.56s | F# ref 比 280x |

## SM83 CPU テスト

SM83 (Game Boy CPU) のサブセット (380 gates, 69k cells) を WireLevel で E2E コンパイル・検証している。

### コンパイル

```bash
# sm83_min.json は Yosys で合成済みのファイル
# compileWL は A* ルーティングが支配的で ~53 秒要する
```

### 検証済み命合 (4 命令 × 2 clk phase = 8 golden tests)

| 命令 | A | B | PC | Flags |
|------|---|---|-----|-------|
| NOP | 0 | 0 | 1 | 0x0 |
| LD_A #42 | 42 | 0 | 2 | 0x0 |
| LD_B #17 | 42 | 17 | 3 | 0x0 |
| ADD A,B (42+17) | 59 | 17 | 4 | 0x2 |

### 重要な発見

DFF は `settle` の 1 世代目で立ち上がりエッジを検知し、その時点での入力値を捕捉する。命令値の変更後、必ず clk=0 のまま組合せ論理を収束させてから clk=1 に遷移しないと、伝播前の古い値が捕捉される。

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
- [x] `leePath`: Lee 法 BFS 最短経路
- [x] `routeAll`: 全ネット配線、`Routed(netId)` でマーク
- [x] 4 ゲート回路が Grid になることを確認

### ✅ M4 — タイミング均等化

- [x] `computeArrival`: iterative propagation で ArrivalMap を計算
- [x] `computeSlack` + `insertDelays`: スラックを `extendPath` で物理延長
- [x] `extendPath`: パス終端から -Y 方向へジグザグ延長

### ✅ M5 — E2E 検証インフラ

- [x] `compileFull` で Grid + Placement + Wire list を取得
- [x] クロックポート識別
- [x] `runWithClocks`: クロック注入タイミングを STA の target に合わせて自動計算
- [x] `measureDelay`: L ターンによる遅延ショートカットを WireWorld 実測で補正
- [x] 多段 NOT チェーン E2E テスト 59/59 全通過

### ✅ M6 — WireLevel + GPU

- [x] WireLevel CA ルール実装 (`WireLevel.fs`)
- [x] WireLevel コンパイラ (`PipelineWL.fs`)
- [x] WebGPU compute shader ( WGSL, ping-pong バッファ )
- [x] Rust + wgpu ネイティブ CLI
- [x] GPU golden tests 24/24 パス (byte-exact 一致)
- [x] SM83 CPU E2E コンパイル・検証 (380 gates, 69k cells)
- [x] SM83 multi-instruction golden tests (NOP/LD_A/LD_B/ADD)
- [x] クロックツリー経路長均等化 (`balanceClockNet`)

### 🔲 M7 — 大規模回路検証

- [ ] カウンタ (4 bit)
- [ ] レジスタ (8 bit)
- [ ] ALU (加算器)
- [ ] 乗算器

### 🔲 M8 — DFF / シーケンシャル回路拡張

- [ ] DFF (D フリップフロップ): クロックゲート型 NAND+NOT 2 段で実装
- [ ] 循環ネットのルーティング対応
- [ ] より複複雑なシーケンシャル回路の E2E 検証

## テスト

| モジュール | 内容 | 状態 |
|-----------|------|------|
| CellTest | StdCell 単体テスト | 13/13 ✅ |
| FrontendTest | Yosys JSON パース | ✅ |
| RoutingTest | Lee/BFS ルーティング | ✅ |
| StaTest | タイミング分析 | ✅ |
| E2eTest | 組合せ回路 E2E | 59/59 ✅ |
| MultiStageTest | 多段 NOT チェーン | ✅ |
| NandGateTest | NAND ゲート | ✅ |
| MultiGateTest | 複数ゲート | ✅ |
| WlSm83Test | SM83 CPU | 7/7 ✅ |
| GPU Golden | byte-exact 一致 | 24/24 ✅ |

**合計**: 158/158 通過 (mincpu.json を moon 側で追加済み)

## ライセンス

MIT

## 参考

- Conway's Game of Life / WireWorld のチューリング完全性
- QFT（Quest For Tetris）プロジェクト — CA 上の汎用計算機構築
- Golly — セルオートマトンシミュレータ
- [suzuki-navi/domino](https://github.com/suzuki-navi/domino) — 独自 CA による論理回路ビジュアルシミュレータ
- https://www.quinapalus.com/wi-index.html
