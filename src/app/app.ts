import * as THREE from 'three'
import { createSolarSystem, makeSatellite, MAJOR_MOON_RADIUS_KM, PLANET_ORDER } from '../sim/catalog'
import { SATELLITES } from '../sim/satellites'
import { Universe } from '../sim/universe'
import { TestParticles, populateBelts } from '../sim/particles'
import { Body, makeBody } from '../sim/body'
import { resolveCollision, ImpactOutcome } from '../sim/collide'
import { predictPath, autoAim, PredictResult } from '../sim/aim'
import { stateToElements, conicPoints } from '../sim/kepler'
import { AU, G, SUN_MASS, SUN_LUMINOSITY, STEFAN_BOLTZMANN, DAY, YEAR } from '../sim/constants'
import { Vec3, add, sub, scale, normalize, length, clamp, dot } from '../sim/vec'
import { SceneSetup } from '../render/scene'
import { CameraRig } from '../render/camera'
import { PlanetView } from '../render/planetView'
import { SunView } from '../render/sunView'
import { RockView } from '../render/rockView'
import { Sky } from '../render/sky'
import { BeltView } from '../render/belt'
import { PolyLine, Trail } from '../render/lines'
import { ImpactEffects } from '../render/effects'
import { BodyActions, BodyView, solidRadius, ViewContext } from '../render/viewContext'
import { worldToBodyFrame, bodyOrientation } from '../render/orientation'
import { pumpTextureUploads, setTextureCeiling, sweepTextures, SurfaceMask, radialSprite, textureQuality } from '../render/textures'
import { blackbody, RENDER_SCALE, toRender } from '../render/units'
import { Hud } from '../ui/hud'
import * as F from '../ui/format'

import { COMPOSITION_COLOR, COMPOSITION_DENSITY, Mode, MoonDetail, Preset, Settings, AsteroidSettings } from './types'
interface Aim {
  spawn: Vec3
  current: Vec3
  startX: number
  startY: number
  px: number
  py: number
  dirty: boolean
  result?: PredictResult
  vel?: Vec3
  speed: number
  primary?: Body
}

export class App {
  universe: Universe
  scene: SceneSetup
  rig: CameraRig
  hud!: Hud
  views = new Map<string, BodyView>()
  mode: Mode = 'explore'
  settings: Settings = { moons: 'all', textures: textureQuality(), orbits: true, trails: true, labels: true, belt: true, bloom: true, cinematic: true, diorama: false, falloff: 0.5 }
  asteroid: AsteroidSettings = { diameterM: 20, composition: 'rock', speedKms: 20 }
  selected: Body | null = null
  warp = 1
  paused = false
  rewind = false
  fps = 0
  sunState = { luminosity: 1, watts: SUN_LUMINOSITY, tempK: 5772, color: new THREE.Color(1, 0.96, 0.9) }
  lastImpact: ImpactOutcome | null = null
  lastImpactDeltaV = 0
  bodiesVersion = 0
  exposure = 1

  private sky: Sky
  private beltView: BeltView
  private effects = new ImpactEffects()
  private orbitLines = new Map<string, PolyLine>()
  private trails = new Map<string, Trail>()
  private periods = new Map<string, number>()
  /**
   * Per-body orbit-line state, reused frame to frame. At 240 bodies the lines used to scan every
   * body for every asteroid's primary (57,600 iterations a frame) and allocate ~540 objects per body
   * per frame for the conic, which in a debris cloud is a garbage-collection hitch every second.
   */
  private lineState = new Map<string, { primaryId: string | null; primaryAt: number; pts: Vec3[]; conicAt: number }>()
  private lineFrame = 0
  private lineOrigin: Vec3 = { x: 0, y: 0, z: 0 }
  /** Projection times view, rebuilt each frame for the frustum the views test their maps against. */
  private viewMatrix = new THREE.Matrix4()
  private pathLine: PolyLine
  private pathUntil = 0
  private hitMarker: THREE.Sprite
  private spawnMarker: THREE.Sprite
  private surfaceMask: SurfaceMask | null = null
  private outcomes: ImpactOutcome[] = []
  private cinematicUntil = 0
  private dioramaAmount = 0
  private realTime = 0
  private lastFrame = 0
  private asteroidCount = 0
  private ctx: ViewContext
  private aiming: Aim | null = null
  private drag: { button: number; x: number; y: number; moved: boolean; shift: boolean } | null = null
  private keys = new Set<string>()
  private brush: HTMLElement | null = null
  private brushRadius = 46
  private erased = 0
  private lastClick = { time: 0, id: '' }
  private hudTimer = 0
  private textureSweep = 0
  private frames = 0
  private fpsTime = 0
  private labelLayer: HTMLElement
  private stage: HTMLElement
  private loopError = false
  /** Simulated seconds the last frame advanced, signed; trails use it to tell when they would alias. */
  private lastDtSim = 0
  private tmpV = new THREE.Vector3()

