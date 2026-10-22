// Headless screenshot + console harness for visual iteration.
// usage: node scripts/shoot.mjs <url> <outDir> '<json array of {name, js, wait}>'
import { chromium } from 'playwright'
import { mkdir } from 'node:fs/promises'

const url = process.argv[2] ?? 'http://localhost:5173/'
const outDir = process.argv[3] ?? 'shots'
const actions = JSON.parse(process.argv[4] ?? '[]')
const width = Number(process.env.SHOT_W ?? 1600)
const height = Number(process.env.SHOT_H ?? 900)

await mkdir(outDir, { recursive: true })
// CHROME_PATH points at a local Chromium when the `chrome` channel is not installed.
const browser = await chromium.launch({
  ...(process.env.CHROME_PATH ? { executablePath: process.env.CHROME_PATH } : { channel: process.env.SHOT_CHANNEL ?? 'chrome' }),
  headless: process.env.SHOT_HEADED ? false : true,
  args: ['--ignore-gpu-blocklist', '--enable-unsafe-swiftshader', '--use-angle=default', '--enable-webgl', '--hide-scrollbars'],
})
const page = await browser.newPage({ viewport: { width, height }, deviceScaleFactor: 1 })
const logs = []
page.on('console', (m) => {
  const t = m.type()
  if (t === 'error' || t === 'warning') logs.push(`[${t}] ${m.text().slice(0, 1500)}`)
})
page.on('pageerror', (e) => logs.push(`[pageerror] ${e.message}`))
page.on('response', (r) => { if (r.status() >= 400) logs.push(`[http ${r.status()}] ${r.url()}`) })
await page.goto(url, { waitUntil: 'load' })
await page.waitForTimeout(Number(process.env.SHOT_SETTLE ?? 5000))
await page.screenshot({ path: `${outDir}/00-start.png` })
let i = 0
for (const a of actions) {
  i++
  if (a.js) {
    try {
      const r = await page.evaluate(a.js)
      if (r !== undefined) console.log(`[${a.name}] ->`, typeof r === 'object' ? JSON.stringify(r).slice(0, 800) : r)
    } catch (e) {
      console.log(`[${a.name}] evaluate error: ${e.message}`)
    }
  }
  await page.waitForTimeout(a.wait ?? 1500)
  await page.screenshot({ path: `${outDir}/${String(i).padStart(2, '0')}-${a.name ?? 'step'}.png` })
}
const info = await page.evaluate(() => {
  const s = window.spayly
  if (!s) return 'no app'
  return { fps: Math.round(s.fps), bodies: s.universe.bodies.length, substeps: s.universe.substepsLastFrame, exposure: s.exposure, camDist: s.rig.distance }
})
console.log('info', JSON.stringify(info))
console.log(logs.length ? logs.join('\n') : 'no console errors/warnings')
await browser.close()
