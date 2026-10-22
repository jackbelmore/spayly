import { describe, it, expect } from 'vitest'

// Read the sources as text rather than importing them: this test is about what the files say,
// not what they do. `?raw` keeps it inside Vite's typed surface, so no @types/node is needed.
const RAW = import.meta.glob('../src/**/*.ts', { query: '?raw', import: 'default', eager: true }) as Record<string, string>

/** Every `from '...'` specifier in a file, paired with whether the statement was type-only. */
function imports(text: string): { spec: string; typeOnly: boolean }[] {
  const out: { spec: string; typeOnly: boolean }[] = []
  for (const m of text.matchAll(/^\s*import\s+(type\s+)?([\s\S]*?)from\s+'([^']+)'/gm)) {
    out.push({ spec: m[3], typeOnly: Boolean(m[1]) })
  }
  return out
}

const files = Object.entries(RAW).map(([path, text]) => ({
  rel: path.replace(/^\.\.\/src\//, ''),
  text,
  imports: imports(text),
}))
const inDir = (d: string) => files.filter((f) => f.rel.startsWith(`${d}/`))

describe('layer law', () => {
  it('finds the source tree', () => {
    expect(files.length).toBeGreaterThan(20)
    expect(files.map((f) => f.rel)).toContain('app/app.ts')
  })

  // sim is a leaf: it must run under plain Node in vitest, with no renderer and no browser.
  it('sim imports nothing outside sim', () => {
    for (const f of inDir('sim')) {
      for (const { spec } of f.imports) {
        expect(spec.startsWith('./'), `${f.rel} imports ${spec}`).toBe(true)
      }
    }
  })

  it('sim never touches three or the DOM', () => {
    for (const f of inDir('sim')) {
      expect(f.imports.some((i) => i.spec === 'three'), `${f.rel} imports three`).toBe(false)
      for (const global of ['document.', 'window.', 'performance.']) {
        expect(f.text.includes(global), `${f.rel} uses ${global}`).toBe(false)
      }
    }
  })

  it('render imports only sim and three', () => {
    for (const f of inDir('render')) {
      for (const { spec } of f.imports) {
        expect(spec.includes('/ui/') || spec.includes('/app/'), `${f.rel} imports ${spec}`).toBe(false)
      }
    }
  })

  // The `type` keyword in hud.ts is what keeps the app<->ui cycle erased at build time.
  it('ui imports App as a type only', () => {
    for (const f of inDir('ui')) {
      for (const { spec, typeOnly } of f.imports) {
        if (spec.includes('app/app')) expect(typeOnly, `${f.rel} imports ${spec} as a value`).toBe(true)
      }
    }
  })

  it('app/types.ts imports nothing', () => {
    const types = files.find((f) => f.rel === 'app/types.ts')
    expect(types, 'src/app/types.ts is missing').toBeDefined()
    expect(types!.imports).toEqual([])
  })

  it('only main.ts and ui/hud.ts reach into app/app', () => {
    const reaching = files.filter((f) => f.imports.some((i) => i.spec.includes('app/app'))).map((f) => f.rel)
    expect(reaching.sort()).toEqual(['main.ts', 'ui/hud.ts'])
  })
})
