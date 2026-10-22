import * as THREE from 'three'
import { CSS2DObject } from 'three/addons/renderers/CSS2DRenderer.js'
import { Body } from '../sim/body'
import { AU } from '../sim/constants'
import { Vec3, sub, length } from '../sim/vec'
import { RENDER_SCALE } from './units'

/** Per-frame data every body view needs. */
export interface ViewContext {
  /** Camera position, km (floating origin). */
  origin: Vec3
  jd: number
  simTime: number
  realTime: number
  sunPos: Vec3
  sunRadiusKm: number
  sunColor: THREE.Color
  /** Luminosity relative to the present Sun. */
  sunLuminosity: number
  /** Luminosity in watts, for equilibrium temperatures. */
  sunWatts: number
  /** Exponent of the irradiance falloff with distance (2 = physical, 0 = flat). */
  falloff: number
  /** Multiplier that keeps self-luminous things sane under auto-exposure. */
  exposureComp: number
  dioramaAmount: number
  dioramaPlanet: number
  dioramaSun: number
  minPixelRadius: number
  /** Pixels per radian of view: height / (2 tan(fov/2)). */
  pixelsPerRadian: number
  bodies: Body[]
  cameraQuaternion: THREE.Quaternion
  /** Widest a body map may be decoded at; streaming asks for less when the body is small. */
  textureCeiling: number
  /** Device pixels per CSS pixel. `pixelsPerRadian` is in CSS pixels, which labels and minimum sizes want. */
  pixelRatio: number
  /** The camera's view volume in render space, for skipping maps nobody can see. */
  frustum: THREE.Frustum
  /** The body the camera is following, which gets its best map straight away. */
  focusId: string | null
}

/** Narrowest map a body is given: plenty for a disc a few pixels across. */
export const MIN_TEXTURE_WIDTH = 256
/** What the camera's focus is shown at first, from a low-res copy that decodes quickly. */
const FOCUS_FIRST_WIDTH = 2048

/**
 * Texture width that puts roughly one texel on each pixel of a body this big on screen.
 *
 * You see half the map at once — 180 degrees of longitude — across a disc two radii wide, so a map
 * four times the on-screen radius is about 1:1. Rounded up to a power of two and held between
 * MIN_TEXTURE_WIDTH and the ceiling. The floor used to be 1024, which kept ~115 MB of maps resident
 * for bodies that were dots. Flying close to a surface pushes the radius past the viewport, which is
 * where the large maps finally earn their memory.
 */
export function textureWidthFor(pixelRadius: number, ceiling: number): number {
  let w = MIN_TEXTURE_WIDTH
  while (w < pixelRadius * 4 && w < ceiling) w *= 2
  return Math.min(w, ceiling)
}

/**
 * Next width to load, given what is loaded now. Upgrades as soon as more detail would show, but only
 * drops once the body is down to a quarter of its current map. With the old half-width threshold the
 * up and down points coincided, and a body sitting on one flip-flopped between two sizes twice a second.
 */
export function streamedWidth(current: number, pixelRadius: number, ceiling: number): number {
  const target = textureWidthFor(pixelRadius, ceiling)
  if (target > current) return target
  if (target <= current / 4) return target
  return Math.min(current, ceiling)
}

/**
 * Streaming bookkeeping shared by the body views: which width to load, when to reconsider it, and
 * which load's result is allowed to land.
 */
export class MapStream {
  /** Width the view is currently asking for. */
  width = MIN_TEXTURE_WIDTH
  private checkAt = 0
  private issued = 0
  private landed = new Map<string, number>()
  private focused = false

  /**
   * Twice a second, pick the width this body now deserves and return a ticket for the load; 0 when
   * it is not yet time. The timer matters beyond picking the width: re-asking is what tells the
   * loader's sweep a map is still on screen, and it coalesces a fast zoom's octave crossings into
   * one load instead of one per octave.
   *
   * The body the camera follows skips the wait on the frame it becomes the focus and goes straight
   * to its best map, so double-clicking a body starts loading at the click rather than when the
   * flight ends: first a 2048 copy that decodes in a moment, then the ceiling. A body outside the
   * view drops to the floor; it is still re-asked, so the sweep keeps its small map.
   */
  due(ctx: ViewContext, pixelRadius: number, body: { id: string; onScreen: boolean }): number {
    const focused = ctx.focusId === body.id
    const justFocused = focused && !this.focused
    this.focused = focused
    if (ctx.realTime < this.checkAt && !justFocused) return 0
    this.checkAt = ctx.realTime + 0.5
    const ceiling = ctx.textureCeiling
    if (focused) {
      this.width = this.width < FOCUS_FIRST_WIDTH && ceiling > FOCUS_FIRST_WIDTH ? FOCUS_FIRST_WIDTH : ceiling
    } else if (!body.onScreen) {
      this.width = streamedWidth(this.width, 0, ceiling)
    } else {
      this.width = streamedWidth(this.width, pixelRadius * ctx.pixelRatio, ceiling)
    }
    return ++this.issued
  }

  /**
   * Whether a load's result for one slot (a planet has several maps) may be bound. Loads are never
   * cancelled and a 16K decode outlasts a 2K one, so without this a slow result could land after a
   * newer one and put back a size the camera has already left.
   */
  accept(slot: string, ticket: number): boolean {
    if (ticket < (this.landed.get(slot) ?? 0)) return false
    this.landed.set(slot, ticket)
    return true
  }
}

const visSphere = new THREE.Sphere()

