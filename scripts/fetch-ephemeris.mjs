// Regenerates src/sim/satellites.ts from JPL's published tables.
//
//   node scripts/fetch-ephemeris.mjs
//
// Sources (all US Government, public domain):
//   https://ssd.jpl.nasa.gov/sats/elem/      mean orbital elements of the planetary satellites
//   https://ssd.jpl.nasa.gov/sats/phys_par/  GM and mean radius of the planetary satellites
//   https://ssd-api.jpl.nasa.gov/sbdb.api    osculating elements of the dwarf planets and asteroids
//
// The generated file is committed, so this only needs running when the numbers should be refreshed.
import { writeFile } from 'node:fs/promises'
import { join } from 'node:path'

const UA = 'spayly-ephemeris-fetch (https://github.com/jackbelmore/spayly)'
const OUT = join(process.cwd(), 'src', 'sim', 'satellites.ts')
const G_KM = 6.6743e-20 // km^3 kg^-1 s^-2, matching src/sim/constants.ts

/** Satellites to include, in display order, with the artistic bits the tables do not carry. */
const SATELLITES = [
  ['Moon', 'moon', 'earth', { color: '#c8c8c8', texture: 'moon.jpg', albedo: 0.11, baselineTemp: 250 }],
  ['Phobos', 'phobos', 'mars', { color: '#8d8377', texture: 'phobos.jpg', albedo: 0.071 }],
  ['Deimos', 'deimos', 'mars', { color: '#9a8f81', texture: 'deimos.jpg', albedo: 0.068 }],
  ['Io', 'io', 'jupiter', { color: '#e8d17a', texture: 'io.jpg', albedo: 0.63 }],
  ['Europa', 'europa', 'jupiter', { color: '#d8cdb8', texture: 'europa.jpg', albedo: 0.67, surface: 'ice' }],
  ['Ganymede', 'ganymede', 'jupiter', { color: '#9c8f7f', texture: 'ganymede.jpg', albedo: 0.43, surface: 'ice' }],
  ['Callisto', 'callisto', 'jupiter', { color: '#7d7264', texture: 'callisto.jpg', albedo: 0.22, surface: 'ice' }],
  ['Mimas', 'mimas', 'saturn', { color: '#c6c3bd', texture: 'mimas.jpg', albedo: 0.96, surface: 'ice' }],
  ['Enceladus', 'enceladus', 'saturn', { color: '#eef2f4', texture: 'enceladus.jpg', albedo: 0.99, surface: 'ice' }],
  ['Tethys', 'tethys', 'saturn', { color: '#d5d2c8', texture: 'tethys.jpg', albedo: 0.8, surface: 'ice' }],
  ['Dione', 'dione', 'saturn', { color: '#cfccc2', texture: 'dione.jpg', albedo: 0.998, surface: 'ice' }],
  ['Rhea', 'rhea', 'saturn', { color: '#cbc8be', texture: 'rhea.jpg', albedo: 0.949, surface: 'ice' }],
  ['Titan', 'titan', 'saturn', { color: '#e0a860', texture: 'titan.jpg', albedo: 0.22, baselineTemp: 94 }],
  ['Iapetus', 'iapetus', 'saturn', { color: '#a09684', texture: 'iapetus.jpg', albedo: 0.28, surface: 'ice' }],
  ['Miranda', 'miranda', 'uranus', { color: '#b9bdc0', texture: 'miranda.jpg', fillGaps: true, albedo: 0.32, surface: 'ice' }],
  ['Ariel', 'ariel', 'uranus', { color: '#c3c6c8', texture: 'ariel.jpg', fillGaps: true, albedo: 0.53, surface: 'ice' }],
  ['Umbriel', 'umbriel', 'uranus', { color: '#8e9091', texture: 'umbriel.jpg', fillGaps: true, albedo: 0.26, surface: 'ice' }],
  ['Titania', 'titania', 'uranus', { color: '#b3b0aa', texture: 'titania.jpg', fillGaps: true, albedo: 0.35, surface: 'ice' }],
  ['Oberon', 'oberon', 'uranus', { color: '#a8a49c', texture: 'oberon.jpg', fillGaps: true, albedo: 0.31, surface: 'ice' }],
  ['Triton', 'triton', 'neptune', { color: '#d8cfc4', texture: 'triton.jpg', fillGaps: true, albedo: 0.76, baselineTemp: 38, surface: 'ice' }],
  ['Proteus', 'proteus', 'neptune', { color: '#8a6f66', texture: 'phobos.jpg', tint: '#6f7276', roll: 0.61, albedo: 0.096, surface: 'ice' }],
  ['Nereid', 'nereid', 'neptune', { color: '#8a8782', texture: 'deimos.jpg', tint: '#8f8a84', roll: 0.18, albedo: 0.155, surface: 'ice', locked: false }],
  ['Charon', 'charon', 'pluto', { color: '#9c948c', texture: 'charon.jpg', fillGaps: true, albedo: 0.372, surface: 'ice' }],
]

