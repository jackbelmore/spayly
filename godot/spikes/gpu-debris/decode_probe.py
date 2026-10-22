#!/usr/bin/env python3
"""Decode the frame-ordering probe bars from CaptureDriver shots. Stdlib only; reuses godot/tools/shotcheck.read_png.

Usage: decode_probe.py CAPTURE_DIR   (needs result.json for the bar pixel positions and NN-name.png shots)
The shot saved at process frame F holds the draw of frame F-1. cpu row = frame number the draw belongs to;
gpu row = frame number the compute wrote into the texel that draw sampled. Equal rows: the draw used same-frame compute.
"""
import glob
import json
import os
import re
import sys

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..', 'tools'))
from shotcheck import read_png  # noqa: E402


def decode(rows, ch, pts):
    v = 0
    for bit, (x, y) in enumerate(pts):
        if rows[y][x * ch] > 127:
            v |= 1 << bit
    return v


def main():
    d = sys.argv[1]
    res = json.load(open(os.path.join(d, 'result.json')))
    cpu_pts = [tuple(map(int, s.split(','))) for s in res['probe_px_cpu_row']]
    gpu_pts = [tuple(map(int, s.split(','))) for s in res['probe_px_gpu_row']]
    log = open(os.path.join(d, 'run.log')).read()
    shot_frame = {os.path.basename(m.group(2)): int(m.group(1)) for m in re.finditer(r'shot frame=(\d+) (\S+\.png)', log)}
    for png in sorted(glob.glob(os.path.join(d, '[0-9][0-9]-*.png'))):
        w, h, ch, rows = read_png(png)
        cpu, gpu = decode(rows, ch, cpu_pts), decode(rows, ch, gpu_pts)
        f = shot_frame.get(os.path.basename(png))
        print(f'{os.path.basename(png)} process_frame={f} cpu_row={cpu} gpu_row={gpu} draw_minus_compute={cpu - gpu}')


if __name__ == '__main__':
    main()
