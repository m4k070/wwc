# WireLevel コンパイラ TODO

## 現在のテスト結果: F# 166/166 / GPU golden 24/24 / Playwright 24/24 PASS 🎉
(F# は 2026-09-15 に確認。mincpu.json 追加で 150 → 158、RoutedArtifactTest 追加で 166。
GPU/Playwright は d008cbe, 2026-08-14 時点)

## 次の一手 (M8: SM83 フルセット)

方針: 長時間配線 (subset 約 100 分 / full 5〜8 時間) の成果を捨てないよう、
**保存 → 検証の道具を先に揃え、安い sm83_subset で全工程を通してから sm83_full に進む**。

### Step A: 配線成果物の保存と再利用 ✅ 完了 (2026-09-15)

- [x] `src/RoutedArtifact.fs`: 配線結果を `routed/<circuit>.bin` (exportGrid 形式) +
      `routed/<circuit>.meta.json` として保存・再読込
      * meta: ポート名 → ビット毎の座標 (.bin と同じ正規化座標、LSB first)、
        元 verilog JSON の SHA-256、生成時の git commit (`+dirty` 付き)
      * 出力ビットは `CellProbe` (ゲート出力セル/ピン) / `ConstProbe` / `Unobservable` の DU。
        yosys の定数ビット ("0"/"1") を位置を保って保持する
        (`Pipeline.parseYosysJson` は定数を捨てるためビット位置がずれる。sm83_min の
        b_out[6:7]、sm83_full の flags 下位 4bit が該当)
      * 読込時に寸法・ファイル長・プローブ先セル種別を検証、`ensureFresh` で
        verilog JSON の変更を検出。書込は一時ファイル経由で置換
- [x] `src/ExportRouted.fsx <circuit> [--pitch X Y] [--out DIR]`: 配線 → 保存 → 再読込確認。
      ポート解析を配線前に済ませ、meta 生成に失敗しても grid を `.rescue.bin` に退避
- [x] `src/LoadRouted.fsx <circuit> [--dir DIR]`: 整合性・鮮度の確認 (Step B/C の雛形)。
      終了コード 0=OK / 1=エラー / 3=verilog JSON 変更
- [x] テスト `RoutedArtifactTest` 8 件 (counter4 を保存 → 読込後、再配線なしで 0→1→2 と数える)
- [x] 動かなかった `TestSm83Subset.fsx` と、結果を捨てる `TestSm83Full.fsx` を削除
- [x] `routed/*.bin` / `*.meta.json` は git 管理する (長時間配線の結果を vega/moon 間で共有するため。
      sm83_min で 100KB、subset/full は数 MB 見込み)

### Step B: メモリバス対応の命令レベル GPU 検証

sm83_subset / sm83_full は外部メモリバス (addr → data_in, mem_write) を持つ。
wgpu-runner `--program` は命令ごとにピンを静的に書き込むだけなので、フェッチに対応できない。

- [ ] wgpu-runner にメモリモデルを追加: 各フェーズで addr/mem_read を読み、
      ROM/RAM イメージから data_in を供給、mem_write 時に書き込む
- [ ] program JSON を「メモリイメージ + 実行サイクル数 + 期待レジスタ値」形式に拡張
- [ ] 期待値の参照モデル: sm83_subset.v / sm83_full.v の写像 (Sm83MinModel と同様、
      Verilog の癖に合わせる)

### Step C: sm83_subset で全工程を通す

- [ ] sm83_subset を配線 (約 100 分、バックグラウンド) → Step A で保存
- [ ] Step B の仕組みで subset 命令の GPU 検証 (LD/ALU/INC/DEC/JP/メモリ読み書き)

### Step D: sm83_full

- [ ] sm83_full (9,059 gates) の配線完走 — 5〜8 時間見込み。バックグラウンド実行 +
      進行ログで監視し、Step A で必ず保存。16x12 で失敗 → 20x14 再試行の時間も含む
