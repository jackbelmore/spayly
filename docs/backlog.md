# spayly backlog

Split out of `design.md` so that file stays a durable decision record. Everything here is provisional:
it goes stale, and nothing in a `CLAUDE.md` should ever cite it.

## Known limitations / open

- The camera stays outside the cloud layer; no surface-level views yet.
- Clouds do not react to impacts; dust is a global tint that builds over ~12 h.
- Crater decals are painted into an equirectangular canvas (distorted near the poles; minimum ~2 texels).
- Tidal disruption (Roche limit), planet-planet grazing collisions and rotation changes are not modelled.
- Sound: none yet.
- Textures are in Git LFS; CI does not fetch them (`lfs: false`), so its `dist` artifact ships pointer stubs and
  falls back to procedural textures.
- The worker texture pipeline has only ever run in Chromium. Two paths exist but are untested: the
  Firefox fallback for maps over its canvas limit, and recovery after a lost GPU context.
- Umbriel and Triton show a seam at 0°. It is in the Voyager imagery itself, not the pipeline, so the
  asset guard tolerates it deliberately — accepted, not outstanding.

- **Engine question is open.** The web build's physics ceiling is algorithmic, not the language: all-pairs
  gravity is 282 ms/step at 10,000 bodies in JS on a Ryzen 7 5800X, while a debris tier that feels the
  planets but not other debris is 5.9 ms/step. Godot 4.8 (.NET, GLSL compute) is the leading candidate
  for a desktop release with GPU debris and SPH planet collisions; on hold while the owner researches it.

## Ideas queued

- Gemini-generated assets for gaps (crater ray textures, fragment surfaces, UI art).
- Lambert-solver targeting with a chosen flight time; multi-asteroid "rain" (done in the Godot build as meteor showers, `App.Shower.cs`).
- Roche-limit breakup into rings; black hole / rogue star toys.
- Sound design (impacts, UI), GIF/video capture, shareable scenario URLs.
- WebGPU renderer once the custom shaders are ported to TSL.

## Found while porting to C# (web build frozen; fix in both or neither)

- `impact.ts` `bindingRatio` is documented as using deposited energy but divides the entry energy.
- `craterDimensions` switches simple→complex when the *transient* diameter passes 3.2 km; Collins et al. define the
  threshold on the *final* diameter (so the switch happens near a final diameter of ~4 km). The C# port copies the TS.
- `collide.ts` (`mergeInto`, `fragment`) writes `mass0`/`radius0`, contradicting the "written once in makeBody" rule in
  `src/sim/CLAUDE.md`; reset-body therefore restores the post-merge mass. The C# port copies the behaviour
  (`Body.Mass0` is `internal set`) — decide which is intended and fix the rule or the code in both builds.
- `frames.ts` `eclipticToPole` can return RA = 360 exactly for a tiny negative atan2 result. Cosmetic.
- `collide.ts` fragment ids are `<target>-frag-<floor(time)>-<k>`: a remnant shattering twice within one integer
  second (a cascade) repeats ids and RNG seeds, and `byId` overwrites. Fragment placement keeps the last direction
  after 24 failed tries and never checks against the remnant. `waterDepth` is passed even when `surfaceAt` says dry.
- `aim.ts` `predictPath` spends one step's pair evaluations before checking the budget, so an `autoAim` near
  `AIM_PAIR_BUDGET` can overrun by up to k²/2 + 2n. No test reaches it.
- `universe.ts`: the pair-budget check is skipped for the first 8 substeps, so a tiny budget can overrun past
  max + n(n−1)/2; `resolveCollisions` dedupes by id, not object identity; test particles get no acceleration after
  `populateBelts` (first half-kick is zero) and integrate against end-of-frame body positions; undo does not restore
  particles. `catalog.ts`: Pluto's μ uses Sun + Pluto only while planets use their whole system.
- **`gapFill.ts` bug (found by `godot/tools/prepare-textures.mjs`)**: when the pyramid's coarsest level is one row
  high, the push step's `Math.min(src.h - 1.001, Math.max(0, …))` yields −0.001, reads row −1, the weights go NaN and
  `Uint8ClampedArray` stores 0 — the hole stays black. Charon at 6000 px is 33.9 % black in the web build once the full
  map streams in; parts of Vesta/Pallas too. The Godot tool swaps the two clamps when it loads the module. Fix in
  `src/render/gapFill.ts` when the web build is next touched.

## Godot build (M4) — open

- LS12 is gravity-only: sub-km rocks pulverise at m/s speeds in `OutcomeModel.LS12`. Needs a strength term (Benz–Asphaug
  Q*_D blend) before the game switches the default from Legacy.
- The game has no branch for `OutcomeKind.HitAndRun` yet (only reachable in LS12 mode).
- `Ls12DebrisFloor` (1 % of the projectile mass) decides which debris becomes bodies vs particle tier; untuned.