/**
 * Heliocentric minor bodies, with the physical data the SBDB record lacks.
 * Rotation elements are the IAU WGCCRE 2015 report values; masses and radii for the trans-Neptunian
 * dwarf planets are the published occultation/thermal values (SBDB carries neither).
 */
const MINOR = [
  ['Ceres', 'ceres', { kind: 'planet', surface: 'rock', color: '#9a8f82', texture: 'ceres.jpg',
    rotation: { poleRA: 291.418, poleDec: 66.764, W0: 170.65, rate: 952.1532 } }],
  ['Pallas', 'pallas', { kind: 'asteroid', surface: 'rock', color: '#8a8378', texture: 'vesta.jpg', fillGaps: true, tint: '#7e8794', roll: 0.37,
    rotation: { poleRA: 33, poleDec: -3, W0: 38, rate: 1105.8036 } }],
  ['Vesta', 'vesta', { kind: 'asteroid', surface: 'rock', color: '#b0a894', texture: 'vesta.jpg', fillGaps: true,
    rotation: { poleRA: 309.031, poleDec: 42.235, W0: 285.39, rate: 1617.3329428 } }],
  ['Pluto', 'pluto', { kind: 'planet', surface: 'ice', color: '#c9ab8f', texture: 'pluto.jpg', baselineTemp: 44,
    radius: 1188.3, mass: 1.303e22, albedo: 0.52,
    rotation: { poleRA: 132.993, poleDec: -6.163, W0: 302.695, rate: 56.3625225 } }],
  ['Eris', 'eris', { kind: 'planet', surface: 'ice', color: '#ded6c8', texture: 'eris.jpg', baselineTemp: 30,
    radius: 1163, mass: 1.638e22, albedo: 0.96 }],
  ['Haumea', 'haumea', { kind: 'planet', surface: 'ice', color: '#ddd6cc', texture: 'haumea.jpg', baselineTemp: 32,
    radius: 798, mass: 4.006e21, albedo: 0.51 }],
  ['Makemake', 'makemake', { kind: 'planet', surface: 'ice', color: '#c9a68e', texture: 'makemake.jpg', baselineTemp: 32,
    radius: 715, mass: 3.1e21, albedo: 0.81 }],
]

const text = async (url) => {
  const res = await fetch(url, { headers: { 'user-agent': UA } })
  if (!res.ok) throw new Error(`${url}: HTTP ${res.status}`)
  return res.text()
}

const cells = (row) =>
  [...row.matchAll(/<t[hd][^>]*>([\s\S]*?)<\/t[hd]>/g)].map((m) =>
    m[1].replace(/<[^>]+>/g, '').replace(/&nbsp;| /g, ' ').replace(/\s+/g, ' ').trim())

function tableRows(html, index = 0) {
  const tables = html.match(/<table[\s\S]*?<\/table>/g) ?? []
  return (tables[index].match(/<tr[\s\S]*?<\/tr>/g) ?? []).map(cells)
}

const num = (s) => {
  const v = parseFloat(String(s).replace(/[^0-9eE+.\-]/g, ''))
  return Number.isFinite(v) ? v : undefined
}

