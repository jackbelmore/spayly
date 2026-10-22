# M5 handoff: make black holes, tidal disruption and SPH playable, with realistic physics

M4 is verified on main, but three of its features can only be reached through capture-harness verbs: `spawnBH`,
`tidalDemo`/`tdeDemo` and `sphDemo` (`App/App.Capture.cs`). M5 makes them part of normal play and tightens the
physics near black holes. What was found on main at 3c559b8:
- **Black holes:** nothing in the HUD can create one. Lensing, disc and Doppler have no switches. A spawned hole
  co-moves with a body. Gravity is purely Newtonian, so bodies plunge in at more than c (10× c has been seen). A
  hole–hole merger is a plain inelastic merge. Disc brightness is [ART], not fed by accretion, and one shared value makes
  every disc flare together during a TDE.
- **Tidal disruption:** it runs automatically (`Settings.Tidal = true`, checked every 10 frames), but it has no HUD
  toggle and no way to get a star to a hole in normal play.
- **SPH:** `SphSettings.Enabled` is static and false (`App/App.Sph.cs:22`), with no HUD control. `SphGate` (`:100`)
  needs the camera focused on one of the two bodies. `DebrisSink` is never assigned, so the remainder always goes to a
  logged reservoir instead of the debris tier.
- **Undo:** placing a hole (or launching or erasing) records no history.

## Setup
1. Create a worktree from the repo root: `git worktree add .claude/worktrees/m5 -b m5-gameplay main`. Git LFS checks
   out `public/textures/`.
2. `godot/Spayly.Game/textures/` is built locally (symlinks to absolute paths) and is not in git. Link main's copy and
   seed the import cache, so nothing re-imports (30 s, 2.3 GB RAM):
   ```
   ln -s <main>/godot/Spayly.Game/textures <wt>/godot/Spayly.Game/textures
   mkdir -p <wt>/godot/Spayly.Game/.godot
   cp -a --reflink=always <main>/godot/Spayly.Game/.godot/imported <wt>/godot/Spayly.Game/.godot/ > /dev/null
   ```
3. Read `godot/CLAUDE.md`. Then read `docs/godot/decisions.md`: the milestone table and the "render-side facts" section,
   which has the reverse-Z projection and the float32-in-SI traps.

## Rules
- **One Godot process at a time.** Always launch through `godot/tools/godot.sh` or `godot/tools/run-capture.sh`.
  Check `free -m` before each run. If a game or Steam is using the GPU, stop running Godot and only compile.
- A step is done only when a `run-capture.sh` run backs it, checked with `godot/tools/shotcheck.py` and run.log greps.
  "It compiles" is not evidence: in M4 it hid five showstoppers.
- `nice -n 19 dotnet test godot/Spayly.Sim.Tests` stays green, and the game build stays at 0 warnings.
- Every [SCI]/[GAME]/[ART] choice gets a `Realism.Register(feature, tag, why, area)` entry
  (`Realism/Realism.cs:227`).
- Commit each milestone on `m5-gameplay`. Never merge to main until its captures pass.
- Never run `git worktree remove --force`.
- `Spayly.Sim` never imports Godot (`LayeringTests`). Render cheats never write back to `Body`.

## Milestones, in order

### M5.1 Black-hole placement mode (UI; the physics stays as it is)
- Add `Mode.BlackHole` (`App/Settings.cs:7`), bound to key **B** (free). Add a `KeyBtn` in the bottom mode bar
  (`UI/Hud.Panels.cs:407`) and a panel built like `asteroidPanel`, shown and hidden in `Hud.SetMode` (`:500`).
- The panel has:
  - A mass slider on a log scale, 3 M☉ to 1e10 M☉.
  - Presets: stellar 10 M☉, Cygnus X-1 21 M☉, IMBH 1e4 M☉, Sgr A* 4.3e6 M☉, M87* 6.5e9 M☉.
  - A velocity choice: "circular orbit about the dominant body" (default), "at rest relative to it", or "drag to set".
  - A readout of r_s, the tidal radius for a Sun-like star and for the Earth, and the Hill sphere at the pointer.
  Format numbers with `UI/Format.cs`.
