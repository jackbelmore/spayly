# M6 handoff: finish what M5 left undone or limited

M5 is verified on main (merge 8764e0c). The M5.1–M5.6 rows of the milestone table in `docs/godot/decisions.md` record what
was measured, and `docs/godot/verify-queue.md` lists what was left. The owner chose how each item should work (2026-09-25).
Those choices are written into the milestones below as requirements. Do not reopen them. If one turns out to be infeasible,
stop and ask.

## Setup
1. Create a worktree from the repo root: `git worktree add .claude/worktrees/m6 -b m6-finish main`. Git LFS checks out
   `public/textures/`.
2. Link main's textures and seed the import cache, so nothing re-imports (30 s, 2.3 GB RAM):
   ```
   ln -s <main>/godot/Spayly.Game/textures <wt>/godot/Spayly.Game/textures
   mkdir -p <wt>/godot/Spayly.Game/.godot
   cp -a --reflink=always <main>/godot/Spayly.Game/.godot/imported <wt>/godot/Spayly.Game/.godot/ > /dev/null
   ```
   `mkdir -p <wt>/godot/Spayly.Game/capture` before the first capture (run-capture.sh does not create the parent).
3. Read `godot/CLAUDE.md`, then `docs/godot/decisions.md`: the milestone table (M4, M5 rows) and the "render-side facts".
   The M5 capture folders are in main's ignored `godot/Spayly.Game/capture/m5-*`; the recipes are in their `run.log` headers.

## Rules (same as M5)
- **One Godot process at a time.** Launch only through `godot/tools/godot.sh` or `godot/tools/run-capture.sh`. Before
  each run, check `free -m`, `nvidia-smi` and `pgrep -fa -i "steamapps|proton|wine"`. A Steam client that is only open and
  idle is fine. If a game is running, stop running Godot and only compile.
- A step is done only when a `run-capture.sh` run backs it, checked with `godot/tools/shotcheck.py` and run.log greps.
  Use a capture verb that logs the number you need. "It compiles" is not evidence.
- `nice -n 19 dotnet test godot/Spayly.Sim.Tests` stays green: 236 tests at the start. The game build stays at 0 warnings.
  `PseudoNewtonianTests.SystemWithoutAHoleIsBitIdenticalToBeforePw` must keep passing: a system without a hole must not change.
- Every [SCI]/[GAME]/[ART] choice gets a `Realism.Register` entry (or a catalogue edit), and `realismSelftest` stays 8/8.
- Commit each milestone on `m6-finish` with a row of measured numbers in the milestone table of `docs/godot/decisions.md`.
  Never merge to main until its captures pass. Never run `git worktree remove --force`.
- `Spayly.Sim` never imports Godot. Render cheats never write back to `Body`.

## Milestones, in order

### M6.1 Undo for firing and erasing
The owner chose: both undoable.
- Firing an asteroid (hand aim or click-to-fire) records a forced undo point before the launch:
  `Universe.PushHistory(label, force: true)`.
- An erase drag records one undo point per stroke, not per rock.
- Undo pops them in order. `App.UndoImpact` already handles non-collision labels (see `HoleUndoLabel`). Give these
  labels the same treatment, so undoing a launch removes the rock by restoring the snapshot and deletes nothing else.
- Verify with `logState` hashes, as M5.1 did: fire, then undo, and the state is bit-identical to before firing. Erase a
  stroke, then undo, and the hash is restored.

### M6.2 Slow time when something is about to be torn apart or swallowed
The owner chose: slow time so the player sees it happen; they can speed up again afterwards if they want.
- A body can be about to reach a black hole's tidal radius, or its 2 r_s capture radius. If, at the current warp, it would
  cross within the next few frames, cap the warp so the crossing takes several frames.
- Show a hint: "Slowing time: Star 1 is about to reach Black hole 1". Log the warp before and after.
- Cap once per approach. If the player raises the warp again, respect it.
- Put the cap next to the cinematic cap in `App.Tick` (`Sim.Step`'s `warpCap`).
- Verify:
  - At warp 1e5–1e6, fire the Sun-like star at a 1e6 M☉ hole. The log shows the cap and `[tidal] full`; without the
    change, the star is swallowed whole.
  - Pressing ] after the cap takes the warp back up.

### M6.3 Launch speed: wider slider, with markers at interesting speeds
The owner chose: a bigger speed range, with markers where the speed would show interesting behaviour.
- The asteroid panel's Speed slider (`UI/Hud.Panels.cs`, today 1–316 km/s, log scale) reaches ~1e5 km/s.
- Draw markers (tick marks with tooltips) on the slider for the body under the pointer, or else the selected one, as seen
  from the camera:
  - the circular-orbit speed there;
  - the escape speed there. For a star aimed at a black hole, this is the classic tidal disruption: about half the gas
    escapes, as in `tdeDemo`.
  - For a star and a hole, the speed above which the star misses the tidal radius on a straight flyby is not a marker:
    auto-aim always aims to hit.
