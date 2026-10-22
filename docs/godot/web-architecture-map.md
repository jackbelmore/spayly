# The web build's presentation and app layers — map for the Godot port (M3)

Generated 2026-09-24 from `src/app`, `src/render`, `src/ui`, `index.html` at commit 5d4b390. Line refs are into those
files. This is the parity checklist's source: every feature named here exists in the web build and must exist in
`godot/Spayly.Game` before M3 closes. Sizes: app.ts 1066, camera.ts 237, viewContext.ts 260, planetView.ts 381,
rockView.ts 190, sunView.ts 106, effects.ts 263, lines.ts 138, textures.ts 626, gapFill.ts 330, shaders 672 total,
hud.ts 293, format.ts 103, index.html 168.

## 1. App (`src/app/app.ts`, `types.ts`, `main.ts`)

**tick() order is load-bearing** (`app.ts:493-588`):
1. dt clamp, fps accounting
2. diorama amount eased toward `settings.diorama`
3. physics `universe.step(dtSim)` unless paused; cinematic camera caps warp at 1 for 9 s after an impact; rewind
   flips the sign; then `stateIsFinite()` → `recoverFromFault()` (pop one undo snapshot, pause, rebind, hint)
4. `syncViews()` — diff `universe.bodies` vs the view map; `createView()` picks Sun/Planet/Rock view by `Body.kind`
   and adds an orbit `PolyLine` + `Trail` per non-star body; `removeView()` disposes and drops per-body caches
5. `updateSunState()` — luminosity ∝ (M/M☉)^3.5 with a brown-dwarf branch below 0.08 M☉, temperature from
   luminosity and radius, blackbody colour
6. exposure: irradiance at the focus body ^(−0.85), log-smoothed; shaders get `exposureComp = 1/exposure`
7. camera surface stop from `solidRadius` (never `visualRadius`)
8. `updateSiteLock`, `flyWithKeys`, `rig.update`, `scene.setNearForDistance`
9. fill `ViewContext`: origin (camera pos, km), jd, simTime, realTime, sunPos, sunRadiusKm, sunColor,
   sunLuminosity, sunWatts, falloff, exposureComp, dioramaAmount, dioramaPlanet=800, dioramaSun=30,
   minPixelRadius=3.5, pixelsPerRadian, bodies, cameraQuaternion, textureCeiling, pixelRatio, frustum, focusId
10. `view.update(ctx)` for every view
11. `updateLabels()`, `updateLines()`
12. belt and sky updates
13. `effects.update`, `handleOutcomes()`, `updateAim()`
14. label-layer visibility, texture sweep timer
15. HUD update at ~8 Hz (`hudTimer > 0.12`)
16. exposure/bloom to the scene, `pumpTextureUploads()`, render

**Modes** `explore | asteroid | erase` (`setMode`, `app.ts:226-234`): toggles panels, cancels aim on leaving asteroid.

**Input** (`bindInput`, `app.ts:947-1065`), one dispatcher for all modes:
- Explore: left-drag orbit, right/shift/middle-drag pan, wheel log zoom, click selects, double-click (< 380 ms)
  flies to the body.
- Asteroid: left-click on empty space starts a hand aim on the near-target plane (`startAim`/`aimPlanePoint`), a
  live `predictPath` once per frame when dirty, drawn as path line + hit marker + spawn marker; left-click on a body
  runs `autoAim(universe, from, target.id, speedKms, radius)` after backing the spawn point off any surface, then
  `launch()` makes a `Body` with density/colour from `COMPOSITION_DENSITY`/`COMPOSITION_COLOR`. Right-drag orbits.
- Erase: left-drag paints a screen-space brush (`eraseAt`), wheel resizes it.
- Keys: Space pause, `[`/`]` warp ÷×4, `r` rewind, `x` asteroid, `z` erase, `f` fly-above selection, `t` tabletop,
  `h` help, `c` cinematic toggle, `0-9` select planet by `PLANET_ORDER`, `Esc` reset mode/close overlays. WASD +
  shift pan, Q/E yaw (`flyWithKeys`).

**Cinematic impact camera**: `handleOutcomes()` → `effects.spawn`, `lastImpact`, HUD report; if `settings.cinematic`
then `cinematicUntil = now + 9000` and `lookAtImpact()` (altitude from fireball/crater size, yaw/pitch at the impact
normal). `updateSiteLock()` keeps the camera on the site in body-fixed coordinates as the body rotates until
`siteLock.until` or user input.

**Undo**: `undoImpact()` pops sim history, removes the impactor, `rebindAfterRestore()` tears down and rebuilds every
view and re-binds `rig.focus`/`selected` by id; the same path serves NaN recovery.

**Presets** (`types.ts:27-36`): Chelyabinsk … Theia, each sets diameter/composition/speed.

