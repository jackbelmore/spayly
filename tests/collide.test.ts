import { describe, expect, it } from 'vitest'
import { createSolarSystem } from '../src/sim/catalog'
import { Universe } from '../src/sim/universe'
import { resolveCollision, disruptionEnergy, ImpactOutcome } from '../src/sim/collide'
import { bindingEnergy, makeBody } from '../src/sim/body'
import { add, scale, sub, length, normalize, Vec3 } from '../src/sim/vec'
import { autoAim } from '../src/sim/aim'
import { AU, G } from '../src/sim/constants'

function build() {
  const { bodies, epochJD } = createSolarSystem(new Date('2000-01-01T12:00:00Z'))
  const u = new Universe(epochJD)
  u.setBodies(bodies)
  const outcomes: ImpactOutcome[] = []
  u.collisionHandlers.push((ev, uni) => {
    outcomes.push(resolveCollision(uni, ev, { bodyFrame: (_b, d) => d }))
  })
  return { u, outcomes }
}

function totalMomentum(u: Universe): Vec3 {
  let p: Vec3 = { x: 0, y: 0, z: 0 }
  for (const b of u.bodies) p = add(p, scale(b.vel, b.mass))
  return p
}

/** Floating-point noise floor of the momentum sum (the barycentric total is ~0, so use the term scale). */
function momentumNoise(u: Universe): number {
  let s = 0
  for (const b of u.bodies) s += b.mass * length(b.vel)
  return s * 1e-14
}

