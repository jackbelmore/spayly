// Downloads the body maps into public/textures/ and checks every one of them.
//
//   npm run assets                 fetch whatever is missing or fails the checks
//   npm run assets -- --force io   re-fetch named maps even if they pass
//   npm run assets -- --check      only run the checks against what is on disk
//
// Raw downloads are cached in .assets-raw/ (gitignored) so a re-run does not pull 200 MB mosaics again.
import { mkdir, writeFile, readFile, readdir, stat } from 'node:fs/promises'
import { join, extname } from 'node:path'
import { createHash } from 'node:crypto'
import sharp from 'sharp'

const OUT = join(process.cwd(), 'public', 'textures')
const RAW = join(process.cwd(), '.assets-raw')
const SATELLITES_TS = join(process.cwd(), 'src', 'sim', 'satellites.ts')
const CATALOG_TS = join(process.cwd(), 'src', 'sim', 'catalog.ts')
const UA = 'spayly-asset-fetch/1.2 (https://github.com/jackbelmore/spayly)'

const commons = (file) => {
  const under = file.replace(/ /g, '_')
  const f = encodeURIComponent(under).replace(/%2C/g, ',')
  const h = createHash('md5').update(under).digest('hex')
  const dir = `${h[0]}/${h.slice(0, 2)}`
  return [
    `https://upload.wikimedia.org/wikipedia/commons/${dir}/${f}`,
    `https://commons.wikimedia.org/wiki/Special:FilePath/${encodeURIComponent(file)}`,
  ]
}
const sss = (file) => `https://www.solarsystemscope.com/textures/download/${file}`
const usgs = (file) => `https://asc-pds-services.s3.us-west-2.amazonaws.com/mosaic/${file}`

