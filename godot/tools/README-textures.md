# prepare-textures.mjs

`node godot/tools/prepare-textures.mjs [--force] [--only <body id | source | output>]` fills
`godot/Spayly.Game/textures/` from `public/textures/`, and refuses to run until `npm run assets` has run. It reads the
maps from `src/sim/catalog.ts` and `src/sim/satellites.ts` through Vite's `runnerImport` (no hand-copied tables), and
fails on any `visual` field it does not know. Work that is up to date is skipped. A bake is redone when its source, the
tool, `gapFill.ts`, `noise.ts` or `mapDecode.ts` is newer, or when its roll, fill or tint changed (tracked in
`.prepare-state.json`). `--force` redoes all of it. Stale outputs are deleted. Each run prints a table: status, time,
and the blank fraction before and after the fill.

## Naming
- If nothing reworks a map, the output is `<name>.jpg`: an absolute symlink to `public/textures/<name>.jpg`, the layout M1b imported.
- A reworked map is a baked JPEG at quality 92, the same size as its source:
  - A borrowed map is named `<stem>__<bodyId>.jpg`: `phobos__proteus`, `deimos__nereid`, `vesta__pallas`.
  - A body's own filled map is named `<stem>__fill.jpg`: the five Uranian moons, triton, charon, vesta.

## Processing (same order as `src/render/mapDecode.ts`: roll, fill, tint)
- **Roll.** Each row shifts right by `round(roll * width)` pixels and wraps, which is what the canvas `drawImage` pair does.
- **Fill and tint.** These run the web's own `fillNoData` and `applyTint` from `src/render/gapFill.ts`, which uses no
  DOM. They are loaded through Vite and run on RGBA from sharp. The rows are flipped around the fill because the web
  fills bottom-first and the fill's noise is not symmetric north to south.
- **One patch.** The push step in gapFill.ts reads row −1 whenever the coarsest level is one row high. That happens
  for Charon at 6000 px and Vesta at 3560 px. The row −1 reads give NaN, NaN is stored as 0, and the hole stays black.
  The tool swaps the clamps at load (`fixCoarsestRow`). The result is identical everywhere else.
- **Check.** Compared with Chromium running `mapDecode.ts`, PSNR at 1024 px is 52.4 dB for umbriel, 54.0 for triton
  and 49.9 for proteus. The difference is JPEG q92 loss.

## manifest.json
- Keys are body ids, plus `_sky` (`milky_way.jpg`) and `_sun`. The Sun appears only as `_sun`.
- Each entry is `{ albedo, source, night?, specular?, normal?, clouds?, ring?, fillGaps, tint?, roll?, width, height, credit }`.
- Values are file names in this folder, and only files that exist are listed. `credit` holds the matching `public/textures/CREDITS.md` rows. The invented half of a filled map needs disclosure in game.

## .import
- Every map gets a `<file>.jpg.import` using `streamed_texture_2d` with the M1b params.
- Exceptions: `earth_normal` sets `compress/normal_map=1` (the shader needs `hint_normal`). `milky_way` uses the plain
  `texture` importer (VRAM, mipmaps), because a sky shader has no `UV` derivatives and would sit at the 2048 floor.
- Godot adds uid, path and dest_files on the first `--import`. The tool leaves a `.import` alone while its params match.

**All of it is gitignored**: rebuild it after a clone. The sources in `public/textures/` are in Git LFS, which a clone
may lack, so every path the game loads needs a procedural fallback.
