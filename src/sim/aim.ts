import { G } from './constants'
import { Vec3, add, scale, sub, normalize, length } from './vec'

/** The part of a body the predictor reads. A `Body` satisfies it; so does a plain snapshot. */
export interface WorldBody {
  id: string
  pos: Vec3
  vel: Vec3
  mass: number
  radius: number
}

/**
 * What the predictor integrates: a `Universe` satisfies it, and so does a structured-clone
 * snapshot, which is how aiming runs in a worker instead of stalling the page.
 */
export interface World {
  bodies: readonly WorldBody[]
}

/** A copy of just what the predictor needs, cheap enough to post to a worker every aim. */
export function snapshotWorld(w: World): World {
  return {
    bodies: w.bodies.map((b) => ({
      id: b.id, mass: b.mass, radius: b.radius,
      pos: { x: b.pos.x, y: b.pos.y, z: b.pos.z }, vel: { x: b.vel.x, y: b.vel.y, z: b.vel.z },
    })),
  }
}

export interface Probe {
  pos: Vec3
  vel: Vec3
  radius: number
}

export interface PredictHit {
  bodyId: string
  time: number
  pos: Vec3
  bodyPos: Vec3
  speed: number // km/s relative to the body
}

export interface Closest {
  bodyId: string
  dist: number
  time: number
  probePos: Vec3
  bodyPos: Vec3
}

export interface PredictResult {
  points: Vec3[]
  hit?: PredictHit
  closest?: Closest
  duration: number
  steps: number
  /** Gravity pair evaluations spent: the unit the budget is set in, and the real cost. */
  pairEvals: number
  /** The pair budget ran out before the horizon, so the path stops short. */
  truncated: boolean
}

export interface PredictOptions {
  maxSteps?: number
  maxPoints?: number
  trackId?: string
  dynFraction?: number
  maxSubstep?: number
  /**
   * Bodies lighter than this do not attract; they still move under everything that does and can
   * still be hit. Defaults to 1e-10 of the heaviest body: about 2e20 kg beside the Sun, so
   * roughly Enceladus and anything lighter. The tracked target always attracts.
   */
  massFloor?: number
  /** Stop once this many gravity pair evaluations have been spent (see `pairEvals`). */
  pairBudget?: number
}

/**
 * Integrate a massless probe through a copy of the universe (bodies keep attracting each other)
 * and return its path, the first collision, and the closest approach to a tracked body.
 */