// [file, candidate URLs, options]. Candidates are tried in order; the first that downloads AND passes
// the checks wins. Dark border rows/columns are trimmed from every imagery map. Options:
//   maxW       cap the width below the 16384 default (use where the source holds less real detail).
//              A cap is the smallest power of two whose round trip - down to the cap and back up to
//              the source's width - still measures at least 45 dB against the source over imaged
//              pixels, i.e. visually lossless. Measure it that way, not by an energy cutoff: a
//              per-block "95% of spectral energy" estimate once suggested Europa at 8192 and the
//              Saturnian moons at 4096, and those caps measured 34-42 dB, a visible loss.
//   degrid     paint out a graticule burned in as 1-px dark lines every this many degrees
//   fillPoles  replace no-data black at the poles with the nearest imaged row, so the loader does not
//              have to run fillGaps (which caps the map at 2048) just to hide a polar cap
//   colour     bake a hue into a greyscale mosaic (sharp's tint keeps luminance). Done here rather than
//              with the catalogue's runtime `tint`, which is a CPU pass over every pixel at every size
//   quality    JPEG quality (default 90)
//   mask       a data mask, not imagery: skip the burned-in-annotation check
//   whiteMax   allowed fraction of pure-white pixels (default 0.005)
//
// Every body map must be 2:1 equirectangular surface imagery: no posters, gore templates, graticules,
// labels or legends, and no two bodies may ship the same file. A body with no map of its own points its
// catalogue entry at a comparable body's file and sets `tint` and `roll` — see checkSharedMaps().
const FILES = [
  ['sun.jpg', [sss('8k_sun.jpg'), commons('Map of the full sun.jpg')]],
  ['mercury.jpg', [commons('Mercury - complete mono basemap 2500mpp equirectangular.png'), commons('Mercury global map 2013-05-14 bright.png')]],
  ['venus_surface.jpg', [sss('8k_venus_surface.jpg'), commons('Map of Venus.png')]],
  // Solar System Scope's "4k" cloud deck holds ~2048 px of detail; storing it wider only costs VRAM.
  ['venus_atmosphere.jpg', [sss('4k_venus_atmosphere.jpg'), commons('Cylindrical Map of Venus.jpg')], { maxW: 2048 }],
  // Earth and the Moon are what you fly closest to, so they come from the largest published sources
  // rather than the 5k/8k sets. sharp decodes the Moon's TIFF and caps both at 16384.
  ['earth_day.jpg', ['https://eoimages.gsfc.nasa.gov/images/imagerecords/73000/73909/world.topo.bathy.200412.3x21600x10800.jpg'], { whiteMax: 0.02 }],
  ['earth_night.jpg', ['https://eoimages.gsfc.nasa.gov/images/imagerecords/79000/79765/dnb_land_ocean_ice.2012.13500x6750.jpg', commons('Earth lights vs population density.png')]],
  ['earth_clouds.jpg', [commons('Solarsystemscope texture 8k earth clouds.jpg')], { mask: true }],
  ['earth_normal.jpg', [commons('Solarsystemscope texture 8k earth normal map.tif')], { mask: true }],
  ['earth_specular.jpg', [commons('Solarsystemscope texture 8k earth specular map.tif')], { mask: true }],
  ['moon.jpg', ['https://svs.gsfc.nasa.gov/vis/a000000/a004700/a004720/lroc_color_poles_16k.tif', commons('Moonmap from clementine data.png')]],
  ['mars.jpg', [
    'https://upload.wikimedia.org/wikipedia/commons/thumb/5/5a/Tianwen-1_Mars_Global_Color_Orthomosaic_%28HX1_GRAS_MoRIC_DOM_076m_Global_00N00E_A%29.png/16384px-Tianwen-1_Mars_Global_Color_Orthomosaic_%28HX1_GRAS_MoRIC_DOM_076m_Global_00N00E_A%29.png',
    commons('Tianwen-1 Mars Global Color Orthomosaic (HX1 GRAS MoRIC DOM 076m Global 00N00E A).png'),
    commons('Mars map.jpg'),
  ]],
  ['jupiter.jpg', [commons('Jupiter map by Askaniy.jpg'), commons('Global Map of Jupiter (PIA19643).jpg')], { maxW: 8192 }],
  // Solar System Scope's "8k" Saturn is 4096 wide with ~2048 px of real detail.
  ['saturn.jpg', [sss('8k_saturn.jpg'), commons('Saturn map by Askaniy.png')], { maxW: 2048 }],
  // No saturn_ring.png: every published ring "map" found so far is a press image (planet, labels,
  // colour bar), and the renderer's procedural ring strip is better than any of them.
  // Not UranusGlobe.png / NeptuneGlobe.png — those are paper-globe gore templates, not maps.
  ['uranus.jpg', [sss('2k_uranus.jpg'), commons('Solarsystemscope texture 2k uranus.jpg')]],
  ['neptune.jpg', [sss('2k_neptune.jpg'), commons('Solarsystemscope texture 2k neptune.jpg')]],
  // MUST be an all-sky map in galactic coordinates at 2:1 — src/render/sky.ts orients the backdrop
  // sphere by the galactic pole and centre. A ground-level panorama puts a horizon, trees and
  // observatory lights into the sky of the solar system, and a non-2:1 one is squashed onto the
  // sphere. ESO's is the same projection as the Solar System Scope fallback but far less washed out.
  ['milky_way.jpg', [
    'https://cdn.eso.org/images/large/eso0932a.jpg',
    sss('8k_stars_milky_way.jpg'),
    commons('Solarsystemscope texture 8k stars milky way.jpg'),
  ]],

  // Moons
  ['phobos.jpg', commons('Phobos map by Askaniy.png'), { quality: 92 }],
  // ~1024 px of real detail; 2048 keeps a margin without shipping a 7 MB PNG.
  ['deimos.jpg', commons('Deimos map by Askaniy.png'), { maxW: 2048, quality: 92 }],
  // Not the "SIMP0 med" product: that is 1225 px wide with three hard seams. The 1 km mosaic is
  // 11445 px wide, 1 km/px at Io's circumference, but most of it is coarser coverage: a round trip
  // through 8192 measures 53.7 dB against the source, so the extra width holds nothing visible.
  ['io.jpg', [usgs('Io_GalileoSSI-Voyager_Global_Mosaic_ClrMerge_1km.tif'), usgs('Io_Galileo_SSI_Global_Mosaic_ClrMerge_1km.tif')], { maxW: 8192 }],
  // Not "Europa USGS 2024.png" — that is the geologic map poster (title block, legend, charts).
  ['europa.jpg', [usgs('Europa_Voyager_GalileoSSI_global_mosaic_500m.tif'), commons('Jupiter II-Europa map NASA JPL Voyager.jpg')], { fillPoles: true, colour: '#d8cdb8' }],
  ['ganymede.jpg', commons('Ganymede map by Askaniy.png')],
  ['callisto.jpg', commons('Callisto map by Askaniy.png')],
  ['mimas.jpg', commons('Map of Mimas colorized 2014-04 PIA18437.jpg'), { quality: 92, maxW: 4096 }],
  ['enceladus.jpg', commons('Enceladus Color Map.jpg'), { quality: 92 }],
  ['tethys.jpg', commons('Tethys Color Map.jpg'), { maxW: 8192 }],
  ['dione.jpg', commons('Dione Color Map.jpg'), { maxW: 8192 }],
  ['rhea.jpg', commons('Rhea Color Map.jpg')],
  // Near-IR surface mosaic, so it is grey; coloured to Titan's haze here. ~1440 px of real detail.
  ['titan.jpg', commons('PIA22770-SaturnMoon-Titan-Surface-20181206.jpg'), { maxW: 2880, colour: '#e0a860' }],
  ['iapetus.jpg', commons('Iapetus Color Map.jpg'), { maxW: 8192 }],
  ['miranda.jpg', commons('Miranda map JPL USGS.jpg')],
  ['ariel.jpg', commons('Ariel map JPL USGS.jpg')],
  ['umbriel.jpg', commons('Umbriel map JPL USGS.jpg')],
  ['titania.jpg', commons('Titania map JPL USGS.jpg')],
  ['oberon.jpg', commons('Oberon map JPL USGS.jpg')],
  // Paul Schenk's 600 m/px Voyager mosaic (PIA18668): 2209 px of real detail against the 1024 of the
  // Commons "no grid" map, and the cantaloupe terrain and polar-cap edge actually read. It ships with
  // a black 30-degree graticule, painted out here. The TIFF is lossless; the JPEG is the same map.
  ['triton.jpg', [
    'https://assets.science.nasa.gov/content/dam/science/psd/photojournal/pia/pia18/pia18668/PIA18668.tif',
    'https://assets.science.nasa.gov/content/dam/science/psd/photojournal/pia/pia18/pia18668/PIA18668.jpg',
    commons('Triton map no grid.jpg'),
  ], { degrid: 30, maxW: 8192 }], // 48.1 dB round trip once degridded
  ['charon.jpg', commons('Charon map iau1803c.jpg')],

  // Dwarf planets and asteroids
  ['pluto.jpg', commons('Pluto-map-sept-16-2015.jpg')],
  ['vesta.jpg', commons('Vesta map for GeoHack.png')],
  // Not PIA19625 (18 crater names and a graticule burned in) and not PIA19606 (a colour-coded
  // topography map with a graticule): the DLR Dawn FC clear-filter mosaic.
  ['ceres.jpg', [usgs('Ceres_Dawn_FC_DLR_global_20ppd_Oct2015.tif')], { fillPoles: true }],
  // No spacecraft has resolved these three; Solar System Scope's artist's impressions, one each.
  ['eris.jpg', [sss('4k_eris_fictional.jpg'), commons('Solarsystemscope texture 4k eris fictional.jpg')]],
  ['haumea.jpg', [sss('4k_haumea_fictional.jpg'), commons('Solarsystemscope texture 4k haumea fictional.jpg')]],
  ['makemake.jpg', [sss('4k_makemake_fictional.jpg'), commons('Solarsystemscope texture 4k makemake fictional.jpg')]],
]

