#!/usr/bin/env python3
"""Numeric sanity check of a PNG screenshot, so nobody has to look at it. Stdlib only (zlib + struct).

Prints the fraction of non-black pixels, mean luminance, distinct colours in a sample, and PSNR against --ref.
Exits 1 if the non-black fraction is below --min-nonblack. Supports 8-bit RGB/RGBA/grey, non-interlaced.
"""
import argparse
import math
import struct
import sys
import zlib

CHANNELS = {0: 1, 2: 3, 4: 2, 6: 4}  # colour type -> samples per pixel


def read_png(path):
    """Return (width, height, channels, rows) where rows is a list of bytes, one per scanline, filters undone."""
    with open(path, 'rb') as f:
        data = f.read()
    if data[:8] != b'\x89PNG\r\n\x1a\n':
        raise ValueError(f'{path}: not a PNG')
    pos, idat, ihdr = 8, [], None
    while pos < len(data):
        length, ctype = struct.unpack('>I4s', data[pos:pos + 8])
        body = data[pos + 8:pos + 8 + length]
        pos += 12 + length
        if ctype == b'IHDR':
            ihdr = struct.unpack('>IIBBBBB', body)
        elif ctype == b'IDAT':
            idat.append(body)
        elif ctype == b'IEND':
            break
    w, h, depth, ctype, _, _, interlace = ihdr
    if depth != 8 or ctype not in CHANNELS or interlace:
        raise ValueError(f'{path}: unsupported PNG (depth {depth}, colour type {ctype}, interlace {interlace})')
    bpp = CHANNELS[ctype]
    raw = zlib.decompress(b''.join(idat))
    stride = w * bpp
    rows, prev = [], bytearray(stride)
    for y in range(h):
        base = y * (stride + 1)
        ft = raw[base]
        cur = bytearray(raw[base + 1:base + 1 + stride])
        if ft == 1:
            for i in range(bpp, stride):
                cur[i] = (cur[i] + cur[i - bpp]) & 255
        elif ft == 2:
            for i in range(stride):
                cur[i] = (cur[i] + prev[i]) & 255
        elif ft == 3:
            for i in range(stride):
                left = cur[i - bpp] if i >= bpp else 0
                cur[i] = (cur[i] + ((left + prev[i]) >> 1)) & 255
        elif ft == 4:
            for i in range(stride):
                a = cur[i - bpp] if i >= bpp else 0
                b = prev[i]
                c = prev[i - bpp] if i >= bpp else 0
                p = a + b - c
                pa, pb, pc = abs(p - a), abs(p - b), abs(p - c)
                cur[i] = (cur[i] + (a if pa <= pb and pa <= pc else b if pb <= pc else c)) & 255
        elif ft != 0:
            raise ValueError(f'{path}: bad filter type {ft} on row {y}')
        rows.append(bytes(cur))
        prev = cur
    return w, h, bpp, rows


def rgb_rows(w, bpp, rows):
    """Yield each row as a flat RGB bytes object (alpha dropped, grey expanded)."""
    for r in rows:
        if bpp == 3:
            yield r
        elif bpp == 4:
            yield bytes(b for i in range(0, len(r), 4) for b in r[i:i + 3])
        else:
            g = r[::bpp]
            yield bytes(v for v in g for _ in range(3))


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument('png')
    ap.add_argument('--ref', help='second PNG of the same size; prints PSNR in dB')
    ap.add_argument('--min-nonblack', type=float, default=0.02, help='fail below this non-black fraction (default 0.02)')
    ap.add_argument('--black-level', type=int, default=8, help='a pixel is black if max(R,G,B) <= this (default 8)')
    a = ap.parse_args()

    w, h, bpp, rows = read_png(a.png)
    img = list(rgb_rows(w, bpp, rows))
    n = w * h
    nonblack, lum_sum, colours = 0, 0.0, set()
    step = max(1, int(math.sqrt(n / 20000)))  # sample ~20k pixels for the colour count
    for y, r in enumerate(img):
        for x in range(w):
            i = 3 * x
            R, G, B = r[i], r[i + 1], r[i + 2]
            if max(R, G, B) > a.black_level:
                nonblack += 1
            lum_sum += 0.2126 * R + 0.7152 * G + 0.0722 * B
            if y % step == 0 and x % step == 0:
                colours.add((R, G, B))
    frac = nonblack / n
    print(f'{a.png}: {w}x{h} ch={bpp} nonblack={frac:.4f} mean_lum={lum_sum / n:.2f} distinct_colours(sampled 1/{step}^2)={len(colours)}')

    if a.ref:
        rw, rh, rbpp, rrows = read_png(a.ref)
        if (rw, rh) != (w, h):
            print(f'psnr: size mismatch {w}x{h} vs {rw}x{rh}')
            return 1
        se = 0
        for r1, r2 in zip(img, rgb_rows(rw, rbpp, rrows)):
            se += sum((p - q) * (p - q) for p, q in zip(r1, r2))
        mse = se / (n * 3)
        psnr = float('inf') if mse == 0 else 10 * math.log10(255 * 255 / mse)
        print(f'psnr vs {a.ref}: {psnr:.2f} dB (mse {mse:.3f})')

    if frac < a.min_nonblack:
        print(f'FAIL: non-black fraction {frac:.4f} < {a.min_nonblack}')
        return 1
    return 0


if __name__ == '__main__':
    sys.exit(main())
