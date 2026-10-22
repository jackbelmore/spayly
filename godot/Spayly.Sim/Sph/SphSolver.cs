namespace Spayly.Sim.Sph;

/// <summary>How self-gravity is computed.</summary>
public enum GravityMode { None, Direct, Tree }

/// <summary>Solver constants. SI; defaults are the values the research report and the literature use.</summary>
public sealed class SphOptions
{
    /// <summary>Monaghan (1992) artificial-viscosity α.</summary>
    public double Alpha { get; set; } = 1;
    /// <summary>Monaghan (1992) artificial-viscosity β.</summary>
    public double Beta { get; set; } = 2;
    /// <summary>Balsara (1995) switch on the viscosity (suppresses it in shear, e.g. a rotating disc).</summary>
    public bool Balsara { get; set; } = true;
    /// <summary>Courant number C in Δt = C h/(c + 1.2(αc + β max|μ|)) (Monaghan 1989).</summary>
    public double Courant { get; set; } = 0.3;
    /// <summary>Acceleration criterion Δt ≤ C_a √(h/|a|).</summary>
    public double AccelerationFactor { get; set; } = 0.3;
    /// <summary>Gravity criterion Δt ≤ C_g √(ε/|g|).</summary>
    public double GravityFactor { get; set; } = 0.3;
    /// <summary>h = η (m/ρ)^{1/3}; 1.2 gives ≈ 58 neighbours for the cubic spline.</summary>
    public double Eta { get; set; } = 1.2;
    /// <summary>Pressure floor, Pa. 0 = strengthless fluid, no tension (and so no tensile instability).</summary>
    public double PressureFloor { get; set; }
    public GravityMode Gravity { get; set; } = GravityMode.Tree;
    /// <summary>Gravitational constant, m³ kg⁻¹ s⁻² (<see cref="Constants.G"/> converted from km³).</summary>
    public double G { get; set; } = Constants.G * 1e9;
    /// <summary>Plummer softening ε, m; 0 = the median initial h.</summary>
    public double Softening { get; set; }
    /// <summary>Barnes–Hut opening angle.</summary>
    public double Theta { get; set; } = 0.6;
    public bool Quadrupole { get; set; } = true;
    /// <summary>Neighbour-grid cell, m; 0 = twice the median initial h. It also caps h (h ≤ cell), so the 5³ stencil always reaches 2h.</summary>
    public double CellSize { get; set; }
    /// <summary>h ≥ this × cell.</summary>
    public double HMinFraction { get; set; } = 0.1;
    /// <summary>s.</summary>
    public double DtMax { get; set; } = double.PositiveInfinity;
    /// <summary>Largest factor Δt may grow by from one step to the next.</summary>
    public double DtGrowth { get; set; } = 1.25;
    /// <summary>When &gt; 0 every step uses this Δt (s), whatever the criteria say: the GPU parity mode.</summary>
    public double FixedDt { get; set; }
}

