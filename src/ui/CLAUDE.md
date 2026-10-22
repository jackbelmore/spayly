# src/ui — HUD

DOM panels over `index.html`. The toolchain catches almost nothing here.

## Imports
- `sim/` for data and maths, `render/` for types, `../app/types` for shared types.
- **`import type { App }` — the `type` keyword is load-bearing.** Dropping it recreates the app↔ui runtime
  cycle, which fails as `ReferenceError: Cannot access 'PRESETS' before initialization` — a black screen
  whose only clue is the console. *(`tests/layering.test.ts`)*

## The index.html contract
Every id in the `e = {...}` map must exist in `index.html`. `$()` casts `getElementById` without checking,
so a typo is a silent runtime null and a dead HUD with a clean typecheck. Adding a control is always three
files in one change: `index.html` (markup + id) → `hud.ts` (`e` entry, listener, sync) → `src/style.css`.
Use the `add-hud-control` skill — `opt-moons`, `opt-textures` and `mode-erase`/`erase-panel` are worked
examples of the pattern (a select or button plus its panel toggle in `setMode`/`update`).

## The HUD computes nothing
- Every number→string goes through `ui/format.ts`. Never format inline.
- Every state change goes through an `App` method — `setWarp`, `setMode`, `applyPreset`. The one deliberate
  exception is `app.settings.*`, which the View panel writes directly.
- Never compute physics here: read `density`, `surfaceGravity`, `escapeVelocity` from `sim/body.ts`.

## The update loop
`Hud.update()` runs at ~8 Hz from `App.tick` (`hudTimer > 0.12`). Keep it allocation-light. Guard any write
to an input the user might be dragging (compare against `document.activeElement`). The body list rebuilds
only when `app.bodiesVersion` or the selection changes.

## Verify
Typecheck catches almost nothing here. Screenshot or it didn't happen — `visual-check`.
