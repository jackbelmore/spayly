# M7 handoff: Ctrl+Z undoes every action, and SPH that is steady and ends on physics

M6 is verified on main (merge 21170ab). The M6 rows of the milestone table in `docs/godot/decisions.md` record what was
measured, and `docs/godot/verify-queue.md` lists what was left. The owner chose how each item should work (2026-09-25).
Those choices are written into the milestones below as requirements. Do not reopen them. If one turns out to be
infeasible, stop and ask.

The owner's goal decides every trade-off here: the most fun simulator, as realistic as it can be, that never crashes and
never drops below 30 fps (frame p95 ≤ 33 ms).

## Setup
1. Create a worktree from the repo root: `git worktree add .claude/worktrees/m7 -b m7-undo main`. Git LFS checks out
   `public/textures/`.
2. Link main's textures and seed the import cache, so nothing re-imports (30 s, 2.3 GB RAM):
   ```
   ln -s <main>/godot/Spayly.Game/textures <wt>/godot/Spayly.Game/textures
   mkdir -p <wt>/godot/Spayly.Game/.godot <wt>/godot/Spayly.Game/capture
   cp -a --reflink=always <main>/godot/Spayly.Game/.godot/imported <wt>/godot/Spayly.Game/.godot/ > /dev/null
   ```
3. Godot is `godot-dev` (`~/.local/opt/godot-dev/godot-dev`, 4.8 dev6 mono). `tools/godot.sh` launches it and holds the
   one-process lock. Never start `godot` or `godot-dev` directly.
4. Read `godot/CLAUDE.md`, then `docs/godot/decisions.md` (the M5 and M6 rows). Capture recipes are in the `run.log`
   headers of main's ignored `godot/Spayly.Game/capture/m5-*` and `m6-*` folders.

## Rules (same as M6)
- **One Godot process at a time.** Before each run, check `free -m`, `nvidia-smi` and
  `pgrep -fa -i "steamapps|proton|wine"`. ProtonVPN matches that pattern but is not a game. A Steam or Lutris client
  that is open and idle is fine. If a game is running, stop running Godot and only compile.
- A step is done only when a `run-capture.sh` run backs it, checked with `tools/shotcheck.py` and run.log greps. Measure
  SPH cost and frame times `--windowed`: Xvfb inflated the SPH substep cost from 6.5 to 21 ms, and windowed it stays at
  6.3–7.9 ms.
- `nice -n 19 dotnet test godot/Spayly.Sim.Tests` stays green: 247 tests at the start. The game build stays at
  0 warnings. `PseudoNewtonianTests.SystemWithoutAHoleIsBitIdenticalToBeforePw` must keep passing.
- Every [SCI]/[GAME]/[ART] choice gets a `Realism.Register` entry, and `realismSelftest` stays 8/8.
- Commit each milestone on `m7-undo` with a row of measured numbers in the milestone table. Never merge to main until
  its captures pass. Never run `git worktree remove --force`.
- `Spayly.Sim` never imports Godot. Render cheats never write back to `Body`.

## What undo does today
- **No keyboard shortcut.** `App.Input.cs:58` drops every key pressed with Ctrl, Alt or Meta. Undo is only the "Undo"
  buttons in the black-hole panel (`UI/Hud.BlackHole.cs:62`) and on the report card (`UI/Hud.Report.cs:59`). Both call
  `App.UndoImpact`.
- **A snapshot holds only the bodies.** `Universe.PushHistory`/`PopHistory` keep 8 snapshots of the body list
  (`Body.Clone`). A snapshot does not hold:
  - the debris tier's GPU particles and its `DebrisLedger`;
  - per-hole accretion reservoirs and tidal fallback flares;
  - tidal streams, which are debris particles;
  - `SphReservoir` and an SPH run in flight;
  - moon detail, and the selected and focused ids.

  So undoing a hole that disrupted a star leaves its stream flying.
- **Actions that already record an undo point:** collisions (non-forced, one per cascade), tidal disruptions, hole
  placement and removal, launches, and erase strokes (M6.1).