/// <summary>
/// Reference SPH solver, double precision, single-threaded; the GPU port (Render/SphGpu.cs) follows it kernel for kernel.
///
/// Equations (Monaghan 1992, 2005), with W̄ᵢⱼ the symmetrised kernel ½[W(rᵢⱼ, hᵢ) + W(rᵢⱼ, hⱼ)]:
///   ρᵢ = Σⱼ mⱼ W(rᵢⱼ, hᵢ)                                                (gather, own h)
///   dvᵢ/dt = −Σⱼ mⱼ (Pᵢ/ρᵢ² + Pⱼ/ρⱼ² + Πᵢⱼ) ∇ᵢW̄ᵢⱼ + gᵢ
///   duᵢ/dt = Σⱼ mⱼ (Pᵢ/ρᵢ² + ½Πᵢⱼ) vᵢⱼ·∇ᵢW̄ᵢⱼ
///   Πᵢⱼ = f̄ᵢⱼ(−α c̄ᵢⱼ μᵢⱼ + β μᵢⱼ²)/ρ̄ᵢⱼ for vᵢⱼ·rᵢⱼ &lt; 0, μᵢⱼ = h̄ᵢⱼ vᵢⱼ·rᵢⱼ/(rᵢⱼ² + 0.01h̄ᵢⱼ²), else 0
///   fᵢ = |∇·v|ᵢ/(|∇·v|ᵢ + |∇×v|ᵢ + 10⁻⁴cᵢ/hᵢ)                            (Balsara 1995)
/// Every pair term is antisymmetric, so momentum is conserved to rounding and E = Σ½mv² + Σmu + W exactly in continuous
/// time; the energy equation uses the Pᵢ/ρᵢ² form (not the research report's ½(Pᵢ/ρᵢ² + Pⱼ/ρⱼ²)) because it keeps a cold
/// particle next to a hot one from being driven to negative u, and is equally conservative.
/// P ≥ <see cref="SphOptions.PressureFloor"/> after the Tillotson EOS. h is updated each step from the previous density,
/// h = η(m/ρ)^{1/3}, clamped to [HMinFraction·cell, cell].
///
/// Integration: kick-drift-kick leapfrog with the velocity and energy predicted to the end of the drift for the
/// viscosity, divergence and EOS (the usual SPH "drift" of v and u). One force evaluation per step.
/// Neighbours: <see cref="UniformGrid"/> with cell = 2 h_ref; a pair interacts when r &lt; 2 max(hᵢ, hⱼ), found from either
/// side because each bucket records its largest h.
/// </summary>
public sealed class SphSolver
{
    public SphState State { get; }
    public SphOptions Options { get; }
    /// <summary>Simulated time, s.</summary>
    public double Time { get; set; }
    /// <summary>Δt the next step will take, s.</summary>
    public double Dt { get; private set; }
    public int Steps { get; private set; }
    /// <summary>Relaxation: when &gt; 0, velocities decay as exp(−Δt/τ) after every step (s).</summary>
    public double DampingTime { get; set; }
    /// <summary>Relaxation: u is not integrated.</summary>
    public bool FreezeInternalEnergy { get; set; }
    /// <summary>Called after every step, after the closing kick.</summary>
    public Action<SphSolver>? AfterStep { get; set; }
    /// <summary>Neighbour-grid cell, m.</summary>
    public double CellSize => cell;
    /// <summary>Plummer softening in use, m.</summary>
    public double Softening => eps;
    /// <summary>Pair interactions evaluated by the last force pass.</summary>
    public long PairsLastPass { get; private set; }

    private readonly UniformGrid grid = new();
    private readonly Octree tree = new();
    private readonly double[] vpx, vpy, vpz, up, div, crx, cry, crz;
    private double cell, hMin, hMax, eps;
    private bool initialized;

    public SphSolver(SphState state, SphOptions options)
    {
        State = state;
        Options = options;
        var n = state.Count;
        vpx = new double[n]; vpy = new double[n]; vpz = new double[n]; up = new double[n];
        div = new double[n]; crx = new double[n]; cry = new double[n]; crz = new double[n];
    }

    /// <summary>Median of the current h, m.</summary>
    public static double MedianH(SphState s)
    {
        if (s.Count == 0) return 1;
        var a = new double[s.Count];
        Array.Copy(s.H, a, s.Count);
        Array.Sort(a);
        return a[s.Count / 2];
    }

    /// <summary>Fix the grid and softening from the current h, evaluate forces at t and choose the first Δt.</summary>
    public void Initialize()
    {
        var s = State;
        var href = MedianH(s);
        cell = Options.CellSize > 0 ? Options.CellSize : 2 * href;
        hMax = cell;
        hMin = Options.HMinFraction * cell;
        eps = Options.Softening > 0 ? Options.Softening : href;
        for (var i = 0; i < s.Count; i++)
        {
            s.H[i] = Math.Clamp(s.H[i], hMin, hMax);
            vpx[i] = s.Vx[i]; vpy[i] = s.Vy[i]; vpz[i] = s.Vz[i]; up[i] = s.U[i];
        }
        ComputeForces();
        Dt = NextDt(0);
        initialized = true;
    }