const MAX_W = 16384 // largest texture almost every desktop GPU accepts; the app streams down from it

// ---------------------------------------------------------------------------------------------------
// Checks. These exist because maps that were posters, gore templates, press photos and byte-identical
// copies of other bodies all shipped at one point, and nothing noticed.

/** Downsampled RGB used by the checks, so a 16K map costs the same to check as a 2K one. */
async function probe(buf) {
  const meta = await sharp(buf, { limitInputPixels: false }).metadata()
  const w = Math.min(meta.width, 2048), h = Math.max(1, Math.round(w * meta.height / meta.width))
  const { data } = await sharp(buf, { limitInputPixels: false })
    .resize(w, h, { fit: 'fill' }).removeAlpha().toColourspace('srgb').raw().toBuffer({ resolveWithObject: true })
  const thumb = await sharp(buf, { limitInputPixels: false }).resize(64, 32, { fit: 'fill' }).toColourspace('b-w').raw().toBuffer()
  return { meta, w, h, data, thumb }
}

/** How much worse the 0°/360° seam is than an ordinary column step, mid-latitudes only. */
function seamScore({ w, h, data }) {
  const lum = (x, y) => { const i = (y * w + x) * 3; return data[i] + data[i + 1] + data[i + 2] }
  const y0 = Math.round(h * 0.2), y1 = Math.round(h * 0.8)
  const step = (a, b) => { let s = 0; for (let y = y0; y < y1; y++) s += Math.abs(lum(a, y) - lum(b, y)); return s / (y1 - y0) }
  let typical = 0
  for (let k = 1; k <= 8; k++) { const x = Math.floor(k * w / 9); typical += step(x, x + 1) }
  return step(w - 1, 0) / Math.max(typical / 8, 1)
}

