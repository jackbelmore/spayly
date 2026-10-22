---
name: add-hud-control
description: Add or change a HUD control - a button, slider, checkbox, select or readout - in index.html and src/ui/hud.ts. Use because a mistyped element id fails silently at runtime with a clean typecheck.
paths: "src/ui/**,index.html,src/style.css"
---

# Adding a HUD control

The widest silent-failure surface in the repo: `index.html` carries ~64 element ids, `$()` in `hud.ts` casts
`getElementById` without a null check, and nothing verifies the two agree. A typo gives a clean typecheck, a
clean build, and a dead panel.

## The order

1. **`src/app/types.ts`** — only if the control introduces a new shared type or preset. It must stay
   dependency-free.
2. **`src/app/app.ts`** — the state it changes and the method that changes it (`setX`), plus a keyboard
   binding if it deserves one. A control that only flips a `settings` flag needs no method.
3. **`index.html`** — the markup and its `id`, inside the right panel.
4. **`src/ui/hud.ts`** — add to the `e = {...}` map, add the listener in the constructor, and set its initial
   value where the panel syncs. **The id string must match `index.html` exactly.**
5. **`src/style.css`** — only if it needs styling beyond the existing `.panel` / `.field` / `.toggle` classes.

## Rules

- State changes go through an `App` method. The one deliberate exception is `app.settings.*`, which the View
  panel writes directly.
- Never format a number inline — `ui/format.ts` owns every number→string conversion.
- Never compute physics in the HUD. Read `density`, `surfaceGravity`, `escapeVelocity` from `sim/body.ts`.
- If `Hud.update()` writes back to your control, guard it against `document.activeElement` so it does not
  fight the user mid-drag.
- `update()` runs ~8 Hz — keep it allocation-light.

## Verify

Typecheck will not catch a bad id. Run `visual-check` with a scenario that opens the panel the control lives
in, and confirm both that it renders and that the console is clean.

---
If you had to re-derive something this skill should have told you, run `update-context` before finishing.
