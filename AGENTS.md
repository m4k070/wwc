# AGENTS.md

## Commands

```bash
dotnet build src/WwHdl.fsproj          # build
dotnet fsi src/RunTests.fsx            # F# 全テスト
web/run-test.sh                        # WebGPU golden tests (Playwright/SwiftShader)
wgpu-runner/run-tests.sh               # GPU golden tests (Rust + wgpu, RTX 3060)
cd wgpu-runner && cargo test           # Rust 側ユニットテスト
wgpu-runner/memory-test.sh [program.json ...]   # メモリバス golden 照合 + 検証器の自己検証
wgpu-runner/target/release/wgpu-runner --memory <prog.json> [--engine tiled|dense] [--batch B] [--dump-dir DIR]
wgpu-runner/target/release/wgpu-runner <grid.bin> [--steps N] [--output out.bin] [--batch B] [--engine tiled|dense]   # 単発 .bin 実行
```

No separate lint or typecheck step — the F# compiler covers both. No formatter config found.

You **must** `dotnet build` before `dotnet fsi src/RunTests.fsx` (or any `src/*.fsx` script) —
they reference the compiled DLL (`src/bin/Debug/net8.0/WwHdl.dll`).

**Current test results** (2026-09-28 に実測): F# `RunTests.fsx` **421/421** /
`cargo test` (wgpu-runner) **49/49** / `wgpu-runner/run-tests.sh` (GPU golden) **24/24** /
`wgpu-runner/memory-test.sh` (引数なし) **5/5**。
Playwright (`web/run-test.sh`) は 2026-08-14 時点で 24/24 のまま未再計測。

### 配線・検証まわりのスクリプト (`src/*.fsx`)

各スクリプトの正確なオプションは冒頭のコメントを見ること（下記は要約、フラグの追加/変更はコメント側が正）。

```bash
# 配線して routed/<circuit>.{bin,meta.json} に保存（保存後に自動で再読込して整合性確認）
dotnet fsi src/ExportRouted.fsx <circuit> [--pitch X Y] [--out DIR] \
    [--place rowmajor|anneal|anneal-timing] [--moves N] [--seed N] [--backward P] \
    [--clocking single|two-phase] [--clock-pins K]

# sm83_full の配線実績コマンド（14x12 ピッチ・タイミング駆動アニーリング配置・2 相クロック・
# クロックピン 16 本で約 10 分、rip-up 0。16x12 も完走する / 12x10 は輻輳失敗）
dotnet fsi src/ExportRouted.fsx sm83_full --pitch 14 12 --place anneal-timing --clocking two-phase --clock-pins 16

# 保存済み配線結果の整合性・鮮度確認 (寸法/ピン座標/verilog JSON の SHA-256)
dotnet fsi src/LoadRouted.fsx <circuit> [--dir DIR]

# プログラム JSON (wgpu-runner --memory と同形式) を NetlistSim で実行し golden を生成
dotnet fsi src/ExportGolden.fsx <program.json>

# 配線済みグリッドの hold 静的解析 (single-edge: skew 検査 / two-phase: 相間経路 0 本の検査)
dotnet fsi src/AnalyzeHold.fsx <circuit> [--dir DIR] [--top N] [--clk PORT]
dotnet fsi src/AnalyzeHold.fsx --compile <circuit> [--place rowmajor|anneal] [--clocking single|two-phase]
dotnet fsi src/AnalyzeHold.fsx --selftest        # 静的判定と実シミュレーションの掃引照合

# 配線前の密度分析 (配置の質 vs 面積不足の切り分け)
dotnet fsi src/AnalyzePlacement.fsx <circuit> [--pitch X Y] [--place rowmajor|anneal] \
    [--moves N] [--seed N] [--backward P] [--lcut N] [--tile N]

# sm83_full ネットリスト vs ../gbfs (別リポジトリの F# 製 GB エミュレータ) の CPU 全命令差分テスト
# 前提: dotnet build src/WwHdl.fsproj と (cd ../gbfs && nix develop -c dotnet build src/gbfs.Lib/gbfs.Lib.fsproj -c Release)
dotnet fsi src/DiffTestGbfs.fsx [--variants N] [--seed S] [--only 0x86,0xCB46,...] [--interrupts N]

# sm83_full の RTL (NetlistSim) を gbfs の周辺回路 (Timer/Ppu/Joypad) につないで公開テスト ROM (blargg) を流す
# NetlistSim は Debug だと遅いため Release ビルド必須
dotnet build src/WwHdl.fsproj -c Release
dotnet fsi src/CoSimGbfs.fsx [--lockstep] [--tsv out.tsv] [--trace FROM:TO] <rom.gb>...
```

