# September 2026: what changed

> **The big changes from `088dd50` to `9b757d0`.** Written as the web build is frozen and work moves
> to a Godot port.

## TL;DR

- 🌍 **10 → 39 bodies.** All 23 major moons plus Pluto, Ceres, Eris, Haumea, Makemake, Vesta and
  Pallas, with orbits from JPL and tidally locked moons facing their planets.
- 🖼️ **Maps that are real surfaces.** Europa was a poster, Uranus and Neptune were cut-out globe
  templates, Ceres had crater names printed on it, and three bodies were byte-identical copies of
  others. All of them are replaced, and an automated guard stops any of that coming back.
- 🔍 **Detail follows the camera.** Distant bodies get tiny maps, the body you double-click gets its
  best map at once, and a lens zoom takes Earth and the Moon to 16K.
- ⚡ **No more multi-second freezes.** Maps decode in workers, auto-aim no longer blocks the page for
  33 s in a debris cloud, and collision cascades have a bounded cost.
- 🧭 **Context for agents.** A `CLAUDE.md` per layer, skills for the recurring workflows, and a test
  that enforces the import rules.
- ➡️ **Next:** the Godot port. The web build's limits are its algorithms more than its language, but
  GPU compute is the way past 100k particles.

## Before and after

| | Before | After |
| --- | --- | --- |
| Bodies | 10 | **39** (240 with debris) |
| Auto-aim with 240 bodies | **33 s**, and missed | **80 ms** in the browser, and hits |
| Worst frame zooming Earth to 16K | 2,817 ms | **100 ms** |
| Worst frame zooming the Moon to 16K | 1,333 ms | **17 ms** |
| Long frames during startup | ~28 | **1** |
| Maps for bodies you aren't looking at | 115 MiB, all 1024 px | **~7 MiB**, 256 px |
| Double-clicked body's map | 1024 px, however long you waited | **2048 in 200 ms, 8192 by 1.6 s** |
| Undo snapshots per collision cascade | one per hit (a GC storm) | **one per cascade** |
| Physics budget during a mid-frame shatter | overran 1.85× | **kept** |
| Maps that were posters, templates, photos or clones | 9 | **0**, guarded |
| Tests | 20 | **46** |

## Timeline

```mermaid
timeline
    title Big changes, oldest first
    section Structure
        9ceab2e and ed43633 : app layer split out, cycle with ui broken
                            : CLAUDE.md per layer, skills, layering test
    section Content
        088dd50 : 39 bodies from JPL elements
                : erase mode, swept collisions, dead top bar fixed
    section Textures
        6300446 : maps stream with the camera
        4992618 : lens zoom past the surface, Earth and Moon at 16K
        b2b9a6b : sweep no longer frees maps still in use
        ab5c9cb : worker decode, strip uploads, orbit trails, smooth rocks, labels
    section Assets
        bf20992 : real all-sky Milky Way, not a ground photo
        e0e7890 : posters, gore templates and clones replaced, asset guard
        7eab79c : Schenk's Triton with its grid painted out, low-res copies
    section Stability
        79c169b : gap fill stops staining invented terrain
        c324e78 : auto-aim 33 s to 80 ms, bounded cascades, NaN rollback
        9b757d0 : focused body gets its best map, distant ones almost none
```

---

## 1. The solar system got full

`088dd50` took the catalogue from 10 bodies to 39. Elements come from JPL (satellite mean elements,
GM and radius tables, and the Small-Body Database) through `scripts/fetch-ephemeris.mjs`, which
regenerates `src/sim/satellites.ts`.

```mermaid
flowchart LR
    JPL["JPL tables<br/>mean elements, GM, radii,<br/>Small-Body Database"] --> F["fetch-ephemeris.mjs"]
    F --> S["satellites.ts<br/>(generated, committed)"]
    S --> C["catalog.ts"]
    C --> U["Universe<br/>39 bodies"]
    Frames["frames.ts<br/>Laplace plane and equator<br/>to ecliptic"] --> C
    Lock["Tidal locking<br/>spin axis = orbit normal<br/>prime meridian faces planet"] --> U
```

The same commit made asteroids real physics citizens. Contacts are swept, so a 20 m rock at 20 km/s
can't pass through another. Disruption adds the Benz–Asphaug strength term, and the frame budget is
counted in pair evaluations, so more bodies throttle time warp rather than the frame rate.

## 2. Texture detail follows the camera

The largest body of work. A map's size is decided every half-second by what it covers on screen.
It's built off the main thread and uploaded in slices, so nothing stalls a frame.

