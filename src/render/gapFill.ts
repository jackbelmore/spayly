import { fbm3, vnoise3 } from './noise'

/**
 * Pixel work on decoded maps: filling no-data regions and tinting. Pure arithmetic on RGBA bytes
 * with no DOM or three.js, so it runs in the texture worker and, failing that, on the main thread.
 */

/** Widest grid the pull-push inpaint works on. Wider maps are filled at this size and scaled back. */
const FILL_WIDTH = 2048

const isBlank = (d: Uint8ClampedArray, o: number) => d[o] <= 10 && d[o + 1] <= 10 && d[o + 2] <= 10

/**
 * How far the imaged region is pulled back from a no-data edge before anything is filled.
 *
 * The pixels straddling that edge carry the encoder's ringing against the black surround, which
 * makes them much darker than the terrain inland — measured across the border of these maps, the
 * median is 89 against 168 on Triton, 96 against 126 on Charon. The inpaint pulls from precisely
 * those pixels, so leaving them in stains the entire invented hemisphere dark and leaves a
 * speckled rim along the join. Discarding a few pixels of real terrain costs nothing by comparison.
 */
const EDGE_BLEED = 3

/** Approximate distance in pixels from the imaged region, measured across the filled region. */
function edgeDistance(fill: Uint8Array, w: number, h: number): Float32Array {
  const dist = new Float32Array(w * h)
  for (let i = 0; i < w * h; i++) dist[i] = fill[i] ? 1e9 : 0
  const at = (x: number, y: number) => y * w + ((((x % w) + w) % w))
  for (let y = 0; y < h; y++) {
    for (let x = 0; x < w; x++) {
      const i = y * w + x
      if (dist[i] === 0) continue
      let v = dist[i]
      if (y > 0) {
        v = Math.min(v, dist[at(x, y - 1)] + 1, dist[at(x - 1, y - 1)] + 1.414, dist[at(x + 1, y - 1)] + 1.414)
      }
      dist[i] = Math.min(v, dist[at(x - 1, y)] + 1)
    }
  }
  for (let y = h - 1; y >= 0; y--) {
    for (let x = w - 1; x >= 0; x--) {
      const i = y * w + x
      if (dist[i] === 0) continue
      let v = dist[i]
      if (y < h - 1) {
        v = Math.min(v, dist[at(x, y + 1)] + 1, dist[at(x - 1, y + 1)] + 1.414, dist[at(x + 1, y + 1)] + 1.414)
      }
      dist[i] = Math.min(v, dist[at(x + 1, y)] + 1)
    }
  }
  return dist
}

/** Grow a mask by r pixels, wrapping in longitude and clamping at the poles. */
function dilate(mask: Uint8Array, w: number, h: number, r: number): Uint8Array {
  const tmp = new Uint8Array(w * h)
  for (let y = 0; y < h; y++) {
    const row = y * w
    for (let x = 0; x < w; x++) {
      for (let dx = -r; dx <= r; dx++) {
        if (mask[row + ((((x + dx) % w) + w) % w)]) { tmp[row + x] = 1; break }
      }
    }
  }
  const out = new Uint8Array(w * h)
  for (let y = 0; y < h; y++) {
    for (let x = 0; x < w; x++) {
      for (let dy = -r; dy <= r; dy++) {
        if (tmp[Math.min(h - 1, Math.max(0, y + dy)) * w + x]) { out[y * w + x] = 1; break }
      }
    }
  }
  return out
}

/** Multiply a map by a colour, so two bodies sharing one file do not look like twins. */
export function applyTint(d: Uint8ClampedArray, hex: string): void {
  const n = parseInt(hex.replace('#', ''), 16)
  const tr = ((n >> 16) & 255) / 128
  const tg = ((n >> 8) & 255) / 128
  const tb = (n & 255) / 128
  for (let i = 0; i < d.length; i += 4) {
    d[i] = Math.min(255, d[i] * tr)
    d[i + 1] = Math.min(255, d[i + 1] * tg)
    d[i + 2] = Math.min(255, d[i + 2] * tb)
  }
}

