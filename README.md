# spayly

[![CI](https://github.com/jackbelmore/spayly/actions/workflows/ci.yml/badge.svg)](https://github.com/jackbelmore/spayly/actions/workflows/ci.yml)

A photoreal solar-system sandbox in the browser with real physics: the Sun, the eight planets, twenty-three
moons, five dwarf planets and the two largest asteroids, every one of them pulling on every other. You can
change the mass and size of anything (including the Sun) while it runs, and an asteroid mode lets you throw
rocks at any of it — planets, moons, or rocks you threw earlier — and watch physically-derived consequences,
from a Chelyabinsk airburst to shattering Earth.

A native port in Godot 4.8 (C#, GPU SPH collisions, black holes) lives in `godot/`; to set it up on a new computer see
[`docs/godot/setup.md`](docs/godot/setup.md).

## Play the Godot build

One command takes a computer with nothing set up to the game running. It needs git, git-lfs, Node 22+ and the .NET SDK 10+
(it tells you which are missing, with the install line for your OS, and installs none of them). It downloads Godot itself.

```bash
# macOS / Linux
curl -fsSL https://raw.githubusercontent.com/jackbelmore/spayly/main/bootstrap.sh | sh
```
```powershell
# Windows (PowerShell)
irm https://raw.githubusercontent.com/jackbelmore/spayly/main/bootstrap.ps1 | iex
```

Either clones into `./spayly` (set `SPAYLY_DIR` to choose another folder) and runs the launcher below.
`bootstrap` is fetched from `main` and run as it arrives, so read it before you pipe it into a shell.

Manual route:
1. Install git, git-lfs, Node 22+ and the .NET SDK 10+. `start` checks all four, lists whatever is missing, and installs none of them.
   - Windows: `winget install Git.Git GitHub.GitLFS OpenJS.NodeJS.LTS Microsoft.DotNet.SDK.10`, then open a **new** terminal.
   - macOS: `brew install git git-lfs node dotnet-sdk`
   - Fedora/RHEL: `sudo dnf install git git-lfs nodejs dotnet-sdk-10.0 unzip`
   - Debian/Ubuntu: `sudo apt install git git-lfs unzip dotnet-sdk-10.0`, plus Node 22+ from [nodejs.org](https://nodejs.org/en/download) if your distro's is older.
2. `git lfs install`, then `git clone https://github.com/jackbelmore/spayly && cd spayly`. Do the LFS step first, or the maps arrive as pointer files (`start` repairs that with `git lfs pull`).
3. Run `./start.sh`, or double-click `start.cmd` on Windows.

The first run pulls `main`, fetches the maps, runs `npm ci`, downloads Godot 4.8 (.NET) to `~/.local/opt/`, then builds and imports (a few
minutes). Later runs skip every step that is up to date, and launch in seconds. Flags: `--no-pull`, `--rebuild`, `--prepare-only`,
`--dry-run`; anything after `--` goes to Godot. Details and Windows notes (no Godot lock; maps hardlinked or copied where symlinks are
refused): [`docs/godot/setup.md`](docs/godot/setup.md).

## Getting started

```bash
npm install
npm run assets      # downloads CC-licensed and public-domain body maps into public/textures
                    # (optional; procedural fallbacks exist for anything missing)
npm run dev         # http://localhost:5173
npm test            # physics test-suite (vitest)
npm run typecheck
npm run ephemeris   # only when the JPL numbers in src/sim/satellites.ts should be refreshed
```

Chromium-based browsers with WebGL2 are the target. Press `H` in the app for the control sheet.

## Controls

| | |
| --- | --- |
| drag · wheel | orbit · zoom; once at a body's surface the wheel keeps zooming by narrowing the lens |
| `W` `A` `S` `D` | pan the view, holding shift to go faster; panning keeps following whatever you are following |
| `Q` `E` | swing the camera around what it is looking at |
| click · double-click | select a body · fly to it, either on the body or on its name in the view |
| space · `[` `]` · `R` | pause · slower, faster · rewind |
| `X` · `Z` | asteroid mode · erase mode (drag to delete asteroids and fragments, wheel resizes the brush) |
| `F` · `T` · `1`–`9`, `0` | fly above the selection · tabletop view · Sun, Mercury … Neptune, Pluto |

## What is real

- **Gravity.** Full mutual N-body integration with a symplectic kick-drift-kick leapfrog. Substeps adapt to the
  shortest dynamical timescale (close encounters get small steps); a per-frame cap throttles the time warp rather
  than skipping physics ("physics-limited" in the status bar). Time can run backwards.
- **Initial conditions.** Standish mean planetary elements (JPL), the Moon from Meeus' mean elements, the
  satellites from JPL's published mean elements referred to their local Laplace planes, and the dwarf planets
  and asteroids from the JPL Small-Body Database — all propagated to today's date, with each planet's system
  barycentre on the ephemeris position and the whole system's barycentre at rest. IAU rotation poles and
  prime-meridian rates give correct axial tilts and day/night; the tidally locked moons keep the face the sky
  sees turned towards their planet, Triton included, which goes round Neptune backwards.
- **Contacts.** Detection is swept, not sampled: within a substep a pair's relative motion is exactly a straight
  line, so the closest approach along it is solved for directly. A 20 m rock closing at 20 km/s covers 5 km in
  one substep and would otherwise pass clean through another 20 m rock.
- **Impacts.** Collins, Melosh & Marcus (2005): pancake-model atmospheric entry and airbursts, pi-group crater
  scaling, fireball, thermal radiation, seismic magnitude, ejecta blanket, air blast, tsunamis when Earth's oceans
  are hit. Merges conserve momentum exactly; impacts above a fraction of the target's gravitational binding energy
  shatter it into fragments that become real bodies (and re-accrete under their own gravity). What counts as
  shattering follows the regime: self-gravity above a few kilometres, and the Benz–Asphaug (1999) basalt
  strength law below it, so a boulder shrugs off a nudge that would tear a moon apart. Fragments can be
  shattered in turn, so a hard ceiling of 240 bodies keeps a cascade from breeding without end — past it a
  disrupting hit melts its target instead of scattering it, and mass and momentum still balance exactly.
- **Stars.** The Sun's luminosity and colour follow its mass (main-sequence relation) and radius; planets' equilibrium
  temperatures respond, and the shader shows Earth freezing or scorching.
- **Cheats that are visual only.** Diorama scale enlarges bodies so the whole system reads on screen; the minimum
  on-screen size keeps distant planets visible; sunlight falloff can be softened for legibility. None of it touches
  the simulation. Where a moon's published map has never-imaged regions — Voyager 2 only saw the southern halves
  of the Uranian moons — the loader grows the imaged terrain over them so the body is not rendered with a hole in
  it. That part is invented, and `public/textures/CREDITS.md` says so.

## Texture detail

Maps stream with the camera. A body far away is decoded at 1024 px; as it grows on screen the view
asks for the next power of two, up to the *Texture detail* ceiling in the View panel, and the large
map is released once the camera moves on. The ceiling caps the peak rather than forcing every body
to that size, and the options on offer are trimmed to what the GPU reports it can hold.

The sizing rule is one texel per pixel: you see 180 degrees of longitude at once, half the map,
across a disc two radii wide, so a map about four times the on-screen radius is 1:1.

How much a big map can ever help is set by the camera, not the file. The camera stops at a body's
surface, so on a 1080p screen at the default 50-degree lens a body never exceeds about 1150 px in
radius, and nothing past 8192 can show. That is why zooming past the surface stop narrows the lens
instead: at 20 degrees the same body reaches 3062 px and a 16384 map is finally 1:1.

Earth and the Moon come from the largest published sources — the 21600 px Blue Marble and NASA's
16384 px LROC colour mosaic — capped at 16384, which is the widest single texture almost every
desktop GPU accepts. Past that you need tiling, which is not implemented; at that point the limit is
the imagery rather than the pipeline.

## Time warp and the moon count

Substeps track the shortest dynamical timescale in the system, and Phobos goes round Mars in under eight hours.
With every moon in, the integrator resolves about 2 × 10⁶× real time before the status bar reads
"physics-limited"; on **Major only** it is about 3 × 10⁷×, and with **None** the slider's full 10⁸× is reachable.
The *Moons* control in the View panel is there for exactly that trade — watching Neptune go round once takes a
while otherwise. Nothing is faked either way: the warp is throttled, never skipped, and the status bar says
"limited" while it is happening.

A fresh cloud of fragments costs warp the same way for a second or two, because a dozen hundred-kilometre rocks
a few hundred kilometres apart genuinely do need short steps. Once they spread out the warp comes back. Erase
mode (`Z`) clears debris you are done with.

## Layout

```
src/sim       physics: constants, vectors, Kepler math, ephemeris, reference frames, catalog,
              satellites.ts (generated from JPL), N-body universe, belts, impacts, collisions, aiming
src/render    three.js views: planets (atmospheres, clouds, rings, eclipses, craters), Sun, rocks, sky, belts, lines, effects, camera
src/ui        HUD wiring and number formatting
src/app       the application: loop, input, modes (explore, asteroid, erase), cinematic camera (app.ts);
              shared config types the HUD also uses (types.ts)
tests         vitest physics tests (Kepler, energy conservation, reversibility, impact model, collisions,
              auto-aim) plus layering.test.ts, which enforces the import law between the four directories
scripts       fetch-assets.mjs (textures), fetch-ephemeris.mjs (regenerates src/sim/satellites.ts from JPL),
              shoot.mjs (headless screenshot + console harness for visual iteration)
docs          design.md (durable decisions), backlog.md (open gaps and queued ideas)
```

## Visual iteration

`node scripts/shoot.mjs http://localhost:5173/ out '[{"name":"x","js":"spayly.viewTabletop()","wait":3000}]'`
drives a headless Chrome through the app, saves screenshots and prints console errors. `window.spayly` exposes the
running app for scripted scenarios.

## Credits

Textures: Solar System Scope (CC BY 4.0), NASA/JPL/USGS/SSI mosaics via Wikimedia Commons (public domain),
Björn Jónsson and Askaniy (CC BY / CC BY-SA), three.js examples (MIT). Per-body sources and licences are in
`public/textures/CREDITS.md`.

Ephemerides: JPL Solar System Dynamics — planetary satellite mean elements and physical parameters — and the
JPL Small-Body Database.

Impact model: Collins, Melosh & Marcus, *Earth Impact Effects Program*, Meteoritics & Planetary Science 40 (2005).
Disruption thresholds: Benz & Asphaug, *Catastrophic disruptions revisited*, Icarus 142 (1999).
