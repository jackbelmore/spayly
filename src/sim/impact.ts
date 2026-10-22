/**
 * Impact consequences after Collins, Melosh & Marcus (2005), "Earth Impact Effects Program",
 * Meteoritics & Planetary Science 40, 817-840. SI units throughout (m, kg, s, J).
 */
import { KT_TNT, MT_TNT } from './constants'

export interface ImpactorSpec {
  diameter: number // m
  density: number // kg/m^3
  speed: number // m/s at the top of the atmosphere
  angle: number // radians from horizontal
}

export interface TargetSpec {
  radius: number // m
  mass: number // kg
  gravity: number // m/s^2
  surface: 'rock' | 'water' | 'ice' | 'gas' | 'star'
  surfaceDensity: number // kg/m^3
  waterDepth?: number // m, for water targets
  atmosphere?: { rho0: number; H: number }
}

export interface EntryResult {
  breakup: boolean
  breakupAltitude: number // m
  airburst: boolean
  burstAltitude: number // m
  surfaceSpeed: number // m/s (0 if airburst)
  burstSpeed: number // m/s at the airburst point
}

export interface CraterResult {
  transientDiameter: number
  finalDiameter: number
  depth: number
  complex: boolean
}

export interface ImpactReport {
  mass: number
  energy: number // kinetic energy at entry, J
  energyMT: number
  energyDeposited: number // energy at the ground or burst point, J
  entry: EntryResult
  crater?: CraterResult
  fireballRadius: number
  thermal: { burnRadius: number; ignitionRadius: number; horizonLimited: boolean }
  seismicMagnitude: number
  seismicDamageRadius: number // m, where effective magnitude >= 6
  ejectaOneMetreRadius: number
  airblast: { glassRadius: number; collapseRadius: number }
  tsunami?: { amplitude100km: number; amplitude1000km: number }
  bindingRatio: number // deposited energy / gravitational binding energy of the target
  /** The crater scaling no longer applies: the event reshapes the whole body. */
  planetScale: boolean
  classification: string
  comparisons: string[]
}

const CD = 2
const FP = 7
const LUMINOUS_EFFICIENCY = 3e-3

export const yieldStrength = (rho: number): number => Math.pow(10, 2.107 + 0.0624 * Math.sqrt(rho))

/** Pancake-model atmospheric entry. */
export function atmosphericEntry(imp: ImpactorSpec, tgt: TargetSpec): EntryResult {
  const none: EntryResult = { breakup: false, breakupAltitude: 0, airburst: false, burstAltitude: 0, surfaceSpeed: imp.speed, burstSpeed: 0 }
  if (!tgt.atmosphere) return none
  const { rho0, H } = tgt.atmosphere
  const L0 = imp.diameter
  const rhoI = imp.density
  const v0 = imp.speed
  const sinT = Math.max(Math.sin(imp.angle), 0.05)
  const Yi = yieldStrength(rhoI)
  const If = (4.07 * CD * H * Yi) / (rhoI * L0 * v0 * v0 * sinT)
  const intactSurfaceSpeed = v0 * Math.exp((-3 * rho0 * CD * H) / (4 * rhoI * L0 * sinT))
  if (If >= 1) return { ...none, surfaceSpeed: intactSurfaceSpeed }
  const zStar = -H * (Math.log(Yi / (rho0 * v0 * v0)) + 1.308 - 0.314 * If - 1.303 * Math.sqrt(1 - If))
  if (!(zStar > 0)) return { ...none, surfaceSpeed: intactSurfaceSpeed }
  const rhoZ = rho0 * Math.exp(-zStar / H)
  const vZ = v0 * Math.exp((-3 * rhoZ * CD * H) / (4 * rhoI * L0 * sinT))
  const l = L0 * sinT * Math.sqrt(rhoI / (CD * rhoZ))
  const zb = zStar - 2 * H * Math.log(1 + (l / (2 * H)) * Math.sqrt(FP * FP - 1))
  const zEnd = Math.max(zb, 0)
  // Integrate dv/dz for the spreading fragment cloud from z* down to the burst altitude or the ground.
  const N = 240
  const dz = (zStar - zEnd) / N
  let integral = 0
  for (let k = 0; k < N; k++) {
    const z = zStar - (k + 0.5) * dz
    const spread = (2 * H) / l
    const L = L0 * Math.sqrt(1 + spread * spread * Math.pow(Math.exp((zStar - z) / (2 * H)) - 1, 2))
    const rho = rho0 * Math.exp(-z / H)
    integral += ((3 * CD * rho * L * L) / (4 * rhoI * L0 * L0 * L0 * sinT)) * dz
  }
  const vEnd = vZ * Math.exp(-integral)
  if (zb > 0) return { breakup: true, breakupAltitude: zStar, airburst: true, burstAltitude: zb, surfaceSpeed: 0, burstSpeed: vEnd }
  return { breakup: true, breakupAltitude: zStar, airburst: false, burstAltitude: 0, surfaceSpeed: vEnd, burstSpeed: 0 }
}

