/** Shared configuration types for the app layer.
 *
 * This file imports nothing — not even from `app.ts`. That is deliberate: `src/ui` needs these values at
 * runtime, and keeping them dependency-free is what stops the app<->ui import cycle coming back.
 */
export type Mode = 'explore' | 'asteroid' | 'erase'
export type MoonDetail = 'all' | 'major' | 'none'
export type Composition = 'rock' | 'iron' | 'carbon' | 'ice'
export const COMPOSITION_DENSITY: Record<Composition, number> = { rock: 3000, iron: 7800, carbon: 1500, ice: 900 }
export const COMPOSITION_COLOR: Record<Composition, string> = { rock: '#b8a48c', iron: '#c9c2b8', carbon: '#6f6560', ice: '#bfe0ff' }

export interface AsteroidSettings {
  diameterM: number
  composition: Composition
  speedKms: number
  name?: string
}

export interface Preset {
  name: string
  diameterM: number
  composition: Composition
  speedKms: number
  blurb: string
}

export const PRESETS: Preset[] = [
  { name: 'Chelyabinsk', diameterM: 19, composition: 'rock', speedKms: 19, blurb: '2013 airburst over Russia' },
  { name: 'Tunguska', diameterM: 60, composition: 'rock', speedKms: 15, blurb: '1908 forest-flattening airburst' },
  { name: 'Apophis', diameterM: 370, composition: 'rock', speedKms: 12.6, blurb: 'the 2029 near miss' },
  { name: 'Bennu', diameterM: 490, composition: 'carbon', speedKms: 12, blurb: 'rubble pile visited by OSIRIS-REx' },
  { name: 'Chicxulub', diameterM: 10000, composition: 'rock', speedKms: 20, blurb: 'goodbye dinosaurs' },
  { name: 'Vesta', diameterM: 525000, composition: 'rock', speedKms: 20, blurb: 'a whole protoplanet' },
  { name: 'Ceres', diameterM: 940000, composition: 'carbon', speedKms: 18, blurb: 'dwarf planet' },
  { name: 'Theia', diameterM: 6000000, composition: 'rock', speedKms: 10, blurb: 'the Moon-forming giant impact' },
]

export interface Settings {
  moons: MoonDetail
  /** Widest a body map is decoded at, in pixels. */
  textures: number
  orbits: boolean
  trails: boolean
  labels: boolean
  belt: boolean
  bloom: boolean
  cinematic: boolean
  diorama: boolean
  falloff: number
}