- [ ] CB 命令の動作検証
- [ ] 通常命令「全 256」の網羅確認 (参照モデルとの一致で機械判定)

### 並行して進められる改善 (必須ではない)

- [ ] 配線時間の短縮 (ネット単位の並列化 / ヒューリスティック改善)。
      Step D の前に効果があれば full の待ち時間が減る。大規模回路から始めるピッチを
      20x14 にして、16x12 の失敗分を省く案もある (`pitchFor`)
- [ ] sm83_min のクロックのずれを `verify_clock.fsx` で測り直す
      (2026-09-15 の ExportRouted 実行で `ClockSkewUnresolved (NetId 2, 92)` — 旧記録 110 から改善したが未解消)
- [ ] web/sm83_mc_*.bin の再生成 (P3 参照)

最終目標: ゲームボーイエミュレータに組込める CPU をセルオートマトンで実現する。

---

## P0: パイプラインの WireLevel 化 ✅ 完了 (2026-06-11, PipelineWL.fs)

- [x] compileWL: yosys JSON → LGrid (techMap は 1 セルゲートなので compileWL 内で完結)
- [x] placeWL: 正方格子配置 (pitchX=24, pitchY=16 — alu4 輻輳対策で拡大) + 左端ピン列
- [x] routeWL: (Coord,Dir) 状態 A*、Cross 化直交通過、ゲート隣接クリアランス、
      ファンアウトタップ (タップ元は非交差化)
- [x] クロック配線 (通常ネットとして DFF S 側面へ。均等化は未実装 → P1 残課題)
- [x] emitWL: LGrid 合成 (byte 一括エクスポートは P2 で)
- [x] E2E: 半加算器真理値表 4/4

## P1: 順序回路 E2E

- [x] yosys $_DFF_P_ → LDff 経路の E2E (toggle FF、q=1,0,1,0)
- [x] 4bit カウンタ (verilog/counter4.v → yosys → 21 ゲート → 0..15 ラップ確認)
- [x] 8bit レジスタ
- [x] ALU (2bit ADD/AND/OR/XOR, 14 tests)
- [x] ALU 4bit (verilog/alu4.v → yosys → 85 ゲート, 13 tests)。
      初回は RoutingCongestion で失敗 → A* に転回ペナルティ (+4) を導入し
      経路を直線化 (直線セルのみ交差可のため後続ネットの交差点が増える)、
      pitchX=24 / pitchY=16 に拡大して解消
- [x] クロックスキュー均等化 (hold 対策, 2026-06-11)。counter4/reg8 で skew=0 達成。
      実装 (PipelineWL.routeWL 内 balanceClockNet):
      * DFF クロック終端をタップ禁止に (数珠つなぎ分配だと均等化が原理的に不可能)
      * 各終端の専有サフィックス (リーフ edge) のみ延長 → 木の再帰均等化が不要
      * 延長は (1) 直線 run のコの字バンプ (+2h)、足りなければ
        (2) リーフ edge を撤去して幹の任意点から「到達 = tMax」の正確長 DFS で再配線
        (スラック消費優先の方向順序 + パリティ/残距離枝刈り + 自己重複禁止)
      * 経路長のパリティは端点で固定のため、tMax / tMax-1 の両方を試す (残差 ≤1)
      * 検証: WireLevel.clockArrivals (終端からの逆走でパス長 = 到達世代を実測)

### 学んだ設計則

- **半周期 > 組合せ収束時間** (setup 制約)。counter4 は halfP=128 で誤動作、
  512 で完動。テストは固定周期でなく `settle` (収束待ち) でクロックを駆動する。
- **P0 compile では maxExplore=2M が必要**。300k では 264k cells の端-to-端経路が探索不足。
  2M で全経路確保。既存テスト (150/150) への影響なし。
