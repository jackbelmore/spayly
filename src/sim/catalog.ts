import { DEG, G, J2000_JD, SUN_MASS, SUN_RADIUS, YEAR } from './constants'
import { Body, BodySpec, makeBody, AtmosphereVisual, AtmosphereModel, Rotation } from './body'
import { elementsToState } from './kepler'
import { centuriesSinceJ2000, julianDay, moonElementsAt, planetElementsAt } from './ephemeris'
import { eclipticToPole, fromBasis, planeBasis, poleToEcliptic } from './frames'
import { SATELLITES, MINOR_BODIES } from './satellites'
import { SatelliteElements, MinorBodyElements } from './satelliteTypes'
import { add, cross, dot, normalize, scale, sub, v3, Vec3 } from './vec'

const EARTH_ATMO: AtmosphereVisual = {
  rayleigh: [5.8e-6, 13.5e-6, 33.1e-6], mie: 8e-6, rayleighH: 8000, mieH: 1200, thickness: 0.028, g: 0.76, strength: 1.0,
}
const MARS_ATMO: AtmosphereVisual = {
  rayleigh: [4e-8, 7e-8, 1.5e-7], mie: 1.6e-6, rayleighH: 11100, mieH: 9000, thickness: 0.025, g: 0.65, strength: 1.0,
}
const VENUS_ATMO: AtmosphereVisual = {
  rayleigh: [2e-6, 2.5e-6, 3.5e-6], mie: 2.5e-5, rayleighH: 15900, mieH: 9000, thickness: 0.035, g: 0.7, strength: 0.55,
}
const JUPITER_ATMO: AtmosphereVisual = {
  rayleigh: [1.2e-7, 1.8e-7, 3.2e-7], mie: 4e-7, rayleighH: 27000, mieH: 20000, thickness: 0.012, g: 0.7, strength: 1.0,
}
const SATURN_ATMO: AtmosphereVisual = {
  rayleigh: [1.1e-7, 1.5e-7, 2.4e-7], mie: 4e-7, rayleighH: 59500, mieH: 40000, thickness: 0.014, g: 0.7, strength: 0.9,
}
const ICE_GIANT_ATMO: AtmosphereVisual = {
  rayleigh: [1.0e-7, 2.2e-7, 4.5e-7], mie: 3e-7, rayleighH: 27700, mieH: 20000, thickness: 0.016, g: 0.7, strength: 1.0,
}
/**
 * Titan's tholin haze. The shader's Rayleigh term is the only colour knob for in-scattered light, so
 * these coefficients run red-heavy rather than blue-heavy: real tholins scatter and absorb their way
 * to orange, which a pure Rayleigh model cannot reach. Extinction is kept mild so the dune fields
 * still show through near the sub-solar point, the way Cassini's infrared maps see them.
 */
const TITAN_ATMO: AtmosphereVisual = {
  rayleigh: [2.6e-6, 1.45e-6, 0.55e-6], mie: 5e-6, rayleighH: 30000, mieH: 40000, thickness: 0.085, g: 0.35, strength: 2.4,
}