/**
 * Straight dark lines across the imaged surface: a graticule drawn in black, which the white-pixel
 * test cannot see. Triton's PIA18668 arrived with one, 1-pixel lines of value 3-4 every 30 degrees,
 * and passed every check. Runs at full resolution because a 1-px line vanishes in any downsample.
 *
 * A column (or row) counts when its mean over imaged pixels sits 40 levels below BOTH sides of its
 * neighbourhood. Real terrain never lines up with a pixel column the whole way down, and a mosaic
 * seam is a step - dark on one side only - so neither counts. Only imaged pixels are averaged, so
 * the black of an unimaged pole is not mistaken for a line.
 */
async function darkLines(buf) {
  const { data, info } = await sharp(buf, { limitInputPixels: false }).toColourspace('b-w').raw().toBuffer({ resolveWithObject: true })
  const { width: W, height: H, channels: C } = info
  const at = (x, y) => data[(y * W + x) * C]
  const colS = new Float64Array(W), colN = new Uint32Array(W), rowS = new Float64Array(H), rowN = new Uint32Array(H)
  for (let y = 0; y < H; y++) {
    const yr = y >= 3 ? y - 3 : y + 3
    for (let x = 0; x < W; x++) {
      const v = at(x, y)
      if (at(x >= 3 ? x - 3 : x + 3, y) > 12) { colS[x] += v; colN[x]++ }
      if (at(x, yr) > 12) { rowS[y] += v; rowN[y]++ }
    }
  }
  const median = (a) => { const s = a.slice().sort((p, q) => p - q); return s.length ? s[s.length >> 1] : NaN }
  const count = (S, N, len, across) => {
    const m = new Float64Array(len).fill(NaN)
    for (let i = 0; i < len; i++) if (N[i] > across * 0.1) m[i] = S[i] / N[i]
    let lines = 0, prev = false
    for (let i = 4; i < len - 4; i++) {
      const side = (lo, hi) => { const v = []; for (let j = Math.max(0, lo); j <= Math.min(len - 1, hi); j++) if (!isNaN(m[j])) v.push(m[j]); return median(v) }
      const dip = !isNaN(m[i]) && m[i] < Math.min(side(i - 20, i - 5), side(i + 5, i + 20)) - 40
      if (dip && !prev) lines++
      prev = dip
    }
    return lines
  }
  return { cols: count(colS, colN, W, H), rows: count(rowS, rowN, H, W) }
}

async function check(name, buf, opts = {}) {
  const problems = []
  const p = await probe(buf)
  const ratio = p.meta.width / p.meta.height
  if (Math.abs(ratio / 2 - 1) > 0.01) problems.push(`aspect ${ratio.toFixed(3)} is not 2:1 equirectangular`)
  if (!opts.mask) {
    let white = 0
    for (let i = 0; i < p.data.length; i += 3) if (p.data[i] >= 254 && p.data[i + 1] >= 254 && p.data[i + 2] >= 254) white++
    const frac = white / (p.w * p.h)
    if (frac > (opts.whiteMax ?? 0.005)) problems.push(`${(frac * 100).toFixed(2)}% pure white — poster, graticule or burned-in text?`)
  }
  if (!opts.mask) {
    const b = await borderLines(sharp(buf, { limitInputPixels: false }))
    // Columns only: a dark top or bottom row is often real (low sun at the pole) and collapses to a point.
    if (b.left || b.right) problems.push(`dark border column(s) l${b.left} r${b.right} — seam at 0°`)
  }
  if (!opts.mask) {
    const g = await darkLines(buf)
    if (g.cols + g.rows >= 3) problems.push(`dark straight lines (${g.cols} columns, ${g.rows} rows) — a graticule drawn in black?`)
  }
  const seam = seamScore(p)
  // Voyager-era mosaics (Umbriel, Triton) carry real seams of ~6–9×; a broken wrap is far worse.
  if (seam > 10) problems.push(`does not wrap at 0° (seam ${seam.toFixed(1)}× a normal column step)`)
  return { problems, md5: createHash('md5').update(buf).digest('hex'), thumb: p.thumb }
}

/** Mean absolute difference of two 64×32 greyscale thumbnails; ~0 means the same picture. */
const thumbDiff = (a, b) => { let s = 0; for (let i = 0; i < a.length; i++) s += Math.abs(a[i] - b[i]); return s / a.length }

