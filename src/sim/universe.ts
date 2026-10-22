import { G } from './constants'
import { Body } from './body'
import { Vec3, v3, add, scale, sub, length } from './vec'
import { TestParticles } from './particles'

export interface CollisionEvent {
  a: Body
  b: Body
  time: number
  /** Positions at closest approach during the substep, when the contact was found by the swept test. */
  posA?: Vec3
  posB?: Vec3
}
export type CollisionHandler = (ev: CollisionEvent, u: Universe) => void

export interface UniverseSnapshot {
  time: number
  bodies: Body[]
  label: string
}

/**
 * Smallest fraction of a pair's gravitational timescale that its crossing time is allowed to demand.
 * At this ratio the flyby changes the relative velocity by about 2e-4 of itself.
 */
const ENCOUNTER_FLOOR = 0.01
/** step() calls without a collision that end a cascade; about half a second of frames. */
const HISTORY_QUIET_STEPS = 30

interface PendingPair {
  a: Body
  b: Body
  posA: Vec3
  posB: Vec3
}

/**
 * Full mutual N-body gravity, integrated with kick-drift-kick leapfrog (symplectic, time reversible).
 * Substeps adapt to the shortest dynamical timescale so close encounters stay accurate; a budget on
 * pair evaluations per frame throttles the time warp instead of losing accuracy.
 *
 * The integrator works on flat Float64Arrays packed from `bodies` at the start of each step and
 * written back at the end, which keeps the inner loop free of object dereferences.
 */
export class Universe {
  bodies: Body[] = []
  /** Seconds since epochJD. */
  time = 0
  epochJD: number
  substepsLastFrame = 0
  warpLimited = false
  maxSubstepsPerFrame = 12000
  /**
   * Force evaluations allowed per call to step(), as a proxy for wall-clock time: the substep budget
   * is this divided by the number of pairs. Infinity here means "only maxSubstepsPerFrame applies";
   * the app sets a real number so that frame time stays bounded as bodies are added.
   */
  maxPairEvalsPerFrame = Infinity
  /** Force pair evaluations the last step() spent, collision re-packs included. */
  pairEvalsLastFrame = 0
  /**
   * Ceiling on how many bodies may exist. Every shatter can add eighteen and each fragment can be
   * shattered again, so without a stop a few shots breed thousands; fragmentation folds the excess
   * back into the remnant instead, which still conserves mass and momentum exactly.
   */
  maxBodies = 240
  maxSubstep = 4 * 3600
  minSubstep = 0.25
  /** Fraction of the shortest dynamical time used per substep. */
  dynFraction = 0.02
  particles: TestParticles | null = null
  collisionHandlers: CollisionHandler[] = []
  history: UniverseSnapshot[] = []
  /** Calls to step() so far: the clock history coalescing counts in, since sim has no wall clock. */
  private steps = 0
  private lastCollisionStep = -Infinity

  private pending: PendingPair[] = []
  private dynTime = Infinity
  private byIdMap = new Map<string, Body>()

  private n = 0
  private capacity = 0
  private px = new Float64Array(0)
  private py = new Float64Array(0)
  private pz = new Float64Array(0)
  private vx = new Float64Array(0)
  private vy = new Float64Array(0)
  private vz = new Float64Array(0)
  private ax = new Float64Array(0)
  private ay = new Float64Array(0)
  private az = new Float64Array(0)
  private mass = new Float64Array(0)
  private rad = new Float64Array(0)

  constructor(epochJD: number) {
    this.epochJD = epochJD
  }

  get jd(): number {
    return this.epochJD + this.time / 86400
  }

  add(b: Body): void {
    this.bodies.push(b)
    this.byIdMap.set(b.id, b)
  }

  remove(b: Body): void {
    const i = this.bodies.indexOf(b)
    if (i >= 0) this.bodies.splice(i, 1)
    this.byIdMap.delete(b.id)
  }

  byId(id: string): Body | undefined {
    return this.byIdMap.get(id)
  }

  setBodies(bodies: Body[]): void {
    this.bodies = bodies
    this.byIdMap.clear()
    for (const b of bodies) this.byIdMap.set(b.id, b)
  }

  /** How many more bodies may be created before maxBodies is reached. */
  get roomForBodies(): number {
    return Math.max(0, this.maxBodies - this.bodies.length)
  }