/** Pi-group crater scaling. Inputs in SI. */
export function craterDimensions(diameter: number, rhoI: number, speed: number, angle: number, tgt: TargetSpec): CraterResult {
  const k = tgt.surface === 'water' ? 1.365 : 1.161
  const rhoT = tgt.surfaceDensity
  const g = tgt.gravity
  const Dtc = k * Math.cbrt(rhoI / rhoT) * Math.pow(diameter, 0.78) * Math.pow(speed, 0.44) * Math.pow(g, -0.22) * Math.cbrt(Math.max(Math.sin(angle), 0.05))
  const Dc = 3200 * (9.81 / g) // simple-to-complex transition scales inversely with gravity
  if (Dtc < Dc) {
    const Dfr = 1.25 * Dtc
    return { transientDiameter: Dtc, finalDiameter: Dfr, depth: 0.2 * Dfr, complex: false }
  }
  const Dfr = (1.17 * Math.pow(Dtc, 1.13)) / Math.pow(Dc, 0.13)
  return { transientDiameter: Dtc, finalDiameter: Dfr, depth: 400 * Math.pow(Dfr / 1000, 0.3), complex: true }
}

/**
 * Peak overpressure (Pa) at ground distance r from a burst of energy E (J).
 * Surface bursts use the scaled-distance fit; airbursts use the regular-reflection form
 * with the burst altitude folded into the scaled slant range.
 */
export function overpressure(r: number, E: number, burstAltitude = 0): number {
  const Ekt = Math.max(E / KT_TNT, 1e-9)
  const s = Math.cbrt(Ekt)
  const r1 = r / s
  if (burstAltitude > 0) {
    const zb1 = burstAltitude / s
    const d2 = r1 * r1 + zb1 * zb1
    return 3.14e11 * Math.pow(d2, -1.3) + 1.8e7 * Math.pow(d2, -0.565)
  }
  const rx = 290
  return 75000 * (rx / (4 * r1)) * (1 + 3 * Math.pow(rx / r1, 1.3))
}

function radiusForOverpressure(target: number, E: number, burstAltitude: number): number {
  let lo = 1, hi = 1e8
  if (overpressure(lo, E, burstAltitude) < target) return 0
  for (let i = 0; i < 60; i++) {
    const mid = Math.sqrt(lo * hi)
    if (overpressure(mid, E, burstAltitude) > target) lo = mid
    else hi = mid
  }
  return lo
}

/** Effective seismic magnitude at distance r (m) for source magnitude M. */
export function effectiveMagnitude(M: number, r: number): number {
  const rkm = r / 1000
  if (rkm < 60) return M - 0.0238 * rkm
  if (rkm < 700) return M - 0.0048 * rkm - 1.1644
  return M - 1.66 * Math.log10(rkm) - 6.399
}

function seismicRadius(M: number, threshold: number): number {
  if (M < threshold) return 0
  let lo = 1, hi = 2e7
  for (let i = 0; i < 60; i++) {
    const mid = Math.sqrt(lo * hi)
    if (effectiveMagnitude(M, mid) > threshold) lo = mid
    else hi = mid
  }
  return lo
}

export function classify(energy: number, bindingRatio: number, entry: EntryResult, tgt: TargetSpec): string {
  if (tgt.surface === 'star') return 'Swallowed by the star. A brief brightening, nothing more.'
  if (bindingRatio >= 1) return 'Catastrophic disruption. The body is torn apart and its pieces scatter.'
  if (bindingRatio >= 0.2) return 'Shattering impact. The body survives as a molten, reassembling ball of debris.'
  if (bindingRatio >= 0.01) return 'Global melting. The surface becomes a magma ocean.'
  if (tgt.surface === 'gas') {
    if (energy > 1e24) return 'Titanic plume. A dark scar the size of Earth spreads through the cloud bands.'
    return 'Plume and fireball in the upper atmosphere, leaving a dark scar like Shoemaker-Levy 9.'
  }
  if (energy > 4e27) return 'Sterilising impact. The oceans boil away and the crust glows.'
  if (energy > 1e23) return 'Mass extinction event. Global firestorm, impact winter, ecosystems collapse.'
  if (energy > 1e21) return 'Global catastrophe. Years of impact winter and crop failure worldwide.'
  if (energy > 1e18) return 'Continental devastation. A crater visible from space and a regional dust cloud.'
  if (energy > 1e16) return entry.airburst ? 'City-killer airburst. Buildings flattened over a wide area.' : 'Regional devastation with a large crater.'
  if (energy > 1e14) return entry.airburst ? 'Powerful airburst. Windows shattered across a city.' : 'Local devastation with a crater.'
  if (energy > 1e12) return entry.airburst ? 'Bright fireball and sonic boom. Meteorites reach the ground.' : 'Small crater; local damage.'
  return 'Fireball. A spectacular meteor with little damage.'
}

