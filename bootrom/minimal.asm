; minimal.asm — CA (wwc) デモ用の最小ブート ROM (自作。Nintendo のコードは含まない)
;
; 目的: CA (wgpu-runner / sm83_full) 上で「ブート ROM が動く」ことを検証する。
;   1. ヘッダチェックサム (0x0134-0x014C の和 vs 0x014D) を検査
;   2. カートリッジヘッダのロゴ (0x0104, 48 バイト 1bpp) を VRAM $8010 へ 2bpp 展開してコピー
;      (各ビットを 2 ビットに複製 = 実機のブート ROM と同じ展開。48 B → 192 B = 12 タイル)
;   3. タイルマップ $9904 にタイル 1-12 を並べる
;   4. BGP=$FC, SCY=$64 → LCDC=$91 (LCD on | BG on | タイルデータ $8000)
;   5. LY (0xFF44) を読んで RAM $C000 に記録 (LY モデルの検証。期待値は golden 側にある)
;   6. 最後の 4 バイト ($00FC-$00FF) で 0xFF50 に 1 を書いて自分を解除する。
;      解除後は PC が $0100 に進む = 実機のブート ROM と同じ終わり方。
;
; カートリッジ側の配置 (RTL のリセット PC が 0x0100 のため):
;   $0100 `jp $0000`  ← リセット直後の入口。ここからブート ROM (0x0000-0x00FF) へ入る
;   $0000 `jp $0200`  ← ブート ROM 解除後に見えるカートリッジ側の入口
;   $0200 プログラム本体
;   つまり「同じ番地 $0000 が、解除の前後で別のコードを返す」ことを実行で確かめる形になる。
;   RTL のリセット PC を 0x0000 に直せば $0100 の trampoline は不要 (TODO 参照)。
;
; 設計方針 (CA の速度に合わせる): CA は 1 M サイクル ≈ 16.5 ms なので VBlank 待ち
; (1 フレーム = 17,556 サイクル ≈ 5 分) は入れない。実機ブート ROM の待ち構造は段階 1
; (Bootix の prefix) で扱う。ここは「短いが構造は本物」を狙う。
;
; ビルド:
;   nix shell nixpkgs#rgbds -c 'rgbasm -o /tmp/bootrom_minimal.o bootrom/minimal.asm \
;       && rgblink -x -o routed/bootrom_minimal.bin /tmp/bootrom_minimal.o'

DEF rBGP  EQU $FF47
DEF rSCY  EQU $FF42
DEF rLCDC EQU $FF40
DEF rLY   EQU $FF44
DEF rBOOT EQU $FF50

DEF LOGO_SRC   EQU $0104   ; ヘッダのロゴ (48 バイト、1bpp)
DEF VRAM_LOGO  EQU $8010   ; 展開先 (タイル 1)。$8010 + 12*16 = $80D0 まで
DEF TILEMAP    EQU $9904   ; タイルマップ row 8 col 4 = 画面中央 (実機ブート ROM と同じ位置)
DEF RAM_LY     EQU $C000   ; LY の記録先
DEF ROW_COUNT  EQU $C010   ; 残り行数 (8)
DEF TILE_COUNT EQU $C011   ; 1 行の残りバイト数 (6)

SECTION "Boot", ROM0[$0000]
Start:
    ld sp, $FFFE

    ; --- 1. ヘッダチェックサム ---
    ld hl, $0134
    ld c, $19            ; 25 バイト (0x0134-0x014C)
    xor a
.Checksum:
    add a, [hl]
    inc hl
    dec c
    jr nz, .Checksum
    cp [hl]              ; [hl] = 0x014D のチェックサム
    jr nz, .BadChecksum

    ; --- 2. ロゴを VRAM へ展開コピー ---
    ; 元データは 48 バイト (6 バイト = 1 行、48x8 ピクセル)。ニブル (4 ピクセル) を
    ; 8 ピクセルに拡大するとちょうどタイル 1 枚の 1 行になるので、48 x 2 = 96 行 =
    ; 12 タイル x 8 行 → VRAM $8010-$80CF に収まる。行内は +16 ずつ進め、
    ; 行末で -190 して次の行のタイル 1 に戻る。
    ld h, $00            ; 展開テーブル ($00E0) を引くための上位バイト
    ld bc, LOGO_SRC
    ld de, VRAM_LOGO
    ld a, $08
    ld [ROW_COUNT], a
.RowLoop:
    ld a, $06            ; 1 行 = 6 バイト (1 バイトで 2 タイル分)
    ld [TILE_COUNT], a
.TileLoop:
    ld a, [bc]
    swap a
    and $0F
    add a, $E0
    ld l, a
    ld a, [hl]           ; 上位ニブル → タイル n の行 (ロープレーン)
    ld [de], a
    inc de
    ld [de], a           ; 1bpp なのでハイプレーンも同じ値
    inc de
    ld a, $0E            ; +14 → 次のタイルの同じ行
    add a, e
    ld e, a
    ld a, [bc]
    and $0F
    add a, $E0
    ld l, a
    ld a, [hl]           ; 下位ニブル → タイル n+1 の行
    ld [de], a
    inc de
    ld [de], a
    inc de
    ld a, $0E
    add a, e
    ld e, a
    inc bc
    ld a, [TILE_COUNT]
    dec a
    ld [TILE_COUNT], a
    jr nz, .TileLoop
    ld a, e
    sub $BE              ; -190 → 次の行のタイル 1 ($80D0 → $8012)
    ld e, a
    ld a, [ROW_COUNT]
    dec a
    ld [ROW_COUNT], a
    jr nz, .RowLoop

    ; --- 3. タイルマップ (画面中央の行にタイル 1-12) ---
    ld hl, TILEMAP
    ld b, $01
    ld c, $0C
.MapLoop:
    ld [hl], b
    inc hl
    inc b
    dec c
    jr nz, .MapLoop

    ; --- 4. BGP / SCY / LCDC ---
    ld a, $FC
    ldh [rBGP], a
    ld a, $64
    ldh [rSCY], a
    ld a, $91            ; LCD on | BG on | タイルデータ $8000
    ldh [rLCDC], a

    ; --- 5. LY を記録 ---
    ldh a, [rLY]
    ld [RAM_LY], a

    ; --- 6. 解除ルーチン ($00FC) へ ---
    jp BootExit

.BadChecksum:
    ld a, $FF
    ld [RAM_LY], a
    jr .BadChecksum      ; ここで止まる (expectMem の不一致として見える)

; --- 展開テーブル: ニブル (0-15) → 各ビットを 2 ビットに複製したバイト ---
SECTION "NibbleTable", ROM0[$00E0]
ExpandedNibble:
    db $00, $03, $0C, $0F, $30, $33, $3C, $3F
    db $C0, $C3, $CC, $CF, $F0, $F3, $FC, $FF

SECTION "BootPad", ROM0[$00F0]
    ds $0C, 0

; --- 解除 (実機と同じくブート ROM の最後の 4 バイト)。解除後は PC が $0100 に進む ---
SECTION "BootExit", ROM0[$00FC]
BootExit:
    ld a, $01
    ldh [rBOOT], a
