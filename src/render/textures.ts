import * as THREE from 'three'
import { Vec3 } from '../sim/vec'
import { bodyDirToUv } from './orientation'
import { BITMAP_FOR_GPU, DecodeJob, decodePixels, Pixels } from './mapDecode'

let maxAniso = 1
/** What this GPU will actually accept. Asking for more than this fails the upload outright. */
let gpuMaxTexture = 8192
let renderer: THREE.WebGLRenderer | null = null

export function configureTextures(r: THREE.WebGLRenderer): void {
  renderer = r
  maxAniso = r.capabilities.getMaxAnisotropy()
  gpuMaxTexture = r.capabilities.maxTextureSize
  if (maxTextureWidth > gpuMaxTexture) maxTextureWidth = gpuMaxTexture
  // A restored context has lost every upload, and the bitmaps behind them were released once they
  // were on the GPU. Drop the lot; views re-request as they render and rebuild from the cached files.
  r.domElement.addEventListener('webglcontextrestored', () => {
    for (const [key, entry] of cache) {
      cache.delete(key)
      void entry.texture.then((t) => t?.dispose())
    }
  })
}

/** Largest map this GPU can hold, so the UI can offer 16K only where 16K exists. */
export const maxTextureSupported = (): number => gpuMaxTexture

export interface LoadOpts {
  /** Treat as colour data (sRGB). Default true. */
  srgb?: boolean
  /** Downscale wider images to this width. */
  maxSize?: number
  /**
   * Grow the imaged parts of the map over the blank ones. Several moons were only photographed on
   * one side, so their published maps have large black no-data regions that would otherwise render
   * as a hole in the body.
   */
  fillGaps?: boolean
  /** Multiply every texel by this colour, as '#rrggbb'. */
  tint?: string
  /** Shift the map in longitude by this fraction of a turn. */
  roll?: number
  /**
   * Clamp rather than repeat horizontally. Set it here, not on the result: the texture is already on
   * the GPU when it resolves, so changing its wrap afterwards does nothing.
   */
  clamp?: boolean
  /**
   * Keep this entry out of the sweep. For maps whose owner does not re-request them as it renders —
   * the sky panorama has no camera-dependent size and nothing to re-ask on its behalf.
   */
  pin?: boolean
}

/**
 * Widest texture we will hand the GPU. A body map is RGBA plus mipmaps, so 8192x4096 costs about
 * 180 MB of video memory each and a full set of them will lose the WebGL context on most cards;
 * 4096 keeps a set under about a gigabyte while still being sharper than the 2k maps. The View
 * panel moves this, so read it through textureQuality() rather than capturing it.
 */
let maxTextureWidth = 8192

export const textureQuality = (): number => maxTextureWidth

/** Cached texture plus when it was last asked for, so the sweep can drop what nothing wants. */
interface Entry {
  /** Resolves once the texture is on the GPU, so binding it never stalls a frame. */
  texture: Promise<THREE.Texture | null>
  /** Set every time a caller asks for this entry. Views re-ask as they render, so anything on
   *  screen stays fresh and anything that has scrolled away goes stale. */
  lastUsed: number
  /** Never swept: nothing re-requests this one, so staleness says nothing about whether it is in use. */
  pinned: boolean
  /** Dropped from the decode queue because nothing asked for it again while it waited. */
  abandoned?: boolean
  /** The file it was decoded from, so the sweep knows which originals are still wanted. */
  file: string
}

/** A fetched map file and its pixel size, read from the header so nothing has to decode it to ask. */
interface Source {
  blob: Blob
  width: number
  height: number
}

/**
 * Keyed by the options that change the pixels AND by the width actually produced, not by the cap
 * asked for. A map narrower than the lowest setting yields the same texture at every setting and is
 * never rebuilt. The width is only known once the file's header has been read, hence the two-step
 * lookup.
 */
const cache = new Map<string, Entry>()
/** Which entry a texture came from, so a view can keep what it has bound fresh. */
const owners = new WeakMap<THREE.Texture, Entry>()
/**
 * The encoded files, kept so a new size is a decode away rather than a download away. Deliberately
 * not the decoded bitmaps: a 16K map is 512 MB decoded and 20 MB encoded.
 */