export function comparisons(energy: number): string[] {
  const out: string[] = []
  const hiroshima = 6.3e13
  const tsar = 2.1e17
  if (energy < hiroshima * 0.5) out.push(`${fmt(energy / 4.184e9)} tons of TNT`)
  else if (energy < tsar * 2) out.push(`${fmt(energy / hiroshima)} Hiroshima bombs`)
  else out.push(`${fmt(energy / tsar)} Tsar Bombas`)
  if (energy > 2e15) out.push(`${fmt(energy / 2.1e15)} Chelyabinsk events`)
  if (energy > 1e21) out.push(`${fmt(energy / 4e23)} Chicxulub impacts`)
  return out
}

const fmt = (x: number): string => {
  if (x >= 1e6) return x.toExponential(1).replace('e+', 'e')
  if (x >= 100) return Math.round(x).toLocaleString()
  if (x >= 10) return x.toFixed(1)
  return x.toFixed(2)
}

export function impactReport(imp: ImpactorSpec, tgt: TargetSpec): ImpactReport {
  const mass = imp.density * (Math.PI / 6) * Math.pow(imp.diameter, 3)
  const energy = 0.5 * mass * imp.speed * imp.speed
  const entry = tgt.surface === 'gas' || tgt.surface === 'star'
    ? { breakup: true, breakupAltitude: 0, airburst: true, burstAltitude: 0, surfaceSpeed: 0, burstSpeed: imp.speed }
    : atmosphericEntry(imp, tgt)
  const speedAtEnd = entry.airburst ? entry.burstSpeed : entry.surfaceSpeed
  const energyDeposited = 0.5 * mass * speedAtEnd * speedAtEnd
  const bindingEnergy = (3 * 6.6743e-11 * tgt.mass * tgt.mass) / (5 * tgt.radius)
  const bindingRatio = energy / bindingEnergy

  let crater: CraterResult | undefined
  if (!entry.airburst && tgt.surface !== 'gas' && tgt.surface !== 'star') {
    crater = craterDimensions(imp.diameter, imp.density, entry.surfaceSpeed, imp.angle, tgt)
  }

  const fireballRadius = 0.002 * Math.cbrt(energyDeposited)
  const Emt = energyDeposited / MT_TNT
  const scaling = Math.pow(Math.max(Emt, 1e-12), 1 / 6)
  const burnFlux = 0.42e6 * scaling
  const igniteFlux = 1.0e6 * scaling
  const thermalRadius = (flux: number) => Math.sqrt((LUMINOUS_EFFICIENCY * energyDeposited) / (2 * Math.PI * flux))
  const horizon = Math.sqrt(Math.max(0, (tgt.radius + fireballRadius + entry.burstAltitude) ** 2 - tgt.radius ** 2))
  const burnRaw = thermalRadius(burnFlux)
  const thermal = {
    burnRadius: Math.min(burnRaw, horizon),
    ignitionRadius: Math.min(thermalRadius(igniteFlux), horizon),
    horizonLimited: burnRaw > horizon,
  }

  const half = Math.PI * tgt.radius // antipode distance: nothing on the surface is farther away
  const seismicMagnitude = crater ? 0.67 * Math.log10(Math.max(energyDeposited, 1)) - 5.87 : -Infinity
  const seismicDamageRadius = crater ? Math.min(seismicRadius(seismicMagnitude, 6), half) : 0
  const ejectaOneMetreRadius = crater ? Math.min(Math.cbrt(Math.pow(crater.transientDiameter, 4) / 112), half) : 0
  const airblast = tgt.atmosphere
    ? { glassRadius: Math.min(radiusForOverpressure(1000, energyDeposited, entry.burstAltitude), half), collapseRadius: Math.min(radiusForOverpressure(28000, energyDeposited, entry.burstAltitude), half) }
    : { glassRadius: 0, collapseRadius: 0 }

  let tsunami: ImpactReport['tsunami']
  if (crater && tgt.surface === 'water') {
    const depth = tgt.waterDepth ?? 3700
    const dtc = crater.transientDiameter / 2.828
    const amp = (r: number) => Math.min(dtc, depth) * (crater!.transientDiameter / (2 * r))
    tsunami = { amplitude100km: amp(1e5), amplitude1000km: amp(1e6) }
  }

  const planetScale = (crater ? crater.finalDiameter > 0.6 * tgt.radius : false) || fireballRadius > tgt.radius
  return {
    mass, energy, energyMT: energy / MT_TNT, energyDeposited, entry, crater, fireballRadius, thermal,
    seismicMagnitude, seismicDamageRadius, ejectaOneMetreRadius, airblast, tsunami, bindingRatio, planetScale,
    classification: classify(energy, bindingRatio, entry, tgt),
    comparisons: comparisons(energy),
  }
}
