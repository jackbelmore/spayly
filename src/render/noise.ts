/**
 * Value noise shared by the main thread and the texture worker, so it must not touch the DOM or
 * three.js.
 */

function hash3i(ix: number, iy: number, iz: number, seed: number): number {
  let h = (Math.imul(ix, 374761393) + Math.imul(iy, 668265263) + Math.imul(iz, 2147483647 ^ 12582917) + Math.imul(seed, 1442695041)) | 0
  h = Math.imul(h ^ (h >>> 13), 1274126177)
  return ((h ^ (h >>> 16)) >>> 0) / 4294967296
}

export function vnoise3(x: number, y: number, z: number, seed: number): number {
  const ix = Math.floor(x), iy = Math.floor(y), iz = Math.floor(z)
  const fx = x - ix, fy = y - iy, fz = z - iz
  const sx = fx * fx * (3 - 2 * fx), sy = fy * fy * (3 - 2 * fy), sz = fz * fz * (3 - 2 * fz)
  const c = (dx: number, dy: number, dz: number) => hash3i(ix + dx, iy + dy, iz + dz, seed)
  const x00 = c(0, 0, 0) + (c(1, 0, 0) - c(0, 0, 0)) * sx
  const x10 = c(0, 1, 0) + (c(1, 1, 0) - c(0, 1, 0)) * sx
  const x01 = c(0, 0, 1) + (c(1, 0, 1) - c(0, 0, 1)) * sx
  const x11 = c(0, 1, 1) + (c(1, 1, 1) - c(0, 1, 1)) * sx
  const y0 = x00 + (x10 - x00) * sy
  const y1 = x01 + (x11 - x01) * sy
  return y0 + (y1 - y0) * sz
}

/** Fractal value noise in three dimensions; sampling it on a sphere has no pole or seam. */
export function fbm3(x: number, y: number, z: number, seed: number, oct = 4): number {
  let v = 0, a = 0.5, f = 1
  for (let i = 0; i < oct; i++) {
    v += a * vnoise3(x * f, y * f, z * f, seed + i * 31)
    a *= 0.5
    f *= 2.1
  }
  return v
}