    /// <summary>One KDK step; returns the Δt it took, s.</summary>
    public double Step()
    {
        if (!initialized) Initialize();
        var s = State;
        var n = s.Count;
        var dt = Options.FixedDt > 0 ? Options.FixedDt : Dt;
        var hdt = 0.5 * dt;
        var frozen = FreezeInternalEnergy;
        for (var i = 0; i < n; i++)
        {
            s.Vx[i] += s.Ax[i] * hdt; s.Vy[i] += s.Ay[i] * hdt; s.Vz[i] += s.Az[i] * hdt;
            if (!frozen) s.U[i] = Math.Max(s.U[i] + s.DuDt[i] * hdt, 0);
            s.X[i] += s.Vx[i] * dt; s.Y[i] += s.Vy[i] * dt; s.Z[i] += s.Vz[i] * dt;
            vpx[i] = s.Vx[i] + s.Ax[i] * hdt; vpy[i] = s.Vy[i] + s.Ay[i] * hdt; vpz[i] = s.Vz[i] + s.Az[i] * hdt;
            up[i] = frozen ? s.U[i] : Math.Max(s.U[i] + s.DuDt[i] * hdt, 0);
            s.H[i] = Math.Clamp(Options.Eta * Math.Cbrt(s.M[i] / s.Rho[i]), hMin, hMax);
        }
        ComputeForces();
        for (var i = 0; i < n; i++)
        {
            s.Vx[i] += s.Ax[i] * hdt; s.Vy[i] += s.Ay[i] * hdt; s.Vz[i] += s.Az[i] * hdt;
            if (!frozen) s.U[i] = Math.Max(s.U[i] + s.DuDt[i] * hdt, 0);
        }
        if (DampingTime > 0)
        {
            var f = Math.Exp(-dt / DampingTime);
            for (var i = 0; i < n; i++) { s.Vx[i] *= f; s.Vy[i] *= f; s.Vz[i] *= f; }
        }
        Time += dt;
        Steps++;
        AfterStep?.Invoke(this);
        Dt = NextDt(dt);
        return dt;
    }

    /// <summary>Step until at least <paramref name="dtBudget"/> seconds have passed or <paramref name="maxSteps"/> steps are taken; returns the time advanced, s.</summary>
    public double Advance(double dtBudget, int maxSteps = int.MaxValue)
    {
        double t = 0;
        for (var k = 0; k < maxSteps && t < dtBudget; k++) t += Step();
        return t;
    }

    /// <summary>Density, EOS, hydro forces and gravity at the current positions, with the predicted v and u.</summary>
    public void ComputeForces()
    {
        var s = State;
        var n = s.Count;
        grid.Build(n, s.X, s.Y, s.Z, s.H, cell);
        var hNow = 0.0;
        for (var i = 0; i < n; i++) hNow = Math.Max(hNow, s.H[i]);
        var k = 2 * hNow <= cell ? 1 : 2;
        Density(k);
        Eos();
        Forces(k);
        Gravity();
    }

    private void Density(int k)
    {
        var s = State;
        var n = s.Count;
        double[] x = s.X, y = s.Y, z = s.Z, m = s.M, h = s.H, rho = s.Rho;
        for (var i = 0; i < n; i++)
        {
            rho[i] = m[i] * Kernel.W(0, h[i]);
            div[i] = 0; crx[i] = 0; cry[i] = 0; crz[i] = 0;
        }
        var g = grid;
        for (var i = 0; i < n; i++)
        {
            double xi = x[i], yi = y[i], zi = z[i], hi = h[i], mi = m[i];
            double vxi = vpx[i], vyi = vpy[i], vzi = vpz[i];
            int ci = g.Cx[i], cj = g.Cy[i], ck = g.Cz[i];
            for (var dz = -k; dz <= k; dz++)
            for (var dy = -k; dy <= k; dy++)
            for (var dx = -k; dx <= k; dx++)
            {
                int a = ci + dx, b = cj + dy, c = ck + dz;
                var box = g.BoxDistance2(xi, yi, zi, a, b, c);
                var key = UniformGrid.Key(a, b, c, g.Mask);
                var reach = 2 * Math.Max(hi, g.BucketHmax[key]);
                if (box >= reach * reach) continue;
                var s0 = g.Start[key];
                var s1 = s0 + g.Count[key];
                for (var q = s0; q < s1; q++)
                {
                    var j = g.Sorted[q];
                    if (j <= i || g.Cx[j] != a || g.Cy[j] != b || g.Cz[j] != c) continue;
                    double rx = xi - x[j], ry = yi - y[j], rz = zi - z[j];
                    var r2 = rx * rx + ry * ry + rz * rz;
                    var hj = h[j];
                    var hh = hi > hj ? hi : hj;
                    if (r2 >= 4 * hh * hh) continue;
                    var r = Math.Sqrt(r2);
                    double wx = vxi - vpx[j], wy = vyi - vpy[j], wz = vzi - vpz[j];
                    var vr = wx * rx + wy * ry + wz * rz;
                    double cx = wy * rz - wz * ry, cy = wz * rx - wx * rz, cz = wx * ry - wy * rx;
                    if (r < 2 * hi)
                    {
                        rho[i] += m[j] * Kernel.W(r, hi);
                        if (r > 0)
                        {
                            var gw = m[j] * Kernel.DwDr(r, hi) / r;
                            div[i] += gw * vr; crx[i] += gw * cx; cry[i] += gw * cy; crz[i] += gw * cz;
                        }
                    }
                    if (r < 2 * hj)
                    {
                        rho[j] += mi * Kernel.W(r, hj);
                        if (r > 0)
                        {
                            var gw = mi * Kernel.DwDr(r, hj) / r;
                            div[j] += gw * vr; crx[j] += gw * cx; cry[j] += gw * cy; crz[j] += gw * cz;
                        }
                    }
                }
            }
        }
    }

