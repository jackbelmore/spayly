#!/bin/sh
# For someone with no clone yet: checks git, git-lfs and Node, clones Spayly, runs start.sh. Installs nothing.
#   curl -fsSL https://raw.githubusercontent.com/jackbelmore/spayly/main/bootstrap.sh | sh
# SPAYLY_DIR sets the clone folder (default ./spayly). Arguments after `sh -s --` go to start.sh.
set -eu

REPO=https://github.com/jackbelmore/spayly
dir="${SPAYLY_DIR:-spayly}"

missing=""
command -v git >/dev/null 2>&1 || missing="$missing git"
if command -v git >/dev/null 2>&1 && ! git lfs version >/dev/null 2>&1; then missing="$missing git-lfs"; fi
if ! command -v node >/dev/null 2>&1 || [ "$(node -p 'process.versions.node.split(".")[0]')" -lt 22 ]; then missing="$missing node>=22"; fi

if [ -n "$missing" ]; then
  echo "bootstrap: missing:$missing" >&2
  case "$(uname -s)" in
    Darwin) echo "  brew install git git-lfs node" >&2 ;;
    Linux)
      echo "  Fedora/RHEL:   sudo dnf install git git-lfs nodejs" >&2
      echo "  Debian/Ubuntu: sudo apt install git git-lfs   (Node 22+: https://nodejs.org/en/download)" >&2 ;;
    *) echo "  https://git-scm.com/downloads  https://git-lfs.com  https://nodejs.org/en/download" >&2 ;;
  esac
  echo "Install them and run this again. (start.sh will list the rest, such as the .NET SDK.)" >&2
  exit 1
fi

git lfs install --skip-repo >/dev/null 2>&1 || true # before the clone, so the textures arrive as images and not pointer files

if [ -f "$dir/start.sh" ] && [ -d "$dir/.git" ]; then
  echo "==> $dir is already a spayly clone; not cloning" >&2
else
  echo "==> Cloning $REPO into $dir" >&2
  git clone "$REPO" "$dir"
fi

cd "$dir"
exec sh ./start.sh "$@"