describe('collisions', () => {
  it('a small asteroid craters Earth and merges, conserving momentum', () => {
    const { u, outcomes } = build()
    const earth = u.byId('earth')!
    const dir = normalize({ x: 1, y: 0.2, z: 0.1 })
    const ast = makeBody({
      id: 'ast', name: 'Test rock', kind: 'asteroid', surface: 'rock', mass: 1.5e12, radius: 0.5,
      pos: add(earth.pos, scale(dir, earth.radius + 400)), vel: add(earth.vel, scale(dir, -20)),
      visual: { color: '#fff', albedo: 0.1 },
    })
    u.add(ast)
    const p0 = totalMomentum(u)
    const m0 = earth.mass
    for (let i = 0; i < 200 && outcomes.length === 0; i++) u.step(2)
    expect(outcomes.length).toBe(1)
    const o = outcomes[0]
    expect(o.kind).toBe('crater')
    expect(o.target.id).toBe('earth')
    expect(o.report?.crater?.finalDiameter).toBeGreaterThan(5000)
    expect(earth.mass).toBeCloseTo(m0 + 1.5e12, -3)
    expect(u.byId('ast')).toBeUndefined()
    expect(earth.damage.craters.length).toBe(1)
    const p1 = totalMomentum(u)
    expect(length(sub(p1, p0))).toBeLessThan(momentumNoise(u))
    expect(u.history.length).toBe(1)
  })

  it('a Mars-mass body at 60 km/s shatters Earth into fragments with momentum conserved', () => {
    const { u, outcomes } = build()
    const earth = u.byId('earth')!
    const dir = normalize({ x: 0, y: 1, z: 0 })
    const big = makeBody({
      id: 'theia', name: 'Theia', kind: 'asteroid', surface: 'rock', mass: 6.4e23, radius: 3390,
      pos: add(earth.pos, scale(dir, earth.radius + 3390 + 300)), vel: add(earth.vel, scale(dir, -60)),
      visual: { color: '#fff', albedo: 0.1 },
    })
    u.add(big)
    const p0 = totalMomentum(u)
    const n0 = u.bodies.length
    for (let i = 0; i < 400 && outcomes.length === 0; i++) u.step(1)
    expect(outcomes.length).toBe(1)
    const o = outcomes[0]
    expect(['shatter', 'disperse']).toContain(o.kind)
    expect(o.fragments.length).toBeGreaterThanOrEqual(6)
    expect(u.bodies.length).toBe(n0 - 1 + o.fragments.length)
    const p1 = totalMomentum(u)
    expect(length(sub(p1, p0))).toBeLessThan(momentumNoise(u) + 1e-9 * 6.4e23 * 60)
    let mass = earth.mass
    for (const f of o.fragments) mass += f.mass
    expect(mass / (5.9722e24 + 6.4e23)).toBeCloseTo(1, 9)
    expect(earth.surface).toBe('lava')
  })

  it('keeps one undo point for a cascade of collisions on consecutive steps', () => {
    // Five pairs of rocks far apart, each timed to touch one step after the last: a cascade the way a
    // debris cloud makes one, where every hit used to clone the whole universe into the undo stack.
    const u = new Universe(2451545)
    const rocks = []
    for (let k = 1; k <= 5; k++) {
      const y = k * 1e5
      const spec = { kind: 'asteroid' as const, surface: 'rock' as const, mass: 1.5e12, radius: 0.5, visual: { color: '#fff', albedo: 0.1 } }
      rocks.push(makeBody({ ...spec, id: `a${k}`, name: `Rock A${k}`, pos: { x: 0, y, z: 0 }, vel: { x: 0, y: 0, z: 0 } }))
      rocks.push(makeBody({ ...spec, id: `b${k}`, name: `Rock B${k}`, pos: { x: 1 + k - 0.5, y, z: 0 }, vel: { x: -1, y: 0, z: 0 } }))
    }
    u.setBodies(rocks)
    const outcomes: ImpactOutcome[] = []
    u.collisionHandlers.push((ev, uni) => { outcomes.push(resolveCollision(uni, ev, { bodyFrame: (_x, d) => d })) })
    for (let i = 0; i < 8; i++) u.step(1)
    expect(outcomes.length).toBe(5)
    expect(u.history.length).toBe(1)
    u.popHistory()
    expect(u.bodies.length).toBe(10)
  })

  it('never spawns fragments inside each other or the remnant', () => {
    // Overlapping fragments would all collide on the very next substep and start a cascade by
    // construction. Sweep sizes, speeds and seeds, including the most violent dispersals.
    let shatters = 0
    for (const R of [0.5, 50, 3000]) {
      for (const speed of [5, 20, 80, 160]) {
        for (let seed = 0; seed < 6; seed++) {
          const u = new Universe(2451545)
          const M = 3000 * (4 / 3) * Math.PI * Math.pow(R * 1000, 3)
          const spec = { kind: 'asteroid' as const, surface: 'rock' as const, visual: { color: '#fff', albedo: 0.1 } }
          const t = makeBody({ ...spec, id: 't', name: 'T', mass: M, radius: R, seed, pos: { x: 0, y: 0, z: 0 }, vel: { x: 0, y: 0, z: 0 } })
          const i = makeBody({ ...spec, id: 'i', name: 'I', mass: M * 0.3, radius: R * 0.67, seed: seed + 99, pos: { x: R * 1.6, y: 0, z: 0 }, vel: { x: -speed, y: 0, z: 0 } })
          u.setBodies([t, i])
          const o = resolveCollision(u, { a: t, b: i, time: seed * 1000 }, { bodyFrame: (_x, d) => d })
          if (!o.fragments.length) continue
          shatters++
          const all = u.bodies
          for (let a = 0; a < all.length; a++) {
            for (let b = a + 1; b < all.length; b++) {
              expect(length(sub(all[a].pos, all[b].pos))).toBeGreaterThan(all[a].radius + all[b].radius)
            }
          }
        }
      }
    }
    expect(shatters).toBeGreaterThan(20)
  })

  it('starts a new undo point only after a quiet spell', () => {
    const u = new Universe(2451545)
    u.pushHistory('first')
    u.step(1)
    u.pushHistory('second')
    u.step(1)
    u.pushHistory('third')
    expect(u.history.map((h) => h.label)).toEqual(['first'])
    for (let i = 0; i < 40; i++) u.step(1)
    u.pushHistory('later')
    expect(u.history.map((h) => h.label)).toEqual(['first', 'later'])
  })

  it('auto-aim hits Earth from 0.05 AU away', () => {
    const { u } = build()
    const earth = u.byId('earth')!
    const sun = u.byId('sun')!
    const ahead = normalize(earth.vel)
    const from = add(earth.pos, scale(ahead, 0.05 * AU))
    const aim = autoAim(u, from, 'earth', 25, 0.5)
    expect(aim).toBeDefined()
    expect(aim!.converged).toBe(true)
    expect(aim!.prediction.hit!.bodyId).toBe('earth')
    expect(aim!.prediction.hit!.speed).toBeGreaterThan(25)
    expect(length(sub(from, sun.pos)) / AU).toBeGreaterThan(0.9)
  })
})