- **Actions that record none:**
  - the inspector's mass and radius sliders (`UI/Hud.Inspector.cs:64,72` write `b.Mass`/`b.Radius` directly);
  - `ResetBody` (`App.cs:219`);
  - `SetMoonDetail` (`App.cs:283`), which adds and removes moons;
  - the hole mass slider acting on a placed hole;
  - an SPH demotion, which replaces the pair with its heirs.

## Milestones, in order

### M7.1 Ctrl+Z undoes, Ctrl+Shift+Z redoes
The owner chose: Ctrl+Z undoes any action.
- Handle Ctrl+Z before the modifier filter in `App.Input.cs`. It pops the last undo point and restores it. It works in
  every mode, and while paused.
- Ctrl+Shift+Z (and Ctrl+Y) redoes. Redo is cheap once undo restores whole states: on undo, push the current state onto
  a redo stack; any new action clears that stack. It is optional: do it if it stays simple.
- Show a hint naming what was undone: "Undone: placing Black hole 2" or "Undone: Earth's mass change". Label each undo
  point for this in M7.2.
- Do not undo view settings: camera, time warp, pause, rewind, lensing/disc/labels/orbits toggles, the Realism panel,
  panel sliders that only set the next launch (diameter, speed, composition, hole mass before placing). Undo changes
  the universe, not how you look at it.
- The HUD "Undo" buttons call the same code as Ctrl+Z.
- Verify: `key:ctrl+z` (add the verb, or send `InputEventKey` with CtrlPressed through `ClickAt`'s path) after each
  action in M7.2, with `logState` hashes before and after.

### M7.2 One undo point per player action
The owner chose: "everything", for example asteroids and changing the mass or radius of planets.
- Every action that changes the universe records one forced undo point first, labelled for the hint:
  - placing, dragging to set, and removing a black hole;
  - launching (hand aim, click-to-fire, star preset);
  - an erase stroke;
  - a mass or radius slider drag in the inspector. One point per drag, recorded at drag start, not per value change;
  - resetting a body;
  - changing moon detail;
  - any capture or demo verb that adds bodies (`spawnBH`, `holePair`, `tdeDemo`, `sphDemo`, `tidalDemo`).
- Things that happen on their own (collisions, tidal disruptions, mergers, SPH demotions) keep their automatic points.
  So Ctrl+Z after an impact goes back to just before it. Pressed again, it goes back to before the launch that caused
  it (M6.1 already stacks these in that order).
- Undoing a launch removes the rock, and undoing an impact restores the rock's pre-impact state. Keep the M6.1 rule:
  undo never deletes anything the snapshot had.

### M7.3 A snapshot restores everything, not only the bodies
The owner chose: undo restores everything to how it was before the action.
- An undo point holds the app state next to `UniverseSnapshot`:
  - the debris tier's particle buffers and the `DebrisLedger` book (T0/T2/dust tallies, injections, absorbed-by
    lists), so a stream or ejecta cloud disappears or returns with its event. Copy the GPU buffers to a VRAM-side copy
    (`RenderingDevice.BufferCopy`), not a CPU readback, so taking a point does not stall a frame;
  - per-hole `AccretionFeed` reservoirs and tidal fallback flares;
  - `SphReservoir`. An SPH run in flight at undo time is cancelled and the pre-collision pair restored (`FallBackToAnalytic`
    shows how to put the pair back);
  - moon detail, and the selected and focused ids. The camera stays where the player has it.
- History depth grows from 8 to about 50. Budget memory instead of counting: body snapshots are small, but a debris
  copy is up to ~32 MB at 10⁶ particles. Keep total history RAM ≤ ~300 MB and VRAM ≤ ~256 MB, measured and logged.
  Drop the oldest points first. A point whose debris copy was dropped for memory still restores the bodies, and the
  hint says the debris could not be restored.
- Undo must never crash: every view, label, inspector row, SPH proxy, cinematic lock and `LastImpact` that points at a
  body missing after the restore is rebound or cleared (`RebindAfterRestore` is the start). Restoring must not drop a
  frame below 30 fps.
- `logState` hashes the bodies. Extend it (or add `logFullState`) with debris particle count, the ledger totals,
  reservoir masses and `SphReservoir`, so a capture can prove "everything" came back.
