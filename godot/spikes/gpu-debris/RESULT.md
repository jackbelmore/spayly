# Spike M1a: GPU debris (Godot 4.8 dev6 mono): result

**GO.** Compute → render with no CPU round trip works in dev6 through both route a (RD texture → `Texture2DRD`) and route b
(compute writes the MultiMesh RD buffer). The draw in frame k samples frame k's compute. 100k particles with contacts
cost **0.25 ms compute + 0.11 ms render** at full GPU clock (windowed dt p50 0.52 ms), and 1M particles with contacts cost
**4.3 ms + 0.38 ms** (windowed dt p50 4.76 ms, p95 6.46 ms). Both meet the 16.7 ms target, including the 1M stretch goal.
Issue #105113 does **not** reproduce, including with TAA/motion vectors on.

Machine: RTX 3080 Ti, Vulkan, Forward+, thread model 1 (safe). Adapter checked by `run-capture.sh`. 2026-09-24.

## What was built
`DebrisMain.cs` (driver), `Shaders.cs` (GLSL compiled at runtime via `RDShaderSource` → `ShaderCompileSpirVFromSource` →
`ShaderCreateFromSpirV`, errors printed), `Layout.cs` (explicit 16-byte structs), `CaptureDriver.cs` (copied harness),
`run_all.sh` (matrix + VRAM/clock sampling), `summarize.py`, `decode_probe.py`. Args: `--n --contacts --gravity
--route a|b|ab --hash block|random --taa`.

- SoA std430 `pos` (vec4, w = radius km) and `vel` (vec4), km and km/s, float32 relative to the central body. Bodies are
  in a std140 UBO: `b0[16]` at t and `b1[16]` at t+dt (w = GM km³/s²). The central body (Earth GM) plus 3 moons on
  analytic circular orbits. The ring runs from 10,000 to 20,000 km on a jittered lattice (no initial overlaps), with 0.1 km/s dispersion.
- KDK leapfrog, dt = 20 s (about 500 steps per inner orbit), softening 50 km. Only the bodies exert gravity; particles
  do not attract each other.
- Grid: clear → count (`atomicAdd` returns the rank inside the cell, so scatter needs no second atomic) → Blelloch
  scan (1024 elements per workgroup, recursive block sums, then top-down carry add; 2 levels for 262,144 cells and 3 for
  2,097,152) → scatter into sorted copies → contacts over 27 neighbour buckets. Duplicate buckets are skipped. It is a
  Jacobi elastic impulse on pre-contact velocities, Δv_i = 2m_j/(m_i+m_j)·(v_rel·n)·n, only for approaching pairs.
  Cell = 2·r_max. The table is next_pow2(2N) buckets.
- **The cell key matters.** A plain spatial hash (Teschner) cost 7.7–12.7 ms for contacts at 1M. The default "block" key
  gives each 4×4×4 cell block 64 consecutive buckets and hashes only the block index. That cut contacts to a
  **2.9 ms min, 2.5× faster**, with identical physics (same overlap count and momentum; `capture/hashcmp-1m-*`). At
  100k the working set is about 5 MB and fits in L2; at 1M it is about 48 MB and does not.

## Numbers (ms; min / p5 / p50 / p95; 300 frames, first 10 dropped)
Compute = `CaptureTimestamp` deltas (`compute.csv`); render = viewport `gpu_ms`.