```mermaid
flowchart TB
    subgraph offline["npm run assets (offline)"]
        direction LR
        DL["Download or cache<br/>.assets-raw/"] --> TR["Trim dark borders"]
        TR --> DG["Paint out a burned-in grid<br/>(degrid, Triton)"]
        DG --> FP["Fill polar no-data<br/>(Europa, Ceres)"]
        FP --> CAP["Cap width<br/>(round trip at least 45 dB)"]
        CAP --> CHK{"Asset guard"}
        CHK -- pass --> OUT["public/textures/map.jpg"]
        OUT --> LOD["lod/map@512.jpg<br/>lod/map@2048.jpg<br/>+ manifest.json"]
        CHK -- fail --> NEXT["Try the next source"]
    end

    subgraph runtime["In the browser, every half second per body"]
        direction LR
        DUE["MapStream.due<br/>picks a width"] --> RES["resolveSource<br/>smallest file wide enough"]
        RES --> W["Texture worker x3<br/>decode, flip, fill gaps"]
        W --> UP["Strip upload<br/>16 MB per frame"]
        UP --> SW["swapTexture<br/>old width freed on next sweep"]
    end

    LOD -.-> RES
    OUT -.-> RES
```

### How a width is chosen

```mermaid
flowchart TD
    A["Body's view renders"] --> F{"Is it the<br/>camera's focus?"}
    F -- "yes, just now" --> F1["Skip the half-second wait<br/>load 2048 at once"]
    F1 --> F2["Then the quality ceiling<br/>(High = 8192)"]
    F -- no --> V{"Inside the<br/>camera frustum?"}
    V -- no --> MIN["Drop to 256"]
    V -- yes --> P["On-screen radius x 4<br/>x device pixel ratio<br/>round up to a power of two"]
    P --> H{"Bigger than now?"}
    H -- yes --> UPG["Upgrade"]
    H -- no --> Q{"At most a quarter<br/>of the current width?"}
    Q -- yes --> DWN["Downgrade"]
    Q -- no --> HOLD["Hold<br/>(no flip-flopping)"]
```

What made that fast:

- **Workers** (`ab5c9cb`). Chromium had been decoding 16K JPEGs synchronously inside the GPU upload,
  which cost seconds per zoom. Three workers now decode and hand back pixels already flipped, and the
  main thread uploads 16 MB slices each frame.
- **Low-res copies** (`7eab79c`, `9b757d0`). A body that is a dot on screen reads a 512 px copy
  instead of decoding a 16K original.
- **Lens zoom** (`4992618`). The camera stops at the surface, so zooming further narrows the field of
  view from 50° down to 6°. That's the only way a 16384 px map ever reaches one texel per pixel.
- **Sweep safety** (`b2b9a6b`). three.js has no finaliser, so every view keeps re-requesting what it
  shows, or the sweep frees a map that is still bound.

## 3. Maps that are actually surfaces

An audit found a lot of cartography where imagery should have been:

| Body | Was | Now |
| --- | --- | --- |
| Europa | USGS geologic-map **poster**: 57% white, legend, title block | USGS Voyager/Galileo mosaic, 16384 px |
| Uranus, Neptune | Printable **globe-gore templates**, nine cut-outs | Solar System Scope maps |
| Ceres | **18 crater names** and a full grid printed on | DLR Dawn mosaic |
| Makemake, Eris, Haumea | **Byte-identical copies** of Ceres, Pluto and Pallas | Their own maps |
| Saturn's ring | A **press photo** with "ISS" and "VIMS" printed on it | The procedural ring |
| Milky Way | A **ground photo**: horizon, trees, lit buildings | ESO all-sky map in galactic coordinates |
| Io | 1225 px, three hard seams | USGS colour mosaic, 8192 px |
| Triton | A smeared reprocessing | Paul Schenk's 600 m/px mosaic, black grid painted out |

The guard in `scripts/fetch-assets.mjs` runs on every download and on everything already on disk
(`npm run assets -- --check`):

```mermaid
flowchart LR
    M["Candidate map"] --> R{"2:1<br/>equirectangular?"}
    R -- no --> X["Reject, never stretch"]
    R -- yes --> W{"Under 0.5%<br/>pure white?"}
    W -- no --> X2["Reject: poster,<br/>legend or labels"]
    W -- yes --> D{"No black grid<br/>lines?"}
    D -- no --> X3["Reject: graticule"]
    D -- yes --> B{"No dark border<br/>column?"}
    B -- no --> X4["Reject: seam at 0 degrees"]
    B -- yes --> U{"Unlike every<br/>other map?"}
    U -- no --> X5["Reject: clone"]
    U -- yes --> FIC{"Not an artist's<br/>impression of a<br/>real body?"}
    FIC -- no --> X6["Reject: fictional"]
    FIC -- yes --> OK["Accept"]
```

