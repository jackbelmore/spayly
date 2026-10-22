import { AU, MT_TNT } from '../sim/constants'
import { dateFromJD } from '../sim/ephemeris'

const SUP = '⁰¹²³⁴⁵⁶⁷⁸⁹'
const sup = (n: number): string => {
  const s = String(Math.abs(n)).split('').map((c) => SUP[+c]).join('')
  return n < 0 ? '⁻' + s : s
}

/** 5.97 × 10²⁴ style. */
export function sci(x: number, digits = 3): string {
  if (x === 0) return '0'
  if (!isFinite(x)) return '∞'
  const e = Math.floor(Math.log10(Math.abs(x)))
  const m = x / Math.pow(10, e)
  return `${m.toFixed(digits - 1)} × 10${sup(e)}`
}

export function num(x: number, digits = 3): string {
  if (!isFinite(x)) return '∞'
  const a = Math.abs(x)
  if (a >= 1e6 || (a > 0 && a < 1e-3)) return sci(x, digits)
  if (a >= 100) return Math.round(x).toLocaleString()
  return x.toPrecision(digits).replace(/\.?0+$/, '')
}

export function mass(kg: number): string {
  const earth = kg / 5.9722e24
  const sun = kg / 1.98841e30
  let rel = ''
  if (sun >= 0.01) rel = ` (${num(sun)} M☉)`
  else if (earth >= 0.001) rel = ` (${num(earth)} M⊕)`
  else if (kg >= 1e15) rel = ` (${num(kg / 7.3458e22)} Moons)`
  return `${sci(kg)} kg${rel}`
}

export function distance(km: number): string {
  if (km >= 0.02 * AU) return `${num(km / AU)} AU`
  if (km >= 1e6) return `${num(km / 1e6)} million km`
  if (km >= 1) return `${Math.round(km).toLocaleString()} km`
  return `${Math.round(km * 1000)} m`
}

export function lengthM(m: number): string {
  if (m >= 1e6) return `${num(m / 1e6)} thousand km`
  if (m >= 1000) return `${num(m / 1000)} km`
  return `${num(m)} m`
}

export function speed(kms: number): string {
  if (kms < 0.001) return `${num(kms * 1e6)} mm/s`
  if (kms < 1) return `${num(kms * 1000)} m/s`
  return `${num(kms)} km/s`
}

export function duration(s: number): string {
  const a = Math.abs(s)
  if (!isFinite(a)) return '∞'
  if (a < 60) return `${num(s)} s`
  if (a < 3600) return `${num(s / 60)} min`
  if (a < 86400 * 2) return `${num(s / 3600)} h`
  if (a < 86400 * 365.25 * 2) return `${num(s / 86400)} days`
  return `${num(s / (86400 * 365.25))} years`
}

export function warp(w: number): string {
  if (w === 0) return 'paused'
  const a = Math.abs(w)
  const sign = w < 0 ? '◀ ' : ''
  if (a < 1.5) return `${sign}real time`
  if (a < 60) return `${sign}${num(a, 2)} s/s`
  if (a < 3600) return `${sign}${num(a / 60, 2)} min/s`
  if (a < 86400) return `${sign}${num(a / 3600, 2)} h/s`
  if (a < 86400 * 365.25) return `${sign}${num(a / 86400, 2)} days/s`
  return `${sign}${num(a / (86400 * 365.25), 2)} yr/s`
}

export function date(jd: number): string {
  const d = dateFromJD(jd)
  if (!isFinite(d.getTime())) return '—'
  const y = d.getUTCFullYear()
  const pad = (n: number) => String(n).padStart(2, '0')
  const yStr = y < 0 ? `${-y} BCE` : y > 9999 ? y.toLocaleString() : String(y)
  return `${yStr}-${pad(d.getUTCMonth() + 1)}-${pad(d.getUTCDate())} ${pad(d.getUTCHours())}:${pad(d.getUTCMinutes())} UTC`
}

export function energy(J: number): string {
  const mt = J / MT_TNT
  let tnt: string
  if (mt >= 1e6) tnt = `${num(mt / 1e6)} million Mt`
  else if (mt >= 1) tnt = `${num(mt)} Mt`
  else if (mt >= 1e-3) tnt = `${num(mt * 1000)} kt`
  else tnt = `${num(mt * 1e6)} t`
  return `${sci(J, 2)} J (${tnt} of TNT)`
}

export function temperature(K: number): string {
  return `${Math.round(K)} K (${Math.round(K - 273.15)} °C)`
}

export function angle(rad: number): string {
  return `${Math.round((rad * 180) / Math.PI)}°`
}
