# WireLevel コンパイラ TODO

## 現在のテスト結果: 150/150 passed 🎉 (GPU golden test 16/16 PASS)
- F#: WireLevel 8, WL-Pipeline 6, WL-CNT 3, WL-REG8 3, WL-GOLDEN 5, WL-ALU 14, WL-ALU4 13, WL-SKEW 2, WL-MINCPU 3, WL-SM83 3 — 他 WireWorld 系 90
- GPU: toggleFF, halfAdder, mincpu(2), sm83(2), sm83p0-cyc0(2), sm83p0-mc(8)

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

## WireWorld 系 (凍結 — 組合せ回路デモとして維持)

WireWorld 系テストは構造的制約により修正しない。現在 90 テストが WireWorld 系。
全テスト 150/150 PASS 維持中。
