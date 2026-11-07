#!/usr/bin/env python3
"""Draws src/Ledly/Ledly.ico: a monitor with a ring of rainbow LEDs (like the Mac menu bar icon).

Only the standard library: shapes are rasterized with 4x4 supersampling and stored as PNG frames
inside the .ico. Run it again after changing the drawing:  python3 tools/make-icon.py
"""
import colorsys
import math
import os
import struct
import zlib

SIZES = [16, 20, 24, 32, 40, 48, 64, 128, 256]
SS = 4  # supersampling per axis


def rounded_rect_inside(x, y, x0, y0, x1, y1, r):
    if x < x0 or x > x1 or y < y0 or y > y1:
        return False
    cx = min(max(x, x0 + r), x1 - r)
    cy = min(max(y, y0 + r), y1 - r)
    return (x - cx) ** 2 + (y - cy) ** 2 <= r * r


def layout(size):
    """Geometry in a 0..1 square, tuned per size so small icons stay readable."""
    small = size <= 24
    medium = size <= 48
    g = {}
    # Screen rectangle and its outline.
    g["screen"] = (0.20, 0.27, 0.80, 0.73)
    g["radius"] = 0.06
    g["stroke"] = 0.085 if small else (0.06 if medium else 0.045)
    # LEDs around it.
    g["dot"] = 0.07 if small else (0.052 if medium else 0.04)
    top = 4 if small else 6
    side = 2 if small else 3
    ring = (0.08, 0.12, 0.92, 0.88)
    x0, y0, x1, y1 = ring
    dots = []
    sx0, sy0, sx1, sy1 = g["screen"]
    for i in range(top):  # top, left to right
        dots.append((sx0 + 0.03 + (sx1 - sx0 - 0.06) * i / (top - 1), y0))
    for i in range(side):  # right, top to bottom
        dots.append((x1, sy0 + 0.06 + (sy1 - sy0 - 0.12) * i / max(side - 1, 1)))
    for i in range(top):  # bottom, right to left
        dots.append((sx1 - 0.03 - (sx1 - sx0 - 0.06) * i / (top - 1), y1))
    for i in range(side):  # left, bottom to top
        dots.append((x0, sy1 - 0.06 - (sy1 - sy0 - 0.12) * i / max(side - 1, 1)))
    g["dots"] = dots
    g["glow"] = not small
    return g


def over(dst, src):
    sr, sg, sb, sa = src
    dr, dg, db, da = dst
    a = sa + da * (1 - sa)
    if a <= 0:
        return (0.0, 0.0, 0.0, 0.0)
    return ((sr * sa + dr * da * (1 - sa)) / a, (sg * sa + dg * da * (1 - sa)) / a,
            (sb * sa + db * da * (1 - sa)) / a, a)


def render(size):
    g = layout(size)
    sx0, sy0, sx1, sy1 = g["screen"]
    r, stroke, dot = g["radius"], g["stroke"], g["dot"]
    colors = [colorsys.hsv_to_rgb(i / len(g["dots"]), 0.85, 1.0) for i in range(len(g["dots"]))]
    pixels = []
    for py in range(size):
        row = []
        for px in range(size):
            # Coverage of each layer, supersampled.
            fill = outline = 0
            dot_cov = [0] * len(g["dots"])
            for sy in range(SS):
                for sx in range(SS):
                    x = (px + (sx + 0.5) / SS) / size
                    y = (py + (sy + 0.5) / SS) / size
                    outer = rounded_rect_inside(x, y, sx0 - stroke / 2, sy0 - stroke / 2,
                                                sx1 + stroke / 2, sy1 + stroke / 2, r + stroke / 2)
                    inner = rounded_rect_inside(x, y, sx0 + stroke / 2, sy0 + stroke / 2,
                                                sx1 - stroke / 2, sy1 - stroke / 2, max(r - stroke / 2, 0.001))
                    if inner:
                        fill += 1
                    elif outer:
                        outline += 1
                    for i, (dx, dy) in enumerate(g["dots"]):
                        if (x - dx) ** 2 + (y - dy) ** 2 <= dot * dot:
                            dot_cov[i] += 1
            n = SS * SS
            c = (0.0, 0.0, 0.0, 0.0)
            cx, cy = (px + 0.5) / size, (py + 0.5) / size
            if g["glow"]:
                for i, (dx, dy) in enumerate(g["dots"]):
                    d = math.hypot(cx - dx, cy - dy)
                    a = 0.45 * math.exp(-((d / (dot * 2.6)) ** 2))
                    if a > 0.004:
                        c = over(c, (*colors[i], a))
            if fill:
                # Dark screen with a faint diagonal sheen.
                t = (cx - sx0) / (sx1 - sx0) * 0.5 + (cy - sy0) / (sy1 - sy0) * 0.5
                shade = 0.16 - 0.06 * t
                c = over(c, (shade, shade, shade + 0.03, fill / n))
            if outline:
                c = over(c, (0.92, 0.92, 0.95, outline / n))
            for i, cov in enumerate(dot_cov):
                if cov:
                    c = over(c, (*colors[i], cov / n))
            row.append(c)
        pixels.append(row)
    return pixels


def png(pixels):
    size = len(pixels)
    raw = bytearray()
    for row in pixels:
        raw.append(0)
        for r, g, b, a in row:
            raw += bytes([round(r * 255), round(g * 255), round(b * 255), round(min(a, 1) * 255)])

    def chunk(kind, data):
        return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data) & 0xFFFFFFFF)

    return (b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0))
            + chunk(b"IDAT", zlib.compress(bytes(raw), 9)) + chunk(b"IEND", b""))


def main():
    images = [png(render(s)) for s in SIZES]
    header = struct.pack("<HHH", 0, 1, len(images))
    offset = 6 + 16 * len(images)
    entries = b""
    for s, data in zip(SIZES, images):
        entries += struct.pack("<BBBBHHII", s % 256, s % 256, 0, 0, 1, 32, len(data), offset)
        offset += len(data)
    out = os.path.join(os.path.dirname(__file__), "..", "src", "Ledly", "Ledly.ico")
    with open(out, "wb") as f:
        f.write(header + entries + b"".join(images))
    print("wrote", os.path.normpath(out))


if __name__ == "__main__":
    main()
