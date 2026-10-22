#!/usr/bin/env bash
# Screenshot + frame-timing run of a Godot C# project on the real GPU.
#
#   run-capture.sh <project-dir> [--frames N] [--shots F:NAME,...] [--warmup N] [--out DIR]
#                  [--windowed] [--display-driver x11|wayland] [--resolution WxH] [-- extra user args for the game]
#
# Default: under Xvfb (1600x900x24, or --resolution), x11 display driver, Vulkan. --windowed uses the live session instead.
# Fails (exit 1) unless device.json names an NVIDIA adapter (or $SPAYLY_GPU): a llvmpipe/software run's pixels and timings are worthless.
set -uo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
godot="$here/godot.sh"

die() { echo "run-capture: $*" >&2; exit 1; }
[[ $# -ge 1 ]] || die "usage: run-capture.sh <project-dir> [--frames N] [--shots spec] [--warmup N] [--out dir] [--windowed] [--display-driver d]"
proj="$(cd "$1" 2>/dev/null && pwd)" || die "no such project dir: $1"
shift
[[ -f "$proj/project.godot" ]] || die "$proj has no project.godot"

frames=120 shots='' warmup=10 out='' windowed=0 driver=x11 res=1600x900 extra=()
while [[ $# -gt 0 ]]; do
  case "$1" in
    --frames) frames="$2"; shift 2 ;;
    --shots) shots="$2"; shift 2 ;;
    --warmup) warmup="$2"; shift 2 ;;
    --out) out="$2"; shift 2 ;;
    --windowed) windowed=1; shift ;;
    --display-driver) driver="$2"; shift 2 ;;
    --resolution) res="$2"; shift 2 ;;
    --) shift; extra=("$@"); break ;;
    *) die "unknown option $1" ;;
  esac
done
out="${out:-$proj/capture/$(date +%Y%m%d-%H%M%S)}"
mkdir -p "$out" && out="$(cd "$out" && pwd)"
# The output usually sits inside the project; without this the editor import ingests every PNG and CSV as a resource.
[ -e "$out/.gdignore" ] || touch "$out/.gdignore"
parent="$(dirname "$out")"; case "$parent" in */capture) [ -e "$parent/.gdignore" ] || touch "$parent/.gdignore";; esac
rm -f "$out"/frametimes.csv "$out"/device.json "$out"/[0-9][0-9]-*.png
log="$out/run.log"
: > "$log"

# (a) build the C# assembly Godot loads from .godot/mono/temp/bin/Debug.
# (Run `node godot/tools/start.mjs --no-pull --prepare-only` before this to build, and import assets, without launching.)
csproj=("$proj"/*.csproj)
if [[ -f "${csproj[0]}" ]]; then
  echo "run-capture: dotnet build ${csproj[0]}" | tee -a "$log"
  set +e; build_out="$(dotnet build "${csproj[0]}" -nologo -v q 2>&1)"; build_rc=$?; set -e
  printf '%s\n' "$build_out" >>"$log"; printf '%s\n' "$build_out" | grep -E 'error|warn|Elapsed' || true
  [[ $build_rc -eq 0 ]] || die "dotnet build failed (rc=$build_rc), see $log"
fi

# (c) capture. (d) everything goes to run.log as well as the terminal.
user_args=(--capture-dir "$out" --frames "$frames" --warmup "$warmup")
[[ -n "$shots" ]] && user_args+=(--shots "$shots")
user_args+=("${extra[@]}")
gargs=(--display-driver "$driver" --rendering-driver vulkan --path "$proj" --resolution "$res" -- "${user_args[@]}")
if [[ $windowed -eq 1 ]]; then
  cmd=("$godot" "${gargs[@]}")
else
  cmd=(xvfb-run -a -s "-screen 0 ${res}x24" "$godot" "${gargs[@]}")
fi
echo "run-capture: ${cmd[*]}" | tee -a "$log"
start=$(date +%s.%N)
"${cmd[@]}" 2>&1 | tee -a "$log"
rc=${PIPESTATUS[0]}
echo "run-capture: godot exit=$rc wall=$(printf '%.1f' "$(echo "$(date +%s.%N) - $start" | bc)")s" | tee -a "$log"

# (e) refuse a software or missing adapter.
[[ -f "$out/device.json" ]] || die "no device.json in $out (godot exit $rc); see $log"
adapter="$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["adapter_name"])' "$out/device.json")"
echo "run-capture: adapter=$adapter"
# SPAYLY_GPU names the expected adapter on another machine (e.g. SPAYLY_GPU=AMD); software rendering is refused always.
[[ "$adapter" == *llvmpipe* || "$adapter" == *SwiftShader* ]] && die "adapter '$adapter' is software rendering; timings and pixels are invalid"
if [[ "$adapter" != *"${SPAYLY_GPU:-NVIDIA}"* ]]; then
  die "adapter '$adapter' is not the expected GPU '${SPAYLY_GPU:-NVIDIA}' (set SPAYLY_GPU to your adapter's name); timings and pixels are invalid"
fi
[[ $rc -eq 0 ]] || die "godot exited $rc; see $log"

# (f) numbers.
fail=0
python3 "$here/summarize-frametimes.py" "$out/frametimes.csv" --warmup "$warmup" || fail=1
prev=''
for png in "$out"/[0-9][0-9]-*.png; do
  [[ -f "$png" ]] || continue
  if [[ -n "$prev" ]]; then
    python3 "$here/shotcheck.py" "$png" --ref "$prev" || fail=1
  else
    python3 "$here/shotcheck.py" "$png" || fail=1
  fi
  prev="$png"
done
echo "run-capture: out=$out"
exit $fail