async function satelliteElements() {
  const rows = tableRows(await text('https://ssd.jpl.nasa.gov/sats/elem/'), 0)
  const by = new Map()
  for (const c of rows) {
    // ID | Planet | Satellite | Code | Ephemeris | Frame | Epoch | a | e | w | M | i | node | P | Papsis | Pnode | RA | Dec | Tilt | Ref
    if (c.length < 16 || !/^\d+$/.test(c[0]) || by.has(c[2])) continue
    by.set(c[2], {
      primary: c[1].toLowerCase(), frame: c[5].toLowerCase(), epoch: c[6],
      a: num(c[7]), e: num(c[8]), peri: num(c[9]), M: num(c[10]), incl: num(c[11]), node: num(c[12]),
      periodDays: num(c[13]), apsisYr: num(c[14]), nodeYr: num(c[15]),
      poleRA: num(c[16]), poleDec: num(c[17]),
    })
  }
  return by
}

async function satellitePhysical() {
  const rows = tableRows(await text('https://ssd.jpl.nasa.gov/sats/phys_par/'), 0)
  const by = new Map()
  for (const c of rows) {
    // Planet | Satellite | Code | "GM sigma ref" | "radius sigma ref" | "density sigma ref"
    if (c.length < 6 || by.has(c[1])) continue
    const gm = num(c[3].split(' ')[0])
    const radius = num(c[4].split(' ')[0])
    if (gm === undefined || radius === undefined) continue
    by.set(c[1], { gm, radius, density: num(c[5].split(' ')[0]) })
  }
  return by
}

async function sbdb(name) {
  const url = `https://ssd-api.jpl.nasa.gov/sbdb.api?sstr=${encodeURIComponent(name)}&full-prec=true&phys-par=true`
  const d = await (await fetch(url, { headers: { 'user-agent': UA } })).json()
  const el = Object.fromEntries(d.orbit.elements.map((e) => [e.name, parseFloat(e.value)]))
  const ph = Object.fromEntries((d.phys_par ?? []).map((p) => [p.name, parseFloat(p.value)]))
  return { epochJD: parseFloat(d.orbit.epoch), el, ph }
}

/** JPL epochs in the satellite table read "2000-01-01.5"; convert to a Julian day. */
function epochToJD(s) {
  const m = /^(\d{4})-(\d{2})-(\d{2})(\.\d+)?$/.exec(s.trim())
  if (!m) throw new Error(`unparsed epoch ${s}`)
  const ms = Date.UTC(+m[1], +m[2] - 1, +m[3])
  return ms / 86400000 + 2440587.5 + (m[4] ? parseFloat(m[4]) : 0)
}

const r6 = (x) => (x === undefined ? undefined : Number(x.toPrecision(9)))

const elements = await satelliteElements()
const physical = await satellitePhysical()

const sats = []
for (const [name, id, primaryId, vis] of SATELLITES) {
  const el = elements.get(name)
  const ph = physical.get(name)
  if (!el) throw new Error(`no elements for ${name}`)
  if (!ph) throw new Error(`no physical data for ${name}`)
  const mass = ph.gm > 0 ? ph.gm / G_KM : (vis.density ?? 1500) * (4 / 3) * Math.PI * Math.pow(ph.radius * 1000, 3)
  sats.push({
    id, name, primaryId,
    frame: el.frame === 'ecliptic' ? 'ecliptic' : 'pole',
    poleRA: r6(el.poleRA), poleDec: r6(el.poleDec),
    epochJD: epochToJD(el.epoch),
    a: r6(el.a), e: r6(el.e), incl: r6(el.incl), node: r6(el.node), peri: r6(el.peri), M: r6(el.M),
    periodDays: r6(el.periodDays), nodeYr: r6(el.nodeYr) || undefined, apsisYr: r6(el.apsisYr) || undefined,
    mass: r6(mass), radius: r6(ph.radius),
    locked: vis.locked !== false,
    surface: vis.surface ?? 'rock',
    visual: vis,
  })
}

const minors = []
for (const [name, id, vis] of MINOR) {
  const { epochJD, el, ph } = await sbdb(name)
  const mass = vis.mass ?? (ph.GM !== undefined ? ph.GM / G_KM : undefined)
  const radius = vis.radius ?? (ph.diameter !== undefined ? ph.diameter / 2 : undefined)
  if (mass === undefined || radius === undefined) throw new Error(`missing mass/radius for ${name}`)
  minors.push({
    id, name, epochJD, albedo: vis.albedo ?? ph.albedo,
    a: r6(el.a * 149597870.7), e: r6(el.e), incl: r6(el.i), node: r6(el.om), peri: r6(el.w), M: r6(el.ma),
    nDegPerDay: r6(el.n),
    mass: r6(mass), radius: r6(radius),
    kind: vis.kind, surface: vis.surface,
    rotationPeriodHours: vis.rotation ? undefined : r6(ph.rot_per),
    visual: vis,
  })
}