  constructor(stage: HTMLElement, labelLayer: HTMLElement) {
    this.stage = stage
    this.labelLayer = labelLayer
    const { bodies, epochJD } = createSolarSystem(new Date())
    this.universe = new Universe(epochJD)
    this.universe.setBodies(bodies)
    // Bound the physics cost per frame: the substep count follows from how many pairs there are, so
    // adding moons throttles the reachable time warp instead of dropping the frame rate.
    this.universe.maxPairEvalsPerFrame = 3e5
    const particles = new TestParticles(4200)
    populateBelts(particles, this.universe.byId('sun')!, this.universe.byId('jupiter'))
    this.universe.particles = particles

    this.scene = new SceneSetup(stage, labelLayer)
    this.rig = new CameraRig(this.scene.camera)
    this.sky = new Sky()
    this.scene.scene.add(this.sky.group)
    this.beltView = new BeltView(particles)
    this.scene.scene.add(this.beltView.points)
    this.pathLine = new PolyLine(700, '#9fd0ff', 0.95)
    this.scene.scene.add(this.pathLine.line)
    const markerTex = radialSprite(64, 1.2)
    this.hitMarker = new THREE.Sprite(new THREE.SpriteMaterial({ map: markerTex, color: new THREE.Color(3, 1.2, 0.6), transparent: true, blending: THREE.AdditiveBlending, depthTest: false, depthWrite: false }))
    this.spawnMarker = new THREE.Sprite(new THREE.SpriteMaterial({ map: markerTex, color: new THREE.Color(0.6, 1.6, 3), transparent: true, blending: THREE.AdditiveBlending, depthTest: false, depthWrite: false }))
    this.hitMarker.visible = false
    this.spawnMarker.visible = false
    this.scene.scene.add(this.hitMarker, this.spawnMarker)

    this.ctx = {
      origin: { x: 0, y: 0, z: 0 }, jd: epochJD, simTime: 0, realTime: 0, sunPos: { x: 0, y: 0, z: 0 }, sunRadiusKm: 695700,
      sunColor: this.sunState.color, sunLuminosity: 1, sunWatts: SUN_LUMINOSITY, falloff: 0.5, exposureComp: 1,
      dioramaAmount: 0, dioramaPlanet: 800, dioramaSun: 30, minPixelRadius: 3.5, pixelsPerRadian: 1000,
      bodies: this.universe.bodies, cameraQuaternion: this.scene.camera.quaternion,
      textureCeiling: textureQuality(), pixelRatio: 1, frustum: new THREE.Frustum(), focusId: null,
    }

    this.universe.collisionHandlers.push((ev, u) => {
      const out = resolveCollision(u, ev, {
        bodyFrame: (b, d) => worldToBodyFrame(b, d, u.jd),
        surfaceAt: (b, d) => (b.id === 'earth' && this.surfaceMask?.isWater(d) ? { water: true, depth: 3700 } : { water: false, depth: 0 }),
      })
      this.outcomes.push(out)
    })
    SurfaceMask.load('earth_specular.jpg').then((m) => { this.surfaceMask = m })

    this.syncViews()
    this.hud = new Hud(this)
    this.bindInput()
    const earth = this.universe.byId('earth')!
    this.select(earth)
    this.flyAbove(earth, true)
    this.hud.hint('Drag or Q/E to orbit, WASD to pan, scroll to zoom, X for asteroid mode, H for help', 6000)
    this.lastFrame = performance.now()
    requestAnimationFrame((t) => this.frame(t))
  }

  // ---------- public API used by the HUD ----------

  /**
   * The body `b` orbits, for the inspector and the orbit lines. A catalogued planet or moon keeps its
   * catalogued primary while it is still bound to it. The physics rule alone picks the nearest
   * heavier body `b` is bound to, afresh every frame, so a moon made heavy with the mass slider
   * captures its neighbours on each close pass and their orbit lines swing between the planet and
   * the moon from one frame to the next. Asteroids and fragments have no catalogued primary worth
   * the name and keep the physics rule.
   */
  primaryOf(b: Body): Body | undefined {
    if ((b.kind === 'moon' || b.kind === 'planet') && b.primaryId) {
      const p = this.universe.byId(b.primaryId)
      if (p && p !== b && p.mass > b.mass) {
        const r = length(sub(b.pos, p.pos))
        const v = sub(b.vel, p.vel)
        if (r > 0 && dot(v, v) / 2 < (G * (p.mass + b.mass)) / r) return p
      }
    }
    return this.universe.primaryOf(b)
  }

  viewFor(b: Body): BodyView | undefined {
    return this.views.get(b.id)
  }

  select(b: Body | null): void {
    this.selected = b
    this.hud?.syncInspector()
  }

  follow(b: Body): void {
    this.rig.flyTo({ focus: b, distance: Math.max(this.rig.distance, b.radius * 3) }, 1.2)
  }

  flyAbove(b: Body, instant = false): void {
    // Use the true radius: the diorama is switched off for close-ups and far bodies carry an inflated on-screen radius.
    const R = b.radius
    const sun = this.star()
    let yaw = this.rig.goalYaw
    if (b !== sun) yaw = CameraRig.yawTowards(normalize(sub(sun.pos, b.pos))) + 0.55
    const spec = { focus: b, distance: b.kind === 'star' ? R * 6 : R * 4.2, yaw, pitch: 0.3 }
    this.settings.diorama = false
    if (instant) this.rig.set(spec)
    else this.rig.flyTo(spec, 1.6)
  }

  setWarp(w: number): void {
    this.warp = clamp(w, 1, 1e8)
    this.paused = false
  }

  togglePause(): void {
    this.paused = !this.paused
  }

  toggleRewind(): void {
    this.rewind = !this.rewind
    this.hud.hint(this.rewind ? 'Rewinding. Gravity runs backwards exactly; impacts are undone from the report card.' : 'Time runs forward again')
  }

  setMode(m: Mode): void {
    this.mode = m
    if (m !== 'asteroid') this.cancelAim()
    this.hud.setMode(m)
    this.stage.classList.toggle('erasing', m === 'erase')
    if (m !== 'erase') this.showBrush(null)
    if (m === 'asteroid') this.hud.hint('Asteroid mode: click any body — planet, moon or rock you fired earlier — to shoot at it, or drag on empty space to aim by hand. Right-drag orbits.', 5500)
    if (m === 'erase') this.hud.hint('Erase mode: drag over the view to delete asteroids and fragments. Wheel resizes the brush; right-drag still orbits.', 5500)
  }

  /**
   * Delete every asteroid or fragment whose dot on screen falls inside the brush. Catalogued bodies
   * are left alone — this is for clearing up debris, not for demolishing the solar system.
   */
  eraseAt(px: number, py: number): number {
    const cam = this.scene.camera
    const v = new THREE.Vector3()
    const doomed: Body[] = []
    for (const view of this.views.values()) {
      const b = view.body
      if (b.kind !== 'asteroid' && b.kind !== 'fragment') continue
      v.copy(view.group.position).project(cam)
      if (v.z < -1 || v.z > 1) continue
      const sx = ((v.x + 1) / 2) * this.scene.width
      const sy = ((1 - v.y) / 2) * this.scene.height
      const pixR = (view.group.scale.x / Math.max(view.group.position.length(), 1e-9)) * this.scene.pixelsPerRadian
      if (Math.hypot(sx - px, sy - py) <= this.brushRadius + pixR) doomed.push(b)
    }
    for (const b of doomed) {
      if (this.rig.focus === b) this.rig.focus = null
      if (this.selected === b) this.select(null)
      if (this.lastImpact && (this.lastImpact.impactor === b || this.lastImpact.target === b)) {
        this.lastImpact = null
        this.hud.hideReport()
      }
      this.universe.remove(b)
      this.periods.delete(b.id)
    }
    if (doomed.length) this.syncViews()
    return doomed.length
  }

