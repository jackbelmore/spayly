# godot/ — the Godot 4.8 port

C# game + pure C# physics library + GLSL compute. The web build it ports is on branch `web-version` (a comment's "port of
src/…" means a file there); the bit of `src/` left on `main` is frozen tool input: never edit it.

## Layers
- Port conventions (names, `Vec3`, RNG, test mapping) are in `docs/godot/porting.md`; read it before editing Sim. *(unenforced)*
- `Spayly.Sim/` references no Godot assembly and no `Godot.NET.Sdk`; doubles everywhere, km/kg/s/rad like
  `src/sim` (SI only in the impact model). *(`Spayly.Sim.Tests/LayeringTests.cs`)*
- Render-side knowledge enters Sim through delegates (`bodyFrame`, `surfaceAt`), never an import. *(LayeringTests)*
- `Spayly.Game/` renders float32 offsets from the camera's double position; a Godot `Vector3` never holds an
  absolute position. *(unenforced — shows up as jitter in `spikes/scale`)*
- 2D is stretched (`canvas_items`, 1600×900 base): screen maths uses `GetVisibleRect`/`UnprojectPosition`, never `Viewport.Size`
  or window px. *(unenforced — shows up only at a non-1600×900 window: `run-capture.sh --resolution 3840x2160`)*

## Engine
- Binary pinned to 4.8 dev6 mono; always launch through `tools/godot.sh`, which holds a machine-wide lock (`$XDG_RUNTIME_DIR/spayly-godot.lock`): **one Godot
  process at a time**, never the editor GUI. *(`tools/godot.sh` flock)* Windows has no lock: `start.mjs` runs the binary directly.
- API truth is `docs/godot/api-dump/` (regenerate with `--doctool`), not the web or a research report. A name not in
  the dump does not exist. *(unenforced)*
- Pin `Godot.NET.Sdk`/`GodotSharp` to `4.8.0-dev.6`, restored from nuget.org (`nuget.config`). *(dotnet restore)*
- Hand-write a `.tscn` only for a one-node root; build everything else in code. *(unenforced)*

## GPU
- Every `Rid` from `RenderingDevice` is freed in `_ExitTree`/`Dispose`. *(unenforced — VRAM climbs across runs)*
- C# structs uploaded to std430 buffers are padded to 16 bytes per `vec3`/`vec4`; a spike test round-trips one
  buffer to prove the layout. *(spike round-trip test)*
- No per-frame managed allocation on the render path; spikes assert `GC.CollectionCount(0)` is flat. *(spike)*

## Verify
- New computer: `docs/godot/setup.md` (install, first run, machine rules). *(unenforced)*
- `dotnet test godot/Spayly.Sim.Tests` proves physics. Nothing else does.
- Pixels and frame times come only from `tools/run-capture.sh` (Xvfb + Vulkan on the real GPU; `--headless` is a
  dummy renderer). Check PNGs with `tools/shotcheck.py` before looking at them; compare before/after. *(unenforced)*
- Every spike ends with `RESULT.md` (measured p50/p95, screenshot paths) and a row in `docs/godot/decisions.md`.
- `Spayly.Game/textures/` is built by `tools/prepare-textures.mjs` (absolute symlinks into `public/textures/`, which is in
  Git LFS; hardlink or copy where Windows refuses symlinks) and never committed; spikes symlink `public/textures/`. *(`.gitignore`)*
