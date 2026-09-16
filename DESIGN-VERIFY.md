# 命令レベル検証の設計 — メモリバス対応 (TODO Step B)

> 状態: 設計 (2026-09-15 初版、2026-09-16 改訂: `--memory` モードの実装 (2406b0a) を取り込み方針を統合)
> 対象: sm83_subset / sm83_full (外部メモリバスを持つ CPU) を WireLevel グリッド上で検証する。

## 1. 目的

配線済みグリッド (Step A の `routed/<circuit>.bin` + `.meta.json`) を GPU で実行し、次の 2 つを確かめる。

1. **スモーク**: プログラムを流して、最終状態 (レジスタ出力・RAM) が期待どおりか
2. **照合**: 配線と CA が、合成済みネットリストと**全周期で**同じ振る舞いをするか。
   食い違ったら最初の周期で止め、原因 (典型的にはクロックスキューによる hold 違反) を調べられるようにする

1 は wgpu-runner `--memory` (2406b0a) で実現済み。2 は `--memory` の拡張として足す。

## 2. 前提として判明したこと

| 項目 | 内容 | 設計への影響 |
|---|---|---|
| バスは同期式 | `addr` / `mem_read` / `mem_write` / `data_out` はすべてレジスタ出力。`data_in` は posedge で取り込まれる | 1 クロック周期単位で入出力を扱える |
| **sm83_subset の RTL はフェッチが壊れている** (B-1 の NetlistSim で確定、2026-09-16) | FETCH で `pc` は進むが、NOP・`LD r,n` などの後に `addr_r` を更新しない。ROM `3E 42 3E 17 06 05 80 76` は `LD A,0x42` の後 addr=0x0101 のまま `mem_read=0` → NOP を読み続け、pc だけ進んで a=0x42 で止まる (本物の SM83 なら a=0x1C) | 本物の SM83 の動作を期待値にできない。**直さない (§8 Q1 で決定)** |
| sm83_full のフェッチは正常 | `PHASE_FETCH` で `addr <= pc`、`PHASE_FETCH2` で `data_in` を読む 2 段階 | CPU としての意味の検証は full で行う |
| ゲートは 3 種類だけ | subset/full は `$_NAND_` / `$_NOT_` / `$_DFF_P_` のみ (sm83_min は `$_DFF_PP0_`、R は無視) | ゲートレベルのシミュレータは小さく書ける |
| Verilog シミュレータ | iverilog / verilator はない。`yosys sim` (`-clock` `-reset` `-n` `-vcd`) は flake にある | RTL 側の参照は yosys で取れる |
| GPU の収束判定が重い | `run_until_settled` は判定のたびにグリッド全体 (w×h×4 byte) を 2 回読み戻す | subset 1197x1126 (約 1.35M セル) で周期あたり秒単位になりうる (§7 B-6) |

## 3. 方針

### 3.1 期待値は「手書きモデル」ではなく「ネットリストのシミュレーション」

sm83_min では期待値を手書きの `Sm83MinModel` で作ったが、H フラグの 4bit ラップのような
Verilog の癖を写し損ねる事故があった。subset はさらに RTL 自体が SM83 と異なる動作をする。
そこで、各段の境界ごとに**同じ入力を流して出力を突き合わせる**。

```
RTL (verilog/*.v)          ── yosys sim ──┐
                                          ├─ 一致? → 合成 + frontend が正しい     (B-5)
Netlist (yosys JSON)       ── NetlistSim ─┤
                                          ├─ 一致? → 配置配線 + CA が正しい       (B-1〜B-4, 本命)
WireLevel grid (routed/)   ── GPU ────────┘
```

ネットリストと CA は同じ yosys JSON から作られるので、不一致なら原因は配線か CA に絞れる。

### 3.2 1 つのプログラム JSON を F# と runner で共有する

```
                     programs/<name>.json (+ ROM .bin)
                    ┌──────────────┴───────────────┐
F# ExportGolden.fsx │                              │ wgpu-runner --memory
  NetlistSim        │                              │   GPU (routed/<circuit>.bin)
  + メモリモデル     ▼                              ▼   + メモリモデル
  routed/<name>.golden.json ───────────────────▶ 周期ごとに全出力と data_in を照合
                                                 最初の不一致で停止 + ダンプ
```

- メモリモデルは F# と Rust の 2 か所に置く (§5.2 の契約で一致させる)。
  二重実装の食い違いは、golden に記録した **`data_in` を runner が自分の値と照合する**ことで
  最初の周期で検出する
- 初版で考えていた「runner がメモリを持たず `data_in` を再生する `--vectors` モード」はやめる。
  `--memory` が既にあり、別モードを増やすより拡張する方がプログラム形式を 1 つに保てる
- `golden` を指定しなければ従来どおりのスモーク (最終状態の `expect` / `expectMem`) として動く

