import * as THREE from 'three'
import { ImpactOutcome } from '../sim/collide'
import { surfaceGravity } from '../sim/body'
import { Vec3, clamp, makeRandom } from '../sim/vec'
import { BodyView, ViewContext } from './viewContext'
import { EJECTA_VERT, EJECTA_FRAG, FIREBALL_FRAG, FIREBALL_VERT } from './shaders/misc'
import { SPHERE_VERT } from './shaders/common'
import { radialSprite } from './textures'

interface Effect {
  group: THREE.Group
  parent: THREE.Object3D
  t: number
  duration: number
  tick: (t: number, ctx: ViewContext) => void
  disposers: Array<() => void>
}

const smooth = (x: number) => {
  const t = clamp(x, 0, 1)
  return t * t * (3 - 2 * t)
}

/**
 * Most impact effects alive at once. A shatter's fragments go on hitting each other, and each hit
 * spawns one: uncapped, a cascade stacked dozens of them (9,500 GPU points and fresh shader
 * materials apiece), which is a GPU-memory crash waiting to happen. The oldest makes way.
 */
const MAX_EFFECTS = 8

/** Flash, fireball, blast ring, ballistic ejecta, debris and meteor streaks, parented to the struck body. */
export class ImpactEffects {
  private active: Effect[] = []
  private flashTex = radialSprite(256, 2.5)
  private ringGeo = new THREE.RingGeometry(0.86, 1, 64)
  private fireGeo = new THREE.SphereGeometry(1, 48, 24)

