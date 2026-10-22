> **UNTRUSTED research (copied 2026-09-24).** Claims here are not facts until verified; verified claims live in `docs/godot/decisions.md`.

# Real Astrophysics → Real-Time Algorithms: A Build Guide for a Godot 4 / C# / GPU-Compute Planet-Smashing Sandbox

The best architecture is a three-tier hybrid. At the top, a GPU N-body core handles gravity for the "important" bodies. When two bodies collide, the Leinhardt & Stewart (2012) analytic scaling laws decide the outcome. Only the currently visible, interesting collision runs a transient GPU SPH patch of about 10^5 particles, and the debris is then demoted to cheap test particles and baked VFX. Black holes should be rendered with precomputed deflection tables (Bruneton 2020) rather than per-pixel geodesic marching, except where Kerr spin is required.

Tags used throughout the report:
- **[SCI]** means established or peer-reviewed science.
- **[GAME]** means a reasonable game-oriented simplification of that science.
- **[ART]** means creative license taken for fun that departs from physical accuracy.

## TL;DR

- **Collisions:** replace Benz & Asphaug Q*_D with the full Leinhardt & Stewart (2012) / Stewart & Leinhardt (2012) regime model. It covers merge, graze-and-merge, hit-and-run, partial accretion, erosion and super-catastrophic disruption, and costs microseconds per event. Add optional GPU SPH (Tillotson EOS, 10^4–10^5 particles in real time, 10^6 as a "cinematic replay" bake) for impacts the player is watching. Research codes need 10^7–10^8 particles to converge even on large-scale results, so a game's SPH will look right but will not be quantitatively predictive.
- **Scale:** your 282 ms/step JavaScript all-pairs baseline at 10^4 bodies should fall to well under 1 ms on an RTX 3080 Ti using a tiled compute shader (engineering estimate). Barnes–Hut on the GPU extends this to 10^6 debris particles in a few ms/step. A counting-sort uniform grid handles collision broad-phase in O(N). For comparison, Burtscher & Pingali's CUDA Barnes–Hut took 5.2 s/step for 5 million bodies on a 2009-era Quadro FX 5800.
- **Black holes and VFX:** Bruneton's precomputed-table Schwarzschild shader runs at about 150 fps in Full HD on a GTX 960, versus about 45 fps for 1000-step ray marching at similar accuracy. SpaceEngine's Kerr ray tracer reaches 60 fps at 4K on an RTX 2080 by tracing at about 1/3 resolution. Ground-truth footage from NASA (generally not copyrighted), ESA/Hubble (CC BY 4.0), DART/LICIACube, Chelyabinsk and the NASA Ames Vertical Gun Range supplies colours, cone angles and timescales.

## Summary Table

"Cost" figures marked (est.) are my engineering estimates, scaled from the published benchmarks cited in the body. They are not measured on a 3080 Ti.

