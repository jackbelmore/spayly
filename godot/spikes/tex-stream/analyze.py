#!/usr/bin/env python3
"""Summarise tex-stream capture runs: VRAM per phase, resident-LOD transitions, force-frame cost, PSNR vs reference.

  analyze.py capture/<run> [--ref capture/<ref-run>]
Stdlib only; PSNR comes from ../../tools/shotcheck.py so the number is the harness's own.
"""
import argparse
import csv
import os
import re
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
SHOTCHECK = os.path.join(HERE, '..', '..', 'tools', 'shotcheck.py')


def rows(path):
    with open(path, newline='') as f:
        return list(csv.DictReader(f))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('run')
    ap.add_argument('--ref')
    a = ap.parse_args()
    s = rows(os.path.join(a.run, 'stream.csv'))
    ft = {int(r['frame']): r for r in rows(os.path.join(a.run, 'frametimes.csv'))}
    base = {}
    bp = os.path.join(a.run, 'baseline.txt')
    if os.path.exists(bp):
        for line in open(bp):
            k, v = line.strip().split('=')
            base[k] = int(v)
    nvb = base.get('nvidia_baseline_MB')
    print(f'== {a.run}  nvidia baseline {nvb} MB')

    # Per phase: the last row of the phase (settled) and the peak.
    phases = []
    for r in s:
        if not phases or phases[-1] != r['phase']:
            phases.append(r['phase'])
    print(f'{"phase":<6}{"texMemMB":>10}{"peak":>9}{"budgetMB":>10}{"rdWidth":>9}{"nvMB":>7}{"nv-base":>9}')
    for ph in phases:
        pr = [r for r in s if r['phase'] == ph]
        last = pr[-1]
        nv = [r for r in pr if r['nvidiaUsedMB']]
        nvl = int(nv[-1]['nvidiaUsedMB']) if nv else None
        peak = max(float(r['textureMemMB']) for r in pr)
        print(f'{ph:<6}{float(last["textureMemMB"]):>10.2f}{peak:>9.2f}{float(last["budgetUsedMB"]):>10.2f}{last["rdWidth"]:>9}'
              f'{nvl if nvl is not None else "":>7}{(nvl - nvb) if nvl is not None and nvb else "":>9}')

    print('resident LOD transitions (frame, t_ms, phase, rdWidth, mips, budgetMB, texMemMB, gpu_ms that frame):')
    prev = None
    for r in s:
        k = (r['rdWidth'], r['rdMips'])
        if k != prev:
            f = int(r['frame'])
            print(f'  f={f:<4} t={float(r["t_ms"]) if "t_ms" in r else float("nan"):>9.1f} {r["phase"]:<5} {r["rdWidth"]:>6}px {r["rdMips"]:>3} mips '
                  f'budget={r["budgetUsedMB"]:>6} texMem={r["textureMemMB"]:>7} gpu={ft.get(f, {}).get("gpu_ms", "?")}')
            prev = k

    ev = os.path.join(a.run, 'stream-events.txt')
    if os.path.exists(ev):
        text = open(ev).read()
        m = re.search(r'force frame=(\d+)', text)
        if m:
            f0 = int(m.group(1))
            print('frames around force (frame dt_ms gpu_ms texMemMB):')
            for f in range(f0 - 2, f0 + 6):
                if f in ft:
                    sr = next((r for r in s if int(r['frame']) == f), {})
                    print(f'  {f} dt={float(ft[f]["dt_ms"]):.2f} gpu={float(ft[f]["gpu_ms"]):.3f} texMem={sr.get("textureMemMB")} rd={sr.get("rdWidth")}')

    if a.ref:
        print('PSNR vs reference:')
        for png in sorted(os.listdir(a.run)):
            if not png.endswith('.png'):
                continue
            name = png.split('-', 1)[1]
            ref = next((os.path.join(a.ref, p) for p in os.listdir(a.ref) if p.endswith('-' + name)), None)
            if ref is None and name.startswith('n1'):
                ref = next((os.path.join(a.ref, p) for p in os.listdir(a.ref) if p.endswith('-near.png')), None)
            if ref is None:
                continue
            out = subprocess.run([sys.executable, SHOTCHECK, os.path.join(a.run, png), '--ref', ref, '--min-nonblack', '0'],
                                 capture_output=True, text=True).stdout
            m = re.search(r'psnr vs .*?: (\S+) dB', out)
            print(f'  {png:<22} vs {os.path.relpath(ref, os.path.dirname(a.run)):<28} {m.group(1) if m else out.strip()} dB')
    return 0


if __name__ == '__main__':
    sys.exit(main())
