import * as THREE from 'three'
import { Body } from '../sim/body'
import { AU } from '../sim/constants'
import { Vec3, add, clamp, lerp, v3 } from '../sim/vec'

export interface ViewSpec {
  /** Body to follow (null detaches, undefined keeps the current one). */
  focus?: Body | null
  /** Fixed target point (km) when not following a body. */
  target?: Vec3
  distance?: number
  yaw?: number
  pitch?: number
}

interface Flight {
  fromTarget: Vec3
  toTarget?: Vec3
  fromDist: number
  toDist: number
  fromYaw: number
  toYaw: number
  fromPitch: number
  toPitch: number
  t: number
  dur: number
}

const smooth = (x: number) => {
  const t = clamp(x, 0, 1)
  return t * t * (3 - 2 * t)
}
const wrapAngle = (a: number) => Math.atan2(Math.sin(a), Math.cos(a))

/**
 * Orbit camera in simulation coordinates (km, float64). The three.js camera stays at the origin and only rotates;
 * everything else is positioned relative to `position` (floating origin). Zoom is multiplicative, so one wheel
 * notch feels the same at 10 km and 10 AU.
 */
export class CameraRig {
  camera: THREE.PerspectiveCamera
  focus: Body | null = null
  target: Vec3 = v3()
  distance = 3 * AU
  yaw = 0.7
  pitch = 0.45
  goalDistance = this.distance
  goalYaw = this.yaw
  goalPitch = this.pitch
  position: Vec3 = v3()
  /** Offset of the look-at point from the followed body, so panning does not break the follow. */
  targetOffset: Vec3 = v3()
  /** Floor on the orbit distance when nothing is focused; a focused body's own surface overrides it. */
  minDistanceKm = 0.01
  maxDistanceKm = 600 * AU
  /** Visual radius of the focused body (km), used to stop the camera at its surface. */
  focusRadiusKm = 0
  /**
   * Field of view, degrees. The camera stops at a body's surface, which caps how large it can get
   * on screen and therefore how much map detail can ever show. Past that point zooming narrows the
   * lens instead of closing the distance, which is what lets a big map earn its memory.
   */
  readonly wideFov = 50
  readonly narrowFov = 6
  fov = 50
  goalFov = 50
  private flight: Flight | null = null
  private tmpDir = new THREE.Vector3()
  private tmpQ = new THREE.Quaternion()
  private right = new THREE.Vector3()
  private up = new THREE.Vector3()

  constructor(camera: THREE.PerspectiveCamera) {
    this.camera = camera
  }

  get flying(): boolean {
    return this.flight !== null
  }

  orbit(dx: number, dy: number): void {
    this.rotateBy(-dx * 0.0055, dy * 0.0055)
  }

  /** Swing the camera around the target by the given angles, radians. */
  rotateBy(dYaw: number, dPitch: number): void {
    if (this.flight) return
    this.goalYaw += dYaw
    this.goalPitch = clamp(this.goalPitch + dPitch, -1.53, 1.53)
  }

  /** Distance the camera may not close past: a focused body's surface, otherwise the global floor. */
  private floor(): number {
    return this.focus ? Math.max(this.focusRadiusKm, 1e-6) : this.minDistanceKm
  }

  zoom(factor: number): void {
    if (this.flight) return
    const grounded = this.goalDistance <= this.floor() * 1.002
    if (factor < 1 && grounded) {
      // Already as close as the surface allows; keep zooming by narrowing the lens.
      this.goalFov = clamp(this.goalFov * factor, this.narrowFov, this.wideFov)
      return
    }
    if (factor > 1 && this.goalFov < this.wideFov) {
      this.goalFov = clamp(this.goalFov * factor, this.narrowFov, this.wideFov)
      if (this.goalFov < this.wideFov) return
    }
    this.goalDistance = clamp(this.goalDistance * factor, this.minDistanceKm, this.maxDistanceKm)
  }

  /** True while the lens is narrowed past its resting angle. */
  get zoomedIn(): boolean {
    return this.goalFov < this.wideFov - 0.01
  }

  /** Slide the look-at point by a mouse drag: the world follows the pointer. */
  pan(dx: number, dy: number): void {
    const k = this.distance * 0.0016
    this.panBy(-dx * k, dy * k)
  }

  /**
   * Slide the look-at point across the view plane, in km: positive `right`/`up` move the view that
   * way. While following a body the slide is stored as an offset, so the follow survives.
   */
  panBy(right: number, up: number): void {
    if (this.flight) return
    this.right.set(1, 0, 0).applyQuaternion(this.camera.quaternion)
    this.up.set(0, 1, 0).applyQuaternion(this.camera.quaternion)
    const shift = {
      x: this.right.x * right + this.up.x * up,
      y: -(this.right.z * right + this.up.z * up),
      z: this.right.y * right + this.up.y * up,
    }
    if (this.focus) this.targetOffset = add(this.targetOffset, shift)
    else this.target = add(this.target, shift)
  }

