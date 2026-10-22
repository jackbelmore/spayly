#!/bin/sh
# One command from a fresh clone to playing Spayly in Godot. Everything is in godot/tools/start.mjs.
# Usage: ./start.sh [--rebuild] [--no-pull] [--prepare-only] [--dry-run] [-- godot args]
cd "$(dirname "$0")" || exit 1

if ! command -v node >/dev/null 2>&1 || [ "$(node -p 'process.versions.node.split(".")[0]')" -lt 22 ]; then
  echo "Spayly needs Node 22 or newer to run its launcher. Install it, then run ./start.sh again:" >&2
  case "$(uname -s)" in
    Darwin) echo "  brew install node" >&2 ;;
    Linux)
      echo "  Fedora/RHEL:   sudo dnf install nodejs" >&2
      echo "  Debian/Ubuntu: https://nodejs.org/en/download (the distro package may be older than 22)" >&2 ;;
    *) echo "  https://nodejs.org/en/download" >&2 ;;
  esac
  exit 1
fi

exec node godot/tools/start.mjs "$@"
