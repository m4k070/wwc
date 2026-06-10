# WireWorld/WireLevel コンパイラ TODO

## 現在のテスト結果: 96/99 passed (WireLevel 7/7 🎉)

最終目標: ゲームボーイエミュレータに組込める CPU をセルオートマトンで実現する。

## 2026-06-11 戦略ピボット

WireWorld はバックファイア (RunBackfire.fsx で実証) と 1gen 厳密タイミング制約
により順序回路でスケールしないため、独自 CA ルール **WireLevel** に移行した。
詳細: DESIGN-CA2.md / AGENTS.md。

---

## P0: パイプラインの WireLevel 化

- [ ] techMap2: GateKind → WireLevel セル (LNand/LDff、1 セル + クリアランス)
- [ ] place2: WireLevel 向け配置 (ピッチ縮小可、ゲートは 1 セル)
- [ ] route2: 方向付与 (dir = 進行方向) + Cross セル挿入 + ゲート隣接クリアランス
- [ ] クロックツリー配線 (Pin → 全 DFF 側面、概均衡)
- [ ] emit2: LGrid 合成 + byte グリッドエクスポート (encodeCell)
- [ ] E2E: yosys JSON (半加算器) → WireLevel → settle 検証で真理値表 4/4

## P1: 順序回路 E2E (M6/M7 統合)

- [ ] yosys $_DFF_P_ → LDff 経路の E2E (toggle FF を Verilog から)
- [ ] 4bit カウンタ
- [ ] 8bit レジスタ
- [ ] ALU (加算器)

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