const sources = new Map<string, Promise<Source | null>>()

/** Full size of each map and which low-res copies of it exist, written by `npm run assets`. */
interface LodManifest {
  [name: string]: { width: number; height: number; lods: number[] }
}
let lodManifest: Promise<LodManifest | null> | undefined

function loadManifest(): Promise<LodManifest | null> {
  lodManifest ??= fetch(`${import.meta.env.BASE_URL}textures/lod/manifest.json`)
    .then((r) => (r.ok ? (r.json() as Promise<LodManifest>) : null))
    .catch(() => null)
  return lodManifest
}

/**
 * The file to decode for a map at up to `cap` wide: the smallest low-res copy at least as wide as
 * the texture will be, or the original. A body a few pixels across used to decode its full 16K
 * original to show a few hundred texels; now it reads a 512 px copy, and the original is fetched
 * only when something needs more than the widest copy. Without a manifest, or with a copy missing
 * on disk, it falls back to the original.
 */
async function resolveSource(name: string, cap: number): Promise<{ src: Source; width: number; file: string } | null> {
  const m = (await loadManifest())?.[name]
  if (m) {
    const width = Math.min(cap, m.width)
    let lod = Infinity
    for (const w of m.lods) if (w >= width && w < lod) lod = w
    if (lod !== Infinity) {
      const file = `lod/${name}@${lod}.jpg`
      const src = await loadSource(file)
      if (src) return { src, width: Math.min(width, src.width), file }
    }
  }
  const src = await loadSource(name)
  return src ? { src, width: Math.min(cap, src.width), file: name } : null
}

function loadSource(name: string): Promise<Source | null> {
  let p = sources.get(name)
  if (!p) {
    p = fetch(`${import.meta.env.BASE_URL}textures/${name}`)
      .then((r) => (r.ok ? r.blob() : null))
      .then(async (blob) => {
        if (!blob || blob.type.startsWith('text/')) return null
        const size = await imageSize(blob)
        return size ? { blob, ...size } : null
      })
      .catch(() => null)
    sources.set(name, p)
  }
  return p
}

/** Pixel size from a PNG or JPEG header; anything else, or a header too odd to walk, gets decoded. */
async function imageSize(blob: Blob): Promise<{ width: number; height: number } | null> {
  const v = new DataView(await blob.slice(0, 1 << 18).arrayBuffer())
  if (v.byteLength >= 24 && v.getUint32(0) === 0x89504e47) return { width: v.getUint32(16), height: v.getUint32(20) }
  if (v.byteLength >= 4 && v.getUint16(0) === 0xffd8) {
    let o = 2
    while (o + 9 < v.byteLength && v.getUint8(o) === 0xff) {
      const m = v.getUint8(o + 1)
      if (m === 0xff) { o++; continue }
      // Start-of-frame markers, less the three in that range that mean something else.
      if (m >= 0xc0 && m <= 0xcf && m !== 0xc4 && m !== 0xc8 && m !== 0xcc) return { width: v.getUint16(o + 7), height: v.getUint16(o + 5) }
      o += 2 + v.getUint16(o + 2)
    }
  }
  try {
    const b = await createImageBitmap(blob)
    const size = { width: b.width, height: b.height }
    b.close()
    return size
  } catch {
    return null
  }
}

function finish(tex: THREE.Texture, srgb: boolean): THREE.Texture {
  tex.colorSpace = srgb ? THREE.SRGBColorSpace : THREE.NoColorSpace
  tex.anisotropy = maxAniso
  tex.wrapS = THREE.RepeatWrapping
  tex.wrapT = THREE.ClampToEdgeWrapping
  // Mipmaps on every size, large ones included: streaming only hands a body a big map while it
  // fills the screen, and without mipmaps that same map shimmers badly the moment it does not.
  tex.minFilter = THREE.LinearMipmapLinearFilter
  tex.magFilter = THREE.LinearFilter
  tex.generateMipmaps = true
  tex.needsUpdate = true
  return tex
}

