# Verification queue

M6 (undo for firing and erasing, slowing time near holes, launch-speed markers, Peters inspiral, HLZ14 remnant spin, SPH
spin and cost) was verified on branch `m6-finish` on 2026-09-25; the measured numbers are the M6.1–M6.7 rows of the
milestone table in `docs/godot/decisions.md`. M0–M5 are verified on main (M5 merge 8764e0c).

## M7 (2026-09-25): verified on `m7-undo`

Ctrl+Z / redo for every action with full-state points, steady SPH substeps and SPH runs that end on the physics: the
M7.1–M7.5 rows of the milestone table. Recipes: `docs/godot/m7-verify-recipes.md`.

## Waiting for the owner

- **The SPH outcome still varies ~2 % run to run in the heir's day** (M7.5). Frame rate no longer matters: Xvfb and
  windowed stop within one checkpoint, mass agrees to 0.4 %. But two identical windowed runs gave 31.96 h and 31.37 h,
  because the GPU's atomic sums add in a different order each run and the collision is chaotic. Options: (a) accept
  ~2 % and say so in Realism; (b) deterministic sums (fixed-point accumulation or a sorted reduction), which costs
  some SPH speed and needs its own measurement.

## Follow-ups found in the M7 run (not blocking)

- **Not yet run together:** M7.6–M7.8 were each verified on their own branch; the merged main is covered by the 279
  tests and a clean build only. The owner asked to stop window-opening runs. Re-run the M7 recipes under Xvfb
  (`docs/godot/m7-verify-recipes.md`, without `--windowed`) on the next session, and windowed timing only when the
  owner agrees.
- **Undo during SPH still costs one ~89 ms frame:** Theia had no view (added and swapped in the same frame), so undo
  builds its procedural map on the main thread (66 ms). Build it on a worker when SPH starts.
- **`App.Textures.cs` runs `nvidia-smi` on the main thread** for a diagnostic line at frames 120–850: 25–38 ms each,
  in every session. Move it to a worker or to captures only.
- **Spawning a black hole costs a one-off ~45 ms frame** (seen at `tdeDemo`); not investigated.
- **The first impact and the camera's texture streaming after it** give 40–90 ms frames (any impact).
- **Idle undo copy buffers** (up to 32 MB pool) are logged but not counted in the 256 MB history VRAM.
- **Meteor shower:** its landed impacts record no undo points while the shower is the newest point, so one Ctrl+Z
  takes the whole volley back (M7.2 would give each collision its own point). Owner may prefer either.

## Follow-ups found in the M6 run (not blocking)

- **The Xvfb cost growth does not happen windowed.** Under Xvfb the Hero substep went 6.5 → 21 ms as the cloud spread.
  Windowed it stays 6.3–7.9 ms all the way to t = 9,143 s (box 12,572–14,736 km). The guess is that the GPU clocks down
  between Xvfb's ~17 fps frames. That is not verified (sample `nvidia-smi -q -d CLOCK` during an Xvfb run). Measure SPH
  cost windowed only.
- **The game's SPH bodies are unrelaxed** (`HeroOptions.RelaxSteps` 0). A lone planet's moment of inertia grows 1.4 % in
  1800 s, so its day comes out +1.5 % long (`SphHeroTests`: relaxed 100 steps, −0.65 %). Relaxing on the CPU costs
  seconds at 10⁵ particles; a GPU relax pass would not.
- **Adaptive leapfrog drifts e on eccentric orbits.** The substep follows the dynamical time round the orbit, which is
  neither symplectic nor reversible. With the inspiral on, at the default DynFraction 0.02, e fell 7 % faster than Peters
  over ~4.5 × 10⁴ orbits from 600 r_s; at 0.005 the residual is PW's own. It matters only over 10⁴+ orbits. A
  time-symmetric step (Hut, Makino & McMillan 1995) would fix it.
- **Head-on plunges gain energy from the 2.5PN force.** It does positive work on purely radial motion in every gauge of the
  Iyer–Will family. A 30 + 11 M☉ pair falling from rest at 41 r_s touches at 1.0004 c (0.9883 c before);
  `MergerTests` allows ≤ 1.001 c.
- **A tight hole pair with the moons on is warp-limited.** 365 substeps × 820 pairs fill the per-frame budget, so the sim
  advances about 1.5 s per frame. With `setMoons:none` the pair merges in ~60 frames at warp 300.
- **Stellar-hole near field.** `Universe.HoleMinSubstep` (1e-6 s) lets a pair with a hole resolve its millisecond orbits.
  While a body orbits within a few r_s of a stellar-mass hole, the warp is throttled ("limited").
- **Lensed disc cost.** At 40 r_s, frame GPU time is 8.46 ms with lensing and 2.82 ms without, and 0.49 ms with lensing
  but no disc (M4). The cost is the per-crossing fbm turbulence in `LensingShader.Lens` (2 layers × 5 octaves). Bake it
  into a small polar texture per frame, or drop octaves when the disc is small on screen. Unfed discs are dark and skip
  the cost.
- **Stars.** Switch `Sky.cs` stars and the `App.Sun.cs` sunlight colour from `RenderUnits.Blackbody` to
  `StarColor.MaxNormalised`.
- **Known limits, recorded rather than fixed:**
  - SPH mass drift is ~3e-8 because particle masses are float32 on the GPU. The remainder absorbs it exactly.
  - A partially disrupted core keeps its [GAME]-shrunk radius, so the Moon's density goes from 3,344 to ~4,370 kg/m³.
  - A merged hole's spin (M6.5) is only reported. Gravity, horizon size, lensing and disc stay Schwarzschild [GAME].
  - The slow-down near holes (M6.2) uses two-body times. A third body can make the event come earlier than predicted.
