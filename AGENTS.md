# AGENTS.md

## Commands

```bash
dotnet build src/WwHdl.fsproj          # build
dotnet fsi src/RunTests.fsx            # all tests
web/run-test.sh                        # WebGPU golden tests
```

No separate lint or typecheck step — the F# compiler covers both. No formatter config found.

## Architecture

Multi-file F# project (10 files in `src/`). Files are compiled in dependency order:

```
Domain.fs      # Units, Domain, Rule, Netlist            ( 98 lines)
WireLevel.fs   # 独自CAルール (レベル駆動・pull型有向配線) — 新ターゲット
Library.fs     # StdCell definitions, CellTest            (516 lines)
Place.fs       # Placement algorithm                       (23 lines)
Route.fs       # Lee/BFS routing algorithm                (255 lines)
Sta.fs         # Static timing analysis                   (290 lines)
Sim.fs         # Clock-gated simulation                   (189 lines)
Pipeline.fs    # Yosys JSON frontend/parse + WireWorld pipeline (legacy)   (765 lines)
PipelineWL.fs  # yosys Netlist → WireLevel コンパイラ (P0)
E2eTests.fs    # All test modules                         (~1500 lines)
```

yosys は `nix develop --command yosys ...` で使う (flake.nix に同梱)。
例: `nix develop --command yosys -p "read_verilog verilog/counter4.v; synth -top top -flatten; abc -g NAND; opt_clean; write_json verilog/counter4.json"`
(この yosys 0.62 では `abc -g NAND,NOT` はエラー。NOT は暗黙なので `-g NAND` でよい)

順序回路合成は `dffunmap` が必要: `nix develop --command yosys -p "read_verilog verilog/mincpu.v; synth -top top -flatten; dffunmap; abc -g NAND; opt_clean; write_json verilog/mincpu.json"` (DFF は `$_DFF_P_` のみサポート。`$_DFFE_PP0P_` 等は PipelineWL.parseGateKind が未対応)

Module dependency order (within and across files):

`Units` → `Domain` → `Rule` → `Netlist` → `Library` → `CellTest` → `Place` → `Route` → `Sta` → `Sim` → `Pipeline` → `FrontendTest` → `RoutingTest` → `StaTest` → `E2eTest` → `MultiStageTest` → `NandGateTest` → `MultiGateTest`

**Compile pipeline** (railway-oriented, each stage returns `Result<'b, CompileError>`):

```
HDL → Yosys JSON → Netlist → (Gate × StdCell) → Placement → Wires → Grid → Golly RLE
```

**WireLevel compile pipeline** (PipelineWL.fs):

```
HDL → Yosys JSON → Netlist → Place (grid) → Route (A* BFS) → Emit (WireLevel grid)
```

Key design choices:
- Sparse grid: `Map<Coord, CellState>` (Empty = key absent)
- `[<Measure>] type gen` — WireWorld generations as a unit of measure; `StdCell.Latency: int<gen>` and `Wire.Delay: int<gen>` share the same dimension
- Each pipeline stage returns a **different type** to catch stage misordering at compile time
- Yosys normalizes all logic to NAND+NOT only (`abc -g NAND,NOT`); no monolithic AND/XOR cells

## Test structure

Tests are modules inside `WwHdl.fs`, not a separate test project. RunTests.fsx calls `runAll()` on each test module. Test groups: CellTest (M1), FrontendTest (M2), RoutingTest (M3), StaTest (M4), E2eTest (M5), MultiStageTest, NandGateTest, MultiGateTest.

You **must** `dotnet build` before `dotnet fsi src/RunTests.fsx` — the script references the compiled DLL.

**Total tests**: 150. Current pass rate: **150/150** (WL Mincpu 3/3 含む、WL ALU4 13/13 含む)。

## Clock balance

`PipelineWL.balanceClockNet` はクロックツリーの経路長を均等化する。小規模回路 (counter4, reg8) では skew=0 に調整可能。
287ゲートの mincpu では配線リソース不足でスキュー非調整となり警告を出すが、CPU の動作自体は正しい
(cyc6 で out=4 を確認。F# シミュレーションは 46k セルで ~80s/cycle)。

スキュー非調整でも `balanceClocks` は `Ok` を返し、コンパイルは続行される。

## External dependency

Yosys is needed to synthesize Verilog to JSON. Not bundled — must be installed separately.

## 2026-06-10: LargeCircuit BFS timeout resolved

**Root cause**: `YosysModule.Cells` used `Map<string, YosysCell>`, and `Map.toList` sorts alphabetically by key name. For 50 NAND gates, `u10` (index 2, row 2) came before `u2` (index 12, row 12), scattering consecutive chain gates across 49 rows. NetId 9's output (gate u9, row 49) needed to reach consumer u10 (row 2) — Manhattan distance 1327 cells.

**Fix**: Changed `parseCells` from `Map.ofSeq` to `List.ofSeq`, and `parseGates` from `m.Cells |> Map.toList` to `m.Cells` directly. This preserves JSON declaration order (numeric order: u0, u1, u2, ..., u49) instead of string-sorted order.

50-gate and 100-gate NAND chains now compile successfully.

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

GPU 実行は WebGPU (ブラウザ + WGSL compute、ping-pong バッファ) で実現済み。
F# の `WireLevel.step` がリファレンス実装で、`encodeCell` の byte
エンコーディングが GPU 側と共有される。

WebGPU golden tests: **10/10 パス** (toggleFF, halfAdder, mincpu-clk1, mincpu-clk0,
sm83-cyc0-high, sm83-cyc0-low,
sm83p0-cyc0-high, sm83p0-cyc0-low,
sm83p0-mc-nop/NOP, sm83p0-mc-lda/LD_A, sm83p0-mc-ldb/LD_B, sm83p0-mc-add/ADD — 各 high+low)。
GPU 結果は F# `settle` と byte-exact 一致。mincpu (46k cells) の 3500 ステップを
SwiftShader (CPU) で 43s、F# リファレンスは約 93s と約 2 倍高速。

WireWorld 系パイプライン (junc3/STA/クロック注入 Sim) は組合せ回路デモとして
維持。新規開発は WireLevel 上で行う。

詳細な技術情報はスキルファイルを参照:

詳細な技術情報はスキルファイルを参照:
- **fsharp-wireworld**: F#イディオム, Units of Measure, Struct gotchas, Yosys JSONパース, Map疎グリッド
- **compiler-pipeline**: パイプライン各段の実装詳細 (Frontend/TechMap/Place/Route/STA/Emit)
- **routing-placement**: 配置モード, Lee法BFS, passable判定, オーバーラップフォールバック
- **fsharp-testing**: テストアーキテクチャ, 8つのテストパターン, ヘルパー関数
- **sta-simulation**: 到達時刻/スラック, 遅延挿入 (waypoint/U字), クロックシミュレーション
- **wireworld-domain**: StdCell全定義, JUNC3/NAND/NOT/DIODE/SPLIT/OR2設計, 遷移規則