## 4. 利用形態

| 用途 | コマンド | 判定 |
|---|---|---|
| スモーク | `wgpu-runner --memory programs/<name>.json` | `expect` (出力ポート) / `expectMem` (RAM) |
| 照合 | 同上 + プログラム JSON に `"golden": "..."` | 全周期の全出力 + `data_in` が golden と一致 |
| golden 生成 | `dotnet fsi src/ExportGolden.fsx programs/<name>.json` | NetlistSim 上で `expect` / `expectMem` も確認 |

## 5. 周期の意味論 (F# と runner で一致させる契約)

### 5.1 リセット

`rstPulses` 周期、`rst=1` で次の 1 周期を行う (`data_in` は 0)。その後 `rst=0`。

### 5.2 1 周期 (`cycles` 回)

wgpu-runner `memory_program.rs` の実装を契約とする。

1. `clk=0` で収束させる
2. 出力 `addr` / `mem_read` / `mem_write` / `data_out` を読む (前の posedge でラッチされた値)
3. メモリ操作 (**書込 → 読出の順**)
   - `mem_write=1` なら `mem.write(addr, data_out)`
   - `mem_read=1` なら `data_in = mem.read(addr)`、**`mem_read=0` なら `data_in = 0`**
4. `clk=0` のまま再度収束させる (`data_in` の変化を posedge 前に伝播)
5. `clk=1` で収束させる (posedge で DFF がラッチ)
6. 全出力を読む → この周期の出力

`mem_read=0` で `data_in=0` を渡すため、sm83_subset の壊れたフェッチ (`mem_read=0` のまま次の opcode を読む) は
NOP (0x00) を読んだものとして進む。スモーク `sm83_subset_smoke` (`LD A,0x42` → a=0x42, pc=0x102) はこの挙動に依存している。

### 5.3 メモリモデル

- ROM: アドレス 0 から ROM ファイルの長さぶん。書込は無視
- RAM: `ramBase` (既定 0xC000) から `ramSize` (既定 8192) バイト。初期値 0
- それ以外の読出は 0xFF

### 5.4 初期状態

- CA の DFF は q=0 で始まる → NetlistSim も全 DFF を 0 で初期化する
- RTL (yosys sim) は未リセットのレジスタが x になる。subset はリセットで `ir` / `operand` /
  `data_out_r` を初期化しない → B-5 の比較では x を「不問」として扱う

### 5.5 NetlistSim の 1 周期

CA の DFF は「clk の立ち上がりを検知した世代の D を取り込む」。NetlistSim はクロックスキューのない理想形とする。

1. 入力を反映し、`clk=0` で組合せ回路を評価する (トポロジカル順、1 パス)
2. 全 DFF について `q <- D` (D は手順 1 の値)
3. `clk=1` で組合せ回路を再評価する → 出力を読む

§5.2 の手順 1〜4 は NetlistSim では「手順 1 を `data_in` 反映前後で 2 回」に対応する。

CA では posedge がクロック木を伝わる間 (skew) に、先にラッチした DFF の変化が後段の D に届きうる。
これが hold 違反で、NetlistSim との不一致として現れる。**この検出が照合の主目的の一つ**。

## 6. データ形式

### 6.1 プログラム (`--memory` の入力、手で書く)

2406b0a の形式をそのまま使い、`golden` を追加する。パスはプログラム JSON からの相対。

```json
{
  "circuit": "sm83_subset",
  "meta": "../routed/sm83_subset.meta.json",
  "init": "../routed/sm83_subset.bin",
  "memory": { "rom": "../routed/rom_sm83_subset_smoke.bin", "ramBase": 49152, "ramSize": 8192 },
  "rstPulses": 2,
  "cycles": 3,
  "maxStepsPerPhase": 12000,
  "checkInterval": 256,
  "expect": { "a_out": 66, "pc_out": 258 },
  "expectMem": { "0xC000": 42 },
  "golden": "../routed/sm83_subset_smoke.golden.json",
  "trace": true
}
```

- `rom` は相対パスか `base64:...`
- `expect` のポート名は meta の `outputs` のどれでもよい。**存在しない名前は起動時にエラー** (§7 B-3a)
- 現状プログラムと ROM は `routed/` に置かれている。配線成果物と分けるなら `programs/` に移す (§8 Q2)

### 6.2 golden (`routed/<program>.golden.json`、F# が生成)

```json
{
  "format": "wwc-golden/1",
  "circuit": "sm83_subset",
  "program": "sm83_subset_smoke",
  "sourceSha256": "…",
  "rstPulses": 2,
  "cycles": [
    { "dataIn": 62, "outputs": { "a_out": 1, "addr": 257, "data_out": 0, "mem_read": 1, "mem_write": 0, "pc_out": 257 } }
  ]
}
```

