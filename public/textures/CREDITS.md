# Texture credits

Textures are downloaded by `npm run assets` (`scripts/fetch-assets.mjs`) and committed through Git LFS.
Anything missing is generated procedurally at runtime, so the app runs without them (e.g. a clone without LFS). The script checks
every map it writes and every map already on disk: 2:1 equirectangular, no burned-in text, graticule or
poster layout, no dark border column at 0°, and no two bodies shipping the same image.

## Sun, planets, sky

| Map | Source | Licence |
| --- | --- | --- |
| Sun, Venus (surface, cloud deck), Saturn, Uranus, Neptune | Solar System Scope (`solarsystemscope.com/textures`) | CC BY 4.0 |
| Mercury | Commons `Mercury - complete mono basemap 2500mpp equirectangular.png` (MESSENGER) | Public domain (NASA/JHUAPL/CIW) |
| Earth day | NASA Blue Marble Next Generation, `world.topo.bathy.200412.3x21600x10800.jpg` | Public domain (NASA Earth Observatory) |
| Earth night | NASA Black Marble 2012, `dnb_land_ocean_ice.2012.13500x6750.jpg` | Public domain (NASA Earth Observatory) |
| Earth clouds, normal, specular | Commons `Solarsystemscope texture 8k earth {clouds,normal map,specular map}` | CC BY 4.0 — Solar System Scope |
| Moon | NASA SVS 4720, `lroc_color_poles_16k.tif` (LRO) | Public domain (NASA/GSFC/ASU) |
| Mars | Commons `Tianwen-1 Mars Global Color Orthomosaic (HX1 GRAS MoRIC DOM 076m Global 00N00E A).png` | CC BY-SA 4.0 — CNSA / GRAS |
| Jupiter | Commons `Jupiter map by Askaniy.jpg` | CC BY-SA 3.0 — Askaniy |
| Saturn's rings | none — procedural (`src/render`), since every published ring image found is a press photo | — |
| Milky Way | ESO `eso0932a` all-sky panorama | CC BY 4.0 — ESO/S. Brunier |

The Milky Way map must stay an all-sky map in galactic coordinates at 2:1 — `src/render/sky.ts`
orients the backdrop sphere by the galactic pole and centre, so a ground-level panorama would put a
horizon and observatory lights into the sky.

## Moons

| Body | Source | Licence |
| --- | --- | --- |
| Phobos | Commons `Phobos map by Askaniy.png` | CC BY-SA 3.0 — Askaniy, from Viking and Mars Express imagery |
| Deimos | Commons `Deimos map by Askaniy.png` | CC BY-SA 3.0 — Askaniy |
| Io | USGS `Io_GalileoSSI-Voyager_Global_Mosaic_ClrMerge_1km.tif` | Public domain (NASA/JPL/USGS) |
| Europa | USGS `Europa_Voyager_GalileoSSI_global_mosaic_500m.tif`, coloured to Europa's hue by the script | Public domain (NASA/JPL/USGS) |
| Ganymede | Commons `Ganymede map by Askaniy.png` | CC BY-SA 3.0 — Askaniy |
| Callisto | Commons `Callisto map by Askaniy.png` | CC BY-SA 3.0 — Askaniy |
| Mimas | Commons `Map of Mimas colorized 2014-04 PIA18437.jpg` | Public domain (NASA/JPL/SSI) |
| Enceladus, Tethys, Dione, Rhea, Iapetus | Commons `<name> Color Map.jpg` | Public domain (NASA/JPL/SSI) |
| Titan | Commons `PIA22770-SaturnMoon-Titan-Surface-20181206.jpg`, coloured to Titan's haze by the script | Public domain (NASA/JPL/University of Nantes/University of Arizona) |
| Miranda, Ariel, Umbriel, Titania, Oberon | Commons `<name> map JPL USGS.jpg` | Public domain (NASA/JPL/USGS, Voyager 2) |
| Triton | NASA Photojournal `PIA18668` (Paul Schenk, 600 m/px), 30° graticule painted out by the script | Public domain (NASA/JPL-Caltech/Lunar and Planetary Institute, Voyager 2) |
| Charon | Commons `Charon map iau1803c.jpg` | Public domain (NASA/JHUAPL/SwRI, New Horizons) |
| Proteus, Nereid | Phobos' and Deimos' maps, tinted and rotated | CC BY-SA 3.0 — Askaniy; neither body has ever been mapped |

Titan's map is a near-infrared surface mosaic and Europa's is a clear-filter mosaic, so both are
greyscale at source. `fetch-assets.mjs` bakes a colour into the file rather than using the catalogue's
runtime `tint`, which would re-process every pixel of a 16K map on each size change.

Voyager 2 only photographed the southern hemispheres of the Uranian moons and about 40% of Triton,
and New Horizons only resolved one side of Charon, so those maps have large blank regions. The
texture loader grows the imaged terrain over the blanks (`fillGaps` in `src/render/textures.ts`)
rather than rendering a hole — the filled area is invented, not data. Europa's and Ceres' smaller
polar gaps are filled once by the fetch script instead, which keeps them off that 2048-px path.

The five Uranian moons' 1440-px maps are the best that exists. A search of the USGS controlled
mosaics and Paul Schenk's reprocessed Voyager work found nothing carrying more real detail; only a new
mission to Uranus will change that.

## Dwarf planets and asteroids

| Body | Source | Licence |
| --- | --- | --- |
| Pluto | Commons `Pluto-map-sept-16-2015.jpg` | CC BY-SA 4.0 (from NASA/JHUAPL/SwRI imagery) — covers all 360°, including the far side New Horizons only saw on approach |
| Vesta | Commons `Vesta map for GeoHack.png` | Public domain (NASA/JPL/DLR, Dawn) |
| Pallas | Vesta's map, tinted and rotated | Public domain (NASA/JPL/DLR, Dawn) — Pallas has only ever been resolved as a few pixels |
| Ceres | USGS `Ceres_Dawn_FC_DLR_global_20ppd_Oct2015.tif` | Public domain (NASA/JPL/DLR, Dawn) |
| Eris, Haumea, Makemake | Solar System Scope `4k_<name>_fictional.jpg`, one each | CC BY 4.0 |

The Eris, Haumea and Makemake maps are **artists' impressions** — no spacecraft has resolved those
surfaces.

Pallas, Proteus and Nereid have no maps at all, so their catalogue entries point at a comparable
body's file: Pallas uses `vesta.jpg`, Proteus `phobos.jpg` and Nereid `deimos.jpg`. Each borrower has
its own tint and longitude offset (`tint` and `roll` in `src/sim/satellites.ts`) so the pairs do not
render as identical twins, and the tints follow the real bodies' measured colours — Pallas bluish,
Proteus dark grey. `fetch-assets.mjs` fails if two bodies share a file without that.
