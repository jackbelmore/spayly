# spayly

Solar-system sandbox: real N-body physics, photoreal rendering, asteroid impacts, black holes. **The game is the Godot 4.8
(C#) build in `godot/`**; start with `godot/CLAUDE.md`. The original web build (Vite + three.js) lives on the
`web-version` branch and is no longer developed. `README.md` says what it does and why; this file says what an edit
must not break.

## Layout

| Path | What | Rule |
| --- | --- | --- |
| `godot/` | the game: `Spayly.Sim` (pure physics), `Spayly.Game` (Godot), tools | `godot/CLAUDE.md` |
| `src/sim/`, `src/render/{gapFill,noise,mapDecode}.ts` | web code `godot/tools` still reads | never edited; `src/sim/CLAUDE.md` |
| `public/textures/` | body maps, Git LFS | below |
| `scripts/` | `fetch-assets.mjs` (maps), `fetch-ephemeris.mjs` (JPL → `src/sim/satellites.ts`) | — |

Anything else web (UI, three.js views, vitest suite, `shoot.mjs`): check out `web-version`. Do not bring it back to `main`.
*(unenforced)*

## Verify — and what each does not cover

- `dotnet test godot/Spayly.Sim.Tests` — physics and the Sim layering law. CI runs it.
- `npm run typecheck` — keeps the remaining `src/` loadable by the tools. CI runs it.
- `godot/tools/run-capture.sh` — the only proof of pixels, input and frame times. **Nothing else covers `Spayly.Game`.**

## Non-negotiables

- The simulation never lies. Visual cheats — diorama scale, minimum-pixel radius, exposure, sunlight
  falloff — live in rendering and must never write back to body position, velocity, mass or radius. *(unenforced)*
- Maps live in `public/textures/` and go through Git LFS *(`.gitattributes`)*; never commit one elsewhere, or `.assets-raw/`. Every texture path still needs a procedural fallback: CI checks out LFS pointers, not images.
- Invented or extrapolated image data must be disclosed in `public/textures/CREDITS.md`.
- No new dependency (NuGet or npm) without asking. *(unenforced)*

## Where a new fact goes

| A fact that… | goes in |
| --- | --- |
| constrains how an edit must be written | one imperative line in that directory's `CLAUDE.md` |
| is a procedure you would otherwise re-derive | a skill in `.claude/skills/` |
| is *why*, or a number worth citing | `docs/design.md`, `docs/godot/decisions.md`, or `README.md` if user-facing |
| is true only today — a bug, a TODO, an idea | `docs/backlog.md`, `docs/godot/verify-queue.md` or a code comment. **Never a `CLAUDE.md`.** |

Every rule names what catches a violation, or is marked `(unenforced)`. If you cannot say what catches it,
you are writing prose.

**Caps: this file ≤ 60 lines, each directory file ≤ 40, each skill body ≤ 120. To add a line past the cap,
delete one first and say which.** Run the `update-context` skill.
