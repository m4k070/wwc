# wwc — HDL → WireWorld Compiler

任意の HDL（Verilog 等）で記述した論理回路を、セルオートマトン **WireWorld** 上で動作するパターンへコンパイルする実験的プロジェクト。F# 製。

> ⚠️ **ステータス: 設計スケッチ段階。** 型・パイプライン骨格・WireWorld シミュレータ核は実装済み。`frontend`（HDL 取り込み）と `route`（配線）はスタブ。

---

## これは何か

WireWorld は 4 状態（Empty / Electron Head / Electron Tail / Conductor）のセルオートマトンで、導線・論理ゲート・メモリを構築でき、チューリング完全。本プロジェクトは「HDL で書いた回路を WireWorld グリッドへ自動変換する」コンパイラを目指す。

通常の論理合成と決定的に違うのは、**WireWorld では「配線長 = 信号遅延」** という点。ゲートの全入力が同じ世代（tick）に到達しないと誤動作する。この制約をコンパイラがどう扱うかが本質的な課題になる。

## アーキテクチャ

```
HDL source
   │  frontend        (Verilog/Yosys JSON → Netlist)        ※未実装
   ▼
Netlist (テクノロジ非依存)
   │  techMap         (Gate → WireWorld StdCell)            ✅
   ▼
(Gate × StdCell) list
   │  place           (グリッドへ配置)                       ✅ 素朴版
   ▼
Placement
   │  route           (Lee 法で配線 + 交差処理)              ※未実装
   ▼
Wire list
   │  balanceGateInputs (タイミング均等化 ★核心)             ✅ ロジック
   │  emit            (配置 + 配線を 1 枚の Grid に合成)      ✅
   ▼
WireWorld Grid → Golly RLE                                  ✅
```

### 設計上の主要判断

- **段ごとに別の型を返す** — 各コンパイル段の中間表現を別の型にし、段の取り違えをコンパイル時に弾く。
- **タイミングを型に載せる** — `[<Measure>] type gen`（世代）を導入し、`StdCell.Latency: int<gen>` と `Wire.Delay: int<gen>` を同じ次元で扱う。タイミング不整合を型と関数で検出できる。
- **railway-oriented pipeline** — 各段が `'a -> Result<'b, CompileError>` を返し `>>=`（bind）で連結。どこで落ちても `CompileError` で伝播する。
- **疎なグリッド** — `Map<Coord, CellState>`。回路は広大かつ疎なので密配列を避ける。Empty は「キー不在」として表現。

## ビルド

```bash
dotnet build src/WwHdl.fsproj
```

`.fsproj` がまだ無い場合:

```bash
cat > src/WwHdl.fsproj <<'EOF'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Library</OutputType>
    <TargetFramework>net8.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="WwHdl.fs" />
  </ItemGroup>
</Project>
EOF
```

### NixOS

`flake.nix` を置く場合の最小例:

```nix
{
  inputs.nixpkgs.url = "github:NixOS/nixpkgs/nixos-unstable";
  outputs = { self, nixpkgs }:
    let pkgs = nixpkgs.legacyPackages.x86_64-linux;
    in {
      devShells.x86_64-linux.default = pkgs.mkShell {
        packages = [ pkgs.dotnet-sdk_8 pkgs.golly ];
      };
    };
}
```

`golly` は生成した RLE の目視検証用。

## 動かし方（現状できること）

`frontend` / `route` が未実装なため end-to-end のコンパイルはまだ通らないが、**シミュレータ核とセルライブラリの検証ループは動く**：

```fsharp
open WwHdl.Library
open WwHdl.Rule
open WwHdl.Pipeline

// ASCII でセルパターンを書く → 数世代回す → RLE 出力 → Golly で確認
let g = ofAscii [ "Ht####" ]   // 電子を注入した導線片
let g' = run 3 g
printfn "%s" (toRle g')
```

この「ASCII → `run` → `toRle` → Golly」の小さなループで各 `StdCell` の動作と `Latency`（入力→出力の世代差）を確定させていくのが、ライブラリ整備の基本作業。

## ロードマップ

詳細設計は [DESIGN.md](DESIGN.md) を参照。

### M1 — セルライブラリ完成

- [ ] NOT / AND / OR / XOR / DFF を `Rule.run` で単体テストし `Latency` を実測して登録
- [ ] Splitter (Y 字分岐) パターンの設計と検証
- [ ] `makeDelay (n: int<gen>)` — 直線（n ≤ 16）/ 蛇行（n > 16）の自動生成
  - 参考: suzuki-navi/domino の `sofaA` 遅延素子
- [ ] Crossover StdCell — タイミング分離型 (7×7 目標) を先行実装。面積超過なら Wireworld++ 型に切替
  - 参考: suzuki-navi/domino の `cross` ノード設計

### M2 — フロントエンド

- [ ] Yosys `synth -flatten; abc -g AND,NOT; write_json` の出力スキーマを確定
- [ ] `parseYosysJson : string -> Result<YosysModule, CompileError>`
- [ ] `yosysToNetlist : YosysModule -> Result<Netlist, CompileError>`
- [ ] AND-NOT 2 ゲート回路でパース結果を手検証

### M3 — ルーティング

- [ ] `buildGrid : Placement -> RoutingGrid` — セルの bounding box を Blocked に
- [ ] `leePath : RoutingGrid -> Coord -> Coord -> Coord list option` — BFS 最短経路
- [ ] `routeAll` — 全ネット配線（扇出優先で順序制御）
- [ ] `findConflicts` + `insertCrossovers` — 衝突点に Crossover セルを自動挿入

### M4 — タイミング均等化

- [ ] `computeArrival` — トポロジカル順で `ArrivalMap` を計算
- [ ] `computeSlack` + `insertDelays` — DELAY_n セルを挿入してスラックを 0 に揃える
- [ ] 生成 Grid を `Rule.run` で実行し正しい論理値を確認

### M5 — E2E 検証

- [ ] カウンタ (4 bit)
- [ ] レジスタ (8 bit)
- [ ] ALU (加算器)
- [ ] 乗算器

各回路: HDL 記述 → `compile` → `toRle` → Golly 目視 + `Rule.run` 回帰テスト。
難易度順は suzuki-navi/domino の実装例に倣う。

## ライセンス

MIT

## 参考

- Conway's Game of Life / WireWorld のチューリング完全性
- QFT（Quest For Tetris）プロジェクト — CA 上の汎用計算機構築
- Golly — セルオートマトンシミュレータ
- [suzuki-navi/domino](https://github.com/suzuki-navi/domino) — 独自 CA による論理回路ビジュアルシミュレータ。crossover セル・遅延素子・ALU まで実装済み。交差処理と遅延挿入の設計参考。
