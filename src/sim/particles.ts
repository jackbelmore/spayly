import { AU, DEG, G } from './constants'
import { Body } from './body'
import { elementsToState } from './kepler'
import { makeRandom } from './vec'

/**
 * Massless test particles (asteroid belt, Trojans, Kuiper belt). They feel every body's gravity
 * but exert none, and use a coarser leapfrog than the main bodies since they are decoration.
 */
export class TestParticles {
  count: number
  pos: Float64Array
  vel: Float64Array
  acc: Float64Array
  alive: Uint8Array
  /** 0 main belt, 1 Trojan, 2 Kuiper belt */
  family: Uint8Array
  maxSubstep = 6 * 3600
  maxSubsteps = 10
  absorbed = 0
  /** Bodies lighter than this are ignored for particle gravity (small asteroids). */
  massFloor = 1e19

  constructor(count: number) {
    this.count = count
    this.pos = new Float64Array(count * 3)
    this.vel = new Float64Array(count * 3)
    this.acc = new Float64Array(count * 3)
    this.alive = new Uint8Array(count)
    this.family = new Uint8Array(count)
  }

  computeAccelerations(bodies: Body[]): void {
    const { pos, acc, alive, count } = this
    acc.fill(0)
    const active = bodies.filter((b) => b.mass >= this.massFloor)
    for (let i = 0; i < count; i++) {
      if (!alive[i]) continue
      const ix = i * 3
      const px = pos[ix], py = pos[ix + 1], pz = pos[ix + 2]
      let ax = 0, ay = 0, az = 0
      for (let k = 0; k < active.length; k++) {
        const b = active[k]
        const dx = b.pos.x - px, dy = b.pos.y - py, dz = b.pos.z - pz
        const r2 = dx * dx + dy * dy + dz * dz
        if (r2 < b.radius * b.radius) {
          alive[i] = 0
          b.damage.absorbed++
          this.absorbed++
          ax = 0; ay = 0; az = 0
          break
        }
        const r = Math.sqrt(r2)
        const g = (G * b.mass) / (r2 * r)
        ax += dx * g; ay += dy * g; az += dz * g
      }
      acc[ix] = ax; acc[ix + 1] = ay; acc[ix + 2] = az
    }
  }

  step(dt: number, bodies: Body[]): void {
    if (dt === 0) return
    const nsub = Math.min(this.maxSubsteps, Math.max(1, Math.ceil(Math.abs(dt) / this.maxSubstep)))
    const h = dt / nsub
    const hh = h / 2
    const { pos, vel, acc, alive, count } = this
    for (let s = 0; s < nsub; s++) {
      for (let i = 0; i < count * 3; i++) vel[i] += acc[i] * hh
      for (let i = 0; i < count; i++) {
        if (!alive[i]) continue
        const ix = i * 3
        pos[ix] += vel[ix] * h; pos[ix + 1] += vel[ix + 1] * h; pos[ix + 2] += vel[ix + 2] * h
      }
      this.computeAccelerations(bodies)
      for (let i = 0; i < count * 3; i++) vel[i] += acc[i] * hh
    }
  }
}

/** Rayleigh-distributed random value with the given scale. */
const rayleigh = (rand: () => number, sigma: number) => sigma * Math.sqrt(-2 * Math.log(1 - rand() + 1e-12))

/** Fill the particle set with the main belt (with Kirkwood gaps), Jupiter Trojans and a cold Kuiper belt. */
export function populateBelts(p: TestParticles, sun: Body, jupiter: Body | undefined, counts = { belt: 2600, trojan: 700, kuiper: 900 }, seed = 7): void {
  const rand = makeRandom(seed)
  const mu = G * sun.mass
  let i = 0
  const place = (a: number, e: number, inc: number, Omega: number, omega: number, M: number, family: number) => {
    if (i >= p.count) return
    const { r, v } = elementsToState(mu, { a, e, i: inc, Omega, omega, M })
    const ix = i * 3
    p.pos[ix] = r.x + sun.pos.x; p.pos[ix + 1] = r.y + sun.pos.y; p.pos[ix + 2] = r.z + sun.pos.z
    p.vel[ix] = v.x + sun.vel.x; p.vel[ix + 1] = v.y + sun.vel.y; p.vel[ix + 2] = v.z + sun.vel.z
    p.alive[i] = 1
    p.family[i] = family
    i++
  }
  const gaps: [number, number][] = [[2.48, 2.52], [2.80, 2.84], [2.93, 2.97], [3.25, 3.29]]
  let placed = 0
  while (placed < counts.belt && i < p.count) {
    const a = 2.1 + rand() * 1.2
    if (gaps.some(([lo, hi]) => a > lo && a < hi)) continue
    place(a * AU, Math.min(0.32, rayleigh(rand, 0.075)), Math.min(25 * DEG, rayleigh(rand, 6 * DEG)), rand() * 2 * Math.PI, rand() * 2 * Math.PI, rand() * 2 * Math.PI, 0)
    placed++
  }
  if (jupiter) {
    // Trojans: same semi-major axis as Jupiter, mean longitude 60 degrees ahead/behind.
    const rel = { x: jupiter.pos.x - sun.pos.x, y: jupiter.pos.y - sun.pos.y, z: jupiter.pos.z - sun.pos.z }
    const lonJ = Math.atan2(rel.y, rel.x)
    const aJ = 5.2028 * AU
    for (let k = 0; k < counts.trojan && i < p.count; k++) {
      const side = k % 2 === 0 ? 1 : -1
      const lon = lonJ + side * 60 * DEG + (rand() - 0.5) * 24 * DEG
      const e = Math.min(0.2, rayleigh(rand, 0.05))
      const inc = Math.min(30 * DEG, rayleigh(rand, 9 * DEG))
      const Omega = rand() * 2 * Math.PI
      const omega = rand() * 2 * Math.PI
      // choose M so that the mean longitude Omega+omega+M lands at lon
      const M = ((lon - Omega - omega) % (2 * Math.PI) + 2 * Math.PI) % (2 * Math.PI)
      place(aJ * (1 + (rand() - 0.5) * 0.03), e, inc, Omega, omega, M, 1)
    }
  }
  for (let k = 0; k < counts.kuiper && i < p.count; k++) {
    const a = 39 + rand() * 9
    place(a * AU, Math.min(0.2, rayleigh(rand, 0.04)), Math.min(20 * DEG, rayleigh(rand, 3 * DEG)), rand() * 2 * Math.PI, rand() * 2 * Math.PI, rand() * 2 * Math.PI, 2)
  }
  p.count = i
}
