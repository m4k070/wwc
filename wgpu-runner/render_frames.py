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


def write_png(path, w, h, rgb):
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
    out += chunk(b"IDAT", zlib.compress(bytes(raw), 6))
    out += chunk(b"IEND", b"")
    with open(path, "wb") as f:
        f.write(out)


def render_frame(bin_path, out_path, scale, crop=None):
    """crop = (x, y, w, h) なら、その領域だけを切り出して描く (bin がすでに crop 済みでも可)。"""
    with open(bin_path, "rb") as f:
        data = f.read()
    gw, gh = struct.unpack("<ii", data[:8])
    cells = data[8:]
    if len(cells) < gw * gh:
        raise SystemExit(f"{bin_path}: {gw}x{gh} に対してセルが {len(cells)} しかない")
    x, y, w, h = crop if crop else (0, 0, gw, gh)
    if x + w > gw or y + h > gh:
        raise SystemExit(f"{bin_path}: crop ({x},{y}) {w}x{h} が {gw}x{gh} の範囲外")
    rows = []
    for row in range(y, y + h):
        line = b"".join(TAB[b] * scale for b in cells[row * gw + x:row * gw + x + w])
        rows.extend([line] * scale)
    write_png(out_path, w * scale, h * scale, b"".join(rows))
    return w * scale, h * scale


def main():
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("frames_dir")
    ap.add_argument("out_dir")
    ap.add_argument("--scale", type=int, default=6, help="1 セルを何ピクセルにするか (既定 6)")
    ap.add_argument("--out-prefix", default="f", help="出力ファイル名の接頭辞 (既定 f)")
    ap.add_argument("--limit", type=int, default=0, help="先頭 N 枚だけ描く (0 = 全部)")
    ap.add_argument("--crop", type=int, nargs=4, metavar=("X", "Y", "W", "H"),
                    help="切り出す領域 (bin が --frame-crop 済みでもさらに絞れる)")
    args = ap.parse_args()

    files = sorted(f for f in os.listdir(args.frames_dir) if f.endswith(".bin"))
    if args.limit:
        files = files[:args.limit]
    if not files:
        raise SystemExit(f"{args.frames_dir} に .bin がない")
    os.makedirs(args.out_dir, exist_ok=True)
    size = None
    for i, f in enumerate(files):
        size = render_frame(os.path.join(args.frames_dir, f),
                            os.path.join(args.out_dir, f"{args.out_prefix}{i:06d}.png"),
                            args.scale, tuple(args.crop) if args.crop else None)
    print(f"{len(files)} 枚を {args.out_dir} に描いた ({size[0]}x{size[1]})")
    print("動画にするには:")
    print(f"  ffmpeg -y -framerate 30 -i {args.out_dir}/{args.out_prefix}%06d.png "
          f"-c:v libx264 -pix_fmt yuv420p -crf 20 out.mp4")


if __name__ == "__main__":
    main()
