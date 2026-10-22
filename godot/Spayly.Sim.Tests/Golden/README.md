# Golden values from the web build

`web-universe-j2000.json` was produced by running the TypeScript sim (`src/sim`, commit b880fa7) with the script in
`web-universe-j2000.source.ts.txt` under vitest: the 39-body system at J2000, its initial energy and barycentre, the
state after 30 days (day steps) with the substep count, and after one Julian year. The C# `Universe` must reproduce the
initial state to ~1e-9 relative and the one-year total energy to well within the 1e-6 test bound; the substep count
should match exactly if the adaptive step logic is ported verbatim. These are diagnostics for the port, not a test
the web build itself passes — regenerate them if `src/sim` ever changes.

The first version stored `earth.pos` etc. by reference; `Universe.step` mutates those objects in place, so `earth0`,
`moon0`, `sun0` and `after30d.earthPos/earthVel` all held the one-year state. The script now copies every vector and the
file was regenerated (at 31838ef, `src/sim` unchanged since b880fa7); every other value came out bit-identical.
`web-universe-handbuilt.json` comes the same way from `web-universe-handbuilt.source.ts.txt` (commit 31838ef): the
catalogue-free systems of `HandBuilt.cs` stepped 30 days, a mid-frame Theia contact with a stand-in handler, and a small
seed-7 belt. Only + − × ÷ and sqrt build them, so `UniverseTests` asserts them with exact equality; `ParticlesTests`
allows 1e-12 because the belt goes through sin/cos/atan2/log.

# Golden values from the C# build before Paczyński–Wiita

`no-hole-pre-pw.json` was written by `PseudoNewtonianTests.NoHoleRun` compiled against `Spayly.Sim` at 4bdf447 (M5.2 not yet
applied; `SPAYLY_WRITE_GOLDEN=1 dotnet test --filter SystemWithoutAHole` on an exported copy): the hand-built ten-body
system with a 5 km rock striking Earth and a 40-particle belt, stepped 30 days, then a one-year aim prediction. It holds
every body's state as double bits and SHA-256s of the particles and the predicted path. With no black hole present the
PW code paths must not run, so `SystemWithoutAHoleIsBitIdenticalToBeforePw` asserts exact equality. Regenerate it only
from a build without PW.