    private void Eos()
    {
        var s = State;
        var pMin = Options.PressureFloor;
        for (var i = 0; i < s.Count; i++)
        {
            var (p, c) = Tillotson.PressureAndSoundSpeed(s.Materials[s.Mat[i]], s.Rho[i], up[i], pMin);
            s.P[i] = p;
            s.Cs[i] = c;
            if (!Options.Balsara)
            {
                s.Balsara[i] = 1;
                continue;
            }
            // ∇·v = −(1/ρ)Σ mⱼ vᵢⱼ·∇ᵢW, ∇×v = (1/ρ)Σ mⱼ vᵢⱼ × ∇ᵢW; only magnitudes enter the switch.
            var dv = Math.Abs(div[i]) / s.Rho[i];
            var cv = Math.Sqrt(crx[i] * crx[i] + cry[i] * cry[i] + crz[i] * crz[i]) / s.Rho[i];
            var denom = dv + cv + 1e-4 * c / s.H[i];
            s.Balsara[i] = denom > 0 ? dv / denom : 1;
        }
    }

    private void Forces(int k)
    {
        var s = State;
        var n = s.Count;
        double[] x = s.X, y = s.Y, z = s.Z, m = s.M, h = s.H, rho = s.Rho, p = s.P, cs = s.Cs, fb = s.Balsara;
        double[] ax = s.Ax, ay = s.Ay, az = s.Az, du = s.DuDt, mumax = s.MuMax;
        double alpha = Options.Alpha, beta = Options.Beta;
        for (var i = 0; i < n; i++) { ax[i] = 0; ay[i] = 0; az[i] = 0; du[i] = 0; mumax[i] = 0; }
        long pairs = 0;
        var g = grid;
        for (var i = 0; i < n; i++)
        {
            double xi = x[i], yi = y[i], zi = z[i], hi = h[i], mi = m[i];
            double vxi = vpx[i], vyi = vpy[i], vzi = vpz[i];
            var qi = p[i] / (rho[i] * rho[i]);
            int ci = g.Cx[i], cj = g.Cy[i], ck = g.Cz[i];
            for (var dz = -k; dz <= k; dz++)
            for (var dy = -k; dy <= k; dy++)
            for (var dx = -k; dx <= k; dx++)
            {
                int a = ci + dx, b = cj + dy, c = ck + dz;
                var box = g.BoxDistance2(xi, yi, zi, a, b, c);
                var key = UniformGrid.Key(a, b, c, g.Mask);
                var reach = 2 * Math.Max(hi, g.BucketHmax[key]);
                if (box >= reach * reach) continue;
                var s0 = g.Start[key];
                var s1 = s0 + g.Count[key];
                for (var q = s0; q < s1; q++)
                {
                    var j = g.Sorted[q];
                    if (j <= i || g.Cx[j] != a || g.Cy[j] != b || g.Cz[j] != c) continue;
                    double rx = xi - x[j], ry = yi - y[j], rz = zi - z[j];
                    var r2 = rx * rx + ry * ry + rz * rz;
                    var hj = h[j];
                    var hh = hi > hj ? hi : hj;
                    if (r2 >= 4 * hh * hh || r2 <= 0) continue;
                    pairs++;
                    var r = Math.Sqrt(r2);
                    var gbar = 0.5 * (Kernel.DwDr(r, hi) + Kernel.DwDr(r, hj)) / r;
                    double wx = vxi - vpx[j], wy = vyi - vpy[j], wz = vzi - vpz[j];
                    var vr = wx * rx + wy * ry + wz * rz;
                    var qj = p[j] / (rho[j] * rho[j]);
                    double visc = 0;
                    if (vr < 0)
                    {
                        var hbar = 0.5 * (hi + hj);
                        var mu = hbar * vr / (r2 + 0.01 * hbar * hbar);
                        var cbar = 0.5 * (cs[i] + cs[j]);
                        var rhobar = 0.5 * (rho[i] + rho[j]);
                        var f = 0.5 * (fb[i] + fb[j]);
                        visc = f * (-alpha * cbar * mu + beta * mu * mu) / rhobar;
                        var amu = -mu;
                        if (amu > mumax[i]) mumax[i] = amu;
                        if (amu > mumax[j]) mumax[j] = amu;
                    }
                    var sij = (qi + qj + visc) * gbar;
                    ax[i] -= m[j] * sij * rx; ay[i] -= m[j] * sij * ry; az[i] -= m[j] * sij * rz;
                    ax[j] += mi * sij * rx; ay[j] += mi * sij * ry; az[j] += mi * sij * rz;
                    var vg = vr * gbar;
                    du[i] += m[j] * (qi + 0.5 * visc) * vg;
                    du[j] += mi * (qj + 0.5 * visc) * vg;
                }
            }
        }
        PairsLastPass = pairs;
    }