export function predictPath(u: World, probe: Probe, horizon: number, opts: PredictOptions = {}): PredictResult {
  const maxSteps = opts.maxSteps ?? 3000
  const maxPoints = opts.maxPoints ?? 500
  const dynFraction = opts.dynFraction ?? 0.04
  const maxSubstep = opts.maxSubstep ?? 12 * 3600
  const bodies = u.bodies
  const n = bodies.length
  const px = new Float64Array(n), py = new Float64Array(n), pz = new Float64Array(n)
  const vx = new Float64Array(n), vy = new Float64Array(n), vz = new Float64Array(n)
  const ax = new Float64Array(n), ay = new Float64Array(n), az = new Float64Array(n)
  const m = new Float64Array(n), rad = new Float64Array(n)
  for (let i = 0; i < n; i++) {
    const b = bodies[i]
    px[i] = b.pos.x; py[i] = b.pos.y; pz[i] = b.pos.z
    vx[i] = b.vel.x; vy[i] = b.vel.y; vz[i] = b.vel.z
    m[i] = b.mass; rad[i] = b.radius
  }
  const trackIdx = opts.trackId ? bodies.findIndex((b) => b.id === opts.trackId) : -1

  // Only bodies above the mass floor attract. A fragment's pull on a trajectory is far below this
  // integration's own error, but keeping it costs twice over: every step becomes all-pairs, and
  // fragments passing close to each other or to their parent set tiny timescales that shrink the
  // step for the whole prediction, so the probe never reaches its target within maxSteps. With 200
  // fragments near the Moon that made one auto-aim take 33 s and still miss.
  //
  // Light bodies are obstacles here: they keep their radius so the probe can still hit them, and the
  // probe-crossing term below is what stops the probe stepping through one. Each follows its host,
  // the attractor pulling it hardest at the start: it takes the host's own acceleration plus the
  // host's pull on it, which keeps its orbit about the host right and drops only third bodies'
  // tides, negligible over a prediction's span. That makes each one O(1) a step instead of O(k).
  // Their own orbital timescales no longer set the step either, so a fragment skimming its parent
  // cannot slow the probe down. None of this touches a probe aimed at a light body: the tracked
  // target always counts as an attractor.
  let heaviest = 0
  for (let i = 0; i < n; i++) if (m[i] > heaviest) heaviest = m[i]
  const floor = opts.massFloor ?? heaviest * 1e-10
  const att: number[] = []
  const attracts = new Uint8Array(n)
  for (let i = 0; i < n; i++) {
    if (m[i] >= floor || i === trackIdx) { att.push(i); attracts[i] = 1 }
  }
  const k = att.length
  const host = new Int32Array(n).fill(-1)
  for (let i = 0; i < n; i++) {
    if (attracts[i]) continue
    let best = -1
    for (let c = 0; c < k; c++) {
      const j = att[c]
      const dx = px[j] - px[i], dy = py[j] - py[i], dz = pz[j] - pz[i]
      const pull = m[j] / (dx * dx + dy * dy + dz * dz)
      if (pull > best) { best = pull; host[i] = j }
    }
  }
  const pairsPerStep = (k * (k - 1)) / 2 + (n - k) + n
  const budget = opts.pairBudget ?? Infinity
  let pairEvals = 0
  let truncated = false

  let qx = probe.pos.x, qy = probe.pos.y, qz = probe.pos.z
  let wx = probe.vel.x, wy = probe.vel.y, wz = probe.vel.z
  let qax = 0, qay = 0, qaz = 0
  let dyn = Infinity
  let hitIdx = -1

  // Timescales are compared squared (r^3/mu, and r^2/v^2 for crossings) so each pair costs one
  // square root instead of two. Gravity is softened to the contact distance, as in the universe's
  // own force loop, so coincident bodies give a finite pull instead of NaN.
  const accel = (): void => {
    for (let i = 0; i < n; i++) { ax[i] = 0; ay[i] = 0; az[i] = 0 }
    let dyn2 = Infinity
    for (let a = 0; a < k; a++) {
      const i = att[a]
      for (let c = a + 1; c < k; c++) {
        const j = att[c]
        const dx = px[j] - px[i], dy = py[j] - py[i], dz = pz[j] - pz[i]
        const r2 = dx * dx + dy * dy + dz * dz
        const r = Math.sqrt(r2)
        const sum = rad[i] + rad[j]
        const rs = r > sum ? r : sum
        const inv3 = 1 / (rs * rs * rs)
        const gi = G * m[j] * inv3, gj = G * m[i] * inv3
        ax[i] += dx * gi; ay[i] += dy * gi; az[i] += dz * gi
        ax[j] -= dx * gj; ay[j] -= dy * gj; az[j] -= dz * gj
        const mu = G * (m[i] + m[j])
        if (mu > 0) {
          const t2 = (r2 * r) / mu
          if (t2 < dyn2) dyn2 = t2
        }
      }
    }
    for (let i = 0; i < n; i++) {
      const j = host[i]
      if (j < 0) continue
      const dx = px[j] - px[i], dy = py[j] - py[i], dz = pz[j] - pz[i]
      const r = Math.sqrt(dx * dx + dy * dy + dz * dz)
      const sum = rad[i] + rad[j]
      const rs = r > sum ? r : sum
      const g = (G * m[j]) / (rs * rs * rs)
      ax[i] = ax[j] + dx * g; ay[i] = ay[j] + dy * g; az[i] = az[j] + dz * g
    }
    qax = 0; qay = 0; qaz = 0
    for (let i = 0; i < n; i++) {
      const dx = px[i] - qx, dy = py[i] - qy, dz = pz[i] - qz
      const r2 = dx * dx + dy * dy + dz * dz
      const r = Math.sqrt(r2)
      if (r < rad[i] + probe.radius) hitIdx = i
      if (attracts[i]) {
        const rs = r > rad[i] ? r : rad[i]
        const g = (G * m[i]) / (rs * rs * rs)
        qax += dx * g; qay += dy * g; qaz += dz * g
        const mu = G * m[i]
        if (mu > 0) {
          const t2 = (r2 * r) / mu
          if (t2 < dyn2) dyn2 = t2
        }
      }
      const dvx = vx[i] - wx, dvy = vy[i] - wy, dvz = vz[i] - wz
      const v2 = dvx * dvx + dvy * dvy + dvz * dvz
      if (v2 > 0) {
        const c2 = r2 / v2
        if (c2 < dyn2) dyn2 = c2
      }
    }
    dyn = Math.sqrt(dyn2)
    pairEvals += pairsPerStep
  }

  accel()
  const points: Vec3[] = [{ x: qx, y: qy, z: qz }]
  const recordEvery = Math.max(1, Math.floor(maxSteps / maxPoints))
  let t = 0
  let steps = 0
  let closest: Closest | undefined
  let hit: PredictHit | undefined
  while (t < horizon && steps < maxSteps) {
    if (pairEvals + pairsPerStep > budget) { truncated = true; break }
    let h = Math.min(maxSubstep, Math.max(1, dynFraction * dyn))
    if (t + h > horizon) h = horizon - t
    const hh = h / 2
    for (let i = 0; i < n; i++) { vx[i] += ax[i] * hh; vy[i] += ay[i] * hh; vz[i] += az[i] * hh }
    wx += qax * hh; wy += qay * hh; wz += qaz * hh
    for (let i = 0; i < n; i++) { px[i] += vx[i] * h; py[i] += vy[i] * h; pz[i] += vz[i] * h }
    qx += wx * h; qy += wy * h; qz += wz * h
    accel()
    for (let i = 0; i < n; i++) { vx[i] += ax[i] * hh; vy[i] += ay[i] * hh; vz[i] += az[i] * hh }
    wx += qax * hh; wy += qay * hh; wz += qaz * hh
    t += h
    steps++
    if (trackIdx >= 0) {
      const dx = qx - px[trackIdx], dy = qy - py[trackIdx], dz = qz - pz[trackIdx]
      const d = Math.sqrt(dx * dx + dy * dy + dz * dz)
      if (!closest || d < closest.dist) {
        closest = { bodyId: bodies[trackIdx].id, dist: d, time: t, probePos: { x: qx, y: qy, z: qz }, bodyPos: { x: px[trackIdx], y: py[trackIdx], z: pz[trackIdx] } }
      }
    }
    if (steps % recordEvery === 0) points.push({ x: qx, y: qy, z: qz })
    if (hitIdx >= 0) {
      const i = hitIdx
      const dvx = wx - vx[i], dvy = wy - vy[i], dvz = wz - vz[i]
      hit = { bodyId: bodies[i].id, time: t, pos: { x: qx, y: qy, z: qz }, bodyPos: { x: px[i], y: py[i], z: pz[i] }, speed: Math.sqrt(dvx * dvx + dvy * dvy + dvz * dvz) }
      points.push({ x: qx, y: qy, z: qz })
      break
    }
  }
  return { points, hit, closest, duration: t, steps, pairEvals, truncated }
}

