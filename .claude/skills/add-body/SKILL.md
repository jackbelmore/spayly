---
name: add-body
description: Add a planet, moon, dwarf planet or asteroid to the solar-system catalogue, including its orbital elements, rotation, texture and licence attribution.
paths: "src/sim/catalog.ts,src/sim/ephemeris.ts,public/textures/**"
---

# Adding a body

## Where it goes

`src/sim/catalog.ts` holds `CATALOG`, a record of `BodySpec` minus `pos`/`vel` — those are derived at build
time from orbital elements. `createSolarSystem(date)` assembles everything with the system barycentre at rest.

1. **`CATALOG`** — add the entry: `id` (stable and unique; views key off it), `name`, `kind`, `surface`,
   `mass` (kg), `radius` (km), `primaryId` for anything orbiting a planet, `rotation` (IAU pole RA/Dec and
   prime-meridian rate), and `visual` (`color`, `texture`, `albedo`, `baselineTemp`).
2. **Orbital elements** — planets come from Standish mean elements via `planetElementsAt` in `ephemeris.ts`;
   the Moon from Meeus via `moonElementsAt`. A new body needs its elements sourced the same way and cited.
   Values from the JPL Small-Body Database or the SSD satellite tables are the right source.
3. **`PLANET_ORDER`** — add it only if it deserves a number-key shortcut and a place in the sorted body list.
4. **Texture** — filename goes in `visual.texture`, and the file is fetched by `scripts/fetch-assets.mjs`.
   Add the download entry there, run it, and commit the new `public/textures/` files (Git LFS takes them).
5. **`public/textures/CREDITS.md`** — **mandatory.** Source and licence for every map. If the map has
   never-imaged regions that the loader extrapolates over, say so: that part is invented.

## Rules

- Units are km and kg, matching the rest of `src/sim`. Degrees appear only in IAU rotation fields.
- Every texture path needs a procedural fallback — `loadTexture` resolves `null` when a file is absent, and
  the app must still render.
- A body with no `texture` is fine; `textures.ts` will generate one procedurally.

## Verify

`npm test` — `universe.test.ts` checks heliocentric distances are physically sensible, so a bad element set
usually shows up there. Add an assertion for the new orbit. Then `visual-check` to confirm it renders, is
lit correctly, and sits where you expect.

---
If you had to re-derive something this skill should have told you, run `update-context` before finishing.