- **P0 settle は ~2500 gen 必要** (cyc0-high が limit hit, cyc0-low は 2161 gen で収束)。
  F# 実装は ~200s/settle と低速 → GPU 検証に委ねる。

## P2: GPU 実行 (WebGPU)

- [x] web/: WGSL compute カーネル (DESIGN-CA2.md §4.3) + ping-pong バッファ
- [x] F# → grid.bin エクスポート / JS ローダー
- [x] ゴールデンテスト: F# WireLevel.step の .bin 入出力自己無矛盾
- [x] 可視化 (canvas カラーマップ描画)
- [x] GPU ゴールデンテスト (2026-06-11, `web/run-test.sh` で 2/2 PASS)。
      未収束 init (ピン設定直後) → GPU N 世代 → F# settle 結果とバイト一致。
      * Playwright はヘッドレスでは SwiftShader adapter。`--enable-unsafe-webgpu`
        が必須 (旧 `--enable-webgpu` は実在しないスイッチで、テストは skip していた)
      * headless-shell ビルドは WebGPU 非対応 → `channel: 'chromium'` を使う
      * Pop!_OS 等ではシステムライブラリで動く。NixOS では flake.nix の
        WWC_CHROMIUM_LIBS を run-test.sh が LD_LIBRARY_PATH に注入
      * 正式ランナーは Playwright (web/run-test.sh)。旧マシン (Vivaldi/NixOS)
        前提だった golden-test-puppeteer.mjs は削除済み

## P3: CPU へ

- [x] SM83 (LR35902) サブセットの Verilog 記述 → yosys 合成 (sm83_min.json, 380 gates)
- [x] P0 拡張 (1095 gates): LD r,#imm8 / MOV r1,r2 / ALU op,r / INC/DEC r / NOP。yosys 合成確認
- [x] P0 compileWL 成功: 264,705 cells, pitch 24×16, maxExplore=2M
- [x] GPU golden test: SM83 P0 10 ケース PASS (cyc0 2 + NOP/LDA/LDB/ADD multi-cycle 8)
- [x] WireLevel CPU のマイクロベンチ (src/TestSm83.fsx, NOP/LD/ALU 8命令) — F# 実装は低速すぎるため GPU 検証で代替
- [x] GPU での SM83 動作確認 (web/ golden test 16/16 PASS, うち SM83/SM83P0 12 ケース)
- [x] 命令レベル GPU 検証 (Phase 1b, 2026-07-10)。F# リファレンス不要で GPU 単独の
      マルチ命令実行 + レジスタ値検証が可能に:
      * wgpu-runner プログラムモード (`--program prog.json [--dump-regs] [--dump-dir D]`)。
        meta JSON (ピン/レジスタの正規化座標バス) + program JSON (命令列 + 期待値) 駆動で
        回路非依存。ループ: pins 書込 → clk=0 固定点 → clk=1 固定点 → レジスタ読出 → 比較。
        固定点検出は F# settle と同値 (interval 実行 → +1 世代不変チェック)
      * `src/ExportSm83MinInstr.fsx`: Sm83MinModel (Verilog quirk 写像。ADD の H は
        4bit ラップ比較 `((a&15)+(b&15))&15 >= 8` に注意) + 正規化 meta + init.bin +
        期待値つき 20 命令 program JSON を生成
      * `wgpu-runner/sm83-instr-test.sh`: 統合ランナー (成果物なければ自動エクスポート)
      * sm83_min 20 命令 (全 opcode + 全フラグ Z/N/H/C、キャリー連鎖/ボロー) 20/20 PASS。
        2 回実行で世代数まで決定的
      * **クロック配線バグを発見・修正**: balanceClockNet の graceful degradation が
        ripUpEdge でリーフ edge を撤去した後 routeExactLen 失敗時に復元せず、
        DFF b[1] がクロック未接続 → レジスタビットがリセット値に固着していた
        (PipelineWL.fs: 撤去前の occ を退避し失敗時に復元)。従来の golden テストは
        b bit1=1 を通る値を一度もロードしていなかったため検出できなかった
