import { describe, expect, it } from 'vitest'
import { createSolarSystem, MAJOR_MOON_RADIUS_KM } from '../src/sim/catalog'
import { SATELLITES, MINOR_BODIES } from '../src/sim/satellites'
import { Universe } from '../src/sim/universe'
import { AU, DAY, G } from '../src/sim/constants'
import { cross, length, normalize, sub } from '../src/sim/vec'
import { stateToElements } from '../src/sim/kepler'
import { bodyOrientation } from '../src/render/orientation'

function build(date = new Date('2026-09-22T00:00:00Z')) {
  const { bodies, epochJD } = createSolarSystem(date)
  const u = new Universe(epochJD)
  u.setBodies(bodies)
  return u
}

/** Inclination of a relative orbit to the ecliptic, degrees. */
function inclination(u: Universe, id: string, primaryId: string): number {
  const b = u.byId(id)!
  const p = u.byId(primaryId)!
  const h = normalize(cross(sub(b.pos, p.pos), sub(b.vel, p.vel)))
  return (Math.acos(Math.max(-1, Math.min(1, h.z))) * 180) / Math.PI
}

function orbit(u: Universe, id: string, primaryId: string) {
  const b = u.byId(id)!
  const p = u.byId(primaryId)!
  return stateToElements(G * (p.mass + b.mass), sub(b.pos, p.pos), sub(b.vel, p.vel))
}

describe('satellites', () => {
  it('gives every planet its moons and adds the dwarf planets', () => {
    const u = build()
    for (const s of SATELLITES) {
      expect(u.byId(s.id), s.name).toBeDefined()
      expect(u.byId(s.id)!.primaryId).toBe(s.primaryId)
    }
    for (const m of MINOR_BODIES) expect(u.byId(m.id), m.name).toBeDefined()
    expect(u.byId('pluto')).toBeDefined()
    expect(u.byId('charon')).toBeDefined()
    expect(u.bodies.length).toBe(9 + SATELLITES.length + MINOR_BODIES.length)
  })

  it('places every satellite on a bound orbit of the right size and period', () => {
    const u = build()
    for (const s of SATELLITES) {
      const el = orbit(u, s.id, s.primaryId)
      expect(el.e, `${s.name} eccentricity`).toBeLessThan(1)
      // Osculating elements wobble around the mean ones the table publishes.
      expect(Math.abs(el.a / s.a - 1), `${s.name} semi-major axis`).toBeLessThan(0.05)
      expect(Math.abs(el.period / (s.periodDays * DAY) - 1), `${s.name} period`).toBeLessThan(0.08)
    }
  })

  it('orients the moon orbits the way the sky does', () => {
    const u = build()
    // Published inclinations to the ecliptic; the mean elements are a degree or two loose.
    const expected: Array<[string, string, number]> = [
      ['moon', 'earth', 5.15],
      ['phobos', 'mars', 26.04],
      ['io', 'jupiter', 2.21],
      ['callisto', 'jupiter', 2.02],
      ['titan', 'saturn', 27.7],
      ['iapetus', 'saturn', 17.3],
      ['miranda', 'uranus', 81.5], // Uranus is tipped over; its moons follow its equator
      ['oberon', 'uranus', 82.3],
      ['triton', 'neptune', 129.6], // retrograde
      ['charon', 'pluto', 112.8],
    ]
    for (const [id, primary, deg] of expected) {
      expect(inclination(u, id, primary), id).toBeGreaterThan(deg - 2.5)
      expect(inclination(u, id, primary), id).toBeLessThan(deg + 2.5)
    }
  })

  it('turns each locked moon to face its planet', () => {
    const u = build()
    for (const s of SATELLITES) {
      if (!s.locked) continue
      const moon = u.byId(s.id)!
      if (!moon.rotation) continue
      const primary = u.byId(s.primaryId)!
      // The IAU prime meridian of a synchronous satellite points at its primary, so the body-frame
      // +x axis should line up with the direction to the planet.
      const q = bodyOrientation(moon, u.jd)
      const rel = normalize(sub(primary.pos, moon.pos))
      // Simulation (x, y, z) to render (x, z, -y), then into the body frame.
      const v = { x: rel.x, y: rel.z, z: -rel.y }
      const inv = q.clone().invert()
      const p = { x: v.x, y: v.y, z: v.z }
      const { x, y, z } = applyQuat(p, inv)
      // The geometry frame has the pole on +y and the prime meridian on +x.
      const lon = Math.atan2(z, x)
      expect(Math.abs(Math.hypot(x, z) > 0.2 ? lon : 0), `${s.name} sub-planetary longitude`).toBeLessThan(0.25)
      expect(y, `${s.name} sub-planetary latitude`).toBeLessThan(0.3)
    }
  })

  it('keeps the Galilean moons in their Laplace resonance for a month', () => {
    const u = build()
    const period = (id: string) => orbit(u, id, 'jupiter').period
    const before = [period('io'), period('europa'), period('ganymede')]
    for (let d = 0; d < 30; d++) u.step(DAY)
    const after = [period('io'), period('europa'), period('ganymede')]
    for (let i = 0; i < 3; i++) expect(Math.abs(after[i] / before[i] - 1)).toBeLessThan(0.02)
    expect(after[1] / after[0]).toBeGreaterThan(1.9)
    expect(after[1] / after[0]).toBeLessThan(2.1)
    expect(after[2] / after[1]).toBeGreaterThan(1.9)
    expect(after[2] / after[1]).toBeLessThan(2.1)
  })

  it('puts the dwarf planets where they belong', () => {
    const u = build()
    const sun = u.byId('sun')!
    const auFromSun = (id: string) => length(sub(u.byId(id)!.pos, sun.pos)) / AU
    expect(auFromSun('ceres')).toBeGreaterThan(2.5)
    expect(auFromSun('ceres')).toBeLessThan(3.0)
    expect(auFromSun('pluto')).toBeGreaterThan(29)
    expect(auFromSun('pluto')).toBeLessThan(50)
    expect(auFromSun('eris')).toBeGreaterThan(38)
    expect(orbit(u, 'pluto', 'sun').period / (365.25 * DAY)).toBeGreaterThan(240)
    expect(orbit(u, 'pluto', 'sun').period / (365.25 * DAY)).toBeLessThan(255)
  })

  it('counts Charon and the big moons as major', () => {
    const major = SATELLITES.filter((s) => s.radius >= MAJOR_MOON_RADIUS_KM).map((s) => s.id)
    expect(major).toContain('moon')
    expect(major).toContain('titan')
    expect(major).toContain('charon')
    expect(major).not.toContain('phobos')
    expect(major).not.toContain('mimas')
  })
})

function applyQuat(v: { x: number; y: number; z: number }, q: { x: number; y: number; z: number; w: number }) {
  const ix = q.w * v.x + q.y * v.z - q.z * v.y
  const iy = q.w * v.y + q.z * v.x - q.x * v.z
  const iz = q.w * v.z + q.x * v.y - q.y * v.x
  const iw = -q.x * v.x - q.y * v.y - q.z * v.z
  return {
    x: ix * q.w + iw * -q.x + iy * -q.z - iz * -q.y,
    y: iy * q.w + iw * -q.y + iz * -q.x - ix * -q.z,
    z: iz * q.w + iw * -q.z + ix * -q.y - iy * -q.x,
  }
}