/**
 * A map may be used by more than one body only if every extra user sets a tint and a roll, so the
 * bodies do not render as twins. Reads the catalogue sources rather than importing TypeScript.
 */
async function checkSharedMaps() {
  const src = (await readFile(SATELLITES_TS, 'utf8')) + (await readFile(CATALOG_TS, 'utf8'))
  const users = new Map()
  for (const m of src.matchAll(/visual:\s*\{([^{}]*)\}/g)) {
    const tex = m[1].match(/(?<![\w.])texture:\s*["']([^"']+)["']/)?.[1]
    if (!tex) continue
    ;(users.get(tex) ?? users.set(tex, []).get(tex)).push(/\btint:/.test(m[1]) && /\broll:/.test(m[1]))
  }
  const problems = []
  for (const [tex, flags] of users) {
    const plain = flags.filter((f) => !f).length
    if (plain > 1) problems.push(`${tex} is used by ${plain} bodies without tint+roll; they would render as twins`)
  }
  return problems
}

// ---------------------------------------------------------------------------------------------------
// Clean-up passes

/**
 * Dark border rows/columns on each edge (some mosaics ship a 1–2 px black frame, which shows as a
 * seam at 0° and a ring at the poles). Measured at full resolution: a downsample dilutes a 1 px line
 * into its neighbours until it no longer looks dark.
 */
async function borderLines(img) {
  const { data, info } = await img.clone().toColourspace('b-w').raw().toBuffer({ resolveWithObject: true })
  const { width: W, height: H } = info
  const col = (x) => { let s = 0; for (let y = 0; y < H; y++) s += data[y * W + x]; return s / H }
  const row = (y) => { let s = 0; for (let x = 0; x < W; x++) s += data[y * W + x]; return s / W }
  const count = (line, first, dir) => {
    let n = 0
    while (n < 4 && line(first + dir * n) < 0.5 * line(first + dir * (n + 4))) n++
    return n
  }
  return { W, H, left: count(col, 0, 1), right: count(col, W - 1, -1), top: count(row, 0, 1), bottom: count(row, H - 1, -1) }
}

async function trimBorders(img) {
  const { W, H, left, right, top, bottom } = await borderLines(img)
  if (!(left || right || top || bottom)) return img
  console.log(`      trimmed borders l${left} r${right} t${top} b${bottom}`)
  return sharp(await img.extract({ left, top, width: W - left - right, height: H - top - bottom }).toBuffer(), { limitInputPixels: false })
}

/**
 * Replace no-data black near the poles with the nearest imaged pixel in the same column, then smear
 * each filled pixel sideways by more the further it is from real data. Rows that close to a pole all
 * converge on one point, so a cap that fades to the row average is close to what the surface looks
 * like; the column copy alone leaves a comb of vertical streaks.
 */
async function fillPolarNoData(img) {
  const { data, info } = await img.clone().removeAlpha().toColourspace('srgb').raw().toBuffer({ resolveWithObject: true })
  const { width: W, height: H } = info
  const blank = (x, y) => { const i = (y * W + x) * 3; return data[i] < 4 && data[i + 1] < 4 && data[i + 2] < 4 }
  const dist = new Uint16Array(W * H) // rows from the nearest imaged pixel; 0 = real data
  const band = Math.round(H * 0.2)
  let filled = 0
  const copy = (x, y, from) => {
    if (!blank(x, y)) return
    data.copyWithin((y * W + x) * 3, (from * W + x) * 3, (from * W + x) * 3 + 3)
    dist[y * W + x] = dist[from * W + x] + 1
    filled++
  }
  for (let x = 0; x < W; x++) {
    for (let y = band; y >= 0; y--) copy(x, y, y + 1)
    for (let y = H - 1 - band; y < H; y++) copy(x, y, y - 1)
  }
  const sums = new Float64Array((W + 1) * 3)
  for (let y = 0; y < H; y++) {
    const row = y * W
    let any = false
    for (let x = 0; x < W && !any; x++) any = dist[row + x] > 0
    if (!any) continue
    for (let x = 0; x < W; x++) for (let c = 0; c < 3; c++) sums[(x + 1) * 3 + c] = sums[x * 3 + c] + data[(row + x) * 3 + c]
    const sumTo = (x, c) => { // prefix sum over [0, x) on a wrapping row
      const wraps = Math.floor(x / W), r = x - wraps * W
      return wraps * sums[W * 3 + c] + sums[r * 3 + c]
    }
    const out = Buffer.from(data.subarray(row * 3, (row + W) * 3))
    for (let x = 0; x < W; x++) {
      const d = dist[row + x]
      if (!d) continue
      const r = Math.min(Math.floor(W / 2) - 1, d * 6)
      for (let c = 0; c < 3; c++) out[x * 3 + c] = (sumTo(x + r + 1 + W, c) - sumTo(x - r + W, c)) / (2 * r + 1)
    }
    out.copy(data, row * 3)
  }
  console.log(`      filled ${(100 * filled / (W * H)).toFixed(2)}% polar no-data`)
  return sharp(data, { raw: { width: W, height: H, channels: 3 }, limitInputPixels: false })
}