  /** Shortest dynamical timescale found by the last force evaluation, seconds. */
  get shortestDynamicalTime(): number {
    return this.dynTime
  }

  private pack(): void {
    const bs = this.bodies
    const n = (this.n = bs.length)
    if (n > this.capacity) {
      const cap = Math.max(16, n * 2)
      this.capacity = cap
      this.px = new Float64Array(cap); this.py = new Float64Array(cap); this.pz = new Float64Array(cap)
      this.vx = new Float64Array(cap); this.vy = new Float64Array(cap); this.vz = new Float64Array(cap)
      this.ax = new Float64Array(cap); this.ay = new Float64Array(cap); this.az = new Float64Array(cap)
      this.mass = new Float64Array(cap); this.rad = new Float64Array(cap)
    }
    for (let i = 0; i < n; i++) {
      const b = bs[i]
      this.px[i] = b.pos.x; this.py[i] = b.pos.y; this.pz[i] = b.pos.z
      this.vx[i] = b.vel.x; this.vy[i] = b.vel.y; this.vz[i] = b.vel.z
      this.mass[i] = b.mass; this.rad[i] = b.radius
    }
    this.accelerate(0)
  }

  private unpack(): void {
    const bs = this.bodies
    for (let i = 0; i < this.n; i++) {
      const b = bs[i]
      b.pos.x = this.px[i]; b.pos.y = this.py[i]; b.pos.z = this.pz[i]
      b.vel.x = this.vx[i]; b.vel.y = this.vy[i]; b.vel.z = this.vz[i]
      b.acc.x = this.ax[i]; b.acc.y = this.ay[i]; b.acc.z = this.az[i]
    }
  }

  /** Accelerations and the shortest dynamical time. `driftH` non-zero also detects contacts swept out by that drift. */
  private accelerate(driftH: number): void {
    const { px, py, pz, vx, vy, vz, ax, ay, az, mass, rad } = this
    const n = this.n
    for (let i = 0; i < n; i++) { ax[i] = 0; ay[i] = 0; az[i] = 0 }
    let minDyn = Infinity
    const detect = driftH !== 0
    const absDrift = driftH < 0 ? -driftH : driftH
    for (let i = 0; i < n; i++) {
      const pix = px[i], piy = py[i], piz = pz[i]
      const vix = vx[i], viy = vy[i], viz = vz[i]
      const mi = mass[i]
      let aix = ax[i], aiy = ay[i], aiz = az[i]
      for (let j = i + 1; j < n; j++) {
        const dx = px[j] - pix, dy = py[j] - piy, dz = pz[j] - piz
        const r2 = dx * dx + dy * dy + dz * dz
        const sum = rad[i] + rad[j]
        // Point-mass gravity is only right outside the bodies. Softening the force at contact costs
        // nothing for a pair that is not touching and keeps a head-on hit, where the centres land on
        // top of each other mid-substep, from firing an infinite kick before the contact resolves.
        const sum2 = sum * sum
        const rs2 = r2 > sum2 ? r2 : sum2
        const rs = Math.sqrt(rs2)
        const inv3 = 1 / (rs2 * rs)
        const gi = G * mass[j] * inv3
        const gj = G * mi * inv3
        aix += dx * gi; aiy += dy * gi; aiz += dz * gi
        ax[j] -= dx * gj; ay[j] -= dy * gj; az[j] -= dz * gj
        const dvx = vx[j] - vix, dvy = vy[j] - viy, dvz = vz[j] - viz
        const vrel = Math.sqrt(dvx * dvx + dvy * dvy + dvz * dvz)
        const mu = G * (mi + mass[j])
        if (mu > 0) {
          // Gravitational timescale of the pair: how long it takes to bend each other's path.
          const grav = Math.sqrt((rs2 * rs) / mu)
          let pair = grav
          if (vrel > 0) {
            // A pair also has to be sampled as it flies past, but only as finely as the deflection
            // deserves: over one crossing time the relative velocity changes by ~2(cross/grav)^2 of
            // itself, so an encounter far faster than the pair's own gravity bends almost nothing and
            // does not need resolving. Without this floor a cloud of fragments drifting past each
            // other pins the substep at its minimum and the clock all but stops.
            const cross = rs / vrel
            const significant = cross > grav * ENCOUNTER_FLOOR ? cross : grav * ENCOUNTER_FLOOR
            if (significant < pair) pair = significant
          }
          if (pair < minDyn) minDyn = pair
        }
        // Closest approach along the straight line each body travelled during the drift. Without
        // this a small fast body slips through a small target between two samples of its position.
        // The pair can only touch if it is within one drift's worth of closing distance, which
        // rejects almost every pair before the swept test.
        const reach = sum + vrel * absDrift
        if (detect && r2 < reach * reach) {
          const ux = dvx * driftH, uy = dvy * driftH, uz = dvz * driftH
          const ex = dx - ux, ey = dy - uy, ez = dz - uz
          const uu = ux * ux + uy * uy + uz * uz
          let s = 1
          if (uu > 0) {
            s = -(ex * ux + ey * uy + ez * uz) / uu
            s = s < 0 ? 0 : s > 1 ? 1 : s
          }
          const cx = ex + s * ux, cy = ey + s * uy, cz = ez + s * uz
          if (cx * cx + cy * cy + cz * cz < sum * sum) {
            const back = (1 - s) * driftH
            this.pending.push({
              a: this.bodies[i], b: this.bodies[j],
              posA: { x: pix - vix * back, y: piy - viy * back, z: piz - viz * back },
              posB: { x: px[j] - vx[j] * back, y: py[j] - vy[j] * back, z: pz[j] - vz[j] * back },
            })
          }
        }
      }
      ax[i] = aix; ay[i] = aiy; az[i] = aiz
    }
    this.dynTime = minDyn
  }

