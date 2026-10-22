# M7 verification recipes (not yet run)

Run from the `m7-undo` worktree, one Godot process at a time. First check for games (handoff §Rules).
`G=godot/Spayly.Game`, and each capture's log is `L=$G/capture/<name>/run.log`.

## Tests
- Run `nice -n 19 dotnet test godot/Spayly.Sim.Tests`. Expect 247 + 7 undo + 10 SPH pacing tests to pass.
- These must pass too: `KeepsFiftyUndoPointsAndRestoresADeepCopy` and
  `PseudoNewtonianTests.SystemWithoutAHoleIsBitIdenticalToBeforePw`.
- Realism: run `godot/tools/run-capture.sh $G --frames 60 --out $G/capture/m7-realism -- --actions "wait:30;realismSelftest"`.
  Expect `[realism] selftest 8/8 PASS`.

## M7.1–M7.3 undo
Every capture below uses `godot/tools/run-capture.sh $G <flags> --out $G/capture/<name> -- --actions "<string>"`.

1. **`m7-3-tde`**, flags `--windowed --frames 2400`:
   `wait:30;togglePause;logFullState:pre-demo;tdeDemo:1e6;wait:2;logFullState:post-demo;setWarp:1e4;wait:1200;togglePause;logTidal;logFullState:stream;shot:stream;key:ctrl+z;wait:3;logFullState:undo1;key:ctrl+z;wait:3;logFullState:undo2;shot:undone;key:ctrl+shift+z;wait:3;logFullState:redo1;key:ctrl+y;wait:3;logFullState:redo2`

   Pass:
   - `undo1` has particles=0 and the star intact;
   - `undo2` equals `pre-demo`, hash and app_hash;
   - `redo1` equals `undo1`;
   - `redo2` has about 50,000 particles and matches `stream`.

   Check the hints with `shotcheck.py`.
2. **`m7-1-mass`**, flags `--frames 400`:
   `wait:30;togglePause;select:earth;wait:3;logInspector;logFullState:before;setMassSlider:1;wait:3;logInspector;logFullState:changed;key:ctrl+z;wait:3;logInspector;logFullState:undone;shot:inspector-undone;key:ctrl+shift+z;wait:3;logFullState:redone`

   Pass:
   - `undone` equals `before`;
   - `redone` equals `changed`;
   - run.log has `[undo] hint Undone: Earth's mass change`.
3. **`m7-1-ten`**, flags `--frames 900`:
   `wait:30;togglePause;logFullState:s0;spawnBH:10,earth;logFullState:s1;setMode:asteroid;applyPreset:Chicxulub;fireAt:mars;logFullState:s2;follow:ast-1;wait:90;erase:800,450,80;logFullState:s3;select:mars;setMassSlider:0.5;logFullState:s4;setRadiusSlider:0.3;logFullState:s5;setMoons:major;logFullState:s6;resetBody:mars;logFullState:s7;removeBH:bh-1;logFullState:s8;holePair:10,10,1e5;logFullState:s9;tidalDemo:moon;logFullState:s10`,
   then `;key:ctrl+z;wait:3;logFullState:uK` for K = 1..10, then `key:ctrl+shift+z` ten times, then `;wait:5;logFullState:r10`.

   Pass:
   - there are 10 `[undo] take` lines;
   - `uK` equals `s(10−K)`;
   - `r10` equals `s10`.
4. **`m7-3-sixty`**, flags `--windowed --frames 3000`. Pass `-- --debris-stress 1000000 --actions "$A"`, where:
   `A="wait:120;select:earth"; for i in $(seq 1 60); do A="$A;setMassSlider:0.$((i%9+1));wait:10"; done; A="$A;logFullState:after60"; for i in $(seq 1 60); do A="$A;key:ctrl+z;wait:10"; done; A="$A;logFullState:end"`

   Pass:
   - every `[undo]` line has total_bytes ≤ 314572800 and total_vram ≤ 268435456;
   - some `[undo] shed` lines appear;
   - frame p95 ≤ 33 ms (`tools/summarize-frametimes.py`).
5. **`m7-3-sph-undo`**, flags `--windowed --frames 900`, two runs:
   - `wait:60;sphDemo:earth,theia;wait:240;key:ctrl+z;wait:30;key:ctrl+z;wait:30;logFullState:end;logSph`
   - `wait:30;tdeDemo:1e6;setWarp:1e4;wait:900;key:ctrl+z;wait:60;key:ctrl+z;wait:60;logFullState:end`

   Pass:
   - `[sph] cancelled by undo` appears;
   - no new `error` or `exception` lines.

## M7.4–M7.5 SPH
Define `W(){ s=""; for i in $(seq $1); do s="$s;wait:200;logSph"; done; echo "$s"; }`. The base recipe is
`wait:30;setSph:fast;setMode:asteroid;applyPreset:Theia;fireAt:earth;setWarp:1000;wait:400;logSph$(W n);logDebris`,
with `--warmup 10`.

| capture | flags | changes to the base recipe |
| --- | --- | --- |
| `m7-4-fast-windowed` | `--windowed --frames 8800` | `W 40` |
| `m7-4-hero-windowed` | `--windowed --frames 8800` | `setSph:hero`, `W 40` |
| `m7-5-fast-windowed` | `--windowed --frames 14000` | `W 68` |
| `m7-5-hero-windowed` | `--windowed --frames 14000` | `setSph:hero`, `W 68` |
| `m7-5-hero-xvfb` | `--frames 16000` | `setSph:hero;sphWall:400;`, `W 78` |

Pass:
- **Tags round-trip:** `grep '\[sph\] gpu' $L | grep -o 'samples [0-9]*, dropped [0-9]*'`. Samples must be > 0, and dropped must stay small.
- **No 1↔16 flicker:** `grep -o 'substeps=[0-9]*' $L | cut -d= -f2 | awk 'NR>1&&((p==1&&$1==16)||(p==16&&$1==1)){f++}{p=$1}END{print "flips",f+0}'` must print `flips 0`.
- **Substep cost:** in the `run cost` line, p95 ≤ 1.5 × p50. Hero must be no worse than p50 6.95 ms / p95 8.24 ms.
- **Frame p95 ≤ 33 ms** over the SPH frames (`frametimes.csv`, between the SPH start and the `run cost` frame). The
  `budget=` values must be 8–20 ms.
- **Ends on physics (M7.5):** `grep -c 'ended by physics' $L` = 1, and `grep -c 'cut short' $L` = 0.
- **Xvfb and windowed agree:** in `[sph]   earth:`, the Hero mass % and spin period of Xvfb and windowed agree to ≤ 1 %.
- **Demotion closure:** `grep 'ledger closure' $L` shows all three values as `0.00E+000`.
