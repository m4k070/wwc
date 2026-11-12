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

## 描画確認（ロゴの見え方）

CA に PPU は無いので、最終メモリを gbfs の PPU に食わせて描く（`src/RenderBootRom.fsx`）:

```bash
dotnet fsi src/RenderBootRom.fsx --program routed/bootix_full_lcdc.json --scy 0 --scale 4 --out frame.png
```

- **75,119 周期（LCDC 初期化直後）は画面が真っ白**。`BGP=$00` のため全ピクセルが階調 0 になる。
  これはバグではなく、実機のブート ROM が LCD を BGP=0 で有効化し、その後のアニメーションで
  フェードインさせるため（＝段階 2 の「起動演出」が担当する部分）
- **174,501 周期**で BGP が `0x54` になり（それまでは `$00` のまま = 真っ白）、ロゴと商標記号が
  画面中央（BG y=64..95）に出る。BGP が 0 以外になる最初のサイクルを golden から拾った値
  ロゴは $8010-$818F の 384 バイト（1 ニブルで 4 バイト進む。minimal.asm の 192 バイトとは配置が違う）
  = タイル 1..24。タイルマップは $9904 にタイル 1..12、$9924 にタイル 13..24（`LogoMapInit` が B を
  進めるため）、商標記号は $8190（tile $19）を $9910 に置く

RTL (Release DLL) の実行速度は約 7,700 サイクル/秒（110,000 周期で 14 秒、250,000 周期で約 50 秒）。

## 所要時間の見積り（CA、14x12 / 2 相 / K=3）

- **LCDC 初期化は cycle 75,119（実測、2026-10-09）**（段階 1 の到達点）。内訳: VRAM クリア
  8 サイクル/バイト × 8,192 = 65,536（golden の VRAM 書込 8,417 件が 5,000 周期あたり 625 件で
  均一 = 1 バイト 8 サイクル）、ロゴ展開とタイルマップで残り約 9,600
- アニメーションの `DoTimeout` は 1 呼び出しで「LY が 144 になるまで」待つので 144 × 114 =
  **16,416 サイクル**。フル実行は 1,329 フレーム ≈ 21.8M サイクル
  （**この見積りは誤り**。2026-11-12 の RTL 実測ではブート ROM 解除まで 1,824,740 サイクル ≈ 104 フレーム。
  CA でも `--engine graph` で 1,900,000 周期を全周期照合済み、4 分 27 秒。TODO.md 残課題 1 (f)）。下の実測レート
  （110,000 周期 = 84 分）で換算すると **約 278 時間**なので、段階 1 は LCDC 初期化までで切る
- 2026-10-09 以前の値（cycle 102,969 / 11.00 サイクル/バイト / 65,664 サイクル / 107 時間）は
  **LY (0xFF44) が実機の 4 倍遅く進んでいたバグを含む**実測と見積り。LY を待つ区間だけが
  4 倍に伸びていた（`set_lcd_y` が T サイクルの定数 456 で割っていた。現在は 114）
- 2000 周期の golden 照合は GPU で 1 分 34 秒（46 ms/サイクル。メモリ読み出し経路が長いため）。
  110,000 周期の全周期照合は約 84 分で **110,000/110,000 一致**（2026-09-30 実測、exit 0）
