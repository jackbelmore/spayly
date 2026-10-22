import { BodyVisual, Surface } from './body'

/**
 * Mean orbital elements of a natural satellite, as JPL publishes them.
 * Angles in degrees, `a` in km, epoch as a Julian day.
 */
export interface SatelliteElements {
  id: string
  name: string
  primaryId: string
  /**
   * 'ecliptic' — angles referred to the ecliptic J2000.
   * 'pole' — referred to the plane normal to (poleRA, poleDec); when those are absent the primary's
   * IAU pole is used. The node is measured from that plane's ascending node on the ICRF equator.
   */
  frame: 'ecliptic' | 'pole'
  poleRA?: number
  poleDec?: number
  epochJD: number
  a: number
  e: number
  incl: number
  node: number
  peri: number
  M: number
  periodDays: number
  /** Nodal regression period, years (absent when the node does not precess measurably). */
  nodeYr?: number
  /** Apsidal precession period, years. */
  apsisYr?: number
  mass: number
  radius: number
  surface: Surface
  /** Tidally locked: one face stays turned towards the primary. */
  locked: boolean
  visual: BodyVisual
}

/** Heliocentric osculating elements of a dwarf planet or asteroid (JPL Small-Body Database). */
export interface MinorBodyElements {
  id: string
  name: string
  kind: 'planet' | 'asteroid'
  surface: Surface
  epochJD: number
  a: number
  e: number
  incl: number
  node: number
  peri: number
  M: number
  nDegPerDay: number
  mass: number
  radius: number
  /** IAU rotation elements when they are published for this body. */
  rotation?: { poleRA: number; poleDec: number; W0: number; rate: number }
  /** Otherwise the measured rotation period, used with an arbitrary upright pole. */
  rotationPeriodHours?: number
  visual: BodyVisual
}
