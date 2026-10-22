# Spike M1b: engine texture streaming of a 16384×8192 planet map

Engine 4.8 dev6 mono, RTX 3080 Ti, Xvfb 1600×900 through `tools/run-capture.sh` (so `dt_ms` has the ~37.5 ms Xvfb
floor; GPU cost comes from `gpu_ms`). Measured 2026-09-24. Other spikes shared the machine, so the absolute
`nvidia-smi` numbers include other processes; read the deltas inside a run.

## Verdict

- **A custom `shader_type spatial` with a plain `uniform sampler2D` does feed streaming, but only when the shader
  references `UV` or writes `STREAMING_UV`.** The feedback is per material. It is computed from the derivatives
  of `UV`, or of `STREAMING_UV` if the shader writes it, and not from the coordinate passed to `texture()`. If a
  shader computes its UV itself and never touches `UV` or `STREAMING_UV`, the texture stays at the 2048 floor
  forever. The near view then gives 27.5 dB against full resolution, as blurry as the web build's 2048 copy.
- **Streaming produces the same pixels as streaming off.** Once the top mip arrives, every streamed shot is
  bit-identical to the streaming-off reference (PSNR = inf), for both the custom and standard materials.
- **Game code can force one texture to full resolution**: set `StreamedTexture2D.MaxLodOverride = 1` (LOD 0),
  then call `TextureStreaming.FlushTextureStreaming()`. `MinLodOverride = 1` does nothing.
- **Go**, with conditions (at the end).

## Import

