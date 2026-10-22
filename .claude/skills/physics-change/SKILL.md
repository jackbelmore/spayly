---
name: physics-change
description: Change the integrator, impact model, collision handling, orbital mechanics or the body catalogue in src/sim. Covers which test guards what, the km-vs-SI unit boundary, and the invariants that must not regress.
paths: "src/sim/**,tests/**"
---

# Changing physics

`src/sim` is the one layer with real test coverage. Use it.

## Which test guards what

| test | asserts |
| --- | --- |
| `kepler.test.ts` | Kepler solver, element ↔ state round-trip, circular-orbit speed and period |
| `universe.test.ts` | full-system build, heliocentric distances, energy conservation, reversibility |
| `collide.test.ts` | momentum conservation against a float noise floor, disruption thresholds, auto-aim |
| `impact.test.ts` | the Collins/Melosh/Marcus model against Chelyabinsk, Meteor Crater and Chicxulub |
| `layering.test.ts` | the import law — `sim/` stays a leaf with no `three` and no DOM |

Before changing anything, name the test that should fail if you get it wrong. If there isn't one, write it first.

## The unit boundary

`src/sim` is km, kg, seconds, radians — **except `impact.ts`, which is SI (m, kg, s, J) end to end** because
the source formulas are. `collide.ts` converts at the boundary, and `ImpactReport` distances come back in
metres. Getting this wrong produces answers wrong by 10^3 that still look plausible, and only
`impact.test.ts`'s real-world cases will catch it.

## Invariants that must not regress

- The leapfrog stays symplectic and exactly time-reversible. No velocity- or time-dependent forces, no
  clamping or renormalising inside `step()`.
- Momentum is conserved exactly across merges and fragmentation.
- Time warp throttles the substep budget; it never skips physics.
- `mass0` / `radius0` are catalogue values, written once in `makeBody`.
- Anything writing `damage.craters` must bump `damage.version`, or render never repaints.

## Rules

- Prefer a conservation or reversibility assertion over a golden-value one — it survives retuning.
- **Never weaken an assertion to make a change pass.** If a test fails, either the change is wrong or the
  test encoded an assumption that is now wrong; say which, explicitly.
- The 60 s `testTimeout` in `vite.config.ts` is deliberate — a simulated year is hundreds of thousands of
  substeps. Don't lower it to make a slow test look fast.
- A physics change with a visual consequence still needs `visual-check`. Tests do not see the screen.

---
If you had to re-derive something this skill should have told you, run `update-context` before finishing.
