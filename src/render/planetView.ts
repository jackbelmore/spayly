import * as THREE from 'three'
import { CSS2DObject } from 'three/addons/renderers/CSS2DRenderer.js'
import { Body, Crater } from '../sim/body'
import { STEFAN_BOLTZMANN } from '../sim/constants'
import { sub, length, clamp } from '../sim/vec'
import { bodyOrientation, bodyDirToUv } from './orientation'
import { RENDER_SCALE, toRender } from './units'
import { SPHERE_VERT } from './shaders/common'
import { PLANET_FRAG, CLOUD_FRAG, RING_FRAG } from './shaders/planet'
import { ATMOSPHERE_FRAG } from './shaders/atmosphere'
import { loadTexture, proceduralPlanet, proceduralRingStrip, radialSprite, swapTexture, touchTexture, whiteTexture } from './textures'
import { BodyActions, BodyView, MapStream, ViewContext, decay, irradiance, makeLabel, occludersFor, onScreen, placeLabel, visualRadius } from './viewContext'

const sphereGeo = new THREE.SphereGeometry(1, 128, 64)
let glowTex: THREE.Texture | null = null
let whiteTex: THREE.Texture | null = null
let greyTex: THREE.Texture | null = null

function grey(): THREE.Texture {
  if (!greyTex) {
    const c = document.createElement('canvas')
    c.width = 4
    c.height = 4
    const ctx = c.getContext('2d')!
    ctx.fillStyle = 'rgb(128,128,128)'
    ctx.fillRect(0, 0, 4, 4)
    greyTex = new THREE.CanvasTexture(c)
    greyTex.colorSpace = THREE.NoColorSpace
  }
  return greyTex
}

/** Equilibrium temperature (K) of a body at distance d (km) from a star of luminosity L (W). */
export function equilibriumTemperature(L: number, dKm: number, albedo: number): number {
  const d = Math.max(dKm, 1) * 1000
  return Math.pow((L * (1 - albedo)) / (16 * Math.PI * STEFAN_BOLTZMANN * d * d), 0.25)
}

export class PlanetView implements BodyView {
  body: Body
  group = new THREE.Group()
  surface: THREE.Mesh
  clouds?: THREE.Mesh
  atmosphere?: THREE.Mesh
  ring?: THREE.Mesh
  glow: THREE.Sprite
  label: CSS2DObject
  pickMesh: THREE.Object3D
  visualRadiusKm = 0
  /** Estimated mean surface temperature, K (baseline plus the change in equilibrium temperature). */
  surfaceTemp?: number
  equilibriumTemp?: number
  private mat: THREE.ShaderMaterial
  private cloudMat?: THREE.ShaderMaterial
  private atmoMat?: THREE.ShaderMaterial
  private ringMat?: THREE.ShaderMaterial
  private damageCanvas?: HTMLCanvasElement
  private damageTex?: THREE.CanvasTexture
  private paintedVersion = -1
  private occ = [new THREE.Vector4(), new THREE.Vector4(), new THREE.Vector4(), new THREE.Vector4()]
  private heat = Array.from({ length: 12 }, () => new THREE.Vector4())
  private heatGlow = new Float32Array(12)
  private invQ = new THREE.Quaternion()
  private tmp = new THREE.Vector3()
  private baselineTeq?: number
  private cloudUniforms?: Record<string, THREE.IUniform>
  private stream = new MapStream()
  private ringUniforms?: Record<string, THREE.IUniform>
  private cloudDrift = (2 * Math.PI * 0.065) / 86400

