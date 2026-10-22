import { DEG, OBLIQUITY } from './constants'
import { Vec3, anyPerpendicular, cross, length, normalize } from './vec'

const COS_EPS = Math.cos(OBLIQUITY)
const SIN_EPS = Math.sin(OBLIQUITY)

/** ICRF equatorial J2000 to ecliptic J2000. */
export const equatorialToEcliptic = (v: Vec3): Vec3 => ({
  x: v.x,
  y: v.y * COS_EPS + v.z * SIN_EPS,
  z: -v.y * SIN_EPS + v.z * COS_EPS,
})

/** Unit vector of a pole given as ICRF right ascension and declination (degrees), in ecliptic coordinates. */
export function poleToEcliptic(raDeg: number, decDeg: number): Vec3 {
  const ra = raDeg * DEG
  const dec = decDeg * DEG
  const cd = Math.cos(dec)
  return normalize(equatorialToEcliptic({ x: cd * Math.cos(ra), y: cd * Math.sin(ra), z: Math.sin(dec) }))
}

/** Ecliptic J2000 back to ICRF equatorial J2000. */
export const eclipticToEquatorial = (v: Vec3): Vec3 => ({
  x: v.x,
  y: v.y * COS_EPS - v.z * SIN_EPS,
  z: v.y * SIN_EPS + v.z * COS_EPS,
})

/** An ecliptic direction as ICRF right ascension and declination, degrees. */
export function eclipticToPole(v: Vec3): { poleRA: number; poleDec: number } {
  const e = eclipticToEquatorial(normalize(v))
  const ra = Math.atan2(e.y, e.x) / DEG
  return { poleRA: (ra + 360) % 360, poleDec: Math.asin(Math.max(-1, Math.min(1, e.z))) / DEG }
}

/** Right-handed basis, in ecliptic coordinates, of a reference plane. */
export interface PlaneBasis {
  x: Vec3
  y: Vec3
  z: Vec3
}

const ICRF_NORTH = equatorialToEcliptic({ x: 0, y: 0, z: 1 })

/**
 * Basis of the plane normal to `pole`. x lies along that plane's ascending node on the ICRF equator,
 * which is the direction JPL measures satellite nodes from; z is the pole.
 */
export function planeBasis(pole: Vec3): PlaneBasis {
  let x = cross(ICRF_NORTH, pole)
  if (length(x) < 1e-9) x = anyPerpendicular(pole)
  x = normalize(x)
  return { x, y: cross(pole, x), z: pole }
}

/** A vector expressed in `b` back into ecliptic coordinates. */
export const fromBasis = (b: PlaneBasis, v: Vec3): Vec3 => ({
  x: b.x.x * v.x + b.y.x * v.y + b.z.x * v.z,
  y: b.x.y * v.x + b.y.y * v.y + b.z.y * v.z,
  z: b.x.z * v.x + b.y.z * v.y + b.z.z * v.z,
})

/** An ecliptic vector expressed in `b`. */
export const toBasis = (b: PlaneBasis, v: Vec3): Vec3 => ({
  x: b.x.x * v.x + b.x.y * v.y + b.x.z * v.z,
  y: b.y.x * v.x + b.y.y * v.y + b.y.z * v.z,
  z: b.z.x * v.x + b.z.y * v.y + b.z.z * v.z,
})