/** Static physical data. Masses from JPL GM values, radii mean volumetric (km), rotation from IAU. */
export const CATALOG: Record<string, Omit<BodySpec, 'pos' | 'vel'>> = {
  sun: {
    id: 'sun', name: 'Sun', kind: 'star', surface: 'star', mass: SUN_MASS, radius: SUN_RADIUS,
    rotation: { poleRA: 286.13, poleDec: 63.87, W0: 84.176, rate: 14.1844 },
    visual: { color: '#ffd27a', texture: 'sun.jpg', albedo: 0 },
  },
  mercury: {
    id: 'mercury', name: 'Mercury', kind: 'planet', surface: 'rock', mass: 3.3011e23, radius: 2439.7, primaryId: 'sun',
    rotation: { poleRA: 281.0103, poleDec: 61.4155, W0: 329.5988, rate: 6.1385108 },
    visual: { color: '#b8b0a8', texture: 'mercury.jpg', albedo: 0.088, baselineTemp: 440 },
  },
  venus: {
    id: 'venus', name: 'Venus', kind: 'planet', surface: 'rock', mass: 4.8673e24, radius: 6051.8, primaryId: 'sun',
    rotation: { poleRA: 272.76, poleDec: 67.16, W0: 160.2, rate: -1.4813688 },
    atmosphere: { rho0: 65, H: 15900 },
    visual: { color: '#e8c98a', texture: 'venus_atmosphere.jpg', albedo: 0.76, baselineTemp: 737, atmosphere: VENUS_ATMO },
  },
  earth: {
    id: 'earth', name: 'Earth', kind: 'planet', surface: 'rock', mass: 5.9722e24, radius: 6371.0, primaryId: 'sun',
    rotation: { poleRA: 0, poleDec: 90, W0: 190.147, rate: 360.9856235 },
    atmosphere: { rho0: 1.225, H: 8000 },
    visual: {
      color: '#6fa8ff', texture: 'earth_day.jpg', night: 'earth_night.jpg', clouds: 'earth_clouds.jpg',
      normal: 'earth_normal.jpg', specular: 'earth_specular.jpg', albedo: 0.306, baselineTemp: 288, atmosphere: EARTH_ATMO,
    },
  },
  mars: {
    id: 'mars', name: 'Mars', kind: 'planet', surface: 'rock', mass: 6.4169e23, radius: 3389.5, primaryId: 'sun',
    rotation: { poleRA: 317.68143, poleDec: 52.8865, W0: 176.63, rate: 350.89198226 },
    atmosphere: { rho0: 0.02, H: 11100 },
    visual: { color: '#ff7a4d', texture: 'mars.jpg', albedo: 0.25, baselineTemp: 210, atmosphere: MARS_ATMO },
  },
  jupiter: {
    id: 'jupiter', name: 'Jupiter', kind: 'planet', surface: 'gas', mass: 1.89819e27, radius: 69911, primaryId: 'sun',
    rotation: { poleRA: 268.056595, poleDec: 64.495303, W0: 284.95, rate: 870.536 },
    atmosphere: { rho0: 0.16, H: 27000 },
    visual: { color: '#d9b48f', texture: 'jupiter.jpg', albedo: 0.503, atmosphere: JUPITER_ATMO },
  },
  saturn: {
    id: 'saturn', name: 'Saturn', kind: 'planet', surface: 'gas', mass: 5.6832e26, radius: 58232, primaryId: 'sun',
    rotation: { poleRA: 40.589, poleDec: 83.537, W0: 38.9, rate: 810.7939024 },
    atmosphere: { rho0: 0.19, H: 59500 },
    visual: { color: '#f0d9a0', texture: 'saturn.jpg', albedo: 0.342, atmosphere: SATURN_ATMO, ring: { inner: 74500, outer: 140220 } },
  },
  uranus: {
    id: 'uranus', name: 'Uranus', kind: 'planet', surface: 'gas', mass: 8.6811e25, radius: 25362, primaryId: 'sun',
    rotation: { poleRA: 257.311, poleDec: -15.175, W0: 203.81, rate: -501.1600928 },
    atmosphere: { rho0: 0.42, H: 27700 },
    visual: { color: '#9be3e8', texture: 'uranus.jpg', albedo: 0.3, atmosphere: ICE_GIANT_ATMO, ring: { inner: 41837, outer: 51149 } },
  },
  neptune: {
    id: 'neptune', name: 'Neptune', kind: 'planet', surface: 'gas', mass: 1.02409e26, radius: 24622, primaryId: 'sun',
    rotation: { poleRA: 299.36, poleDec: 43.46, W0: 249.978, rate: 541.1397757 },
    atmosphere: { rho0: 0.45, H: 19700 },
    visual: { color: '#5c7cff', texture: 'neptune.jpg', albedo: 0.29, atmosphere: ICE_GIANT_ATMO },
  },
}

/** Entry atmospheres for bodies outside CATALOG (surface density kg/m^3, scale height m). */
const SATELLITE_ATMOSPHERE: Record<string, AtmosphereModel> = {
  titan: { rho0: 5.4, H: 20600 },
}

const PLANETS_FROM_ELEMENTS = ['mercury', 'venus', 'earth', 'mars', 'jupiter', 'saturn', 'uranus', 'neptune']

/** Number-key targets, in order: 1..9 then 0. */
export const PLANET_ORDER = ['sun', 'mercury', 'venus', 'earth', 'mars', 'jupiter', 'saturn', 'uranus', 'neptune', 'pluto']

/** Order bodies appear in the HUD list: each planet followed by its moons. */
export const DISPLAY_ORDER: string[] = (() => {
  const order = ['sun']
  const moonsOf = (id: string) => SATELLITES.filter((s) => s.primaryId === id).map((s) => s.id)
  const minorsBetween: Record<string, string[]> = { mars: ['ceres', 'pallas', 'vesta'], neptune: ['pluto', 'haumea', 'makemake', 'eris'] }
  for (const id of PLANETS_FROM_ELEMENTS) {
    order.push(id, ...moonsOf(id))
    for (const m of minorsBetween[id] ?? []) order.push(m, ...moonsOf(m))
  }
  return order
})()

