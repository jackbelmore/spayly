import * as THREE from 'three'
import { TestParticles } from '../sim/particles'
import { Vec3 } from '../sim/vec'
import { POINTS_VERT, POINTS_FRAG } from './shaders/misc'
import { RENDER_SCALE } from './units'

const FAMILY_COLORS = [new THREE.Color(0.75, 0.68, 0.6), new THREE.Color(0.65, 0.68, 0.78), new THREE.Color(0.6, 0.72, 0.9)]

/** Points for the asteroid belt, Trojans and Kuiper belt test particles. */
export class BeltView {
  points: THREE.Points
  private particles: TestParticles
  private positions: Float32Array
  private sizes: Float32Array
  private posAttr: THREE.BufferAttribute
  private sizeAttr: THREE.BufferAttribute
  private mat: THREE.ShaderMaterial

  constructor(particles: TestParticles) {
    this.particles = particles
    const n = particles.count
    this.positions = new Float32Array(n * 3)
    this.sizes = new Float32Array(n)
    const colors = new Float32Array(n * 3)
    for (let i = 0; i < n; i++) {
      const c = FAMILY_COLORS[particles.family[i]] ?? FAMILY_COLORS[0]
      const v = 0.7 + 0.3 * ((i * 7919) % 97) / 97
      colors[i * 3] = c.r * v
      colors[i * 3 + 1] = c.g * v
      colors[i * 3 + 2] = c.b * v
      this.sizes[i] = particles.family[i] === 2 ? 1.6 : 1.4 + 0.6 * ((i * 104729) % 89) / 89
    }
    const geo = new THREE.BufferGeometry()
    this.posAttr = new THREE.BufferAttribute(this.positions, 3)
    this.posAttr.setUsage(THREE.DynamicDrawUsage)
    this.sizeAttr = new THREE.BufferAttribute(this.sizes, 1)
    geo.setAttribute('position', this.posAttr)
    geo.setAttribute('aSize', this.sizeAttr)
    geo.setAttribute('aColor', new THREE.BufferAttribute(colors, 3))
    this.mat = new THREE.ShaderMaterial({
      vertexShader: POINTS_VERT, fragmentShader: POINTS_FRAG,
      uniforms: { uScale: { value: 1 }, uAlpha: { value: 0.75 } },
      transparent: true, depthWrite: false, blending: THREE.AdditiveBlending,
    })
    this.points = new THREE.Points(geo, this.mat)
    this.points.frustumCulled = false
    this.points.renderOrder = -1
  }

  update(origin: Vec3, exposureComp: number, dpr: number): void {
    const p = this.particles
    const { pos, alive } = p
    const out = this.positions
    let sizesChanged = false
    for (let i = 0; i < p.count; i++) {
      const ix = i * 3
      if (!alive[i]) {
        if (this.sizes[i] !== 0) {
          this.sizes[i] = 0
          sizesChanged = true
        }
        out[ix] = 0; out[ix + 1] = 0; out[ix + 2] = 1
        continue
      }
      out[ix] = (pos[ix] - origin.x) * RENDER_SCALE
      out[ix + 1] = (pos[ix + 2] - origin.z) * RENDER_SCALE
      out[ix + 2] = -(pos[ix + 1] - origin.y) * RENDER_SCALE
    }
    this.posAttr.needsUpdate = true
    if (sizesChanged) this.sizeAttr.needsUpdate = true
    this.mat.uniforms.uAlpha.value = 0.75 * exposureComp
    this.mat.uniforms.uScale.value = dpr
  }
}