  viewTabletop(): void {
    this.settings.diorama = true
    this.rig.flyTo({ focus: this.star(), distance: 36 * AU, pitch: 0.78 }, 2.2)
  }

  viewInner(): void {
    this.settings.diorama = true
    this.rig.flyTo({ focus: this.star(), distance: 2.7 * AU, pitch: 0.62 }, 2)
  }

  viewOuter(): void {
    this.settings.diorama = true
    this.rig.flyTo({ focus: this.star(), distance: 75 * AU, pitch: 0.9 }, 2.2)
  }

  resetBody(b: Body): void {
    b.mass = b.mass0
    b.radius = b.radius0
    this.hud.syncInspector()
  }

  undoImpact(): void {
    const impactorId = this.lastImpact?.impactor.id
    if (!this.universe.popHistory()) return
    const impactor = impactorId ? this.universe.byId(impactorId) : undefined
    if (impactor) this.universe.remove(impactor)
    this.rebindAfterRestore()
    this.hud.hideReport()
    this.lastImpact = null
    this.siteLock = null
    this.cinematicUntil = 0
    this.hud.hint(impactor ? `Impact undone; ${impactor.name} removed` : 'Impact undone')
  }

  lookAtLastImpact(): void {
    const o = this.lastImpact
    if (!o) return
    const view = this.views.get(o.target.id)
    if (view) this.lookAtImpact(o, view)
  }

  /**
   * Add or drop satellites. Every extra body costs force evaluations, and the inner moons force short
   * substeps (Phobos orbits Mars in eight hours), so dropping them is what buys back the high warps.
   */
  setMoonDetail(level: MoonDetail): void {
    this.settings.moons = level
    const u = this.universe
    for (const sat of SATELLITES) {
      const wanted = level === 'all' || (level === 'major' && sat.radius >= MAJOR_MOON_RADIUS_KM)
      const present = u.byId(sat.id)
      if (wanted && !present) {
        const primary = u.byId(sat.primaryId)
        if (primary) u.add(makeSatellite(sat, primary, u.jd))
      } else if (!wanted && present) {
        const primary = u.byId(sat.primaryId) ?? null
        if (this.rig.focus === present) this.rig.focus = primary
        if (this.selected === present) this.select(primary)
        u.remove(present)
      }
    }
    this.syncViews()
  }

  /**
   * Raise or lower the ceiling on map size. Bodies stream up to it as they grow on screen, so this
   * caps the peak rather than forcing every map to that size; the loader releases whatever the new
   * ceiling leaves unwanted.
   */
  setTextureQuality(width: number): void {
    this.settings.textures = width
    setTextureCeiling(width)
    this.hud.hint(`Texture detail: up to ${width >= 8192 ? '8K' : width >= 4096 ? '4K' : '2K'} when close`, 2600)
  }

  applyPreset(p: Preset): void {
    this.asteroid = { diameterM: p.diameterM, composition: p.composition, speedKms: p.speedKms, name: p.name }
  }

  // ---------- internals ----------

  private showBrush(at: { x: number; y: number } | null): void {
    if (!this.brush) this.brush = document.getElementById('brush')
    const el = this.brush
    if (!el) return
    if (!at) { el.style.display = 'none'; return }
    el.style.display = 'block'
    el.style.width = `${this.brushRadius * 2}px`
    el.style.height = `${this.brushRadius * 2}px`
    el.style.left = `${at.x}px`
    el.style.top = `${at.y}px`
  }

  private star(): Body {
    return this.universe.byId('sun') ?? this.universe.bodies.find((b) => b.kind === 'star') ?? this.universe.bodies[0]
  }

  private bodyActions: BodyActions = {
    select: (b) => this.select(b),
    activate: (b) => { this.select(b); this.flyAbove(b) },
  }

  private createView(b: Body): void {
    const actions = this.bodyActions
    let v: BodyView
    if (b.kind === 'star') {
      const s = new SunView(b, actions)
      this.scene.scene.add(s.glowGroup)
      v = s
    } else if (b.kind === 'planet' || b.kind === 'moon') {
      v = new PlanetView(b, actions)
    } else {
      v = new RockView(b, actions)
    }
    this.scene.scene.add(v.group)
    this.views.set(b.id, v)
    if (b.kind !== 'star') {
      const line = new PolyLine(200, b.visual.color, 0.45)
      this.scene.scene.add(line.line)
      this.orbitLines.set(b.id, line)
      const trail = new Trail(360, b.visual.color)
      this.scene.scene.add(trail.line.line)
      this.trails.set(b.id, trail)
    }
  }

  private removeView(id: string): void {
    const v = this.views.get(id)
    if (v) {
      this.scene.scene.remove(v.group)
      if (v instanceof SunView) this.scene.scene.remove(v.glowGroup)
      v.dispose()
      this.views.delete(id)
    }
    const line = this.orbitLines.get(id)
    if (line) { this.scene.scene.remove(line.line); line.dispose(); this.orbitLines.delete(id) }
    const trail = this.trails.get(id)
    if (trail) { this.scene.scene.remove(trail.line.line); trail.dispose(); this.trails.delete(id) }
    this.periods.delete(id)
    this.lineState.delete(id)
  }

  private syncViews(): void {
    let changed = false
    for (const b of this.universe.bodies) {
      if (!this.views.has(b.id)) { this.createView(b); changed = true }
    }
    for (const id of Array.from(this.views.keys())) {
      if (!this.universe.byId(id)) { this.removeView(id); changed = true }
    }
    if (changed) this.bodiesVersion++
  }