/**
 * Load a texture from public/textures, resolving to null when missing so callers can fall back.
 * Resolves only once the texture is on the GPU.
 */
export function loadTexture(name: string, opts: LoadOpts = {}): Promise<THREE.Texture | null> {
  const variant = `${name}|${opts.srgb ?? true}|${opts.fillGaps ? 1 : 0}|${opts.tint ?? ''}|${opts.roll ?? 0}|${opts.clamp ? 1 : 0}`
  const cap = Math.min(opts.maxSize ?? maxTextureWidth, gpuMaxTexture)
  return resolveSource(name, cap).then((found) => {
    if (!found) return null
    const { src, width, file } = found
    const key = `${variant}|${width}`
    let entry = cache.get(key)
    if (!entry) {
      const e: Entry = { texture: Promise.resolve(null), lastUsed: performance.now(), pinned: !!opts.pin, file }
      e.texture = buildTexture(name, src, width, opts, e).then((t) => {
        if (t) owners.set(t, e)
        // Abandoned before it was built: forget it, so asking again starts afresh.
        else if (cache.get(key) === e && e.abandoned) cache.delete(key)
        return t
      })
      cache.set(key, e)
      entry = e
    }
    entry.lastUsed = performance.now()
    return entry.texture
  })
}

/**
 * Tell the sweep a texture is still bound. Re-asking by size only refreshes the size a view wants
 * next; while a bigger map is still on its way, the one on screen is a different entry, and without
 * this a slow load could see it freed out from under the material.
 */
export function touchTexture(t: THREE.Texture | null | undefined): void {
  const e = t ? owners.get(t) : undefined
  if (e) e.lastUsed = performance.now()
}

/**
 * Release textures nothing has asked for lately.
 *
 * three.js frees a texture's video memory in dispose() and has no finaliser, so a forgotten texture
 * stays on the GPU for the life of the page. Views re-request whatever they are showing as they
 * render, which keeps it fresh; a map that was only needed for a close pass goes stale and is
 * released here once the camera has moved on.
 */
export function sweepTextures(maxAgeMs = STALE_AFTER_MS): number {
  const cutoff = performance.now() - maxAgeMs
  let released = 0
  for (const [key, entry] of cache) {
    if (entry.pinned || entry.lastUsed > cutoff) continue
    cache.delete(key)
    released++
    void entry.texture.then((t) => t?.dispose())
  }
  // Full-size originals are tens of megabytes of JPEG each; keep one only while a live texture
  // came from it. The low-res copies are small and asked for constantly, so they stay.
  const wanted = new Set<string>()
  for (const e of cache.values()) wanted.add(e.file)
  for (const file of sources.keys()) if (!file.startsWith('lod/') && !wanted.has(file)) sources.delete(file)
  return released
}

/** How long a texture nothing has asked for stays on the GPU. */
const STALE_AFTER_MS = 8000

/**
 * Bind `next` where `old` was, and let the sweep take `old` on its next pass instead of eight
 * seconds later. A zoom builds each octave in turn, and every width it passed through sat in video
 * memory until it went stale: 1024 up to 8192 of one map is about 226 MB at once. Safe when
 * another view still shows `old`, because that view keeps re-requesting it, which refreshes it.
 */
export function swapTexture(old: THREE.Texture | null | undefined, next: THREE.Texture): THREE.Texture {
  if (old && old !== next) {
    const e = owners.get(old)
    if (e && !e.pinned) e.lastUsed = Math.min(e.lastUsed, performance.now() - STALE_AFTER_MS + 1000)
  }
  return next
}

/** Widest a map may be decoded at. Streaming picks the size a body actually needs below this. */
export function setTextureCeiling(width: number): void {
  maxTextureWidth = Math.min(width, gpuMaxTexture)
}

