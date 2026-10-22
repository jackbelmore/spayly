import { describe, expect, it } from 'vitest'
import { elementsToState, stateToElements, conicPoints, solveKepler } from '../src/sim/kepler'
import { AU, G, SUN_MASS, DEG, YEAR } from '../src/sim/constants'
import { length } from '../src/sim/vec'

const mu = G * SUN_MASS

describe('kepler', () => {
  it('solves Kepler equation', () => {
    for (const e of [0, 0.1, 0.5, 0.9, 0.99]) {
      for (let M = -3; M <= 3; M += 0.37) {
        const E = solveKepler(M, e)
        expect(E - e * Math.sin(E)).toBeCloseTo(((M + Math.PI) % (2 * Math.PI) + 2 * Math.PI) % (2 * Math.PI) - Math.PI, 10)
      }
    }
  })

  it('places a circular 1 AU orbit at the right speed and period', () => {
    const { r, v } = elementsToState(mu, { a: AU, e: 0, i: 0, Omega: 0, omega: 0, M: 0 })
    expect(length(r) / AU).toBeCloseTo(1, 9)
    expect(length(v)).toBeCloseTo(29.78, 1)
    const el = stateToElements(mu, r, v)
    expect(el.period / YEAR).toBeCloseTo(1, 3)
  })

  it('round-trips elements through state vectors', () => {
    const el0 = { a: 2.3 * AU, e: 0.31, i: 12 * DEG, Omega: 80 * DEG, omega: 200 * DEG, M: 1.1 }
    const { r, v } = elementsToState(mu, el0)
    const el = stateToElements(mu, r, v)
    expect(el.a / el0.a).toBeCloseTo(1, 9)
    expect(el.e).toBeCloseTo(el0.e, 9)
    expect(el.i).toBeCloseTo(el0.i, 9)
    expect(el.Omega).toBeCloseTo(el0.Omega, 9)
    expect(el.omega).toBeCloseTo(el0.omega, 9)
  })

  it('samples a closed conic through the current point', () => {
    const el0 = { a: 1.5 * AU, e: 0.2, i: 5 * DEG, Omega: 1, omega: 2, M: 0.7 }
    const { r, v } = elementsToState(mu, el0)
    const pts = conicPoints(mu, r, v, 256)
    expect(pts.length).toBe(257)
    const minDist = Math.min(...pts.map((p) => length({ x: p.x - r.x, y: p.y - r.y, z: p.z - r.z })))
    expect(minDist / AU).toBeLessThan(0.02)
    const el = stateToElements(mu, r, v)
    const radii = pts.map((p) => length(p))
    expect(Math.min(...radii) / el.periapsis).toBeCloseTo(1, 3)
    expect(Math.max(...radii) / el.apoapsis).toBeCloseTo(1, 3)
  })
})
