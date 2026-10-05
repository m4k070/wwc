#!/usr/bin/env python3
"""wgpu-runner のフレーム (.bin) を PNG に描く。

    python3 wgpu-runner/render_frames.py <frames_dir> <out_dir> [--scale N] [--out-prefix P]

入力は `wgpu-runner --memory prog.json --frames DIR [--frame-crop X Y W H]` が書く
`f%06d.bin` (形式は grid.bin と同じ [width:i32][height:i32][cell...])。

セルのレベルビットは web/index.html の presentedTo と同じ規則で読む:
  Pin / Wire / Nand : bit 0
  Dff               : bit 0 (q)
  Cross             : bit 0 = 水平、bit 1 = 垂直 (描画ではどちらか立っていれば ON)

配色は web 版と違い **OFF を暗く / ON を明るく** 大きく振る (動画では web の配色だと
ON/OFF の差が潰れて動きが見えない)。
"""
import argparse
import os
import struct
import sys
import zlib

# 0 Empty / 1 Pin / 2 Wire / 3 Nand / 4 Cross / 5 Dff
OFF = [(0x10, 0x10, 0x18), (0x6b, 0x5a, 0x10), (0x10, 0x3d, 0x4d),
       (0x5a, 0x1e, 0x1e), (0x1a, 0x3d, 0x1e), (0x45, 0x1c, 0x4f)]
ON = [(0x10, 0x10, 0x18), (0xff, 0xf9, 0xc4), (0x7f, 0xf0, 0xff),
      (0xff, 0x80, 0x80), (0x7f, 0xe8, 0x90), (0xe5, 0x99, 0xf7)]


def build_table():
    """256 エントリの色表 (セル 1 byte -> RGB 3 byte)。"""
    tab = []
    for b in range(256):
        kind = (b >> 5) & 7
        if kind > 5:
            kind = 0
        if kind == 4:  # Cross: 水平 (bit0) か垂直 (bit1) のどちらかが立てば ON
            lvl = (b & 1) | ((b >> 1) & 1)
        else:          # Pin / Wire / Nand / Dff は bit0
            lvl = b & 1
        tab.append(bytes((ON if lvl else OFF)[kind]))
    return tab


TAB = build_table()


def write_png(path, w, h, rgb, level=6):
    raw = bytearray()
    stride = w * 3
    for y in range(h):
        raw.append(0)  # filter type 0
        raw += rgb[y * stride:(y + 1) * stride]

    def chunk(tag, data):
        return (struct.pack(">I", len(data)) + tag + data
                + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF))

    out = b"\x89PNG\r\n\x1a\n"
    out += chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, 8, 2, 0, 0, 0))
    out += chunk(b"IDAT", zlib.compress(bytes(raw), level))
    out += chunk(b"IEND", b"")
    with open(path, "wb") as f:
        f.write(out)


# DMG の 4 階調 (0 = 最も明るい)
PPU_SHADES = [(0x9B, 0xBC, 0x0F), (0x8B, 0xAC, 0x0F), (0x30, 0x62, 0x30), (0x0F, 0x38, 0x0F)]
PPU_W, PPU_H = 160, 144
GAP = 8     # CA と PPU の間の黒帯
BORDER = 4  # 全体の余白 (PPU の枠もこの太さ)
FRAME_RGB = (0x50, 0x58, 0x60)


def load_index(path):
    """tsv (番号<TAB>バス周期) を読んで [(番号, 周期)] を返す。"""
    out = []
    with open(path) as f:
        for line in f:
            parts = line.split()
            if len(parts) == 2:
                out.append((int(parts[0]), int(parts[1])))
    if not out:
        raise SystemExit(f"{path} が空")
    return out


def ppu_rows(raw_path, scale):
    """ExportPpuFrames.fsx が書いた 160x144 の階調 (0-3) を、scale 倍した RGB 行にする。"""
    with open(raw_path, "rb") as f:
        fb = f.read()
    if len(fb) < PPU_W * PPU_H:
        raise SystemExit(f"{raw_path}: 160x144 に対して {len(fb)} バイトしかない")
    shades = [bytes(PPU_SHADES[min(i, 3)]) * scale for i in range(4)]
    rows = []
    for y in range(PPU_H):
        line = b"".join([shades[min(v, 3)] for v in fb[y * PPU_W:(y + 1) * PPU_W]])
        rows.extend([line] * scale)
    return rows


_EXP_CACHE = {}


def _expand_table(scale):
    """セル値 -> 横に scale 倍した 3 バイト。TAB[b] * scale を 1 セルずつ作るのを避ける。

    合成フレームは 1 枚 0.3 秒かかり、律速はここ (1776x728 で 160x120 セルを 120 行)。
    """
    if scale not in _EXP_CACHE:
        _EXP_CACHE[scale] = [TAB[b] * scale for b in range(256)]
    return _EXP_CACHE[scale]