    private void Gravity()
    {
        var s = State;
        var n = s.Count;
        double[] gx = s.Gx, gy = s.Gy, gz = s.Gz;
        for (var i = 0; i < n; i++) { gx[i] = 0; gy[i] = 0; gz[i] = 0; }
        var G = Options.G;
        var e2 = eps * eps;
        switch (Options.Gravity)
        {
            case GravityMode.Direct:
            {
                double[] x = s.X, y = s.Y, z = s.Z, m = s.M;
                for (var i = 0; i < n; i++)
                {
                    double xi = x[i], yi = y[i], zi = z[i], mi = m[i];
                    double axi = 0, ayi = 0, azi = 0;
                    for (var j = i + 1; j < n; j++)
                    {
                        double dx = x[j] - xi, dy = y[j] - yi, dz = z[j] - zi;
                        var r2 = dx * dx + dy * dy + dz * dz + e2;
                        var inv3 = G / (r2 * Math.Sqrt(r2));
                        var fi = m[j] * inv3;
                        var fj = mi * inv3;
                        axi += dx * fi; ayi += dy * fi; azi += dz * fi;
                        gx[j] -= dx * fj; gy[j] -= dy * fj; gz[j] -= dz * fj;
                    }
                    gx[i] += axi; gy[i] += ayi; gz[i] += azi;
                }
                break;
            }
            case GravityMode.Tree:
            {
                tree.Build(s.X, s.Y, s.Z, s.M, n);
                for (var i = 0; i < n; i++)
                {
                    var a = tree.Accel(s.X[i], s.Y[i], s.Z[i], G, e2, Options.Theta, Options.Quadrupole);
                    gx[i] = a.X; gy[i] = a.Y; gz[i] = a.Z;
                }
                break;
            }
        }
        for (var i = 0; i < n; i++) { s.Ax[i] += gx[i]; s.Ay[i] += gy[i]; s.Az[i] += gz[i]; }
    }

    private double NextDt(double previous)
    {
        var s = State;
        var o = Options;
        var dt = double.PositiveInfinity;
        for (var i = 0; i < s.Count; i++)
        {
            double h = s.H[i], c = s.Cs[i];
            var dc = o.Courant * h / (c + 1.2 * (o.Alpha * c + o.Beta * s.MuMax[i]));
            if (dc < dt) dt = dc;
            var a = Math.Sqrt(s.Ax[i] * s.Ax[i] + s.Ay[i] * s.Ay[i] + s.Az[i] * s.Az[i]);
            if (a > 0)
            {
                var da = o.AccelerationFactor * Math.Sqrt(h / a);
                if (da < dt) dt = da;
            }
            var g = Math.Sqrt(s.Gx[i] * s.Gx[i] + s.Gy[i] * s.Gy[i] + s.Gz[i] * s.Gz[i]);
            if (g > 0 && eps > 0)
            {
                var dg = o.GravityFactor * Math.Sqrt(eps / g);
                if (dg < dt) dt = dg;
            }
        }
        dt = Math.Min(dt, o.DtMax);
        if (previous > 0) dt = Math.Min(dt, o.DtGrowth * previous);
        return dt;
    }

    /// <summary>Total energy Σ½mv² + Σmu + W (J), W by direct summation with this solver's softening; O(N²), diagnostics only.</summary>
    public double TotalEnergy() =>
        State.KineticEnergy() + State.InternalEnergy() + (Options.Gravity == GravityMode.None ? 0 : State.PotentialEnergy(Options.G, eps));
}