// ---------------------------------------------------------------------------------------------------

async function download(url, name) {
  await mkdir(RAW, { recursive: true })
  const cached = join(RAW, createHash('sha1').update(url).digest('hex').slice(0, 16) + (extname(new URL(url).pathname) || '.bin'))
  try { if ((await stat(cached)).size > 10_000) return readFile(cached) } catch {}
  let res
  for (let attempt = 0; attempt < 5; attempt++) {
    try {
      res = await fetch(url, { headers: { 'User-Agent': UA } })
      if (res.status !== 429) break
      const retryAfter = res.headers.get('retry-after')
      const waitMs = retryAfter ? parseInt(retryAfter, 10) * 1000 : 5000 * (attempt + 1)
      console.log(`      rate limited on ${name}, waiting ${Math.round(waitMs / 1000)}s`)
      await new Promise((r) => setTimeout(r, waitMs))
    } catch (err) {
      console.log(`      network error on ${name}: ${err.message}`)
      await new Promise((r) => setTimeout(r, 3000))
    }
  }
  if (!res?.ok) throw new Error(res ? `HTTP ${res.status}` : 'no response')
  const buf = Buffer.from(await res.arrayBuffer())
  if (buf.length < 10_000) throw new Error(`too small (${buf.length} bytes)`)
  await writeFile(cached, buf)
  return buf
}

/**
 * Paint out a graticule burned in as 1-pixel dark lines every `step` degrees, at full resolution and
 * before any downscale smears the lines into the terrain. Each line is found within 3 px of where
 * the grid puts it, as a column or row far darker than its neighbourhood over the imaged surface.
 * Its pixels are then interpolated across the line from the nearest clean pixels either side:
 * columns first, then rows, so a crossing is filled from column pixels already repaired. A line
 * pixel with no imaged pixel on either side is unimaged ground and stays black for fillGaps. The
 * lines are one pixel wide with untouched pixels beside them, so nothing real is lost.
 */