  private stateIsFinite(): boolean {
    for (const b of this.universe.bodies) {
      if (!Number.isFinite(b.pos.x + b.pos.y + b.pos.z + b.vel.x + b.vel.y + b.vel.z)) return false
    }
    return Number.isFinite(this.universe.time)
  }

  /**
   * A non-finite position or velocity poisons the sim for good. NaN fails every comparison, so
   * collision detection switches off without a word and the integrator goes on spending its budget
   * on garbage, spreading the NaN to every body through the force sum. `step()` may not validate or
   * clamp (see src/sim/CLAUDE.md), so the app checks after it, rolls back to the last undo point and
   * stops the clock so the player can see what happened rather than watch it silently rot.
   */
  private recoverFromFault(): void {
    const restored = this.universe.popHistory()
    this.paused = true
    console.error(`[spayly] non-finite body state after a step; ${restored ? 'rolled back to the last undo point' : 'no undo point to roll back to'}`)
    if (!restored) {
      this.hud.hint('The simulation hit a numerical fault and has been paused. Reset the scene to carry on.', 8000)
      return
    }
    this.rebindAfterRestore()
    this.hud.hideReport()
    this.lastImpact = null
    this.siteLock = null
    this.cinematicUntil = 0
    this.hud.hint('The simulation hit a numerical fault, so it was rolled back to before the last impact and paused.', 8000)
  }

  private rebindAfterRestore(): void {
    for (const id of Array.from(this.views.keys())) this.removeView(id)
    this.syncViews()
    this.rig.focus = this.rig.focus ? this.universe.byId(this.rig.focus.id) ?? null : null
    this.selected = this.selected ? this.universe.byId(this.selected.id) ?? null : null
    this.hud.syncInspector()
    this.bodiesVersion++
  }

  private updateSunState(): void {
    const sun = this.star()
    const s = this.sunState
    if (sun.kind !== 'star') {
      s.luminosity = 0
      s.watts = 0
      s.tempK = 1000
      s.color.setRGB(1, 0.6, 0.4)
      return
    }
    const mRel = sun.mass / SUN_MASS
    const lum = mRel < 0.08 ? 1e-4 * Math.pow(mRel / 0.08, 2) : Math.pow(mRel, 3.5)
    const watts = lum * SUN_LUMINOSITY
    const R = sun.radius * 1000
    const T = Math.pow(watts / (4 * Math.PI * R * R * STEFAN_BOLTZMANN), 0.25)
    s.luminosity = lum
    s.watts = watts
    s.tempK = T
    s.color.copy(blackbody(T))
  }

  private frame(now: number): void {
    requestAnimationFrame((t) => this.frame(t))
    if (this.loopError) return
    try {
      this.tick(now)
    } catch (err) {
      this.loopError = true
      console.error(err)
      this.hud?.hint(`Error: ${(err as Error).message}`, 60000)
    }
  }

  private tick(now: number): void {
    const dtReal = clamp((now - this.lastFrame) / 1000, 0, 0.1)
    this.lastFrame = now
    this.realTime += dtReal
    this.frames++
    this.fpsTime += dtReal
    if (this.fpsTime >= 0.5) {
      this.fps = this.frames / this.fpsTime
      this.frames = 0
      this.fpsTime = 0
    }
    const dioramaTarget = this.settings.diorama ? 1 : 0
    this.dioramaAmount += clamp(dioramaTarget - this.dioramaAmount, -dtReal * 1.4, dtReal * 1.4)

    if (!this.paused) {
      const w = now < this.cinematicUntil ? Math.min(this.warp, 1) : this.warp
      const dtSim = (this.rewind ? -1 : 1) * w * dtReal
      this.universe.step(dtSim)
      this.lastDtSim = dtSim
      if (!this.stateIsFinite()) this.recoverFromFault()
    } else this.lastDtSim = 0
    this.syncViews()
    this.updateSunState()

    const sun = this.star()
    const focusBody = this.rig.focus ?? this.selected ?? this.universe.byId('earth')
    let irr = 1
    if (focusBody && focusBody.kind !== 'star') {
      const d = Math.max(length(sub(focusBody.pos, sun.pos)), 1)
      irr = Math.max(this.sunState.luminosity, 1e-6) * Math.pow(AU / d, this.settings.falloff)
    }
    const targetExposure = clamp(Math.pow(irr, -0.85), 1e-3, 100)
    const k = 1 - Math.exp(-dtReal * 3)
    this.exposure = Math.exp(Math.log(this.exposure) + (Math.log(targetExposure) - Math.log(this.exposure)) * k)

    // Stop the camera at the body's real surface. Using the on-screen radius here would feed the
    // minimum-pixel legibility floor back into the camera and shove it away from anything distant.
    const camFocus = this.rig.focus
    this.rig.focusRadiusKm = camFocus
      ? (solidRadius(this.ctx, camFocus) / RENDER_SCALE) * (camFocus.kind === 'star' ? 1.25 : 1.03)
      : 0
    this.updateSiteLock(now)
    this.flyWithKeys(dtReal)
    this.rig.update(dtReal)
    this.scene.setNearForDistance(this.rig.distance * RENDER_SCALE)

    const ctx = this.ctx
    ctx.origin = this.rig.position
    ctx.jd = this.universe.jd
    ctx.simTime = this.universe.time
    ctx.realTime = this.realTime
    ctx.sunPos = sun.pos
    ctx.sunRadiusKm = sun.radius
    ctx.sunLuminosity = this.sunState.luminosity
    ctx.sunWatts = this.sunState.watts
    ctx.falloff = this.settings.falloff
    ctx.exposureComp = 1 / this.exposure
    ctx.dioramaAmount = this.dioramaAmount
    ctx.pixelsPerRadian = this.scene.pixelsPerRadian
    ctx.bodies = this.universe.bodies
    ctx.cameraQuaternion = this.scene.camera.quaternion
    ctx.textureCeiling = this.settings.textures
    ctx.pixelRatio = this.scene.renderer.getPixelRatio()
    ctx.focusId = this.rig.focus?.id ?? null
    const cam = this.scene.camera
    cam.updateMatrixWorld()
    this.viewMatrix.multiplyMatrices(cam.projectionMatrix, cam.matrixWorldInverse)
    ctx.frustum.setFromProjectionMatrix(this.viewMatrix)

    for (const v of this.views.values()) v.update(ctx)
    this.updateLabels()
    this.updateLines()
    this.beltView.points.visible = this.settings.belt
    if (this.settings.belt) this.beltView.update(ctx.origin, ctx.exposureComp, this.scene.dpr)
    this.sky.update(ctx.exposureComp, this.scene.dpr)
    this.effects.update(dtReal, ctx)
    this.handleOutcomes()
    this.updateAim()
    this.labelLayer.style.visibility = this.settings.labels ? '' : 'hidden'

    this.textureSweep += dtReal
    if (this.textureSweep > 4) {
      this.textureSweep = 0
      sweepTextures()
    }

    this.hudTimer += dtReal
    if (this.hudTimer > 0.12) {
      this.hudTimer = 0
      this.hud.update()
    }
    this.scene.exposure = this.exposure
    this.scene.bloom = this.settings.bloom
    pumpTextureUploads()
    this.scene.render()
  }