  /** Advance by dt seconds (negative rewinds). Returns the time actually advanced. */
  step(dt: number): number {
    this.steps++
    this.substepsLastFrame = 0
    this.warpLimited = false
    this.pending.length = 0
    this.pack()
    let remaining = dt
    const sign = dt < 0 ? -1 : 1
    // The budget is counted in pair evaluations as they are spent, not fixed from n up front: a
    // shatter mid-frame adds up to 17 bodies, and every substep after it costs the new, larger n
    // squared. Sized once from the old n, the rest of that frame overran its budget about twofold.
    // At constant n this stops on exactly the substep the old fixed count did.
    this.pairEvalsLastFrame = 0
    while (Math.abs(remaining) > 1e-6) {
      const pairs = Math.max(1, (this.n * (this.n - 1)) / 2)
      const overBudget = this.substepsLastFrame >= 8 && this.pairEvalsLastFrame + pairs > this.maxPairEvalsPerFrame
      if (this.substepsLastFrame >= this.maxSubstepsPerFrame || overBudget) {
        this.warpLimited = true
        break
      }
      let h = Math.min(this.maxSubstep, Math.max(this.minSubstep, this.dynFraction * this.dynTime))
      if (h > Math.abs(remaining)) h = Math.abs(remaining)
      h *= sign
      this.substep(h)
      this.pairEvalsLastFrame += pairs
      this.time += h
      remaining -= h
      this.substepsLastFrame++
      if (this.pending.length) {
        this.resolveCollisions()
        // resolveCollisions re-packs, and pack() ends with a full force evaluation at the new n.
        this.pairEvalsLastFrame += Math.max(1, (this.n * (this.n - 1)) / 2)
      }
    }
    this.unpack()
    const advanced = dt - remaining
    if (this.particles) this.particles.step(advanced, this.bodies)
    return advanced
  }

  private substep(h: number): void {
    const { px, py, pz, vx, vy, vz, ax, ay, az } = this
    const n = this.n
    const hh = h / 2
    for (let i = 0; i < n; i++) { vx[i] += ax[i] * hh; vy[i] += ay[i] * hh; vz[i] += az[i] * hh }
    for (let i = 0; i < n; i++) { px[i] += vx[i] * h; py[i] += vy[i] * h; pz[i] += vz[i] * h }
    this.accelerate(h)
    for (let i = 0; i < n; i++) { vx[i] += ax[i] * hh; vy[i] += ay[i] * hh; vz[i] += az[i] * hh }
  }

