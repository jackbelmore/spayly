#!/usr/bin/env bash
# Every Godot launch goes through here: one Godot process at a time on this machine (memory), so we
# serialise on a lock file instead of trusting callers.
# Auto-fetches Godot 4.8-dev6 mono from godotengine/godot-builds if not already installed.
# Override the binary path with GODOT_BIN; set GODOT_VERSION to target a different release tag.
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# ── version + URLs ─────────────────────────────────────────────────────────────
GODOT_VERSION="${GODOT_VERSION:-4.8-dev6}"
BASE_URL="https://github.com/godotengine/godot-builds/releases/download/${GODOT_VERSION}"
INSTALL_DIR="${GODOT_INSTALL_DIR:-$HOME/.local/opt/godot-v${GODOT_VERSION}-mono}"

# ── OS / arch detection ────────────────────────────────────────────────────────
OS="$(uname -s)"
ARCH="$(uname -m)"
case "$OS" in
  Linux)
    case "$ARCH" in
      x86_64)
        ZIP="Godot_v${GODOT_VERSION}_mono_linux_x86_64.zip"
        BIN_REL="Godot_v${GODOT_VERSION}_mono_linux_x86_64/Godot_v${GODOT_VERSION}_mono_linux.x86_64" ;;
      aarch64|arm64)
        ZIP="Godot_v${GODOT_VERSION}_mono_linux_arm64.zip"
        BIN_REL="Godot_v${GODOT_VERSION}_mono_linux_arm64/Godot_v${GODOT_VERSION}_mono_linux.arm64" ;;
      *)
        echo "godot.sh: unsupported Linux arch: $ARCH" >&2; exit 1 ;;
    esac ;;
  Darwin)
    ZIP="Godot_v${GODOT_VERSION}_mono_macos.universal.zip"
    BIN_REL="Godot_mono.app/Contents/MacOS/Godot" ;;
  MINGW*|MSYS*|CYGWIN*)
    ZIP="Godot_v${GODOT_VERSION}_mono_win64.zip"
    BIN_REL="Godot_v${GODOT_VERSION}_mono_win64/Godot_v${GODOT_VERSION}_mono_win64.exe" ;;
  *)
    echo "godot.sh: unsupported OS: $OS" >&2; exit 1 ;;
esac

# ── binary resolution ──────────────────────────────────────────────────────────
# Priority: $GODOT_BIN > godot-dev on $PATH (legacy alias) > auto-fetched binary
_default_bin="${INSTALL_DIR}/${BIN_REL}"
GODOT_BIN="${GODOT_BIN:-$(command -v godot-dev 2>/dev/null || echo "$_default_bin")}"

# ── auto-fetch if the resolved binary is missing ───────────────────────────────
if [[ ! -f "$GODOT_BIN" ]]; then
  if [[ "$GODOT_BIN" != "$_default_bin" ]]; then
    echo "godot.sh: GODOT_BIN='$GODOT_BIN' does not exist" >&2
    exit 1
  fi
  echo "godot.sh: Godot $GODOT_VERSION not found; downloading to $INSTALL_DIR..." >&2
  _tmp="$(mktemp -d)"
  trap 'rm -rf "$_tmp"' EXIT
  curl -fL --progress-bar -o "$_tmp/$ZIP" "${BASE_URL}/${ZIP}"
  # Extract
  mkdir -p "$INSTALL_DIR"
  unzip -q -o "$_tmp/$ZIP" -d "$INSTALL_DIR"
  chmod +x "${INSTALL_DIR}/${BIN_REL}"
  # macOS: remove Gatekeeper quarantine flag from downloaded binary
  if [[ "$OS" == Darwin ]]; then
    xattr -dr com.apple.quarantine "$INSTALL_DIR" 2>/dev/null || true
  fi
  echo "godot.sh: installed → $GODOT_BIN" >&2
fi

# One lock for the machine, not per checkout: agent worktrees each have their own godot/, and a
# per-tree lock let two Godot processes run at once.
lock="${SPAYLY_GODOT_LOCK:-${XDG_RUNTIME_DIR:-/tmp}/spayly-godot.lock}"
exec 9>"$lock"
if command -v flock >/dev/null 2>&1; then
  flock -w 900 9 || { echo "godot.sh: another Godot held $lock for 900 s; giving up" >&2; exit 75; }
else
  echo "godot.sh: warning: 'flock' not available, skipping concurrency lock" >&2
fi
# fd 9 stays open in the exec'd Godot, so the lock lives exactly as long as the process.
exec "$GODOT_BIN" "$@"
