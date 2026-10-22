# src/app — the application

The loop runner: rAF loop, input, modes, asteroid aim-and-fire, cinematic camera, view lifecycle, and the
assembly of `ViewContext` each frame. The only layer allowed to import every other one.

## Growth
`app.ts` is already ~760 lines and owns most cross-layer orchestration. A new subsystem goes in a **new file
in this directory**, not another private field on `App`. *(unenforced)*

## types.ts is the boundary
`types.ts` holds everything `src/ui` needs and **imports nothing** — not from `app.ts`, not from `sim/`.
That constraint is the only thing keeping the app↔ui cycle dead. Adding a value to `app.ts` and importing it
from `hud.ts` reopens it, and it fails as `ReferenceError: Cannot access 'PRESETS' before initialization` —
a black screen whose only clue is the console. *(`tests/layering.test.ts`)* `Settings` also carries `moons`
(`MoonDetail`) and `textures` (the streaming ceiling in px) — both live here, not on `App`.

## tick() is ordered, and the order is load-bearing
Physics, then view sync, then sun state, then camera, then fill `ctx`, then views, then lines and effects
and outcomes, then HUD, then render. **Read `tick()` before inserting a step — do not trust a list here.**
Erase mode's brush shares the same pointer dispatch as asteroid aim-and-fire in `bindInput` — a new mode
needs a branch in every handler there, not a parallel listener.

## A black screen means read the console
`frame()` wraps `tick()` in try/catch and latches `loopError`, so a single throw freezes the simulation
permanently and logs exactly once. The pixels tell you nothing after that; `npm run shoot` captures the
console and pageerrors, which is where the actual cause is.

## Rebinding after undo
`undoImpact` restores a snapshot and calls `rebindAfterRestore()`. Anything caching a `Body` reference —
`rig.focus`, `selected`, `lastImpact`, `views`, orbit periods, the site lock — must be re-bound there, or
you are holding a pointer to a body that no longer exists in the universe. *(unenforced)*

## window.spayly is a contract
`src/main.ts` hangs the `App` on `window` for `scripts/shoot.mjs`. Anything a screenshot scenario needs to
drive must be a public method on `App`; renaming one breaks the `visual-check` skill's scenarios.

## Verify
`npm run typecheck`, then the `visual-check` skill. There are no tests for this layer beyond the layering law.
