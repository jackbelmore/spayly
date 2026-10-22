#!/usr/bin/env bash
# Reference screenshots of the web build, for pixel/composition parity against the Godot port.
#
#   web-parity-shots.sh [outDir]         (default: godot/parity/web)
#
# Starts a throwaway vite dev server on a free port (never assume 5173 or 5188 - other chats run
# their own dev servers on this machine, see the spayly-browser-harness memory), waits for it to
# print its URL, runs the app through the window.spayly API via scripts/shoot.mjs (one Chromium,
# one page, all actions in sequence - never more than one browser at a time), then stops the
# server. See docs/godot/web-architecture-map.md §1 for the App API and
# .claude/skills/visual-check/SKILL.md for how scripts/shoot.mjs and its env vars work.
#
# Writes <outDir>/00-start.png plus one NN-<name>.png per action below, and <outDir>/manifest.json
# mapping each shot to the web action that produced it and the intended Godot --actions equivalent
# (exact Godot syntax TBD by that side; this is a proposal for it to consume or amend).
set -uo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
root="$(cd "$here/../.." && pwd)"
cd "$root"

die() { echo "web-parity-shots: $*" >&2; exit 1; }

out="${1:-godot/parity/web}"
mkdir -p "$out"
out_abs="$(cd "$out" && pwd)"

chrome="$HOME/.cache/ms-playwright/chromium-1243/chrome-linux64/chrome"
[[ -x "$chrome" ]] || die "chromium not found at $chrome (the 'chrome' channel isn't installed on this machine - see the spayly-browser-harness memory)"

vite_pid=""
vite_log="$(mktemp -t spayly-parity-vite-XXXXXX.log)"

cleanup() {
  [[ -n "$vite_pid" ]] && kill "$vite_pid" 2>/dev/null
  [[ -n "$vite_pid" ]] && wait "$vite_pid" 2>/dev/null
  true
}
trap cleanup EXIT

# --port 0 asks the OS for a free port; --strictPort stops vite silently reusing a port some
# other chat's dev server already has (memory: other chats run vite concurrently here).
npm run dev -- --port 0 --strictPort >"$vite_log" 2>&1 &
vite_pid=$!

url=""
for _ in $(seq 1 60); do
  url="$(command grep -oE 'Local: *http://[^ ]+/' "$vite_log" | command grep -oE 'http://[^ ]+/' | head -1)"
  [[ -n "$url" ]] && break
  kill -0 "$vite_pid" 2>/dev/null || die "vite exited before printing a URL; see $vite_log"
  sleep 0.5
done
[[ -n "$url" ]] || die "vite did not print a URL within 30s; see $vite_log"
echo "web-parity-shots: dev server at $url (pid $vite_pid, log $vite_log)"

# Each action's wait is >=4000ms so textures (public/textures/) finish streaming in before the
# shot; earth/saturn/moon/jupiter-io/sun get 6000 to cover the 1.6s flyAbove flight as well.
# earth-close: the task asks to zoom in after warp is reset, but App exposes no public zoom
# method (only CameraRig.zoom, called internally from the wheel handler) - so per instructions
# this step is SKIPPED and the shot is identical in framing to "earth", just with warp=1.
actions='[
  {"name":"tabletop","js":"spayly.settings.diorama = true; spayly.viewTabletop()","wait":4000},
  {"name":"inner","js":"spayly.viewInner()","wait":4000},
  {"name":"outer","js":"spayly.viewOuter()","wait":4000},
  {"name":"earth","js":"spayly.flyAbove(spayly.universe.bodies.find(b => b.id === \"earth\"))","wait":6000},
  {"name":"earth-close","js":"spayly.setWarp(1) /* zoom skipped: no public App.zoom() */","wait":4000},
  {"name":"saturn","js":"spayly.flyAbove(spayly.universe.bodies.find(b => b.id === \"saturn\"))","wait":6000},
  {"name":"moon","js":"spayly.flyAbove(spayly.universe.bodies.find(b => b.id === \"moon\"))","wait":6000},
  {"name":"jupiter-io","js":"const io = spayly.universe.bodies.find(b => b.id === \"io\"); spayly.select(io); spayly.flyAbove(io)","wait":6000},
  {"name":"sun","js":"spayly.flyAbove(spayly.universe.bodies.find(b => b.id === \"sun\"))","wait":6000},
  {"name":"warp","js":"spayly.viewTabletop(); spayly.setWarp(1e6)","wait":5000},
  {"name":"asteroid-mode","js":"spayly.setMode(\"asteroid\")","wait":4000},
  {"name":"erase-mode","js":"spayly.setMode(\"erase\")","wait":4000}
]'

CHROME_PATH="$chrome" SHOT_W=1600 SHOT_H=900 SHOT_SETTLE=15000 \
  npm run shoot -- "$url" "$out_abs" "$actions"
shoot_rc=$?

kill "$vite_pid" 2>/dev/null
wait "$vite_pid" 2>/dev/null
vite_pid=""

cat > "$out_abs/manifest.json" <<'JSON'
{
  "tabletop":     { "file": "01-tabletop.png",     "web": "settings.diorama = true; viewTabletop()", "godot": "diorama:true;viewTabletop;wait:90;shot:tabletop" },
  "inner":        { "file": "02-inner.png",        "web": "viewInner()",                             "godot": "viewInner;wait:90;shot:inner" },
  "outer":        { "file": "03-outer.png",        "web": "viewOuter()",                             "godot": "viewOuter;wait:90;shot:outer" },
  "earth":        { "file": "04-earth.png",        "web": "flyAbove(earth)",                         "godot": "flyAbove:earth;wait:90;shot:earth" },
  "earth-close":  { "file": "05-earth-close.png",  "web": "setWarp(1) [zoom skipped: App has no public zoom() - only CameraRig.zoom(), called internally from the wheel handler]", "godot": "flyAbove:earth;zoom:0.3;setWarp:1;wait:60;shot:earth-close" },
  "saturn":       { "file": "06-saturn.png",       "web": "flyAbove(saturn)",                        "godot": "flyAbove:saturn;wait:90;shot:saturn" },
  "moon":         { "file": "07-moon.png",         "web": "flyAbove(moon)",                          "godot": "flyAbove:moon;wait:90;shot:moon" },
  "jupiter-io":   { "file": "08-jupiter-io.png",   "web": "select(io); flyAbove(io)",                "godot": "select:io;flyAbove:io;wait:90;shot:jupiter-io" },
  "sun":          { "file": "09-sun.png",          "web": "flyAbove(sun)",                           "godot": "flyAbove:sun;wait:90;shot:sun" },
  "warp":         { "file": "10-warp.png",         "web": "viewTabletop(); setWarp(1e6)",            "godot": "viewTabletop;setWarp:1e6;wait:75;shot:warp" },
  "asteroid-mode":{ "file": "11-asteroid-mode.png","web": "setMode('asteroid')",                     "godot": "setMode:asteroid;wait:60;shot:asteroid-mode" },
  "erase-mode":   { "file": "12-erase-mode.png",   "web": "setMode('erase')",                        "godot": "setMode:erase;wait:60;shot:erase-mode" }
}
JSON

echo "web-parity-shots: wrote shots + manifest to $out_abs"
exit "$shoot_rc"
