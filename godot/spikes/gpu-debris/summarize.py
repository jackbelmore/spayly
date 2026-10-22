#!/usr/bin/env python3
"""Summarise every capture/<label>/ run: compute.csv (RD timestamps) and frametimes.csv (viewport gpu_ms). Stdlib only."""
import csv
import glob
import json
import math
import os
import sys

WARMUP = 10


def pct(v, p):
    k = max(1, math.ceil(p / 100 * len(v)))
    return v[k - 1]


def stats(v):
    v = sorted(v)
    return (v[0], pct(v, 5), pct(v, 50), pct(v, 95)) if v else (float('nan'),) * 4


def col(path, name):
    with open(path, newline='') as f:
        return [float(r[name]) for r in csv.DictReader(f) if int(r['frame']) > WARMUP]


def main():
    root = sys.argv[1] if len(sys.argv) > 1 else os.path.join(os.path.dirname(os.path.abspath(__file__)), 'capture')
    fmt = '{:<18}{:>30}{:>30}{:>30}{:>30}  {}'
    print(fmt.format('run', 'compute total min/p5/p50/p95', 'integrate', 'grid', 'contacts', 'gpu_ms (viewport) min/p5/p50/p95'))
    for d in sorted(glob.glob(os.path.join(root, '*'))):
        c, ft = os.path.join(d, 'compute.csv'), os.path.join(d, 'frametimes.csv')
        if not (os.path.exists(c) and os.path.exists(ft)):
            continue
        s = lambda xs: '/'.join(f'{x:.3f}' for x in stats(xs))
        print(fmt.format(os.path.basename(d), s(col(c, 'total_ms')), s(col(c, 'integrate_ms')), s(col(c, 'grid_ms')),
                         s(col(c, 'contacts_ms')), s(col(ft, 'gpu_ms'))))
    print()
    for d in sorted(glob.glob(os.path.join(root, '*'))):
        rj = os.path.join(d, 'result.json')
        if not os.path.exists(rj):
            continue
        r = json.load(open(rj))
        vb = open(os.path.join(d, 'vram_before.txt')).read().strip() if os.path.exists(os.path.join(d, 'vram_before.txt')) else '?'
        vd = []
        if os.path.exists(os.path.join(d, 'vram_during.csv')):
            vd = [int(l.split(',')[1].split()[0]) for l in open(os.path.join(d, 'vram_during.csv')) if ',' in l]
        print(f"{os.path.basename(d)}: gc0 {r.get('gc0_at_50')}->{r.get('gc0_at_end')} alloc(my _Process, f51+)={r.get('my_process_alloc_bytes_frames_51_to_end')}B "
              f"overlaps f1={r.get('overlaps_frame1')} end={r.get('overlaps_end')} dP/sum(m|v|)={r.get('momentum_rel_change'):.3e} dKE/KE={r.get('kinetic_rel_change'):.3e} "
              f"vram before={vb} during max={max(vd) if vd else '?'}MiB rd_end=[{r.get('vram_rd_end_mb')}] ts_skipped={r.get('timestamps_skipped_partial_frames')}")


if __name__ == '__main__':
    main()
