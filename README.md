# wwc — HDL → Cellular Automaton Compiler

任意の HDL（Verilog 等）で記述した論理回路を、セルオートマトン上で動作するパターンへコンパイルする実験的プロジェクト。F# 製。

> **ステータス (2026-09-28): SM83 CPU フルセット (sm83_full、通常命令 (STOP を除く) + CB prefix 256 + 割込み、
> 組合せ 10,859 + DFF 181) を WireLevel CA 上で配線完走。2 相クロックのピンを各 16 本に分け (issue #7 (b))、
> さらにピッチを 20x14 → 14x12 に詰めて (残課題 1 (d))、クロックの最大到達を 1,478 / 1,494 → 186 / 196 世代、
> 34 本の GPU 照合を 195 → 113 秒にした。
> RTL の正しさは blargg `cpu_instrs` 個別版 11/11 PASS、RTL ≡ CA は GPU 全周期照合 37/37 で確認済み。
> テスト F# 421/421 / cargo test 49/49 / GPU golden 24/24 / memory-test.sh 5/5 通過。**

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
  Domain.fs        # Units, Domain, Rule, Netlist
  WireLevel.fs     # 独自CAルール (レベル駆動・pull型有向配線) — メインターゲット
  TwoPhaseClock.fs # 2 相ノンオーバーラップクロック (DFF のマスタ/スレーブ分割)
  Library.fs       # StdCell definitions, CellTest (WireWorld legacy)
  GatePlacement.fs # ゲート配置のシミュレーテッドアニーリング (アーク距離最小化)
  Place.fs         # Placement algorithm (WireWorld legacy)
  Route.fs         # Lee/BFS routing algorithm (WireWorld legacy)
  Sta.fs           # Static timing analysis (WireWorld legacy)
  Sim.fs           # Clock-gated simulation (WireWorld legacy)
  Pipeline.fs      # Yosys JSON frontend + WireWorld pipeline (legacy)
  PipelineWL.fs    # Yosys Netlist → WireLevel コンパイラ (配置・A* 配線・クロック均等化)
  RoutedArtifact.fs # 配線結果 (.bin + meta JSON) の保存・再読込・鮮度確認
  HoldAnalysis.fs  # 配線済みグリッドの hold 静的解析
  NetlistSim.fs    # ゲートレベル周期シミュレータ (CA と同じ規則、検証の期待値生成用)
  Testbench.fs     # メモリバス TB (runner --memory と同じプログラム JSON、golden 生成)
  E2eTests.fs      # All test modules
  TestbenchTests.fs # Testbench 単体テスト + sm83_full 仕様テスト
  TwoPhaseTests.fs  # 2 相クロックの単体テスト・skew 耐性の実証テスト
routed/          # 配線成果物 (<circuit>.bin / <circuit>.meta.json / golden)
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
dotnet fsi src/RunTests.fsx                       # F# テスト (421/421)
web/run-test.sh                                   # WebGPU golden tests (Playwright/SwiftShader)
wgpu-runner/run-tests.sh                          # GPU golden tests (Rust + wgpu, RTX 3060) — 24/24
cd wgpu-runner && cargo test                      # Rust 側ユニットテスト — 49/49
wgpu-runner/memory-test.sh [program.json ...]     # メモリバス CPU の golden 照合 + 検証器の検証 — 5/5
dotnet fsi src/DiffTestGbfs.fsx [--variants N]    # sm83_full ネットリストと ../gbfs の CPU の全命令差分テスト (要 gbfs.Lib Release ビルド)
dotnet build src/WwHdl.fsproj -c Release && dotnet fsi src/CoSimGbfs.fsx --lockstep <rom.gb>
                                                   # sm83_full RTL + gbfs 周辺回路で公開テスト ROM (blargg cpu_instrs) を流す
```

（テスト数は 2026-09-28 時点で実測。Playwright (`web/run-test.sh`) は 2026-08-14 時点の 24/24 のまま未再計測）

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
| `src/Run*.fsx` | 実行・一括処理 | `RunTests.fsx`（全テスト 421/421）, `RunWl.fsx`, `RunBackfire.fsx` |
| `src/Export*.fsx` | グリッド/バイナリ出力 | `ExportSm83Multi.fsx`, `ExportRLE.fsx` |
| `src/Test*.fsx` / `Test*.fsx` | 個別機能の検証 | `TestMincpu.fsx`, `src/LoadRouted.fsx` |
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

### ベンチマーク (RTX 3060, Vulkan、2026-06-11 時点。以後未再計測)

| テスト | Cells | Steps | 時間 | 比較 |
|--------|-------|-------|------|------|
| sm83-cyc0-high | 139k | 2000 | 0.41s | SwiftShader 比 44x |
| sm83p0-cyc0-high | 425k | 2500 | 0.46s | SwiftShader 比 130x |
| mincpu-clk1 | 105k | 3500 | 0.39s | F# ref 比 205x |
| sm83-mc-add-high | 139k | 2419 | 0.56s | F# ref 比 280x |

sm83_full 全 33 本 (33 プログラム) の GPU 全周期照合は、収束判定の GPU 化・tiled エンジン・
クロック木最短経路化により約 2 時間 → **191 秒** まで高速化した (2026-09-27、PR #5)。

## SM83 CPU テスト

SM83 (Game Boy CPU) を WireLevel で E2E コンパイル・検証している。規模は 3 段階:

| 回路 | gates (最終) | 状態 |
|------|------|------|
| sm83_min | 380 | 4 命令 byte-exact 検証済み (NOP/LD_A/LD_B/ADD) |
| sm83_subset | 3,553 | ✅ 配線完走 (20x14、行優先、111.6 分、skew 46)。CA がネットリストと全周期一致 (367 周期) |
| sm83_full | 10,859 combinational + 181 DFF (2 相化で DFF 362、gateCount 11,221) | ✅ 配線完走 (14x12、タイミング駆動アニーリング配置 + 2 相クロック + クロックピン 16 本、10.0 分、rip-up 0、2026-09-28)。全命令セット (通常命令は STOP を除く + CB prefix 256) + 割込み (irq / int_ack、HALT バグ含む)。gbfs の CPU との差分テストで通常命令+CB 命令の全 498 通り × 4 パターン一致 (NetlistSim)。RTL の正しさは blargg `cpu_instrs` 11/11、RTL≡CA は GPU 全周期照合 37/37 で確認済み |

### コンパイル

```bash
# 配線して routed/<circuit>.{bin,meta.json} に保存 (保存後に再読込して整合性を確認)
dotnet fsi src/ExportRouted.fsx sm83_subset

# sm83_full はアニーリング配置 + 2 相クロックで約 16 分 (行優先・単相だと輻輳失敗するか数時間かかる。TODO.md 参照)
nohup dotnet fsi src/ExportRouted.fsx sm83_full --pitch 20 14 --place anneal --clocking two-phase > routed/sm83_full.log 2>&1 &

# 保存済みの結果を確認 (寸法・ピン座標の整合性、verilog JSON が配線時から変わっていないか)
dotnet fsi src/LoadRouted.fsx sm83_subset
```

- compileWL はピッチを回路規模から自動決定し、輻輳失敗時は自動拡大する (12x10 → 16x12 → 20x14 → 24x16)。`--pitch X Y` で固定も可能。sm83_full はタイミング駆動配置で 14x12 が最小 (12x10 は輻輳失敗)
- 配置は既定で行優先 (`--place rowmajor`)。`--place anneal` はアーク距離 (駆動元→受け手のマンハッタン距離の総和)
  を最小化するシミュレーテッドアニーリング配置で、sm83_full のような密な回路では配線完走率を大きく上げる
  (詳細は TODO.md 「経緯」表)
- クロック終端は優先配線され、既定 (`--clocking single`) では balanceClockNet がスキューを均等化する
- `--clocking two-phase` は各 DFF をマスター (clk_a) / スレーブ (clk_b) に分ける 2 相ノンオーバーラップクロックで配線する。
  skew を均等化せず、hold は相の間の settle で構造的に守る (駆動手順は DESIGN-VERIFY.md §5.2.1、検査は
  `dotnet fsi src/AnalyzeHold.fsx <circuit>` が meta の clocking に従って行う)
- meta JSON はポート名 → ビット毎の座標 (LSB first) を持つ。yosys が定数に畳んだビットは `{"const":0}` として位置を保つ

### 検証済み命令 (4 命令 × 2 clk phase = 8 golden tests)

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

### ✅ M7 — 大規模回路検証

- [x] カウンタ (4 bit) / レジスタ (8 bit) / ALU (加算器)
- [x] 配置ピッチの動的調整 → 輻輳失敗時の自動拡大 (`pitchFor` / `pitchSequence`)
- [x] sm83_subset 配線完走 (3,553 gates、20x14、約 100 分)
- [x] 探索上限削減 (50M → 5M) による爆発ネットの早期確定
- [x] rip-up 撤去対象を「ブロッカー記録ベース」に改善
- [x] クロック優先配線 (skew 1068 → 46)

### ✅ M8 — SM83 フルセット (2026-09-27, PR #5)

- [x] CB prefix 命令 (0xCB) のデコード有効化 (9,059 gates、`d008cbe`)
- [x] 即値読出の off-by-one 修正 (FETCH2/IMM で加算前の pc を addr に出していた。9,155 gates)
- [x] 仕様テスト 16 本で RTL 不具合 7 件を修正 (ALU/INC/DEC の右辺、H/C フラグ幅、POP/RET、CALL/RST、JP、ALU (HL)。9,958 gates)
- [x] gbfs との差分テストで RTL 不具合 6 分類を修正し、494 命令すべて一致 (10,650 gates)
- [x] 割込み (IE/IF は CPU の外、EI の 1 命令遅延、RETI、HALT 復帰) を実装 (10,654 gates)
- [x] HALT バグ (Pan Docs 準拠) を実装 (10,767 gates)。STOP は未実装
- [x] DAA を含む全命令の RTL 不具合を修正し gbfs 差分 0 件に (最終 10,859 combinational + 181 DFF)
- [x] 配置をシミュレーテッドアニーリング (`GatePlacement.fs`) に切替え、2 相ノンオーバーラップクロック
      (`TwoPhaseClock.fs`) を導入。sm83_full の配線完走 (20x14、16.1 分、rip-up 0)。
      行優先・単相では 89% で輻輳失敗していた (経緯は TODO.md 参照)
- [x] CB 命令の動作検証 (gbfs 差分 498 命令 × 4 パターン一致 + GPU golden)
- [x] 配線時間の短縮 (アニーリング配置切替で 142 分 → 16.1 分)
- [x] 通常命令「全 256」の網羅確認 (STOP・未定義 opcode を除く)
- [x] RTL の正しさを公開テスト ROM で確認: blargg `cpu_instrs` 個別版 11/11 PASS (`CoSimGbfs.fsx --lockstep`)
- [x] RTL ≡ CA の確認: GPU 全周期照合 37/37 (`wgpu-runner/memory-test.sh`)
- [ ] 残課題 (CA 高速化の続き、`compileWL` 既定値の見直し、サイクル精度、mooneye acceptance 系、
      STOP 未実装など) は TODO.md 「残課題」節を参照

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
| WlSm83Test | SM83 CPU | ✅ |
| RoutedArtifactTest | 配線結果の保存・再読込 | ✅ |
| NetlistSimTest | ゲートレベルシミュレータ (sm83_min 20 命令ほか) | ✅ |
| TestbenchTest | メモリバス TB (I/O・割込み含む) + sm83_full 仕様テスト (手書き + gbfs 差分の回帰、DAA 追加分含む) | ✅ |
| WlPlacementTest | ゲート配置のシミュレーテッドアニーリング最適化 (`GatePlacement.fs`) | ✅ |
| WlTwoPhaseTest (WL-2PH) | 2 相ノンオーバーラップクロック (DFF 分割・skew 耐性・sm83_min 検証) | 38/38 ✅ |
| GPU Golden (`wgpu-runner/run-tests.sh`) | byte-exact 一致 | 24/24 ✅ |
| cargo test (`wgpu-runner`) | Rust 側ユニットテスト | 49/49 ✅ |
| `wgpu-runner/memory-test.sh` | メモリバス golden 照合 + 検証器の自己検証 | 5/5 ✅ |

**合計 (F#, `dotnet fsi src/RunTests.fsx`)**: **421/421** 通過 (2026-09-28 実測。内訳の正確な数はコマンド出力を参照)。
上記の個別カウントを持つ行以外は複数テストを含むモジュールで、正確な内訳は `RunTests.fsx` の出力を参照のこと。

## ライセンス

MIT

## 参考

- Conway's Game of Life / WireWorld のチューリング完全性
- QFT（Quest For Tetris）プロジェクト — CA 上の汎用計算機構築
- Golly — セルオートマトンシミュレータ
- [suzuki-navi/domino](https://github.com/suzuki-navi/domino) — 独自 CA による論理回路ビジュアルシミュレータ
- https://www.quinapalus.com/wi-index.html
