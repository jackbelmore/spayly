# src/render — three.js views

Presentation only. Almost nothing here is under test, so assume a mistake is invisible until screenshotted.

## Imports
- `src/sim/` and `three` only. Never `ui/`, never `app/`. *(`tests/layering.test.ts`)*
- New per-frame data arrives via a `ViewContext` field populated in `App.tick`, never a module global.
- Read-only over sim: never write `Body.pos/vel/mass/radius`. `damage` may be read, only through its
  `version` counter.

## Units and the camera stop
1 render unit = 1000 km (`RENDER_SCALE`), y-up; the sim→render swap lives in `units.ts` — use `toRender`
/ `dirToRender`, never inline it. `visualRadius` adds diorama scale and a legibility floor; using it for
a camera stop or physics is a bug *(unenforced)*. The camera stops at a body's true surface instead, and
past that `camera.ts` narrows the FOV rather than closing distance, so a body keeps growing past the
pixel-radius ceiling a fixed lens would otherwise impose.

## Texture pipeline
`textures.ts` is the cache and entry point, not the whole story: `textureWorker.ts`/`mapDecode.ts` decode
off the main thread; `gapFill.ts` fills no-data at 2048 into blank pixels only, so imaged areas keep full
resolution; `noise.ts` backs procedural rock. `loadTexture` resolves `null` when a file is missing.

**Every texture consumer must re-request its map as it renders.** `sweepTextures` disposes entries whose
`lastUsed` is stale, and three.js has no finalizer — a one-shot load gets freed while still bound to a
material. *(unenforced — shows up as a texture going blank minutes later)* Bind a new map through
`swapTexture(old, next)` so the width it replaces is freed on the next sweep, not eight seconds later.

**Width comes from `MapStream.due`, never from a caller.** It floors at 256 for a dot, drops a body to
the floor when it leaves the frustum, gives the camera's focus a 2048 copy at once and then the ceiling,
and only downgrades at a quarter of the current width. `loadTexture` then decodes the smallest file at
least that wide: `npm run assets` writes `textures/lod/<map>@512.jpg`, `@2048.jpg` and a manifest of
full widths, and the original is fetched only past 2048. Missing copies fall back to the original.

`milky_way.jpg` must be an all-sky map in **galactic coordinates**: `sky.ts` orients it by the galactic
pole and centre, so a ground panorama instead puts a horizon and lights into the sky. A body map must be
**2:1 equirectangular surface imagery**, never a legend, graticule or globe-gore cut-out. *(`fetch-assets.mjs --check`)*

## three.js hygiene
Log depth buffer is on: a custom `ShaderMaterial` needs the `logdepthbuf` chunks from `shaders/common.ts`.
Colour textures get `SRGBColorSpace`, data textures `NoColorSpace`; everything added to the scene must be
`dispose()`d. *(unenforced)* `orientation.ts` uses `THREE.Quaternion` only as maths — it's physics-adjacent
(decides where craters land), so keep scene/material/texture code out of it.

## Verify
Typecheck proves nothing visual. Use the `visual-check` skill and read the console, not just the image.