export interface AimResult {
  vel: Vec3
  prediction: PredictResult
  converged: boolean
  /** Gravity pair evaluations spent across every iteration. */
  pairEvals: number
}

/**
 * Pair evaluations one auto-aim may spend in total. It runs synchronously on a click, so this is
 * what bounds how long the page can stall: a clear shot needs a few million, and a crowded scene
 * aims as well as the budget allows instead of freezing the page.
 */
export const AIM_PAIR_BUDGET = 4e6

/**
 * Find a launch velocity from `from` that hits the target body with the given speed relative to it.
 * Iteratively corrects the aim direction by the predicted miss (a shooting method).
 */
export function autoAim(u: World, from: Vec3, targetId: string, speed: number, probeRadius = 0.001, iterations = 8, pairBudget = AIM_PAIR_BUDGET): AimResult | undefined {
  const target = u.bodies.find((b) => b.id === targetId)
  if (!target) return undefined
  let dir = normalize(sub(target.pos, from))
  const dist = length(sub(target.pos, from))
  const horizon = Math.min((2.5 * dist) / Math.max(speed, 0.1) + 3600, 5 * 365.25 * 86400)
  let best: AimResult | undefined
  let spent = 0
  let stalls = 0
  for (let iter = 0; iter < iterations; iter++) {
    const vel = add(target.vel, scale(dir, speed))
    const prediction = predictPath(u, { pos: from, vel, radius: probeRadius }, horizon, { trackId: targetId, maxSteps: 4000, pairBudget: pairBudget - spent })
    spent += prediction.pairEvals
    const converged = !!prediction.hit && prediction.hit.bodyId === targetId
    const prevMiss = best?.prediction.closest?.dist ?? Infinity
    if (!best || converged || (prediction.closest && best.prediction.closest && prediction.closest.dist < best.prediction.closest.dist)) {
      best = { vel, prediction, converged, pairEvals: 0 }
    }
    // Out of budget: a path that stops short cannot steer the next shot.
    if (converged || prediction.truncated) break
    // Give up on a shot that has stopped closing in. A small moon whipping round its planet can
    // defeat this correction entirely, and chasing it just spends the budget on the page's time.
    const missNow = prediction.closest?.dist ?? Infinity
    stalls = missNow < prevMiss * 0.8 ? 0 : stalls + 1
    if (stalls >= 2) break
    const c = prediction.closest
    if (!c) break
    const miss = sub(c.probePos, c.bodyPos)
    const L = length(sub(c.bodyPos, from))
    dir = normalize(sub(scale(dir, L), miss))
  }
  if (best) best.pairEvals = spent
  return best
}