  constructor(body: Body, actions: BodyActions) {
    this.body = body
    if (!glowTex) glowTex = radialSprite(128, 2.2)
    if (!whiteTex) whiteTex = whiteTexture()
    const vis = body.visual
    const base = new THREE.Color(vis.color)
    const fallback = proceduralPlanet(body.seed, base, body.surface === 'gas' ? 'gas' : body.surface === 'ice' ? 'ice' : 'rock')
    const u: Record<string, THREE.IUniform> = {
      uDay: { value: fallback }, uNight: { value: whiteTex }, uSpecular: { value: whiteTex }, uNormal: { value: whiteTex },
      uClouds: { value: whiteTex }, uDamage: { value: grey() }, uRing: { value: whiteTex },
      uHasNight: { value: 0 }, uHasSpecular: { value: 0 }, uHasNormal: { value: 0 }, uHasClouds: { value: 0 }, uHasRing: { value: 0 },
      uCloudShift: { value: 0 }, uSunDir: { value: new THREE.Vector3(0, 0, 1) }, uSunColor: { value: new THREE.Color(1, 1, 1) },
      uSunIntensity: { value: 1 }, uViewPos: { value: new THREE.Vector3(0, 0, 3) },
      uOccluders: { value: this.occ }, uOccluderCount: { value: 0 }, uSunAngular: { value: 0.0047 },
      uHeat: { value: this.heat }, uHeatGlow: { value: this.heatGlow }, uHeatCount: { value: 0 },
      uMagma: { value: 0 }, uClimate: { value: 0 }, uTime: { value: 0 }, uGas: { value: body.surface === 'gas' ? 1 : 0 },
      uRingInner: { value: 0 }, uRingOuter: { value: 0 },
      uBetaR: { value: new THREE.Vector3() }, uBetaM: { value: 0 }, uHR: { value: 0.001 }, uHM: { value: 0.001 }, uAtmoOn: { value: 0 }, uDust: { value: 0 },
    }
    this.mat = new THREE.ShaderMaterial({ vertexShader: SPHERE_VERT, fragmentShader: PLANET_FRAG, uniforms: u })
    this.surface = new THREE.Mesh(sphereGeo, this.mat)
    this.surface.userData.body = body
    this.pickMesh = this.surface
    this.group.add(this.surface)

    if (vis.clouds) {
      const cu: Record<string, THREE.IUniform> = {
        uClouds: { value: whiteTex }, uSunDir: u.uSunDir, uSunColor: u.uSunColor, uSunIntensity: u.uSunIntensity,
        uOccluders: u.uOccluders, uOccluderCount: u.uOccluderCount, uSunAngular: u.uSunAngular, uDust: u.uDust,
      }
      this.cloudMat = new THREE.ShaderMaterial({ vertexShader: SPHERE_VERT, fragmentShader: CLOUD_FRAG, uniforms: cu, transparent: true, depthWrite: false })
      this.clouds = new THREE.Mesh(sphereGeo, this.cloudMat)
      this.clouds.scale.setScalar(1.0045)
      this.clouds.renderOrder = 1
      this.clouds.visible = false
      this.group.add(this.clouds)
      this.cloudUniforms = cu
    }

    if (vis.atmosphere) {
      const a = vis.atmosphere
      const au: Record<string, THREE.IUniform> = {
        uSunDir: u.uSunDir, uViewPos: { value: new THREE.Vector3() }, uAtmR: { value: 1 + a.thickness },
        uBetaR: u.uBetaR, uBetaM: u.uBetaM, uHR: u.uHR, uHM: u.uHM, uG: { value: a.g },
        uSunColor: u.uSunColor, uSunIntensity: u.uSunIntensity, uStrength: { value: a.strength }, uDust: u.uDust,
        uDustColor: { value: new THREE.Color(0.62, 0.45, 0.3) },
        uOccluders: u.uOccluders, uOccluderCount: u.uOccluderCount, uSunAngular: u.uSunAngular,
      }
      this.atmoMat = new THREE.ShaderMaterial({
        vertexShader: SPHERE_VERT, fragmentShader: ATMOSPHERE_FRAG, uniforms: au, transparent: true, depthWrite: false, blending: THREE.AdditiveBlending, side: THREE.DoubleSide,
      })
      this.atmosphere = new THREE.Mesh(sphereGeo, this.atmoMat)
      this.atmosphere.scale.setScalar(1 + a.thickness)
      this.atmosphere.renderOrder = 2
      this.group.add(this.atmosphere)
      u.uAtmoOn.value = 1
    }

    if (vis.ring) {
      const r = vis.ring
      const inner = r.inner / body.radius
      const outer = r.outer / body.radius
      const geo = new THREE.RingGeometry(inner, outer, 256, 2)
      geo.rotateX(-Math.PI / 2)
      const ru: Record<string, THREE.IUniform> = {
        uRing: { value: proceduralRingStrip(body.seed) }, uInner: { value: inner }, uOuter: { value: outer },
        uSunDir: u.uSunDir, uSunColor: u.uSunColor, uSunIntensity: u.uSunIntensity, uViewPos: u.uViewPos, uSunAngular: u.uSunAngular,
      }
      this.ringMat = new THREE.ShaderMaterial({ vertexShader: SPHERE_VERT, fragmentShader: RING_FRAG, uniforms: ru, transparent: true, depthWrite: false, side: THREE.DoubleSide })
      this.ring = new THREE.Mesh(geo, this.ringMat)
      this.ring.renderOrder = 1
      this.group.add(this.ring)
      u.uRing.value = ru.uRing.value
      u.uHasRing.value = 1
      u.uRingInner.value = inner
      u.uRingOuter.value = outer
      this.ringUniforms = ru
    }

    this.glow = new THREE.Sprite(new THREE.SpriteMaterial({ map: glowTex, color: base, transparent: true, blending: THREE.AdditiveBlending, depthWrite: false }))
    this.glow.renderOrder = 3
    this.group.add(this.glow)
    this.label = makeLabel(body, actions)
    this.group.add(this.label)
  }