  private resolveCollisions(): void {
    const pairs = this.pending
    this.pending = []
    this.unpack()
    const seen = new Set<string>()
    for (const { a, b, posA, posB } of pairs) {
      if (!this.byIdMap.has(a.id) || !this.byIdMap.has(b.id)) continue
      const key = a.id + '|' + b.id
      if (seen.has(key)) continue
      seen.add(key)
      for (const h of this.collisionHandlers) h({ a, b, time: this.time, posA, posB }, this)
    }
    this.pack()
  }

  snapshot(label = ''): UniverseSnapshot {
    return { time: this.time, label, bodies: structuredClone(this.bodies) }
  }

  restore(s: UniverseSnapshot): void {
    this.time = s.time
    this.setBodies(structuredClone(s.bodies))
    this.pending.length = 0
    this.lastCollisionStep = -Infinity
  }

  /**
   * Record an undo point before an impact, unless this collision continues a cascade that already
   * has one. A shatter's fragments go on hitting each other for many frames, and a snapshot per
   * collision meant a structuredClone of every body per hit - a GC storm mid-cascade - while one
   * cascade flushed all eight undo slots with near-identical states. A cascade is collisions fewer
   * than HISTORY_QUIET_STEPS step() calls apart; undo takes it back to before the first of them.
   */
  pushHistory(label: string): void {
    const quiet = this.steps - this.lastCollisionStep > HISTORY_QUIET_STEPS
    this.lastCollisionStep = this.steps
    if (!quiet) return
    this.history.push(this.snapshot(label))
    if (this.history.length > 8) this.history.shift()
  }

  popHistory(): UniverseSnapshot | undefined {
    const s = this.history.pop()
    if (s) this.restore(s)
    return s
  }

  /** Total mechanical energy, J. */
  energy(): { kinetic: number; potential: number; total: number } {
    let k = 0, p = 0
    const bs = this.bodies
    for (let i = 0; i < bs.length; i++) {
      const b = bs[i]
      const v2 = b.vel.x * b.vel.x + b.vel.y * b.vel.y + b.vel.z * b.vel.z
      k += 0.5 * b.mass * v2 * 1e6
      for (let j = i + 1; j < bs.length; j++) {
        p -= (G * 1e6 * b.mass * bs[j].mass) / length(sub(bs[j].pos, b.pos))
      }
    }
    return { kinetic: k, potential: p, total: k + p }
  }

  barycentre(): { pos: Vec3; vel: Vec3; mass: number } {
    let m = 0
    let r = v3(), v = v3()
    for (const b of this.bodies) {
      m += b.mass
      r = add(r, scale(b.pos, b.mass))
      v = add(v, scale(b.vel, b.mass))
    }
    return { pos: scale(r, 1 / m), vel: scale(v, 1 / m), mass: m }
  }

  /** The body whose gravity dominates at a point (Hill-sphere style), defaulting to the most massive body. */
  dominantBody(pos: Vec3, exclude?: Body): Body | undefined {
    let best: Body | undefined
    let bestAcc = -1
    for (const b of this.bodies) {
      if (b === exclude) continue
      const d = sub(pos, b.pos)
      const r2 = d.x * d.x + d.y * d.y + d.z * d.z
      if (r2 === 0) continue
      const a = b.mass / r2
      if (a > bestAcc) {
        bestAcc = a
        best = b
      }
    }
    return best
  }

  /**
   * The body `b` is most tightly bound to: among everything heavier than it that it is gravitationally
   * bound to, the nearest. That makes the Moon orbit Earth rather than the Sun, and a captured rock
   * orbit whatever actually captured it.
   */
  primaryOf(b: Body): Body | undefined {
    let best: Body | undefined
    let bestDist = Infinity
    let fallback: Body | undefined
    let fallbackAcc = -1
    for (const o of this.bodies) {
      if (o === b || o.mass <= b.mass) continue
      const r = sub(b.pos, o.pos)
      const d = length(r)
      if (d <= 0) continue
      const v = sub(b.vel, o.vel)
      const specificEnergy = (v.x * v.x + v.y * v.y + v.z * v.z) / 2 - (G * (o.mass + b.mass)) / d
      if (specificEnergy < 0 && d < bestDist) {
        bestDist = d
        best = o
      }
      const acc = o.mass / (d * d)
      if (acc > fallbackAcc) {
        fallbackAcc = acc
        fallback = o
      }
    }
    return best ?? fallback
  }
}
