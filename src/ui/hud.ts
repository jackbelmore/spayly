import type { App } from '../app/app'
import { Mode, MoonDetail, PRESETS, COMPOSITION_DENSITY, Composition } from '../app/types'
import { Body, density, surfaceGravity, escapeVelocity } from '../sim/body'
import { ImpactOutcome } from '../sim/collide'
import { G, SUN_MASS } from '../sim/constants'
import { stateToElements } from '../sim/kepler'
import { sub, length } from '../sim/vec'
import { PlanetView } from '../render/planetView'
import { maxTextureSupported } from '../render/textures'
import * as F from './format'

const $ = <T extends HTMLElement = HTMLElement>(id: string): T => document.getElementById(id) as T

const KIND_LABEL: Record<string, string> = { star: 'star', planet: 'planet', moon: 'moon', asteroid: 'asteroid', fragment: 'fragment' }

function spectralClass(T: number, mass: number): string {
  if (mass < 0.08 * SUN_MASS) return 'brown dwarf'
  if (T >= 30000) return 'O-type blue giant'
  if (T >= 10000) return 'B-type blue-white'
  if (T >= 7500) return 'A-type white'
  if (T >= 6000) return 'F-type yellow-white'
  if (T >= 5200) return 'G-type yellow (like our Sun)'
  if (T >= 3700) return 'K-type orange'
  return 'M-type red dwarf'
}

export class Hud {
  private app: App
  private hintTimer: number | undefined
  private listVersion = -1
  private listSelected = ''
  private activePreset = ''
  private e = {
    date: $('date'), warpLabel: $('warp-label'), warp: $<HTMLInputElement>('warp'), fps: $('fps'), physics: $('physics'),
    pause: $('btn-pause'), rewind: $('btn-rewind'), bodyList: $('body-list'), limited: $('limited'),
    inspName: $('insp-name'), inspKind: $('insp-kind'), mass: $<HTMLInputElement>('mass'), massVal: $('mass-val'),
    radius: $<HTMLInputElement>('radius'), radiusVal: $('radius-val'), stats: $('insp-stats'),
    modeExplore: $('mode-explore'), modeAsteroid: $('mode-asteroid'), modeErase: $('mode-erase'),
    astPanel: $('asteroid-panel'), erasePanel: $('erase-panel'),
    astDiam: $<HTMLInputElement>('ast-diam'), astDiamVal: $('ast-diam-val'), astSpeed: $<HTMLInputElement>('ast-speed'), astSpeedVal: $('ast-speed-val'),
    astComp: $<HTMLSelectElement>('ast-comp'), aim: $('aim-readout'), presets: $('presets'), hint: $('hint'),
    report: $('report'), reportTitle: $('report-title'), reportClass: $('report-class'), reportStats: $('report-stats'), reportCompare: $('report-compare'),
    help: $('help'), diorama: $<HTMLInputElement>('opt-diorama'), orbits: $<HTMLInputElement>('opt-orbits'), trails: $<HTMLInputElement>('opt-trails'),
    labels: $<HTMLInputElement>('opt-labels'), belt: $<HTMLInputElement>('opt-belt'), bloom: $<HTMLInputElement>('opt-bloom'),
    cinematic: $<HTMLInputElement>('opt-cinematic'), falloff: $<HTMLSelectElement>('opt-falloff'),
    moons: $<HTMLSelectElement>('opt-moons'), textures: $<HTMLSelectElement>('opt-textures'),
  }

