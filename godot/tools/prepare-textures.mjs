#!/usr/bin/env node
// Lay out godot/Spayly.Game/textures/ from public/textures/: every map the catalogue names, a manifest.json the C# views
// read, and a .import beside each map so a headless `--import` brings them in as streamed textures without the editor.
//
// The web build reworks some maps at load (src/render/mapDecode.ts): roll, then gap fill, then tint. Godot streams the
// file as it is on disk, so those variants are baked here, once, by running the web's own src/render/gapFill.ts. The
// catalogue and gapFill.ts are loaded through Vite's module runner rather than copied, so neither can drift from the web.
// A map nobody reworks is a symlink, as the spikes do: a 16K map is 20 MB of JPEG, and a second copy buys nothing.
// Windows without Developer Mode refuses symlinks (EPERM): fall back to a hardlink, then to a copy.
//
// Usage: node godot/tools/prepare-textures.mjs [--force] [--only <body id | source file | output file>]
import { copyFileSync, existsSync, linkSync, lstatSync, mkdirSync, readdirSync, readFileSync, readlinkSync, renameSync, statSync, symlinkSync, unlinkSync, writeFileSync } from 'node:fs'
import { basename, dirname, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'
import sharp from 'sharp'
import { runnerImport } from 'vite'

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..', '..')
const SRC = resolve(root, 'public/textures')
const OUT = resolve(root, 'godot/Spayly.Game/textures')
const STATE = resolve(OUT, '.prepare-state.json')
const TOOL = fileURLToPath(import.meta.url)
/** What a baked map depends on besides its source file: change any of them and every bake is stale. */
const BAKE_CODE = [TOOL, ...['gapFill.ts', 'noise.ts', 'mapDecode.ts'].map((f) => resolve(root, 'src/render', f))]
/** Not in the catalogue: src/render/sky.ts names it. */
const SKY = 'milky_way.jpg'

const args = process.argv.slice(2)
const force = args.includes('--force')
const onlyAt = args.indexOf('--only')
const only = onlyAt >= 0 ? args[onlyAt + 1] : undefined
if (onlyAt >= 0 && !only) fail('--only needs a body id, a source file or an output file')
for (const [i, a] of args.entries()) if (a !== '--force' && a !== '--only' && (onlyAt < 0 || i !== onlyAt + 1)) fail(`unknown argument ${a}`)

function fail(msg) {
  console.error(`prepare-textures: ${msg}`)
  process.exit(1)
}

if (!existsSync(SRC) || !readdirSync(SRC).some((f) => f.endsWith('.jpg'))) {
  fail('public/textures/ has no maps. Run `npm run assets` first (scripts/fetch-assets.mjs).')
}

sharp.cache(false)
const vite = { root, configFile: false, logLevel: 'error' }
let patchedGapFill = false
const { CATALOG } = (await runnerImport(resolve(root, 'src/sim/catalog.ts'), vite)).module
const { SATELLITES, MINOR_BODIES } = (await runnerImport(resolve(root, 'src/sim/satellites.ts'), vite)).module
const { fillNoData, applyTint } = (await runnerImport(resolve(root, 'src/render/gapFill.ts'), { ...vite, plugins: [fixCoarsestRow()] })).module

/**
 * gapFill.ts's push step clamps the row as min(h - 1.001, max(0, y)), which is -0.001 when the coarsest level is one
 * row high: row -1 is read, every weight goes NaN, and a Uint8ClampedArray stores NaN as 0, so the hole stays black.
 * The pyramid ends one row high when the fill grid's height halves down to 1 (Charon at 6000 px fills on 2000x1000,
 * Vesta at 3560 on 1780x890). Swapping the clamps changes nothing for any level two or more rows high, so every map
 * the bug does not reach keeps the web's exact pixels. src/ is frozen, hence a transform here rather than an edit there.
 */
function fixCoarsestRow() {
  const bug = 'const fy = Math.min(src.h - 1.001, Math.max(0, (y + 0.5) / 2 - 0.5))'
  return {
    name: 'gapfill-coarsest-row',
    enforce: 'pre',
    transform(code, id) {
      if (!id.endsWith('/src/render/gapFill.ts') || !code.includes(bug)) return null
      patchedGapFill = true
      return code.replace(bug, 'const fy = Math.max(0, Math.min(src.h - 1.001, (y + 0.5) / 2 - 0.5))')
    },
  }
}

// A visual field this script does not know is an error, not a silent drop: it may be a map the game then never gets.
const VISUAL_FIELDS = ['color', 'texture', 'night', 'clouds', 'normal', 'specular', 'ring', 'fillGaps', 'tint', 'roll', 'atmosphere', 'albedo', 'baselineTemp']
const MAP_FIELDS = ['night', 'specular', 'normal', 'clouds']
const bodies = [...Object.values(CATALOG), ...SATELLITES, ...MINOR_BODIES]
for (const b of bodies) {
  for (const k of Object.keys(b.visual)) if (!VISUAL_FIELDS.includes(k)) fail(`${b.id}: unknown visual.${k}; extend prepare-textures.mjs`)
  for (const k of Object.keys(b.visual.ring ?? {})) if (!['inner', 'outer', 'texture'].includes(k)) fail(`${b.id}: unknown visual.ring.${k}`)
}

// ---------- jobs ----------

/** Output file -> what produces it. */
const jobs = new Map()
function addJob(job) {
  const prev = jobs.get(job.out)
  if (prev && JSON.stringify(prev.params) !== JSON.stringify(job.params)) fail(`${job.out} is asked for twice with different processing`)
  if (!prev) jobs.set(job.out, job)
  else prev.users.push(...job.users)
  return job.out
}
const plain = (source, user, extra = {}) => addJob({ out: source, source, bake: false, params: null, users: [user], ...extra })

/** The albedo of a body, baked when the web reworks it at load. Named after the borrower, or after the rework when own. */
function albedo(b) {
  const v = b.visual
  const params = { roll: v.roll ?? 0, fillGaps: !!v.fillGaps, tint: v.tint ?? null }
  if (!params.roll && !params.fillGaps && !params.tint) return plain(v.texture, b.id)
  const stem = v.texture.replace(/\.jpg$/, '')
  const ops = [params.fillGaps && 'fill', params.tint && 'tint', params.roll && 'roll'].filter(Boolean).join('-')
  const out = `${stem}__${stem === b.id ? ops : b.id}.jpg`
  return addJob({ out, source: v.texture, bake: true, params, users: [b.id] })
}

const entries = []
for (const b of bodies) {
  const v = b.visual
  if (!v.texture) continue
  const e = { key: b.id === 'sun' ? '_sun' : b.id, name: b.name, albedo: albedo(b), maps: {}, v }
  for (const f of MAP_FIELDS) if (v[f]) e.maps[f] = plain(v[f], b.id, f === 'normal' ? { normalMap: true } : {})
  if (v.ring?.texture) e.maps.ring = plain(v.ring.texture, b.id)
  entries.push(e)
}
// The sky is drawn by a sky shader, which gives the streaming feedback no UV derivatives, so a streamed map would sit
// at its 2048 floor forever (spikes/tex-stream/RESULT.md). It is imported whole instead.
entries.push({ key: '_sky', name: 'Milky Way', albedo: plain(SKY, '_sky', { whole: true }), maps: {}, v: {} })

// ---------- .import ----------

const STREAMED = (normalMap) => ({
  importer: 'streamed_texture_2d', type: 'StreamedTexture2D',
  params: [
    'compress/high_quality=false', 'compress/hdr_compression=1', `compress/normal_map=${normalMap ? 1 : 0}`, 'compress/channel_pack=0',
    'roughness/mode=0', 'roughness/src_normal=""', 'streaming/min_lod_override=0', 'streaming/max_lod_override=0',
  ],
})
const WHOLE = {
  importer: 'texture', type: 'CompressedTexture2D',
  params: [
    'compress/mode=2', 'compress/high_quality=false', 'compress/high_quality_mode=0', 'compress/lossy_quality=0.7', 'compress/uastc_level=0',
    'compress/rdo_quality_loss=0.0', 'compress/hdr_compression=1', 'compress/normal_map=0', 'compress/channel_pack=0', 'mipmaps/generate=true',
    'mipmaps/limit=-1', 'mipmaps/preserve_alpha_test_coverage=false', 'mipmaps/alpha_test_threshold=0.5', 'roughness/mode=0', 'roughness/src_normal=""',
    'process/channel_remap/red=0', 'process/channel_remap/green=1', 'process/channel_remap/blue=2', 'process/channel_remap/alpha=3',
    'process/fix_alpha_border=true', 'process/premult_alpha=false', 'process/normal_map_invert_y=false', 'process/hdr_as_srgb=false',
    'process/hdr_clamp_exposure=false', 'process/size_limit=0', 'detect_3d/compress_to=0',
  ],
}

/**
 * Write <file>.import unless one already asks for the same importer and params. Godot fills in uid, path and
 * dest_files on its first import and keeps the params as written; rewriting over that would only force a reimport,
 * so an up-to-date file is left alone, and a rewritten one keeps the uid Godot gave it.
 */
function writeImport(job) {
  const spec = job.whole ? WHOLE : STREAMED(job.normalMap)
  const path = resolve(OUT, `${job.out}.import`)
  const old = existsSync(path) ? readFileSync(path, 'utf8') : ''
  if (old.includes(`importer="${spec.importer}"`) && spec.params.every((p) => old.split('\n').includes(p))) return false
  const uid = old.match(/^uid="[^"]*"$/m)?.[0]
  const text = [
    '[remap]', '', `importer="${spec.importer}"`, `type="${spec.type}"`, ...(uid ? [uid] : []), '',
    '[deps]', '', `source_file="res://textures/${job.out}"`, '',
    '[params]', '', ...spec.params, '',
  ].join('\n')
  writeFileSync(path, text)
  return true
}

// ---------- pixels ----------

const isBlank = (d, o) => d[o] <= 10 && d[o + 1] <= 10 && d[o + 2] <= 10
function blankFraction(d) {
  let n = 0
  for (let o = 0; o < d.length; o += 4) if (isBlank(d, o)) n++
  return n / (d.length / 4)
}

/** Shift every row right by `off` pixels, wrapping: what mapDecode's two drawImage calls do. */
function rollRows(d, w, h, off) {
  const row = new Uint8ClampedArray(w * 4)
  for (let y = 0; y < h; y++) {
    const r = d.subarray(y * w * 4, (y + 1) * w * 4)
    row.set(r)
    r.set(row.subarray((w - off) * 4), 0)
    r.set(row.subarray(0, (w - off) * 4), off * 4)
  }
}

/**
 * Roll, fill, tint, in mapDecode's order. The web fills rows bottom first (the canvas draws flipped for WebGL), and the
 * fill's noise is not symmetric in latitude, so the rows are flipped round it to give the web's pixels, not a mirror.
 */
async function bake(job) {
  const { roll, fillGaps, tint } = job.params
  const { data, info } = await sharp(resolve(SRC, job.source), { limitInputPixels: false }).flip().ensureAlpha().raw().toBuffer({ resolveWithObject: true })
  const { width: w, height: h } = info
  if (info.channels !== 4) throw new Error(`${job.source}: expected RGBA from sharp, got ${info.channels} channels`)
  const d = new Uint8ClampedArray(data.buffer, data.byteOffset, data.length)
  const off = roll ? Math.round((((roll % 1) + 1) % 1) * w) : 0
  if (off) rollRows(d, w, h, off)
  if (fillGaps) {
    job.blankBefore = blankFraction(d)
    fillNoData(d, w, h)
    job.blankAfter = blankFraction(d)
  }
  if (tint) applyTint(d, tint)
  const tmp = resolve(OUT, `.${job.out}.tmp`)
  await sharp(data, { raw: { width: w, height: h, channels: 4 }, limitInputPixels: false }).flip().removeAlpha().jpeg({ quality: 92 }).toFile(tmp)
  renameSync(tmp, resolve(OUT, job.out))
}

// ---------- run ----------

/** Symlink `src` to `out`; on EPERM (Windows without Developer Mode) hardlink, and on EXDEV or another refusal copy. */
function linkMap(src, out) {
  try { symlinkSync(src, out); return } catch (e) { if (e.code !== 'EPERM') throw e }
  try { linkSync(src, out); return } catch { /* different volume or a filesystem without hardlinks */ }
  copyFileSync(src, out)
}

/** True when `out` already is `src` by any route linkMap takes: the symlink, the same inode, or a copy at least as new and as large. */
function isLinked(out, current, src) {
  if (!current) return false
  if (current.isSymbolicLink()) return readlinkSync(out) === src
  const s = statSync(src)
  if (current.ino && current.ino === s.ino && current.dev === s.dev) return true
  return current.isFile() && current.size === s.size && current.mtimeMs >= s.mtimeMs
}

function memAvailableMb() {
  try {
    const m = readFileSync('/proc/meminfo', 'utf8').match(/^MemAvailable:\s+(\d+)/m)
    return m ? Math.round(Number(m[1]) / 1024) : NaN
  } catch { return NaN } // no /proc on macOS or Windows
}
const memStart = memAvailableMb()
let memMin = memStart
const sampler = setInterval(() => { memMin = Math.min(memMin, memAvailableMb()) }, 200)

mkdirSync(OUT, { recursive: true })
const state = existsSync(STATE) ? JSON.parse(readFileSync(STATE, 'utf8')) : {}
const mtime = (p) => statSync(p).mtimeMs
const codeTime = Math.max(...BAKE_CODE.map(mtime))
const wanted = (job) => !only || job.out === only || job.source === only || job.users.includes(only)
const rows = []
const missing = []
let imports = 0
const t0 = Date.now()

for (const job of jobs.values()) {
  const src = resolve(SRC, job.source)
  const out = resolve(OUT, job.out)
  if (!existsSync(src)) { missing.push(job.source); continue }
  const meta = await sharp(src, { limitInputPixels: false }).metadata()
  job.width = meta.width
  job.height = meta.height
  if (meta.width !== 2 * meta.height) console.warn(`warning: ${job.source} is ${meta.width}x${meta.height}, not 2:1`)
  if (!wanted(job)) continue
  const t = Date.now()
  let status
  if (!job.bake) {
    const current = lstatSync(out, { throwIfNoEntry: false })
    const linked = isLinked(out, current, src)
    if (linked && !force) status = 'skipped'
    else {
      if (current) unlinkSync(out)
      linkMap(src, out)
      status = 'linked'
    }
  } else {
    const fresh = existsSync(out) && mtime(out) > Math.max(mtime(src), codeTime) && JSON.stringify(state[job.out]) === JSON.stringify(job.params)
    if (fresh && !force) status = 'skipped'
    else {
      await bake(job)
      state[job.out] = job.params
      writeFileSync(STATE, JSON.stringify(state, null, 2) + '\n')
      status = 'baked'
    }
  }
  memMin = Math.min(memMin, memAvailableMb())
  if (writeImport(job)) imports++
  const p = job.params
  const variant = p ? [p.roll && `roll ${p.roll}`, p.fillGaps && 'fill', p.tint && `tint ${p.tint}`].filter(Boolean).join(', ') : '-'
  const blank = job.blankBefore !== undefined ? `${(job.blankBefore * 100).toFixed(2)}% -> ${(job.blankAfter * 100).toFixed(4)}%` : ''
  rows.push([job.out, job.source, variant, `${job.width}x${job.height}`, ((Date.now() - t) / 1000).toFixed(2), status, blank])
}
clearInterval(sampler)

// Anything in the folder that no job produces is left over from a variant the catalogue no longer has.
const stale = []
if (!only) {
  for (const f of readdirSync(OUT)) {
    const name = f.replace(/\.import$/, '')
    if (!name.endsWith('.jpg') || jobs.has(name)) continue
    unlinkSync(resolve(OUT, f))
    stale.push(f)
  }
  const kept = Object.fromEntries(Object.entries(state).filter(([f]) => jobs.has(f)))
  if (Object.keys(kept).length !== Object.keys(state).length) writeFileSync(STATE, JSON.stringify(kept, null, 2) + '\n')
}

// ---------- manifest ----------

/** Credit rows from CREDITS.md tables, keyed by each name in the first column ("Earth day" files under Earth). */
function credits() {
  const text = readFileSync(resolve(SRC, 'CREDITS.md'), 'utf8')
  const out = []
  for (const line of text.split('\n')) {
    const cells = line.split('|').slice(1, -1).map((c) => c.trim().replace(/`/g, ''))
    if (cells.length !== 3 || /^-+$/.test(cells[0]) || cells[0] === 'Map' || cells[0] === 'Body') continue
    const names = cells[0].replace(/\([^)]*\)/g, '').split(',').map((s) => s.trim())
    out.push({ names, text: `${cells[0]}: ${cells[1]} [${cells[2]}]` })
  }
  return (name) => out.filter((r) => r.names.some((n) => n === name || n.startsWith(`${name} `))).map((r) => r.text).join('; ')
}
const creditFor = credits()
const present = (file) => file && existsSync(resolve(OUT, file))

const manifest = {}
for (const e of entries) {
  const job = jobs.get(e.albedo)
  if (!present(e.albedo)) continue
  const credit = creditFor(e.name)
  if (!credit) console.warn(`warning: no CREDITS.md row names ${e.name}`)
  const m = { albedo: e.albedo, source: job.source }
  for (const [f, file] of Object.entries(e.maps)) if (present(file)) m[f] = file
  m.fillGaps = !!e.v.fillGaps
  if (e.v.tint) m.tint = e.v.tint
  if (e.v.roll) m.roll = e.v.roll
  Object.assign(m, { width: job.width, height: job.height, credit })
  manifest[e.key] = m
}
const manifestPath = resolve(OUT, 'manifest.json')
const manifestText = JSON.stringify(manifest, null, 2) + '\n'
const manifestChanged = !existsSync(manifestPath) || readFileSync(manifestPath, 'utf8') !== manifestText
if (manifestChanged) writeFileSync(manifestPath, manifestText)

// ---------- report ----------

const head = ['output', 'source', 'variant', 'size', 's', 'status', 'blank before -> after']
const widths = head.map((h, i) => Math.max(h.length, ...rows.map((r) => r[i].length)))
const fmt = (r) => r.map((c, i) => c.padEnd(widths[i])).join('  ').trimEnd()
console.log(fmt(head))
console.log(widths.map((w) => '-'.repeat(w)).join('  '))
for (const r of rows) console.log(fmt(r))
const count = (s) => rows.filter((r) => r[5] === s).length
console.log(`\n${count('baked')} baked, ${count('linked')} linked, ${count('skipped')} skipped; ${imports} .import written; manifest ${manifestChanged ? 'written' : 'unchanged'} (${Object.keys(manifest).length} entries)`)
if (stale.length) console.log(`removed stale: ${stale.join(', ')}`)
if (missing.length) console.log(`missing from public/textures (the game falls back to procedural): ${missing.join(', ')}`)
console.log(`wall ${((Date.now() - t0) / 1000).toFixed(1)} s; MemAvailable ${memStart} MB at start, lowest ${memMin} MB (drop ${memStart - memMin} MB)`)
if (patchedGapFill) console.log('gapFill.ts: coarsest-row clamp patched at load (see fixCoarsestRow); the web still has the bug')
console.log(`output: ${basename(dirname(OUT))}/${basename(OUT)}/`)