  /**
   * Request every map this body uses and point the uniforms at the results. Driven from update() on
   * a timer, so the view keeps its climate baseline and painted craters rather than being rebuilt,
   * and so the loader's sweep can tell a map that is on screen from one that has been left behind.
   */
  private async loadSurfaceMaps(ticket: number): Promise<void> {
    const vis = this.body.visual
    const u = this.mat.uniforms
    const cu = this.cloudUniforms
    const ru = this.ringUniforms
    const maxSize = this.stream.width
    const lands = (slot: string, t: THREE.Texture | null): t is THREE.Texture => !!t && this.stream.accept(slot, ticket)
    await Promise.all([
      vis.texture && loadTexture(vis.texture, { maxSize, fillGaps: vis.fillGaps, tint: vis.tint, roll: vis.roll })
        .then((t) => { if (lands('day', t)) u.uDay.value = swapTexture(u.uDay.value, t) }),
      vis.night && loadTexture(vis.night, { maxSize }).then((t) => { if (lands('night', t)) { u.uNight.value = swapTexture(u.uNight.value, t); u.uHasNight.value = 1 } }),
      vis.specular && loadTexture(vis.specular, { maxSize, srgb: false })
        .then((t) => { if (lands('specular', t)) { u.uSpecular.value = swapTexture(u.uSpecular.value, t); u.uHasSpecular.value = 1 } }),
      vis.normal && loadTexture(vis.normal, { maxSize, srgb: false })
        .then((t) => { if (lands('normal', t)) { u.uNormal.value = swapTexture(u.uNormal.value, t); u.uHasNormal.value = 1 } }),
      vis.clouds && cu && loadTexture(vis.clouds, { maxSize, srgb: false }).then((t) => {
        if (lands('clouds', t)) { cu.uClouds.value = swapTexture(cu.uClouds.value, t); u.uClouds.value = t; u.uHasClouds.value = 1; if (this.clouds) this.clouds.visible = true }
      }),
      vis.ring?.texture && ru && loadTexture(vis.ring.texture, { maxSize, clamp: true }).then((t) => {
        if (lands('ring', t)) { ru.uRing.value = swapTexture(ru.uRing.value, t); u.uRing.value = t }
      }),
    ])
  }

  /** Keep whatever is bound now alive in the loader while a replacement is on its way. */
  private touchMaps(): void {
    const u = this.mat.uniforms
    touchTexture(u.uDay.value)
    touchTexture(u.uNight.value)
    touchTexture(u.uSpecular.value)
    touchTexture(u.uNormal.value)
    touchTexture(u.uClouds.value)
    touchTexture(u.uRing.value)
  }