**Settings** (`types.ts:38-50`, defaults `app.ts:50`): `moons: all|major|none`, `textures` (streaming ceiling px),
`orbits/trails/labels/belt/bloom/cinematic` (true), `diorama` (false), `falloff` (0.5; 0 flat, 2 physical).
`AsteroidSettings`: diameterM, composition, speedKms, name? (default 20 m rock at 20 km/s). Warp clamps to [1, 1e8];
the HUD slider is log10.

`main.ts` hangs the `App` on `window.spayly` for `scripts/shoot.mjs`; the Godot build needs an equivalent scripted
driver (CaptureDriver actions) so parity screenshots are automatable.

## 2. Render (`src/render/`)

- **units.ts**: `RENDER_SCALE = 1e-3` (1 unit = 1000 km); sim ecliptic z-up → render y-up via
  `(x, y, z) → (x, z, −y)` and back; `blackbody(tempK)`.
- **viewContext.ts**: `ViewContext`; `MapStream` per-body texture state machine (`due()/accept()`); `onScreen()`;
  `placeLabel()`; `irradiance()`; `solidRadius()` = true radius × diorama, capped at ¼ of the distance to the
  nearest heavy neighbour; `visualRadius()` adds the 3.5 px legibility floor and is cosmetic only; `occludersFor()`
  picks ≤ 4 shadow casters; `decay()` for heat/dust/magma fades.
- **camera.ts** `CameraRig`: orbit, pan (offset-based so it survives follow), `flyTo` (smoothstep, log-interpolated
  distance), `zoom()` multiplicative until the surface floor, then FOV narrows 50° → 6° instead of closing distance;
  `focus`/`focusRadiusKm`; `rayDir()`; camera stays at the render origin.
- **scene.ts**: log depth buffer, ACES tone mapping, bloom pass, CSS2D labels, `setNearForDistance`, exposure/bloom
  setters.
- **planetView.ts**: surface sphere, optional clouds, atmosphere shell, ring, glow sprite, canvas-painted crater
  overlay (ray-splatted craters with ejecta rays; repainted only when `damage.version` changes), ≤ 12 heat spots,
  magma/dust decay, climate cue (`uClimate` from equilibrium-temperature delta vs `baselineTeq`), per-map streaming
  for day/night/specular/normal/clouds/ring. Features: Chapman single-scattering atmosphere, night lights, specular
  oceans with ice suppression, cloud shadows, eclipse shadows from ≤ 4 occluders, ring shadows both ways, gas-giant
  limb darkening, climate tint (ice caps grow, deserts spread), craters, heat spots, dust tint, magma cracks.
- **rockView.ts**: shared welded icospheres (16 shapes, ~19k tris), fbm-displaced potato, real texture (Vesta, Ceres…)
  or procedural albedo, composition tint, magma glow.
- **sunView.ts**: granulation/cells/sunspots via fbm, limb darkening, optional real map; billboard corona (rays,
  halo, core) with a flare boost on impact.
- **belt.ts**: point cloud for `TestParticles` (main belt, Trojans, Kuiper) with family colours.
- **sky.ts**: `milky_way.jpg` (galactic coordinates; oriented by galactic pole and centre) or procedural, plus 9000
  blackbody-coloured stars.