  /** WASD slides the view, Q/E swings it around; hold shift to go faster. */
  private flyWithKeys(dt: number): void {
    const k = this.keys
    const right = (k.has('d') ? 1 : 0) - (k.has('a') ? 1 : 0)
    const up = (k.has('w') ? 1 : 0) - (k.has('s') ? 1 : 0)
    const spin = (k.has('q') ? 1 : 0) - (k.has('e') ? 1 : 0)
    if (!right && !up && !spin) return
    this.siteLock = null
    const boost = k.has('shift') ? 3.5 : 1
    if (right || up) {
      const speed = this.rig.distance * 0.85 * boost * dt
      this.rig.panBy(right * speed, up * speed)
    }
    if (spin) this.rig.rotateBy(spin * 1.5 * boost * dt, 0)
  }

  private updateLines(): void {
    const origin = this.rig.position
    const t = this.universe.time
    const focusView = this.rig.focus ? this.views.get(this.rig.focus.id) : undefined
    const focusPix = focusView ? (focusView.group.scale.x / Math.max(focusView.group.position.length(), 1e-9)) * this.scene.pixelsPerRadian : 0
    const closeFade = clamp(1.3 - focusPix / 220, 0.15, 1)
    const lf = ++this.lineFrame
    const bodies = this.universe.bodies
    for (let i = 0; i < bodies.length; i++) {
      const b = bodies[i]
      if (b.kind === 'star') continue
      const line = this.orbitLines.get(b.id)
      const trail = this.trails.get(b.id)
      if (!line || !trail) continue
      let st = this.lineState.get(b.id)
      if (!st) this.lineState.set(b.id, (st = { primaryId: null, primaryAt: -Infinity, pts: [], conicAt: -Infinity }))
      // What a body orbits changes over many frames, not one. Re-derive it every tenth frame,
      // staggered by index so the O(n) scans spread evenly instead of landing on the same frame.
      let p = st.primaryId ? this.universe.byId(st.primaryId) : undefined
      if (!p || (lf + i) % 10 === 0 || lf - st.primaryAt > 10) {
        p = this.primaryOf(b)
        st.primaryId = p?.id ?? null
        st.primaryAt = lf
      }
      let period = this.periods.get(b.id) ?? 30 * DAY
      if (p) {
        const mu = G * (p.mass + b.mass)
        const r = sub(b.pos, p.pos)
        const v = sub(b.vel, p.vel)
        const el = stateToElements(mu, r, v)
        period = isFinite(el.period) ? el.period : (2 * Math.PI * length(r)) / Math.max(length(v), 1e-6)
        this.periods.set(b.id, period)
        const small = b.kind === 'asteroid' || b.kind === 'fragment'
        const bound = el.e < 1
        if (this.settings.orbits && (bound || b.kind === 'asteroid')) {
          const view = this.views.get(b.id)
          const gap = Math.max(0.02, (3 * (view?.visualRadiusKm ?? b.radius)) / Math.max(length(r), 1))
          const rMax = bound ? 120 * AU : 20 * length(r)
          // Fragments are faint and many: their conics refresh every fourth frame.
          if (b.kind !== 'fragment' || lf - st.conicAt >= 4) {
            conicPoints(mu, r, v, 180, rMax, el.nu, gap, st.pts)
            st.conicAt = lf
          }
          const pts = st.pts
          // The points are relative to the primary; shift the origin instead of every point.
          const o = this.lineOrigin
          o.x = origin.x - p.pos.x; o.y = origin.y - p.pos.y; o.z = origin.z - p.pos.z
          line.set(pts, o, 0, (b.kind === 'fragment' ? 0.35 : small ? 0.7 : 1) * closeFade)
          line.line.visible = pts.length > 1
        } else line.line.visible = false
      } else line.line.visible = false
      // A moon trails in its planet's frame, where its path is the orbit; in the Sun's frame it is a
      // corkscrew stretched along the planet's path, which at speed smears arcs across the system.
      const frame = b.kind === 'moon' && p && p.id === b.primaryId ? p : undefined
      trail.useFrame(frame?.id ?? '')
      // One sample a frame at most: once a frame spans a good fraction of the orbit the samples land
      // at random points on it and the line between them zig-zags, so drop the trail until warp eases.
      const aliased = Math.abs(this.lastDtSim) > period / 16
      if (this.settings.trails && !aliased) {
        trail.push(frame ? sub(b.pos, frame.pos) : b.pos, t, clamp(period / 400, 1, 30 * DAY))
        trail.update(origin, b.pos, closeFade, frame?.pos)
        trail.line.line.visible = true
      } else {
        if (aliased) trail.reset()
        trail.line.line.visible = false
      }
    }
  }

