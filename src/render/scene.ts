import * as THREE from 'three'
import { EffectComposer } from 'three/addons/postprocessing/EffectComposer.js'
import { RenderPass } from 'three/addons/postprocessing/RenderPass.js'
import { UnrealBloomPass } from 'three/addons/postprocessing/UnrealBloomPass.js'
import { OutputPass } from 'three/addons/postprocessing/OutputPass.js'
import { CSS2DRenderer } from 'three/addons/renderers/CSS2DRenderer.js'
import { configureTextures } from './textures'

/** Renderer, HDR post-processing chain (bloom, ACES tone mapping) and the label overlay. */
export class SceneSetup {
  renderer: THREE.WebGLRenderer
  scene = new THREE.Scene()
  camera: THREE.PerspectiveCamera
  composer: EffectComposer
  bloomPass: UnrealBloomPass
  labelRenderer: CSS2DRenderer
  width = 1
  height = 1
  dpr = 1

  constructor(stage: HTMLElement, labelLayer: HTMLElement) {
    this.renderer = new THREE.WebGLRenderer({ antialias: false, logarithmicDepthBuffer: true, powerPreference: 'high-performance' })
    this.dpr = Math.min(window.devicePixelRatio || 1, 2)
    this.renderer.setPixelRatio(this.dpr)
    this.renderer.toneMapping = THREE.ACESFilmicToneMapping
    this.renderer.toneMappingExposure = 1
    this.renderer.setClearColor(0x000000, 1)
    stage.appendChild(this.renderer.domElement)
    configureTextures(this.renderer)

    this.camera = new THREE.PerspectiveCamera(50, 1, 1e-6, 1e9)
    const target = new THREE.WebGLRenderTarget(1, 1, { type: THREE.HalfFloatType, samples: 4 })
    this.composer = new EffectComposer(this.renderer, target)
    this.composer.addPass(new RenderPass(this.scene, this.camera))
    this.bloomPass = new UnrealBloomPass(new THREE.Vector2(1, 1), 0.5, 0.55, 0.92)
    this.composer.addPass(this.bloomPass)
    this.composer.addPass(new OutputPass())

    this.labelRenderer = new CSS2DRenderer({ element: labelLayer })
    this.resize()
    window.addEventListener('resize', () => this.resize())
  }

  resize(): void {
    this.width = window.innerWidth
    this.height = window.innerHeight
    this.camera.aspect = this.width / this.height
    this.camera.updateProjectionMatrix()
    this.renderer.setSize(this.width, this.height)
    this.composer.setPixelRatio(this.dpr)
    this.composer.setSize(this.width, this.height)
    this.bloomPass.resolution.set(this.width, this.height)
    this.labelRenderer.setSize(this.width, this.height)
  }

  /**
   * Pull the near plane in when the camera is close to something. It is 1 unit = 1000 km out here,
   * so a fixed near plane of 1 km swallows anything small enough to fly right up to; the logarithmic
   * depth buffer copes with the resulting range.
   */
  setNearForDistance(distanceUnits: number): void {
    const near = Math.min(0.001, Math.max(1e-7, distanceUnits * 0.002))
    if (near !== this.camera.near) {
      this.camera.near = near
      this.camera.updateProjectionMatrix()
    }
  }

  /** CSS pixels per radian of field of view (vertical). */
  get pixelsPerRadian(): number {
    return this.height / (2 * Math.tan((this.camera.fov * Math.PI) / 360))
  }

  set exposure(v: number) {
    this.renderer.toneMappingExposure = v
  }

  set bloom(on: boolean) {
    this.bloomPass.enabled = on
  }

  render(): void {
    this.composer.render()
    this.labelRenderer.render(this.scene, this.camera)
  }
}