/** Whether any of a sphere (render space, camera-relative) is inside the camera's view. */
export function onScreen(ctx: ViewContext, center: THREE.Vector3, radius: number): boolean {
  visSphere.center.copy(center)
  visSphere.radius = radius
  return ctx.frustum.intersectsSphere(visSphere)
}

const labelUp = new THREE.Vector3()

/**
 * Put a body's label just above it on screen, and hide it once the body fills the view. The label
 * is a child of the body's group, which spins with the body, so a fixed local offset points along
 * the pole — dead centre on the disc for a body whose pole faces the camera. Offset along the
 * camera's up axis instead, carried into the body's frame. `height` is in body radii.
 */
export function placeLabel(label: CSS2DObject, ctx: ViewContext, invQ: THREE.Quaternion, height: number, pixelRadius: number): void {
  labelUp.set(0, 1, 0).applyQuaternion(ctx.cameraQuaternion).applyQuaternion(invQ)
  label.position.copy(labelUp).multiplyScalar(height)
  // CSS2DRenderer rewrites style.display every frame, so visibility has to go through the object.
  label.visible = pixelRadius <= 0.45 * ctx.pixelsPerRadian
}

/** What a view's label and mesh do when the user interacts with them. */
export interface BodyActions {
  select(b: Body): void
  /** Second click on the same body: fly to it. */
  activate(b: Body): void
}

export interface BodyView {
  body: Body
  group: THREE.Group
  pickMesh: THREE.Object3D
  label: CSS2DObject
  /** Rendered radius in km after diorama and minimum-pixel scaling. */
  visualRadiusKm: number
  update(ctx: ViewContext): void
  dispose(): void
}

const DOUBLE_CLICK_MS = 380

export function makeLabel(body: Body, actions: BodyActions): CSS2DObject {
  const el = document.createElement('div')
  el.className = 'label'
  el.title = `${body.name} — click to select, double-click to fly there`
  el.innerHTML = `<span class="dot" style="background:${body.visual.color}"></span>${body.name}`
  // The body drifts across the screen, so a real dblclick can miss; time the clicks ourselves,
  // the same way picking a body in the scene does.
  let lastClick = 0
  el.addEventListener('pointerdown', (e) => {
    e.stopPropagation()
    const now = performance.now()
    if (now - lastClick < DOUBLE_CLICK_MS) {
      lastClick = 0
      actions.activate(body)
    } else {
      lastClick = now
      actions.select(body)
    }
  })
  const obj = new CSS2DObject(el)
  obj.position.set(0, 1.35, 0)
  obj.center.set(0.5, 1)
  return obj
}

/** Irradiance at a body relative to Earth's present sunlight. */
export function irradiance(ctx: ViewContext, body: Body): number {
  const d = length(sub(body.pos, ctx.sunPos))
  if (d <= 0) return ctx.sunLuminosity
  return ctx.sunLuminosity * Math.pow(AU / d, ctx.falloff)
}

/**
 * Radius the body's surface really occupies, in render units: its true radius, enlarged for the
 * diorama view (capped so moons never swallow their planets). No legibility floor, so this is what
 * the camera should stop at.
 */
export function solidRadius(ctx: ViewContext, body: Body): number {
  const r = body.radius * RENDER_SCALE
  if (ctx.dioramaAmount <= 0) return r
  const factor = body.kind === 'star' ? ctx.dioramaSun : ctx.dioramaPlanet
  let target = r * Math.pow(factor, ctx.dioramaAmount)
  if (body.kind !== 'star') {
    let nearest = Infinity
    for (const o of ctx.bodies) {
      if (o === body || o.mass < 1e20) continue
      const d = length(sub(o.pos, body.pos))
      if (d < nearest) nearest = d
    }
    if (isFinite(nearest)) target = Math.min(target, 0.25 * nearest * RENDER_SCALE)
  }
  return Math.max(r, target)
}

/** As solidRadius, but never smaller than a few pixels so a distant body stays visible. */
export function visualRadius(ctx: ViewContext, body: Body, distUnits: number, minPixels = ctx.minPixelRadius): number {
  const r = solidRadius(ctx, body)
  const pixels = (r / Math.max(distUnits, 1e-9)) * ctx.pixelsPerRadian
  return pixels < minPixels ? (minPixels * distUnits) / ctx.pixelsPerRadian : r
}

/** The few bodies that could cast a shadow on this one, as (position, radius) in units of the body's true radius. */
export function occludersFor(ctx: ViewContext, body: Body, invQ: THREE.Quaternion, out: THREE.Vector4[]): number {
  let n = 0
  const candidates: Array<{ b: Body; d: number }> = []
  for (const o of ctx.bodies) {
    if (o === body || o.kind === 'star' || o.radius < 50) continue
    const d = length(sub(o.pos, body.pos))
    if (d > 300 * o.radius + 50 * body.radius) continue
    candidates.push({ b: o, d })
  }
  candidates.sort((a, b) => a.d - b.d)
  const tmp = new THREE.Vector3()
  for (const c of candidates) {
    if (n >= 4) break
    const rel = sub(c.b.pos, body.pos)
    tmp.set(rel.x, rel.z, -rel.y).applyQuaternion(invQ).multiplyScalar(1 / body.radius)
    out[n].set(tmp.x, tmp.y, tmp.z, c.b.radius / body.radius)
    n++
  }
  return n
}

export const decay = (value: number, since: number, tau: number, now: number, rise = 0): number => {
  if (value <= 0) return 0
  const age = Math.max(0, now - since)
  const up = rise > 0 ? 1 - Math.exp(-age / rise) : 1
  return value * up * Math.exp(-age / Math.max(tau, 1))
}