/** The satellites orbiting a given body, as the generated table lists them. */
export const satellitesOf = (id: string): SatelliteElements[] => SATELLITES.filter((s) => s.primaryId === id)

/**
 * Rotation elements of a tidally locked satellite. Its spin axis is the orbit normal and it turns
 * once per orbit, with the prime meridian on the sub-primary point — the IAU convention for the
 * regular satellites, and what makes the published maps line up. The axis is taken in whichever
 * hemisphere `reference` (the Laplace or planet pole) lies in, so that north on the map is north;
 * a retrograde moon like Triton then gets a negative rotation rate, again as the IAU writes it.
 */
function lockedRotation(reference: Vec3, periodDays: number, rel: { r: Vec3; v: Vec3 }, jd: number): Rotation {
  const normal = normalize(cross(rel.r, rel.v))
  const prograde = dot(normal, reference) >= 0
  const spin = prograde ? normal : scale(normal, -1)
  const basis = planeBasis(spin)
  const rate = (prograde ? 360 : -360) / periodDays
  // The primary sits opposite the satellite's position vector.
  const toPrimary = normalize(scale(rel.r, -1))
  const W = Math.atan2(dot(toPrimary, basis.y), dot(toPrimary, basis.x)) / DEG
  return { ...eclipticToPole(spin), W0: W - rate * (jd - J2000_JD), rate }
}

/** Position and velocity of a satellite relative to its primary, at Julian day jd. */
function satelliteState(sat: SatelliteElements, primaryMass: number, pole: Vec3 | null, jd: number): { r: Vec3; v: Vec3 } {
  const days = jd - sat.epochJD
  // Secular precession of the orbit itself: the node regresses for prograde orbits and advances for
  // retrograde ones, while the apsides always advance along the motion.
  const retrograde = sat.incl > 90
  const nodeRate = sat.nodeYr ? ((retrograde ? 360 : -360) * 86400) / (sat.nodeYr * YEAR) : 0
  const periRate = sat.apsisYr ? (360 * 86400) / (sat.apsisYr * YEAR) : 0
  const el = {
    a: sat.a,
    e: sat.e,
    i: sat.incl * DEG,
    Omega: (sat.node + nodeRate * days) * DEG,
    omega: (sat.peri + periRate * days) * DEG,
    M: (sat.M + (360 * days) / sat.periodDays) * DEG,
  }
  const mu = G * (primaryMass + sat.mass)
  // Meeus' mean lunar elements track the Moon better than the single-epoch table row.
  if (sat.id === 'moon') return elementsToState(mu, moonElementsAt(centuriesSinceJ2000(jd)))
  const state = elementsToState(mu, el)
  if (sat.frame === 'ecliptic' || !pole) return state
  const basis = planeBasis(pole)
  return { r: fromBasis(basis, state.r), v: fromBasis(basis, state.v) }
}

/** Radius at which a satellite counts as "major" for the level-of-detail setting. */
export const MAJOR_MOON_RADIUS_KM = 600

/** Build a satellite in orbit around `primary` as its elements place it at Julian day jd. */
export function makeSatellite(sat: SatelliteElements, primary: Body, jd: number): Body {
  return satelliteBody(sat, primary, primary.rotation, jd)
}

function satelliteBody(sat: SatelliteElements, primary: Body, primaryRotation: Rotation | undefined, jd: number): Body {
  const poleRA = sat.poleRA ?? primaryRotation?.poleRA
  const poleDec = sat.poleDec ?? primaryRotation?.poleDec
  const pole = poleRA !== undefined && poleDec !== undefined ? poleToEcliptic(poleRA, poleDec) : null
  const rel = satelliteState(sat, primary.mass, pole, jd)
  const rotation = sat.locked
    ? lockedRotation(pole ?? ECLIPTIC_NORTH, sat.periodDays, rel, jd)
    : undefined
  return makeBody({
    id: sat.id, name: sat.name, kind: 'moon', surface: sat.surface,
    mass: sat.mass, radius: sat.radius, primaryId: sat.primaryId,
    pos: add(primary.pos, rel.r), vel: add(primary.vel, rel.v),
    rotation, atmosphere: SATELLITE_ATMOSPHERE[sat.id],
    visual: sat.id === 'titan' ? { ...sat.visual, atmosphere: TITAN_ATMO } : sat.visual,
  })
}