/**
 * Grow the imaged terrain over the blank parts of a map, in place.
 *
 * The inpaint's float pyramid costs about 20 bytes a pixel, so a map wider than FILL_WIDTH is filled
 * on a reduced copy and only its blank pixels are written back at full size. The imaged pixels keep
 * their full resolution, which is the point: a map that is 0.2% no-data should not lose half its
 * width to the 0.2%.
 */
export function fillNoData(d: Uint8ClampedArray, w: number, h: number): void {
  if (w <= FILL_WIDTH) {
    inpaint(d, w, h, EDGE_BLEED)
    return
  }
  const f = Math.ceil(w / FILL_WIDTH)
  const lw = Math.ceil(w / f)
  const lh = Math.ceil(h / f)
  const low = new Uint8ClampedArray(lw * lh * 4)
  // A block holding any no-data at all is left blank rather than averaged, which erodes the
  // imaged area by one block and keeps the contaminated edge pixels (see EDGE_BLEED) out of it.
  const lowBlank = new Uint8Array(lw * lh)
  let blank = 0
  for (let ly = 0; ly < lh; ly++) {
    for (let lx = 0; lx < lw; lx++) {
      let r = 0, g = 0, b = 0, n = 0, holed = false
      const y1 = Math.min(h, (ly + 1) * f)
      const x1 = Math.min(w, (lx + 1) * f)
      for (let y = ly * f; y < y1; y++) {
        for (let x = lx * f; x < x1; x++) {
          const o = (y * w + x) * 4
          if (isBlank(d, o)) { holed = true; continue }
          r += d[o]; g += d[o + 1]; b += d[o + 2]; n++
        }
      }
      const i = ly * lw + lx
      const o = i * 4
      if (holed) { lowBlank[i] = 1; blank++ }
      else if (n) { low[o] = r / n; low[o + 1] = g / n; low[o + 2] = b / n }
      low[o + 3] = 255
    }
  }
  if (blank === 0) return
  const amp = inpaint(low, lw, lh)

  // Scale the filled copy back up into the blank pixels only, with a pixel-scale grain so the
  // invented ground is not visibly softer than the real ground beside it.
  const F = w / (6 * Math.PI)
  for (let y = 0; y < h; y++) {
    const fy = Math.min(lh - 1, Math.max(0, (y + 0.5) / f - 0.5))
    const y0 = Math.floor(fy)
    const ty = fy - y0
    const y1 = Math.min(lh - 1, y0 + 1)
    const theta = ((y + 0.5) / h) * Math.PI
    const st = Math.sin(theta) * F
    const cy = Math.cos(theta) * F
    const lrow = Math.min(lh - 1, (y / f) | 0) * lw
    for (let x = 0; x < w; x++) {
      const o = (y * w + x) * 4
      if (!isBlank(d, o) && !lowBlank[lrow + Math.min(lw - 1, (x / f) | 0)]) continue
      const fx = (x + 0.5) / f - 0.5
      const x0f = Math.floor(fx)
      const tx = fx - x0f
      const xa = ((x0f % lw) + lw) % lw
      const xb = (xa + 1) % lw
      const a = (y0 * lw + xa) * 4, b = (y0 * lw + xb) * 4, c = (y1 * lw + xa) * 4, e = (y1 * lw + xb) * 4
      const w00 = (1 - tx) * (1 - ty), w10 = tx * (1 - ty), w01 = (1 - tx) * ty, w11 = tx * ty
      const phi = ((x + 0.5) / w) * 2 * Math.PI
      const k = 1 + amp * 0.25 * (vnoise3(st * Math.cos(phi) + 64, cy + 64, st * Math.sin(phi) + 64, 23) - 0.5) * 2
      for (let ch = 0; ch < 3; ch++) {
        d[o + ch] = Math.min(255, (low[a + ch] * w00 + low[b + ch] * w10 + low[c + ch] * w01 + low[e + ch] * w11) * k)
      }
    }
  }
}