def render_frame(bin_path, out_path, scale, crop=None, side=None):
    """crop = (x, y, w, h) なら、その領域だけを切り出して描く (bin がすでに crop 済みでも可)。

    side = (rows, w_px, h_px) を渡すと、右側にその画像を縦中央で並べる (PPU の絵など)。
    """
    with open(bin_path, "rb") as f:
        data = f.read()
    gw, gh = struct.unpack("<ii", data[:8])
    cells = data[8:]
    if len(cells) < gw * gh:
        raise SystemExit(f"{bin_path}: {gw}x{gh} に対してセルが {len(cells)} しかない")
    x, y, w, h = crop if crop else (0, 0, gw, gh)
    if x + w > gw or y + h > gh:
        raise SystemExit(f"{bin_path}: crop ({x},{y}) {w}x{h} が {gw}x{gh} の範囲外")
    exp = _expand_table(scale)
    rows = []
    for row in range(y, y + h):
        line = b"".join([exp[b] for b in cells[row * gw + x:row * gw + x + w]])
        rows.extend([line] * scale)

    cw, ch = w * scale, h * scale
    if side is None:
        write_png(out_path, cw, ch, b"".join(rows))
        return cw, ch

    srows, sw, sh = side
    total_w = cw + GAP + sw + 2 * BORDER
    total_h = max(ch, sh) + 2 * BORDER
    canvas = bytearray(total_w * total_h * 3)  # 合成フレームは毎回作り直す (行数が多く使い回しの利得が薄い)
    for i, line in enumerate(rows):
        o = ((i + BORDER) * total_w + BORDER) * 3
        canvas[o:o + cw * 3] = line
    # PPU は縦中央に置き、DMG の筐体色で 1 周ぶん枠を描く
    y0 = BORDER + (max(ch, sh) - sh) // 2
    x0 = BORDER + cw + GAP
    for i in range(sh + 2):
        o = ((y0 - 1 + i) * total_w + x0 - 1) * 3
        for x in range(sw + 2):
            canvas[o + x * 3:o + x * 3 + 3] = bytes(FRAME_RGB)
    for i, line in enumerate(srows):
        o = ((y0 + i) * total_w + x0) * 3
        canvas[o:o + sw * 3] = line
    write_png(out_path, total_w, total_h, bytes(canvas), level=1)
    return total_w, total_h


def main():
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("frames_dir")
    ap.add_argument("out_dir")
    ap.add_argument("--scale", type=int, default=6, help="1 セルを何ピクセルにするか (既定 6)")
    ap.add_argument("--out-prefix", default="f", help="出力ファイル名の接頭辞 (既定 f)")
    ap.add_argument("--limit", type=int, default=0, help="先頭 N 枚だけ描く (0 = 全部)")
    ap.add_argument("--step", type=int, default=1, help="N 枚に 1 枚だけ描く (既定 1 = 全部)")
    ap.add_argument("--crop", type=int, nargs=4, metavar=("X", "Y", "W", "H"),
                    help="切り出す領域 (bin が --frame-crop 済みでもさらに絞れる)")
    ap.add_argument("--side-dir", help="右側に並べる画像のディレクトリ (ExportPpuFrames.fsx の出力)")
    ap.add_argument("--side-scale", type=int, default=0,
                    help="右側の画像の倍率 (0 = CA 側の高さに合わせる)")
    args = ap.parse_args()

    files = sorted(f for f in os.listdir(args.frames_dir) if f.endswith(".bin"))
    if args.limit:
        files = files[:args.limit]
    if not files:
        raise SystemExit(f"{args.frames_dir} に .bin がない")
    os.makedirs(args.out_dir, exist_ok=True)

    ca_index = None
    ppu_index = None
    if args.side_dir:
        ca_index = load_index(os.path.join(args.frames_dir, "frames.tsv"))
        ppu_index = load_index(os.path.join(args.side_dir, "ppu.tsv"))
        # 書き出し中に読むと .bin が index より 1 枚先行することがある。index のある範囲だけ描く
        files = files[:len(ca_index)]

    size = None
    out_i = 0
    side_cache = (None, None)  # (index, rows)
    j = 0
    # --side-scale を省略したときは CA 側の高さに合わせる。高さは --crop か、先頭フレームのヘッダから取る
    if args.side_dir and not args.side_scale:
        if args.crop:
            ca_h = args.crop[3]
        else:
            with open(os.path.join(args.frames_dir, files[0]), "rb") as f:
                ca_h = struct.unpack("<ii", f.read(8))[1]
        side_scale = max(1, round(args.scale * ca_h / PPU_H))
    else:
        side_scale = args.side_scale
    for i, f in enumerate(files):
        if i % args.step:
            continue
        side = None
        if args.side_dir:
            cycle = ca_index[i][1]
            while j + 1 < len(ppu_index) and ppu_index[j + 1][1] <= cycle:
                j += 1
            if side_cache[0] != j:
                rows = ppu_rows(os.path.join(args.side_dir, f"ppu{ppu_index[j][0]:06d}.raw"), side_scale)
                side_cache = (j, (rows, PPU_W * side_scale, PPU_H * side_scale))
            side = side_cache[1]
        size = render_frame(os.path.join(args.frames_dir, f),
                            os.path.join(args.out_dir, f"{args.out_prefix}{out_i:06d}.png"),
                            args.scale, tuple(args.crop) if args.crop else None, side)
        out_i += 1
    print(f"{out_i} 枚を {args.out_dir} に描いた ({size[0]}x{size[1]})")
    print("動画にするには:")
    print(f"  ffmpeg -y -framerate 30 -i {args.out_dir}/{args.out_prefix}%06d.png "
          f"-c:v libx264 -pix_fmt yuv420p -crf 20 out.mp4")


if __name__ == "__main__":
    main()