## Architecture

Multi-file F# project (18 files in `src/`, ~9,300 lines). Compile order is defined by
`src/WwHdl.fsproj` (this is also the dependency order):

```
Domain.fs        # Units, Domain, Rule, Netlist                              (  98 lines)
WireLevel.fs     # 独自CAルール (レベル駆動・pull型有向配線) — メインターゲット      ( 393 lines)
TwoPhaseClock.fs # 2 相ノンオーバーラップクロック (toTwoPhase, DFF のマスタ/スレーブ分割) ( 257 lines)
Library.fs       # StdCell definitions, CellTest (WireWorld legacy)          ( 516 lines)
GatePlacement.fs # ゲート配置のシミュレーテッドアニーリング (アーク距離最小化)      ( 386 lines)
Place.fs         # Placement algorithm (WireWorld legacy)                    (  23 lines)
Route.fs         # Lee/BFS routing algorithm (WireWorld legacy)              ( 284 lines)
Sta.fs           # Static timing analysis (WireWorld legacy)                 ( 290 lines)
Sim.fs           # Clock-gated simulation (WireWorld legacy)                 ( 189 lines)
Pipeline.fs      # Yosys JSON frontend/parse + WireWorld pipeline (legacy)   ( 760 lines)
PipelineWL.fs    # yosys Netlist → WireLevel コンパイラ (配置・A* 配線・クロック均等化) (1337 lines)
RoutedArtifact.fs # 配線済みグリッドの保存・再読込 (.bin + meta JSON、鮮度確認)   ( 534 lines)
HoldAnalysis.fs  # 配線済みグリッドの hold 静的解析 (AnalyzeHold.fsx の本体)     ( 431 lines)
NetlistSim.fs    # ゲートレベル周期シミュレータ (CA と同じ規則、検証の期待値生成用) ( 235 lines)
Testbench.fs     # メモリバス TB (runner --memory と同じプログラム JSON、golden 生成) ( 425 lines)
E2eTests.fs      # All test modules (CellTest 〜 MultiGateTest 等)            (2458 lines)
TestbenchTests.fs # Testbench の単体テスト + sm83_full 仕様テスト             ( 228 lines)
TwoPhaseTests.fs  # 2 相クロックの単体テスト・skew 耐性の実証テスト            ( 495 lines)
```

yosys は `nix develop --command yosys ...` で使う (flake.nix に同梱)。
例: `nix develop --command yosys -p "read_verilog verilog/counter4.v; synth -top top -flatten; abc -g NAND; opt_clean; write_json verilog/counter4.json"`
(この yosys 0.62 では `abc -g NAND,NOT` はエラー。NOT は暗黙なので `-g NAND` でよい)

順序回路合成は `dffunmap` が必要: `nix develop --command yosys -p "read_verilog verilog/mincpu.v; synth -top top -flatten; dffunmap; abc -g NAND; opt_clean; write_json verilog/mincpu.json"` (DFF は `$_DFF_P_` のみサポート。`$_DFFE_PP0P_` 等は PipelineWL.parseGateKind が未対応)

**Compile pipeline** (WireWorld legacy, railway-oriented, each stage returns `Result<'b, CompileError>`):

```
HDL → Yosys JSON → Netlist → (Gate × StdCell) → Placement → Wires → Grid → Golly RLE
```

**WireLevel compile pipeline** (メイン、`PipelineWL.fs`):

```
HDL → Yosys JSON → Netlist → Place (rowmajor|anneal) → Route (A* BFS) → (単相ならクロック均等化 / 2相なら DFF 分割)
    → WireLevel Grid → RoutedArtifact (.bin + meta) → NetlistSim/Testbench (golden) → wgpu-runner (GPU 検証)
```

Key design choices:
- Sparse grid: `Map<Coord, CellState>` (Empty = key absent)
- `[<Measure>] type gen` — WireLevel/WireWorld generations as a unit of measure
- Each pipeline stage returns a **different type** to catch stage misordering at compile time
- Yosys normalizes all logic to NAND+NOT only (`abc -g NAND,NOT`); no monolithic AND/XOR cells
- 配置は既定で行優先 (`rowmajor`)。密な回路 (sm83_full 規模) はシミュレーテッドアニーリング配置
  (`GatePlacement.fs`、アーク距離の総和を最小化) の方が配線完走率が大きく上がる。詳細は TODO.md 「残課題」参照