| Feature | Recommended algorithm | Count & cost on RTX-3080-Ti class | Key reference(s) |
|---|---|---|---|
| Gravity, major bodies | Tiled all-pairs direct summation in a compute shader; symplectic leapfrog (KDK) with adaptive/block timesteps near close encounters | 10^3–10^4 bodies, ≪1 ms/step (est.) | Nyland, Harris & Prins, GPU Gems 3 ch. 31 (2007) |
| Gravity, debris and SPH self-gravity | GPU Barnes–Hut octree (θ≈0.5–0.7) or LBVH; FMM optional | 10^5–10^6 particles, ~1–10 ms/step (est.); 5×10^6 took 5.2 s on a 2009 GPU and ~1.5 s in Hsin-hung's 2-D quadtree CUDA version | Burtscher & Pingali 2011, doi:10.1016/B978-0-12-384988-5.00006-1; Bédorf et al. 2012 (Bonsai, arXiv:1106.1900); Yokota & Barba (arXiv:1010.1482) |
| Collision outcome | LS12 analytic regime model + Kokubo–Genda hit-and-run criterion | µs/event, CPU (C#) | Leinhardt & Stewart 2012, doi:10.1088/0004-637X/745/1/79; Stewart & Leinhardt 2012, doi:10.1088/0004-637X/751/1/32 |
| Planet-scale impact hydrodynamics | Transient GPU SPH (Tillotson EOS, Balsara-switched artificial viscosity, tree gravity) | 10^4–10^5 interactive; 10^6 near-real-time or baked replay | Kegerreis et al. 2019/2022 (SWIFT); Schäfer et al. 2016 (miluphcuda) |
| Broad-phase collision detection | Uniform grid: hash → counting sort (atomic count + prefix sum) or radix sort → cell start/end | O(N); 10^6 particles in ~1 ms class (est.) | Green, "Particle Simulation using CUDA" (NVIDIA 2010/2012) |
| Rubble-pile re-accumulation | Soft-/hard-sphere granular contacts + tree gravity, with delayed merging | 10^3–10^4 grains per body in real time | Leinhardt & Stewart 2012 (PKDGRAV method); Richardson et al. |
| Ejecta | Sample Housen & Holsapple (2011) mass–velocity power laws into GPU particles | 10^5–10^6 test particles, <1 ms (est.) | Housen & Holsapple 2011, Icarus 211, 856 |
| Fine dust and plumes | Non-gravitating GPU particles + baked flipbooks / volumetrics | 10^6+, fill-rate bound | DART/LICIACube, Deep Impact, AVGR footage |
| Black-hole lensing, Schwarzschild | Precomputed 2-D deflection tables + filtered star cubemap | Full HD, ~150 fps on GTX 960 | Bruneton 2020, arXiv:2010.08735 |
| Black-hole lensing, Kerr | Low-resolution geodesic RK integration into a "warp vector" target, then upscale | 4K 60 fps on RTX 2080 (SpaceEngine) | James et al. 2015, doi:10.1088/0264-9381/32/6/065001; SpaceEngine blog 2022 |
| Accretion disk | Thin-disk T(r) + g-factor Doppler/gravitational shift, I ∝ g^4 | Negligible on top of lensing | Shakura & Sunyaev 1973; Bruneton 2020 |
| Tidal disruption | Roche/tidal-radius trigger → SPH or analytic "banana-stream" particles; fallback ∝ t^-5/3 | 10^4–10^5 particles | Rees 1988, doi:10.1038/333523a0 |
| Star surfaces | Blackbody LUT + limb darkening + animated cellular granulation + flare sprites | <0.2 ms (est.) | Standard stellar atmospheres |
| Stellar collisions and mergers | Offline SPH bakes (StarSmasher) + analytic "sticky merge with mass loss" at runtime | Bake 10^5 particles per star | Gaburov, Lombardi & Portegies Zwart 2010, doi:10.1111/j.1365-2966.2009.15900.x |

---

## 1. Planet-Scale Collisions

### 1.1 State of the art [SCI]

**SWIFT and Kegerreis et al.**
- SWIFT is an open-source (LGPL v3), task-parallel SPH + gravity code (Schaller et al. 2016, doi:10.1145/2929908.2929916; Schaller et al. 2023, arXiv:2305.13380).
- Kegerreis et al. (2019, MNRAS 487, 5029) reported that SWIFT enabled "a 100–1000× improvement in the number of particles" for giant impacts.
- In their Uranus-impact convergence study, "even large-scale results such as the rotation rate are not converged with standard-resolution simulations of 10^5 and 10^6 particles". The rotation rate "appears to be well-converged with 10^7 and 10^8 particles, but not fewer", and "the low mass of rock placed into orbit has not converged at all by 10^7 particles".
- The companion SEAGen tool (github.com/jkeger/seagen) builds near-equilibrium spherical particle arrangements, with every particle's SPH density "within 1% of the desired value". That is directly useful to you as a planet initializer.

**Kegerreis et al. 2022 (ApJL, "Immediate Origin of the Moon as a Post-impact Satellite")**
- They used up to 10^8 particles, each "6 × 10^16 kg and an effective size in the planet of ∼14 km".
- A lunar-mass satellite contained about 10^6 particles.
- At high resolution, a canonical impact can immediately place a Moon-like satellite outside the Roche limit, where earlier 10^5–10^6-particle runs produced a debris disk that took years to accrete.
- They repeated scenarios at 10^4, 10^4.5 … 10^8 particles to test convergence.
- Hosono et al. (arXiv:1612.06984) varied resolution from 3×10^3 to 10^8 particles and found results "does not converge, but shows oscillatory behaviour".

**Canup, Ćuk & Stewart**
- In the canonical model (Canup & Asphaug 2001; Canup 2004), a Mars-mass Theia hits obliquely at about v_esc and leaves a disk of roughly ≤2.5 lunar masses, mostly near or inside the Roche radius (about 2.9 R⊕).
- Ćuk & Stewart (2012) and Canup (2012) proposed high-energy, high-angular-momentum alternatives.
- Lock & Stewart (2017, JGR Planets, doi:10.1002/2016JE005239) showed that such impacts create **synestias**: rapidly rotating, partly vaporized, biconcave structures whose angular momentum exceeds the corotation limit, with a corotating inner region, a transition region and a disk-like outer region.
- Lock et al. (2018, doi:10.1002/2017JE005333) formed the Moon inside a synestia. A UC Davis news release on that work says synestias "likely don't last long — perhaps only hundreds of years".

**Hull et al. (2023, arXiv:2310.14476)** showed that EOS choice and cutoff density measurably change Moon-forming-impact outcomes.

**Qualitative phenomena your game must reproduce [SCI → GAME]:**
1. **Debris disks.** Oblique impacts near v_esc throw a spiral arm of material that winds into a disk. Much of it lies inside the Roche limit, and clumps form outside it.
2. **Mantle stripping.** In partial accretion, "the target body grows by preferentially accreting the iron core of the projectile and the escaping fragments are derived primarily from the silicate mantles of both bodies" (Stewart & Leinhardt 2012). Track core mass fraction per body.
3. **Hit-and-run.** At grazing geometry and modest speed, the projectile passes through, gets smeared and often disrupted, and leaves.
4. **Synestias.** For very energetic, high-angular-momentum mergers, render a puffed, glowing, flattened vapour torus instead of a sphere, and let it contract over (accelerated) time [ART: compress hundreds of years into seconds].
5. **Re-accretion.** Bound debris falls back over several orbits and spins up the remnant.

### 1.2 Minimum viable SPH [SCI + GAME]

**What particle counts buy you.** This is a synthesis of the convergence studies above.

| N (total) | What it resolves | Game use |
|---|---|---|
| 10^4 | Gross merge vs. bounce vs. hit-and-run; bulk shape; a plume-like "arm". The mass in orbit is noise. | Always-on real-time preview |
| 10^5 | Spiral arms, disk formation, core/mantle separation, rough remnant spin. Still not converged (Kegerreis 2019). | Interactive "hero" collision |
| 10^6 | Satellite clumps, credible disks, layered stripping. Rotation still drifts. | Replay bake (seconds to minutes) |
| 10^7–10^8 | Converged spin and ejected mass; Moon composition studies | Research only; bake offline for cutscenes |

**Core SPH equations [SCI].** Cubic-spline or Wendland C4 kernel W(r,h).

- Density: ρ_i = Σ_j m_j W(|r_ij|, h_i)
- Momentum: dv_i/dt = −Σ_j m_j (P_i/ρ_i² + P_j/ρ_j² + Π_ij) ∇_i W_ij + g_i
- Energy: du_i/dt = ½ Σ_j m_j (P_i/ρ_i² + P_j/ρ_j² + Π_ij) v_ij·∇_i W_ij
- Monaghan artificial viscosity Π_ij, with α≈1, β≈2 (the values Genda et al. used for planetesimal collisions). A Balsara switch suppresses shear viscosity in rotating disks (the "vanilla SPH + Balsara" setup in Kegerreis et al.).
- Timestep: Courant condition Δt = C h / (c_s + 1.2(α c_s + β μ_max)), with C≈0.3.

**Tillotson EOS is sufficient for a game [SCI → GAME].**

- **Compressed or cold branch** (ρ ≥ ρ₀, or u ≤ u_iv). Here η = ρ/ρ₀ and μ = η − 1.

  P_c = [a + b/(u/(u₀η²) + 1)] ρu + Aμ + Bμ²

- **Expanded hot branch** (ρ < ρ₀ and u ≥ u_cv):

  P_e = aρu + [bρu/(u/(u₀η²)+1) + Aμ·e^{−β(ρ₀/ρ−1)}] · e^{−α(ρ₀/ρ−1)²}

- **Between u_iv and u_cv**, interpolate linearly in u. Clamp P ≥ P_min (a small negative or zero value) to avoid tensile instability.
- Example parameters (aluminium, from Schäfer et al. 2016): A = 75.2 GPa, B = 65 GPa, E₀ = 5 MJ/kg, E_iv = 3 MJ/kg, E_cv = 13.9 MJ/kg, a = 0.5, b = 1.63, α = β = 5. Use granite/basalt, iron and ice sets for planets (Melosh 1989; Benz & Asphaug 1999).

Why Tillotson is acceptable:
- It is widely used in planetary collision work (Benz & Asphaug 1999; Canup & Asphaug 2001; Genda et al. 2012).
- Burger et al. (arXiv:1910.14334) chose it because "the rather large uncertainties in the actual physical state of bodies before the collision" made a more sophisticated EOS unjustified.
- It is branch-light, so a GPU can evaluate it per particle almost for free.

What it lacks:
- A thermodynamically consistent vapour dome and entropy, which is why synestia work uses ANEOS/M-ANEOS.
- Correct temperatures.
- **[GAME] recommendation:** use Tillotson for dynamics. Derive a display "temperature" from u with a per-material heat capacity, plus a latent-heat plateau for melt and vapour colouring. Reserve ANEOS tables (a 2-D texture lookup P(ρ,u) on the GPU) as an optional upgrade.

**Self-gravity on the GPU [SCI].**
- Direct all-pairs is fastest below about 10^4 particles. One CUDA study found "Direct Sum GPU runs faster than Barnes-Hut GPU when N < 10000".
- Above that, use Barnes–Hut. Burtscher & Pingali (2011) split it into six kernels: bounding box, octree build, centre-of-mass summarization, spatial sort, force calculation, and integration. Their code "takes 5.2 seconds to simulate one time step with 5,000,000 bodies on a 1.3 GHz Quadro FX 5800… 74 times faster than an optimized serial implementation".
- A 2-D quadtree CUDA implementation by Hsin-hung ("High-Performance CUDA Implementation of N-Body") reports "1509.54 milliseconds of run time per iteration for 5,000,000 bodies".
- An OpenCL port exists (github.com/bneukom/gpu-nbody).
- Bonsai (Bédorf, Gaburov & Portegies Zwart) is a sparse-octree code that runs entirely on the GPU.
- For FMM on GPUs, see Yokota & Barba (arXiv:1010.1482). FMM's O(N) scaling only wins at N ≫ 10^6 and is far harder to write in GLSL, so **[GAME]: implement Barnes–Hut with a Morton-sorted LBVH/octree, not FMM.**

**Open-source GPU SPH codes worth reading:**
- **miluphcuda** (Schäfer et al. 2016, A&A 590, A19). CUDA; Tillotson; Barnes–Hut self-gravity; solid, plastic and Grady–Kipp fracture. It reports "several ~10^1–10^2 times faster for a single GPU compared to a single CPU". Licence: GPLv3 — "The new version is published under the GPLv3 license" (Schäfer et al. 2020, Astronomy and Computing; github.com/christophmschaefer/miluphcuda).
- **pkdgrav3 SPH** (Meier, Potter, Reinhardt & Stadel, "Smoothed Particle Hydrodynamics in pkdgrav3 for Shock Physics Simulations. I. Hydrodynamics", ApJ, doi:10.3847/1538-4357/ae4e29). Hybrid CPU/GPU, aimed at planetary impacts; Paper II (arXiv:2603.19764) states the SPH code is "released under the GNU General Public License (version 3, GPLv3)".
- **StarSmasher** (see §4).
- **DualSPHysics.** Engineering SPH (LGPL). It measured 23 GPIPS (10^9 particle interactions per second) on an RTX 2080 Ti, 12 on a GTX 1080 Ti and 29 on a V100.

### 1.3 Real-time GPU SPH and Vulkan [SCI/engineering]

- Gunadi & Yugopuspito (2018, IEEE) implemented SPH as GLSL compute shaders compiled to SPIR-V and ran them under both Vulkan and OpenGL 4.6. "The most optimum of the work group size is 128, and our Vulkan implementation performs faster than OpenGL for 30,000 or greater particles." Code: github.com/multiprecision/sph_vulkan (MIT).
- A Rust + Vulkan + Slang fluid simulator (github.com/MarcVivas/fluid-simulation) advertises 1,000,000 particles in real time with octree neighbour search. It publishes no ms figures, so treat that as a claim.
- **Budget estimate for your hardware (est.):** take DualSPHysics's ~23 GPIPS on a 2080 Ti and assume ~1.5× on a 3080 Ti. With ~50 neighbours per particle, 10^5 particles cost about 0.2–0.5 ms per hydro pass, and 10^6 cost about 2–5 ms. Add tree gravity and 2–4 substeps per frame, and 10^5 is comfortably real time while 10^6 is interactive only at reduced substeps.
- **Godot 4 note.** Use `RenderingDevice` (C# `RenderingServer.CreateLocalRenderingDevice()` or the main device), storage buffers and `ComputeListDispatch`. Avoid `BufferGetData` readbacks every frame because they stall. Keep positions on the GPU and render them with `MultiMesh`, or with a custom vertex-pulling shader bound to the same buffer.

**Pseudocode: one SPH step (GLSL compute, workgroup 128)**

```
// K1 hash:      cell[i] = morton(floor(pos[i]/h_cell)); idx[i]=i
// K2 sort:      counting sort (atomicAdd count[cell]) -> exclusive scan -> scatter
// K3 density:   for each i: rho=0; for 27 neighbor cells c: for j in [start[c],end[c]):
//                   rho += m[j]*W(|x_i-x_j|,h)
//               P[i] = tillotson(rho, u[i], mat[i])
// K4 forces:    a_hydro, du/dt via momentum/energy eq. + Balsara-switched Π_ij
// K5 gravity:   Barnes-Hut walk (stack in local array), softening ε ≈ h
// K6 integrate: KDK leapfrog: v += a*dt/2; x += v*dt; (recompute a); v += a*dt/2
```

### 1.4 Collision-outcome regimes: the formulas [SCI]

These come from Leinhardt & Stewart 2012 (LS12) as summarized in Stewart & Leinhardt 2012. Notation: M_p ≤ M_t, M_tot = M_p + M_t, γ = M_p/M_t, reduced mass μ = M_pM_t/M_tot, b = sin θ, ρ₁ = 1000 kg m⁻³, and R_C1 is the radius of M_tot at density ρ₁.

1. Specific impact energy: **Q_R = μV_i² / (2M_tot)**
2. Equal-mass critical velocity: **V*_{γ=1} = (32πc*/5)^{1/2} (ρ₁G)^{1/2} R_C1**
3. Principal disruption curve: **Q*_{RD,γ=1} = c* · (4/5)πρ₁G R_C1²**
   - c* = 1.9 ± 0.3 for fluid or strengthless planets; c* = 5 ± 2 for solid planetesimals.
   - μ̄ = 0.36 ± 0.01.
4. Mass-ratio correction: **Q*_RD = Q*_{RD,γ=1} · [¼(γ+1)²/γ]^{2/(3μ̄) − 1}**
5. Interacting mass fraction:
   - α = 1 if R_t > b(R_p+R_t) + R_p.
   - Otherwise, α = (3R_p l² − l³)/(4R_p³), where l = (R_p+R_t)(1−b) is the overlap depth. This is the geometric form of LS12's ρ(πR_p l² − πl³/3) normalized by the projectile mass.
   - **μ_α = αM_pM_t/(αM_p+M_t)**
6. Oblique correction: **Q'*_RD = (μ/μ_α)^{2 − 3μ̄/2} · Q*_RD**
   - Critical velocity: V'* = √(2Q'*_RD M_tot/μ)
7. Largest remnant ("universal law"):
   - For Q_R/Q'*_RD < 1.8: **M_lr/M_tot = −0.5(Q_R/Q'*_RD − 1) + 0.5**
   - Super-catastrophic regime: **M_lr/M_tot = (0.1/1.8^η)(Q_R/Q'*_RD)^η**, with η ≈ −1.5.
8. Grazing threshold: **b_crit = R_t/(R_t+R_p)**
9. Hit-and-run velocity (Kokubo & Genda 2010), with ζ = (M_t−M_p)/M_tot:
   **V_hr/V_esc = 2.43ζ²(1−b)^{5/2} − 0.0408ζ² + 1.86(1−b)^{5/2} + 1.08**

**Decision tree (C#, runs on CPU per collision event):**

```
Vesc' = sqrt(2G(αMp+Mt)/(Rp+Rt))
if Vi < Vesc'                    -> PERFECT MERGE
else if b > b_crit:
    if Vi < V_hr                 -> GRAZE-AND-MERGE (separate, re-collide next orbit)
    else if M_lr(Q) >= Mt        -> HIT-AND-RUN (reverse impact test for projectile disruption)
    else                         -> EROSION (grazing disruption)
else:
    if M_lr > Mt                 -> PARTIAL ACCRETION
    elif M_lr > 0.1 Mtot         -> PARTIAL EROSION
    else                         -> SUPER-CATASTROPHIC
core fraction: average of "cores always merge" and "cores merge only on accretion" (Marcus et al. 2010)
```

Sanity anchors from the paper for c* = 1.9:
- 1:2 mass ratio at b = 0: erosion begins at 2.0 V_esc and catastrophic disruption at 2.5 V_esc.
- 1:10: erosion at 2.5 V_esc, disruption at 5.9 V_esc.
- 1:40: disruption needs at least 18 V_esc.
- In N-body planet formation, giant impacts split "approximately evenly" between partial accretion, graze-and-merge and hit-and-run, and fragmentation produces "~15% of the final mass" as debris.

Caveats and upgrades:
- A 2024 A&A study ("Protoplanet collisions: new scaling laws from SPH simulations", doi:10.1051/0004-6361/202346519) of more than 20,000 SPH simulations found LS12 "overestimate the fragmentation efficiency in the merging regime" and handle the super-catastrophic regime poorly. It notes LS12 was built "from a catalog of ~180 N-body simulations of rubble-pile collisions" and proposes updated laws.
- Cambioni et al. (2019, arXiv:1903.04507) trained neural networks on SPH outputs for "on-the-fly" outcomes. **[GAME]:** a small MLP (evaluated in C#) is a legitimate drop-in replacement once you have training data.

---

## 2. Debris and Fragments

### 2.1 Fragment and ejecta distributions [SCI]

- **Largest remnant:** use LS12 eq. 7 above.
- **Remaining fragments:** LS12 give a power-law size distribution for the fragments below the largest remnant, N(>D) ∝ D^{−β}, with β ≈ 2.85 as their fit. Check the exact normalization in LS12 §5 before shipping.

Sampling procedure [GAME]:
- Draw fragments from the truncated power law until the escaping mass M_tot − M_lr is used up.
- Cap the number of discrete N-body fragments at, say, 64. Put the rest into the particle tier (§2.4).

**Ejecta speed from cratering (Housen & Holsapple 2011, Icarus 211, 856–875) [SCI]:**

- Launch speed versus launch radius x:

  v(x)/U = C₁ (x/a)^{−1/μ} · (1 − x/(n₂R))^p, for n₁a < x < n₂R

- Mass ejected inside x: M(<x) = k ρ (x³ − n₁³a³)
- Constants: n₁ ≈ 1.2, n₂ ≈ 1, p ≈ 0.3; μ ≈ 0.4 for porous or sand-like targets and ≈ 0.55 for non-porous rock.
- Together these imply a cumulative mass-above-speed law M(>v) ∝ (v/U)^{−3μ}. Inverse-transform sample it on the GPU.
- Ejection angles are about 45° (roughly 50° near the impact point to 40° near the rim in sand experiments). The result is an inverted-cone curtain.

### 2.2 Rubble-pile re-accumulation [SCI → GAME]

- LS12 used PKDGRAV with rubble piles, meaning "gravitational aggregates with no bulk tensile strength".
- Bodies had about 10^4 particles each, with coefficients of restitution ε_n = 0.5 and ε_t = 1.
- Merging was only turned on after a delay, to avoid "runaway merging" that overestimates the largest remnant. It was stable "if merging is turned on after 50 steps" (one step = one minute), though LS12 add: "we choose to be conservative and merge after 250 steps."
- **When it matters visually:** asteroid-scale (≤100 km) disruptions near Q*, contact binaries, and slow collapse into a new lumpy body.
- **When to skip it:** planet-scale impacts, where SPH or the analytic model is the right tool, and anything the camera sees for less than one free-fall time t_ff ≈ (Gρ)^{−1/2}. That is about 1 hour for ρ = 3000 kg m⁻³. At 1× time this is invisible anyway.
- **[GAME]:** run a GPU soft-sphere discrete element model (spring-dashpot contacts on the §2.3 grid, plus tree gravity) only for the hero rubble pile. Merge contacting grains into rigid clusters after N frames.

### 2.3 GPU spatial hashing / broad phase [SCI/engineering]

Green (NVIDIA CUDA SDK "particles") describes the method:
1. Hash each particle's cell.
2. Sort by hash with radix sort.
3. Run a `findCellStart` kernel in which "each thread compares the cell index of the current particle with the cell index of the previous particle… if different, this indicates the start of a new cell".
4. Alternatively, count per cell with atomics and use "a parallel prefix sum (scan)… to calculate the destination" of each particle.

**Complexity:**
- Hashing: O(N).
- Counting sort: O(N + C), where C is the number of cells. With a hash table of size about 2N, this is O(N).
- Scan: O(C) work in O(log C) depth.
- Neighbour queries: O(N·k), where k is the mean occupancy of the 27 surrounding cells.
- Compare O(N²) for all-pairs.
- Radix sort is O(N·bits/r) and helps memory coherence. Resort every 10–100 steps, since one MD study found binning "only necessary every 10 to 100 steps".

```
// Pass A: count     atomicAdd(cellCount[hash(cell(x_i))], 1)
// Pass B: scan      cellStart = exclusive_prefix_sum(cellCount)   // Blelloch, 2 dispatches
// Pass C: scatter   dst = cellStart[h] + atomicAdd(cellFill[h],1); sorted[dst]=i
// Pass D: query     for 27 neighbors: for j in cell: sweptSphereTest(i,j)
```

For large, spread-out scenes, hash cell coordinates as (p1·x ⊕ p2·y ⊕ p3·z) mod T. Use one grid level per size class, so that a single planet does not overflow a cell.

### 2.4 Fidelity hierarchy and conservative handoff [GAME]

| Tier | Contents | Physics |
|---|---|---|
| T0 "Majors" | ≤ ~2,000 planets, moons, stars, BHs | All-pairs gravity, analytic collisions, optional SPH |
| T1 "Fragments" | ≤ ~10^4 large debris | Gravity from T0 plus tree self-gravity; sphere collisions |
| T2 "Debris" | 10^5–10^6 | Test particles: feel T0 (and a coarse T1 multipole), no mutual forces; absorbed on contact |
| T3 "Dust/VFX" | Unlimited | No gravity or ballistic only; baked flipbooks, volumetric plumes |

This matches the planet-formation practice described by Stewart & Leinhardt: "planetesimals did not influence other planetesimals". It is also the direct GPU version of your 5.9 ms/step JavaScript debris tier.

**Handoff rules:**
- **Promotion/demotion must conserve Σm, Σmv and Σ(r×mv + L_spin).** When SPH particles re-accumulate into a clump found by friends-of-friends on the GPU, replace them with one T0/T1 body. Its mass is m = Σm_i, its velocity is the centre-of-mass velocity, and its spin is ω = L/I, taken from the particles' angular momentum about the centre of mass.
- **Dust:** when T2 particles become T3 dust, sum the removed mass and momentum into a "reservoir" per region. Re-inject it later as re-accreted mass onto the nearest T0 body. This keeps the mass ledger exact.
- **Energy:** for analytic outcomes, set fragment velocities so that total momentum is exact. Distribute the dispersal kinetic energy, (Q_R − binding) × mass, isotropically in the centre-of-mass frame. Record the dissipated remainder as heat that drives glow colour.

**All-pairs tiled gravity kernel (T0/T1):**

```
shared vec4 tile[256];                       // xyz = pos, w = mass
acc = 0
for (t = 0; t < N; t += 256) {
  tile[lid] = posMass[t + lid]; barrier();
  for (k = 0; k < 256; k++) { d = tile[k].xyz - p; r2 = dot(d,d) + eps2;
      acc += tile[k].w * d * inversesqrt(r2*r2*r2); }
  barrier();
}
```

At 10^4 bodies this is 10^8 interactions at about 20 flops each, which is sub-millisecond on a ~34 TFLOPS FP32 GPU (est.).

**Precision:** use camera-relative, double-precision positions on the CPU for T0, via Godot's `precision=double` build. For GPU tiers, use fp32 offsets from a moving local origin.

---

## 3. Black Holes

### 3.1 Lensing: tables vs. ray marching [SCI + engineering]

**DNGR (James, von Tunzelmann, Franklin & Thorne 2015, CQG 32, 065001, CC BY 3.0).**
- It traced ray bundles, not single rays, through Kerr spacetime (spin a = 0.999 in the film's reference renders) to avoid flicker at IMAX quality.
- Bruneton notes that an IMAX Interstellar frame "requires at least 30 minutes with 10 cores and the renderer has 40kLoC".
- The film also deliberately removed most Doppler colour and brightness asymmetry for audience readability. That is an **[ART]** precedent you can cite.

**Bruneton 2020 (arXiv:2010.08735; github.com/ebruneton/black_hole_shader, WebGL2, "released under the BSD license" per its README).**
- Units: r_s = 1, u = 1/r.
- Photon orbit equation: **d²u/dφ² = (3/2)u² − u**, with first integral (du/dφ)² = e² − u²(1−u).
- The photon sphere is at u = 2/3, and μ = 4/27. Rays with e² > μ fall into the hole.
- Periapsis: **u_a = 1/3 + (2/3)·sin((1/3)·arcsin(2e²/μ − 1))**
- Two small tables (512×512 for deflection and 64×32 for disk intersection) replace per-pixel integration.
- Stars sit in a manually filtered cubemap holding one star per texel, with colour summed in mips, so 3.6 million stars stay point-like.
- **Performance:** "Full HD at about 150 fps on an NVidia GeForce GTX 960". To match that with ray marching, "only 25 integration steps at most can be used, and stars end up at several degrees from their correct positions". With 1000 steps "the framerate drops to about 45 fps".

**SpaceEngine Kerr ray tracer (blog, July 2022).**
- It traces the general Kerr–Newman metric at reduced "warp resolution" into a deflection-vector render target, then upscales. The shadow edge is kept at full resolution.
- An RTX 2080 at 3840×2160 ran at 120 fps with warp resolution 0.35 and 29 fps at 1.0. The team reports "100+ fps on a GTX 1060".
- The August 2022 update added volumetric disks and jets integrated along curved geodesics in a low-resolution pass plus an upscaling pass (FidelityFX CAS among the options).

**Recommendation.**
- **[GAME]:** Schwarzschild via Bruneton tables as the default, at about 0.5 ms (est.) on a 3080 Ti at 1440p.
- Offer "Kerr mode" as SpaceEngine-style RK4 geodesics at 1/3 resolution. The 3080 Ti is about 1.5–2× an RTX 2080, so expect roughly 1.5–4 ms at 1440p (est.).
- **Multiple black holes:** superpose deflections as a weak-field approximation **[ART]**, since the exact problem has no closed-form metric.

**Kerr fragment kernel sketch:**

```
// per low-res pixel: init photon (x^μ, p_μ) in Boyer-Lindquist via camera tetrad
for step in 0..MAX:           // adaptive h ∝ (r - r_+)
   RK4 on Hamiltonian H = ½ g^{μν} p_μ p_ν       // Carter constant check for drift
   if r < r_+ * 1.01: color = 0; break
   if crosses disk plane in [r_isco, r_out]: accumulate disk emission * g^4; (opacity)
   if r > R_escape: dir = normalize(dx); break
store dir (warp vector) -> full-res pass samples skybox cubemap with custom star filter
```

### 3.2 Accretion disk appearance [SCI]

- **Temperature (thin disk, Shakura & Sunyaev 1973):**

  **T(r) = [3GMṀ / (8πσ r³) · (1 − √(r_in/r))]^{1/4}**

  Here r_in = r_ISCO = 6GM/c² for Schwarzschild, shrinking to about GM/c² for prograde maximal Kerr. T peaks just outside r_in. Novikov–Thorne adds relativistic correction factors.
- **Redshift factor for an emitter on a circular Keplerian orbit in Schwarzschild, seen by a distant observer:**

  **g = √(1 − 3M/r) / (1 − Ω b)**, with Ω = √(M/r³), where b is the photon's impact parameter projected on the orbit direction.

  - Observed temperature: T_obs = g·T.
  - Bolometric intensity: **I_obs = g⁴ I_em** (I_ν/ν³ is invariant).
  - The approaching side is brighter and bluer; the receding side is dimmer and redder. A static emitter has the gravitational factor √(1 − r_s/r).
- **Colour:** precompute a 1-D LUT from temperature to sRGB by integrating the Planck function B_λ(T) = 2hc²/λ⁵ · 1/(e^{hc/λkT} − 1) against the CIE 1931 colour matching functions. Index it by g·T. Tone-map with exposure, because 10^7 K disks are X-ray bright **[GAME: map temperature to visible palette]**.
- **[ART]:** add turbulent noise advected at Keplerian Ω(r), plus a thin volumetric layer. Bruneton explicitly uses "a basic, infinitely thin disc model", which is not a realistic flow.

### 3.3 Tidal disruption, spaghettification and the infalling observer [SCI]

- **Roche limit for a fluid satellite:** d ≈ 2.44 R_M (ρ_M/ρ_m)^{1/3}. For a rigid satellite, about 1.26 R_M (ρ_M/ρ_m)^{1/3}.
- **TDE tidal radius:** r_t ≈ R_*(M_BH/M_*)^{1/3}. Disruption is visible only if r_t > r_s = 2GM/c². For Sun-like stars that caps M_BH at roughly 10^8 M_⊙; one analysis quotes M_h,max = 5×10^7 M_⊙. Heavier black holes swallow stars whole, which is an important "fun fact" to show players.
- **Debris (Rees 1988, Nature 333, 523; exponent corrected by Phinney 1989):**
  - The star's orbital-energy spread is ΔE ≈ GM_BH R_*/r_t². About half is bound and half escapes.
  - Escaping debris leaves at v_ej ~ (M_BH/M_*)^{1/6} V_*, roughly 10× the stellar escape speed for a mass ratio of 10^6.
  - With flat dM/dE and Kepler's third law, the **fallback rate is Ṁ ∝ t^{−5/3}**.
  - The most-bound debris returns after about 0.03 M₆^{1/2} yr (≈ 11 days for 10^6 M_⊙).
  - Partial disruptions steepen the decay to t^{−9/4} (Coughlin & Nixon 2019; ApJ 2020 simulations).
- **[GAME] TDE pipeline:**
  1. When a star or planet crosses 1.5 r_t, spawn 10^4–10^5 SPH particles, or cheaper ballistic particles with the energy spread above.
  2. Integrate them as test particles in the black hole's potential. They naturally form the thin stream and "banana" shape.
  3. Feed returning mass into disk brightness following t^{−5/3}.
  4. **[ART]:** time-compress days into seconds.
- **Observer on an infalling planet [SCI]:**
  - Radial tidal stretch over a body of length L is Δa ≈ 2GML/r³. For a 2 m human, this becomes lethal (of order 10^3 g) at the horizon of a roughly 10-solar-mass hole, but is negligible at the horizon of a 10^8 M_⊙ hole.
  - A planet of radius R starts to disrupt at its Roche distance, which lies outside the horizon for stellar-mass holes. A game should show crustal cracking, then a stream.
  - Clock rate for a static observer: dτ/dt = √(1 − r_s/r). A free-faller crosses the horizon in finite proper time and sees nothing special locally.
  - The outside sky compresses into a shrinking, brightening, blueshifted patch (aberration plus lensing), with the black hole's shadow growing behind.
  - A distant observer sees the infaller redden, dim and freeze near the horizon.
  - Render the view with the same Bruneton/Kerr shaders, using an infalling camera 4-velocity. Bruneton's demo supports observer velocity, including the "near the speed of light" beaming mode.

---

## 4. Stars

### 4.1 Real-time star rendering [SCI → GAME]

- **Colour:** reuse the Planck→CIE LUT from §3.2, indexed by T_eff: about 3,000 K for M dwarfs (orange-red), 5,772 K for the Sun (near-white), and 30,000 K+ for O stars (blue-white). Apply exposure before tone mapping so the Sun stays white rather than yellow **[SCI]**.
- **Limb darkening:** I(μ)/I(1) = 1 − u₁(1−μ) − u₂(1−μ)², where μ = cos θ from disk centre. For the Sun in the visible, u₁ ≈ 0.6 is a reasonable linear value. Make u wavelength-dependent (stronger in blue) to get the correct reddened limb.
- **Granulation:** animated Worley/Voronoi cells with bright centres and dark lanes. Solar granules are about 1,000 km across and live for minutes. Add supergranulation at about 30,000 km, plus sunspots as a low-frequency noise threshold with umbra and penumbra **[GAME]**.
- **Flares and prominences:** billboarded or volumetric arcs along procedural magnetic loops (Bézier from dipole pairs), with a brief white-light brightening and an extreme-UV-style glow **[ART, time-compressed]**.
- **Bloom/glare:** a diffraction-spike PSF kernel scaled with apparent magnitude. Point stars use a Celestia-derived point shader, as SpaceEngine credits.

### 4.2 Stellar collisions and mergers [SCI vs. GAME]

- **Science:**
  - Most physical stellar collisions (in dense clusters and galactic nuclei) happen at low relative speed and produce mergers with modest mass loss. They are candidate blue-straggler formation channels.
  - High-velocity collisions near supermassive black holes strip envelopes.
  - Modelling standards include StarSmasher (github.com/jalombar/starsmasher; Gaburov, Lombardi & Portegies Zwart 2010, MNRAS 402, 105; ASCL 1805.010). It uses variational SPH, direct-summation GPU gravity via CUDA with a recently merged Apple Metal backend, and relaxation to hydrostatic equilibrium. Its code archive is licensed GPL v3 or later (confirm the LICENSE file in the repo).
  - Typical runs use 10^5–2.5×10^5 particles per star.
  - Very large or exotic stars (red supergiants, Wolf–Rayet stars, neutron stars) need realistic EOS, radiation and, for compact objects, general relativity. That is beyond real time.
- **[GAME] recommendation:**
  - Use the LS12-style decision (merge if V < V_esc, otherwise disrupt) with c* tuned for stars.
  - On merge, conserve mass minus a few-percent ejecta shell, conserve momentum and angular momentum, and set the new radius from a main-sequence mass–radius relation (R ∝ M^0.8).
  - Play a pre-baked StarSmasher-derived SPH flipbook or point-cache for the envelope splash.
  - Supergiant engulfing a planet: the planet simply vanishes into the envelope, with a surface flash **[ART]**.
- **[ART]:** instantaneous supernova on stellar mergers exceeding a mass threshold. That is not physical on these timescales, but it is fun; label it as such in-game.

---

## 5. Reference Material for VFX

**Licensing ground rules:**
- NASA images, audio, video and 3-D model data "generally are not subject to copyright in the United States" and may be used in "computer graphical simulations". However:
  - They must not imply endorsement.
  - "The NASA Insignia, Logotype, identifiers… are not in the public domain" (14 CFR 1221).
  - Identifiable people may require permission.
- ESA/Hubble and ESA/Webb media are CC BY 4.0, with clear credit (for example "ESA/Hubble").
- Other ESA content is often CC BY-SA 3.0 IGO. Check per item.

| Event | Where to source | What it shows (VFX ground truth) | License |
|---|---|---|---|
| Shoemaker–Levy 9 → Jupiter (July 1994) | NASA/STScI Hubble archive, ESA/Hubble | Plumes rising above the limb and collapsing within minutes; dark, long-lived (weeks) impact scars larger than Earth that shear into bands. Details not re-verified in this research pass. | NASA: generally public domain; ESA/Hubble: CC BY 4.0 |
| DART → Dimorphos (26 Sep 2022) | NASA/JHUAPL DART gallery; ASI LICIACube (LUKE/LEIA); Hubble; SOAR/NOIRLab | Ejecta cone with aperture 140 ± 4°; inner plume blue, turning redder outward; filaments, boulders; speeds from tens of m/s to ~500 m/s; fast ~2 km/s plume with Na/K/Li emission; later a comet-like tail driven by radiation pressure; system brightened 8.3×, back to baseline after 23.7 days | NASA/APL: generally usable with credit; check ASI terms for LICIACube |
| Deep Impact → Tempel 1 (4 Jul 2005) | NASA/JPL Photojournal | Bright initial flash, then a fan-shaped, sunlit ejecta plume that persists and expands over minutes (details not re-verified in this pass) | NASA: generally public domain |
| Chelyabinsk airburst (15 Feb 2013) | Dashcam/CCTV compilations (copyright belongs to individual filmers); Popova et al. 2013 SOM | ~20 m body at 19.16 km/s, 18.3° entry; peak brightness at 29.7 km; brighter than the Sun; 500 ± 100 kt; half-energy deposition between 33 and 27 km; persistent debris trail; shock arrival delayed by minutes at ~45 km range; 1,613 people sought medical attention | Scientific figures: journal copyright; videos: private, license individually |
| NASA Ames Vertical Gun Range | NASA Ames AVGR page; YouTube high-speed clips (e.g., ¼″ aluminium sphere into sand); NASA/JHUAPL Ceres-organics experiment images | Inverted-cone ejecta curtain that detaches from the crater and propagates outward; rays; early luminous flash vapour | NASA: generally public domain; check university-owned clips |
| Giant impact / Theia visualizations | Durham ICC / DiRAC (Kegerreis et al. SWIFT 10^8-particle videos); Lock & Stewart synestia renders; NASA Ames 2022 Moon-formation video | Spiral arm, satellite clump, disk; the synestia as a glowing, flattened torus | Check each: NASA generally public domain; university videos usually require permission |

**How to use them:**
- Match timescales: plume rise time, dust tail formation, fireball duration of about seconds.
- Match colours: incandescent white → orange; blue scattered dust close in → red further out.
- Match cone half-angles (about 45° curtains; DART's 140° full aperture).
- For Chelyabinsk-style airbursts, use Collins et al. (2005) pancake fragmentation. The observed energy-deposition curve (peak about 80 kt per km of altitude) is your calibration target.

---

## 6. How Existing Games Do It

**Universe Sandbox (Giant Army, Unity).**
- Collisions use point-mass bodies. Material melts and is ejected while the crater area heats up.
- Heavy impacts spawn fragment particles. A developer on the forum described "total fragmentation… a type of SPH lite. Here colliding planets turn into a cloud of fragments which moves in a fluid like way."
- The December 2019 SPH devlog showed prototype pressure-plus-gravity SPH producing mergers, pass-through impacts and Roche fragmentation. The team stressed that "each point comes at the cost of a good chunk of computing power… not so many that it becomes a slideshow".
- Later the team "shifted our focus on fixing problems and bugs with the fundamental systems… before resuming work on awesome new features like SPH". Update 26.3 brought "epic collisions with double the particles" and "as much as triple framerate increases".
- Update 34 overhauled the physics architecture onto Unity DOTS, and Update 34.1 revised the particle system "to preserve performance while simulating fuller, more realistic collisions".
- The 2026 roadmap lists rigid-body physics and stretched and flattened objects, but full SPH remains unreleased.
- **Lesson:** a shipping sandbox succeeded for a decade on analytic outcomes + particles + temperature + surface grids. Full SPH was never necessary for the product to be loved.

**Solar Smash (Paradyme Games; engine not officially confirmed — Unity per the sister app's release notes).**
- According to the fan wiki, planets are voxel volumes: "made entirely from voxels, which allows them to all be completely destructible". Exposed interiors glow and cool.
- Heavy damage triggers scripted "breakdown" stages, and planet-killer weapons delete all voxels at once with a cracking animation.
- Marketing for the PC sequel, *Solar Smash: Annihilation*, claims "1 Million Voxels Per Planet" and "150,000 particles" in rings. These are unverified marketing claims.
- **Realism:** low. Planet type "does not seem to change any actual reaction to the weapons" (fan wiki). There are no energy-based regimes or self-gravity re-accretion.
- **Lesson:** instant, legible and tactile destruction sells. Borrow the voxel/SDF crust damage for surface-scale weapons while your physics tiers handle the global response.

**SpaceEngine (C++/GLSL, proprietary).**
Techniques worth borrowing:
- Bruneton–Neyret precomputed atmospheric scattering.
- Quadtree cube-sphere tiled terrain LOD.
- Deterministic seeded procedural universes.
- A real-time Kerr–Newman geodesic tracer at 1/3 warp resolution with upscaling.
- Volumetric lensed accretion disks with light-travel time and Doppler.
- Energy-balance climate maps.

It openly documents its approximations, for example that skybox blueshift was "fake" in 2015 and that objects inside disks sort only in front of or behind them.

**Where the fun is vs. where realism gets expensive:**
- **Fun comes from** legible cause and effect: bigger or faster means messier; grazing means a hit-and-run "skip". The same comes from spectacle at human timescales, from surprising-but-true outcomes (Moon-forming disks, synestias, whole-swallowing black holes above ~10^8 M_⊙), and from the sense that the numbers are real.
- **Realism becomes counterproductive** in several cases:
  - Results that need 10^7+ particles to converge. Players cannot see convergence.
  - True timescales: re-accretion over months, synestias over centuries, TDE fallback over months.
  - Film-accurate Doppler asymmetry that makes disks unreadable. Interstellar itself toned this down.
  - Exact EOS thermodynamics that only changes colours.
- **Policy:** keep conserved quantities (mass, momentum, angular momentum, energy ledger) exact. Keep regime boundaries sourced (LS12). Fake only time and appearance, and label [ART] choices in a "Realism" tooltip.

---

## Recommendations (build order)

1. Port the T0/T1 gravity to a Godot `RenderingDevice` tiled compute kernel. Keep leapfrog, and add block timesteps for close pairs.
2. Implement the LS12 + Kokubo–Genda decision tree in C#, with core fraction tracking. Unit-test it against the paper's anchors (2.0/2.5, 2.5/5.9 and 18 V_esc).
3. Build the counting-sort grid (§2.3). Reuse it for broad phase, the rubble-pile discrete element model, friends-of-friends clumping and SPH neighbours.
4. Move debris to T2 GPU test particles. Sample ejecta from Housen–Holsapple and fragments from the LS12 power law, with a conservative mass ledger.
5. Add hero-collision SPH at 10^5 particles, with Tillotson EOS and Barnes–Hut gravity. Initialize with SEAGen-style shells. Bake 10^6-particle replays in the background.
6. Black holes: Bruneton tables first; Kerr low-resolution tracer second; TDE particles third.
7. Stars: Planck LUT, limb darkening, granulation; StarSmasher bakes for mergers.

## Caveats

- All 3080 Ti costs marked (est.) are extrapolations from benchmarks on other GPUs (Quadro FX 5800, GTX 960, RTX 2080/2080 Ti). Profile before committing budgets.
- LS12 is fit to specific simulation suites and is known to over-fragment in the merging regime (A&A 2024). The Kokubo–Genda coefficients were fit to fluid Earth-mass planets.
- SPH giant-impact results are not converged below about 10^7 particles (Kegerreis et al. 2019; Hosono et al.). The game's SPH is qualitative.
- Some items were not fully verified: SL9 and Deep Impact visual specifics, the LS12 fragment-slope normalization and the Solar Smash implementation (fan sources). Bruneton's shader (BSD per its README) and miluphcuda (GPLv3 per Schäfer et al. 2020) have stated licences, but confirm the repository LICENSE files before shipping.