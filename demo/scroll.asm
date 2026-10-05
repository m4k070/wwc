; demo/scroll.asm — CA デモ映像用の最小 ROM (rgbds)
;
; 目的: CA (sm83_full) の実行と gbfs の PPU 出力を並べて撮るための題材。
; 起動直後に LCD を有効化し、タイルパターンを敷いた BG を描かせる。PPU は走査線ごとに
; フレームバッファを更新するので、画面が上から塗られていく過程がそのまま映像になる。
;
; カートリッジ ROM としてビルドし、コードを 0x0100 の入口から置く。CA のリセット PC は
; 0x0100、gbfs もブート後状態 (PC=0x0100) から始まるので、両者が同じ経路をたどる。
; ブート ROM は使わない (0x0000-0x00FF を上書きしない)。
;
;   nix shell nixpkgs#rgbds -c bash -c \
;     'rgbasm -o /tmp/scroll.o demo/scroll.asm \
;      && rgblink -o demo/scroll.gb /tmp/scroll.o && rgbfix -p 0xFF -v demo/scroll.gb'
;   cp demo/scroll.gb routed/rom_demo_scroll.bin
;   dotnet fsi src/ExportGolden.fsx routed/demo_scroll.json   # 6,000 周期ぶんの golden

SECTION "Entry", ROM0[$0100]
    nop
    jp Start

SECTION "Start", ROM0[$0150]
Start:
    ; --- LCD を切ってから VRAM を触る ---
    xor a
    ld [$FF40], a               ; LCDC = 0 (LCD off)

    ; --- タイルデータ 2 個 (16 バイト) を $8000 に置く ---
    ld hl, $8000
    ld de, TileData
    ld b, 16
.copy_tiles:
    ld a, [de]
    ld [hl+], a
    inc de
    dec b
    jr nz, .copy_tiles

    ; --- タイルマップは触らない ---
    ; メモリモデルの VRAM は 0 初期化なので、タイルマップ $9800 は既にタイル 0 で埋まっている。
    ; 1024 バイトを実際に埋めると約 7,000 周期かかり、その間 LCD を切ったままにできない
    ; (LCD を点けるのは最後) ため、デモの 6,000 周期がまるごと無表示になる。

    ; --- パレットとスクロール ---
    ld a, %11100100
    ld [$FF47], a               ; BGP
    xor a
    ld [$FF42], a               ; SCY
    ld [$FF43], a               ; SCX

    ; --- LCD on + BG on ---
    ld a, %10010001
    ld [$FF40], a               ; LCDC = 0x91

.loop:
    jr .loop

TileData:
    ; タイル 0: 細かいチェッカー
    db %10101010, %01010101, %10101010, %01010101
    db %10101010, %01010101, %10101010, %01010101
    ; タイル 1: 太い縞
    db %11111111, %00000000, %11111111, %00000000
    db %11111111, %00000000, %11111111, %00000000
