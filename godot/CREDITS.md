# Credits — godot/

Third-party methods the Godot port uses. Everything here was written from published equations and papers; GPL codes
were read as ideas at most and no code was copied from them. Textures and their sources are credited in `public/textures/CREDITS.md`.

## Black-hole lensing

- **Method:** Eric Bruneton, *Real-time High-Quality Rendering of Non-Rotating Black Holes*, 2020,
  [arXiv:2010.08735](https://arxiv.org/abs/2010.08735). The idea is his: precompute the Schwarzschild photon orbits
  once into small 2D tables indexed by the inverse impact parameter, then trace each pixel's ray, its deflection and
  its crossings of the accretion-disc plane with a few table lookups instead of integrating it.
- **Licence of the reference code:** his implementation, `github.com/ebruneton/black_hole_shader`, is released under the
  BSD license (per its README; variant not checked here). No file of it is copied here. `Spayly.Sim/BlackHole.cs` (the table
  generator) and `Spayly.Game/Render/LensingShader.cs` (the kernel) were written from the equations: the orbit equation
  d²u/dφ² = 3u²/2 − u with r_s = 1, its first integral (du/dφ)² = e² − u²(1 − u), the capture criterion e² > 4/27, and
  the trigonometric periapsis root. The table layout, parameterisation and float32 handling are this project's own, so
  they differ from his; if a later change does port code from that repository, its BSD notice must be reproduced here.
- **Other results used:** Shakura & Sunyaev (1973) thin-disc temperature; the circular-emitter redshift
  g = √(1 − 3M/r)/(1 − Ωb_z); Bozza (2002, Phys. Rev. D 66, 103001) strong-field deflection limit and the weak-field
  series 4M/b + 15πM²/4b² + 128M³/3b³, both as test references only.

## Hero-collision SPH (`Spayly.Sim/Sph/`, `Spayly.Game/Render/SphGpu.cs`, `Spayly.Game/Shaders/sph_*`)

Equations and methods:
- **SPH core**: Monaghan, J. J. (1992), *Smoothed particle hydrodynamics*, ARA&A 30:543; Monaghan (2005), Rep. Prog.
  Phys. 68:1703. Density sum, symmetric momentum equation, the Pᵢ/ρᵢ² energy equation, artificial viscosity Πᵢⱼ
  (α = 1, β = 2, the 0.01h² term) and the Courant condition Δt = C h/(c + 1.2(αc + β max|μ|)) (Monaghan 1989,
  J. Comput. Phys. 82:1).
- **Kernel**: M4 cubic spline, Monaghan & Lattanzio (1985), A&A 149:135.
- **Viscosity switch**: Balsara, D. S. (1995), J. Comput. Phys. 121:357.
- **Equation of state**: Tillotson, J. H. (1962), *Metallic equations of state for hypervelocity impact*, General
  Atomic GA-3216; as written in Melosh, H. J. (1989), *Impact Cratering: A Geologic Process*, OUP, appendix II.
- **Material parameters**: granite and iron from Melosh (1989) table AII.3 (iron ρ₀ = 7800 kg/m³ as used in most
  giant-impact work; Melosh lists 7860); basalt and water ice from Benz, W. & Asphaug, E. (1999), *Catastrophic
  disruptions revisited*, Icarus 142:5, table II; aluminium from Tillotson (1962), the set Schäfer et al. (2016,
  A&A 590:A19) test miluphcuda with. Display heat capacities, melting points and latent heats are [GAME] handbook-order
  values, not physics.
- **Self-gravity**: Barnes, J. & Hut, P. (1986), Nature 324:446; the δ-guarded opening test of Barnes, J. (1994),
  *Computational Astrophysics* (Springer), and the quadrupole field of the standard multipole expansion; Plummer
  softening. The GPU tree is a complete (dense) octree over Morton-ordered leaves, after the six-kernel structure of
  Burtscher, M. & Pingali, K. (2011), *GPU Computing Gems Emerald Edition*, ch. 6, simplified to a fixed-depth pyramid.
- **Neighbour grid**: counting sort by cell hash (Green, S., NVIDIA CUDA SDK "particles", 2008); spatial-hash primes of
  Teschner, M. et al. (2003), VMV; the 4×4×4 block key measured in `spikes/gpu-debris/RESULT.md`; Blelloch, G. (1990)
  prefix sum.
- **Planet initial conditions**: equal-mass particles on shells matched to a density profile, after the idea of
  Kegerreis, J. A. et al. (2019), MNRAS 487:5029 (SEAGen). SEAGen is GPL: its idea was used, not its code. The sphere
  partition is Leopardi, P. (2006), *A partition of the unit sphere into regions of equal area and small diameter*,
  ETNA 25:309. Hydrostatic profiles by shooting on the surface radius, as in WoMa (Ruiz-Bonilla, S. et al. 2021,
  MNRAS 500:2861), written from the equations dm/dr = 4πr²ρ, dP/dr = −Gmρ/r². Uniform random rotations: Shoemake, K.
  (1992), *Graphics Gems III*.
- **Clumps**: friends-of-friends, Davis, M. et al. (1985), ApJ 292:371. The conservative demotion (Σm, Σmv,
  Σ(r × mv + L_spin) closed by an exact-difference remainder) follows `docs/research/physics-and-visuals.md` §2.4.
- **Trigger and demo geometry**: Leinhardt, Z. M. & Stewart, S. T. (2012), ApJ 745:79 (Q_R, Q*_RD via
  `CollisionRegimes`); canonical Moon-forming impact from Canup, R. M. (2004), Icarus 168:433 (Mars-mass impactor,
  45°, ≈ v_esc); impact parameter convention b = sin θ from Canup & Asphaug (2001), Nature 412:708.
- **Morton bit tricks**: the public-domain "part 1 by 2" interleave (Giesen, F., "Decoding Morton codes", 2009).

Codes read for ideas only (GPL, nothing copied): miluphcuda (Schäfer et al. 2016, 2020), pkdgrav3 SPH (Meier et al.),
SWIFT (Schaller et al. 2016/2023; LGPL), StarSmasher.
