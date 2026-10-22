import { Vec3, cross, dot, length, normalize, scale, sub, clamp, anyPerpendicular } from './vec'

/** Solve Kepler's equation M = E - e sin E (elliptic). Radians. */
export function solveKepler(M: number, e: number): number {
  M = ((M + Math.PI) % (2 * Math.PI) + 2 * Math.PI) % (2 * Math.PI) - Math.PI
  let E = e < 0.8 ? M : Math.PI * Math.sign(M || 1)
  for (let i = 0; i < 40; i++) {
    const f = E - e * Math.sin(E) - M
    const fp = 1 - e * Math.cos(E)
    const dE = f / fp
    E -= dE
    if (Math.abs(dE) < 1e-14) break
  }
  return E
}

/** Classical elements: a (km), angles in radians. M = mean anomaly. */
export interface Elements {
  a: number
  e: number
  i: number
  Omega: number
  omega: number
  M: number
}

/** Position (km) and velocity (km/s) relative to the primary for elliptic elements. */
export function elementsToState(mu: number, el: Elements): { r: Vec3; v: Vec3 } {
  const { a, e, i, Omega, omega } = el
  const E = solveKepler(el.M, e)
  const cosE = Math.cos(E)
  const sinE = Math.sin(E)
  const sq = Math.sqrt(1 - e * e)
  const xp = a * (cosE - e)
  const yp = a * sq * sinE
  const n = Math.sqrt(mu / (a * a * a))
  const denom = 1 - e * cosE
  const vxp = (-a * n * sinE) / denom
  const vyp = (a * n * sq * cosE) / denom
  const cO = Math.cos(Omega), sO = Math.sin(Omega)
  const ci = Math.cos(i), si = Math.sin(i)
  const cw = Math.cos(omega), sw = Math.sin(omega)
  const R11 = cO * cw - sO * sw * ci
  const R12 = -cO * sw - sO * cw * ci
  const R21 = sO * cw + cO * sw * ci
  const R22 = -sO * sw + cO * cw * ci
  const R31 = sw * si
  const R32 = cw * si
  return {
    r: { x: R11 * xp + R12 * yp, y: R21 * xp + R22 * yp, z: R31 * xp + R32 * yp },
    v: { x: R11 * vxp + R12 * vyp, y: R21 * vxp + R22 * vyp, z: R31 * vxp + R32 * vyp },
  }
}

export interface Osculating {
  a: number // km (negative if hyperbolic)
  e: number
  i: number
  Omega: number
  omega: number
  nu: number
  p: number // semi-latus rectum km
  period: number // s (Infinity if unbound)
  energy: number // specific orbital energy km^2/s^2
  periapsis: number
  apoapsis: number // Infinity if unbound
  h: Vec3
  evec: Vec3
}

/** Osculating elements from a relative state vector. */
export function stateToElements(mu: number, r: Vec3, v: Vec3): Osculating {
  const rm = length(r)
  const v2 = dot(v, v)
  const h = cross(r, v)
  const hm = length(h)
  const evec = sub(scale(cross(v, h), 1 / mu), scale(r, 1 / rm))
  const e = length(evec)
  const energy = v2 / 2 - mu / rm
  const a = -mu / (2 * energy)
  const p = (hm * hm) / mu
  const i = Math.acos(clamp(hm > 0 ? h.z / hm : 1, -1, 1))
  const nvec = { x: -h.y, y: h.x, z: 0 }
  const nm = length(nvec)
  const Omega = nm > 1e-12 ? Math.atan2(nvec.y, nvec.x) : 0
  let omega = 0
  if (nm > 1e-12 && e > 1e-12) {
    omega = Math.acos(clamp(dot(nvec, evec) / (nm * e), -1, 1))
    if (evec.z < 0) omega = 2 * Math.PI - omega
  } else if (e > 1e-12) {
    omega = Math.atan2(evec.y, evec.x)
  }
  let nu = 0
  if (e > 1e-12) {
    nu = Math.acos(clamp(dot(evec, r) / (e * rm), -1, 1))
    if (dot(r, v) < 0) nu = 2 * Math.PI - nu
  }
  const period = e < 1 && a > 0 ? 2 * Math.PI * Math.sqrt((a * a * a) / mu) : Infinity
  return {
    a, e, i, Omega, omega, nu, p, period, energy,
    periapsis: p / (1 + e),
    apoapsis: e < 1 ? p / (1 - e) : Infinity,
    h, evec,
  }
}

/**
 * Sample the osculating conic through (r, v) relative to the primary.
 * Returns points relative to the primary (km). Hyperbolic paths are clipped at rMax.
 *
 * Pass `out` to have the points written into it, reusing its objects: orbit lines redraw every
 * body every frame, and fresh points cost about 540 short-lived objects per body per frame.
 */
export function conicPoints(mu: number, r: Vec3, v: Vec3, count: number, rMax = 1e10, startNu = 0, gap = 0, out: Vec3[] = []): Vec3[] {
  const el = stateToElements(mu, r, v)
  const { e, p } = el
  if (!(p > 0) || !isFinite(p)) { out.length = 0; return out }
  const hUnit = normalize(el.h)
  const P = e > 1e-9 ? normalize(el.evec) : anyPerpendicular(hUnit)
  const Q = normalize(cross(hUnit, P))
  let nu0: number, span: number
  if (e < 1) {
    nu0 = startNu + gap
    span = 2 * Math.PI - 2 * gap
  } else {
    const cosLimit = clamp((p / rMax - 1) / e, -1, 1)
    const nuMax = Math.acos(cosLimit) * 0.999
    nu0 = -nuMax
    span = 2 * nuMax
  }
  for (let k = 0; k <= count; k++) {
    const nu = nu0 + (k / count) * span
    const rad = p / (1 + e * Math.cos(nu))
    const a = rad * Math.cos(nu), b = rad * Math.sin(nu)
    const q = out[k] ?? (out[k] = { x: 0, y: 0, z: 0 })
    q.x = P.x * a + Q.x * b
    q.y = P.y * a + Q.y * b
    q.z = P.z * a + Q.z * b
  }
  out.length = count + 1
  return out
}