  /**
   * Hide labels that sit on something nearer, then thin out the rest where they overlap.
   * CSS2DRenderer only checks the frustum, so without this a body 19 AU behind Earth still writes
   * its name across Earth's disc, and a planet's moons pile their names into one smear. Runs after
   * the views, which have already hidden the labels of bodies that fill the screen.
   */
  private updateLabels(): void {
    if (!this.settings.labels) return
    const cam = this.scene.camera
    const W = this.scene.width
    const H = this.scene.height
    const ppr = this.scene.pixelsPerRadian
    const v = this.tmpV
    interface Mark { view: BodyView; x: number; y: number; r: number; d: number; box?: [number, number, number, number] }
    const marks: Mark[] = []
    for (const view of this.views.values()) {
      const d = view.group.position.length()
      v.copy(view.group.position).project(cam)
      if (v.z < -1 || v.z > 1) continue
      marks.push({ view, x: ((v.x + 1) / 2) * W, y: ((1 - v.y) / 2) * H, r: (view.group.scale.x / Math.max(d, 1e-9)) * ppr, d })
    }
    marks.sort((a, b) => a.d - b.d)
    // Only a disc big enough to read as a body hides what is behind it; a far planet's dot sitting
    // on a label is left to the overlap pass below.
    const occluders = marks.filter((m) => m.r >= 8)
    const candidates: Mark[] = []
    for (const m of marks) {
      const label = m.view.label
      if (!label.visible) continue
      // The label's bottom edge sits `height` radii straight up the screen from the body's centre.
      const ay = m.y - label.position.length() * m.r
      const halfW = 9 + m.view.body.name.length * 3.4
      const box: [number, number, number, number] = [m.x - halfW, ay - 18, m.x + halfW, ay]
      let hidden = false
      for (const o of occluders) {
        if (o.d >= m.d) break
        const cx = clamp(o.x, box[0], box[2])
        const cy = clamp(o.y, box[1], box[3])
        if (Math.hypot(o.x - m.x, o.y - m.y) < o.r || Math.hypot(o.x - cx, o.y - cy) < o.r) { hidden = true; break }
      }
      if (hidden) label.visible = false
      else { m.box = box; candidates.push(m) }
    }
    // Where labels collide, the more important body keeps its name: whatever is selected or
    // followed, then planets before moons before rocks, then whichever is bigger on screen.
    const rank: Record<string, number> = { star: 0, planet: 1, moon: 2, asteroid: 3, fragment: 4 }
    const pinned = (m: Mark) => m.view.body === this.selected || m.view.body === this.rig.focus
    candidates.sort((a, b) => Number(pinned(b)) - Number(pinned(a)) || rank[a.view.body.kind] - rank[b.view.body.kind] || b.r - a.r)
    const placed: Array<[number, number, number, number]> = []
    for (const m of candidates) {
      const b = m.box!
      if (placed.some((p) => b[0] < p[2] && b[2] > p[0] && b[1] < p[3] && b[3] > p[1])) m.view.label.visible = false
      else placed.push(b)
    }
  }

  private handleOutcomes(): void {
    if (!this.outcomes.length) return
    for (const o of this.outcomes) {
      const view = this.views.get(o.target.id)
      if (!view) continue
      const velDirBody = worldToBodyFrame(o.target, o.velDirWorld, this.universe.jd)
      this.effects.spawn(o, view, velDirBody, this.ctx)
      if (view instanceof SunView) view.flare = 1.5
      const deltaV = o.targetDeltaV
      this.lastImpact = o
      this.lastImpactDeltaV = deltaV
      this.hud.showReport(o, deltaV)
      if (this.selected === o.impactor || this.selected === null) this.select(o.target)
      if (this.rig.focus === o.impactor) this.rig.focus = o.target
      if (this.settings.cinematic) {
        this.cinematicUntil = performance.now() + 9000
        this.lookAtImpact(o, view)
      }
      this.hud.hint(`${o.impactor.name} hit ${o.target.name} at ${F.speed(o.relSpeed)}`, 4000)
    }
    this.outcomes.length = 0
    this.bodiesVersion++
  }

  private siteLock: { body: Body; dirBody: Vec3; until: number } | null = null

  private lookAtImpact(o: ImpactOutcome, view: BodyView): void {
    const n = o.normalWorld
    const R = view.visualRadiusKm
    const rf = (o.report?.fireballRadius ?? 0) / 1000
    const D = (o.report?.crater?.finalDiameter ?? 0) / 1000
    const shattered = o.kind === 'shatter' || o.kind === 'disperse'
    const altitude = shattered ? 3.5 * o.target.radius : clamp(Math.max(rf * 18, D * 5), 0.12 * o.target.radius, 0.7 * o.target.radius)
    const dist = R * (1 + altitude / o.target.radius)
    this.siteLock = { body: o.target, dirBody: o.dirBody, until: performance.now() + 9000 }
    this.rig.flyTo({ focus: o.target, distance: dist, yaw: CameraRig.yawTowards(n), pitch: Math.asin(clamp(n.z, -1, 1)) }, 1.2)
  }

  /** Keep the camera over the impact site while the planet rotates underneath. */
  private updateSiteLock(now: number): void {
    const lock = this.siteLock
    if (!lock) return
    if (now > lock.until || this.rig.focus !== lock.body || !this.universe.byId(lock.body.id)) {
      this.siteLock = null
      return
    }
    if (this.rig.flying) return
    const q = bodyOrientation(lock.body, this.universe.jd)
    const v = new THREE.Vector3(lock.dirBody.x, lock.dirBody.y, lock.dirBody.z).applyQuaternion(q)
    const dirSim = { x: v.x, y: -v.z, z: v.y }
    const yaw = CameraRig.yawTowards(dirSim)
    const pitch = Math.asin(clamp(dirSim.z, -1, 1))
    this.rig.yaw = this.rig.goalYaw = yaw
    this.rig.pitch = this.rig.goalPitch = pitch
  }

  // ---------- asteroid mode ----------

  private aimPlanePoint(x: number, y: number): Vec3 | null {
    const ndx = (x / this.scene.width) * 2 - 1
    const ndy = -(y / this.scene.height) * 2 + 1
    const dir = this.rig.rayDir(ndx, ndy)
    const pos = this.rig.position
    const planeZ = this.rig.target.z
    if (Math.abs(dir.z) < 1e-9) return null
    const t = (planeZ - pos.z) / dir.z
    if (!(t > 0)) return null
    return add(pos, scale(dir, t))
  }