- クロックは既定で単相 + 経路長均等化 (`balanceClockNet`)。アニーリング配置のように密になると
  均等化の蛇行余地がなくなり hold 違反が出るため、2 相ノンオーバーラップクロック
  (`TwoPhaseClock.fs`、下記「クロック方式」参照) を使う

## Test structure

Tests are modules across `E2eTests.fs` / `TestbenchTests.fs` / `TwoPhaseTests.fs`
(not a separate test project). `RunTests.fsx` calls `runAll()` on each test module.

主なテストモジュール: CellTest / FrontendTest / RoutingTest / StaTest / E2eTest / MultiStageTest /
NandGateTest / MultiGateTest (WireWorld legacy、凍結) / WlSm83Test / RoutedArtifactTest /
NetlistSimTest / TestbenchTest (sm83_full 仕様テスト含む) / GatePlacementTest / WL-2PH 系
(TwoPhaseClock/settleIncremental/skew 耐性)。内訳は README.md の「テスト」表を参照。

**SM83 multi-instruction golden tests** (`ExportSm83Multi.fsx` + `golden-cases.json`): 4 命令 (NOP/LD_A/LD_B/ADD) の各 clk phase (high/low) の F# `settle` をリファレンスとし、GPU が byte-exact 一致することを検証 (8 tests)。

**重要な発見 (DFF ラッチのタイミング)**: DFF は `settle` の 1 世代目で立ち上がりエッジを検知し、その時点での入力値を捕捉する。命令値の変更後、必ずクロックが低いまま組合せ論理を収束させてから立ち上げないと、伝播前の古い値が捕捉される。単相・2 相いずれのクロック駆動手順もこれを踏まえて「相ごとに収束を挟む」形になっている (詳細は DESIGN-VERIFY.md §5.2 / §5.2.1)。

## クロック方式

- **単相 (`Clocking.SingleEdge`、既定)**: `PipelineWL.balanceClockNet` がクロックツリーの経路長を均等化する。
  小規模回路 (counter4, reg8) では skew=0 に調整可能。密な回路では配線資源不足で均等化しきれず hold 違反の
  リスクが残る (`AnalyzeHold.fsx` で検査)。
- **2 相ノンオーバーラップ (`Clocking.TwoPhase`、CLI: `--clocking two-phase`)**: 各 DFF をマスター (clk_a) /
  スレーブ (clk_b) に分け、skew 均等化をしない。ホストは「clk_a=1 → settle → clk_a=0, clk_b=1 → settle」
  (2 周期目以降は短縮手順) で駆動する。同じ相の DFF 間に組合せ経路が無い限り hold 違反が構造的に起きない。
  手順の正確な根拠は DESIGN-VERIFY.md §5.2.1、不変条件の検査は `AnalyzeHold.fsx` (`--clocking two-phase` /
  meta の `clocking` から自動判定)。論理 Netlist / NetlistSim / golden は単相のまま変わらない。
  sm83_full はこの方式で 16.1 分・rip-up 0 で配線完走した (2026-09-27)。クロックピンは `--clock-pins K` で
  K 個の区画に分けられる (既定 1)。区画ごとに独立したピン・配線木を持ち、ホストは同じ相の全ピンを
  同じ世代に書く。到達時間が区画ごとにずれても、同相の DFF 間に組合せ経路が無いので hold 違反は起きない。
  sm83_full は K=16 で配線し直してあり (10.0 分、rip-up 0)、クロック最大到達は 1,478 / 1,494 → 186 / 196 世代
  (ピッチ 14x12 のとき。20x14 では 284 / 282 世代)。

## External dependency