- Verify (captures, `logFullState` before and after):
  - Place a 10⁶ M☉ hole, fire the Sun-like star (it is torn apart into a 50,000-particle stream), warp a while, then
    Ctrl+Z twice. Everything matches the pre-launch values, then the pre-placement values bit for bit: bodies hash,
    particle count 0, ledger, reservoir.
  - Drag Earth's mass slider, Ctrl+Z: hash identical, and Earth's inspector mass is back.
  - Ten mixed actions (hole, launch, erase, mass, radius, moons, reset), then ten Ctrl+Z: back to the first hash. If
    redo exists, ten redos land on the last hash.
  - Frame p95 stays ≤ 33 ms while points are taken and restored (windowed). The history memory log stays within
    budget over 60 actions.
  - Tests in `Spayly.Sim.Tests` for the Sim-side parts: history depth, labels, and redo clearing on a new action.

### M7.4 SPH Fast mode: steady substeps
The owner chose (from the M6.7 options): fix the measurement, then smooth the controller. Options (a) and (b) together.
- `SphGpu.ReadTimestamps` divides each GPU interval by the substep count of the frame "3 frames ago" (`substepRing`).
  When frames are fast the lag is different, so the controller flips between 1 and 16 substeps. Tag each timestamp
  pair with the frame it was recorded in, and divide by that frame's count.
- `App.Sph` sizes k from the median of the last 120 timings, not the latest one, and changes k by at most ×2 a frame.
- Verify windowed with the Theia recipe at Fast, approach at `setWarp:1000` (`m6-7-fast-windowed`). Measured over the
  run (`[sph] run cost`):
  - no 1↔16 flicker in the log;
  - p95 per substep within 1.5× of p50 (M6: p50 2.46 ms, p95 41.9 ms);
  - frame p95 ≤ 33 ms.

  Repeat at Hero to show nothing got worse (M6: p50 6.95 ms, p95 8.24 ms).

### M7.5 SPH ends when the physics is done, at ≥ 30 fps
The owner chose: the same collision gives the same planet on every machine, and the game never drops below 30 fps. That
is option (a), with a frame-rate guard.
- The run finishes on the physics alone: `HeroCollision.Finished` (the energy criterion after MinTime, or EndTime 8 h).
  `SphSettings.WallSeconds` (30 s) no longer ends it. Keep it as a soft safety cap of about 120 s with a notice ("SPH
  collision cut short after 2 minutes"), so a very slow GPU still finishes.
- Raise the per-frame SPH budget `SphSettings.GpuBudgetMs` from 8 to what keeps the frame at or under 33 ms. Measure the
  frame without SPH (render, lensing and debris) and give SPH the rest, about 20 ms, and never let the frame p95 pass
  33 ms. At Hero's ~7 ms a substep that is 2–3 substeps a frame. The M6 windowed run reached 9,143 s of SPH time in 30 s
  at 1 substep a frame, so 8 h should take about 30–45 s.
- The HUD progress line already shows SPH time against 8 h. Drop the "of 30 s wall" part, or show the soft cap.
- Update the Realism "Hero SPH collisions" entry: the outcome no longer depends on frame rate.
- Verify with the Theia recipe:
  - under Xvfb, and windowed at Hero and at Fast;
  - Hero under Xvfb and windowed agree on the heir's mass fraction and day to 1 % (M6 got 98.06 % / 26.41 h under Xvfb and
    95.54 % / 32.60 h windowed, because they stopped at different SPH times);
  - windowed frame p95 ≤ 33 ms throughout;
  - demotion closure stays 0.

## Done means
- Each milestone has a row in the milestone table of `docs/godot/decisions.md`, with measured numbers only.
- `docs/godot/verify-queue.md` is updated: move done items out, and add new follow-ups.
- Merge `m7-undo` into main, check that the merge commit exists and the tests pass, and push.
- Copy the capture folders into main's `godot/Spayly.Game/capture/`. Only after that, remove the worktree, without `--force`.

## Still in verify-queue, not in scope unless the owner asks
- the lensed disc's turbulence cost;
- the `Sky.cs` / sunlight colours via `StarColor.MaxNormalised`;
- relaxing the game's SPH bodies before contact;
- the adaptive leapfrog's eccentricity drift;
- the zero-direction impact-effect crash when coincident rocks merge. It is small and could ride along with M7.3's
  undo work if it is on the way.
