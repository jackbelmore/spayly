import * as THREE from 'three'
import { CSS2DObject } from 'three/addons/renderers/CSS2DRenderer.js'
import { mergeVertices } from 'three/addons/utils/BufferGeometryUtils.js'
import { Body } from '../sim/body'
import { sub, clamp, makeRandom } from '../sim/vec'
import { bodyOrientation } from './orientation'
import { RENDER_SCALE, toRender } from './units'
import { ROCK_VERT, ROCK_FRAG } from './shaders/misc'
import { fbm3 } from './noise'
import { loadTexture, radialSprite, swapTexture, touchTexture, whiteTexture } from './textures'
import { BodyActions, BodyView, MapStream, ViewContext, decay, irradiance, makeLabel, onScreen, placeLabel, visualRadius } from './viewContext'

let glowTex: THREE.Texture | null = null
let blankTex: THREE.Texture | null = null
/** A shatter can spawn a dozen rocks at once; sixteen shared shapes beats building one per body. */
const geoCache = new Map<number, THREE.BufferGeometry>()

function sharedRockGeometry(seed: number): THREE.BufferGeometry {
  const key = ((seed % 16) + 16) % 16
  let g = geoCache.get(key)
  if (!g) {
    g = rockGeometry(key * 7919 + 13)
    geoCache.set(key, g)
  }
  return g
}

/**
 * Subdivision of the icosphere: (n + 1)^2 triangles on each of its 20 faces, so 30 gives about 19k
 * triangles and facets under 2 degrees across. At 4 the facets were 13 degrees and a rock filling
 * the screen had a visibly polygonal outline.
 */
const ROCK_DETAIL = 30

/**
 * The icosphere every rock is displaced from, welded once and shared. three builds its polyhedra
 * unindexed, with a private copy of every vertex for each face it touches, and computeVertexNormals
 * on that gives each face its own flat normal. Welded into one indexed mesh, shared vertices move
 * together and their normals average across the faces around them. The weld has to drop the UVs,
 * which differ across the seam; the shader maps the texture from `aDir`, the undisplaced direction,
 * instead. Welding is most of the cost of a rock, and a shatter can ask for a dozen shapes at once.
 */
let welded: { index: THREE.BufferAttribute; dir: THREE.BufferAttribute } | null = null

function weldedSphere(): { index: THREE.BufferAttribute; dir: THREE.BufferAttribute } {
  if (!welded) {
    const ico = new THREE.IcosahedronGeometry(1, ROCK_DETAIL)
    ico.deleteAttribute('normal')
    ico.deleteAttribute('uv')
    const geo = mergeVertices(ico)
    ico.dispose()
    welded = { index: geo.index!, dir: geo.attributes.position as THREE.BufferAttribute }
  }
  return welded
}

/** A lumpy potato: the welded icosphere displaced by low-frequency noise. */
export function rockGeometry(seed: number): THREE.BufferGeometry {
  const { index, dir } = weldedSphere()
  const rand = makeRandom(seed)
  const ex = 0.85 + rand() * 0.5
  const ez = 0.75 + rand() * 0.4
  const pos = new Float32Array(dir.count * 3)
  const v = new THREE.Vector3()
  for (let i = 0; i < dir.count; i++) {
    v.fromBufferAttribute(dir, i).normalize()
    // Low frequencies only: the lumps should be many facets wide, or neighbouring vertices swing in
    // and out and the surface creases.
    const n = fbm3(v.x * 1.8 + 3.1, v.y * 1.8, v.z * 1.8, seed) - 0.5
    const m = fbm3(v.x * 4 + 0.7, v.y * 4 + 1.3, v.z * 4, seed + 7, 3) - 0.5
    const r = 1 + 0.42 * n + 0.1 * m
    pos[i * 3] = v.x * r * ex
    pos[i * 3 + 1] = v.y * r
    pos[i * 3 + 2] = v.z * r * ez
  }
  const geo = new THREE.BufferGeometry()
  geo.setIndex(index)
  geo.setAttribute('position', new THREE.BufferAttribute(pos, 3))
  geo.setAttribute('aDir', dir)
  geo.computeVertexNormals()
  geo.computeBoundingSphere()
  return geo
}

const TINTS: Record<string, THREE.Color> = {
  rock: new THREE.Color(0.55, 0.5, 0.45),
  ice: new THREE.Color(0.8, 0.85, 0.9),
  lava: new THREE.Color(0.35, 0.3, 0.28),
  gas: new THREE.Color(0.6, 0.55, 0.5),
  star: new THREE.Color(1, 1, 1),
}

export class RockView implements BodyView {
  body: Body
  group = new THREE.Group()
  mesh: THREE.Mesh
  glow: THREE.Sprite
  label: CSS2DObject
  pickMesh: THREE.Object3D
  visualRadiusKm = 0
  private mat: THREE.ShaderMaterial
  private invQ = new THREE.Quaternion()
  private tmp = new THREE.Vector3()
  private uniforms!: Record<string, THREE.IUniform>
  private stream = new MapStream()