  constructor(app: App) {
    this.app = app
    const e = this.e
    e.warp.addEventListener('input', () => app.setWarp(Math.pow(10, parseFloat(e.warp.value))))
    $('btn-slower').addEventListener('click', () => app.setWarp(app.warp / 4))
    $('btn-faster').addEventListener('click', () => app.setWarp(app.warp * 4))
    e.pause.addEventListener('click', () => app.togglePause())
    e.rewind.addEventListener('click', () => app.toggleRewind())
    $('btn-help').addEventListener('click', () => this.toggleHelp())
    $('help-close').addEventListener('click', () => this.toggleHelp(false))
    $('view-tabletop').addEventListener('click', () => app.viewTabletop())
    $('view-inner').addEventListener('click', () => app.viewInner())
    $('view-outer').addEventListener('click', () => app.viewOuter())
    const s = app.settings
    e.diorama.addEventListener('change', () => { s.diorama = e.diorama.checked })
    e.orbits.addEventListener('change', () => { s.orbits = e.orbits.checked })
    e.trails.addEventListener('change', () => { s.trails = e.trails.checked })
    e.labels.addEventListener('change', () => { s.labels = e.labels.checked })
    e.belt.addEventListener('change', () => { s.belt = e.belt.checked })
    e.bloom.addEventListener('change', () => { s.bloom = e.bloom.checked })
    e.cinematic.addEventListener('change', () => { s.cinematic = e.cinematic.checked })
    e.falloff.addEventListener('change', () => { s.falloff = parseFloat(e.falloff.value) })
    e.moons.addEventListener('change', () => app.setMoonDetail(e.moons.value as MoonDetail))
    // Offer only the sizes this GPU can actually hold.
    for (const opt of Array.from(e.textures.options)) {
      if (parseInt(opt.value, 10) > maxTextureSupported()) opt.remove()
    }
    e.textures.value = String(app.settings.textures)
    e.textures.addEventListener('change', () => app.setTextureQuality(parseInt(e.textures.value, 10)))
    $('btn-follow').addEventListener('click', () => { if (app.selected) app.follow(app.selected) })
    $('btn-above').addEventListener('click', () => { if (app.selected) app.flyAbove(app.selected) })
    $('btn-reset-body').addEventListener('click', () => { if (app.selected) app.resetBody(app.selected) })
    e.mass.addEventListener('input', () => {
      const b = app.selected
      if (!b) return
      b.mass = b.mass0 * Math.pow(10, parseFloat(e.mass.value))
      e.massVal.textContent = F.mass(b.mass)
    })
    e.radius.addEventListener('input', () => {
      const b = app.selected
      if (!b) return
      b.radius = b.radius0 * Math.pow(10, parseFloat(e.radius.value))
      e.radiusVal.textContent = F.distance(b.radius)
    })
    e.modeExplore.addEventListener('click', () => app.setMode('explore'))
    e.modeAsteroid.addEventListener('click', () => app.setMode('asteroid'))
    e.modeErase.addEventListener('click', () => app.setMode(app.mode === 'erase' ? 'explore' : 'erase'))
    e.modeErase.classList.add('erase')
    e.astDiam.addEventListener('input', () => { app.asteroid.diameterM = Math.pow(10, parseFloat(e.astDiam.value)); this.activePreset = ''; this.syncAsteroid() })
    e.astSpeed.addEventListener('input', () => { app.asteroid.speedKms = Math.pow(10, parseFloat(e.astSpeed.value)); this.syncAsteroid() })
    e.astComp.addEventListener('change', () => { app.asteroid.composition = e.astComp.value as Composition; this.activePreset = ''; this.syncAsteroid() })
    for (const p of PRESETS) {
      const btn = document.createElement('button')
      btn.textContent = p.name
      btn.title = `${p.blurb} · ${F.lengthM(p.diameterM)} · ${p.speedKms} km/s`
      btn.addEventListener('click', () => { app.applyPreset(p); this.activePreset = p.name; this.syncAsteroid() })
      e.presets.appendChild(btn)
    }
    $('report-close').addEventListener('click', () => this.hideReport())
    $('btn-undo').addEventListener('click', () => app.undoImpact())
    $('btn-goto-impact').addEventListener('click', () => app.lookAtLastImpact())
    this.syncAsteroid()
    this.syncInspector()
    this.setMode(app.mode)
  }

  update(): void {
    const app = this.app
    const u = app.universe
    const e = this.e
    e.date.textContent = F.date(u.jd)
    e.warpLabel.textContent = app.paused ? 'paused' : F.warp(app.rewind ? -app.warp : app.warp)
    if (document.activeElement !== e.warp) e.warp.value = String(Math.log10(app.warp))
    e.fps.textContent = `${Math.round(app.fps)} fps`
    e.physics.textContent = `${u.bodies.length} bodies · ${u.substepsLastFrame} steps`
    e.limited.classList.toggle('show', u.warpLimited)
    // ❚❚ while running, ▶ while paused: the button shows what pressing it does next.
    e.pause.textContent = app.paused ? '\u25B6' : '\u2759\u2759'
    e.pause.title = app.paused ? 'Play (Space)' : 'Pause (Space)'
    e.pause.classList.toggle('active', app.paused)
    e.rewind.classList.toggle('active', app.rewind)
    e.diorama.checked = app.settings.diorama
    const selId = app.selected?.id ?? ''
    if (app.bodiesVersion !== this.listVersion || selId !== this.listSelected) this.rebuildBodyList()
    this.updateInspector()
  }

  private rebuildBodyList(): void {
    const app = this.app
    const list = this.e.bodyList
    list.innerHTML = ''
    let n = 0
    for (const b of app.universe.bodies) {
      if (n++ > 80) break
      const btn = document.createElement('button')
      btn.innerHTML = `<span class="dot" style="background:${b.visual.color};color:${b.visual.color}"></span><span>${b.name}</span><span class="sub">${KIND_LABEL[b.kind] ?? b.kind}</span>`
      btn.classList.toggle('active', b === app.selected)
      btn.classList.toggle('sat', b.kind === 'moon')
      btn.addEventListener('click', () => { app.select(b); app.flyAbove(b) })
      list.appendChild(btn)
    }
    this.listVersion = app.bodiesVersion
    this.listSelected = app.selected?.id ?? ''
  }

