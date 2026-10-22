import { Body, bindingEnergy, density, escapeVelocity, makeBody, radiusForDensity, surfaceGravity } from './body'
import { DAY, HOUR, YEAR } from './constants'
import { ImpactReport, impactReport, TargetSpec } from './impact'
import { Universe, CollisionEvent } from './universe'
import { Vec3, add, sub, scale, normalize, dot, length, clamp, randomUnit, makeRandom, cross } from './vec'

export type OutcomeKind = 'absorb' | 'crater' | 'airburst' | 'gas' | 'merge' | 'shatter' | 'disperse'

export interface ImpactOutcome {
  kind: OutcomeKind
  target: Body
  impactor: Body
  /** Contact point, world km. */
  contactWorld: Vec3
  /** Outward unit normal at the contact point, world frame. */
  normalWorld: Vec3
  /** Same normal in the target's body frame (rotates with the surface). */
  dirBody: Vec3
  /** Unit direction of the impactor's velocity relative to the target, world frame. */
  velDirWorld: Vec3
  relSpeed: number // km/s
  angle: number // radians from horizontal
  energy: number // J, centre-of-mass frame
  /** Change of the target's velocity from the momentum exchange, km/s. */
  targetDeltaV: number
  report?: ImpactReport
  fragments: Body[]
  time: number
}

export interface CollideOptions {
  /** Converts a world direction into the body's rotating frame. */
  bodyFrame: (b: Body, dirWorld: Vec3) => Vec3
  /** Tells whether a body-frame direction points at water (Earth's oceans). */
  surfaceAt?: (b: Body, dirBody: Vec3) => { water: boolean; depth: number }
}

const clampTau = (s: number, lo: number, hi: number) => clamp(s, lo, hi)

/**
 * Energy that catastrophically disrupts a body, J.
 *
 * Self-gravity sets the scale above a few kilometres — a fifth of the gravitational binding energy,
 * which is where the shatter threshold has always sat here. Below that, material strength dominates:
 * the Benz & Asphaug (1999) basalt strength term (Q*_D = 3.5e7 R_cm^-0.38 erg/g, converted to SI)
 * stops a boulder from being blown apart by a gentle nudge, which self-gravity alone would allow.
 */
export function disruptionEnergy(b: Body): number {
  const rm = Math.max(b.radius * 1000, 1)
  const strength = 608 * Math.pow(rm, -0.38) // J/kg
  return 0.2 * bindingEnergy(b) + strength * b.mass
}

function mergeInto(target: Body, impactor: Body): void {
  const mTot = target.mass + impactor.mass
  const rhoT = density(target)
  const rhoI = density(impactor)
  target.vel = scale(add(scale(target.vel, target.mass), scale(impactor.vel, impactor.mass)), 1 / mTot)
  target.pos = scale(add(scale(target.pos, target.mass), scale(impactor.pos, impactor.mass)), 1 / mTot)
  const volume = target.mass / rhoT + impactor.mass / rhoI // m^3
  target.mass = mTot
  if (target.kind !== 'star') target.radius = Math.cbrt((3 * volume) / (4 * Math.PI)) / 1000
  target.mass0 = target.mass
  target.radius0 = target.radius
}

function decayed(value: number, since: number, tau: number, now: number): number {
  return value * Math.exp(-Math.max(0, now - since) / tau)
}