/**
 * Decode a map at the width asked for and put it on the GPU. The decode, downscale and any rework
 * happen in the worker, which hands back raw bytes; the upload then goes in strips over several
 * frames. An <img> would instead be decoded synchronously inside one upload, which for a 16K JPEG is
 * seconds of frozen frame, and even a decoded bitmap takes the best part of half a second to upload
 * in one piece.
 */
async function buildTexture(name: string, src: Source, width: number, opts: LoadOpts, entry: Entry): Promise<THREE.Texture | null> {
  const height = Math.max(1, Math.round((src.height * width) / src.width))
  const job: DecodeJob = { blob: src.blob, width, height, resize: width < src.width, roll: opts.roll ?? 0, fillGaps: !!opts.fillGaps, tint: opts.tint }
  const setUp = (tex: THREE.Texture) => {
    finish(tex, opts.srgb ?? true)
    tex.flipY = false
    if (opts.clamp) tex.wrapS = THREE.ClampToEdgeWrapping
    return tex
  }
  try {
    const px = await decodeOffThread(job, entry)
    if (!px) return null
    const tex = setUp(new THREE.DataTexture(px.data, px.width, px.height, THREE.RGBAFormat, THREE.UnsignedByteType))
    return await upload(tex, 'strips')
  } catch (err) {
    // A canvas this large may be refused (Firefox caps canvas area below 16K x 8K). A plain map can
    // still go up as a bitmap in one piece; a reworked one has no other way to get its pixels.
    if (job.fillGaps || job.tint || job.roll) {
      console.warn(`texture ${name} at ${width}px failed:`, err)
      return null
    }
    try {
      const bitmap = await createImageBitmap(src.blob, job.resize ? { ...BITMAP_FOR_GPU, resizeWidth: width, resizeHeight: height, resizeQuality: 'high' } : BITMAP_FOR_GPU)
      return await upload(setUp(new THREE.Texture(bitmap)), 'whole')
    } catch (err2) {
      console.warn(`texture ${name} at ${width}px failed:`, err2)
      return null
    }
  }
}

// ---------- decode workers ----------

/**
 * Decoding is bound by the JPEG decode itself — half a second for a 16K source, whatever size comes
 * out — and startup asks for some forty maps, so one worker would take the best part of twenty
 * seconds to texture everything. More workers than three mostly multiply peak memory, which for a
 * 16K decode is over a gigabyte each.
 */
const WORKERS = 3
/**
 * A queued decode nothing has re-asked for in this long is dropped. Views re-ask twice a second for
 * the size they want now, so this only catches sizes a zoom has already passed through — each one a
 * full decode of the source, which for a 16K map is most of a second of a worker's time.
 */
const ABANDON_MS = 1500

interface Queued {
  job: DecodeJob
  entry: Entry
  resolve: (p: Pixels | null) => void
  reject: (e: Error) => void
}
interface DecodeWorker {
  worker: Worker
  current: Queued | null
}
const workers: DecodeWorker[] = []
let workersFailed = false
const queue: Queued[] = []

function startWorkers(): void {
  for (let i = 0; i < WORKERS; i++) {
    const slot: DecodeWorker = { worker: new Worker(new URL('./textureWorker.ts', import.meta.url), { type: 'module' }), current: null }
    slot.worker.onmessage = (e: MessageEvent<{ pixels?: Pixels; error?: string }>) => {
      const q = slot.current
      slot.current = null
      if (q) {
        if (e.data.pixels) q.resolve(e.data.pixels)
        else q.reject(new Error(e.data.error ?? 'decode failed'))
      }
      dispatch()
    }
    // A worker that cannot even load takes the rest down with it; finish their jobs here instead.
    slot.worker.onerror = () => {
      workersFailed = true
      const orphans = workers.flatMap((w) => (w.current ? [w.current] : [])).concat(queue.splice(0))
      for (const w of workers) w.worker.terminate()
      workers.length = 0
      for (const q of orphans) decodePixels(q.job).then(q.resolve, q.reject)
    }
    workers.push(slot)
  }
}

