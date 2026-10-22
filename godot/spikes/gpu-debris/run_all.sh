#!/usr/bin/env bash
# The M1a measurement matrix. WINDOWED=1 runs on the live session instead of Xvfb (real dt_ms, busy GPU).
# Each run: 300 frames, 1600x900 Xvfb, shots at 60 and 240, VRAM sampled every second.
#   run_all.sh [label n contacts gravity route [extra...]]   (no args: the full matrix)
set -uo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
tools="$here/../../tools"

wait_mem() {
  while true; do
    avail=$(free -m | awk '/^Mem:/ {print $7}')
    [[ $avail -ge 2500 ]] && break
    echo "run_all: only ${avail} MB available, waiting 30 s" >&2
    sleep 30
  done
}

one() {
  local label=$1 n=$2 contacts=$3 gravity=$4 route=$5; shift 5
  local out="$here/capture/$label"
  wait_mem
  mkdir -p "$out"
  free -m | awk '/^Mem:/ {print "available_mb=" $7}' > "$out/mem_before.txt"
  nvidia-smi --query-gpu=memory.used --format=csv,noheader > "$out/vram_before.txt"
  nvidia-smi --query-gpu=timestamp,memory.used,clocks.sm,clocks.mem,pstate,utilization.gpu --format=csv,noheader -lms 250 > "$out/vram_during.csv" 2>/dev/null &
  local sampler=$!
  "$tools/run-capture.sh" "$here" ${WINDOWED:+--windowed} --frames ${FRAMES:-300} --shots ${SHOTS:-60:f060,240:f240} --out "$out" -- --n "$n" --contacts "$contacts" --gravity "$gravity" --route "$route" "$@" > "$out/stdout.txt" 2>&1
  local rc=$?
  kill $sampler 2>/dev/null; wait $sampler 2>/dev/null
  echo "run_all: $label exit=$rc"
}

if [[ $# -ge 5 ]]; then one "$@"; exit; fi
for rep in 1 2; do
  one "n100k-c0-r$rep" 100000 0 1 a
  one "n100k-c1-r$rep" 100000 1 1 a
  one "n1m-c0-r$rep" 1000000 0 1 a
  one "n1m-c1-r$rep" 1000000 1 1 a
done
one "n100k-c1-g0" 100000 1 0 a
one "n1m-c1-g0" 1000000 1 0 a
one "n1m-c1-routeb" 1000000 1 1 b