async function degrid(img, step) {
  const { data, info } = await img.clone().removeAlpha().toColourspace('srgb').raw().toBuffer({ resolveWithObject: true })
  const { width: W, height: H } = info
  const lum = (x, y) => { const i = (y * W + x) * 3; return 0.299 * data[i] + 0.587 * data[i + 1] + 0.114 * data[i + 2] }
  // Means over pixels whose neighbour three over is imaged, so neither a dark line (which is not
  // itself "imaged") nor an unimaged pole skews them.
  const colMean = new Float64Array(W), rowMean = new Float64Array(H)
  const colN = new Uint32Array(W), rowN = new Uint32Array(H)
  for (let y = 0; y < H; y++) {
    const yr = y >= 3 ? y - 3 : y + 3
    for (let x = 0; x < W; x++) {
      const v = lum(x, y)
      if (lum(x >= 3 ? x - 3 : x + 3, y) > 12) { colMean[x] += v; colN[x]++ }
      if (lum(x, yr) > 12) { rowMean[y] += v; rowN[y]++ }
    }
  }
  for (let x = 0; x < W; x++) colMean[x] = colN[x] ? colMean[x] / colN[x] : NaN
  for (let y = 0; y < H; y++) rowMean[y] = rowN[y] ? rowMean[y] / rowN[y] : NaN
  const find = (mean, len, count) => {
    const lines = new Uint8Array(len)
    const med = (lo, hi) => { const v = []; for (let j = Math.max(0, lo); j <= Math.min(len - 1, hi); j++) if (!isNaN(mean[j])) v.push(mean[j]); v.sort((a, b) => a - b); return v.length ? v[v.length >> 1] : NaN }
    for (let k = 0; k <= count; k++) {
      const c = Math.min(len - 1, Math.round((k * len) / count))
      const local = Math.min(med(c - 16, c - 5), med(c + 5, c + 16))
      for (let i = Math.max(0, c - 3); i <= Math.min(len - 1, c + 3); i++) if (mean[i] < 0.6 * local) lines[i] = 1
    }
    return lines
  }
  const lineCol = find(colMean, W, Math.round(360 / step))
  const lineRow = find(rowMean, H, Math.round(180 / step))
  const copyMix = (dst, a, b, t) => {
    const ok = (o) => 0.299 * data[o] + 0.587 * data[o + 1] + 0.114 * data[o + 2] > 12
    const oa = a >= 0 && ok(a), ob = b >= 0 && ok(b)
    if (!oa && !ob) return false
    for (let ch = 0; ch < 3; ch++) data[dst + ch] = oa && ob ? Math.round(data[a + ch] * (1 - t) + data[b + ch] * t) : data[(oa ? a : b) + ch]
    return true
  }
  let fixed = 0
  for (let x = 0; x < W; x++) {
    if (!lineCol[x]) continue
    let l = x, r = x, dl = 0, dr = 0
    do { l = (l - 1 + W) % W; dl++ } while (lineCol[l] && dl < W)
    do { r = (r + 1) % W; dr++ } while (lineCol[r] && dr < W)
    for (let y = 0; y < H; y++) {
      if (lineRow[y]) continue
      if (copyMix((y * W + x) * 3, (y * W + l) * 3, (y * W + r) * 3, dl / (dl + dr))) fixed++
    }
  }
  for (let y = 0; y < H; y++) {
    if (!lineRow[y]) continue
    let u = y, d = y
    while (u >= 0 && lineRow[u]) u--
    while (d < H && lineRow[d]) d++
    const t = u < 0 ? 1 : d >= H ? 0 : (y - u) / (d - u)
    for (let x = 0; x < W; x++) {
      if (copyMix((y * W + x) * 3, u >= 0 ? (u * W + x) * 3 : -1, d < H ? (d * W + x) * 3 : -1, t)) fixed++
    }
  }
  console.log(`      degridded ${lineCol.reduce((a, b) => a + b, 0)} columns and ${lineRow.reduce((a, b) => a + b, 0)} rows (${fixed} px)`)
  return sharp(data, { raw: { width: W, height: H, channels: 3 }, limitInputPixels: false })
}

async function build(name, raw, opts) {
  let img = sharp(raw, { limitInputPixels: false })
  if (!opts.mask) img = await trimBorders(img)
  if (opts.degrid) img = await degrid(img, opts.degrid)
  if (opts.fillPoles) img = await fillPolarNoData(img)
  const meta = await sharp(await img.clone().png({ compressionLevel: 0 }).toBuffer(), { limitInputPixels: false }).metadata()
  const ratio = meta.width / meta.height
  // Reject here rather than squash: a source that is not already ~2:1 is not an equirectangular map.
  if (Math.abs(ratio / 2 - 1) > 0.01) throw new Error(`source aspect ${ratio.toFixed(3)} is not 2:1`)
  // Largest even width within the cap that still leaves a 2:1 crop inside the source.
  const w = Math.min(meta.width, meta.height * 2, opts.maxW ?? MAX_W) & ~1
  const h = w / 2
  // Resizing to within a pixel of the source still pushes every pixel through the resample
  // kernel. Cropping those last rows or columns instead is free and measurably cleaner
  // (Io: 50.3 dB against the source rather than 49.2). Only resample to come down in size.
  if (meta.width - w > 2) img = img.resize(w, h, { fit: 'fill' })
  else if (meta.width !== w || meta.height !== h) img = img.extract({ left: 0, top: 0, width: w, height: h })
  img = img.removeAlpha().toColourspace('srgb')
  // tint() is a no-op on a one-channel greyscale source, so expand to RGB first.
  if (opts.colour) img = sharp(await img.raw().toBuffer(), { raw: { width: w, height: h, channels: 3 } }).tint(opts.colour)
  return name.endsWith('.png') ? img.png().toBuffer() : img.jpeg({ quality: opts.quality ?? 90, mozjpeg: true }).toBuffer()
}

// The only three bodies no spacecraft has resolved, so an artist's impression is all there is.
const FICTIONAL_OK = new Set(['eris.jpg', 'haumea.jpg', 'makemake.jpg'])

const args = process.argv.slice(2)
const checkOnly = args.includes('--check')
const forced = new Set(args.filter((a) => !a.startsWith('--')).map((a) => a.replace(/\.(jpg|png)$/, '')))

await mkdir(OUT, { recursive: true })
const accepted = [] // { name, md5, thumb }
const failed = []
const clash = (r) => accepted.find((a) => a.md5 === r.md5 || thumbDiff(a.thumb, r.thumb) < 2)

