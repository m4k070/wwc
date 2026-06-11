# WireWorld/WireLevel コンパイラ TODO

## 現在のテスト結果: 96/99 passed (WireLevel 7/7 🎉)

最終目標: ゲームボーイエミュレータに組込める CPU をセルオートマトンで実現する。

## 2026-06-11 戦略ピボット

WireWorld はバックファイア (RunBackfire.fsx で実証) と 1gen 厳密タイミング制約
により順序回路でスケールしないため、独自 CA ルール **WireLevel** に移行した。
詳細: DESIGN-CA2.md / AGENTS.md。

---

## P0: パイプラインの WireLevel 化 ✅ 完了 (2026-06-11, PipelineWL.fs)

- [x] compileWL: yosys JSON → LGrid (techMap は 1 セルゲートなので compileWL 内で完結)
- [x] placeWL: 正方格子配置 (pitchX=16, pitchY=12) + 左端ピン列
- [x] routeWL: (Coord,Dir) 状態 A*、Cross 化直交通過、ゲート隣接クリアランス、
      ファンアウトタップ (タップ元は非交差化)
- [x] クロック配線 (通常ネットとして DFF S 側面へ。均等化は未実装 → P1 残課題)
- [x] emitWL: LGrid 合成 (byte 一括エクスポートは P2 で)
- [x] E2E: 半加算器真理値表 4/4

## P1: 順序回路 E2E

- [x] yosys $_DFF_P_ → LDff 経路の E2E (toggle FF、q=1,0,1,0)
- [x] 4bit カウンタ (verilog/counter4.v → yosys → 21 ゲート → 0..15 ラップ確認)
- [ ] 8bit レジスタ
- [ ] ALU (加算器)
- [ ] クロックスキュー均等化 (hold 対策)。counter4 では顕在化していないが、
      回路規模が大きくなると最短データパス < スキューで壊れうる。
      WireLevel は遅延 = パス長そのものなので、クロック枝の長さを揃えるだけでよい

### 学んだ設計則

- **半周期 > 組合せ収束時間** (setup 制約)。counter4 は halfP=128 で誤動作、
  512 で完動。テストは固定周期でなく `settle` (収束待ち) でクロックを駆動する。

## P2: GPU 実行 (WebGPU)

- [ ] web/: WGSL compute カーネル (DESIGN-CA2.md §4.3) + ping-pong バッファ
- [ ] F# → grid.bin エクスポート / JS ローダー
- [ ] ゴールデンテスト: F# WireLevel.step と GPU の N 世代一致
- [ ] 可視化 (同一バッファのカラーマップ描画)

## P3: CPU へ

- [ ] SM83 (LR35902) サブセットの Verilog 入手/記述 → yosys 合成
- [ ] WireLevel CPU のマイクロベンチ (NOP ループ等)
- [ ] GB エミュレータ統合 (バス/割込みブリッジ)

---

## WireWorld 系 (凍結 — 組合せ回路デモとして維持)

既知の失敗 3 件は WireWorld の構造的制約によるもので、修正予定なし:

| Test | Cause |
|------|-------|
| `2-NOT: wire (net3) delay = measureDelay` | シミュレーション実効遅延とSTA遅延の不一致 |
| `sum(1,0) = 1` (HalfAdder) | パルス方式の 1gen 厳密タイミング制約 |
| `fa-like-9: compileFull succeeds` | 4列×狭ピッチ配置の配線チャネル不足 |
