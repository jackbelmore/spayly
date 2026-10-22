import { describe, expect, it } from 'vitest'
import { impactReport, atmosphericEntry, TargetSpec } from '../src/sim/impact'

const EARTH: TargetSpec = {
  radius: 6.371e6, mass: 5.9722e24, gravity: 9.81, surface: 'rock', surfaceDensity: 2500, atmosphere: { rho0: 1.225, H: 8000 },
}
const MOON: TargetSpec = { radius: 1.7374e6, mass: 7.3458e22, gravity: 1.62, surface: 'rock', surfaceDensity: 2500 }

describe('impact model (Collins, Melosh & Marcus 2005)', () => {
  it('Chelyabinsk-class stone airbursts at tens of km', () => {
    const r = impactReport({ diameter: 19, density: 3300, speed: 19000, angle: (18 * Math.PI) / 180 }, EARTH)
    expect(r.entry.airburst).toBe(true)
    expect(r.entry.burstAltitude / 1000).toBeGreaterThan(15)
    expect(r.entry.burstAltitude / 1000).toBeLessThan(45)
    expect(r.crater).toBeUndefined()
    expect(r.energyMT).toBeGreaterThan(0.2)
    expect(r.energyMT).toBeLessThan(1.5)
    expect(r.airblast.glassRadius / 1000).toBeGreaterThan(10)
  })

  it('a 50 m iron reaches the ground and digs a Meteor-Crater-sized hole', () => {
    const r = impactReport({ diameter: 50, density: 7800, speed: 15000, angle: (45 * Math.PI) / 180 }, EARTH)
    expect(r.entry.airburst).toBe(false)
    expect(r.crater).toBeDefined()
    expect(r.crater!.finalDiameter / 1000).toBeGreaterThan(0.8)
    expect(r.crater!.finalDiameter / 1000).toBeLessThan(2.0)
    expect(r.crater!.complex).toBe(false)
  })

  it('a 10 km asteroid makes a Chicxulub-class complex crater', () => {
    const r = impactReport({ diameter: 10000, density: 3000, speed: 20000, angle: (45 * Math.PI) / 180 }, EARTH)
    expect(r.crater!.complex).toBe(true)
    expect(r.crater!.finalDiameter / 1000).toBeGreaterThan(90)
    expect(r.crater!.finalDiameter / 1000).toBeLessThan(200)
    expect(r.seismicMagnitude).toBeGreaterThan(9)
    expect(r.seismicMagnitude).toBeLessThan(11)
    expect(r.fireballRadius / 1000).toBeGreaterThan(80)
    expect(r.classification.toLowerCase()).toContain('extinction')
    expect(r.bindingRatio).toBeLessThan(1e-6)
  })

  it('has no atmospheric breakup on the Moon', () => {
    const e = atmosphericEntry({ diameter: 5, density: 3000, speed: 20000, angle: 0.8 }, MOON)
    expect(e.breakup).toBe(false)
    expect(e.surfaceSpeed).toBe(20000)
    const r = impactReport({ diameter: 5, density: 3000, speed: 20000, angle: 0.8 }, MOON)
    expect(r.crater).toBeDefined()
    expect(r.crater!.finalDiameter).toBeGreaterThan(50)
  })

  it('reports oceans boiling for a Ceres-class impact', () => {
    const r = impactReport({ diameter: 940000, density: 2100, speed: 30000, angle: 1.2 }, EARTH)
    expect(r.energy).toBeGreaterThan(4e27)
    expect(r.classification.toLowerCase()).toMatch(/steril|melt|shatter|disrupt/)
  })
})
