# AGENTS.md

## Commands

```bash
dotnet build src/WwHdl.fsproj          # build (required before tests)
dotnet fsi src/RunTests.fsx            # run all 92 E2E tests (references bin/Debug/net8.0/WwHdl.dll)
```

No separate lint or typecheck step — the F# compiler covers both. No formatter config found.

## Architecture

Multi-file F# project (~3290 lines across 8 files in `src/`). Files are compiled in dependency order:

```
Domain.fs      # Units, Domain, Rule, Netlist            ( 98 lines)
Library.fs     # StdCell definitions, CellTest            (428 lines)
Place.fs       # Placement algorithm                       (23 lines)
Route.fs       # Lee/BFS routing algorithm                (255 lines)
Sta.fs         # Static timing analysis                   (290 lines)
Sim.fs         # Clock-gated simulation                   (189 lines)
Pipeline.fs    # Compilation pipeline (frontend→RLE)      (765 lines)
E2eTests.fs    # All test modules                         (1242 lines)
```

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

**Total tests**: 92 (timing-issue tests除去). Current pass rate: **89/92**.

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

---

詳細な技術情報はスキルファイルを参照:
- **fsharp-wireworld**: F#イディオム, Units of Measure, Struct gotchas, Yosys JSONパース, Map疎グリッド
- **compiler-pipeline**: パイプライン各段の実装詳細 (Frontend/TechMap/Place/Route/STA/Emit)
- **routing-placement**: 配置モード, Lee法BFS, passable判定, オーバーラップフォールバック
- **fsharp-testing**: テストアーキテクチャ, 8つのテストパターン, ヘルパー関数
- **sta-simulation**: 到達時刻/スラック, 遅延挿入 (waypoint/U字), クロックシミュレーション
- **wireworld-domain**: StdCell全定義, JUNC3/NAND/NOT/DIODE/SPLIT/OR2設計, 遷移規則
