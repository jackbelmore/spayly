# M1c — floating-origin scale spike

Godot 4.8.dev6 mono, C#, `godot/spikes/scale/`. RTX 3080 Ti, Vulkan 1.4.351, Forward+ (reverse-Z), Xvfb via
`tools/run-capture.sh`. `dt=60` s sim-time/frame, `RENDER_SCALE`=1000 km/unit, y-up. All six bodies (Sun,
Earth, Moon, 10 m cube, Neptune, 50 AU marker) exist simultaneously; only the camera framing differs by
scenario. Orbits are circular and analytic in double (`Vec3d`, local to this spike). Neptune and the marker
are static (no speed was specified for either).

## Jitter (scenario 1, cube corner projected screen position, 120 frames)

| run | max deviation from mean | p95 |
| --- | --- | --- |
| floating origin | **0.00041 px** | 0.00036 px |
| `--no-floating-origin` control | **460.68 px** | 460.68 px |

Requirement was < 0.05 px for floating origin: passes with ~120x margin. The control's corner position
ranges from y=-39.6 to y=544.1 across 120 frames (x pinned at 800.0 by this camera's geometry) — the cube
corner is not stably projectable at all once the camera/target positions are absolute floats at ~1.5e5
render-unit (1.5e8 km) magnitude. Both csvs: `capture/main/jitter.csv` (floating-origin, cube-scenario
frames 0–119 of the 480-frame run), `capture/control/jitter.csv` (the dedicated 120-frame control run).

## Depth: one camera is enough only with a ratio cap — found the opposite empirically

The near/far formula from the task (near = 0.5×nearest surface, far = 1.1×(farthest+radius), bodies filtered
to "in front of camera") produces enormous ratios once Neptune/the marker are in front of the camera at the
same time as the 10 m cube 30 m away: near≈7.6e-6 units, far≈4.8–9.6 units of magnitude 1e6–1e7. Naively
uncapped, this **does not degrade gracefully** — it does not look like the fighting/clipping the task asked
me to watch for. It fails outright:

```
ERROR: Condition "!res" is true. Returning: false
   at: create_frustum_points (servers/rendering/rendering_light_culler.cpp:597)
```

and the entire frame renders black (shotcheck nonblack fraction 0.0002, i.e. only the HUD label survives).
I bisected the cliff with the near value pinned at 7.615e-6 units: **far=100 renders correctly (ratio
1.3e7); far=300 already fails (ratio 3.9e7)**, and it stays failed all the way to far=1e6 (ratio 1.3e11,
the value the naive formula actually wanted for the cube scenario). So the failure threshold on this
GPU/driver is a near:far ratio somewhere in **1.3e7–3.9e7**, not a subtle precision loss at the far end —
the whole frame goes down, including the near objects the spike cares about (cube vs. Moon surface).

Fix implemented in `ComputeNearFar` (`Main.cs`): cap `far` to `near × 1e6` (a safe margin under the
measured cliff). Effect: only the cube scenario ever needs the clamp (120/480 frames in the combined run,
logged as `[scale] far-clamped frames: 120 / 480`) — Neptune and the marker simply drop out of the depth
test while the cube is framed, which is the right trade-off (nobody needs Neptune's z-buffer slot while
looking at a 10 m box). Moon-scenario ratio ≈487, system-scenario ≈2.5, neptune-scenario ≈1e5: none of
those needed clamping. With the cap in place, no depth fighting or clipping was visible in any of the four
shots (cube sits cleanly against the Moon-surface plane; Earth/Moon never nearly align in the chosen
camera angles so that case wasn't exercised).

**Go/no-go: go**, for a single camera + floating origin at 1 unit = 1000 km with reverse-Z, *provided*
near/far is capped to a bounded ratio (here 1e6) rather than computed unconditionally from every body
"in front of camera" — the latter is a no-go, and fails silently (black frame, easy to mistake for a
camera/orientation bug rather than a depth-precision one).

## Bugs found and fixed along the way

1. **Cube invisible (black frame) even before the near/far issue.** `Node3D.Basis` is the transform's
   rotation+scale combined; the first version set `Node.Scale` (tiny, correct) and then overwrote
   `Node.Basis` with a *pure-rotation* matrix for the cube's tidally-locked orientation, silently discarding
   the scale — the box rendered at its unit mesh size, 1000 km edge instead of 10 m, with the camera deep
   inside it (only interior/back faces, culled → black). Separately, the (right, up, fwd) triple was built
   as `up × right` (left-handed) instead of `right × up`; Godot decomposed that mirrored basis back to a
   reported `Scale=(-1,-1,-1)` rather than erroring, which is what exposed both bugs together in the debug
   log. Fixed by folding the edge length directly into the basis vectors and correcting the cross-product
   order (`Main.cs`, `UpdatePhysics`/`_Process`).
2. **Neptune scenario: camera position placed Neptune directly between the camera and the Sun**, eclipsing
   Sun+Earth entirely (Neptune's own ~7 deg angular radius at the original 3–8 R offset covered the <2 deg
   Sun-Earth separation as seen from 30 AU). Fixed with a 7.5R-outward + 2.75R-sideways offset (~20 deg
   bearing): clears Neptune's own disc, stays inside the 37.5 deg FOV half-angle.
3. **System scenario: marker at 50 AU fell just outside the default 75 deg FOV** from a 60 AU-high camera
   (atan(50/60)=39.8 deg > 37.5 deg half-angle). Raised the camera to 75 AU (atan(50/75)=33.7 deg, margin).

## Label readability

- **cube, moon**: clean. Moon scenario in particular (Sun/Earth/Moon from ~1e6 km) — three separated,
  fully readable labels, verified by eye (`capture/main/02-moon.png`).
- **system, neptune**: Sun+Earth(+Moon) labels overlap into unreadable garbled text
  (`S⊕arth`-style overlap in both `03-system.png` and `04-neptune.png`). This is a real scale fact, not a
  positioning bug: from 30–75 AU the Sun-Earth angular separation is ~1.9 deg (a few px to a few dozen px
  depending on FOV), and Earth-Moon separation is ~400x smaller — sub-pixel. A ~45-70 px-wide label can't
  avoid overlap at that separation. Neptune's own label and the marker's label stayed clean and correctly
  positioned throughout (confirmed via the per-frame debug log of `nodePos`/`UnprojectPosition`, not just
  eyeballing). Occlusion test worked as intended: the cube (sitting on/near the Moon's surface) was
  correctly hidden behind the Moon's disc in the moon/neptune framings; a coincidental frame-0 orbital
  phase alignment (all angles start at 0) also had Moon briefly occluding Earth occluding the Sun at frame
  0 of the neptune scenario specifically — real geometry, not a bug, and gone by frame 400+ (the scenario's
  actual shot frame) as the phases drift apart. Label decluttering (leader lines, merging) is out of scope
  for this spike.

## Frame timing (`tools/summarize-frametimes.py`, warmup 10 dropped)

Combined 480-frame run (all 4 scenarios): dt_ms p50 40.9, p95 42.4, max 77.7 (mean 40.9); gpu_ms p50 1.23,
p95 1.96, max 2.17; fps@p50 24.4 — consistent with the Xvfb ~37.5 ms floor noted in the task plus real GPU
work. 120-frame control run: dt_ms p50 37.9, p95 41.7; gpu_ms p50 0.18 (less geometry drawn since only the
cube scenario runs). No per-frame managed-allocation or GC concerns checked (out of scope; not asked for
here).

## Screenshots

- `capture/main/01-cube.png` (frame 100) — cube fills a modest fraction of frame (~19° angular diameter at
  the literal "30 m" the task specified; "~1/3 of frame" would need ~20 m instead — noted, not changed,
  since the task's own number was used literally), grey Moon-surface plane behind, label centred.
- `capture/main/02-moon.png` (frame 220) — Sun/Earth/Moon separated, readable.
- `capture/main/03-system.png` (frame 340) — diorama on, marker visible near frame bottom, Neptune clean,
  inner three overlap (see above).
- `capture/main/04-neptune.png` (frame 460) — Neptune's disc off-centre, Sun/Earth/Moon cluster near frame
  centre (labels overlap, dots present).
- `capture/control/01-cube_control.png` (frame 100) — renders a red mass at this single frame, but per
  the jitter numbers above the corner's screen position is essentially uncontrolled frame-to-frame; a
  single static screenshot understates how broken it is.

## Files

- `Main.cs`, `Vec3d.cs`, `CaptureDriver.cs` (unmodified copy), `ScaleSpike.csproj`, `project.godot`,
  `Main.tscn` — all under `godot/spikes/scale/`.
- No changes made outside `godot/spikes/scale/`; nothing committed. Per the task's explicit scope
  (`docs/` is read-only for this task), I did not add the usual spike row to `docs/godot/decisions.md`
  that `godot/CLAUDE.md` calls for — that's left for whoever integrates this spike's findings.
