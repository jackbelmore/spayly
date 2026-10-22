import * as THREE from 'three'
import { Body } from '../sim/body'
import { DEG, J2000_JD, OBLIQUITY } from '../sim/constants'
import { Vec3, makeRandom } from '../sim/vec'
import { dirToRender } from './units'

const X = new THREE.Vector3(1, 0, 0)
const Z = new THREE.Vector3(0, 0, 1)
/** Simulation ecliptic frame to render frame. */
const qToRender = new THREE.Quaternion().setFromAxisAngle(X, -Math.PI / 2)
/** ICRF equatorial to ecliptic. */
const qEps = new THREE.Quaternion().setFromAxisAngle(X, -OBLIQUITY)
/** Sphere geometry (pole +y, prime meridian +x) to the IAU body frame (pole +z, prime meridian +x). */
const qGeom = new THREE.Quaternion().setFromAxisAngle(X, Math.PI / 2)

const tmpA = new THREE.Quaternion()
const tmpB = new THREE.Quaternion()
const tmpC = new THREE.Quaternion()
const tmpV = new THREE.Vector3()
const tmpFrame = new THREE.Quaternion()
const tumbleAxes = new Map<string, { axis: THREE.Vector3; rate: number }>()

/**
 * Orientation of a body's mesh in the render frame at Julian day jd, following the IAU convention:
 * body-fixed to ICRF is Rz(alpha0 + 90) Rx(90 - delta0) Rz(W).
 */
export function bodyOrientation(b: Body, jd: number, out: THREE.Quaternion = new THREE.Quaternion()): THREE.Quaternion {
  const d = jd - J2000_JD
  const rot = b.rotation
  if (rot) {
    const W = (rot.W0 + rot.rate * d) * DEG
    out.copy(qToRender).multiply(qEps)
    out.multiply(tmpA.setFromAxisAngle(Z, (rot.poleRA + 90) * DEG))
    out.multiply(tmpB.setFromAxisAngle(X, (90 - rot.poleDec) * DEG))
    out.multiply(tmpC.setFromAxisAngle(Z, W))
    out.multiply(qGeom)
    return out
  }
  // Small bodies tumble about a seeded axis with a period of a few hours.
  let t = tumbleAxes.get(b.id)
  if (!t) {
    const rand = makeRandom(b.seed)
    const zz = rand() * 2 - 1
    const th = rand() * Math.PI * 2
    const rr = Math.sqrt(1 - zz * zz)
    t = { axis: new THREE.Vector3(rr * Math.cos(th), rr * Math.sin(th), zz), rate: (2 * Math.PI) / ((2 + rand() * 18) / 24) }
    tumbleAxes.set(b.id, t)
  }
  const bornDays = b.born / 86400
  out.copy(qToRender).multiply(tmpA.setFromAxisAngle(t.axis, (d - bornDays) * t.rate))
  return out
}

/** Convert a simulation-frame direction into the body's geometry frame (what the shaders and crater maps use). */
export function worldToBodyFrame(b: Body, dirSim: Vec3, jd: number): Vec3 {
  // tmpA/tmpB/tmpC are scratch inside bodyOrientation, so the result needs its own quaternion.
  const q = bodyOrientation(b, jd, tmpFrame).invert()
  const v = dirToRender(dirSim, tmpV).applyQuaternion(q)
  return { x: v.x, y: v.y, z: v.z }
}

/** Equirectangular UV of a geometry-frame direction, matching THREE.SphereGeometry. */
export function bodyDirToUv(dir: Vec3): { u: number; v: number } {
  const len = Math.hypot(dir.x, dir.y, dir.z) || 1
  const y = dir.y / len
  const theta = Math.acos(Math.min(1, Math.max(-1, y)))
  const phi = Math.atan2(dir.z, -dir.x)
  const u = ((phi / (2 * Math.PI)) % 1 + 1) % 1
  return { u, v: 1 - theta / Math.PI }
}