  private pickBody(x: number, y: number): Body | null {
    const cam = this.scene.camera
    const v = new THREE.Vector3()
    const sorted = Array.from(this.views.values()).sort((a, b) => a.group.position.lengthSq() - b.group.position.lengthSq())
    for (const view of sorted) {
      v.copy(view.group.position).project(cam)
      if (v.z < -1 || v.z > 1) continue
      const sx = ((v.x + 1) / 2) * this.scene.width
      const sy = ((1 - v.y) / 2) * this.scene.height
      const dist = view.group.position.length()
      const pixR = (view.group.scale.x / Math.max(dist, 1e-9)) * this.scene.pixelsPerRadian
      const d = Math.hypot(sx - x, sy - y)
      if (d <= Math.max(pixR + 4, 14)) return view.body
    }
    return null
  }

  private startAim(p: Vec3, px: number, py: number): void {
    this.aiming = { spawn: p, current: p, startX: px, startY: py, px, py, dirty: true, speed: 0, primary: this.universe.dominantBody(p) }
  }

  private cancelAim(): void {
    this.aiming = null
    this.pathLine.clear()
    this.hitMarker.visible = false
    this.spawnMarker.visible = false
    this.hud?.setAimText('')
  }

  private updateAim(): void {
    const a = this.aiming
    const origin = this.rig.position
    if (!a) {
      if (this.pathUntil > this.realTime && this.lastPath) {
        this.pathLine.set(this.lastPath, origin, 0, 0.8)
      } else {
        this.pathLine.clear()
        this.hitMarker.visible = false
      }
      return
    }
    const dragPx = Math.hypot(a.px - a.startX, a.py - a.startY)
    const dirVec = sub(a.current, a.spawn)
    toRender(a.spawn, origin, this.spawnMarker.position)
    this.placeMarker(this.spawnMarker, 10)
    if (length(dirVec) < 1 || dragPx < 4) {
      a.speed = 0
      this.pathLine.clear()
      this.hitMarker.visible = false
      this.hud.setAimText('Drag to set the direction; drag further for more speed')
      return
    }
    a.speed = clamp((this.asteroid.speedKms * dragPx) / 150, 0.3, 500)
    const dir = normalize(dirVec)
    const prim = a.primary ?? this.star()
    a.vel = add(prim.vel, scale(dir, a.speed))
    if (a.dirty) {
      a.result = predictPath(this.universe, { pos: a.spawn, vel: a.vel, radius: this.asteroid.diameterM / 2000 }, 2 * YEAR, { maxSteps: 2500, maxPoints: 600, pairBudget: 3e6 })
      a.dirty = false
    }
    const res = a.result
    if (!res) return
    this.pathLine.set(res.points, origin, 0, 1)
    if (res.hit) {
      toRender(res.hit.pos, origin, this.hitMarker.position)
      this.placeMarker(this.hitMarker, 14)
      const target = this.universe.byId(res.hit.bodyId)
      this.hud.setAimText(`${F.speed(a.speed)} relative to ${prim.name} → hits ${target?.name ?? res.hit.bodyId} in ${F.duration(res.hit.time)} at ${F.speed(res.hit.speed)}`)
    } else {
      this.hitMarker.visible = false
      this.hud.setAimText(`${F.speed(a.speed)} relative to ${prim.name} · no impact within ${F.duration(res.duration)}`)
    }
  }

  private lastPath: Vec3[] | null = null

  private placeMarker(m: THREE.Sprite, px: number): void {
    const dist = m.position.length()
    const s = (px * dist) / this.scene.pixelsPerRadian
    m.scale.set(s, s, 1)
    m.visible = true
  }

  private fireManual(): void {
    const a = this.aiming
    if (a?.vel && a.speed > 0) {
      this.cinematicUntil = 0
      const target = a.result?.hit ? this.universe.byId(a.result.hit.bodyId) : undefined
      this.launch(a.spawn, a.vel, target)
      this.lastPath = a.result?.points ?? null
      this.pathUntil = this.realTime + 3
    }
    this.cancelAim()
  }

  private fireAt(target: Body): void {
    const astR = this.asteroid.diameterM / 2000
    let from = { ...this.rig.position }
    for (const b of this.universe.bodies) {
      const d = sub(from, b.pos)
      const min = b.radius * 1.6 + astR * 1.8
      if (length(d) < min) from = add(b.pos, scale(normalize(d), min))
    }
    const res = autoAim(this.universe, from, target.id, this.asteroid.speedKms, astR)
    if (!res) return
    this.cinematicUntil = 0
    const b = this.launch(from, res.vel, target)
    // A giant impactor spawned at the viewpoint would swallow the camera; back off to watch it fall.
    if (length(sub(this.rig.position, b.pos)) < astR * 1.8) {
      this.rig.flyTo({ focus: target, distance: Math.max(this.rig.distance, length(sub(from, target.pos)) * 1.7) }, 1.2)
    }
    this.lastPath = res.prediction.points
    this.pathUntil = this.realTime + 4
    if (res.converged && res.prediction.hit) {
      this.hud.hint(`${b.name} away! Hits ${target.name} in ${F.duration(res.prediction.hit.time)} at ${F.speed(res.prediction.hit.speed)}. Warp time to skip ahead.`, 5000)
    } else {
      this.hud.hint(`${b.name} launched toward ${target.name}; gravity may bend it off course`, 4000)
    }
  }

  private launch(pos: Vec3, vel: Vec3, target?: Body): Body {
    const a = this.asteroid
    const d = a.diameterM
    const rho = COMPOSITION_DENSITY[a.composition]
    const mass = rho * (Math.PI / 6) * Math.pow(d, 3)
    this.asteroidCount++
    const b = makeBody({
      id: `ast-${this.asteroidCount}`,
      name: `${a.name ?? 'Asteroid'} ${this.asteroidCount}`,
      kind: 'asteroid',
      surface: a.composition === 'ice' ? 'ice' : 'rock',
      mass,
      radius: d / 2000,
      pos: { ...pos },
      vel: { ...vel },
      primaryId: target?.id,
      visual: { color: COMPOSITION_COLOR[a.composition], albedo: a.composition === 'ice' ? 0.5 : 0.1 },
      born: this.universe.time,
    })
    this.universe.add(b)
    this.syncViews()
    return b
  }