- `sourceSha256` を routed meta と照合し、古い配線結果に新しい golden を当てる事故を防ぐ
- `cycles[k].dataIn` は §5.2 手順 3 の値、`outputs` は手順 6 の値。リセット周期は含めない
- 出力はポート単位の整数 (LSB first)。runner は meta の probe に従って読み、
  `Unobservable` のビットは比較から除外する

## 7. 実装計画

| 段 | 内容 | 成果物 | 完了条件 |
|---|---|---|---|
| B-3a | `--memory` の堅牢化 | `memory_program.rs` | meta の `{"const":0}` / `{"unobservable":n}` を読める。`expect` の未知ポート名・未収束の周期を失敗として扱う。`gateCount` 等のフィールド名を meta と一致させる。`formatVersion` を確認 |
| B-1 | NetlistSim | `src/NetlistSim.fs` + テスト | counter4 が 0..15 でラップ、alu4 が全入力で算術と一致、**sm83_min で既存 20 命令の期待値 (GPU で 20/20 実績) と一致**。subset のフェッチ不具合を確定 |
| B-2 | F# メモリモデル + golden 生成 | `src/Testbench.fs`、`src/ExportGolden.fsx` | `sm83_subset_smoke` の `expect` が NetlistSim 上でも通る (§5.2 の契約が F# と Rust で一致している証拠) |
| B-3b | `--memory` に golden 照合を追加 | `memory_program.rs` | smoke の golden で全周期一致。**わざと 1 セル壊した .bin で不一致を検出する** (検証器の検証) |
| B-4 | subset で長いプログラムを通す (= Step C) | `programs/`、golden | 全周期一致、または不一致の原因特定 |
| B-5 | RTL との照合 | テストベンチ Verilog + `yosys sim -vcd` + VCD 比較 | NetlistSim と RTL が x 以外で一致 |
| B-6 | GPU 収束判定の高速化 (必要なら) | 変化フラグを compute shader で集計し 4 byte だけ読み戻す | B-4 の実測で周期あたりの時間が問題になったときだけ着手 |

### モジュール設計 (F#)

`NetlistSim.fs` — 純粋関数:

```fsharp
type SimError =
    | CombinationalLoop of NetId list        // DFF を通らない閉路
    | UnsupportedGate of GateKind            // NAND/NOT/DFF 以外
    | GatedClock of dff: int * clock: NetId  // DFF の C が主クロック以外
    | UndrivenNet of NetId

type CompiledNetlist   // 評価順 (トポロジカル) と DFF 一覧を前計算したもの
type SimState          // ネット値 (bool[]) + DFF q

val compile   : Netlist -> Result<CompiledNetlist, SimError>
val initial   : CompiledNetlist -> SimState                                          // 全 DFF q=0
val settleLow : CompiledNetlist -> inputs: Map<NetId, bool> -> SimState -> SimState  // clk=0 評価
val posedge   : CompiledNetlist -> SimState -> SimState                              // q <- D、clk=1 再評価
val readPorts : YosysPortBits list -> SimState -> Map<string, uint64>                // 定数ビット込み
```

- 評価順をコンパイル時に確定し、閉路・ゲートクロック・未駆動ネットを**明示的なエラー**にする
- `bool[]` インデックスは NetId から密に振り直す (9k ゲート × 数千周期でも十分速い)

`Testbench.fs` — §5.2 / §5.3 の F# 実装。プログラム JSON (§6.1) をそのまま読む。

### runner (Rust)

- 既存の `--program` モード (sm83_min 20/20) は変更しない
- `--memory` の golden 照合 (B-3b):
  - 起動時検査: golden の `sourceSha256` と meta の一致、`rstPulses` の一致
  - 周期ごと (§5.2 手順 3 と 6): `data_in` と全出力を golden と比較
  - 不一致時: 周期番号、ポート、期待値/実測値 (16 進)、異なるビット位置、settle 世代数を表示し停止。
    `--dump-dir` 指定時はその周期の setup/high グリッドを保存
  - 終了コード: 0=全周期一致 / 1=不一致・未収束 / それ以外=入力エラー

## 8. 決定事項と未決事項

**Q1. sm83_subset の RTL フェッチ不具合をどうするか → 決定: (a) (2026-09-15)**

- **(a) subset は直さず、CA とネットリストの一致の検証専用にする ← 採用**。
  一致の検証に CPU として正しい必要はなく、2026-09-16 に完走した配線結果 (111.6 分) もそのまま使える。
  CPU としての意味の検証は full で行う
- (b) subset の Verilog を直す。再合成と再配線が必要 — 不採用

**Q2. プログラム・ROM・golden の置き場所 (未決)**

- 現状 `routed/sm83_subset_smoke.json` と `routed/rom_sm83_subset_smoke.bin` が `routed/` にある
- 案: 手書きのプログラムと ROM は `programs/`、生成物の golden は `routed/` に分ける
