import { AU, DEG, J2000_JD } from './constants'
import { Elements } from './kepler'

export const julianDay = (date: Date): number => date.getTime() / 86400000 + 2440587.5
export const centuriesSinceJ2000 = (jd: number): number => (jd - J2000_JD) / 36525
export const dateFromJD = (jd: number): Date => new Date((jd - 2440587.5) * 86400000)

/**
 * Keplerian mean elements and rates, valid 1800-2050 (E. M. Standish, JPL).
 * [value at J2000, rate per century]; a in AU, angles in degrees.
 * L = mean longitude, lp = longitude of perihelion, node = longitude of ascending node.
 */
interface MeanElements {
  a: [number, number]
  e: [number, number]
  i: [number, number]
  L: [number, number]
  lp: [number, number]
  node: [number, number]
}

export const PLANET_ELEMENTS: Record<string, MeanElements> = {
  mercury: { a: [0.38709927, 0.00000037], e: [0.20563593, 0.00001906], i: [7.00497902, -0.00594749], L: [252.2503235, 149472.67411175], lp: [77.45779628, 0.16047689], node: [48.33076593, -0.12534081] },
  venus: { a: [0.72333566, 0.0000039], e: [0.00677672, -0.00004107], i: [3.39467605, -0.0007889], L: [181.9790995, 58517.81538729], lp: [131.60246718, 0.00268329], node: [76.67984255, -0.27769418] },
  earth: { a: [1.00000261, 0.00000562], e: [0.01671123, -0.00004392], i: [-0.00001531, -0.01294668], L: [100.46457166, 35999.37244981], lp: [102.93768193, 0.32327364], node: [0, 0] },
  mars: { a: [1.52371034, 0.00001847], e: [0.0933941, 0.00007882], i: [1.84969142, -0.00813131], L: [-4.55343205, 19140.30268499], lp: [-23.94362959, 0.44441088], node: [49.55953891, -0.29257343] },
  jupiter: { a: [5.202887, -0.00011607], e: [0.04838624, -0.00013253], i: [1.30439695, -0.00183714], L: [34.39644051, 3034.74612775], lp: [14.72847983, 0.21252668], node: [100.47390909, 0.20469106] },
  saturn: { a: [9.53667594, -0.0012506], e: [0.05386179, -0.00050991], i: [2.48599187, 0.00193609], L: [49.95424423, 1222.49362201], lp: [92.59887831, -0.41897216], node: [113.66242448, -0.28867794] },
  uranus: { a: [19.18916464, -0.00196176], e: [0.04725744, -0.00004397], i: [0.77263783, -0.00242939], L: [313.23810451, 428.48202785], lp: [170.9542763, 0.40805281], node: [74.01692503, 0.04240589] },
  neptune: { a: [30.06992276, 0.00026291], e: [0.00859048, 0.00005105], i: [1.77004347, 0.00035372], L: [-55.12002969, 218.45945325], lp: [44.96476227, -0.32241464], node: [131.78422574, -0.00508664] },
}

const wrapDeg = (d: number): number => ((d % 360) + 360) % 360

/** Heliocentric ecliptic elements of a planet (Earth = Earth-Moon barycentre) at T centuries past J2000. */
export function planetElementsAt(name: string, T: number): Elements {
  const m = PLANET_ELEMENTS[name]
  if (!m) throw new Error(`no elements for ${name}`)
  const at = (p: [number, number]) => p[0] + p[1] * T
  const a = at(m.a) * AU
  const e = at(m.e)
  const i = at(m.i) * DEG
  const L = at(m.L)
  const lp = at(m.lp)
  const node = at(m.node)
  const omega = (lp - node) * DEG
  const M = wrapDeg(L - lp) * DEG
  return { a, e, i, Omega: node * DEG, omega, M }
}

/** Mean geocentric lunar elements (Meeus, Astronomical Algorithms ch. 47). Accurate to a few degrees. */
export function moonElementsAt(T: number): Elements {
  const Lp = 218.3164477 + 481267.88123421 * T - 0.0015786 * T * T
  const Mp = 134.9633964 + 477198.8675055 * T + 0.0087414 * T * T
  const node = 125.0445479 - 1934.1362891 * T + 0.0020754 * T * T
  const omega = wrapDeg(Lp - Mp - node)
  return { a: 384400, e: 0.0549, i: 5.145 * DEG, Omega: wrapDeg(node) * DEG, omega: omega * DEG, M: wrapDeg(Mp) * DEG }
}