  syncInspector(): void {
    const b = this.app.selected
    const e = this.e
    if (!b) {
      e.inspName.textContent = 'Nothing selected'
      e.inspKind.textContent = 'click a body or a label'
      e.stats.innerHTML = ''
      return
    }
    e.inspName.textContent = b.name
    e.inspKind.textContent = KIND_LABEL[b.kind] ?? b.kind
    e.mass.value = String(Math.log10(b.mass / b.mass0))
    e.radius.value = String(Math.log10(b.radius / b.radius0))
    e.massVal.textContent = F.mass(b.mass)
    e.radiusVal.textContent = F.distance(b.radius)
  }

  private updateInspector(): void {
    const app = this.app
    const b = app.selected
    const e = this.e
    if (!b) return
    if (e.inspName.textContent !== b.name) e.inspName.textContent = b.name
    if (document.activeElement !== e.mass) e.massVal.textContent = F.mass(b.mass)
    if (document.activeElement !== e.radius) e.radiusVal.textContent = F.distance(b.radius)
    const rows: Array<[string, string]> = []
    rows.push(['Density', `${F.num(density(b))} kg/m³`])
    const g = surfaceGravity(b)
    rows.push(['Gravity', `${F.num(g / 9.80665)} g`])
    rows.push(['Escape velocity', F.speed(escapeVelocity(b))])
    if (b.kind === 'star') {
      const s = app.sunState
      rows.push(['Luminosity', `${F.num(s.luminosity)} L☉`])
      rows.push(['Temperature', `${Math.round(s.tempK)} K`])
      rows.push(['Class', spectralClass(s.tempK, b.mass)])
    } else {
      const p = app.primaryOf(b)
      if (p) {
        const r = sub(b.pos, p.pos)
        const v = sub(b.vel, p.vel)
        const el = stateToElements(G * (p.mass + b.mass), r, v)
        rows.push(['Orbits', p.name])
        rows.push(['Period', isFinite(el.period) ? F.duration(el.period) : 'unbound'])
        rows.push(['Distance', F.distance(length(r))])
        rows.push(['Speed', F.speed(length(v))])
        rows.push(['Eccentricity', F.num(el.e)])
        if (isFinite(el.apoapsis)) rows.push(['Peri / apo', `${F.distance(el.periapsis)} / ${F.distance(el.apoapsis)}`])
      }
      const view = app.viewFor(b)
      if (view instanceof PlanetView && view.surfaceTemp !== undefined) rows.push(['Surface temp', F.temperature(view.surfaceTemp)])
      else if (view instanceof PlanetView && view.equilibriumTemp !== undefined) rows.push(['Equilibrium temp', F.temperature(view.equilibriumTemp)])
    }
    if (b.damage.hits) rows.push(['Impacts', `${b.damage.hits} · ${F.energy(b.damage.energy)}`])
    if (b.damage.absorbed) rows.push(['Rocks swallowed', String(b.damage.absorbed)])
    e.stats.innerHTML = rows.map(([k, v]) => `<dt>${k}</dt><dd>${v}</dd>`).join('')
  }

  syncAsteroid(): void {
    const a = this.app.asteroid
    const e = this.e
    e.astDiam.value = String(Math.log10(a.diameterM))
    e.astSpeed.value = String(Math.log10(a.speedKms))
    e.astComp.value = a.composition
    const mass = COMPOSITION_DENSITY[a.composition] * (Math.PI / 6) * Math.pow(a.diameterM, 3)
    e.astDiamVal.textContent = `${F.lengthM(a.diameterM)} · ${F.sci(mass, 2)} kg`
    e.astSpeedVal.textContent = F.speed(a.speedKms)
    for (const btn of Array.from(e.presets.children)) btn.classList.toggle('active', btn.textContent === this.activePreset)
  }

  setMode(m: Mode): void {
    this.e.modeExplore.classList.toggle('active', m === 'explore')
    this.e.modeAsteroid.classList.toggle('active', m === 'asteroid')
    this.e.modeErase.classList.toggle('active', m === 'erase')
    this.e.astPanel.classList.toggle('hidden', m !== 'asteroid')
    this.e.erasePanel.classList.toggle('hidden', m !== 'erase')
  }

  setAimText(t: string): void {
    this.e.aim.textContent = t || 'Click a body to fire from the camera. Drag on empty space to aim by hand.'
  }

  hint(text: string, ms = 3200): void {
    this.e.hint.textContent = text
    this.e.hint.classList.add('show')
    if (this.hintTimer) window.clearTimeout(this.hintTimer)
    this.hintTimer = window.setTimeout(() => this.e.hint.classList.remove('show'), ms)
  }