/**
 * Hand each idle worker the widest job still wanted, oldest first among equals, after dropping any
 * nothing has asked for lately. Width stands in for importance: streaming only asks for a wide map
 * when the body is big on screen, so whatever fills the view — at startup, Earth — goes first, and a
 * fast zoom's final size goes ahead of the sizes it passed through. Among equals, request order puts
 * a planet's surface ahead of its night lights and clouds.
 */
function dispatch(): void {
  const cutoff = performance.now() - ABANDON_MS
  for (let i = queue.length - 1; i >= 0; i--) {
    const q = queue[i]
    if (q.entry.pinned || q.entry.lastUsed > cutoff) continue
    queue.splice(i, 1)
    q.entry.abandoned = true
    q.resolve(null)
  }
  for (const w of workers) {
    if (w.current || !queue.length) continue
    let best = 0
    for (let i = 1; i < queue.length; i++) if (queue[i].job.width > queue[best].job.width) best = i
    const q = queue.splice(best, 1)[0]
    w.current = q
    w.worker.postMessage({ job: q.job })
  }
}

/** Decode in a worker; null if the job was abandoned before a worker got to it. */
function decodeOffThread(job: DecodeJob, entry: Entry): Promise<Pixels | null> {
  if (!workers.length && !workersFailed) {
    try {
      startWorkers()
    } catch {
      workersFailed = true
    }
  }
  if (!workers.length) return decodePixels(job)
  return new Promise((resolve, reject) => {
    queue.push({ job, entry, resolve, reject })
    dispatch()
  })
}

// ---------- paced upload ----------

/**
 * Texels pushed to the GPU per frame. 4M texels is 16 MB, around 10 ms of upload with a real map
 * behind it: a 16K map goes up over half a second of frames instead of stalling one, and a 1K map in
 * a fraction of one.
 */
const UPLOAD_TEXELS_PER_FRAME = 4 * 1024 * 1024

interface Upload {
  tex: THREE.Texture
  /** 'strips' for a DataTexture whose bytes go up a band of rows at a time; 'whole' for a bitmap. */
  mode: 'strips' | 'whole'
  /** Rows uploaded so far. */
  row: number
  done: (t: THREE.Texture) => void
}
const uploads: Upload[] = []

function upload(tex: THREE.Texture, mode: Upload['mode']): Promise<THREE.Texture> {
  if (!renderer) return Promise.resolve(tex)
  // three allocates the storage and sets the sampler up, but leaves the bytes to the pump.
  if (mode === 'strips') tex.source.dataReady = false
  return new Promise((done) => uploads.push({ tex, mode, row: 0, done }))
}

/** Put queued maps on the GPU within a per-frame budget, oldest first. Call once a frame, before rendering. */
export function pumpTextureUploads(): void {
  const r = renderer
  if (!r) return
  const gl = r.getContext() as WebGL2RenderingContext
  let budget = UPLOAD_TEXELS_PER_FRAME
  while (uploads.length && budget > 0) {
    const job = uploads[0]
    if (job.mode === 'whole') {
      const bitmap = job.tex.image as ImageBitmap
      r.initTexture(job.tex)
      budget -= bitmap.width * bitmap.height
      // The GPU holds its own copy now. A decoded 16K map is 512 MB of CPU memory otherwise.
      bitmap.close()
      uploads.shift()
      job.done(job.tex)
      continue
    }
    const img = job.tex.image as { data: Uint8ClampedArray | null; width: number; height: number }
    if (job.row === 0) r.initTexture(job.tex)
    const rows = Math.min(img.height - job.row, Math.max(1, Math.floor(budget / img.width)))
    r.state.bindTexture(gl.TEXTURE_2D, (r.properties.get(job.tex) as { __webglTexture: WebGLTexture }).__webglTexture)
    r.state.pixelStorei(gl.UNPACK_FLIP_Y_WEBGL, false)
    r.state.pixelStorei(gl.UNPACK_PREMULTIPLY_ALPHA_WEBGL, false)
    r.state.pixelStorei(gl.UNPACK_ALIGNMENT, 4)
    gl.texSubImage2D(gl.TEXTURE_2D, 0, 0, job.row, img.width, rows, gl.RGBA, gl.UNSIGNED_BYTE, img.data!, job.row * img.width * 4)
    job.row += rows
    budget -= rows * img.width
    if (job.row >= img.height) {
      gl.generateMipmap(gl.TEXTURE_2D)
      // three will not read these again (it thinks it uploaded nothing), and they are 512 MB at 16K.
      img.data = null
      uploads.shift()
      job.done(job.tex)
    }
  }
}