Width caps are measured, not guessed. Each map is squeezed to a candidate width and back up, then
compared with the source; the cap is the smallest width that stays at least 45 dB (visually
lossless). That test overturned an earlier estimate that would have blurred ten moons.

## 4. The freezes and crashes

### Auto-aim: 33 s → 80 ms

The predictor integrated every body against every other on the click. Worse, it stepped at the pace
of the fastest orbit anywhere, Phobos, even when aiming at Earth.

```mermaid
flowchart LR
    subgraph before["Before"]
        direction TB
        B1["Every body attracts<br/>every other: n squared"] --> B2["Step set by the fastest orbit<br/>anywhere, Phobos"]
        B2 --> B3["200 fragments near the Moon<br/>33 s, still misses"]
    end
    subgraph after["After"]
        direction TB
        A1["Heavy bodies attract<br/>(above 1e-10 of the Sun)"] --> A2["Light bodies ride their host<br/>O(1) each, still hittable"]
        A2 --> A3["Stop when not closing in<br/>+ a pair-evaluation budget"]
        A3 --> A4["80 ms, and hits"]
    end
    before ~~~ after
```

### Collision cascades

```mermaid
sequenceDiagram
    participant S as Shatter
    participant F as Fragments
    participant H as Undo history
    participant E as Impact effects
    S->>H: one snapshot, before the first hit
    loop fragments keep colliding
        F->>H: coalesced into the same undo point
        F->>E: at most 8 live, with fewer particles
    end
    Note over S,E: Physics budget counted as it is spent,<br/>so a shatter mid-frame can't overrun it
```

Also in `c324e78`:
- A non-finite body state now rolls back to the last undo point and pauses. Before, NaN silently
  switched collision detection off for good.
- Orbit lines stopped allocating about 540 objects per body per frame.

### Renderer fixes (`ab5c9cb`, `79c169b`)

- **Orbit lines across the Saturn system** were moon trails drawn relative to the Sun. They're now
  drawn relative to the planet, and hidden at warps too fast to sample.
- **Asteroids** are a welded 19k-triangle mesh with smooth normals, not 500 faceted triangles.
- **Labels** sit above the body on screen, hide behind nearer bodies, and give way when they overlap.
- **Gap fill** no longer pulls from the dark ringing at a map's no-data edge, which had stained whole
  invented hemispheres dark and left a speckled rim on Charon and Umbriel.

## 5. Structure and agent context

```mermaid
flowchart BT
    sim["src/sim<br/>physics, km and SI<br/>no three, no DOM"]
    render["src/render<br/>three.js views"]
    ui["src/ui<br/>HUD"]
    types["src/app/types.ts<br/>imports nothing"]
    app["src/app<br/>loop, input, modes"]
    render --> sim
    app --> sim
    app --> render
    app --> ui
    ui --> types
    ui -. "import type only" .-> app
    test["tests/layering.test.ts"] -. enforces .-> sim
    test -. enforces .-> render
    test -. enforces .-> ui
```

Each layer has its own `CLAUDE.md` (it loads only when that directory is touched). `.claude/skills/`
covers the recurring workflows: `physics-change`, `visual-check`, `add-body`, `add-hud-control` and
`update-context`.
Every rule names what enforces it, or is marked *unenforced*.

---

## What carries over to Godot

The port is the next step. The measurement that drove it: in this JavaScript, all-pairs gravity takes
**282 ms per step at 10,000 bodies**, while a debris tier that feels the planets but not other debris
takes **5.9 ms**, 48× faster in the same language. Language buys about 2×; algorithms and GPU compute
buy orders of magnitude.

```mermaid
flowchart LR
    subgraph keep["Carries over as is"]
        K1["fetch-assets.mjs<br/>guard, degrid, caps, LOD copies"]
        K2["Physics design<br/>leapfrog, swept contacts,<br/>Benz-Asphaug, impact model"]
        K3["The tests<br/>as the spec to port against"]
        K4["Lessons<br/>focus-first streaming,<br/>bounded cascades, NaN rollback"]
    end
    subgraph port["Rewritten in Godot"]
        P1["C# sim library<br/>no engine references"]
        P2["GLSL compute<br/>GPU debris, then SPH"]
        P3["Godot shaders<br/>planets, atmospheres,<br/>black-hole lensing"]
        P4["Built-in texture streaming<br/>(4.8 dev)"]
    end
    K1 --> P4
    K2 --> P1
    K3 --> P1
    K4 --> P2
```