  toggleHelp(force?: boolean): void {
    const show = force ?? this.e.help.classList.contains('hidden')
    this.e.help.classList.toggle('hidden', !show)
  }

  closeOverlays(): void {
    this.e.help.classList.add('hidden')
  }

  hideReport(): void {
    this.e.report.classList.add('hidden')
  }

  showReport(o: ImpactOutcome, deltaV: number): void {
    const e = this.e
    const r = o.report
    e.reportTitle.textContent = `${o.impactor.name} → ${o.target.name}`
    let cls = r?.classification ?? ''
    if (o.kind === 'absorb') cls = `Swallowed by ${o.target.name}. A brief flare, nothing more.`
    if (o.kind === 'shatter') cls = `${o.target.name} shattered into ${o.fragments.length} pieces. The molten remnant keeps ${Math.round((o.target.mass / (o.target.mass + o.fragments.reduce((s, f) => s + f.mass, 0))) * 100)}% of the mass.`
    if (o.kind === 'disperse') cls = `${o.target.name} destroyed. ${o.fragments.length} fragments scatter; only a molten core remains.`
    e.reportClass.textContent = cls
    const rows: Array<[string, string]> = []
    rows.push(['Impact speed', `${F.speed(o.relSpeed)} at ${F.angle(o.angle)}`])
    rows.push(['Energy', F.energy(o.energy)])
    if (r && r.planetScale) {
      rows.push(['Scale', 'planet-wide: the crust is remelted, no crater survives'])
      rows.push(['Fireball', 'engulfs the planet'])
      if (isFinite(r.seismicMagnitude)) rows.push(['Earthquake', `magnitude ${r.seismicMagnitude.toFixed(1)}, felt everywhere`])
      rows.push(['Binding energy', `${(r.bindingRatio * 100).toPrecision(2)}% of ${o.target.name}'s`])
    } else if (r) {
      const en = r.entry
      if (en.airburst) rows.push(['Entry', `broke up at ${F.lengthM(en.breakupAltitude)}, airburst at ${F.lengthM(en.burstAltitude)}`])
      else if (en.breakup) rows.push(['Entry', `fragmented at ${F.lengthM(en.breakupAltitude)}, hit the ground at ${F.speed(en.surfaceSpeed / 1000)}`])
      else if (o.target.atmosphere) rows.push(['Entry', `intact, hit the ground at ${F.speed(en.surfaceSpeed / 1000)}`])
      if (r.crater) rows.push(['Crater', `${F.lengthM(r.crater.finalDiameter)} wide, ${F.lengthM(r.crater.depth)} deep (${r.crater.complex ? 'complex' : 'simple'})`])
      else if (o.kind !== 'gas') rows.push(['Crater', 'none'])
      if (r.fireballRadius > 1) rows.push(['Fireball', `${F.lengthM(r.fireballRadius)} radius`])
      if (r.thermal.burnRadius > 100) rows.push(['Thermal', `3rd-degree burns to ${F.lengthM(r.thermal.burnRadius)}${r.thermal.horizonLimited ? ' (to the horizon)' : ''}`])
      if (r.crater && isFinite(r.seismicMagnitude)) rows.push(['Earthquake', `magnitude ${r.seismicMagnitude.toFixed(1)}${r.seismicDamageRadius > 0 ? `, serious damage to ${F.lengthM(r.seismicDamageRadius)}` : ''}`])
      if (r.airblast.glassRadius > 0) rows.push(['Air blast', `windows break to ${F.lengthM(r.airblast.glassRadius)}${r.airblast.collapseRadius > 0 ? `, buildings collapse to ${F.lengthM(r.airblast.collapseRadius)}` : ''}`])
      if (r.crater && r.ejectaOneMetreRadius > 0) rows.push(['Ejecta', `≥ 1 m deep out to ${F.lengthM(r.ejectaOneMetreRadius)}`])
      if (r.tsunami) rows.push(['Tsunami', `${F.lengthM(r.tsunami.amplitude100km)} wave at 100 km, ${F.lengthM(r.tsunami.amplitude1000km)} at 1000 km`])
      if (r.bindingRatio > 1e-4) rows.push(['Binding energy', `${(r.bindingRatio * 100).toPrecision(2)}% of ${o.target.name}'s`])
    }
    if (o.fragments.length) rows.push(['Fragments', `${o.fragments.length}, largest ${F.mass(Math.max(...o.fragments.map((f) => f.mass)))}`])
    rows.push([`${o.target.name} recoil`, F.speed(deltaV)])
    e.reportStats.innerHTML = rows.map(([k, v]) => `<dt>${k}</dt><dd>${v}</dd>`).join('')
    e.reportCompare.textContent = r?.comparisons.join(' · ') ?? ''
    e.report.classList.remove('hidden')
  }
}
