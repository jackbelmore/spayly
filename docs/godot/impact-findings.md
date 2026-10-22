# Impact model findings (round 2 playtest, problems 4 and 5)

Read-only investigation of branch `playtest-fixes`; nothing here was run. No impact code written: waiting on the owner's choice.

## What the model does today
Flow: `Universe` swept contact -> `Collide.ResolveCollision` (Legacy outcome model by default) -> `App.Impacts.HandleOutcomes`. `SphGate` wraps it only when `Settings.Sph != Off` (default Off).

| Class | Today |
| --- | --- |
| Asteroid into planet | Legacy: severity = E / DisruptionEnergy (`Collide.cs:~98,212`); Airburst, Crater or Merge from `Impact`; fragments only at severity >= 1. |
| Planet into planet | SPH if on, watched and `HeroCollision.Qualifies`; else Legacy (crater/merge, shatter at severity >= 1). LS12 regimes (`CollisionRegimes`) exist but are opt-in (`setOutcomeModel:ls12`). |
| Star into star or planet | `Collide.cs:178-184`: always `MergeInto` and return, before any severity logic. Only a flash and `sun.Flare = 1.5`. Deliberate (`design.md:23`), independent of mass and speed. Stars have no SPH layer set. |
| Into a black hole | Absorbed, accretion; hole-hole HLZ14 remnant and kick. |

## Moon into Vesta
No instant-melt shortcut exists. SPH never qualifies (Vesta radius ~262 km < 1000 km floor; mass ratio ~0.0035 < 0.005), so Legacy runs. At severity >= 1 (~13.6 km/s or more, an estimate) `Fragment`/`BecomeRemnant` (`Collide.cs:393-407`) sets the Moon to `Surface.Lava, Magma = 1` (decay 1-100 years): that is the "instant molten rock". Below severity 1: crater plus partial glow.

## Real physics to model
Specific impact energy Q_R vs catastrophic disruption Q*_RD; Leinhardt and Stewart 2012 outcomes (merge, graze-and-merge, hit-and-run, erosion, partial accretion, super-catastrophic); Theia-class giant impacts; SPH studies of main-sequence collisions (Lombardi 1996, Freitag and Benz 2005): low-speed hits merge with a few percent mass loss, faster or grazing ones strip mass; no supernova. Literature numbers still to be checked against sources before they are cited in code.

## Options
- **A: scaling law only.** LS12 by default plus an energy-scaled star branch. Pure Sim, testable, no frame cost; no true tidal stretching.
- **B: route large hits through SPH.** Lower the `Qualifies` floors, add a stellar EOS. Visible breakup; Fast mode ~2.6 ms/substep, Hero ~14.6 ms/frame (windowed), up to 120 s wall; needs star EOS research and has no strength model for small bodies.
- **C: hybrid (recommended).** A everywhere, SPH Fast for rock-on-rock with both bodies >= ~100 km; stars on A with scaled visuals first.