  spawn(o: ImpactOutcome, view: BodyView, velDirBody: Vec3 | null, ctx: ViewContext): void {
    while (this.active.length >= MAX_EFFECTS) this.retire(0)
    // Thin the particle clouds while others are still playing, so a cascade costs about as much as
    // a couple of lone impacts rather than one per hit.
    const density = 1 / (1 + this.active.length / 2)
    const body = o.target
    const Rm = body.radius * 1000
    const parent = view.group
    const camDist = parent.position.length()
    // Minimum on-screen sizes are re-evaluated every frame: the cinematic camera moves a lot right after impact.
    const minRadii = (px: number, c: ViewContext = ctx) => ((px * parent.position.length()) / c.pixelsPerRadian) / parent.scale.x
    const dir = new THREE.Vector3(o.dirBody.x, o.dirBody.y, o.dirBody.z).normalize()
    const group = new THREE.Group()
    parent.add(group)
    const ticks: Array<(t: number, ctx: ViewContext) => void> = []
    const disposers: Array<() => void> = []
    const E = o.report?.energyDeposited ?? o.energy
    const rf = Math.min(o.report ? o.report.fireballRadius / Rm : (0.002 * Math.cbrt(Math.max(E, 1))) / Rm, 0.35)
    const fireR = (c: ViewContext) => Math.max(rf, minRadii(4, c))
    const altitude = o.kind === 'airburst' && o.report ? o.report.entry.burstAltitude / Rm : 0
    const rand = makeRandom((body.seed ^ Math.floor(o.time)) >>> 0)
    const invQ = new THREE.Quaternion()
    const tmp = new THREE.Vector3()

    // Flash
    const flashMat = new THREE.SpriteMaterial({
      map: this.flashTex, color: new THREE.Color(1, 0.92, 0.78).multiplyScalar(6 * ctx.exposureComp),
      transparent: true, blending: THREE.AdditiveBlending, depthWrite: false, depthTest: false,
    })
    const flash = new THREE.Sprite(flashMat)
    flash.position.copy(dir).multiplyScalar(1.003 + altitude)
    group.add(flash)
    ticks.push((t, c) => {
      const a = t < 0.15 ? t / 0.15 : Math.max(0, 1 - (t - 0.15) / 1.4)
      const flashR = Math.max(fireR(c) * 6, minRadii(18, c))
      const s = flashR * 2 * (0.4 + 0.6 * smooth(t / 0.3))
      flash.scale.set(s, s, 1)
      flashMat.opacity = a
      flash.visible = a > 0
    })
    disposers.push(() => flashMat.dispose())

    // Fireball / plume
    if (o.kind !== 'absorb') {
      const dur = clamp(3 + Math.log10(Math.max(E, 1) / 1e15), 3, 9)
      const fmat = new THREE.ShaderMaterial({
        vertexShader: FIREBALL_VERT, fragmentShader: FIREBALL_FRAG,
        uniforms: { uT: { value: 0 }, uTime: { value: 0 }, uSeed: { value: rand() * 10 }, uViewPos: { value: new THREE.Vector3(0, 0, 3) } },
        transparent: true, depthWrite: false,
      })
      const fire = new THREE.Mesh(this.fireGeo, fmat)
      fire.renderOrder = 5
      group.add(fire)
      ticks.push((t, c) => {
        const tau = clamp(t / dur, 0, 1)
        fmat.uniforms.uT.value = tau
        fmat.uniforms.uTime.value = c.realTime
        const s = fireR(c) * (0.15 + 0.85 * smooth(tau / 0.25)) * (1 + 0.8 * tau)
        fire.scale.setScalar(s)
        fire.position.copy(dir).multiplyScalar(1 + altitude + s * 0.55 + 0.5 * tau * s)
        fire.visible = tau < 1
        invQ.copy(parent.quaternion).invert()
        tmp.copy(parent.position).negate().applyQuaternion(invQ).divideScalar(parent.scale.x).sub(fire.position).divideScalar(s)
        ;(fmat.uniforms.uViewPos.value as THREE.Vector3).copy(tmp)
      })
      disposers.push(() => fmat.dispose())
    }

    // Blast ring on the surface
    if (o.kind === 'crater' || o.kind === 'merge' || o.kind === 'airburst' || o.kind === 'gas') {
      const rmat = new THREE.MeshBasicMaterial({ color: new THREE.Color(2.5, 1.8, 1.2), transparent: true, blending: THREE.AdditiveBlending, depthWrite: false, side: THREE.DoubleSide })
      const ring = new THREE.Mesh(this.ringGeo, rmat)
      ring.quaternion.setFromUnitVectors(new THREE.Vector3(0, 0, 1), dir)
      ring.position.copy(dir).multiplyScalar(1.002)
      ring.renderOrder = 5
      group.add(ring)
      const strength = body.atmosphere ? 0.9 : 0.35
      ticks.push((t, c) => {
        const tau = clamp(t / 4, 0, 1)
        const r = Math.max(rf * (0.6 + 26 * tau), minRadii(3, c))
        ring.scale.setScalar(r)
        rmat.opacity = strength * (1 - tau) * (1 - tau)
        ring.visible = tau < 1
      })
      disposers.push(() => rmat.dispose())
    }

    // Ballistic ejecta curtain
    if ((o.kind === 'crater' || o.kind === 'merge') && o.report?.crater) {
      const N = Math.max(400, Math.round(3500 * density))
      const g = surfaceGravity(body)
      const gLocal = g / Rm
      const Rc = o.report.crater.finalDiameter / 2
      const vmax = Math.sqrt(10 * Rc * g)
      const vLocal = vmax / Rm
      const dirs = new Float32Array(N * 3)
      const speeds = new Float32Array(N)
      const seeds = new Float32Array(N)
      const t1 = new THREE.Vector3(1, 0, 0)
      if (Math.abs(dir.x) > 0.9) t1.set(0, 1, 0)
      t1.cross(dir).normalize()
      const t2 = new THREE.Vector3().crossVectors(dir, t1).normalize()
      const d = new THREE.Vector3()
      for (let i = 0; i < N; i++) {
        const theta = (28 + 34 * rand()) * (Math.PI / 180)
        const az = rand() * Math.PI * 2
        d.copy(dir).multiplyScalar(Math.cos(theta))
          .addScaledVector(t1, Math.sin(theta) * Math.cos(az))
          .addScaledVector(t2, Math.sin(theta) * Math.sin(az))
        dirs[i * 3] = d.x; dirs[i * 3 + 1] = d.y; dirs[i * 3 + 2] = d.z
        speeds[i] = vLocal * (0.25 + 0.75 * Math.pow(rand(), 0.7))
        seeds[i] = rand()
      }
      const geo = new THREE.BufferGeometry()
      geo.setAttribute('position', new THREE.BufferAttribute(new Float32Array(N * 3), 3))
      geo.setAttribute('aDir', new THREE.BufferAttribute(dirs, 3))
      geo.setAttribute('aSpeed', new THREE.BufferAttribute(speeds, 1))
      geo.setAttribute('aSeed', new THREE.BufferAttribute(seeds, 1))
      const tFlight = (2 * vmax * Math.sin(1.05)) / g
      const timeScale = tFlight / 6
      const emat = new THREE.ShaderMaterial({
        vertexShader: EJECTA_VERT, fragmentShader: EJECTA_FRAG,
        uniforms: { uT: { value: 0 }, uG: { value: gLocal }, uOrigin: { value: dir.clone() }, uPointScale: { value: 3 * camDist }, uMode: { value: 0 }, uLife: { value: 1 } },
        transparent: true, depthWrite: false,
      })
      const pts = new THREE.Points(geo, emat)
      pts.frustumCulled = false
      pts.renderOrder = 6
      group.add(pts)
      ticks.push((t) => {
        emat.uniforms.uT.value = t * timeScale
        emat.uniforms.uPointScale.value = 3 * parent.position.length()
        pts.visible = t < 8
      })
      disposers.push(() => { geo.dispose(); emat.dispose() })
    }

    // Free-flying debris cloud for shattering impacts
    if (o.kind === 'shatter' || o.kind === 'disperse') {
      const N = Math.max(600, Math.round(6000 * density))
      const dirs = new Float32Array(N * 3)
      const speeds = new Float32Array(N)
      const seeds = new Float32Array(N)
      for (let i = 0; i < N; i++) {
        const z = rand() * 2 - 1
        const th = rand() * Math.PI * 2
        const r = Math.sqrt(1 - z * z)
        dirs[i * 3] = r * Math.cos(th); dirs[i * 3 + 1] = r * Math.sin(th); dirs[i * 3 + 2] = z
        speeds[i] = 0.25 + 1.4 * Math.pow(rand(), 2)
        seeds[i] = rand()
      }
      const geo = new THREE.BufferGeometry()
      geo.setAttribute('position', new THREE.BufferAttribute(new Float32Array(N * 3), 3))
      geo.setAttribute('aDir', new THREE.BufferAttribute(dirs, 3))
      geo.setAttribute('aSpeed', new THREE.BufferAttribute(speeds, 1))
      geo.setAttribute('aSeed', new THREE.BufferAttribute(seeds, 1))
      const emat = new THREE.ShaderMaterial({
        vertexShader: EJECTA_VERT, fragmentShader: EJECTA_FRAG,
        uniforms: { uT: { value: 0 }, uG: { value: 1 }, uOrigin: { value: new THREE.Vector3() }, uPointScale: { value: 3 * camDist }, uMode: { value: 1 }, uLife: { value: 12 } },
        transparent: true, depthWrite: false,
      })
      const pts = new THREE.Points(geo, emat)
      pts.frustumCulled = false
      pts.renderOrder = 6
      group.add(pts)
      ticks.push((t) => {
        emat.uniforms.uT.value = t
        emat.uniforms.uPointScale.value = 3.5 * parent.position.length()
      })
      disposers.push(() => { geo.dispose(); emat.dispose() })
    }

    // Meteor streak through the atmosphere
    if (velDirBody && o.report && body.atmosphere && (o.kind === 'airburst' || o.kind === 'crater')) {
      const v = new THREE.Vector3(velDirBody.x, velDirBody.y, velDirBody.z).normalize()
      const top = 1 + 130e3 / Rm
      const dv = dir.dot(v)
      const L = dv + Math.sqrt(Math.max(dv * dv + top * top - 1, 0))
      const start = dir.clone().addScaledVector(v, -L)
      const endR = 1 + altitude
      const Lb = dv + Math.sqrt(Math.max(dv * dv + endR * endR - 1, 0))
      const end = dir.clone().addScaledVector(v, -Lb)
      const geo = new THREE.BufferGeometry().setFromPoints([start, end])
      const cols = new Float32Array([0.6, 0.6, 0.7, 6, 5, 3.5])
      geo.setAttribute('color', new THREE.BufferAttribute(cols, 3))
      const lmat = new THREE.LineBasicMaterial({ vertexColors: true, transparent: true, blending: THREE.AdditiveBlending, depthWrite: false })
      const line = new THREE.Line(geo, lmat)
      line.renderOrder = 5
      group.add(line)
      ticks.push((t) => {
        lmat.opacity = Math.max(0, 1 - t / 1.6)
        line.visible = t < 1.6
      })
      disposers.push(() => { geo.dispose(); lmat.dispose() })
    }

    this.active.push({
      group, parent, t: 0, duration: 13,
      tick: (t, c) => { for (const f of ticks) f(t, c) },
      disposers,
    })
  }

  update(dtReal: number, ctx: ViewContext): void {
    for (let i = this.active.length - 1; i >= 0; i--) {
      const e = this.active[i]
      e.t += dtReal
      if (e.t > e.duration || !e.parent.parent) {
        this.retire(i)
        continue
      }
      e.tick(e.t, ctx)
    }
  }

  private retire(i: number): void {
    const e = this.active[i]
    e.parent.remove(e.group)
    for (const d of e.disposers) d()
    this.active.splice(i, 1)
  }

  get count(): number {
    return this.active.length
  }
}
