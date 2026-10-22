# spayly design notes

Status: first playable build (September 2026). Everything below is implemented. Open gaps and queued ideas live in `backlog.md`.

## Pillars

1. Physically faithful: the simulation never lies. Visual scale, exposure and camera tricks are presentation only.
2. Maximum spectacle: photoreal planets, HDR bloom, atmospheric scattering, cinematic impact camera.
3. Fun to poke: sliders for mass and radius on any body, time warp with rewind, asteroid mode with auto-aim,
   undo for impacts.

## Decisions taken

| Question | Decision |
| --- | --- |
| Platform | Browser, TypeScript, Vite, three.js (WebGL2, log depth buffer, HDR post-processing). Easy for AI tooling, shareable, hot reload. |
| Scale | True scale always in physics. Rendering keeps every body at least a few pixels wide and offers a diorama toggle (planets ~800x, Sun ~30x, moons capped so they never swallow their planet). |
| Integrator | Kick-drift-kick leapfrog (symplectic, reversible) with adaptive substeps from the shortest of sqrt(r^3/GM) and r/v over all pairs; 2 % of that per step, 0.25 s to 4 h, at most 12 000 substeps per frame. |
| Belts | Main belt with Kirkwood gaps, Jupiter Trojans and a cold Kuiper belt as massless test particles that feel gravity (coarser leapfrog). |
| Initial state | JPL/Standish mean elements at the current date, Meeus mean lunar elements, barycentre at rest. |
| Rotation | IAU pole RA/Dec and prime meridian rates, applied as quaternions; asteroid tumble from a seeded axis. |
| Impact numbers | Collins, Melosh & Marcus 2005 (entry, crater, fireball, thermal, seismic, ejecta, air blast, tsunami). Radii capped at the antipode; planet-scale events reported as such. |
| Collision outcomes | Star: a body under 0.1 % of its mass is absorbed; anything bigger merges, shedding 1 − e^(−x²) of the pair's mass for x = impact energy over binding energy (a grazing fast pass is a hit-and-run), see decisions.md. Gas giant: plume + dark scar. Airburst: flash, no crater. Crater: crater decal, heat spot, dust load for large energies, magma above ~0.3 % of binding energy. >= 20 % of binding energy: fragmentation with a largest-remnant fraction f = 0.5(2 - ratio); momentum conserved exactly. |
| Sun | Luminosity ∝ (M/M☉)^3.5 (brown dwarf below 0.08 M☉), temperature from luminosity and radius, blackbody colour. |
| Planet look | 8k Earth day map, night lights, specular oceans, normal map, 8k drifting cloud layer with shadows, single-scattering atmosphere shell (Chapman-function optical depths), analytic eclipse shadows from nearby bodies, Saturn ring shadows both ways, gas-giant limb darkening, climate cue (ice caps grow, deserts spread) from equilibrium temperature. |
| Camera | Floating origin in km (float64), log zoom, follow mode, fly-to presets, cinematic impact camera that locks onto the impact site while the planet rotates. |
| Aiming | Click a body: shooting-method auto-aim from the viewpoint through a copy of the N-body system. Drag on empty space: manual aim with a live predicted path and time-to-impact. |
| Undo | A snapshot is taken before every collision; undo restores it and removes the impactor. |
