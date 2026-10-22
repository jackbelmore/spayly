---
name: visual-check
description: Screenshot the running app headlessly to verify a render, HUD or camera change, check FPS, or debug a black screen. Use whenever a change touches src/render, src/ui, src/app, index.html or style.css, because nothing else verifies those.
paths: "src/render/**,src/ui/**,src/app/**,index.html,src/style.css"
---

# Visual check

`npm test` and `npm run typecheck` prove nothing about pixels. This is the only verification path for
`render/`, `ui/` and `app/`.

## Run it

1. Start the dev server: `npm run dev`. **Read the port it prints.** `vite.config.ts` asks for 5173, but
   Vite will pick another if it is taken, and `scripts/shoot.mjs` defaults to 5188 — so always pass the URL
   explicitly rather than trusting any of them.
2. `npm run shoot -- <url> <outDir> '<actions>'`

```bash
npm run shoot -- http://localhost:5173/ shots '[{"name":"tabletop","js":"spayly.viewTabletop()","wait":3000}]'
```

Actions are `[{name, js, wait}]`: `js` is evaluated in the page, `wait` is milliseconds after it. A
`00-start.png` is always taken first, then `NN-name.png` per action. `shots/` is gitignored.

## Driving the app

`window.spayly` is the live `App`. Public methods: `viewTabletop()`, `viewInner()`, `viewOuter()`,
`select(body)`, `follow(body)`, `flyAbove(body)`, `setWarp(w)`, `togglePause()`, `toggleRewind()`,
`setMode('explore'|'asteroid')`, `applyPreset(p)`, `resetBody(body)`, `undoImpact()`, `lookAtLastImpact()`,
`primaryOf(b)`, `viewFor(b)`. Readable fields: `universe`, `settings`, `asteroid`, `selected`, `mode`, `warp`,
`fps`, `exposure`, `bodiesVersion`, `lastImpact`.

Find a body with `spayly.universe.bodies.find(b => b.id === 'mars')`.

Useful scenarios: the whole system (`viewTabletop()`), a close-up (`flyAbove(body)`), a settings toggle
(assign to `spayly.settings.*`), and a performance read at high warp (`setWarp(1e6)`).

**Firing an asteroid cannot be scripted today** — `fireAt` and `launch` are private, so an impact needs a
real click, or making one of them public first. Say so rather than faking a screenshot of it.

## Environment

- `SHOT_W` / `SHOT_H` — viewport, default 1600x900.
- `SHOT_SETTLE` — ms before the first shot. **Raise it when textures are present**; the default is not enough
  once `public/textures/` is populated.
- `SHOT_HEADED=1` — watch it run.
- `CHROME_PATH=/path/to/chromium` — overrides the `chrome` channel when Chrome is not installed.
  Machine-specific: put it in `.claude/settings.local.json`, never in this file.

## Reading the output

The harness prints console errors, warnings, pageerrors and failed HTTP responses alongside the shots.
**Read them.** A black or frozen image plus a `pageerror` is a latched `loopError` in `App.frame` — the
simulation threw once and stopped, and the pixels tell you nothing. The cause is in the console.

It also prints fps, body count, substeps, exposure and camera distance. Sudden fps decay across a
moon-detail or mode switch usually means a view was added to the scene without being `dispose()`d.

Always compare a before/after pair. A single "after" shot proves nothing.

---
If you had to re-derive something this skill should have told you, run `update-context` before finishing.
