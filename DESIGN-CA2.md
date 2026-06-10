# DESIGN-CA2: WireLevel — 独自 CA ルールへのピボット

2026-06-11。最終目標 (ゲームボーイエミュレータに組込める CPU をセルオートマトンで実現)
に向けて、ターゲット CA を WireWorld から独自ルール **WireLevel** に切り替える決定と、
その設計・GPU 実行計画を記す。

## 1. なぜ WireWorld を捨てるのか

### 1.1 バックファイア (2026-06-11 実証: `src/RunBackfire.fsx`)

junc3 (NAND/NOT の核) のジャンクションが発火すると、出力方向だけでなく
**全入力配線へ電子が逆流する**。WireWorld の遷移規則 (Wire は近傍 Head 1-2 個で発火)
の帰結であり、ジャンクション設計では回避できない。

```
t=11: ..........H##.#...   ← A 入力配線を Head が逆走している
t=14: Ht###########.#...
```

- 既存テスト (89/92 pass) はワンショット評価のため、観測後に逆流が起きても無害だった
- 周期クロックの順序回路では、逆流電子が上流ゲートに再進入してジャンクションを
  誤発火させ、**サイクル毎に回路を自己破壊する**
- 対策は全ゲート入力への DIODE 挿入だが、面積・遅延・タイミング再調整のコストが
  全ゲートに掛かる

### 1.2 厳密タイミング制約

パルス方式 (1 = クロック窓に電子あり) は、全ゲートの全入力で信号到達世代が
**1 gen 精度で一致**しなければならない。現実には:

- 5 ゲートの半加算器ですら `sum(1,0)` が未解決 (TODO.md P1)
- 対角ショートカット・干渉遅延など 1 gen 単位の誤差源が多数
- ゲームボーイの CPU (LR35902 相当) は数千ゲート規模 → この精度管理はスケールしない

### 1.3 検討したが捨てた代替案: 遅延線 DFF

同期パルス方式では DFF は「1 クロック周期分の遅延線」として実現できる
(本家 Wireworld Computer のレジスタと同原理)。しかしループを閉じた瞬間に
1.1 のバックファイアが全ゲートで発生するため、DIODE 全面挿入 + 1 gen 精度の
ループ長校正が必要になり、1.2 と合わせて断念した。

## 2. WireLevel ルール仕様 (実装: `src/WireLevel.fs`)

デジタル回路専用に設計した CA。**von Neumann 近傍 (4近傍)・状態数 ≤ 64**。

### 2.1 設計原則

| 原則 | 帰結 |
|------|------|
| **レベル駆動**: セルが bool レベルを保持し「値」が流れる | グリッチは実 HW 同様に自然収束 → 厳密 STA 不要、収束待ちのみ |
| **pull 型有向配線**: Wire(dir) は背面の提示値を毎世代取り込む | 読み出しは元セルに影響しない → 逆流が構造的に不可能、ファンアウト自由 |
| **専用 Cross セル** | 配線交差が 1 セル → 配線輻輳がほぼ消滅 |
| **専用 DFF セル**: クロック立ち上がりで q := D | 順序回路がプリミティブ。レジスタ = DFF の列 |

### 2.2 セル種別

| セル | 状態 | 更新規則 |
|------|------|----------|
| `LEmpty` | — | 不変 |
| `Pin v` | level | 不変 (ホストが書く)。全方向に提示 |
| `LWire (dir, v)` | 方向 × level | v' = 背面 (-dir) の隣セルの提示値 |
| `LNand (dir, v)` | 方向 × level | v' = NOT(AND(出力方向以外の全隣接提示値))。入力 1 本なら NOT |
| `Cross (hd, vd, hv, vv)` | 水平/垂直方向 × 2 level | 各チャネル独立に背面から取り込み、各出力方向にのみ提示 |
| `LDff (dir, q, prevClk)` | 方向 × q × prevClk | D = 背面、CLK = 側面。clk ∧ ¬prevClk のとき q := D |

状態数: 1 + 2 + 8 + 8 + 16 + 16 = **51 ≤ 64** (6 bit)。
Golly ruletable (`@TABLE`, von Neumann) としてもエクスポート可能な範囲。

### 2.3 配置制約 (ルーターが保証する)

- NAND / DFF は出力方向以外の非空隣接セルをすべて入力と見なす
  → 無関係な配線をゲートに隣接させない (1 セルのクリアランス)
- ファンイン (合流) は暗黙には起きない。OR はゲートで明示的に合成する
- クロックスキュー: DFF 間の最短データパス遅延 > クロック到達スキュー
  (hold 制約)。クロックツリーの概均衡 + 最小パス長パディングで満たす

### 2.4 検証状況 (2026-06-11)

`WireLevelTest` 7/7 PASS:
wire 伝播 / NAND 真理値表 / NOT / Cross 独立性 / ファンアウト /
DFF エッジトリガ&ホールド / **toggle FF 4 サイクル (順序回路マイルストーン)**

## 3. コンパイラパイプラインへの影響

既存資産はほぼ流用できる。差し替えは末端のみ:

