import * as THREE from 'three'
import { CSS2DObject } from 'three/addons/renderers/CSS2DRenderer.js'
import { Body } from '../sim/body'
import { bodyOrientation } from './orientation'
import { RENDER_SCALE, toRender } from './units'
import { SPHERE_VERT } from './shaders/common'
import { SUN_FRAG, CORONA_FRAG, BILLBOARD_VERT } from './shaders/sun'
import { loadTexture, proceduralSun, swapTexture, touchTexture } from './textures'
import { BodyActions, BodyView, MapStream, ViewContext, makeLabel, onScreen, placeLabel, visualRadius } from './viewContext'

export class SunView implements BodyView {
  body: Body
  group = new THREE.Group()
  glowGroup = new THREE.Group()
  mesh: THREE.Mesh
  corona: THREE.Mesh
  label: CSS2DObject
  pickMesh: THREE.Object3D
  visualRadiusKm = 0
  /** Transient brightening after something falls in. */
  flare = 0
  private mat: THREE.ShaderMaterial
  private coronaMat: THREE.ShaderMaterial
  private invQ = new THREE.Quaternion()
  private uniforms!: Record<string, THREE.IUniform>
  private stream = new MapStream()

  constructor(body: Body, actions: BodyActions) {
    this.body = body
    const u: Record<string, THREE.IUniform> = {
      uTime: { value: 0 }, uColor: { value: new THREE.Color(1, 0.9, 0.7) }, uIntensity: { value: 4 },
      uViewPos: { value: new THREE.Vector3(0, 0, 3) }, uTex: { value: proceduralSun() }, uHasTex: { value: 1 },
    }
    this.mat = new THREE.ShaderMaterial({ vertexShader: SPHERE_VERT, fragmentShader: SUN_FRAG, uniforms: u })
    this.mesh = new THREE.Mesh(new THREE.SphereGeometry(1, 96, 48), this.mat)
    this.mesh.userData.body = body
    this.pickMesh = this.mesh
    this.group.add(this.mesh)
    this.uniforms = u

    this.coronaMat = new THREE.ShaderMaterial({
      vertexShader: BILLBOARD_VERT, fragmentShader: CORONA_FRAG,
      uniforms: { uTime: { value: 0 }, uColor: { value: new THREE.Color(1, 0.9, 0.7) }, uIntensity: { value: 1 }, uFlare: { value: 0 } },
      transparent: true, depthWrite: false, blending: THREE.AdditiveBlending,
    })
    this.corona = new THREE.Mesh(new THREE.PlaneGeometry(6, 6), this.coronaMat)
    this.corona.renderOrder = 4
    this.glowGroup.add(this.corona)

    this.label = makeLabel(body, actions)
    this.group.add(this.label)
  }

  private async loadMap(ticket: number): Promise<void> {
    if (!this.body.visual.texture) return
    const t = await loadTexture(this.body.visual.texture, { maxSize: this.stream.width })
    if (t && this.stream.accept('map', ticket)) this.uniforms.uTex.value = swapTexture(this.uniforms.uTex.value, t)
  }

  update(ctx: ViewContext): void {
    const body = this.body
    const g = this.group
    toRender(body.pos, ctx.origin, g.position)
    this.glowGroup.position.copy(g.position)
    const dist = g.position.length()
    const R = visualRadius(ctx, body, dist, 2)
    g.scale.setScalar(R)
    this.visualRadiusKm = R / RENDER_SCALE
    bodyOrientation(body, ctx.jd, g.quaternion)
    this.invQ.copy(g.quaternion).invert()

    const u = this.mat.uniforms
    ;(u.uColor.value as THREE.Color).copy(ctx.sunColor)
    u.uIntensity.value = 1.3 * ctx.exposureComp
    ;(u.uViewPos.value as THREE.Vector3).copy(g.position).multiplyScalar(-1).applyQuaternion(this.invQ).multiplyScalar(1 / R)
    u.uTime.value = ctx.realTime

    const pixR = (R / Math.max(dist, 1e-9)) * ctx.pixelsPerRadian
    const coronaScale = Math.max(1, (10 * dist) / ctx.pixelsPerRadian / (3 * R))
    this.glowGroup.quaternion.copy(ctx.cameraQuaternion)
    this.glowGroup.scale.setScalar(R * coronaScale)
    const cu = this.coronaMat.uniforms
    ;(cu.uColor.value as THREE.Color).copy(ctx.sunColor)
    const big = Math.min(1, Math.max(0, (pixR - 40) / 360))
    cu.uIntensity.value = ctx.exposureComp * Math.min(1, 0.6 + 0.4 * Math.sqrt(ctx.sunLuminosity)) * (1 - 0.7 * big)
    cu.uTime.value = ctx.realTime
    cu.uFlare.value = this.flare
    this.flare *= 0.97
    placeLabel(this.label, ctx, this.invQ, pixR > 4 ? 1.35 : (8 * dist) / ctx.pixelsPerRadian / R, pixR)

    // Re-ask for the map at the size the Sun now deserves. Re-asking is also what tells the loader's
    // sweep this texture is still on screen; without it the sweep frees a map still bound here.
    const ticket = this.stream.due(ctx, pixR, { id: body.id, onScreen: onScreen(ctx, g.position, R * 2) })
    if (ticket) {
      touchTexture(this.uniforms.uTex.value)
      void this.loadMap(ticket)
    }
  }

  dispose(): void {
    this.mat.dispose()
    this.coronaMat.dispose()
    this.mesh.geometry.dispose()
    this.label.element.remove()
  }
}
