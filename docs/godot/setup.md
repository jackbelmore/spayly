# Setting up the Godot port on a new computer

Everything the port needs is in git, except the engine binary, the build tools and two generated folders. Those are
the Godot import cache and `docs/godot/api-dump/`, both git-ignored and rebuilt below.

## First run
```
./start.sh              # macOS / Linux; on Windows double-click start.cmd or run it in a terminal
```
That is the whole procedure. (With no clone yet, the README's `bootstrap.sh` / `bootstrap.ps1` one-liners clone first, then do this.)
`start` runs `godot/tools/start.mjs`, which does these in order, skipping each step whose output is newer than its inputs:
1. **Pull** `main` with `git pull --ff-only --autostash`. Skipped on a detached HEAD, a branch without an upstream, or any branch but
   `main`. Offline, it warns and carries on. If the pull moves HEAD it restarts itself with the new scripts and rebuilds.
   A diverged `main` stops with `git status -sb` and the fix.
2. **Toolchain check**: git, git-lfs, Node >= 22, .NET SDK >= 10 (any installed SDK, whatever `global.json` pins), plus `unzip` on
   Linux. Every missing item is reported at once with the install line for your OS. Nothing is installed.
3. **Assets**: `git lfs pull` if any map is still a pointer file; `npm ci` if `node_modules` is older than `package-lock.json`;
   `prepare-textures.mjs` if the manifest is older than a map or the bake code.
4. **Godot**: `$GODOT_BIN`, else `godot-dev` on `PATH`, else `~/.local/opt/godot-v<ver>-mono/`, downloaded on first use. The version
   is the `GODOT_VERSION` default in `godot/tools/godot.sh`. Windows uses the `_console.exe` so logs reach the terminal.
5. **Build** (`dotnet build`) when a `.cs`/`.csproj` under `Spayly.Game/` or `Spayly.Sim/` is newer than the DLL.
6. **Import** (`--headless --import`) when the cache is missing or a `.import` file is newer. First time: about 30 s.
7. **Launch** through `godot.sh` (Linux/macOS), or the binary directly (Windows).

Flags: `--rebuild` (force textures, build and import), `--no-pull`, `--prepare-only` (everything except the launch; use it before
`run-capture.sh`), `--dry-run` (print every decision, command and download URL; change nothing), `--platform win32|darwin|linux`
and `--arch x64|arm64` (only with `--dry-run`: the Windows check from a Linux box). Arguments after `--` go to Godot:
`./start.sh -- --quit-after 1`. `godot/tools/play.sh` is the same launcher with `--no-pull`.

**Windows differences.** There is no machine-wide Godot lock (no `flock`), so do not start two. Windows refuses symlinks without
Developer Mode, so `prepare-textures.mjs` falls back to a hardlink and then to a copy of each map; both are recognised as
up to date on the next run. After `winget install`, open a new terminal: the old one's `PATH` does not refresh.

## What it needs
- **Git with LFS** (`git lfs install`). The maps in `public/textures/` are LFS objects.
- **Node 22** (tested 22.23), for `npm ci` and the texture tool.
- **.NET SDK 10** (tested 10.0.112).
- **Godot 4.8 dev6, .NET (mono) build**, from the `4.8-dev6` release of github.com/godotengine/godot-builds. `start` fetches the
  right build for your OS and arch; override the folder with `GODOT_INSTALL_DIR`, the tag with `GODOT_VERSION`.
  Do not use 4.7 stable: the project pins 4.8.0-dev.6.
  `Godot.NET.Sdk` and `GodotSharp` 4.8.0-dev.6 restore from nuget.org; no local package path is needed.
- **Python 3.** `shotcheck.py` and `summarize-frametimes.py` use only the standard library.
- **Linux, for captures:** `xvfb-run` (Xvfb) and a Vulkan GPU driver.

For a full verification run (captures + frame timing):
```
git lfs pull                                   # public/textures/*.jpg
npm install
node godot/tools/prepare-textures.mjs          # fills godot/Spayly.Game/textures/ (symlinks + baked maps)
nice -n 19 dotnet test godot/Spayly.Sim.Tests  # 271 pass as of M7
nice -n 19 dotnet build godot/Spayly.Game/Spayly.Game.csproj   # 0 warnings
godot/tools/run-capture.sh godot/Spayly.Game --frames 120 --out godot/Spayly.Game/capture/smoke -- --actions "wait:30;realismSelftest"
```
- **Captures refuse a GPU that is not NVIDIA.** On another GPU, set `SPAYLY_GPU` to part of its adapter name, for
  example `SPAYLY_GPU=AMD`. Software rendering (llvmpipe) is always refused.
- **API dump** (`docs/godot/api-dump/`, the API truth that `godot/CLAUDE.md` refers to): run
  `godot-dev --headless --doctool docs/godot/api-dump`, then `godot-dev --headless --dump-extension-api-with-docs`
  inside that folder.

## Working rules that are machine facts, not code
- **One Godot process at a time.** `tools/godot.sh` takes a machine-wide lock in `$XDG_RUNTIME_DIR` (or `/tmp`).
- **Frame times and SPH cost are measured `--windowed`.** Xvfb inflates the SPH substep cost about 2–3×.
- **Stopping a run:** kill Godot by PID, never with `pkill -f <pattern>`. The pattern also matches the shell that runs it.
- **Agent worktrees:** symlink `godot/Spayly.Game/textures` to the main checkout's, and copy the import cache with
  `cp -a --reflink=always <main>/godot/Spayly.Game/.godot/imported <wt>/godot/Spayly.Game/.godot/`. This avoids a full
  re-import.

## Where things stand
- **Milestone history:** the milestone table in `docs/godot/decisions.md`.
- **Open items and owner decisions:** `docs/godot/verify-queue.md`.
- **Latest capture recipes:** `docs/godot/m7-verify-recipes.md`.
- **Capture output:** `godot/Spayly.Game/capture/` is git-ignored, so a new computer starts without old captures.
  Their numbers are in the milestone table.
