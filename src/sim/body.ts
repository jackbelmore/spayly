import { Vec3, v3 } from './vec'

export type BodyKind = 'star' | 'planet' | 'moon' | 'asteroid' | 'fragment'
export type Surface = 'rock' | 'ice' | 'gas' | 'star' | 'lava'

export interface Rotation {
  /** Pole direction, ICRF right ascension / declination in degrees (IAU). */
  poleRA: number
  poleDec: number
  /** Prime meridian angle at J2000 (deg) and rate (deg/day). Negative rate = retrograde. */
  W0: number
  rate: number
}

/** Exponential atmosphere used by the entry model: surface density (kg/m^3), scale height (m). */
export interface AtmosphereModel {
  rho0: number
  H: number
}

export interface AtmosphereVisual {
  /** Rayleigh scattering coefficients per metre (r,g,b). */
  rayleigh: [number, number, number]
  /** Mie scattering coefficient per metre. */
  mie: number
  rayleighH: number // m
  mieH: number // m
  /** Shell thickness as a fraction of the radius. */
  thickness: number
  g: number
  /** Overall multiplier for artistic control. */
  strength: number
}

export interface RingVisual {
  inner: number // km
  outer: number // km
  texture?: string
}

export interface BodyVisual {
  color: string
  texture?: string
  night?: string
  clouds?: string
  normal?: string
  specular?: string
  ring?: RingVisual
  /** The map has blank, never-imaged regions; grow the imaged parts over them at load. */
  fillGaps?: boolean
  /** Multiply the map by this colour at load; for bodies that borrow another body's map. */
  tint?: string
  /** Rotate the map in longitude by this fraction of a turn at load, for the same reason. */
  roll?: number
  atmosphere?: AtmosphereVisual
  /** Bond albedo for equilibrium temperature. */
  albedo: number
  /** Baseline mean surface temperature (K) used for the climate cue. */
  baselineTemp?: number
}

export interface Crater {
  /** Unit direction in the body frame. */
  dir: Vec3
  diameterKm: number
  time: number
  energy: number
}

export interface HeatSpot {
  dir: Vec3
  angularRadius: number
  time: number
  /** Cooling timescale, seconds. */
  tau: number
}

export interface DamageState {
  craters: Crater[]
  heat: HeatSpot[]
  /** 0..1 magma glow (global), decaying over magmaTau seconds from magmaTime. */
  magma: number
  magmaTime: number
  magmaTau: number
  /** 0..1 atmospheric dust load. */
  dust: number
  dustTime: number
  dustTau: number
  hits: number
  energy: number
  absorbed: number
  /** Incremented whenever the crater list changes so views can repaint. */
  version: number
}

export interface Body {
  id: string
  name: string
  kind: BodyKind
  surface: Surface
  mass: number // kg
  radius: number // km
  mass0: number
  radius0: number
  pos: Vec3 // km, ecliptic J2000
  vel: Vec3 // km/s
  acc: Vec3
  primaryId?: string
  rotation?: Rotation
  atmosphere?: AtmosphereModel
  visual: BodyVisual
  damage: DamageState
  /** Random seed for procedural shapes. */
  seed: number
  /** Simulation time at which the body was created. */
  born: number
}

export const emptyDamage = (): DamageState => ({
  craters: [], heat: [], magma: 0, magmaTime: 0, magmaTau: 1, dust: 0, dustTime: 0, dustTau: 1,
  hits: 0, energy: 0, absorbed: 0, version: 0,
})

export interface BodySpec {
  id: string
  name: string
  kind: BodyKind
  surface: Surface
  mass: number
  radius: number
  pos?: Vec3
  vel?: Vec3
  primaryId?: string
  rotation?: Rotation
  atmosphere?: AtmosphereModel
  visual: BodyVisual
  seed?: number
  born?: number
}

export function makeBody(spec: BodySpec): Body {
  return {
    id: spec.id,
    name: spec.name,
    kind: spec.kind,
    surface: spec.surface,
    mass: spec.mass,
    radius: spec.radius,
    mass0: spec.mass,
    radius0: spec.radius,
    pos: spec.pos ?? v3(),
    vel: spec.vel ?? v3(),
    acc: v3(),
    primaryId: spec.primaryId,
    rotation: spec.rotation,
    atmosphere: spec.atmosphere,
    visual: spec.visual,
    damage: emptyDamage(),
    seed: spec.seed ?? Math.floor(Math.random() * 1e9),
    born: spec.born ?? 0,
  }
}

const G_SI = 6.6743e-11
export const density = (b: Body): number => b.mass / ((4 / 3) * Math.PI * Math.pow(b.radius * 1000, 3)) // kg/m^3
export const surfaceGravity = (b: Body): number => (G_SI * b.mass) / Math.pow(b.radius * 1000, 2) // m/s^2
export const escapeVelocity = (b: Body): number => Math.sqrt((2 * G_SI * b.mass) / (b.radius * 1000)) / 1000 // km/s
/** Gravitational binding energy of a uniform sphere, J. */
export const bindingEnergy = (b: Body): number => (3 * G_SI * b.mass * b.mass) / (5 * b.radius * 1000)
/** Radius (km) a body of this mass would have at the given density (kg/m^3). */
export const radiusForDensity = (mass: number, rho: number): number =>
  Math.cbrt((3 * mass) / (4 * Math.PI * rho)) / 1000