- Click flow: copy the asteroid path.
  - Pointer handling is in `App.Input.cs` (PointerDown `:72`, Move `:88`, Up `:116`).
  - `AimPlanePoint` (`:237`) intersects the pointer ray with the plane through the camera target, in absolute sim km.
  - `App.Aim.cs` has `StartAim`, `UpdateAim` (drag-to-speed) and `Aim.PredictPath` for the preview.
  - Spawn through `SpawnBlackHole` (`App.BlackHole.cs:32`). Generalise it to take an absolute position and velocity;
    today it takes an offset from a named body.
- Undo:
  - Add a force flag to `Universe.PushHistory` (`Spayly.Sim/Universe.cs:351`) that skips the 30-step throttle.
  - Push "before black hole" on placement.
  - Offer "Undo" as a button and as the existing undo path (`App.UndoImpact`, `App.cs:235`).
- View panel (`Hud.Panels.cs:225`):
  - Add checkboxes for Lensing, Accretion disc and Doppler (setters at `App.BlackHole.cs:101-109`), and for Tidal
    disruption (`App.SetTidal`, `App.Tidal.cs:95`).
  - Add each to `SyncToggles` (`:300`) and `ApplyRealismTooltips` (`Hud.Realism.cs:221`).
- Inspector (`Hud.Inspector.cs`): for a hole, add a "Remove" button, and disc and lensing rows beside the r_s, ISCO and
  shadow rows (`:146`).
- Add help rows (`Hud.Overlays.cs:16`) and a mode hint (`App.cs:192`).
- Placing a fifth hole shows a hint that it won't be lensed (`LensingPass.MaxHoles = 4`).
- Capture verbs: `setMode:blackhole`, `bhMass:<M☉>`, `bhVelocity:orbit|rest`, `placeBH:<px x>,<px y>` (a click at
  screen px, through the same input path). `logInspector` and `setMassSlider` already exist.
- Verify:
  - Place a hole 1 AU from the Sun in a circular orbit. Over one orbit its period matches Kepler within 1 %, and
    `logBH` / `logInspector` show the right values.
  - Undo it, and every body's state returns exactly.
  - Shots with lensing on and off show the shadow in both.

### M5.2 Pseudo-Newtonian gravity near holes (Sim)
- For every gravitating pair that includes a hole, replace Newton with **Paczyński–Wiita**:
  a = G M / (r − r_s)² r̂, where r_s is the hole's.
  - The pair force stays equal and opposite, so momentum stays exact, and energy is conserved against
    Φ = −GM/(r − r_s).
  - It reproduces the ISCO at 3 r_s and the marginally bound orbit at 2 r_s, where circular speed reaches c.
  - Soften or clamp the r ≤ r_s singularity; capture removes anything that gets there anyway.
- Capture a body or particle on crossing **2 r_s** (the marginally bound radius), not r_s.
  - `Collide` and the contact test need a hole's capture radius.
  - The horizon radius `Body.Radius = r_s` stays what the renderer and inspector show.
- Change every place that computes gravity, and doc-comment each site:
  - the N-body force in `Spayly.Sim` (`Universe` gravity)
  - `Aim.PredictPath`
  - the debris GPU integrate kernel (`Render/DebrisTier.cs`): the body table needs r_s per body (0 for non-holes) plus
    the capture radius
  - `Particles.cs`, if the belt particles can see holes
  - Tidal's energy terms (`Tidal.Disrupt` uses GM/d)
- Tests (new `PseudoNewtonianTests.cs`):
  - a circular orbit at 10 r_s has the PW period;
  - a slightly perturbed orbit at 2.8 r_s plunges, while one at 3.5 r_s stays bound;
  - a radial plunge from rest at 1000 r_s reaches 2 r_s below c and is captured;
  - a hole plus body conserves momentum exactly and PW energy to 1e-9 over 1e4 steps;
  - a system with no hole is bit-identical to before (compare against a stored state).
- Realism [GAME]: "Pseudo-Newtonian gravity near black holes". Replace any entry that says gravity is Newtonian
  everywhere.
