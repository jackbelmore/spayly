#!/usr/bin/env node
// One command from a fresh clone to a running game, on Windows, macOS and Linux: pull, check the toolchain, fetch assets,
// fetch Godot, build, import, launch. Every step is skipped when its output is already newer than its inputs.
// Node built-ins only, because it runs before `npm install`. Start it through start.sh / start.cmd at the repo root.
//
// Usage: node godot/tools/start.mjs [--rebuild] [--no-pull] [--prepare-only] [--dry-run]
//                                   [--platform win32|darwin|linux] [--arch x64|arm64] [-- godot args]
import { spawnSync } from 'node:child_process'
import { chmodSync, createWriteStream, existsSync, mkdirSync, mkdtempSync, readdirSync, readFileSync, realpathSync, rmSync, statSync, utimesSync } from 'node:fs'
import { constants as osConstants, homedir, tmpdir } from 'node:os'
import path from 'node:path'
import { Readable, Transform } from 'node:stream'
import { pipeline } from 'node:stream/promises'
import { fileURLToPath } from 'node:url'

const SCRIPT = fileURLToPath(import.meta.url)
const root = path.resolve(path.dirname(SCRIPT), '..', '..')
const proj = path.join(root, 'godot', 'Spayly.Game')

// ---------- flags ----------

const argv = process.argv.slice(2)
const dash = argv.indexOf('--')
const flagArgs = dash >= 0 ? argv.slice(0, dash) : argv
const godotArgs = dash >= 0 ? argv.slice(dash + 1) : []
const opts = { rebuild: false, noPull: false, pulled: false, prepareOnly: false, dryRun: false, platform: process.platform, arch: process.arch }
const die = (msg) => { console.error(`start: ${msg}`); process.exit(1) }
for (let i = 0; i < flagArgs.length; i++) {
  const a = flagArgs[i]
  if (a === '--rebuild') opts.rebuild = true
  else if (a === '--no-pull') opts.noPull = true
  else if (a === '--pulled') opts.pulled = true
  else if (a === '--prepare-only') opts.prepareOnly = true
  else if (a === '--dry-run') opts.dryRun = true
  else if (a === '--platform' || a === '--arch') {
    const v = flagArgs[++i]
    if (!v) die(`${a} needs a value`)
    opts[a.slice(2)] = v
  } else if (a === '--help' || a === '-h') {
    console.log(readFileSync(SCRIPT, 'utf8').split('\n').slice(5, 8).map((l) => l.replace(/^\/\/ ?/, '')).join('\n'))
    process.exit(0)
  } else die(`unknown option ${a} (put Godot's own arguments after --)`)
}
if (!['win32', 'darwin', 'linux'].includes(opts.platform)) die(`--platform must be win32, darwin or linux, not ${opts.platform}`)
if (!['x64', 'arm64'].includes(opts.arch)) die(`--arch must be x64 or arm64, not ${opts.arch}`)
const plat = opts.platform
const foreign = plat !== process.platform || opts.arch !== process.arch
if (foreign && !opts.dryRun) die('--platform / --arch other than this machine only make sense with --dry-run')
const dry = opts.dryRun
const win = plat === 'win32'
/** Paths inside the Godot install belong to the target OS, which in a dry run is not this one. */
const tp = win ? path.win32 : path.posix

// ---------- output and process helpers ----------