- **lines.ts**: `PolyLine` (camera-relative fading conic) and `Trail` (ring buffer, optionally relative to a moving
  primary so a moon's trail is its orbit, not a corkscrew).
- **effects.ts** `ImpactEffects`, cap 8, density-thinned when concurrent: flash, fireball/plume, blast ring,
  ballistic ejecta curtain, shatter debris cloud, meteor streak; parented to the struck body so they rotate with it.
- **orientation.ts**: `bodyOrientation()` (IAU pole/W → quaternion, or seeded tumble), `worldToBodyFrame()`
  (injected into `resolveCollision`), `bodyDirToUv()`.
- **textures.ts / mapDecode.ts / textureWorker.ts / gapFill.ts / noise.ts**: see §5.

## 3. Shaders (`src/render/shaders/`)

- `common.ts`: 3D value noise/fbm, Chapman function, soft sphere shadow with the Sun's angular radius, ray–sphere,
  occluder loop (≤ 4), shared sphere vertex shader.
- `atmosphere.ts`: single-scattering shell, Chapman optical depths for sun and view rays, 7 clustered samples,
  Rayleigh + Mie phases, dust tint, occluder shadows.
- `planet.ts`: `PLANET_FRAG` (day/night/specular/normal/clouds/damage/ring shadow/heat/magma/climate/gas limb
  darkening), `CLOUD_FRAG`, `RING_FRAG` (self-shadow, near/far-side asymmetry).
- `sun.ts`: `SUN_FRAG`, billboard vertex, `CORONA_FRAG`.
- `misc.ts`: rock vertex/fragment (seamless equirect UV, magma cracks), ejecta and fireball (hot → warm → ash ramp),
  generic additive point sprite (belt, stars).
Every three.js shader carries log-depth chunks; in Godot the depth strategy is the per-frame near/far with the
far ≤ near × 1e6 cap from spike M1c.

## 4. HUD (`src/ui/hud.ts`, `format.ts`, `index.html`)

- **Top bar**: date, warp label, rewind, pause, slower/faster (÷×4), log10 warp slider, fps, body count + substeps,
  a "limited" badge when the substep budget throttles, help.
- **Left**: body list (cap 80, rebuilt on `bodiesVersion`/selection; row → select + fly above). View panel:
  texture ceiling 2K/4K/8K/16K (pruned to what the GPU supports), moons all/major/none, diorama, orbits, trails,
  labels, belt, bloom, cinematic, falloff 0/0.5/2, tabletop/inner/outer presets.
- **Right, inspector**: name, kind, follow / above / reset-body; log sliders for mass (±3 decades) and radius (±1)
  writing the body directly; stats: density, gravity, escape velocity; star → luminosity, temperature, spectral
  class; else primary, period, distance, speed, eccentricity, peri/apo (via `stateToElements`), surface and
  equilibrium temperature (from the view), impacts and absorbed counts from `damage`.
- **Bottom**: mode buttons; erase panel hint; asteroid panel with preset buttons, log sliders for diameter and speed,
  composition select, aim readout.
- **Overlays**: erase brush cursor, hint toast, impact report (title impactor → target, classification with
  absorb/shatter/disperse special cases, stats built branch by branch from `ImpactOutcome.report`: entry/airburst,
  crater, fireball, thermal, seismic, air blast, ejecta, tsunami, binding-energy ratio; comparisons; undo and
  go-to-impact buttons), help overlay.
- `Hud.update()` at 8 Hz; never writes an input the user is dragging.
- `format.ts`: sci, num, mass (kg + M⊕/M☉/Moons), distance (km → AU/Mkm/km/m), lengthM, speed, duration, warp
  (with ◀ for rewind), date (JD → UTC incl. BCE), energy (J + Mt/kt/t TNT), temperature (K + °C), angle.

## 5. Textures

- Width rules (`viewContext.ts:44-74`): floor 256; smallest power of two ≥ 4 × on-screen radius, capped at the
  ceiling; upgrade at once, downgrade only at ¼; re-poll every 0.5 s; focus body gets 2048 immediately then the
  ceiling; off-screen drops to the floor. In Godot this whole mechanism is replaced by engine streaming (spike
  M1b): `STREAMING_UV` in every planet shader, memory budget on, focus-first via `MaxLodOverride = 1` + flush
  guarded by the resident width.
- LOD manifest and 512/2048 copies exist for the web; the Godot build imports the full maps as
  `streamed_texture_2d` and does not need them.
- `gapFill.ts` inpaints un-imaged hemispheres (Uranian moons, Triton, Charon, Proteus) with a pull-push pyramid and
  fractal re-roughening at 2048, only where `visual.fillGaps` is set. `mapDecode.ts` also applies roll and tint
  variants (Phobos reused for Proteus, Deimos for Nereid, Vesta for Pallas). For Godot these variants are pre-baked offline into
  the game's texture folder (gitignored) by a tool script, not done at runtime.
- Procedural fallbacks: planet (rock/gas/ice fbm), sun, ring strip, Milky Way, radial sprite, white — used when a
  map is missing (a clone without Git LFS has pointer stubs, not maps).
- Maps: sun, mercury, venus_atmosphere (+ unused venus_surface), earth_day/night/clouds/normal/specular, mars,
  jupiter, saturn (procedural ring), uranus (procedural ring), neptune, moon, phobos, deimos, io, europa, ganymede,
  callisto, mimas, enceladus, tethys, dione, rhea, titan, iapetus, miranda, ariel, umbriel, titania, oberon,
  triton, charon (fillGaps), ceres, vesta (fillGaps), pluto, eris, haumea, makemake, milky_way.

## 6. Cross-cutting rules the port keeps

- 1 unit = 1000 km and the y-up swap; floating origin (spike M1c); the camera stop uses `solidRadius`, never
  `visualRadius`.
- Sun state from live mass; auto-exposure from irradiance at the focus; `exposureComp` to every glow shader.
- Impact effects parented to the struck body; cap of 8.
- Crater repaint only when `damage.version` changes.
- Orbit lines drawn relative to the body's dynamic primary (`primaryOf`, re-evaluated every 10th frame), hidden at
  warps too fast to sample; labels: disc occlusion test, then greedy AABB declutter ranked selection/focus > kind
  (star > planet > moon > asteroid > fragment) > screen size.
