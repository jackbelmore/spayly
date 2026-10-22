import { describe, expect, it } from 'vitest'
import { createSolarSystem } from '../src/sim/catalog'
import { Universe } from '../src/sim/universe'
import { AU, DAY, YEAR, G, SUN_MASS } from '../src/sim/constants'
import { add, length, normalize, scale, sub } from '../src/sim/vec'
import { makeBody } from '../src/sim/body'
import { resolveCollision, ImpactOutcome } from '../src/sim/collide'
import { stateToElements } from '../src/sim/kepler'

function build(date = new Date('2000-01-01T12:00:00Z')) {
  const { bodies, epochJD } = createSolarSystem(date)
  const u = new Universe(epochJD)
  u.setBodies(bodies)
  return u
}

describe('solar system', () => {
  it('places the planets at sensible heliocentric distances at J2000', () => {
    const u = build()
    const sun = u.byId('sun')!
    const d = (id: string) => length(sub(u.byId(id)!.pos, sun.pos)) / AU
    expect(d('mercury')).toBeGreaterThan(0.3)
    expect(d('mercury')).toBeLessThan(0.47)
    expect(d('earth')).toBeGreaterThan(0.98)
    expect(d('earth')).toBeLessThan(1.02)
    expect(d('jupiter')).toBeGreaterThan(4.9)
    expect(d('jupiter')).toBeLessThan(5.5)
    expect(d('neptune')).toBeGreaterThan(29.7)
    expect(d('neptune')).toBeLessThan(30.4)
    const moon = length(sub(u.byId('moon')!.pos, u.byId('earth')!.pos))
    expect(moon).toBeGreaterThan(356000)
    expect(moon).toBeLessThan(407000)
  })

  it('has the barycentre at rest', () => {
    const u = build()
    const b = u.barycentre()
    expect(length(b.pos)).toBeLessThan(1)
    expect(length(b.vel)).toBeLessThan(1e-9)
  })

  it('conserves energy over a year of integration', () => {
    const u = build()
    const e0 = u.energy().total
    let t = 0
    while (t < YEAR) {
      u.step(DAY)
      t += DAY
    }
    const e1 = u.energy().total
    expect(Math.abs((e1 - e0) / e0)).toBeLessThan(1e-6)
  })

  it('brings Earth back after one orbital period', () => {
    const u = build()
    const earth = u.byId('earth')!
    const sun = u.byId('sun')!
    const start = sub(earth.pos, sun.pos)
    const el = stateToElements(G * (SUN_MASS + earth.mass), start, sub(earth.vel, sun.vel))
    let t = 0
    while (t < el.period) {
      const h = Math.min(DAY, el.period - t)
      u.step(h)
      t += h
    }
    const end = sub(earth.pos, sun.pos)
    expect(length(sub(end, start)) / AU).toBeLessThan(0.01)
  })

  it('keeps the Moon on a monthly orbit', () => {
    const u = build()
    const earth = u.byId('earth')!
    const moon = u.byId('moon')!
    const angle = () => {
      const r = sub(moon.pos, earth.pos)
      return Math.atan2(r.y, r.x)
    }
    let a0 = angle()
    let turned = 0
    let t = 0
    while (turned < 2 * Math.PI) {
      u.step(3600)
      t += 3600
      const a1 = angle()
      let d = a1 - a0
      if (d > Math.PI) d -= 2 * Math.PI
      if (d < -Math.PI) d += 2 * Math.PI
      turned += d
      a0 = a1
      if (t > 40 * DAY) break
    }
    expect(t / DAY).toBeGreaterThan(26.5)
    expect(t / DAY).toBeLessThan(28.5)
  })

  it('is exactly time reversible with fixed substeps', () => {
    const u = build()
    u.maxSubstep = 1800
    u.minSubstep = 1800
    const snap = u.snapshot()
    for (let i = 0; i < 30; i++) u.step(DAY)
    for (let i = 0; i < 30; i++) u.step(-DAY)
    for (const b of u.bodies) {
      const orig = snap.bodies.find((x) => x.id === b.id)!
      expect(length(sub(b.pos, orig.pos))).toBeLessThan(1e-3)
    }
  })

  it('returns close to the start with adaptive substeps', () => {
    const u = build()
    const snap = u.snapshot()
    for (let i = 0; i < 60; i++) u.step(DAY)
    for (let i = 0; i < 60; i++) u.step(-DAY)
    for (const b of u.bodies) {
      const orig = snap.bodies.find((x) => x.id === b.id)!
      expect(length(sub(b.pos, orig.pos))).toBeLessThan(200)
    }
  })

  it('keeps to its pair budget when a shatter adds bodies partway through a frame', () => {
    const u = build()
    const outcomes: ImpactOutcome[] = []
    u.collisionHandlers.push((ev, uni) => { outcomes.push(resolveCollision(uni, ev, { bodyFrame: (_b, d) => d })) })
    const earth = u.byId('earth')!
    const dir = normalize({ x: 0, y: 1, z: 0 })
    u.add(makeBody({
      id: 'theia', name: 'Theia', kind: 'asteroid', surface: 'rock', mass: 6.4e23, radius: 3390,
      pos: add(earth.pos, scale(dir, earth.radius + 3390 + 300)), vel: add(earth.vel, scale(dir, -60)),
      visual: { color: '#fff', albedo: 0.1 },
    }))
    const n0 = u.bodies.length
    u.maxPairEvalsPerFrame = 60 * (n0 * (n0 - 1)) / 2
    u.step(3600)
    expect(outcomes.some((o) => o.fragments.length > 0)).toBe(true)
    expect(u.bodies.length).toBeGreaterThan(n0)
    expect(u.warpLimited).toBe(true)
    // At most one collision re-pack past the budget: the check runs before each substep.
    const n = u.bodies.length
    expect(u.pairEvalsLastFrame).toBeLessThanOrEqual(u.maxPairEvalsPerFrame + (n * (n - 1)) / 2)
  })

  it('throttles the warp instead of skipping physics', () => {
    const u = build()
    u.maxSubstepsPerFrame = 5
    const advanced = u.step(30 * DAY)
    expect(u.warpLimited).toBe(true)
    expect(advanced).toBeLessThan(30 * DAY)
    expect(advanced).toBeGreaterThan(0)
  })
})