  update(ctx: ViewContext): void {
    const body = this.body
    const g = this.group
    const u = this.mat.uniforms
    toRender(body.pos, ctx.origin, g.position)
    const dist = g.position.length()
    const R = visualRadius(ctx, body, dist)
    g.scale.setScalar(R)
    this.visualRadiusKm = R / RENDER_SCALE
    bodyOrientation(body, ctx.jd, g.quaternion)
    this.invQ.copy(g.quaternion).invert()

    const sunRel = sub(ctx.sunPos, body.pos)
    const dSun = length(sunRel)
    this.tmp.set(sunRel.x, sunRel.z, -sunRel.y).normalize().applyQuaternion(this.invQ)
    ;(u.uSunDir.value as THREE.Vector3).copy(this.tmp)
    ;(u.uSunColor.value as THREE.Color).copy(ctx.sunColor)
    u.uSunIntensity.value = irradiance(ctx, body)
    u.uSunAngular.value = ctx.sunRadiusKm / Math.max(dSun, 1)
    ;(u.uViewPos.value as THREE.Vector3).copy(g.position).multiplyScalar(-1).applyQuaternion(this.invQ).multiplyScalar(1 / R)
    u.uOccluderCount.value = occludersFor(ctx, body, this.invQ, this.occ)

    const d = body.damage
    const t = ctx.simTime
    let n = 0
    for (let i = d.heat.length - 1; i >= 0 && n < 12; i--) {
      const h = d.heat[i]
      const glow = Math.exp(-Math.max(0, t - h.time) / h.tau)
      if (glow < 0.02) continue
      this.heat[n].set(h.dir.x, h.dir.y, h.dir.z, h.angularRadius)
      this.heatGlow[n] = glow
      n++
    }
    u.uHeatCount.value = n
    u.uMagma.value = decay(d.magma, d.magmaTime, d.magmaTau, t)
    u.uDust.value = decay(d.dust, d.dustTime, d.dustTau, t, 12 * 3600)
    if (d.version !== this.paintedVersion) this.paintDamage()

    const Teq = equilibriumTemperature(ctx.sunWatts, dSun, body.visual.albedo)
    if (this.baselineTeq === undefined) this.baselineTeq = Teq
    this.equilibriumTemp = Teq
    if (body.visual.baselineTemp !== undefined) {
      u.uClimate.value = clamp((Teq - this.baselineTeq) / 45, -1, 1)
      this.surfaceTemp = body.visual.baselineTemp + (Teq - this.baselineTeq)
    }
    u.uTime.value = ctx.realTime

    if (this.clouds) {
      const ang = ctx.simTime * this.cloudDrift
      this.clouds.rotation.y = ang
      u.uCloudShift.value = ang / (2 * Math.PI)
    }
    if (this.atmoMat) {
      const a = body.visual.atmosphere!
      const Rm = body.radius * 1000
      ;(u.uBetaR.value as THREE.Vector3).set(a.rayleigh[0] * Rm, a.rayleigh[1] * Rm, a.rayleigh[2] * Rm)
      u.uBetaM.value = a.mie * Rm
      u.uHR.value = a.rayleighH / Rm
      u.uHM.value = a.mieH / Rm
      ;(this.atmoMat.uniforms.uViewPos.value as THREE.Vector3).copy(u.uViewPos.value as THREE.Vector3)
    }

    const pixR = (R / Math.max(dist, 1e-9)) * ctx.pixelsPerRadian
    const glowMat = this.glow.material as THREE.SpriteMaterial
    glowMat.opacity = clamp((16 - pixR) / 12, 0, 1) * 0.9
    this.glow.visible = glowMat.opacity > 0.01
    if (this.glow.visible) {
      const s = ((7 * dist) / ctx.pixelsPerRadian) / R
      this.glow.scale.set(s, s, 1)
      glowMat.color.set(body.visual.color).multiplyScalar(1.2 * ctx.exposureComp)
    }
    placeLabel(this.label, ctx, this.invQ, pixR > 4 ? 1.3 : (6 * dist) / ctx.pixelsPerRadian / R, pixR)

    // Ask for the map size this body now deserves, and re-ask periodically so the loader's sweep
    // can tell what is still on screen from what has been left behind.
    const ticket = this.stream.due(ctx, pixR, { id: body.id, onScreen: onScreen(ctx, g.position, R * 1.3) })
    if (ticket) {
      this.touchMaps()
      void this.loadSurfaceMaps(ticket)
    }
  }

  private paintDamage(): void {
    const body = this.body
    const w = body.radius > 3000 ? 2048 : 1024
    const h = w / 2
    if (!this.damageCanvas) {
      this.damageCanvas = document.createElement('canvas')
      this.damageCanvas.width = w
      this.damageCanvas.height = h
      this.damageTex = new THREE.CanvasTexture(this.damageCanvas)
      this.damageTex.colorSpace = THREE.NoColorSpace
      this.damageTex.wrapS = THREE.RepeatWrapping
      this.damageTex.anisotropy = 8
      this.mat.uniforms.uDamage.value = this.damageTex
    }
    const ctx = this.damageCanvas.getContext('2d')!
    ctx.fillStyle = 'rgb(128,128,128)'
    ctx.fillRect(0, 0, w, h)
    for (const c of body.damage.craters) this.drawCrater(ctx, c, w, h)
    this.damageTex!.needsUpdate = true
    this.paintedVersion = body.damage.version
  }