/** Land/water lookup from a specular map (white = water). */
export class SurfaceMask {
  private data: ImageData | null = null
  private w = 0
  private h = 0

  static async load(name: string): Promise<SurfaceMask> {
    const m = new SurfaceMask()
    const src = await loadSource(name)
    if (src) {
      const w = Math.min(src.width, 1024)
      const h = Math.max(1, Math.round((src.height * w) / src.width))
      try {
        const bmp = await createImageBitmap(src.blob, { resizeWidth: w, resizeHeight: h, resizeQuality: 'high', premultiplyAlpha: 'none', colorSpaceConversion: 'none' })
        const ctx = new OffscreenCanvas(w, h).getContext('2d', { willReadFrequently: true })
        if (ctx) {
          ctx.drawImage(bmp, 0, 0)
          m.data = ctx.getImageData(0, 0, w, h)
          m.w = w
          m.h = h
        }
        bmp.close()
      } catch {
        // No mask: everything reads as land, which is what a missing specular map meant before.
      }
    }
    return m
  }

  isWater(dir: Vec3): boolean {
    if (!this.data) return false
    const { u, v } = bodyDirToUv(dir)
    const x = Math.min(this.w - 1, Math.floor(u * this.w))
    const y = Math.min(this.h - 1, Math.floor((1 - v) * this.h))
    return this.data.data[(y * this.w + x) * 4] > 128
  }
}

// ---------- procedural fallbacks ----------

function hash(ix: number, iy: number, seed: number): number {
  let h = (Math.imul(ix, 374761393) + Math.imul(iy, 668265263) + Math.imul(seed, 1442695041)) | 0
  h = Math.imul(h ^ (h >>> 13), 1274126177)
  return ((h ^ (h >>> 16)) >>> 0) / 4294967296
}

function vnoise(x: number, y: number, seed: number): number {
  const ix = Math.floor(x), iy = Math.floor(y)
  const fx = x - ix, fy = y - iy
  const sx = fx * fx * (3 - 2 * fx), sy = fy * fy * (3 - 2 * fy)
  const a = hash(ix, iy, seed), b = hash(ix + 1, iy, seed), c = hash(ix, iy + 1, seed), d = hash(ix + 1, iy + 1, seed)
  return a + (b - a) * sx + (c - a) * sy + (a - b - c + d) * sx * sy
}

function fbm(x: number, y: number, seed: number, octaves = 5): number {
  let v = 0, amp = 0.5, f = 1
  for (let i = 0; i < octaves; i++) {
    v += amp * vnoise(x * f, y * f, seed + i * 17)
    amp *= 0.5
    f *= 2.03
  }
  return v
}

function canvasTexture(w: number, h: number, fill: (x: number, y: number, out: number[]) => void, srgb = true): THREE.CanvasTexture {
  const c = document.createElement('canvas')
  c.width = w
  c.height = h
  const ctx = c.getContext('2d')!
  const img = ctx.createImageData(w, h)
  const px = [0, 0, 0, 255]
  for (let y = 0; y < h; y++) {
    for (let x = 0; x < w; x++) {
      px[3] = 255
      fill(x / w, y / h, px)
      const i = (y * w + x) * 4
      img.data[i] = px[0]
      img.data[i + 1] = px[1]
      img.data[i + 2] = px[2]
      img.data[i + 3] = px[3]
    }
  }
  ctx.putImageData(img, 0, 0)
  return finish(new THREE.CanvasTexture(c), srgb) as THREE.CanvasTexture
}

