import * as THREE from 'three'
import { Vec3 } from '../sim/vec'

/** Render units per km: 1 unit = 1000 km. Everything is rendered relative to the camera (floating origin). */
export const RENDER_SCALE = 1e-3

/** Simulation frame (ecliptic, z north) to render frame (y up): (x, y, z) -> (x, z, -y). */
export function toRender(v: Vec3, origin: Vec3, out: THREE.Vector3 = new THREE.Vector3()): THREE.Vector3 {
  return out.set((v.x - origin.x) * RENDER_SCALE, (v.z - origin.z) * RENDER_SCALE, -(v.y - origin.y) * RENDER_SCALE)
}

export function dirToRender(v: Vec3, out: THREE.Vector3 = new THREE.Vector3()): THREE.Vector3 {
  return out.set(v.x, v.z, -v.y)
}

export function renderToSim(v: THREE.Vector3, origin: Vec3): Vec3 {
  return { x: origin.x + v.x / RENDER_SCALE, y: origin.y - v.z / RENDER_SCALE, z: origin.z + v.y / RENDER_SCALE }
}

export function renderDirToSim(v: THREE.Vector3): Vec3 {
  return { x: v.x, y: -v.z, z: v.y }
}

/** Approximate blackbody colour (linear RGB, normalised so the brightest channel is 1). */
export function blackbody(tempK: number): THREE.Color {
  const t = Math.min(Math.max(tempK, 1000), 40000) / 100
  let r: number, g: number, b: number
  if (t <= 66) {
    r = 255
    g = 99.4708025861 * Math.log(t) - 161.1195681661
    b = t <= 19 ? 0 : 138.5177312231 * Math.log(t - 10) - 305.0447927307
  } else {
    r = 329.698727446 * Math.pow(t - 60, -0.1332047592)
    g = 288.1221695283 * Math.pow(t - 60, -0.0755148492)
    b = 255
  }
  const c = new THREE.Color(clamp01(r / 255), clamp01(g / 255), clamp01(b / 255))
  // convert the sRGB-ish estimate to linear
  c.convertSRGBToLinear()
  const m = Math.max(c.r, c.g, c.b) || 1
  return c.multiplyScalar(1 / m)
}

const clamp01 = (x: number) => Math.min(1, Math.max(0, x))