const j = (o) => JSON.stringify(o)
const visualOf = (v) => {
  const parts = [`color: ${j(v.color)}`]
  if (v.texture) parts.push(`texture: ${j(v.texture)}`)
  if (v.fillGaps) parts.push('fillGaps: true')
  if (v.tint) parts.push(`tint: ${j(v.tint)}`)
  if (v.roll) parts.push(`roll: ${v.roll}`)
  parts.push(`albedo: ${v.albedo ?? 0.1}`)
  if (v.baselineTemp !== undefined) parts.push(`baselineTemp: ${v.baselineTemp}`)
  return `{ ${parts.join(', ')} }`
}

const satLine = (s) => {
  const p = [`id: ${j(s.id)}`, `name: ${j(s.name)}`, `primaryId: ${j(s.primaryId)}`, `frame: ${j(s.frame)}`]
  if (s.frame === 'pole' && s.poleRA !== undefined) p.push(`poleRA: ${s.poleRA}`, `poleDec: ${s.poleDec}`)
  p.push(`epochJD: ${s.epochJD}`, `a: ${s.a}`, `e: ${s.e}`, `incl: ${s.incl}`, `node: ${s.node}`, `peri: ${s.peri}`, `M: ${s.M}`,
    `periodDays: ${s.periodDays}`)
  if (s.nodeYr) p.push(`nodeYr: ${s.nodeYr}`)
  if (s.apsisYr) p.push(`apsisYr: ${s.apsisYr}`)
  p.push(`mass: ${s.mass}`, `radius: ${s.radius}`, `surface: ${j(s.surface)}`, `locked: ${s.locked}`,
    `visual: ${visualOf(s.visual)}`)
  return `  { ${p.join(', ')} },`
}

const minorLine = (m) => {
  const p = [`id: ${j(m.id)}`, `name: ${j(m.name)}`, `kind: ${j(m.kind)}`, `surface: ${j(m.surface)}`,
    `epochJD: ${m.epochJD}`, `a: ${m.a}`, `e: ${m.e}`, `incl: ${m.incl}`, `node: ${m.node}`, `peri: ${m.peri}`,
    `M: ${m.M}`, `nDegPerDay: ${m.nDegPerDay}`, `mass: ${m.mass}`, `radius: ${m.radius}`]
  if (m.visual.rotation) {
    const r = m.visual.rotation
    p.push(`rotation: { poleRA: ${r.poleRA}, poleDec: ${r.poleDec}, W0: ${r.W0}, rate: ${r.rate} }`)
  } else if (m.rotationPeriodHours) {
    p.push(`rotationPeriodHours: ${m.rotationPeriodHours}`)
  }
  p.push(`visual: ${visualOf({ ...m.visual, albedo: m.albedo })}`)
  return `  { ${p.join(', ')} },`
}

const out = `// GENERATED by scripts/fetch-ephemeris.mjs — do not edit by hand.
//
// Satellite mean elements and GM/radius: JPL Solar System Dynamics,
//   https://ssd.jpl.nasa.gov/sats/elem/ and https://ssd.jpl.nasa.gov/sats/phys_par/
// Dwarf-planet and asteroid elements: JPL Small-Body Database, https://ssd-api.jpl.nasa.gov/doc/sbdb.html
// Both are US Government works and in the public domain.
//
// 'frame' says what the angles are referred to: 'ecliptic' is ecliptic J2000; 'pole' is the plane
// normal to (poleRA, poleDec) — JPL's local Laplace plane, or the planet's IAU equator when the table
// says "equatorial", in which case the pole comes from CATALOG. In both cases the node is measured
// from the ascending node of that plane on the ICRF equator.
import { SatelliteElements, MinorBodyElements } from './satelliteTypes'

export const SATELLITES: SatelliteElements[] = [
${sats.map(satLine).join('\n')}
]

export const MINOR_BODIES: MinorBodyElements[] = [
${minors.map(minorLine).join('\n')}
]
`

await writeFile(OUT, out)
console.log(`wrote ${OUT}: ${sats.length} satellites, ${minors.length} minor bodies`)