| run | compute total | contacts | grid | integrate | render gpu_ms |
|---|---|---|---|---|---|
| Xvfb 100k c0 (r1, r2) | 0.013/0.014/0.276/0.341 · 0.013/0.014/0.276/0.329 | – | – | 0.013/…/0.275/0.340 | 0.119/0.121/1.738/1.992 |
| Xvfb 100k c1 (r1, r2) | 0.229/0.235/4.253/5.325 · 0.228/0.235/4.261/5.343 | 0.186/…/3.631/4.614 | 0.028/…/0.319/0.373 | 0.013/…/0.274/0.523 | 0.100/0.102/1.621/2.380 |
| Xvfb 1M c0 (r1, r2) | 0.101/0.103/3.157/4.171 · 0.102/0.104/3.159/4.190 | – | – | 0.101/…/3.156/4.171 | 0.642/0.643/5.232/6.002 |
| Xvfb 1M c1 (r1, r2) | 3.347/3.641/12.346/12.570 · 3.381/3.613/11.925/12.580 | 2.860/…/10.384/10.567 | 0.384/…/1.637/1.679 | 0.101/…/0.322/0.328 | 0.253/0.256/2.595/2.822 |
| Xvfb 1M c1 route b | 3.750/3.952/13.582/15.198 (incl. 0.3 ms multimesh write) | 2.868/…/9.455/10.580 | | | 0.806/0.808/2.458/2.946 |
| **windowed 100k c1** | **0.223/0.227/0.245/0.254** | 0.182/0.188/0.203/0.211 | 0.024/…/0.029/0.029 | 0.011/…/0.013/0.015 | **0.099/0.101/0.106/0.111** |
| **windowed 1M c1** | **3.599/3.950/4.298/4.736** | 2.883/3.231/3.724/4.171 | 0.386/…/0.460/0.648 | 0.102/…/0.105/0.121 | **0.255/0.257/0.379/0.639** |

Windowed `dt_ms` (whole frame, no Xvfb floor): 100k c1 is 0.049/0.058/**0.516**/3.333; 1M c1 is 0.592/4.167/**4.762**/6.455.

**Read Xvfb p50/p95 as low-clock numbers.** Under Xvfb the GPU idles about 95% of each ~45 ms frame and drops clocks.
`nvidia-smi` sampling every 250 ms during `clk-1m-c1-xvfb` found 26 samples at P3 (210 MHz SM / 5001 MHz memory),
20 at P8 (210/405) and 18 at P0 (1665/9501). The bandwidth-bound `integrate` then swings 0.10 → 3.16 ms at 1M. The windowed
runs keep P0 while rendering, and their p50 is 1.1–1.3× the Xvfb min, not 4–20× like Xvfb's own p50. Xvfb min/p5 are
the full-clock figures. Repeat runs agree to within 4% at every percentile.

## Render routes
- **a: works.** Compute `imageStore`s xyz + speed into a 1024×H RGBA32F RD texture (usage sampling|storage|update|copy_from).
  A `Texture2Drd` wraps it. An `ArrayMesh` of N `PRIMITIVE_POINTS` (zeroed vertex data, custom AABB) uses a spatial
  shader that `texelFetch`es by `VERTEX_ID` and sets `VERTEX` and `POINT_SIZE`. Non-black fraction at f060/f240:
  100k 0.250/0.256, 1M 0.361/0.377. The texture layout check found 0 of N sorted coords differing from the `pos` buffer.
- **b: works; #105113 does not reproduce.** `RenderingServer.MultimeshGetBufferRdRid` returned a valid RID. A compute
  kernel writes 16 floats per instance (3×4 row-major basis|origin, then RGBA) into it. **No `MultimeshSetBuffer` or
  `MultimeshGetBuffer` call anywhere.** It renders and moves: 1M f060/f240 non-black 0.361/0.373, PSNR 11.2 dB between
  them, and 22.5 dB against route a at the same frame. A readback at the end shows compute data (instance 0 =
  `1,0,0,-2.89,0,1,0,0.38,0,0,1,17.14,0.49,0.57,0.85,1`). With `--taa 1` (motion vectors) the uniform set was never
  invalidated and the image still moved (`capture/routeb-taa`). Costs: the buffer is +64 MB at 1M, and `gpu_ms` is
  0.81 vs 0.25 ms (1M instanced single-vertex draws).
- **c: not attempted** (a and b both worked).