- Re-run the M4 black-hole capture (`spawnBH:10,mars`; `docs/godot/decisions.md` has the numbers). The expected
  `a_expected` changes to the PW value, so update `logBH`.

### M5.3 Accretion-fed, per-hole discs
- Each hole keeps its own accretion rate Ṁ(t), a sum of:
  - mass it absorbs: debris ledger `Absorb`/`Accrete` into a hole, and `Collide` merges into a hole;
  - TDE fallback (`Tidal.FallbackRate`).
- Spread each deposit over a viscous time [GAME].
- Luminosity L = ε Ṁ c² with ε = 0.057 (Schwarzschild), capped at `BlackHole.EddingtonLuminosity`.
- Drive disc brightness and display temperature from L. With no feed the disc fades out, which is realistic: an
  isolated hole is dark apart from lensing.
- Move `DiscBrightness` off the shared `BlackHoleOptions` onto each hole; `LensingPass.Hole` already carries it per
  hole. A TDE then flares only its own hole, so `UpdateTdeFlare` in `App.Tidal.cs` changes too.
- Replace the [ART] "Disc size and brightness" Realism entry with the new model.
- Verify: fire rocks into a hole and see its disc brighten, then decay. A second hole nearby stays dark.

### M5.4 Hole–hole mergers
- In `Collide`, when both bodies are holes:
  - The remnant loses the energy radiated in gravitational waves. Use a published non-spinning fit, e.g. Healy, Lousto
    & Zlochower 2014. Check the coefficients against the paper; about 4.8 % of the total mass at equal mass.
  - The remnant gets a recoil kick v = A η² √(1 − 4η) (1 + B η), with A = 1.2e4 km/s and B = −0.93 (González et al.
    2007), in the orbital plane.
  - Book the radiated mass in the debris ledger as an external loss, so the book still closes.
- Optional: Peters (1964) GW inspiral da/dt for hole pairs closer than about 1000 r_s, so bound pairs merge
  realistically.
- Tests:
  - an equal-mass remnant is about 0.952 of the total mass;
  - the kick is 0 at q = 1 and peaks at about 175 km/s near η ≈ 0.195;
  - momentum including the kick is booked;
  - the ledger closes.
- Realism [SCI].

### M5.5 Tidal disruption and TDEs in play
- Add the Tidal View toggle from M5.1.
- Add a launchable "Sun-like star" (1 M☉, R☉) to the asteroid presets or composition list. It goes through the
  existing aim path, so a TDE happens through the automatic `TidalCheck`, not `tdeDemo`.
- Make the stream visible from normal framing: TDE particles get a minimum pixel size (the debris point shader), and
  the cinematic camera frames the stream. This is the follow-up in `docs/godot/verify-queue.md`.
- Show a HUD notice when a disruption happens (`TidalFlash`), with β, stripped fraction and bound fraction.
- Verify: place a 1e6 M☉ hole in M5.1 and launch the star at it through the UI verbs only. `[tidal] full` is logged,
  the stream is visible (a non-black delta against the pre-shot), and only that hole flares.

### M5.6 SPH giant impacts in play
- Replace the static `SphSettings.Enabled` with a `Settings.Sph` select in the View panel: Off, Fast (30k) or Hero
  (100k). M4 measured 1e5 particles at 13.0 ms a frame and 3e4 at about 4.5 ms.
- Relax the `SphGate` focus rule (`App.Sph.cs:100`): a collision qualifies when either body is focused **or** the
  cinematic camera will fly to it.
- Assign `DebrisSink` to the debris tier so the SPH remainder becomes particles. The ledger closure must stay ≲ 1e-15.
- Show a HUD progress line during a run: sim time, wall-clock budget and particle count.
- Verify: through UI verbs only (`setMode:asteroid`, the Theia preset, `fireAt:earth`, no `sphDemo`), an SPH run
  starts, demotes into bodies and debris, and logs the ledger closure.

## Done means
- Each milestone has a row in the milestone table of `docs/godot/decisions.md`, with measured numbers only.
- `docs/godot/verify-queue.md` is updated.
- Then merge `m5-gameplay` into main, check the merge commit exists and the tests pass, and push.
- Only after that, remove the worktree, without `--force`.