/** Heliocentric state of a dwarf planet or asteroid at Julian day jd. */
function minorState(m: MinorBodyElements, jd: number): { r: Vec3; v: Vec3 } {
  const days = jd - m.epochJD
  const el = {
    a: m.a, e: m.e, i: m.incl * DEG, Omega: m.node * DEG, omega: m.peri * DEG,
    M: (m.M + m.nDegPerDay * days) * DEG,
  }
  return elementsToState(G * (SUN_MASS + m.mass), el)
}

/** Ecliptic north in ICRF: the fallback spin axis for bodies with no published pole. */
const ECLIPTIC_POLE_RA = 270
const ECLIPTIC_POLE_DEC = 90 - 23.4392911
const ECLIPTIC_NORTH: Vec3 = { x: 0, y: 0, z: 1 }

function minorBody(m: MinorBodyElements, state: { r: Vec3; v: Vec3 }): Body {
  const rotation: Rotation | undefined = m.rotation
    ?? (m.rotationPeriodHours
      ? { poleRA: ECLIPTIC_POLE_RA, poleDec: ECLIPTIC_POLE_DEC, W0: 0, rate: (360 * 24) / m.rotationPeriodHours }
      : undefined)
  return makeBody({
    id: m.id, name: m.name, kind: m.kind, surface: m.surface, mass: m.mass, radius: m.radius,
    primaryId: 'sun', pos: state.r, vel: state.v, rotation, visual: m.visual,
  })
}

/** Shift a planet and its moons so their common barycentre lands on the position the elements describe. */
function centreSystem(members: Body[], pos: Vec3, vel: Vec3): void {
  let m = 0
  let r = v3()
  let v = v3()
  for (const b of members) {
    m += b.mass
    r = add(r, scale(b.pos, b.mass))
    v = add(v, scale(b.vel, b.mass))
  }
  const dr = sub(pos, scale(r, 1 / m))
  const dv = sub(vel, scale(v, 1 / m))
  for (const b of members) {
    b.pos = add(b.pos, dr)
    b.vel = add(b.vel, dv)
  }
}

/**
 * Build the Sun, the planets with their major moons, the dwarf planets and the two largest asteroids
 * at the given date, with the system barycentre at rest at the origin.
 */
export function createSolarSystem(date: Date): { bodies: Body[]; epochJD: number } {
  const jd = julianDay(date)
  const T = centuriesSinceJ2000(jd)
  const bodies: Body[] = []
  bodies.push(makeBody({ ...CATALOG.sun }))

  /** Place a primary at (r, v), hang its satellites off it, then recentre the group on (r, v). */
  const withMoons = (primary: Body, moons: SatelliteElements[], r: Vec3, v: Vec3, rotation?: Rotation): void => {
    primary.pos = { ...r }
    primary.vel = { ...v }
    const group = [primary]
    for (const sat of moons) group.push(satelliteBody(sat, primary, rotation, jd))
    centreSystem(group, r, v)
    bodies.push(...group)
  }

  for (const id of PLANETS_FROM_ELEMENTS) {
    const spec = CATALOG[id]
    const moons = satellitesOf(id)
    // The Standish elements describe the planet-system barycentre, so the mass in mu is the system's.
    const systemMass = moons.reduce((s, m) => s + m.mass, spec.mass)
    const el = planetElementsAt(id, T)
    const { r, v } = elementsToState(G * (SUN_MASS + systemMass), el)
    withMoons(makeBody({ ...spec }), moons, r, v, spec.rotation)
  }

  for (const m of MINOR_BODIES) {
    const state = minorState(m, jd)
    const body = minorBody(m, state)
    withMoons(body, satellitesOf(m.id), state.r, state.v, body.rotation)
  }

  // Put the barycentre at rest at the origin.
  let mTot = 0
  let pSum: Vec3 = v3()
  let rSum: Vec3 = v3()
  for (const b of bodies) {
    mTot += b.mass
    pSum = add(pSum, scale(b.vel, b.mass))
    rSum = add(rSum, scale(b.pos, b.mass))
  }
  const vBary = scale(pSum, 1 / mTot)
  const rBary = scale(rSum, 1 / mTot)
  for (const b of bodies) {
    b.vel = sub(b.vel, vBary)
    b.pos = sub(b.pos, rBary)
  }
  const rank = (id: string) => {
    const i = DISPLAY_ORDER.indexOf(id)
    return i < 0 ? DISPLAY_ORDER.length : i
  }
  bodies.sort((a, b) => rank(a.id) - rank(b.id))
  return { bodies, epochJD: jd }
}
