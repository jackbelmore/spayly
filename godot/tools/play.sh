#!/usr/bin/env bash
# Launch without pulling. The logic lives in start.mjs; `./start.sh` at the repo root is the same thing with a pull first.
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
exec node "$here/start.mjs" --no-pull "$@"