for (const [name, url, opts = {}] of FILES) {
  const dest = join(OUT, name)
  const force = forced.has(name.replace(/\.(jpg|png)$/, ''))
  let existing = null
  try { existing = await readFile(dest) } catch {}
  if (existing && !force) {
    const r = await check(name, existing, opts)
    const twin = clash(r)
    if (twin) r.problems.push(`same image as ${twin.name}`)
    if (!r.problems.length) { accepted.push({ name, ...r }); console.log(`ok    ${name}`); continue }
    console.log(`BAD   ${name}: ${r.problems.join('; ')}`)
  }
  if (checkOnly) { if (!existing) console.log(`MISS  ${name}`); failed.push(name); continue }

  let done = false
  for (const candidate of [url].flat(Infinity)) {
    try {
      // Solar System Scope's "fictional" maps are artists' impressions. They are the only thing
      // that exists for Eris, Haumea and Makemake, but reaching for one anywhere else means
      // swapping real spacecraft imagery for invented terrain — the worst outcome in the set,
      // and the easiest to do by accident when a real source fails a check.
      if (/_fictional/.test(candidate) && !FICTIONAL_OK.has(name)) {
        throw new Error('fictional map on a body that has been imaged for real')
      }
      const out = await build(name, await download(candidate, name), opts)
      const r = await check(name, out, opts)
      const twin = clash(r)
      if (twin) r.problems.push(`same image as ${twin.name}`)
      if (r.problems.length) { console.log(`      rejected ${candidate}: ${r.problems.join('; ')}`); continue }
      await writeFile(dest, out)
      accepted.push({ name, ...r })
      console.log(`fetch ${name} (${(out.length / 1e6).toFixed(1)} MB) from ${decodeURIComponent(candidate)}`)
      done = true
      break
    } catch (e) {
      console.log(`      ${candidate}: ${e.message}`)
    }
  }
  if (!done) { console.log(`FAIL  ${name}`); failed.push(name) }
}

// Smaller copies of every map, so a body a few pixels across never decodes its full-size original to
// show a dozen texels - on a 16K source that was half a second of worker time per body at startup.
// The loader reads full widths from the manifest and picks the smallest file at least as wide as it
// needs, fetching the original only past the widest copy. Rebuilt when missing or older than the map.
const LOD_WIDTHS = [512, 2048]
const LOD_DIR = join(OUT, 'lod')
async function writeLods(names) {
  if (!checkOnly) await mkdir(LOD_DIR, { recursive: true })
  const manifest = {}
  let made = 0
  const missing = []
  for (const name of names) {
    const src = join(OUT, name)
    const meta = await sharp(src, { limitInputPixels: false }).metadata()
    const srcTime = (await stat(src)).mtimeMs
    const lods = []
    for (const w of LOD_WIDTHS) {
      if (w >= meta.width) continue
      lods.push(w)
      const dest = join(LOD_DIR, `${name}@${w}.jpg`)
      let fresh = false
      try { fresh = (await stat(dest)).mtimeMs >= srcTime } catch {}
      if (fresh) continue
      if (checkOnly) { missing.push(`lod/${name}@${w}.jpg`); continue }
      await sharp(src, { limitInputPixels: false }).resize(w, w / 2, { fit: 'fill' }).removeAlpha()
        .jpeg({ quality: 90, mozjpeg: true }).toFile(dest)
      made++
    }
    manifest[name] = { width: meta.width, height: meta.height, lods }
  }
  if (checkOnly) {
    // Only a speed-up, not a correctness problem: the loader falls back to the full map.
    if (missing.length) console.log(`\nlow-res copies missing or stale (run npm run assets): ${missing.length}`)
    return
  }
  await writeFile(join(LOD_DIR, 'manifest.json'), JSON.stringify(manifest, null, 1))
  console.log(`\nlow-res copies: ${made} written, manifest covers ${Object.keys(manifest).length} maps`)
}
await writeLods(accepted.map((a) => a.name))

// Files the list no longer produces (renamed or dropped maps) are reported, not deleted.
const known = new Set(FILES.map(([n]) => n))
const stray = (await readdir(OUT)).filter((f) => /\.(jpg|png)$/.test(f) && !known.has(f))
if (stray.length) console.log(`\nnot produced by this script (safe to delete): ${stray.join(', ')}`)

const shared = await checkSharedMaps()
for (const p of shared) console.log(`BAD   ${p}`)

console.log(`\n${accepted.length}/${FILES.length} maps pass`)
if (failed.length || shared.length) process.exitCode = 1