| | |
| --- | --- |
| Importer name | `streamed_texture_2d` (visible name "Texture2D Streamed"; found in the binary next to the other importer names) |
| Resource type | `StreamedTexture2D`, output `.s3tc.stex`, `imported_formats=["s3tc_bptc"]`, `vram_texture=true` |
| Params used (`textures/earth_day.jpg.import`) | `compress/high_quality=false`, `compress/hdr_compression=1`, `compress/normal_map=0`, `compress/channel_pack=0`, `roughness/mode=0`, `roughness/src_normal=""`, `streaming/min_lod_override=0`, `streaming/max_lod_override=0`. Godot kept these as written and added `[remap]`/`[deps]`. |
| Format | BC1 (DXT1), 15 mips: 16384² BC1 is 64 MiB, plus mips = 89,478,668 B |
| Import time | **4.81 s**, twice. Timed inside the lock with a `GODOT_BIN` wrapper; timed outside it, lock waits on other spikes made it 11.6–16.6 s. GPU compressor on (the default). |
| Memory | Godot peak RSS **2.17 GB**. Lowest MemAvailable **5962 MB**, from 7873 MB at the start, sampled every 0.25 s (19 samples). The first 2 s-sampled import: 3 samples, minimum 7396 MB. The watchdog never fired. |
| `.godot/imported` on disk | **90,877,022 B**: `.stex` 89,478,668 plus the 2048 control `.ctex` 1,398,172 |
| 2048 control | `textures/earth_day_2048.jpg` → `public/textures/lod/earth_day.jpg@2048.jpg` (**the brief's `lod/earth_day@2048.jpg` does not exist**), importer `texture`, `compress/mode=2` (VRAM, BC1), `mipmaps/generate=true` |

Runtime: `ResourceLoader.Load("res://textures/earth_day.jpg")` returns **`StreamedTexture2D`, 16384×8192**, in 3.2–3.7 ms.

## VRAM (settled value at the end of each phase)

`texMem` is `Performance.RenderTextureMemUsed` in MB, and about 34.4 MB of it is the engine's own textures.
`budget` is `TextureStreaming.GetMemoryBudgetBytesUsed()` in MB. `nv` is `nvidia-smi memory.used` in MiB; the
baseline before the runs was 5054. `res` is the resident width, read through `RenderingServer.TextureGetRdTexture`
and `RenderingDevice.TextureGetFormat(...).Width`.

| Run | far: texMem / budget / nv / res | mid | near |
| --- | --- | --- | --- |
| custom, streaming on | 35.77 / 1.40 / 5319 / 2048 | 39.97 / 5.59 / 5319 / 4096 | 123.85 / 89.48 / 5575 / 16384 |
| standard, streaming on | 35.77 / 1.40 / 5319 / 2048 | 39.97 / 5.59 / 5319 / 4096 | 123.85 / 89.48 / 5575 / 16384 |
| custom, streaming off (reference) | 123.85 / 0 / 5511 / 16384 | same | same |
| 2048 plain `CompressedTexture2D` | 35.77 / 0 / 5313 / 2048 | same | same |
| procedural UV, no `UV`/`STREAMING_UV` | 2048 / 1.40 | 2048 | **2048 (never streams)** |

Transient peaks: 151.8 MB during the swap from 8192 to 16384, and 213.3 MB when an override is set on a texture
already at 16384 (see Force). Resident width goes up in whole steps (the texture is reallocated), and the floor
is 2048 because of the project setting `max_lod=3`.

## Sharpness and time to sharp

PSNR against the streaming-off shot of the same frame (`shotcheck.py --ref`):

| Shot (frame) | custom on | standard on | 2048 plain | procedural UV without `STREAMING_UV` |
| --- | --- | --- | --- | --- |
| far (80) | inf | inf | inf | 89.2 |
| mid (170) | inf | inf | 49.93 | 56.6 |
| near (280), hold (380), force (400), after (470), far2 (590) | inf | inf | 26.97 (near) | 27.52 (near) |

The standard-off and custom-off references are also identical to each other (inf).

Time to sharp: the camera cuts to near at frame 180. The streamed texture was still 4096 at frames 181–183,
8192 at 184, and **16384 at 186**, where budgetUsed plateaued at 89.48 MB. That is 6 frames / 311 ms
(t = 7508 → 7820 ms) at the ~42 ms Xvfb frame, and the same frames 184/186 in all 10 runs without per-frame shots. With a shot every
frame (each `GetImage` stalls about 480 ms), shot n181 (4096 resident) = 30.29 dB, and n182 onwards = inf. Latency
is in wall time (readback plus the I/O thread), not in frames.

GPU cost of the instrumentation, near phase p50: custom 2.556 ms against 2.520 ms off; standard 2.578 against
2.546. That is +0.03 ms (about 1 %), close to the noise.

## Force full resolution (`capture/custom-forcefar-*`, `custom-on`, `custom-forcemin`)

| Case | setter | `FlushTextureStreaming()` | result |
| --- | --- | --- | --- |
| `MaxLodOverride = 1` while far (2048 resident) | 0.97 ms | 0.10 ms | `FlushCompleted` fired the next frame (61); 16384 resident at frame 62. Frame 61 dt 82.6 ms against the 37.5 ms floor: a **hitch of about 45 ms** on the CPU/render thread, gpu_ms unchanged (0.068). |
| `MinLodOverride = 1` while far | 1.13 ms | 0.12 ms | **no change**: stays 2048; `FlushCompleted` fired in the same frame |
| Either override while 16384 is already resident (frame 390) | **53.3 / 53.9 / 54.8 / 55.7 ms** (4 runs) | 0.15–0.21 ms | a synchronous reload: texMem +89 MB for one frame, frame dt 116–130 ms, gpu_ms normal |

Answer: yes. `MaxLodOverride = 1` plus a flush forces one texture to LOD 0. The call costs about 1 ms, the next
frame hitches about 45 ms to bring in the 89 MB, and it is sharp 2 frames later. Setting any override reloads
the mips already resident, synchronously, so check the resident width first (the RD query above) and do not
re-set an override on a texture that is already resident.

## Going back down (`capture/decay-*`, frames 480–599 far)

- While visible, it never goes back down. With the default 5000 ms decay and with 500 ms, the texture stayed at
  16384 for the whole 420-frame (about 18 s) far phase, even though the far view needs only 2048.
- `MemoryBudgetMbOverride = 16` at frame 480 evicted it to 4096 (5.59 MB, the largest that fits) within 2 frames;
  texMem was back to 39.97 by frame 484.
- With the Earth hidden and `inactivity_decay_rate_ms = 500`, it drops one level per period: 16384 → 8192 at +16
  frames, → 4096 at +27, → 2048 at +39 (about 1.6 s).

## Startup (the frame after the first draw, engine ticks)

| Texture | `Load` | first frame drawn |
| --- | --- | --- |
| 16K streamed | 3.4 / 3.7 ms | 1429 / 1491 ms |
| 2048 plain | 3.1 / 3.2 ms | 1438 / 1478 ms |
| 16K, streaming off | 112 / 120 ms | 1549 / 1559 ms |

The first run after the import took 2599 ms: shader compilation and a cold cache, not the texture.

## Problems hit (exact text)

1. The 2048 file is at `lod/earth_day.jpg@2048.jpg`, not `lod/earth_day@2048.jpg`.
2. `run-capture.sh` writes into `<project>/capture/`, and the editor import then imported every PNG and CSV
   (102 `.import` files). Fixed here with `capture/.gdignore`. **`Spayly.Game/capture` has the same problem**;
   `run-capture.sh` should create `.gdignore` (not changed here: outside this spike).
3. The import logged `ERROR: Cannot open file 'res://Spike.cs'.` (the file did not exist yet), plus the harmless
   headless-editor errors `ERROR: Plugin is not attached to debugger.` and `ERROR: Unable to start the timer because
   it's not inside the scene tree.`
4. `Spike.cs(205,55): error CS0266: Cannot implicitly convert type 'int' to 'uint'`: in C#,
   `TextureStreaming.MemoryBudgetMbOverride` is a `uint`.
5. `Texture2D.HasMipmaps()` returns `False` for both the `StreamedTexture2D` and the mipmapped 2048
   `CompressedTexture2D`, while their RD textures have 15 and 12 mips.
6. The far shots (3 px Earth) fail shotcheck's `FAIL: non-black fraction 0.0000 < 0.02`, so run-capture exits 1.
   This is expected.
7. `STREAMING_UV = uv` inside `if (uniform_bool_false)` still streamed correctly: the write only has to exist
   in the source. The value used is then undefined; write it unconditionally.
8. The `WARNING: Failed to create wd.xic ...` Xvfb warning on every run. Harmless.

## Recommendation: go for engine streaming, replacing the web build's 512/2048 + full swap

- Every planet shader writes `STREAMING_UV` with the exact coordinate it samples the albedo with. Otherwise, if
  the shader never uses `UV`, the map stays at 2048 without any warning. The feedback is one value per material,
  so a detail layer tiled at a different scale is not covered (untested).
- Turn on `memory_budget_enabled` with a real budget. A visible texture never gives memory back without budget
  pressure.
- Focus-first (double-click): if the resident width is below full, `MaxLodOverride = 1` plus a flush, which costs
  about 1 ms plus about a 45 ms hitch. Resetting the override to 0 when focus moves away is untested.
- Risk: dev6. PR #113429 names `sampler2DStreamed` as a follow-up planned before release "to avoid paying the
  cost in all shaders", so how shaders opt in may change. Re-run this spike (`analyze.py`) on every engine bump.
  The 2048 plain copy remains the fallback, and costs the same as the streamed texture's far floor.

## Files

`Spike.cs` (scene, camera by frame count, stream.csv), `earth_custom.gdshader`, `earth_procuv*.gdshader`,
`earth_constuv.gdshader`, `analyze.py` (per-phase VRAM, LOD transitions, PSNR), and `capture/<run>/`
(`stream.csv`, `stream-events.txt`, `frametimes.csv`, PNGs; gitignored). Key shots:
`capture/custom-on/03-near.png`, `capture/ref-custom/03-near.png`, `capture/tex2048/03-near.png`,
`capture/procuv-plain/03-near.png`, `capture/custom-noforce/04-n181.png`.
Whole-run gpu_ms p50/p95: custom-on 2.508/3.382, standard-on 2.543/3.452, ref-custom 2.488/3.348.
