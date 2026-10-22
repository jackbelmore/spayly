export interface Vec3 { x: number; y: number; z: number }

export const v3 = (x = 0, y = 0, z = 0): Vec3 => ({ x, y, z })
export const clone = (a: Vec3): Vec3 => ({ x: a.x, y: a.y, z: a.z })
export const add = (a: Vec3, b: Vec3): Vec3 => ({ x: a.x + b.x, y: a.y + b.y, z: a.z + b.z })
export const sub = (a: Vec3, b: Vec3): Vec3 => ({ x: a.x - b.x, y: a.y - b.y, z: a.z - b.z })
export const scale = (a: Vec3, s: number): Vec3 => ({ x: a.x * s, y: a.y * s, z: a.z * s })
export const dot = (a: Vec3, b: Vec3): number => a.x * b.x + a.y * b.y + a.z * b.z
export const cross = (a: Vec3, b: Vec3): Vec3 => ({
  x: a.y * b.z - a.z * b.y,
  y: a.z * b.x - a.x * b.z,
  z: a.x * b.y - a.y * b.x,
})
export const length = (a: Vec3): number => Math.sqrt(a.x * a.x + a.y * a.y + a.z * a.z)
export const distance = (a: Vec3, b: Vec3): number => length(sub(a, b))
export const normalize = (a: Vec3): Vec3 => {
  const l = length(a)
  return l > 0 ? scale(a, 1 / l) : v3()
}
export const lerp = (a: Vec3, b: Vec3, t: number): Vec3 => ({
  x: a.x + (b.x - a.x) * t,
  y: a.y + (b.y - a.y) * t,
  z: a.z + (b.z - a.z) * t,
})
export const clamp = (x: number, lo: number, hi: number): number => Math.min(hi, Math.max(lo, x))
/** Any unit vector perpendicular to a. */
export const anyPerpendicular = (a: Vec3): Vec3 => {
  const n = normalize(a)
  const helper = Math.abs(n.x) < 0.9 ? v3(1, 0, 0) : v3(0, 1, 0)
  return normalize(cross(n, helper))
}
/** Uniformly distributed random unit vector. */
export const randomUnit = (rand: () => number = Math.random): Vec3 => {
  const z = rand() * 2 - 1
  const t = rand() * Math.PI * 2
  const r = Math.sqrt(1 - z * z)
  return { x: r * Math.cos(t), y: r * Math.sin(t), z }
}
/** Deterministic PRNG (mulberry32). */
export function makeRandom(seed: number): () => number {
  let a = seed >>> 0
  return () => {
    a = (a + 0x6d2b79f5) >>> 0
    let t = a
    t = Math.imul(t ^ (t >>> 15), t | 1)
    t ^= t + Math.imul(t ^ (t >>> 7), t | 61)
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296
  }
}
