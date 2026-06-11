# AGENTS.md

## Commands

```bash
dotnet build src/WwHdl.fsproj          # build (required before tests)
dotnet fsi src/RunTests.fsx            # run all 92 E2E tests (references bin/Debug/net8.0/WwHdl.dll)
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
Pipeline.fs    # Compilation pipeline (frontend→RLE)      (765 lines)
PipelineWL.fs  # yosys Netlist → WireLevel コンパイラ (P0)
E2eTests.fs    # All test modules                         (~1500 lines)
```

yosys は `nix develop --command yosys ...` で使う (flake.nix に同梱)。
例: `nix develop --command yosys -p "read_verilog verilog/counter4.v; synth -top top -flatten; abc -g NAND; opt_clean; write_json verilog/counter4.json"`
(この yosys 0.62 では `abc -g NAND,NOT` はエラー。NOT は暗黙なので `-g NAND` でよい)

Module dependency order (within and across files):

`Units` → `Domain` → `Rule` → `Netlist` → `Library` → `CellTest` → `Place` → `Route` → `Sta` → `Sim` → `Pipeline` → `FrontendTest` → `RoutingTest` → `StaTest` → `E2eTest` → `MultiStageTest` → `NandGateTest` → `MultiGateTest`

**Compile pipeline** (railway-oriented, each stage returns `Result<'b, CompileError>`):

```
HDL → Yosys JSON → Netlist → (Gate × StdCell) → Placement → Wires → Grid → Golly RLE
```

Key design choices:
- Sparse grid: `Map<Coord, CellState>` (Empty = key absent)
- `[<Measure>] type gen` — WireWorld generations as a unit of measure; `StdCell.Latency: int<gen>` and `Wire.Delay: int<gen>` share the same dimension
- Each pipeline stage returns a **different type** to catch stage misordering at compile time
- Yosys normalizes all logic to NAND+NOT only (`abc -g NAND,NOT`); no monolithic AND/XOR cells

## Test structure

Tests are modules inside `WwHdl.fs`, not a separate test project. RunTests.fsx calls `runAll()` on each test module. Test groups: CellTest (M1), FrontendTest (M2), RoutingTest (M3), StaTest (M4), E2eTest (M5), MultiStageTest, NandGateTest, MultiGateTest.

You **must** `dotnet build` before `dotnet fsi src/RunTests.fsx` — the script references the compiled DLL.

**Total tests**: 110. Current pass rate: **107/110** (WireLevel 8/8, WL Pipeline 7/7, WL Counter4 3/3 含む)。

WireLevel のテストは settle (収束待ち) でクロックを駆動する。固定半周期を使う場合は
「半周期 > 組合せ収束時間」を守ること (counter4 は halfP=128 で誤動作、512 で完動)。

### Known failures

| Test | Cause |
|------|-------|
| `2-NOT: wire (net3) delay = measureDelay` | シミュレーション実効遅延とSTA遅延が一致しない |
| `sum(1,0) = 1` (FullAdder) | タイミング問題（クロックシミュレーション） |
| `fa-like-9: compileFull succeeds` | 4列×狭ピッチ配置で配線チャネル不足 (RoutingCongestion) |

**4列配置（9-10ゲート）の限界**: 4列×ピッチ13の配置は、fan-out≧3のネットが複数存在する回路で配線輻輳が発生する。11ゲート以上は8列以上になるためこの問題は起きにくい。

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

GPU 実行は WebGPU (ブラウザ + WGSL compute、ping-pong バッファ) を推奨。
F# の `WireLevel.step` がリファレンス実装で、`encodeCell` の byte
エンコーディングが GPU 側と共有される。

WireWorld 系パイプライン (junc3/STA/クロック注入 Sim) は組合せ回路デモとして
維持。新規開発は WireLevel 上で行う。

## DFF (D flip-flop) design status — WireWorld (歴史的経緯)

`$_DFF_P_` → `GateKind.Dff` is parsed by the pipeline, and `Library.buildDLatch()` produces a 5×JUNC3 + DIODE-based level-sensitive D-latch pattern (37×7, 109 cells). However, DFF is **not yet functional** due to a fundamental WireWorld limitation:

- **JUNC3 fires on any 1-2 Head inputs**; there is no way to create a CLK-gated AND gate that requires 2 specific inputs without firing on Vdd alone
- The SR-latch (J4/J5) oscillates because the Vdd (=CLK) alone fires the junction regardless of S/R inputs
- Feedback loop delays exceed the Head lifetime (1 gen Head → 1 gen Tail → Wire), preventing stable state retention
- True CLK-gated storage requires system-level timing (clock period < loop delay) or a different storage mechanism (DIODE-based ring oscillator)

**Next approach**: Ring-oscillator-based storage: DIODE + delay loop with JUNC3 write gate (AND(D,CLK) via NAND+NOT). The ring maintains Head circulation without requiring continuous Vdd. Write gate uses 3-Head absorption to inject/clear Head based on D.

Currently DFF is excluded from `defaultLib`. `buildDLatch()` and `dff` remain in Library.fs for future development.

---

詳細な技術情報はスキルファイルを参照:
- **fsharp-wireworld**: F#イディオム, Units of Measure, Struct gotchas, Yosys JSONパース, Map疎グリッド
- **compiler-pipeline**: パイプライン各段の実装詳細 (Frontend/TechMap/Place/Route/STA/Emit)
- **routing-placement**: 配置モード, Lee法BFS, passable判定, オーバーラップフォールバック
- **fsharp-testing**: テストアーキテクチャ, 8つのテストパターン, ヘルパー関数
- **sta-simulation**: 到達時刻/スラック, 遅延挿入 (waypoint/U字), クロックシミュレーション
- **wireworld-domain**: StdCell全定義, JUNC3/NAND/NOT/DIODE/SPLIT/OR2設計, 遷移規則