/** Two loose rocks on their own, so nothing else sets the substep. */
function rockPair(radiusKm: number, separationKm: number, closingKms: number) {
  const u = new Universe(2451545)
  const rho = 3000
  const mass = rho * (4 / 3) * Math.PI * Math.pow(radiusKm * 1000, 3)
  const a = makeBody({
    id: 'a', name: 'Rock A', kind: 'asteroid', surface: 'rock', mass, radius: radiusKm,
    pos: { x: 0, y: 0, z: 0 }, vel: { x: 0, y: 0, z: 0 }, visual: { color: '#fff', albedo: 0.1 },
  })
  const b = makeBody({
    id: 'b', name: 'Rock B', kind: 'asteroid', surface: 'rock', mass, radius: radiusKm,
    pos: { x: separationKm, y: 0, z: 0 }, vel: { x: -closingKms, y: 0, z: 0 }, visual: { color: '#fff', albedo: 0.1 },
  })
  u.setBodies([a, b])
  const outcomes: ImpactOutcome[] = []
  u.collisionHandlers.push((ev, uni) => { outcomes.push(resolveCollision(uni, ev, { bodyFrame: (_x, d) => d })) })
  return { u, a, b, outcomes }
}

describe('rock on rock', () => {
  it('catches a fast little asteroid hitting another one instead of letting it pass through', () => {
    // 10 m radius closing at 20 km/s: a substep is 5 km long, 250 times wider than the target.
    const { u, outcomes } = rockPair(0.01, 400, 20)
    for (let i = 0; i < 400 && outcomes.length === 0; i++) u.step(1)
    expect(outcomes.length).toBe(1)
    const o = outcomes[0]
    expect(o.relSpeed).toBeCloseTo(20, 3)
    // The contact normal comes from the closest approach, so it points along the approach line.
    expect(Math.abs(o.normalWorld.x)).toBeGreaterThan(0.9)
  })

  it('merges rocks that nudge each other and shatters ones that slam together', () => {
    const gentle = rockPair(0.01, 1, 0.001) // 1 m/s
    const each = gentle.a.mass
    for (let i = 0; i < 4000 && gentle.outcomes.length === 0; i++) gentle.u.step(1)
    expect(gentle.outcomes.length).toBe(1)
    expect(gentle.outcomes[0].fragments.length).toBe(0)
    expect(gentle.u.bodies.length).toBe(1)
    expect(gentle.u.bodies[0].mass / (2 * each)).toBeCloseTo(1, 9)

    const hard = rockPair(0.01, 20, 0.1) // 100 m/s, well past the strength limit for a 20 m boulder
    for (let i = 0; i < 4000 && hard.outcomes.length === 0; i++) hard.u.step(1)
    expect(hard.outcomes.length).toBe(1)
    expect(['shatter', 'disperse']).toContain(hard.outcomes[0].kind)
    expect(hard.outcomes[0].fragments.length).toBeGreaterThanOrEqual(6)
  })

  it('needs far more than self-gravity to break a boulder, and no more than before to break a planet', () => {
    const boulder = makeBody({
      id: 'r', name: 'r', kind: 'asteroid', surface: 'rock', mass: 1.26e7, radius: 0.01,
      visual: { color: '#fff', albedo: 0.1 },
    })
    expect(disruptionEnergy(boulder) / bindingEnergy(boulder)).toBeGreaterThan(1e5)
    const { u } = build()
    const earth = u.byId('earth')!
    // Gravity still rules at planet scale: the threshold is the old one, a fifth of the binding energy.
    expect(disruptionEnergy(earth) / (0.2 * bindingEnergy(earth))).toBeLessThan(1.001)
  })
})

describe('what a body orbits', () => {
  it('follows gravity rather than the catalogue', () => {
    const { u } = build()
    expect(u.primaryOf(u.byId('moon')!)?.id).toBe('earth')
    expect(u.primaryOf(u.byId('earth')!)?.id).toBe('sun')
    expect(u.primaryOf(u.byId('io')!)?.id).toBe('jupiter')
    expect(u.primaryOf(u.byId('charon')!)?.id).toBe('pluto')

    // A rock dropped into a circular orbit 5000 km above the Moon belongs to the Moon, not the Sun,
    // even though it was fired at Earth.
    const moon = u.byId('moon')!
    const r = 5000
    const speed = Math.sqrt((G * moon.mass) / r)
    const rock = makeBody({
      id: 'rock', name: 'Rock', kind: 'asteroid', surface: 'rock', mass: 1e9, radius: 0.05,
      pos: add(moon.pos, { x: r, y: 0, z: 0 }), vel: add(moon.vel, { x: 0, y: speed, z: 0 }),
      primaryId: 'earth', visual: { color: '#fff', albedo: 0.1 },
    })
    u.add(rock)
    expect(u.primaryOf(rock)?.id).toBe('moon')
  })
})