- [x] 大規模回路の配線 (2026-06-15〜08-14)。sm83_subset (3,553 gates) 配線完走:
      * ルーティング順序最適化 (短いネット優先) + A* リトライ時の bbox マージン拡大 (bc358f0)
      * リトライ時の転回ペナルティ低減 4→2→1 (a2b64f5)
      * Rip-up & reroute: 輻輳ネットの経路を実際に塞いだ「ブロッカー」を撤去して
        再配線、失敗時は occ 復元 (679f792, 6cc7704)
      * A* 探索上限 50M → 5M: 経路なしネットを早く諦めて rip-up に回す (dc5c367)
      * 配置ピッチの動的決定 `pitchFor` (≤200: 24x16 / ≤1000: 20x14 / ≤3000: 16x12 /
        >3000: 16x12) と輻輳失敗時の自動拡大 `pitchSequence` 12x10→16x12→20x14→24x16
        (0c3e39b, 0fe4566)。sm83_subset は 16x12 で失敗 → 20x14 で完走
      * クロック優先配線: クロック終端を先に配線・均等化してからデータ配線 (12de255)。
        sm83_subset skew 1068 → 46
      * 検証スクリプト: verify_clock.fsx (skew 実測), pitch_bench.fsx / pitch_route.fsx
- [x] sm83_full に CB prefix (0xCB) デコード追加 (d008cbe)。8,379 → 9,059 gates、yosys 合成成功
- [ ] web/sm83_mc_*.bin (2026-06-13 生成) の再生成 — クロック未接続バグ入り
      回路のもの (F#/GPU 一致テストとしては有効だが回路として b[1] 欠陥あり)
- [ ] GB エミュレータ統合 (バス/割込みブリッジ)

### 開発サイクルへの GPU 統合

`web/run-wl.sh` がコンパイル → .bin エクスポート → GPU シミュレーション → 検証
を一括実行する:

```bash
web/run-wl.sh sm83          # SM83: export → GPU test
web/run-wl.sh --all         # 全回路: export → GPU golden test (16 ケース, ~4min)
web/run-wl.sh --list        # 利用可能テスト一覧
web/run-wl.sh mincpu --headed  # ブラウザ表示あり
```

アーキテクチャ:
- `web/golden-cases.json` — テストケース定義 (init/steps/expected/exportScript)
- `golden-test.ts` — JSON 駆動の Playwright テスト (.bin 不在時は自動スキップ + エクスポートヒント表示)
- `run-wl.sh` — 統合ランナー (依存自動セットアップ付き, npm/playwright install 不要)

---

## 既知の問題

- 大規模回路の配線が遅い: sm83_subset で約 100〜120 分 (20x14)、sm83_full は 5〜8 時間見込み。
  16x12 で輻輳失敗 → 拡大再試行のため、失敗分の時間も上乗せされる
- ピッチ拡大に伴い cross 率が上昇 (12x10 で約 22%、sm83_subset 20x14 で約 31%)
- クロックスキュー: クロック優先配線 (12de255) で大幅改善 (sm83_subset skew 46)。
  sm83_min の旧 WARN (残差 110 gen) は優先配線後に未再計測 → verify_clock.fsx で要確認

### 解消済み

- `verilog/sm83_p0.json` / `sm83_p0.v` の紛失 → 復元済み (2674330)
- 399b9c8 に混入した PipelineWL.fs 高速化 WIP (trySimplePath) → trySimplePath は
  配線資源を食い RoutingCongestion を起こすため削除 (2674330)

---

## WireWorld 系 (凍結 — 組合せ回路デモとして維持)

WireWorld 系テストは構造的制約により修正しない。現在 90 テストが WireWorld 系。
全テスト 166/166 PASS 維持中。