/** Resolve one overlapping pair: crater, airburst, merge or fragmentation. Records an undo snapshot first. */
export function resolveCollision(u: Universe, ev: CollisionEvent, opts: CollideOptions): ImpactOutcome {
  const targetIsA = ev.a.mass >= ev.b.mass
  const [target, impactor] = targetIsA ? [ev.a, ev.b] : [ev.b, ev.a]
  u.pushHistory(`before ${impactor.name} hit ${target.name}`)
  const time = ev.time
  const rel = sub(impactor.vel, target.vel)
  const relSpeed = length(rel)
  // Contact geometry comes from the closest approach inside the substep, which for a fast small
  // impactor is nowhere near where the pair ended up at the end of it.
  const contactA = (targetIsA ? ev.posA : ev.posB) ?? target.pos
  const contactB = (targetIsA ? ev.posB : ev.posA) ?? impactor.pos
  const normalWorld = normalize(sub(contactB, contactA))
  const vin = relSpeed > 0 ? scale(rel, 1 / relSpeed) : scale(normalWorld, -1)
  const angle = Math.asin(clamp(Math.abs(dot(vin, normalWorld)), 0, 1))
  const contactWorld = add(target.pos, scale(normalWorld, target.radius))
  const dirBody = opts.bodyFrame(target, normalWorld)
  const reduced = (target.mass * impactor.mass) / (target.mass + impactor.mass)
  const energy = 0.5 * reduced * Math.pow(relSpeed * 1000, 2)
  const vBefore = { ...target.vel }
  const base: ImpactOutcome = { kind: 'absorb', target, impactor, contactWorld, normalWorld, dirBody, velDirWorld: vin, relSpeed, angle, energy, targetDeltaV: 0, fragments: [], time }
  const dmg = target.damage
  dmg.hits++
  dmg.energy += energy

  if (target.kind === 'star') {
    mergeInto(target, impactor)
    u.remove(impactor)
    base.targetDeltaV = length(sub(target.vel, vBefore))
    return base
  }

  // 1 = catastrophic disruption; the old crater/shatter boundary was a fifth of the binding energy,
  // so a severity of 1 here is the same event for a planet-sized target.
  const severity = energy / disruptionEnergy(target)
  if (severity >= 1 && u.roomForBodies >= 2) {
    const out = fragment(u, base, severity)
    out.targetDeltaV = length(sub(target.vel, vBefore))
    return out
  }
  if (severity >= 1) {
    // Out of room for new bodies: the target still melts, it just does not scatter.
    base.kind = 'merge'
    mergeInto(target, impactor)
    target.surface = 'lava'
    dmg.craters = []
    dmg.heat = []
    dmg.magma = 1
    dmg.magmaTime = time
    dmg.magmaTau = clampTau(30 * YEAR * Math.sqrt(0.2 * severity), YEAR, 100 * YEAR)
    dmg.version++
    u.remove(impactor)
    base.targetDeltaV = length(sub(target.vel, vBefore))
    return base
  }

  const surf = opts.surfaceAt?.(target, dirBody)
  const surface: TargetSpec['surface'] = target.surface === 'gas' ? 'gas' : surf?.water ? 'water' : target.surface === 'ice' ? 'ice' : 'rock'
  const tgt: TargetSpec = {
    radius: target.radius * 1000,
    mass: target.mass,
    gravity: surfaceGravity(target),
    surface,
    surfaceDensity: surface === 'water' ? 1000 : surface === 'ice' ? 900 : 2500,
    waterDepth: surf?.depth,
    atmosphere: target.atmosphere,
  }
  const report = impactReport({ diameter: impactor.radius * 2000, density: density(impactor), speed: relSpeed * 1000, angle }, tgt)
  base.report = report

  const now = time
  const R = target.radius * 1000
  if (target.surface === 'gas') {
    base.kind = 'gas'
    const scarRadius = Math.max(report.fireballRadius * 6, R * 0.01)
    dmg.heat.push({ dir: dirBody, angularRadius: scarRadius / R, time: now, tau: clampTau(30 * DAY * Math.cbrt(energy / 1e23), 2 * DAY, 3 * YEAR) })
    dmg.craters.push({ dir: dirBody, diameterKm: (scarRadius * 2) / 1000, time: now, energy })
    dmg.version++
  } else if (report.entry.airburst) {
    base.kind = 'airburst'
    dmg.heat.push({ dir: dirBody, angularRadius: Math.max(report.fireballRadius, 2000) / R, time: now, tau: 20 * 60 })
  } else if (report.crater) {
    base.kind = impactor.mass > 0.02 * target.mass ? 'merge' : 'crater'
    const D = report.crater.finalDiameter
    dmg.craters.push({ dir: dirBody, diameterKm: D / 1000, time: now, energy })
    dmg.version++
    const Dkm = D / 1000
    dmg.heat.push({ dir: dirBody, angularRadius: Math.max((D / 2) * 1.6, 1500) / R, time: now, tau: clampTau(Math.pow(Dkm, 1.5) * HOUR, 10 * 60, 50 * YEAR) })
    if (target.atmosphere && energy > 1e18) {
      const dust = clamp(0.25 * Math.log10(energy / 1e18), 0, 1)
      const prev = decayed(dmg.dust, dmg.dustTime, dmg.dustTau, now)
      if (dust > prev) {
        dmg.dust = dust
        dmg.dustTime = now
        dmg.dustTau = clampTau(YEAR * Math.pow(energy / 1e21, 0.3), 30 * DAY, 30 * YEAR)
      }
    }
  }
  if (severity > 0.015) {
    const magma = clamp(Math.log10(severity / 0.015) / Math.log10(1 / 0.015), 0, 1)
    const prev = decayed(dmg.magma, dmg.magmaTime, dmg.magmaTau, now)
    if (magma > prev) {
      dmg.magma = magma
      dmg.magmaTime = now
      dmg.magmaTau = clampTau(30 * YEAR * Math.sqrt(severity), YEAR, 30 * YEAR)
    }
  }
  if (dmg.heat.length > 12) dmg.heat.splice(0, dmg.heat.length - 12)
  mergeInto(target, impactor)
  u.remove(impactor)
  base.targetDeltaV = length(sub(target.vel, vBefore))
  return base
}

