# spayly

Browser solar-system sandbox: real N-body physics, photoreal three.js rendering, an asteroid impact mode.
Vite + TypeScript + three.js. No framework, no state library, no linter. `README.md` says what it does and
why; this file says what an edit must not break.

## Layer law

Four layers, each with its own `CLAUDE.md` that loads when you open a file there. Edges, not description:

| Layer | May import | Never |
| --- | --- | --- |
| `src/sim/` | only `src/sim/` | `three`, DOM, anything else |
| `src/render/` | `src/sim/`, `three` | `ui/`, `app/` |
| `src/ui/` | `sim/`, `render/` types, `app/types` | `app/app` as a value — `import type` only |
| `src/app/` | everything | — |
| `godot/` | `src/sim` as *reference only*; see `godot/CLAUDE.md` | editing anything in `src/` — the web build is frozen |

`tests/layering.test.ts` enforces the `src/` rows (`npm test`); `godot/Spayly.Sim.Tests/LayeringTests.cs` the Sim one (`dotnet test`).

**The escape hatch, so you do not "fix" it:** a sim function that needs render-side knowledge takes a
callback. `resolveCollision` receives `bodyFrame` and `surfaceAt` (`src/sim/collide.ts`), injected from
`src/app/app.ts`. That is the sanctioned pattern. Never relax the import law instead.

## Verify — and what each does not cover

- `npm run typecheck` — the real gate for any import or signature change.
- `npm test` — covers `src/sim/` and the layering law. **Nothing covers `src/render/` or `src/ui/`.**
- `npm run build` — what CI runs (typecheck · test · build).
- `npm run shoot` — headless screenshot + console capture. A render or HUD change is **unverified** until
  you have run it; use the `visual-check` skill.

## Non-negotiables

- The simulation never lies. Visual cheats — diorama scale, minimum-pixel radius, exposure, sunlight
  falloff — live in `render/` and `ViewContext` and must never write back to `Body.pos/vel/mass/radius`.
- Maps live in `public/textures/` and go through Git LFS *(`.gitattributes`)*; never commit one elsewhere, or `.assets-raw/`. Every texture path still needs a procedural fallback: CI checks out LFS pointers, not images.
- Invented or extrapolated image data must be disclosed in `public/textures/CREDITS.md`.
- No new runtime dependency without asking. `three` is the only one.

## Style

No semicolons, single quotes, 2-space indent. Long lines are fine (the codebase runs past 140 chars).
Comments explain *why*, never *what*. JSDoc on an exported function carries its units. *(unenforced — there
is no linter, deliberately)*

## Where a new fact goes

| A fact that… | goes in |
| --- | --- |
| constrains how an edit must be written | one imperative line in that directory's `CLAUDE.md` |
| is a procedure you would otherwise re-derive | a skill in `.claude/skills/` |
| is *why*, or a number worth citing | `docs/design.md`, or `README.md` if user-facing |
| is true only today — a bug, a TODO, an idea | `docs/backlog.md` or a code comment. **Never a `CLAUDE.md`.** |

Every rule names what catches a violation, or is marked `(unenforced)`. If you cannot say what catches it,
you are writing prose.

**Caps: this file ≤ 60 lines, each directory file ≤ 40, each skill body ≤ 120. To add a line past the cap,
delete one first and say which.** Run the `update-context` skill.