  flyTo(spec: ViewSpec, duration = 1.5): void {
    const fromTarget = { ...this.target }
    this.targetOffset = v3()
    this.goalFov = this.wideFov
    if (spec.focus !== undefined) this.focus = spec.focus
    if (spec.target) this.focus = null
    const toYaw = spec.yaw ?? this.goalYaw
    this.flight = {
      fromTarget,
      toTarget: spec.target,
      fromDist: this.distance,
      toDist: clamp(spec.distance ?? this.goalDistance, this.minDistanceKm, this.maxDistanceKm),
      fromYaw: this.yaw,
      toYaw: this.yaw + wrapAngle(toYaw - this.yaw),
      fromPitch: this.pitch,
      toPitch: clamp(spec.pitch ?? this.goalPitch, -1.53, 1.53),
      t: 0,
      dur: duration,
    }
  }

  /** Jump instantly. */
  set(spec: ViewSpec): void {
    this.targetOffset = v3()
    this.goalFov = this.wideFov
    if (spec.focus !== undefined) this.focus = spec.focus
    if (spec.target) { this.target = { ...spec.target }; this.focus = null }
    if (spec.distance !== undefined) this.distance = this.goalDistance = clamp(spec.distance, this.minDistanceKm, this.maxDistanceKm)
    if (spec.yaw !== undefined) this.yaw = this.goalYaw = spec.yaw
    if (spec.pitch !== undefined) this.pitch = this.goalPitch = spec.pitch
    this.flight = null
  }

  update(dt: number): void {
    const f = this.flight
    if (f) {
      f.t += dt
      const s = smooth(f.t / f.dur)
      const dest = this.focus ? this.focus.pos : (f.toTarget ?? f.fromTarget)
      this.target = lerp(f.fromTarget, dest, s)
      this.distance = Math.exp(Math.log(f.fromDist) + (Math.log(f.toDist) - Math.log(f.fromDist)) * s)
      this.yaw = f.fromYaw + (f.toYaw - f.fromYaw) * s
      this.pitch = f.fromPitch + (f.toPitch - f.fromPitch) * s
      if (f.t >= f.dur) {
        this.flight = null
        this.goalDistance = this.distance
        this.goalYaw = this.yaw
        this.goalPitch = this.pitch
      }
    } else {
      if (this.focus) {
        const o = this.targetOffset
        this.target = { x: this.focus.pos.x + o.x, y: this.focus.pos.y + o.y, z: this.focus.pos.z + o.z }
      }
      const k = 1 - Math.exp(-dt * 9)
      this.distance = Math.exp(Math.log(this.distance) + (Math.log(this.goalDistance) - Math.log(this.distance)) * k)
      this.yaw += (this.goalYaw - this.yaw) * k
      this.pitch += (this.goalPitch - this.pitch) * k
    }
    const kf = 1 - Math.exp(-dt * 9)
    this.fov += (this.goalFov - this.fov) * kf
    if (Math.abs(this.camera.fov - this.fov) > 1e-4) {
      this.camera.fov = this.fov
      this.camera.updateProjectionMatrix()
    }
    // A 19 m Chelyabinsk rock wants the camera 40 m away; only its own surface should stop us.
    const minD = this.focus ? Math.max(this.focusRadiusKm, 1e-6) : this.minDistanceKm
    if (this.distance < minD) {
      this.distance = minD
      if (this.goalDistance < minD) this.goalDistance = minD
    }
    const cp = Math.cos(this.pitch)
    const ox = cp * Math.sin(this.yaw) * this.distance
    const oy = Math.sin(this.pitch) * this.distance
    const oz = cp * Math.cos(this.yaw) * this.distance
    this.position = { x: this.target.x + ox, y: this.target.y - oz, z: this.target.z + oy }
    this.camera.position.set(0, 0, 0)
    this.camera.up.set(0, 1, 0)
    this.tmpDir.set(-ox, -oy, -oz)
    this.camera.lookAt(this.tmpDir)
    this.camera.updateMatrixWorld()
  }

  /** Unit ray direction in the simulation frame through normalised device coordinates. */
  rayDir(ndcX: number, ndcY: number): Vec3 {
    const v = new THREE.Vector3(ndcX, ndcY, 0.5).unproject(this.camera).normalize()
    return { x: v.x, y: -v.z, z: v.y }
  }

  /** Yaw that places the camera on the side of the target facing `dirSim` (e.g. towards the Sun). */
  static yawTowards(dirSim: Vec3): number {
    return Math.atan2(dirSim.x, -dirSim.y)
  }

  quaternion(): THREE.Quaternion {
    return this.tmpQ.copy(this.camera.quaternion)
  }
}