- **Yosys** — Verilog を JSON に合成するために必要。Not bundled — `nix develop` 経由で使う。
- **gbfs** (`../gbfs`、別リポジトリの F# 製 Game Boy エミュレータ) — `DiffTestGbfs.fsx` / `CoSimGbfs.fsx`
  が参照モデルとして使う。事前に `gbfs.Lib` を Release ビルドしておく必要がある。

## sm83_full の現状 (2026-09-28)

sm83_full (通常命令 (STOP を除く) + CB prefix 256 + 割込み + HALT バグ、組合せ 10,859 + DFF 181) は
**14x12 ピッチ・タイミング駆動アニーリング配置・2 相クロック・クロックピン 16 本で配線完走**
(10.0 分、rip-up 0、grid 1481x1273、`routed/sm83_full.{bin,meta.json}`、meta formatVersion 3)。
クロック木の最大到達は clk_a 186 / clk_b 196 世代 (ピン 1 本のときは 1,478 / 1,494)。
クリティカルパスの 97% はゲート間の「配置距離 (セル数)」なので、ピッチが 1 周期の直接のスケール因子になる
(20x14 → 14x12 で 34 本の GPU 照合 150.7 → 112.6 秒)。12x10 は輻輳失敗 (18,041/18,691 で停止)。
RTL の正しさは blargg `cpu_instrs` 個別版 **11/11 PASS** (`CoSimGbfs.fsx --lockstep`) で、
RTL ≡ CA は GPU 全周期照合 **37/37** (`wgpu-runner/memory-test.sh` 一式、34 本で 78 秒。tiled エンジンの
`BLOCK_GENS` を実測で 8 → 3 にした効果。詳細は TODO.md 残課題 1 (e)) で確認済み。
残課題 (優先順) は TODO.md 「残課題」節を参照 (`data_in` 窓の組合せ収束、`compileWL` の既定値見直し、
サイクル精度、mooneye acceptance 系、STOP 未実装、NetlistSim 高速化など)。

## 2026-06-10: LargeCircuit BFS timeout resolved

**Root cause**: `YosysModule.Cells` used `Map<string, YosysCell>`, and `Map.toList` sorts alphabetically by key name. For 50 NAND gates, `u10` (index 2, row 2) came before `u2` (index 12, row 12), scattering consecutive chain gates across 49 rows. NetId 9's output (gate u9, row 49) needed to reach consumer u10 (row 2) — Manhattan distance 1327 cells.

**Fix**: Changed `parseCells` from `Map.ofSeq` to `List.ofSeq`, and `parseGates` from `m.Cells |> Map.toList` to `m.Cells` directly. This preserves JSON declaration order (numeric order: u0, u1, u2, ..., u49) instead of string-sorted order.

## 2026-06-11: WireLevel への戦略ピボット (DESIGN-CA2.md)

**WireWorld は順序回路でスケールしない**ことが実証された:

1. **バックファイア** (`src/RunBackfire.fsx` で実証): junc3 発火時に電子が
   全入力配線へ逆流する。ワンショットテストでは無害だが、周期クロックの
   順序回路ではサイクル毎に上流ゲートを誤発火させる。対策は全入力への DIODE 挿入。
2. **厳密タイミング**: パルス方式は全ゲート入力の 1gen 精度整合が必須。
   5 ゲートの半加算器ですら sum(1,0) が未解決のまま。

→ 独自 CA ルール **WireLevel** (`src/WireLevel.fs`) を新ターゲットに採用。
レベル駆動・pull 型有向配線・専用 Cross/DFF セル。von Neumann 近傍・51 状態。
toggle FF (DFF+NOT ループ) の複数サイクル動作を検証済み — WireWorld で
不可能だった順序回路が動く。詳細は **DESIGN-CA2.md** 参照。

GPU 実行は WebGPU (ブラウザ + WGSL compute、ping-pong バッファ) と Rust + wgpu (ネイティブ CLI、
`wgpu-runner/`) で実現済み。F# の `WireLevel.step` がリファレンス実装で、`encodeCell` の byte
エンコーディングが GPU 側と共有される。GPU 結果は F# `settle` と byte-exact 一致。

WireWorld 系パイプライン (junc3/STA/クロック注入 Sim) は組合せ回路デモとして
維持。新規開発は WireLevel 上で行う。

詳細な技術情報はスキルファイルを参照:
- **fsharp-wireworld**: F#イディオム, Units of Measure, Struct gotchas, Yosys JSONパース, Map疎グリッド
- **compiler-pipeline**: パイプライン各段の実装詳細 (Frontend/TechMap/Place/Route/STA/Emit)
- **routing-placement**: 配置モード, Lee法BFS, passable判定, オーバーラップフォールバック
- **fsharp-testing**: テストアーキテクチャ, テストパターン, ヘルパー関数
- **sta-simulation**: 到達時刻/スラック, 遅延挿入 (waypoint/U字), クロックシミュレーション
- **wireworld-domain**: StdCell全定義, JUNC3/NAND/NOT/DIODE/SPLIT/OR2設計, 遷移規則