| 段 | 現状 (WireWorld) | WireLevel 化 |
|----|------------------|--------------|
| Frontend (yosys JSON → Netlist) | ✔ | **そのまま** ($_DFF_P_ も既にパース済み) |
| TechMap | junc3 等 StdCell | LNand/LDff 1 セル + クリアランス (セルが激減) |
| Place | 2D 配置 | そのまま (ピッチ縮小可) |
| Route (Lee/A*) | 交差回避が本質的制約 | **Cross セル挿入で交差可** → 輻輳問題が消滅 |
| STA | 1 gen 精度の到達時刻整合 | **不要**。最長パス長 → クロック周期下限の概算のみ |
| Sim | クロック注入時刻の厳密計算 | ピンにクロック波形を書くだけ |
| Emit | Golly RLE | byte グリッド (GPU) / Golly ruletable |

ルーティング時の方向付与は自明: パスは順序付きセル列なので、各セルの dir =
直前セルから当該セルへの進行方向。

## 4. GPU シェーダー実行計画

### 4.1 なぜ GPU 向きか

- 次状態 = f(自セル, 4 近傍) の純関数 → 完全データ並列
- セル状態は 1 byte (§2.2 の 6 bit + 余白)
- ダブルバッファ (ping-pong) で読み書き分離 → 同期は dispatch 境界のみ
- GB CPU 想定規模: 数千ゲート × セル化 ≈ 数百×数百〜数千×数千グリッド
  = 10⁶〜10⁷ セル/世代。GPU なら 1 dispatch で処理、CPU 比 100〜1000 倍

### 4.2 実装形態の比較

| 案 | 利点 | 欠点 |
|----|------|------|
| **WebGPU (ブラウザ + WGSL)** ★推奨 | ネイティブ依存ゼロ、可視化が無料で付く、F# は byte グリッドを JSON/bin で吐くだけ | ブラウザが必要 |
| Silk.NET (OpenGL/Vulkan compute) | .NET 内で完結 | Linux でのセットアップ・保守コスト、可視化は別途 |
| ILGPU (.NET → CUDA/OpenCL) | C#/F# でカーネルが書ける | NVIDIA 依存になりがち |

趣味プロジェクトで「動くこと優先」なら WebGPU 一択:
`web/` に静的 HTML + WGSL を置き、F# が `--export grid.bin` で吐いたものを読む。
シミュレーション本体はシェーダー、クロック駆動 (Pin 書き込み) は JS が
ストレージバッファの該当 1 byte を書くだけ。

### 4.3 WGSL カーネル設計 (スケッチ)

```wgsl
// 状態エンコーディングは WireLevel.encodeCell と一致させる
// bit7-5: kind (0=Empty 1=Pin 2=Wire 3=Nand 4=Cross 5=Dff)
// bit4-3: dir (E=0 W=1 N=2 S=3) / Cross: bit4=hDir, bit3=vDir
// bit1-0: levels

@group(0) @binding(0) var<storage, read>       src  : array<u32>;  // 4 cells/u32
@group(0) @binding(1) var<storage, read_write> dst  : array<u32>;
@group(0) @binding(2) var<uniform>             dims : vec2u;

fn cellAt(x: i32, y: i32) -> u32 {
  if (x < 0 || y < 0 || x >= i32(dims.x) || y >= i32(dims.y)) { return 0u; }
  let i = u32(y) * dims.x + u32(x);
  return (src[i >> 2u] >> ((i & 3u) * 8u)) & 0xFFu;
}

// presentedTo: セル c が toward 方向 (E0/W1/N2/S3) に提示するレベル。
// 戻り値: 0xFFFFFFFFu = 提示なし / 0 or 1 = レベル
fn presented(c: u32, toward: u32) -> u32 { /* §2.2 の表をそのまま分岐 */ }

@compute @workgroup_size(16, 16)
fn step(@builtin(global_invocation_id) gid: vec3u) {
  // 自セルと 4 近傍を読み、F# の WireLevel.step と同じ規則で次状態を計算。
  // Pin/Empty は素通し。書き込みは dst のみ (ping-pong)。
}
```

ホスト側ループ: `N 世代 = N dispatch`(同期不要)。クロック半周期ごとに
queue.writeBuffer で Pin の 1 byte を書く。表示は同じバッファを
フラグメントシェーダーでカラーマップするだけ。

### 4.4 正しさの担保

F# 実装 (`WireLevel.step`) が**リファレンス**。GPU 実装は同一グリッドを
N 世代回して byte 列一致を確認するゴールデンテストで検証する
(エクスポートした toggle FF / カウンタをテストベクタにする)。

## 5. ロードマップ (改訂)

1. ✅ WireLevel ルール + F# リファレンスシミュレータ + プリミティブ検証
2. パイプライン WireLevel 化: techMap / route (方向付与 + Cross 挿入 + クリアランス)
3. yosys 経由でカウンタ・レジスタ・ALU を WireLevel に落とす (M6 相当)
4. WebGPU ランナー (`web/`) + ゴールデンテスト
5. LR35902 サブセット (SM83) を Verilog から合成 → WireLevel CPU
6. GB エミュレータ統合: CPU コアを WireLevel シミュレータ実行に差し替え、
   バス/割込みはエミュレータ側でブリッジ

WireWorld 系 (junc3 / STA / クロック注入 Sim) は組合せ回路のデモとして残すが、
新規開発は WireLevel 上で行う。
