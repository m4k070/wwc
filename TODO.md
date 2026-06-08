# WireWorld コンパイラ TODO

## 現在のテスト結果: 80/81 passed 🎉

---

## P0: 回帰バグ修正 ✅ 完了

### 2次元配置導入で発生した失敗

- [x] M3 RoutingTest: `wire paths are non-empty and start/end at expected coords`
  - 修正: テスト期待値を `{X=21;Y=11}` に更新（vGap=8, hGap=16）

- [x] Multi-stage E2E: `2-NOT: wire (net3) delay = measureDelay`
  - 修正: テスト期待値を `25<gen>` に更新

---

## P1: 半加算器 sum(1,0) の問題 🔴 残り1件

### 失敗しているテスト

- [ ] `sum(1,0) = 1` → 実際には 0 を返す

### 成功しているテスト

- [x] `sum(0,0) = 0` ✅
- [x] `sum(0,1) = 1` ✅
- [x] `sum(1,1) = 0` ✅

### 問題の詳細

u4 (最終NANDゲート) への信号到着タイミングがずれている:
- net6 (u2 の出力): junction@115gen (1gen 遅い)
- net7 (u3 の出力): junction@114gen
- Clock: 114gen

### 試行した対策 (すべて完了)

1. **配置間隔拡大** (vGap=8, hGap=16) → 効果なし
2. **セルバリアント適用** (junc3_Ab3, junc3_Ab5, junc3_Ab7) → 悪化
3. **複数回の applyVariants** (3回繰り返し) → 収束せず
4. **applyVariants の廃止** → 80/81 に改善 ✅

### 根本原因の推測

- u2 = NAND(a, net4)、u3 = NAND(b, net4)
- a=1, b=0 の場合、u2 は両方の入力が 1、u3 は片方のみが 1
- この非対称性がタイミングのずれを生んでいる可能性
- 配置・ルーティングが非対称なため、net6 と net7 の遅延が異なる

### 今後の対策候補

1. **配置の対称化**
   - u2 と u3 の配置を対称にする
   - 効果は未知数

2. **ワイヤ長の調整**
   - net6 のワイヤを短くする、または net7 のワイヤを長くする
   - insertDelays が正確に動作すれば解決する可能性

3. **STA の改善**
   - insertDelays が正確にスラック分の遅延を追加できるようにする
   - 現在、net6 に25genの遅延が追加されている（期待値は24gen）

---

## P2: 大規模回路検証 (M6)

半加算器が動作したら着手。

- [ ] カウンタ (4bit)
- [ ] レジスタ (8bit)
- [ ] ALU (加算器)
- [ ] 乗算器

---

## P3: DFF / シーケンシャル回路 (M7)

- [ ] DFF パターンの実装 (Rule.run で検証)
- [ ] 循環ネットのルーティング
- [ ] クロック配線のタイミング調整

---

## P4: セルバリアントの拡充

- [x] `junc3_Ab3` (スラック 3gen 用)
- [x] `junc3_Ab5` (スラック 5gen 用)
- [x] `junc3_Ab7` (スラック 7gen 用)
- [ ] 実際の回路での動作検証（現在は使用していない）

---

## 完了したタスク

- [x] `applyVariants` を `compile`/`compileFull` パイプラインに接続（後に廃止）
- [x] `StdCell` に `PortDelays` フィールドを追加
- [x] STA をポート遅延対応に修正 (`computeArrival`, `computeSlack`, `clockTimeOf`)
- [x] セル回転・反転関数の実装 (`flipH`, `flipV`, `rotate180`)
- [x] 2次元配置アルゴリズムの実装 (Y=0, Y=11 の交互配置、vGap=8, hGap=16)
- [x] KNOWLEDGE.md に最近の試行結果を追記
- [x] 回帰バグ修正 (M3 RoutingTest, Multi-stage E2E)
- [x] junc3_Ab5, junc3_Ab7 の実装
- [x] applyVariants の廃止（単純なパイプラインに戻す）

---

## 推奨する次のアクション

1. **P1 の sum(1,0) 問題に対処**
   - 配置の対称化を試す
   - または、STA の改善（insertDelays の精度向上）
2. 半加算器が完全に動作したら P2 (大規模回路) に着手