export function proceduralPlanet(seed: number, base: THREE.Color, kind: 'rock' | 'gas' | 'ice' | 'lava' = 'rock'): THREE.Texture {
  return canvasTexture(1024, 512, (u, v, px) => {
    let r: number, g: number, b: number
    if (kind === 'gas') {
      const band = fbm(u * 3 + fbm(u * 8, v * 40, seed) * 0.15, v * 24, seed, 4)
      const t = 0.55 + 0.45 * Math.sin(v * 40 + band * 6)
      r = base.r * (0.7 + 0.5 * t); g = base.g * (0.7 + 0.5 * t); b = base.b * (0.7 + 0.5 * t)
    } else {
      const n = fbm(u * 8, v * 4, seed)
      const m = fbm(u * 30, v * 15, seed + 5, 4)
      const shade = 0.55 + 0.6 * n + 0.15 * m
      r = base.r * shade; g = base.g * shade; b = base.b * shade
      if (kind === 'ice') { r = r * 0.5 + 0.5; g = g * 0.5 + 0.55; b = b * 0.5 + 0.6 }
    }
    px[0] = Math.min(255, r * 255); px[1] = Math.min(255, g * 255); px[2] = Math.min(255, b * 255)
  })
}

export function proceduralSun(): THREE.Texture {
  return canvasTexture(1024, 512, (u, v, px) => {
    const n = fbm(u * 16, v * 8, 99, 5)
    const s = 0.75 + 0.35 * n
    px[0] = 255 * Math.min(1, s * 1.0)
    px[1] = 255 * Math.min(1, s * 0.75)
    px[2] = 255 * Math.min(1, s * 0.45)
  })
}

/** Ring colour/alpha strip: rgb = colour, a = optical depth. */
export function proceduralRingStrip(seed = 3): THREE.Texture {
  const tex = canvasTexture(1024, 4, (u, _v, px) => {
    const n = fbm(u * 60, 0.5, seed, 4)
    const gap = Math.exp(-Math.pow((u - 0.62) / 0.03, 2)) + 0.6 * Math.exp(-Math.pow((u - 0.85) / 0.01, 2))
    const a = Math.max(0, (0.35 + 0.65 * n) * (1 - gap) * Math.min(1, u * 12) * Math.min(1, (1 - u) * 8))
    px[0] = 230; px[1] = 215; px[2] = 190; px[3] = a * 255
  })
  tex.wrapS = THREE.ClampToEdgeWrapping
  return tex
}

export function proceduralMilkyWay(): THREE.Texture {
  return canvasTexture(2048, 1024, (u, v, px) => {
    const band = Math.exp(-Math.pow((v - 0.5) / 0.09, 2))
    const cloud = fbm(u * 12, v * 6, 11, 5)
    const dust = fbm(u * 30, v * 15, 12, 4)
    const glow = band * (0.15 + 0.85 * cloud) * (1 - 0.6 * Math.pow(dust, 2)) * 90
    const star = hash(Math.floor(u * 2048), Math.floor(v * 1024), 5) > 0.997 ? 140 : 0
    px[0] = Math.min(255, glow * 0.95 + star); px[1] = Math.min(255, glow * 0.9 + star); px[2] = Math.min(255, glow * 1.05 + star)
  })
}

/** Soft radial sprite used for glows, flashes and lens flare elements. */
export function radialSprite(size = 256, power = 2, hue: [number, number, number] = [1, 1, 1]): THREE.Texture {
  const tex = canvasTexture(size, size, (u, v, px) => {
    const d = Math.hypot(u - 0.5, v - 0.5) * 2
    const a = Math.pow(Math.max(0, 1 - d), power)
    px[0] = 255 * hue[0]; px[1] = 255 * hue[1]; px[2] = 255 * hue[2]; px[3] = 255 * a
  })
  tex.wrapS = THREE.ClampToEdgeWrapping
  return tex
}

export function whiteTexture(): THREE.Texture {
  return canvasTexture(4, 4, (_u, _v, px) => { px[0] = 255; px[1] = 255; px[2] = 255 }, false)
}