  constructor(body: Body, actions: BodyActions) {
    this.body = body
    if (!glowTex) glowTex = radialSprite(128, 2.2)
    if (!blankTex) blankTex = whiteTexture()
    const tint = TINTS[body.surface] ?? TINTS.rock
    // Where in the shader's noise field this rock's surface comes from, so no two look alike.
    const rand = makeRandom(body.seed + 11)
    const u: Record<string, THREE.IUniform> = {
      uTex: { value: blankTex }, uHasMap: { value: 0 }, uSeed: { value: new THREE.Vector3(rand() * 50, rand() * 50, rand() * 50) },
      uSunDir: { value: new THREE.Vector3(0, 0, 1) }, uSunColor: { value: new THREE.Color(1, 1, 1) },
      uSunIntensity: { value: 1 }, uViewPos: { value: new THREE.Vector3(0, 0, 3) }, uMagma: { value: 0 }, uTime: { value: 0 },
      uTint: { value: tint.clone() },
    }
    this.uniforms = u
    this.mat = new THREE.ShaderMaterial({ vertexShader: ROCK_VERT, fragmentShader: ROCK_FRAG, uniforms: u })
    this.mesh = new THREE.Mesh(sharedRockGeometry(body.seed), this.mat)
    this.mesh.userData.body = body
    this.pickMesh = this.mesh
    this.group.add(this.mesh)
    const glowColor = body.surface === 'lava' ? new THREE.Color(1, 0.45, 0.15) : new THREE.Color(0.9, 0.85, 0.8)
    this.glow = new THREE.Sprite(new THREE.SpriteMaterial({ map: glowTex, color: glowColor, transparent: true, blending: THREE.AdditiveBlending, depthWrite: false }))
    this.glow.renderOrder = 3
    this.group.add(this.glow)
    this.label = makeLabel(body, actions)
    this.group.add(this.label)
  }

  /** Vesta and friends have real maps; the tint exists only to colour the procedural surface. */
  private async loadMap(ticket: number): Promise<void> {
    const vis = this.body.visual
    if (!vis.texture) return
    const t = await loadTexture(vis.texture, { maxSize: this.stream.width, fillGaps: vis.fillGaps, tint: vis.tint, roll: vis.roll })
    if (!t || !this.stream.accept('map', ticket)) return
    this.uniforms.uTex.value = swapTexture(this.uniforms.uTex.value, t)
    this.uniforms.uHasMap.value = 1
    ;(this.uniforms.uTint.value as THREE.Color).setRGB(1, 1, 1)
  }

  update(ctx: ViewContext): void {
    const body = this.body
    const g = this.group
    const u = this.mat.uniforms
    toRender(body.pos, ctx.origin, g.position)
    const dist = g.position.length()
    const R = visualRadius(ctx, body, dist, 2.5)
    g.scale.setScalar(R)
    this.visualRadiusKm = R / RENDER_SCALE
    bodyOrientation(body, ctx.jd, g.quaternion)
    this.invQ.copy(g.quaternion).invert()
    const sunRel = sub(ctx.sunPos, body.pos)
    this.tmp.set(sunRel.x, sunRel.z, -sunRel.y).normalize().applyQuaternion(this.invQ)
    ;(u.uSunDir.value as THREE.Vector3).copy(this.tmp)
    ;(u.uSunColor.value as THREE.Color).copy(ctx.sunColor)
    u.uSunIntensity.value = irradiance(ctx, body)
    ;(u.uViewPos.value as THREE.Vector3).copy(g.position).multiplyScalar(-1).applyQuaternion(this.invQ).multiplyScalar(1 / R)
    const d = body.damage
    u.uMagma.value = decay(d.magma, d.magmaTime, d.magmaTau, ctx.simTime)
    u.uTime.value = ctx.realTime
    const pixR = (R / Math.max(dist, 1e-9)) * ctx.pixelsPerRadian
    const glowMat = this.glow.material as THREE.SpriteMaterial
    glowMat.opacity = clamp((12 - pixR) / 9, 0, 1) * (0.5 + 0.5 * (u.uMagma.value as number))
    this.glow.visible = glowMat.opacity > 0.01
    if (this.glow.visible) {
      const s = ((5 * dist) / ctx.pixelsPerRadian) / R
      this.glow.scale.set(s, s, 1)
    }
    // Lumps and squash keep most of a rock inside its nominal radius, so its label sits lower than a planet's.
    placeLabel(this.label, ctx, this.invQ, pixR > 4 ? 1.25 : (6 * dist) / ctx.pixelsPerRadian / R, pixR)

    if (this.body.visual.texture) {
      const ticket = this.stream.due(ctx, pixR, { id: body.id, onScreen: onScreen(ctx, g.position, R * 1.3) })
      if (ticket) {
        touchTexture(u.uTex.value)
        void this.loadMap(ticket)
      }
    }
  }

  dispose(): void {
    this.mat.dispose()
    // The geometry is shared between rocks; leave it in the cache.
    this.label.element.remove()
  }
}
