# Bootix v1.2 (DMG boot ROM)

`bootix_dmg.asm`（ソース）と `routed/bootix_dmg.bin`（バイナリ）は **Bootix v1.2** — 自作の
Game Boy ブート ROM。CA (GPU) の段階 1 検証に使う。

- 配布元: https://github.com/Ashiepaws/Bootix
- バイナリ: https://github.com/Ashiepaws/Bootix/releases/download/v1.2/bootix_dmg.bin
- sha256: `c313435280dda8ccfae0786c7159aab791a00930605101c647794f64bfa17b5e` (256 バイト)
- ライセンス: **CC0-1.0**（パブリックドメイン相当、再配布可）。Nintendo の純正コード・ロゴは含まない

## 自作 minimal.asm との違い

`bootrom/minimal.asm` は「CA がブート ROM を走らせてロゴを出せるか」だけを見る最小構成だが、
Bootix は実機ブート ROM の構造を持つ:

SP 初期化 → VRAM クリア（$9FFF から下降、8 KB）→ オーディオ初期化 → ロゴ展開（$0104 → $8010）
→ タイルマップ（2 行）→ **LCDC 初期化** → アニメーション（SCX/BGP フェード）→ 効果音 → 解除

特に効くのは次の 2 点:

- **HRAM のスタック**（SP=$FFFE）で `call`/`ret` を多用する → メモリモデルの HRAM 書込が要る
- **LY ポーリング待ち**（`DoTimeout`: `ldh a,[rLY]` / `cp 144` を C 回）→ 実機のフレーム待ち構造

## 使い方

```bash
dotnet fsi src/ExportBootRom.fsx --bootrom routed/bootix_dmg.bin --name bootrom_bootix --cycles 2000
dotnet fsi src/ExportGolden.fsx routed/bootrom_bootix.json
nix develop -c ./wgpu-runner/target/release/wgpu-runner --memory routed/bootrom_bootix.json
```

- バイナリは `routed/` に置く（program JSON のパスは `routed/` 基準で解決されるため）
- 2,000 周期の prefix は VRAM クリアの途中まで（LCDC 書込は約 62K サイクル目）
- 外部 ROM では `expectMem` を出さない（VRAM への展開手順が minimal.asm と違うため）。
  照合は golden（全周期）で行う

## 所要時間の見積り（CA、14x12 / 2 相 / K=3）

- **LCDC 初期化まで ≈ 62K サイクル = 約 48 分**（段階 1 の到達点）
- アニメーションの `DoTimeout` は 1 呼び出しで 1 フレーム（65,664 サイクル）待つ。
  フル実行は 1,329 フレーム ≈ 23.3M サイクル = 約 107 時間なので、段階 1 は LCDC 初期化までで切る
- 2000 周期の golden 照合は GPU で 1 分 34 秒（46 ms/サイクル。メモリ読み出し経路が長いため）