  // ---------- input ----------

  private bindInput(): void {
    const stage = this.stage
    stage.addEventListener('pointerdown', (e) => {
      stage.setPointerCapture(e.pointerId)
      this.drag = { button: e.button, x: e.clientX, y: e.clientY, moved: false, shift: e.shiftKey }
      if (this.mode === 'erase' && e.button === 0 && !e.shiftKey) {
        this.erased = this.eraseAt(e.clientX, e.clientY)
        return
      }
      if (this.mode === 'asteroid' && e.button === 0 && !e.shiftKey) {
        const hit = this.pickBody(e.clientX, e.clientY)
        if (!hit) {
          const p = this.aimPlanePoint(e.clientX, e.clientY)
          if (p) this.startAim(p, e.clientX, e.clientY)
        }
      }
    })
    stage.addEventListener('pointermove', (e) => {
      if (this.mode === 'erase') this.showBrush({ x: e.clientX, y: e.clientY })
      const d = this.drag
      if (!d) return
      if (this.mode === 'erase' && d.button === 0 && !d.shift) {
        d.moved = true
        this.erased += this.eraseAt(e.clientX, e.clientY)
        d.x = e.clientX
        d.y = e.clientY
        return
      }
      const dx = e.clientX - d.x
      const dy = e.clientY - d.y
      if (Math.abs(e.clientX - d.x) + Math.abs(e.clientY - d.y) > 3) d.moved = true
      if (this.aiming) {
        const p = this.aimPlanePoint(e.clientX, e.clientY)
        if (p) {
          this.aiming.current = p
          this.aiming.px = e.clientX
          this.aiming.py = e.clientY
          this.aiming.dirty = true
        }
        return
      }
      const rotate = (this.mode === 'explore' && d.button === 0 && !d.shift) || (this.mode === 'asteroid' && d.button === 2)
      const pan = d.button === 1 || (d.button === 2 && this.mode === 'explore') || (d.button === 0 && d.shift)
      if (rotate || pan) this.siteLock = null
      if (rotate) this.rig.orbit(dx, dy)
      else if (pan) this.rig.pan(dx, dy)
      d.x = e.clientX
      d.y = e.clientY
    })
    const up = (e: PointerEvent) => {
      const d = this.drag
      if (!d) return
      this.drag = null
      try { stage.releasePointerCapture(e.pointerId) } catch { /* ignore */ }
      if (this.aiming) {
        if (d.moved) this.fireManual()
        else this.cancelAim()
        return
      }
      if (this.mode === 'erase' && d.button === 0 && !d.shift) {
        if (this.erased) this.hud.hint(`Erased ${this.erased} ${this.erased === 1 ? 'rock' : 'rocks'}`, 1800)
        this.erased = 0
        return
      }
      if (!d.moved && d.button === 0) {
        const hit = this.pickBody(e.clientX, e.clientY)
        if (this.mode === 'asteroid') {
          if (hit) this.fireAt(hit)
          return
        }
        if (hit) {
          const now = performance.now()
          if (this.lastClick.id === hit.id && now - this.lastClick.time < 380) this.flyAbove(hit)
          else this.select(hit)
          this.lastClick = { time: now, id: hit.id }
        }
      }
    }
    stage.addEventListener('pointerleave', () => { if (this.mode === 'erase') this.showBrush(null) })
    stage.addEventListener('pointerup', up)
    stage.addEventListener('pointercancel', up)
    stage.addEventListener('wheel', (e) => {
      e.preventDefault()
      if (this.mode === 'erase' && !e.shiftKey) {
        this.brushRadius = clamp(this.brushRadius * Math.exp(clamp(-e.deltaY, -200, 200) * 0.0015), 8, 400)
        this.showBrush({ x: e.clientX, y: e.clientY })
        return
      }
      this.rig.zoom(Math.exp(clamp(e.deltaY, -200, 200) * 0.0012))
    }, { passive: false })
    stage.addEventListener('contextmenu', (e) => e.preventDefault())
    const clearKeys = () => this.keys.clear()
    window.addEventListener('blur', clearKeys)
    window.addEventListener('keyup', (e) => this.keys.delete(e.key.toLowerCase()))
    window.addEventListener('keydown', (e) => {
      const t = e.target as HTMLElement
      if (t && (t.tagName === 'INPUT' || t.tagName === 'SELECT' || t.tagName === 'TEXTAREA')) return
      const k = e.key
      if (e.metaKey || e.ctrlKey || e.altKey) return
      this.keys.add(k.toLowerCase())
      if (k === ' ') { e.preventDefault(); this.togglePause() }
      else if (k === '[') this.setWarp(this.warp / 4)
      else if (k === ']') this.setWarp(this.warp * 4)
      else if (k === 'r' || k === 'R') this.toggleRewind()
      else if (k === 'x' || k === 'X') this.setMode(this.mode === 'asteroid' ? 'explore' : 'asteroid')
      else if (k === 'z' || k === 'Z') this.setMode(this.mode === 'erase' ? 'explore' : 'erase')
      else if (k === 'f' || k === 'F') { if (this.selected) this.flyAbove(this.selected) }
      else if (k === 't' || k === 'T') this.viewTabletop()
      else if (k === 'h' || k === 'H') this.hud.toggleHelp()
      else if (k === 'c' || k === 'C') { this.settings.cinematic = !this.settings.cinematic; this.hud.hint(`Cinematic impact camera ${this.settings.cinematic ? 'on' : 'off'}`) }
      else if (k === 'Escape') { this.setMode('explore'); this.hud.closeOverlays(); this.hud.hideReport() }
      else if (/^[0-9]$/.test(k)) {
        const idx = k === '0' ? 9 : parseInt(k, 10) - 1
        const id = PLANET_ORDER[idx]
        const b = id ? this.universe.byId(id) : undefined
        if (b) { this.select(b); this.flyAbove(b) }
      }
    })
  }
}