/**
 * Pull-push pyramid inpaint, in place. Returns the relative amplitude of the roughening it applied.
 *
 * Voyager 2 only saw the southern half of the Uranian moons, so a nearest-pixel fill would drag one
 * row of pixels all the way to the north pole and leave visible streaks. Instead: average the image
 * into a stack of half-size levels ignoring blank pixels, then push each level back down into the
 * holes of the one below. Large voids come back as a smooth continuation of the surrounding terrain,
 * which then gets a little fractal noise so it does not read as a painted-on patch. The filled area
 * is invented; it is not data.
 */
function inpaint(d: Uint8ClampedArray, w: number, h: number, erode = 0): number {
  const n = w * h
  const colour = new Float32Array(n * 3)
  const weight = new Float32Array(n)
  const hole = new Uint8Array(n)
  for (let i = 0; i < n; i++) if (isBlank(d, i * 4)) hole[i] = 1
  const fill = erode > 0 ? dilate(hole, w, h, erode) : hole
  let blank = 0
  for (let i = 0; i < n; i++) {
    const o = i * 4
    if (fill[i]) {
      blank++
    } else {
      colour[i * 3] = d[o]
      colour[i * 3 + 1] = d[o + 1]
      colour[i * 3 + 2] = d[o + 2]
      weight[i] = 1
    }
  }
  if (blank === 0 || blank === n) return 0
  const weight0 = weight.slice()

  interface Level { w: number; h: number; colour: Float32Array; weight: Float32Array }
  const levels: Level[] = [{ w, h, colour, weight }]
  while (levels[levels.length - 1].w > 2 && levels[levels.length - 1].h > 2) {
    const p = levels[levels.length - 1]
    const nw = p.w >> 1
    const nh = p.h >> 1
    const c = new Float32Array(nw * nh * 3)
    const a = new Float32Array(nw * nh)
    for (let y = 0; y < nh; y++) {
      for (let x = 0; x < nw; x++) {
        let r = 0, g = 0, b = 0, sum = 0
        for (let dy = 0; dy < 2; dy++) {
          for (let dx = 0; dx < 2; dx++) {
            const si = Math.min(p.h - 1, y * 2 + dy) * p.w + Math.min(p.w - 1, x * 2 + dx)
            const sa = p.weight[si]
            r += p.colour[si * 3] * sa
            g += p.colour[si * 3 + 1] * sa
            b += p.colour[si * 3 + 2] * sa
            sum += sa
          }
        }
        const i = y * nw + x
        if (sum > 0) {
          c[i * 3] = r / sum
          c[i * 3 + 1] = g / sum
          c[i * 3 + 2] = b / sum
          a[i] = Math.min(1, sum)
        }
      }
    }
    levels.push({ w: nw, h: nh, colour: c, weight: a })
  }

  // Push each level into the holes of the one below it, sampling bilinearly and wrapping in longitude.
  const wts = [0, 0, 0, 0]
  for (let k = levels.length - 1; k > 0; k--) {
    const src = levels[k]
    const dst = levels[k - 1]
    for (let y = 0; y < dst.h; y++) {
      const fy = Math.min(src.h - 1.001, Math.max(0, (y + 0.5) / 2 - 0.5))
      const y0 = Math.floor(fy)
      const ty = fy - y0
      const y1 = Math.min(src.h - 1, y0 + 1)
      for (let x = 0; x < dst.w; x++) {
        const i = y * dst.w + x
        const a = dst.weight[i]
        if (a >= 1) continue
        const fx = (x + 0.5) / 2 - 0.5
        const x0 = Math.floor(fx)
        const tx = fx - x0
        const xa = ((x0 % src.w) + src.w) % src.w
        const xb = ((x0 + 1) % src.w + src.w) % src.w
        const s0 = y0 * src.w + xa, s1 = y0 * src.w + xb, s2 = y1 * src.w + xa, s3 = y1 * src.w + xb
        wts[0] = (1 - tx) * (1 - ty) * src.weight[s0]
        wts[1] = tx * (1 - ty) * src.weight[s1]
        wts[2] = (1 - tx) * ty * src.weight[s2]
        wts[3] = tx * ty * src.weight[s3]
        const sum = wts[0] + wts[1] + wts[2] + wts[3]
        if (sum <= 0) continue
        const blend = 1 - a
        for (let ch = 0; ch < 3; ch++) {
          const v = (src.colour[s0 * 3 + ch] * wts[0] + src.colour[s1 * 3 + ch] * wts[1] + src.colour[s2 * 3 + ch] * wts[2] + src.colour[s3 * 3 + ch] * wts[3]) / sum
          dst.colour[i * 3 + ch] = dst.colour[i * 3 + ch] * a + v * blend
        }
        dst.weight[i] = Math.min(1, a + Math.min(1, sum) * blend)
      }
    }
  }

  // Settle the invented ground onto the average of the imaged terrain as it gets further from
  // real data. Extrapolating from the boundary alone carries whatever the boundary happens to
  // have: Voyager's coverage of Triton ends in low-sun terrain far darker than the lit interior
  // (median 99 against 169), and the encoder's chroma fringing along the edge tints it. Left
  // alone that spreads across the whole invented hemisphere, and any long-range gradient in it
  // gathers into a bright spot at the pole once the map wraps onto a sphere. Near the join the
  // pull-push continuation still wins, so the two sides meet without a visible edge.
  const dist = edgeDistance(fill, w, h)
  const reach = Math.max(8, w * 0.08)
  let mr = 0, mg = 0, mb = 0, mn = 0
  for (let i = 0; i < n; i++) {
    if (weight0[i] < 1) continue
    mr += colour[i * 3]; mg += colour[i * 3 + 1]; mb += colour[i * 3 + 2]; mn++
  }
  if (mn) {
    mr /= mn; mg /= mn; mb /= mn
    for (let i = 0; i < n; i++) {
      if (weight0[i] >= 1) continue
      const u = Math.min(1, dist[i] / reach)
      const t = u * u * (3 - 2 * u)
      colour[i * 3] += (mr - colour[i * 3]) * t
      colour[i * 3 + 1] += (mg - colour[i * 3 + 1]) * t
      colour[i * 3 + 2] += (mb - colour[i * 3 + 2]) * t
    }
  }

  // Roughen what was invented so it reads as terrain rather than an airbrushed patch. The noise is
  // sampled on the sphere the map wraps onto, so it neither fans into spokes at the poles nor breaks
  // at the seam, as noise in map coordinates would, and its amplitude is set from how contrasty the
  // imaged terrain actually is, so the fill neither looks flat next to it nor louder than it.
  let lumSum = 0
  let lumSq = 0
  let lumN = 0
  for (let i = 0; i < n; i++) {
    if (weight0[i] < 1) continue
    const l = 0.299 * colour[i * 3] + 0.587 * colour[i * 3 + 1] + 0.114 * colour[i * 3 + 2]
    lumSum += l
    lumSq += l * l
    lumN++
  }
  const mean = lumN ? lumSum / lumN : 1
  const sigma = lumN ? Math.sqrt(Math.max(0, lumSq / lumN - mean * mean)) : 0
  const amp = Math.min(0.55, (sigma / Math.max(mean, 1)) * 1.6)
  const F = 26
  const cosPhi = new Float32Array(w)
  const sinPhi = new Float32Array(w)
  for (let x = 0; x < w; x++) {
    const phi = ((x + 0.5) / w) * 2 * Math.PI
    cosPhi[x] = Math.cos(phi)
    sinPhi[x] = Math.sin(phi)
  }
  for (let y = 0; y < h; y++) {
    const theta = ((y + 0.5) / h) * Math.PI
    const st = Math.sin(theta) * F
    const cy = Math.cos(theta) * F
    for (let x = 0; x < w; x++) {
      const i = y * w + x
      const o = i * 4
      if (weight0[i] >= 1) continue
      const k = 1 + amp * (fbm3(st * cosPhi[x] + 64, cy + 64, st * sinPhi[x] + 64, 17, 6) - 0.5) * 2
      d[o] = Math.min(255, colour[i * 3] * k)
      d[o + 1] = Math.min(255, colour[i * 3 + 1] * k)
      d[o + 2] = Math.min(255, colour[i * 3 + 2] * k)
    }
  }
  return amp
}