## Frame ordering (route a): same frame
A probe quad pinned to the camera draws two rows of 16 bit-bars. The top row is the frame number the CPU set as a
uniform in `_Process` (the frame the draw belongs to). The bottom row is the frame number integrate wrote into the
texture's last texel, which the draw samples through the same `Texture2Drd`. Decoded in-engine at frames 20–24 and from
every f060/f240 PNG (`decode_probe.py`), the result is always `cpu_row = gpu_row = F−1` for a shot taken in `_Process` of
frame F, so `draw_minus_compute = 0` in every route-a run. **The draw of frame k uses the positions from frame k's compute.**
The RD graph orders the compute list recorded in `_Process` before that frame's draw.

## Correctness
- **Layout round trip** passed: pos/vel (16 MB each at 1M) and the 512 B UBO came back byte-identical, and `pos[N−1]` matches field by field.
- **Scan** passed: 0 of 2,097,152 starts wrong, total = 1,000,000.
- **Momentum**, gravity off, contacts on, 300 frames: |ΔP|/Σm|v| = **2.9e-10 at 100k and 5.4e-11 at 1M** (float noise).
  ΔKE/KE = −5.2e-5 and +1.4e-4 (Jacobi multi-contact is not exactly energy-conserving).
- **Overlapping pairs at the end** (gravity on): contacts on 356 at 100k and 3,451 at 1M (0.36%); contacts off 9,354 and
  41,982. Frame 1: 0 and 58 (58 come from the first drift). No non-finite values.

## GC, VRAM, RIDs
- `GC.CollectionCount(0)` was 3 at frame 50 and 3 at the end in every run. Bytes allocated inside `_Process` from frame 51 to
  the end: **0**. Push constants use `stackalloc` with the `ReadOnlySpan` overloads, and timings go into preallocated
  arrays written at exit.
- VRAM (`nvidia-smi` used): before 4995–5319 MiB (baseline about 5.0–5.3 GB today, not 6.4), max during about 5325 MiB at
  100k and about 5450–5570 MiB at 1M (the whole Godot process). Our own allocations: 100k about 11 MB, 1M about 104 MB
  (64 MB of particle buffers, 16 MB texture, 16 MB table, 8 MB ranks), plus 64 MB for route b.
- `_ExitTree` frees 38–39 RIDs (sets → pipelines → shaders → buffers/texture) after detaching the `Texture2Drd`. No
  RID-leak or error lines appeared in any `run.log`.

## Problems hit (exact text)
- C# names differ from the brief: `Texture2Drd`, `RDShaderSpirV`, `ShaderCompileSpirVFromSource`, `ShaderCreateFromSpirV`.
- `error CS1503: Argument 2: cannot convert from 'System.ReadOnlySpan<byte>' to 'byte[]'`: the span overload of
  `StorageBufferCreate` has no default `usage`, so it must be passed explicitly.
- Frames that stall on a readback (`GetImage`, `BufferGetData`) report a partial timestamp set (7 → 2). Re-resolving
  names allocated 2,032 B after frame 50, so the driver now resolves once and skips those frames (7 per run).
- Timestamps come back 3 frames late (`get_frame_delay` = 2).
- Under Xvfb: `WARNING: Failed to create wd.xic as the input method or its xim server may not have started yet.` (harmless).

## Evidence (gitignored, local)
`capture/<run>/{compute.csv,frametimes.csv,result.json,run.log,stdout.txt,vram_during.csv,01-f060.png,02-f240.png}` for
runs `n100k-c{0,1}-r{1,2}`, `n1m-c{0,1}-r{1,2}`, `n100k-c1-g0`, `n1m-c1-g0`, `n1m-c1-routeb`, `win-100k-c1`, `win-1m-c1`,
`clk-1m-c1-xvfb`, `hashcmp-1m-{random,block}`, `routeb-smoke`, `routeb-taa`. Screenshot example:
`capture/n1m-c1-r1/01-f060.png`. Reproduce with `./run_all.sh`, then `python3 summarize.py`.

Not done (outside this spike's write scope): the M1a row in `docs/godot/decisions.md`.
