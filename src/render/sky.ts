import * as THREE from 'three'
import { loadTexture, proceduralMilkyWay } from './textures'
import { POINTS_VERT, POINTS_FRAG } from './shaders/misc'
import { blackbody } from './units'
import { makeRandom } from '../sim/vec'

/** Milky Way panorama plus a field of point stars, both centred on the camera. */
export class Sky {
  group = new THREE.Group()
  private milkyMat: THREE.MeshBasicMaterial
  private starMat: THREE.ShaderMaterial

  constructor() {
    const geo = new THREE.SphereGeometry(5e7, 64, 32)
    this.milkyMat = new THREE.MeshBasicMaterial({ map: proceduralMilkyWay(), side: THREE.BackSide, depthWrite: false, fog: false })
    this.milkyMat.color.setScalar(0.5)
    const milky = new THREE.Mesh(geo, this.milkyMat)
    milky.renderOrder = -10
    milky.frustumCulled = false
    // Galactic pole and centre in the render frame (from their ecliptic coordinates).
    const pole = new THREE.Vector3(-0.8676, 0.4971, 0.0003).normalize()
    const gc = new THREE.Vector3(-0.0556, -0.0958, 0.9937).normalize()
    const X = gc.clone().sub(pole.clone().multiplyScalar(gc.dot(pole))).normalize()
    const Z = new THREE.Vector3().crossVectors(X, pole)
    milky.quaternion.setFromRotationMatrix(new THREE.Matrix4().makeBasis(X, pole, Z))
    this.group.add(milky)
    loadTexture('milky_way.jpg', { maxSize: 8192, pin: true, clamp: true }).then((t) => {
      if (t) {
        this.milkyMat.map = t
        this.milkyMat.needsUpdate = true
      }
    })

    const N = 9000
    const positions = new Float32Array(N * 3)
    const sizes = new Float32Array(N)
    const colors = new Float32Array(N * 3)
    const rand = makeRandom(42)
    const c = new THREE.Color()
    for (let i = 0; i < N; i++) {
      const z = rand() * 2 - 1
      const t = rand() * Math.PI * 2
      const r = Math.sqrt(1 - z * z)
      positions[i * 3] = r * Math.cos(t) * 4e7
      positions[i * 3 + 1] = z * 4e7
      positions[i * 3 + 2] = r * Math.sin(t) * 4e7
      sizes[i] = 0.9 + 2.4 * Math.pow(rand(), 9) + (rand() < 0.003 ? 1.6 : 0)
      const temp = 3200 + 9000 * Math.pow(rand(), 2.2)
      c.copy(blackbody(temp)).multiplyScalar(0.35 + 0.65 * Math.pow(rand(), 1.5))
      colors[i * 3] = c.r
      colors[i * 3 + 1] = c.g
      colors[i * 3 + 2] = c.b
    }
    const sgeo = new THREE.BufferGeometry()
    sgeo.setAttribute('position', new THREE.BufferAttribute(positions, 3))
    sgeo.setAttribute('aSize', new THREE.BufferAttribute(sizes, 1))
    sgeo.setAttribute('aColor', new THREE.BufferAttribute(colors, 3))
    this.starMat = new THREE.ShaderMaterial({
      vertexShader: POINTS_VERT, fragmentShader: POINTS_FRAG,
      uniforms: { uScale: { value: 1 }, uAlpha: { value: 1 } },
      transparent: true, depthWrite: false, blending: THREE.AdditiveBlending,
    })
    const stars = new THREE.Points(sgeo, this.starMat)
    stars.renderOrder = -9
    stars.frustumCulled = false
    this.group.add(stars)
  }

  update(exposureComp: number, dpr: number): void {
    this.milkyMat.color.setScalar(0.55 * exposureComp)
    this.starMat.uniforms.uAlpha.value = exposureComp
    this.starMat.uniforms.uScale.value = dpr
  }
}