- Use the Paczyński–Wiita values near a hole (`PseudoNewtonian`).
- A marker tooltip says what that speed does in one plain sentence.
- Verify:
  - A capture verb logs the markers for a target.
  - Fire the star at the escape-speed marker. `[tidal] ... bound=` is ~0.5, where M5 got 1.0 at the 1000 km/s preset.

### M6.4 Black holes spiral in by radiating gravitational waves
The owner chose: yes, at the realistic rate.
- Hole pairs lose orbital energy and angular momentum at the Peters (1964) rate:
  - circular decay time t = (5/256) c⁵ a⁴ / (G³ m₁ m₂ M);
  - eccentricity decays too.
- Implement it in `Spayly.Sim` as a radiation-reaction term on the pair's relative motion, applied equal and opposite, so
  momentum stays exact. Two options:
  - the 2.5PN Burke–Thorne / Iyer–Will force;
  - an orbit-averaged drag fitted to Peters' da/dt and de/dt.

  Document which one you use and why.
- Only hole–hole pairs feel it. A system with no hole stays bit-identical.
- Do not double count: HLZ14's radiated mass (M5.4) is the whole merger's. Orbital energy lost in the inspiral is not rest
  mass. Say in a code comment how the two relate.
- Tests (new):
  - A circular 30 + 30 M☉ pair's separation follows Peters' a(t) to 1 % through a decade of shrinkage.
  - An eccentric pair's e falls as Peters predicts.
  - Momentum is conserved to rounding.
  - A no-hole system is bit-identical.
- Realism [SCI] entry.
- Capture: two holes on a tight orbit merge by themselves; log a(t), and the merger at the end.

### M6.5 Full HLZ14 remnant: spin, and the ISCO energy of the spinning remnant
The owner chose: yes, use the full formula.
- Add the remnant spin fit:
  - HLZ14 (arXiv:1406.7295) Eq. (15)/(16), spins zero; coefficients L0, L2d, L4f and the rest from Table VI. Check them
    against the paper: `pdftotext` works on the arXiv PDF, as M5.4 did.
  - Use Ẽ_ISCO of that spin in Eq. (14), with the Bardeen–Press–Teukolsky Kerr ISCO, instead of Schwarzschild's √(8/9).
- Store the spin on the remnant body (a hole's dimensionless spin a/M), carry it through snapshots and clones, and show it
  in the inspector.
- Rendering stays Schwarzschild: the lensing tables and disc do not use the spin. Say so in a Realism [GAME] entry.
- Tests:
  - Equal mass: the remnant spin is 0.6867 (L0) and M_rem/M stays 0.951507.
  - The test-particle limit still holds.
  - The q = 1/4 E_rad moves by under 1 % from the M5 value.

### M6.6 SPH: the surviving planet keeps a real spin
The owner chose: put the real spin into the collision.
- Before the SPH run, give each body's particles the body's rigid rotation, ω from its IAU rotation rate
  (`Body.Rotation`, `Render/Orientation.cs`). Add the spin angular momentum to the truth ledger, so the demotion's
  closure stays exact.
- After demotion, set each heir's rotation rate (and pole, if the model allows) from its ω = L/I. The report and inspector
  then show the new day length.
- Verify:
  - A test with a lone spinning planet and a negligible impactor demotes with the same period to 1 %.
  - The M5.6 Theia capture (`setSph:hero`, `setMode:asteroid`, `applyPreset:Theia`, `fireAt:earth`, warp 30) logs a
    plausible day: M5 got 9,623 h. Demotion closure stays ≲ 1e-15.

### M6.7 SPH cost: measure, then report
The owner chose: measure first, then decide.
- Re-run the M5.6 Theia capture with `--windowed` (no Xvfb floor) at Hero (1e5) and Fast (3e4). Record ms per substep
  p50/p95 over the run, and GPU frame time.
- Under Xvfb the cost grew from 6.5 to 21 ms per substep as the cloud spread. If it stays well above M4's 6.4 ms windowed,
  find out why: tree depth, neighbour counts, box size (`GravityBox`). Then write the options into verify-queue.md and
  **ask the owner before optimising**.

## Done means
- Each milestone has a row in the milestone table of `docs/godot/decisions.md`, with measured numbers only.
- `docs/godot/verify-queue.md` is updated: move done items out, and add new follow-ups.
- Merge `m6-finish` into main, check that the merge commit exists and the tests pass, and push.
- Copy the capture folders into main's `godot/Spayly.Game/capture/`. Only after that, remove the worktree, without `--force`.

## Still in verify-queue, not in scope unless the owner asks
The lensed disc's turbulence cost, and the `Sky.cs` / sunlight colours via `StarColor.MaxNormalised`. Also the
stellar-hole near field, which throttles the warp by design.
