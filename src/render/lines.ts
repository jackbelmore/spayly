import * as THREE from 'three'
import { Vec3 } from '../sim/vec'
import { RENDER_SCALE } from './units'

/** A camera-relative polyline with optional head-to-tail fade, drawn additively. */
export class PolyLine {
  line: THREE.Line
  capacity: number
  private positions: Float32Array
  private colors: Float32Array
  private posAttr: THREE.BufferAttribute
  private colAttr: THREE.BufferAttribute
  private geo: THREE.BufferGeometry
  color: THREE.Color

  constructor(capacity: number, color: THREE.Color | string, opacity = 0.5) {
    this.capacity = capacity
    this.color = new THREE.Color(color)
    this.positions = new Float32Array(capacity * 3)
    this.colors = new Float32Array(capacity * 3)
    this.geo = new THREE.BufferGeometry()
    this.posAttr = new THREE.BufferAttribute(this.positions, 3)
    this.posAttr.setUsage(THREE.DynamicDrawUsage)
    this.colAttr = new THREE.BufferAttribute(this.colors, 3)
    this.colAttr.setUsage(THREE.DynamicDrawUsage)
    this.geo.setAttribute('position', this.posAttr)
    this.geo.setAttribute('color', this.colAttr)
    this.geo.setDrawRange(0, 0)
    const mat = new THREE.LineBasicMaterial({ vertexColors: true, transparent: true, opacity, blending: THREE.AdditiveBlending, depthWrite: false })
    this.line = new THREE.Line(this.geo, mat)
    this.line.frustumCulled = false
  }

  set opacity(v: number) {
    ;(this.line.material as THREE.LineBasicMaterial).opacity = v
  }

  /** Write points (simulation km) relative to origin. fade > 0 darkens toward the first point. */
  set(points: Vec3[], origin: Vec3, fade = 0, brightness = 1): void {
    const n = Math.min(points.length, this.capacity)
    const c = this.color
    for (let i = 0; i < n; i++) {
      const p = points[i]
      const ix = i * 3
      this.positions[ix] = (p.x - origin.x) * RENDER_SCALE
      this.positions[ix + 1] = (p.z - origin.z) * RENDER_SCALE
      this.positions[ix + 2] = -(p.y - origin.y) * RENDER_SCALE
      const k = fade > 0 ? Math.pow(i / Math.max(n - 1, 1), fade) : 1
      this.colors[ix] = c.r * k * brightness
      this.colors[ix + 1] = c.g * k * brightness
      this.colors[ix + 2] = c.b * k * brightness
    }
    this.geo.setDrawRange(0, n)
    this.posAttr.needsUpdate = true
    this.colAttr.needsUpdate = true
  }

  clear(): void {
    this.geo.setDrawRange(0, 0)
  }

  dispose(): void {
    this.geo.dispose()
    ;(this.line.material as THREE.Material).dispose()
  }
}

/**
 * Ring buffer of past positions rendered as a fading line. Positions can be kept relative to a
 * moving frame — a moon's trail relative to its planet — so the trail draws the orbit rather than a
 * corkscrew smeared along the planet's own path.
 */
export class Trail {
  line: PolyLine
  /** Id of the body the stored positions are relative to; '' for the solar-system frame. */
  frame = ''
  private buf: Float64Array
  private head = 0
  private count = 0
  private lastTime = -Infinity
  private lastSign = 1
  private scratch: Vec3[] = []
  capacity: number

  constructor(capacity: number, color: THREE.Color | string) {
    this.capacity = capacity
    this.buf = new Float64Array(capacity * 3)
    this.line = new PolyLine(capacity, color, 0.6)
  }

  push(pos: Vec3, simTime: number, minInterval: number): void {
    const dt = simTime - this.lastTime
    const sign = dt < 0 ? -1 : 1
    if (sign !== this.lastSign && isFinite(this.lastTime)) this.reset()
    this.lastSign = sign
    if (Math.abs(simTime - this.lastTime) < minInterval) return
    this.lastTime = simTime
    const ix = this.head * 3
    this.buf[ix] = pos.x
    this.buf[ix + 1] = pos.y
    this.buf[ix + 2] = pos.z
    this.head = (this.head + 1) % this.capacity
    this.count = Math.min(this.count + 1, this.capacity)
  }

  /** Switch the frame positions are pushed in; the old history means nothing in the new one. */
  useFrame(id: string): void {
    if (id === this.frame) return
    this.frame = id
    this.reset()
  }

  reset(): void {
    this.head = 0
    this.count = 0
    this.lastTime = -Infinity
    this.line.clear()
  }

  /** `current` is absolute; `frameAt` is where the trail's frame is now, for relative trails. */
  update(origin: Vec3, current: Vec3, brightness = 1, frameAt?: Vec3): void {
    const n = this.count
    const pts = this.scratch
    pts.length = 0
    const ox = frameAt?.x ?? 0, oy = frameAt?.y ?? 0, oz = frameAt?.z ?? 0
    const start = (this.head - n + this.capacity) % this.capacity
    for (let i = 0; i < n; i++) {
      const ix = ((start + i) % this.capacity) * 3
      pts.push({ x: this.buf[ix] + ox, y: this.buf[ix + 1] + oy, z: this.buf[ix + 2] + oz })
    }
    pts.push(current)
    this.line.set(pts, origin, 1.6, brightness)
  }

  dispose(): void {
    this.line.dispose()
  }
}
