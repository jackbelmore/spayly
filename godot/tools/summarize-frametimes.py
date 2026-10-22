#!/usr/bin/env python3
"""Print p50/p95/max of dt_ms, cpu_ms, gpu_ms from a CaptureDriver frametimes.csv. Stdlib only."""
import argparse
import csv
import math
import sys


def pct(sorted_vals, p):
    # Nearest-rank percentile: a value that actually occurred, no interpolation.
    if not sorted_vals:
        return float('nan')
    k = max(1, math.ceil(p / 100 * len(sorted_vals)))
    return sorted_vals[k - 1]


def main():
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument('csv')
    ap.add_argument('--warmup', type=int, default=10, help='leading frames to drop (default 10)')
    a = ap.parse_args()
    with open(a.csv, newline='') as f:
        rows = [r for r in csv.DictReader(f) if int(r['frame']) > a.warmup]
    if not rows:
        print(f'no frames after warmup {a.warmup} in {a.csv}', file=sys.stderr)
        return 1
    print(f'frames={len(rows)} (warmup {a.warmup} dropped)  {a.csv}')
    # min/p5 of gpu_ms is the repeatable number for tiny workloads: the GPU drops clocks mid-run when mostly idle.
    print(f'{"metric":<8}{"min":>10}{"p5":>10}{"p50":>10}{"p95":>10}{"max":>10}{"mean":>10}')
    for col in ('dt_ms', 'cpu_ms', 'gpu_ms'):
        v = sorted(float(r[col]) for r in rows)
        print(f'{col:<8}{v[0]:>10.3f}{pct(v, 5):>10.3f}{pct(v, 50):>10.3f}{pct(v, 95):>10.3f}{v[-1]:>10.3f}{sum(v) / len(v):>10.3f}')
    fps = 1000 / pct(sorted(float(r['dt_ms']) for r in rows), 50)
    print(f'fps@p50 {fps:.1f}')
    return 0


if __name__ == '__main__':
    sys.exit(main())