  private drawCrater(ctx: CanvasRenderingContext2D, c: Crater, w: number, h: number): void {
    const body = this.body
    const { u, v } = bodyDirToUv(c.dir)
    const cx = u * w
    const cy = (1 - v) * h
    const len = Math.hypot(c.dir.x, c.dir.y, c.dir.z) || 1
    const lat = Math.asin(clamp(c.dir.y / len, -1, 1))
    const sx = 1 / Math.max(Math.cos(lat), 0.15)
    const theta = c.diameterKm / 2 / body.radius
    const r = Math.max((theta / Math.PI) * h, 2.5)
    const seed = Math.abs(Math.floor((c.dir.x * 1000 + c.dir.z * 7919 + c.dir.y * 104729) * 1e3)) | 0
    const gas = body.surface === 'gas'
    for (const x of [cx - w, cx, cx + w]) {
      if (x + r * 5 * sx < 0 || x - r * 5 * sx > w) continue
      ctx.save()
      ctx.translate(x, cy)
      ctx.scale(sx, 1)
      if (gas) {
        const gr = ctx.createRadialGradient(0, 0, 0, 0, 0, r * 1.6)
        gr.addColorStop(0, 'rgba(38,28,20,0.9)')
        gr.addColorStop(0.5, 'rgba(50,38,28,0.6)')
        gr.addColorStop(1, 'rgba(60,45,35,0)')
        ctx.fillStyle = gr
        ctx.beginPath()
        ctx.arc(0, 0, r * 1.6, 0, Math.PI * 2)
        ctx.fill()
        ctx.restore()
        continue
      }
      const blanket = ctx.createRadialGradient(0, 0, r * 0.9, 0, 0, r * 2.4)
      blanket.addColorStop(0, 'rgba(152,150,146,0.85)')
      blanket.addColorStop(1, 'rgba(152,150,146,0)')
      ctx.fillStyle = blanket
      ctx.beginPath()
      ctx.arc(0, 0, r * 2.4, 0, Math.PI * 2)
      ctx.fill()
      const rays = 14 + (seed % 9)
      ctx.strokeStyle = 'rgba(178,175,170,0.32)'
      ctx.lineWidth = Math.max(1, r * 0.06)
      for (let k = 0; k < rays; k++) {
        const jitter = ((seed * (k + 3)) % 97) / 97
        const ang = (2 * Math.PI * k) / rays + jitter * 0.4
        const l = r * (2.6 + 3 * (((seed * (k + 7)) % 89) / 89))
        ctx.beginPath()
        ctx.moveTo(Math.cos(ang) * r * 1.1, Math.sin(ang) * r * 1.1)
        ctx.lineTo(Math.cos(ang) * l, Math.sin(ang) * l)
        ctx.stroke()
      }
      ctx.strokeStyle = 'rgb(184,181,177)'
      ctx.lineWidth = Math.max(1, r * 0.18)
      ctx.beginPath()
      ctx.arc(0, 0, r, 0, Math.PI * 2)
      ctx.stroke()
      const floor = ctx.createRadialGradient(0, 0, 0, 0, 0, r * 0.92)
      floor.addColorStop(0, 'rgb(66,64,62)')
      floor.addColorStop(0.7, 'rgb(92,90,88)')
      floor.addColorStop(1, 'rgb(122,120,117)')
      ctx.fillStyle = floor
      ctx.beginPath()
      ctx.arc(0, 0, r * 0.92, 0, Math.PI * 2)
      ctx.fill()
      if (c.diameterKm > 4 && r > 8) {
        ctx.fillStyle = 'rgb(150,148,145)'
        ctx.beginPath()
        ctx.arc(0, 0, r * 0.12, 0, Math.PI * 2)
        ctx.fill()
      }
      ctx.restore()
    }
  }

  dispose(): void {
    this.mat.dispose()
    this.cloudMat?.dispose()
    this.atmoMat?.dispose()
    this.ringMat?.dispose()
    this.ring?.geometry.dispose()
    this.damageTex?.dispose()
    this.label.element.remove()
  }
}