const step = (msg) => console.error(`==> ${msg}`)
const skip = (why) => console.error(`    (skipped: ${why})`)
const note = (msg) => console.error(`    ${msg}`)
const quote = (a) => (/[\s"]/.test(a) ? JSON.stringify(a) : a)
const show = (cmd, args, prefix = '') => note(`$ ${prefix}${[cmd, ...args].map(quote).join(' ')}`)

/** Read-only probe: never printed, never skipped in a dry run. A missing program comes back as ok:false. */
function probe(cmd, args, o = {}) {
  const r = spawnSync(cmd, args, { cwd: root, encoding: 'utf8', shell: o.shell ?? false, env: o.env ?? process.env, maxBuffer: 64 << 20 })
  return { ok: !r.error && r.status === 0, missing: !!r.error, out: (r.stdout ?? '').trim(), text: `${r.stdout ?? ''}${r.stderr ?? ''}`.trim() }
}

/** A command that changes something. In a dry run it is only printed. npm/npx are .cmd files on Windows, which Node >= 20 will not spawn without a shell. */
function act(cmd, args, o = {}) {
  if (dry) { show(cmd, args); return { status: 0 } }
  const shell = win && /^(npm|npx)$/.test(cmd)
  const r = spawnSync(cmd, args, { cwd: o.cwd ?? root, env: o.env ?? process.env, encoding: 'utf8', shell, stdio: o.capture ? 'pipe' : 'inherit', maxBuffer: 256 << 20 })
  if (r.error) die(`could not run ${cmd}: ${r.error.message}`)
  // A child killed by a signal has no status; report it the way a shell would (SIGSEGV -> 139).
  return { status: r.status ?? 128 + (osConstants.signals[r.signal] ?? 1), text: `${r.stdout ?? ''}${r.stderr ?? ''}`.trim() }
}

// ---------- filesystem helpers ----------

const mt = (p) => { try { return statSync(p).mtimeMs } catch { return 0 } }

/** Files under `dir`, following directory symlinks (agent worktrees link textures/ to the main checkout's) unless `follow` is off. */
function* walk(dir, { skipDir = () => false, follow = true } = {}, seen = new Set()) {
  let real
  try { real = realpathSync(dir) } catch { return }
  if (seen.has(real)) return
  seen.add(real)
  for (const e of readdirSync(dir, { withFileTypes: true })) {
    const p = path.join(dir, e.name)
    let isDir = e.isDirectory()
    let isFile = e.isFile()
    if (e.isSymbolicLink()) {
      try { const s = statSync(p); isDir = s.isDirectory(); isFile = s.isFile() } catch { continue }
      if (isDir && !follow) continue
    }
    if (isDir) { if (!skipDir(e.name)) yield* walk(p, { skipDir, follow }, seen) } else if (isFile) yield p
  }
}

/** The path of the first file in `files` newer than `ref`, if any. */
const firstNewer = (files, ref) => { for (const f of files) if (mt(f) > ref) return f }

function onPath(name) {
  const exts = win ? (process.env.PATHEXT ?? '.EXE;.CMD').split(';') : ['']
  for (const dir of (process.env.PATH ?? '').split(path.delimiter)) {
    for (const ext of exts) {
      const p = path.join(dir, name + ext)
      if (existsSync(p) && statSync(p).isFile()) return p
    }
  }
}

// ---------- 1. pull ----------

function pull() {
  step('Pulling the latest main')
  if (opts.noPull) return skip('--no-pull')
  const branch = probe('git', ['rev-parse', '--abbrev-ref', 'HEAD']).out
  if (branch === 'HEAD') return skip('detached HEAD')
  if (!probe('git', ['rev-parse', '--abbrev-ref', '--symbolic-full-name', '@{u}']).ok) return skip(`'${branch}' has no upstream`)
  if (branch !== 'main') return skip(`on '${branch}', not main`)
  if (dry) return show('git', ['pull', '--ff-only', '--autostash'])

  const before = probe('git', ['rev-parse', 'HEAD']).out
  // LC_ALL=C so the failure text below is English whatever the user's locale.
  const r = probe('git', ['pull', '--ff-only', '--autostash'], { env: { ...process.env, LC_ALL: 'C' } })
  if (!r.ok) {
    if (/Could not resolve host|unable to access|Could not read from remote|Connection (timed out|refused|reset)|Network is unreachable|Temporary failure in name resolution|Failed to connect|early EOF/i.test(r.text)) {
      console.error('    warning: cannot reach the remote; carrying on with the checkout as it is (offline)')
      return
    }
    console.error(r.text)
    console.error(probe('git', ['status', '-sb']).text)
    const diverged = /not possible to fast-forward|diverg|reconcile/i.test(r.text)
    die(diverged
      ? `'${branch}' has commits that are not on its upstream, so it cannot fast-forward.\n  Rebase them with 'git pull --rebase', or drop them with 'git reset --hard @{u}', then run this again.\n  To launch what you have without pulling: --no-pull.`
      : "git pull failed (see above). Fix that, or launch what you have with --no-pull.")
  }
  const after = probe('git', ['rev-parse', 'HEAD']).out
  if (before === after) return note('already up to date')
  // This script may have changed in the pull; the copy in memory is stale. Hand over to the new one.
  note(`updated ${before.slice(0, 7)}..${after.slice(0, 7)}; restarting with the new scripts`)
  const again = [SCRIPT, ...flagArgs, '--no-pull', '--pulled', ...(dash >= 0 ? ['--', ...godotArgs] : [])]
  const rr = spawnSync(process.execPath, again, { stdio: 'inherit' })
  process.exit(rr.status ?? 1)
}

// ---------- 2. dependencies ----------

function osRelease() {
  try {
    const kv = Object.fromEntries(readFileSync('/etc/os-release', 'utf8').split('\n').map((l) => l.match(/^(\w+)=("?)(.*)\2$/)).filter(Boolean).map((m) => [m[1], m[3]]))
    return `${kv.ID ?? ''} ${kv.ID_LIKE ?? ''}`.toLowerCase().split(/\s+/)
  } catch { return [] }
}

function installHints() {
  if (win) return ['winget install Git.Git GitHub.GitLFS OpenJS.NodeJS.LTS Microsoft.DotNet.SDK.10', 'then open a NEW terminal (the PATH of this one does not refresh) and run start.cmd again.']
  if (plat === 'darwin') return ['brew install git git-lfs node dotnet-sdk']
  const ids = osRelease()
  if (ids.some((i) => ['fedora', 'rhel', 'centos'].includes(i))) return ['sudo dnf install git git-lfs nodejs dotnet-sdk-10.0 unzip']
  if (ids.some((i) => ['debian', 'ubuntu'].includes(i))) {
    return ['sudo apt install git git-lfs unzip dotnet-sdk-10.0', 'Node 22+: your distro package may be older; use https://nodejs.org/en/download or NodeSource.']
  }
  return ['git + git-lfs: https://git-scm.com/downloads and https://git-lfs.com', 'Node 22+:       https://nodejs.org/en/download', '.NET SDK 10+:   https://dotnet.microsoft.com/download', 'unzip:          your distro\'s package manager']
}

function checkDependencies() {
  step('Checking the toolchain')
  const missing = []
  const hasGit = !probe('git', ['--version']).missing
  if (!hasGit) missing.push('git')
  else if (!probe('git', ['lfs', 'version']).ok) missing.push('git-lfs')
  const nodeMajor = Number(process.versions.node.split('.')[0])
  if (nodeMajor < 22) missing.push(`Node >= 22 (this is ${process.versions.node})`)
  const sdks = probe('dotnet', ['--list-sdks'])
  // `dotnet --version` follows a global.json pin and can name an older SDK than the newest installed, so list them all.
  if (!sdks.ok || !sdks.text.split('\n').some((l) => Number(l.split('.')[0]) >= 10)) missing.push('.NET SDK >= 10')
  if (!foreign) {
    if (plat === 'linux' && probe('unzip', ['-v']).missing) missing.push('unzip')
    if (win && probe('tar', ['--version']).missing) missing.push('tar.exe (built into Windows 10 1803+)')
  }
  if (!missing.length) return note('git, git-lfs, node, dotnet: ok')

  console.error(`    missing: ${missing.join(', ')}`)
  console.error('    install them with:')
  for (const l of installHints()) console.error(`      ${l}`)
  if (dry) return note('(dry run: carrying on)')
  process.exit(1)
}

// ---------- 3. assets ----------

function prepareAssets() {
  const textures = path.join(root, 'public', 'textures')
  step('Git LFS textures')
  if (!dry) probe('git', ['lfs', 'install', '--skip-repo']) // idempotent; makes sure the smudge filter is on
  const pointers = existsSync(textures) ? [...walk(textures)].filter((f) => f.endsWith('.jpg') && statSync(f).size < 500).length : 0
  if (pointers > 0) act('git', ['lfs', 'pull'])
  else skip('every map is already a real image')

  step('npm dependencies')
  const installed = path.join(root, 'node_modules', '.package-lock.json')
  if (mt(installed) === 0 || mt(path.join(root, 'package-lock.json')) > mt(installed)) {
    const r = act('npm', ['ci']) // not `install`, which can rewrite the lockfile and leave the tree dirty
    if (r.status) die('npm ci failed')
  } else skip('node_modules is newer than package-lock.json')

  step('Godot textures')
  const manifest = path.join(proj, 'textures', 'manifest.json')
  const bakeSources = ['gapFill', 'noise', 'mapDecode'].map((n) => path.join(root, 'src', 'render', `${n}.ts`)).concat(path.join(root, 'godot', 'tools', 'prepare-textures.mjs'))
  const ref = mt(manifest)
  const why = opts.rebuild ? '--rebuild' : ref === 0 ? 'no manifest.json yet' : firstNewer(bakeSources, ref) ? 'bake code changed' : firstNewer([...walk(textures)].filter((f) => f.endsWith('.jpg')), ref) ? 'a source map changed' : ''
  if (why) {
    note(`(${why})`)
    const r = act(process.execPath, [path.join(root, 'godot', 'tools', 'prepare-textures.mjs')])
    if (r.status) die('prepare-textures failed')
    // It leaves an unchanged manifest.json alone, which would read as stale on every later run.
    if (!dry) utimesSync(manifest, new Date(), new Date())
  } else skip('manifest.json is newer than every map and the bake code')
}

// ---------- 4. godot binary ----------

/** Where this build of Godot lives, and how to fetch it. The version has one source of truth: the GODOT_VERSION default in godot.sh. */
function godotPlan() {
  const sh = readFileSync(path.join(root, 'godot', 'tools', 'godot.sh'), 'utf8')
  const m = sh.match(/GODOT_VERSION:-([^}"\s]+)/)
  if (!m) die('cannot read the Godot version: no "GODOT_VERSION:-<tag>" in godot/tools/godot.sh. Fix the regex in start.mjs or the line in godot.sh.')
  const v = process.env.GODOT_VERSION || m[1]
  let zip, rel
  if (plat === 'linux') {
    const a = opts.arch === 'arm64' ? 'arm64' : 'x86_64'
    zip = `Godot_v${v}_mono_linux_${a}.zip`
    rel = `Godot_v${v}_mono_linux_${a}/Godot_v${v}_mono_linux.${a}`
  } else if (plat === 'darwin') {
    zip = `Godot_v${v}_mono_macos.universal.zip`
    rel = 'Godot_mono.app/Contents/MacOS/Godot'
  } else {
    // The _console build, so log output reaches the terminal that started it. arm64 Windows runs the x64 build.
    zip = `Godot_v${v}_mono_win64.zip`
    rel = `Godot_v${v}_mono_win64/Godot_v${v}_mono_win64_console.exe`
  }
  const home = foreign ? (win ? 'C:\\Users\\you' : '~') : homedir()
  const dir = process.env.GODOT_INSTALL_DIR || tp.join(home, '.local', 'opt', `godot-v${v}-mono`)
  return { v, zip, url: `https://github.com/godotengine/godot-builds/releases/download/${v}/${zip}`, dir, bin: tp.join(dir, rel) }
}

async function download(url, dest) {
  const res = await fetch(url, { redirect: 'follow' })
  if (!res.ok || !res.body) throw new Error(`HTTP ${res.status} ${res.statusText} for ${url}`)
  const total = Number(res.headers.get('content-length')) || 0
  const mb = (n) => (n / 1048576).toFixed(1)
  let got = 0
  let lastTenth = -1
  const meter = new Transform({
    transform(chunk, _enc, cb) {
      got += chunk.length
      if (process.stderr.isTTY) process.stderr.write(`\r    ${mb(got)}${total ? ` / ${mb(total)}` : ''} MB${total ? ` (${Math.floor((got / total) * 100)}%)` : ''}  `)
      else if (total && Math.floor((got / total) * 10) !== lastTenth) { lastTenth = Math.floor((got / total) * 10); console.error(`    ${lastTenth * 10}% of ${mb(total)} MB`) }
      cb(null, chunk)
    },
  })
  await pipeline(Readable.fromWeb(res.body), meter, createWriteStream(dest))
  if (process.stderr.isTTY) process.stderr.write('\n')
  if (total && got !== total) throw new Error(`download cut short: ${got} of ${total} bytes`)
}

/** @returns {Promise<string>} the Godot binary to run. */
async function resolveGodot() {
  step('Godot binary')
  const plan = godotPlan()
  // Same order as godot.sh: $GODOT_BIN, then godot-dev on PATH, then the install folder.
  if (!foreign) {
    const given = process.env.GODOT_BIN || onPath('godot-dev')
    if (given) {
      if (!existsSync(given)) die(`GODOT_BIN='${given}' does not exist`)
      skip(`using ${given}`)
      return given
    }
  }
  if (!foreign && existsSync(plan.bin)) { skip(`${plan.bin} is installed`); return plan.bin }

  note(`Godot ${plan.v} (.NET build) is not installed`)
  const extract = win ? ['tar', ['-xf', '<zip>', '-C', plan.dir]] : plat === 'darwin' ? ['ditto', ['-x', '-k', '<zip>', plan.dir]] : ['unzip', ['-q', '-o', '<zip>', '-d', plan.dir]]
  if (dry) {
    note(`download: ${plan.url}`)
    show(extract[0], extract[1].map((a) => (a === '<zip>' ? plan.zip : a)))
    if (plat !== 'win32') note(`then chmod +x ${plan.bin}`)
    if (plat === 'darwin') note(`then xattr -dr com.apple.quarantine ${plan.dir}`)
    return plan.bin
  }
  const tmp = mkdtempSync(path.join(tmpdir(), 'spayly-godot-'))
  try {
    const zip = path.join(tmp, plan.zip)
    note(`downloading ${plan.url}`)
    await download(plan.url, zip)
    mkdirSync(plan.dir, { recursive: true })
    const r = act(extract[0], extract[1].map((a) => (a === '<zip>' ? zip : a)), { capture: true })
    if (r.status) die(`extracting ${plan.zip} failed:\n${r.text}`)
  } finally {
    rmSync(tmp, { recursive: true, force: true })
  }
  if (!existsSync(plan.bin)) die(`${plan.zip} did not contain ${plan.bin}`)
  if (!win) chmodSync(plan.bin, 0o755)
  if (plat === 'darwin') probe('xattr', ['-dr', 'com.apple.quarantine', plan.dir]) // best effort: Gatekeeper refuses a quarantined download
  note(`installed -> ${plan.bin}`)
  return plan.bin
}

// ---------- godot invocation ----------

/** Linux and macOS go through godot.sh, which holds the machine-wide one-Godot-at-a-time lock. Windows has no lock. */
function godotCommand(bin, args) {
  if (win) return [bin, args]
  return [path.join(root, 'godot', 'tools', 'godot.sh'), args]
}

function runGodot(bin, args) {
  const [cmd, a] = godotCommand(bin, args)
  if (dry) { show(cmd, a, win ? '' : `GODOT_BIN=${quote(bin)} `); return { status: 0 } }
  return act(cmd, a, { env: { ...process.env, GODOT_BIN: bin } })
}

// ---------- 5. build ----------

function build() {
  step('Building the C# project')
  const dll = path.join(proj, '.godot', 'mono', 'temp', 'bin', 'Debug', 'Spayly.Game.dll')
  // Spayly.Sim.Tests and spikes/ would trigger rebuilds that change nothing; bin/obj/.godot are build output.
  const sources = ['Spayly.Game', 'Spayly.Sim'].flatMap((d) => [...walk(path.join(root, 'godot', d), { follow: false, skipDir: (n) => ['bin', 'obj', '.godot', 'textures', 'capture'].includes(n) })])
    .filter((f) => /\.(cs|csproj)$/.test(f))
  const ref = mt(dll)
  const why = opts.rebuild ? '--rebuild' : opts.pulled ? 'the pull may have deleted files' : ref === 0 ? 'no Spayly.Game.dll yet' : firstNewer(sources, ref) ? `${path.relative(root, firstNewer(sources, ref))} is newer` : ''
  if (!why) return skip('Spayly.Game.dll is newer than every .cs and .csproj')
  note(`(${why})`)
  const r = act('dotnet', ['build', '-nologo', '-v', 'q', path.join(proj, 'Spayly.Game.csproj')], { capture: true })
  if (r.status) { console.error(r.text); die(`dotnet build failed (exit ${r.status})`) }
  if (r.text) console.error(r.text.split('\n').filter((l) => /warning|error|Elapsed/.test(l)).join('\n'))
}

// ---------- 6. import ----------

function importAssets(bin) {
  step('Importing Godot assets')
  const imported = path.join(proj, '.godot', 'imported')
  const ref = mt(imported)
  const imports = [...walk(proj, { skipDir: (n) => ['.godot', 'bin', 'obj', 'capture'].includes(n) })].filter((f) => f.endsWith('.import'))
  const why = opts.rebuild ? '--rebuild' : ref === 0 ? 'no import cache yet; the first run takes about 30 s' : firstNewer(imports, ref) ? 'a .import file changed' : ''
  if (!why) return skip('the import cache is newer than every .import file')
  note(`(${why})`)
  const r = runGodot(bin, ['--headless', '--path', proj, '--import'])
  if (r.status) die(`godot --import failed (exit ${r.status})`)
  // An import that finds nothing to do leaves the folder's mtime alone, and the next run would import again.
  if (!dry) utimesSync(imported, new Date(), new Date())
}

// ---------- 7. launch ----------

function launch(bin) {
  step('Launching the game')
  const r = runGodot(bin, ['--path', proj, ...godotArgs])
  process.exit(r.status)
}

// ---------- run ----------

if (dry) console.error('dry run: read-only probes (git, dotnet) still run; nothing is downloaded, installed, built or written')
pull()
checkDependencies()
prepareAssets()
const bin = await resolveGodot()
build()
importAssets(bin)
if (opts.prepareOnly) { step('Done (--prepare-only)'); process.exit(0) }
launch(bin)
