import { describe, expect, it } from 'vitest'
import { createSolarSystem } from '../src/sim/catalog'
import { Universe } from '../src/sim/universe'
import { makeBody } from '../src/sim/body'
import { add, scale, normalize } from '../src/sim/vec'
import { AIM_PAIR_BUDGET, autoAim, predictPath } from '../src/sim/aim'
import { AU } from '../src/sim/constants'

/** The solar system plus a debris ring of light fragments round the Moon, as a shatter leaves. */
function crowded(fragments: number) {
  const { bodies, epochJD } = createSolarSystem(new Date('2000-01-01T12:00:00Z'))
  const u = new Universe(epochJD)
  u.setBodies(bodies)
  const moon = u.byId('moon')!
  for (let i = 0; i < fragments; i++) {
    const a = (i / fragments) * Math.PI * 2
    u.add(makeBody({
      id: 'frag' + i, name: 'Fragment', kind: 'fragment', surface: 'rock', mass: 1e18 * (1 + (i % 7)), radius: 40,
      pos: add(moon.pos, { x: 4000 * Math.cos(a), y: 4000 * Math.sin(a), z: (i % 5) * 300 }),
      vel: add(moon.vel, { x: Math.sin(a) * 0.3, y: -Math.cos(a) * 0.3, z: 0 }),
      visual: { color: '#888', albedo: 0.1 },
    }))
  }
  return u
}

describe('aiming', () => {
  // Auto-aim runs synchronously on a click. It used to integrate every fragment against every other,
  // and 200 of them made one aim take 33 s and still miss. Counting pair evaluations rather than
  // timing keeps this deterministic.
  it('still hits through a debris cloud, within its budget', () => {
    const u = crowded(201)
    expect(u.bodies.length).toBe(240)
    const earth = u.byId('earth')!
    const from = add(earth.pos, scale(normalize(earth.vel), 0.05 * AU))
    const aim = autoAim(u, from, 'earth', 25, 0.5)!
    expect(aim.converged).toBe(true)
    expect(aim.prediction.hit!.bodyId).toBe('earth')
    expect(aim.pairEvals).toBeLessThanOrEqual(AIM_PAIR_BUDGET)
  })

  it('costs about the same with a debris cloud as without one', () => {
    const cost = (f: number) => {
      const u = crowded(f)
      const io = u.byId('io')!
      const from = add(io.pos, scale(normalize(u.byId('earth')!.vel), 0.02 * AU))
      const aim = autoAim(u, from, 'io', 20, 0.5)!
      expect(aim.converged).toBe(true)
      return aim.pairEvals
    }
    expect(cost(201)).toBeLessThan(cost(0) * 2.5)
  })

  it('stops at its pair budget and says so', () => {
    const u = crowded(50)
    const earth = u.byId('earth')!
    const r = predictPath(u, { pos: add(earth.pos, { x: 2e6, y: 0, z: 0 }), vel: earth.vel, radius: 0.5 }, 1e9, { pairBudget: 50_000 })
    expect(r.truncated).toBe(true)
    expect(r.pairEvals).toBeLessThanOrEqual(50_000)
  })

  it('stays finite when two bodies sit on top of each other', () => {
    const u = crowded(0)
    const moon = u.byId('moon')!
    u.add(makeBody({ id: 'twin', name: 'Twin', kind: 'fragment', surface: 'rock', mass: moon.mass, radius: 10, pos: { ...moon.pos }, vel: { ...moon.vel }, visual: { color: '#888', albedo: 0.1 } }))
    const earth = u.byId('earth')!
    const r = predictPath(u, { pos: add(earth.pos, { x: 5e5, y: 0, z: 0 }), vel: earth.vel, radius: 0.5 }, 30 * 86400, { maxSteps: 500 })
    for (const p of r.points) expect(Number.isFinite(p.x + p.y + p.z)).toBe(true)
  })
})