/** Break the target apart. `severity` is the impact energy over the disruption energy (1 = catastrophic). */
function fragment(u: Universe, out: ImpactOutcome, severity: number): ImpactOutcome {
  const { target, impactor, time } = out
  const mTot = target.mass + impactor.mass
  const rho = density(target)
  const fLR = clamp(1 - 0.1 * severity, 0.05, 0.9)
  const remnantMass = fLR * mTot
  const fragMass = mTot - remnantMass
  const n = Math.min(clamp(Math.round(6 + 6 * Math.log10(1 + 2 * severity)), 6, 18), u.roomForBodies)
  const vCom = scale(add(scale(target.vel, target.mass), scale(impactor.vel, impactor.mass)), 1 / mTot)
  const pCom = scale(add(scale(target.pos, target.mass), scale(impactor.pos, impactor.mass)), 1 / mTot)
  const vEsc = escapeVelocity(target)
  const rand = makeRandom((target.seed ^ Math.floor(time)) >>> 0)
  const impactDir = normalize(sub(impactor.vel, target.vel))
  const weights: number[] = []
  let wSum = 0
  for (let k = 0; k < n; k++) {
    const w = Math.pow(k + 1, -1.2)
    weights.push(w)
    wSum += w
  }
  const shell = target.radius * 1.6
  const placed: Array<{ pos: Vec3; r: number }> = []
  const fragments: Body[] = []
  let momentum: Vec3 = { x: 0, y: 0, z: 0 }
  const spread = 0.6 + 0.6 * Math.sqrt(Math.min(0.2 * severity, 3))
  for (let k = 0; k < n; k++) {
    const mass = (fragMass * weights[k]) / wSum
    const radius = radiusForDensity(mass, rho)
    let dir = randomUnit(rand)
    for (let tries = 0; tries < 24; tries++) {
      const candidate = add(pCom, scale(dir, shell + radius))
      if (placed.every((p) => length(sub(p.pos, candidate)) > 1.2 * (p.r + radius))) break
      dir = randomUnit(rand)
    }
    const pos = add(pCom, scale(dir, shell + radius))
    const radial = scale(dir, vEsc * (0.4 + 0.9 * rand()) * spread)
    const tangential = scale(normalize(cross(dir, impactDir)), vEsc * 0.15 * rand())
    const dv = add(radial, tangential)
    const vel = add(vCom, dv)
    momentum = add(momentum, scale(dv, mass))
    placed.push({ pos, r: radius })
    const body = makeBody({
      id: `${target.id}-frag-${Math.floor(time)}-${k}`,
      name: `${target.name} fragment ${k + 1}`,
      kind: 'fragment',
      surface: 'lava',
      mass,
      radius,
      pos,
      vel,
      primaryId: target.primaryId,
      visual: { color: target.visual.color, albedo: 0.1 },
      seed: Math.floor(rand() * 1e9),
      born: time,
    })
    body.damage.magma = 1
    body.damage.magmaTime = time
    body.damage.magmaTau = clamp(DAY * Math.pow(radius / 10, 1.5), HOUR, 30 * YEAR)
    fragments.push(body)
  }
  // The remnant carries the recoil so total momentum is conserved exactly.
  target.mass = remnantMass
  target.radius = radiusForDensity(remnantMass, rho)
  target.mass0 = target.mass
  target.radius0 = target.radius
  target.pos = pCom
  target.vel = sub(vCom, scale(momentum, 1 / remnantMass))
  target.surface = 'lava'
  target.damage.craters = []
  target.damage.heat = []
  target.damage.magma = 1
  target.damage.magmaTime = time
  target.damage.magmaTau = clamp(30 * YEAR * Math.sqrt(0.2 * severity), YEAR, 100 * YEAR)
  target.damage.version++
  u.remove(impactor)
  for (const f of fragments) u.add(f)
  out.kind = fLR >= 0.3 ? 'shatter' : 'disperse'
  out.fragments = fragments
  return out
}
