namespace Spayly.Sim.Sph;

/// <summary>One layer of a differentiated body, innermost first; <paramref name="MassFraction"/> of the total mass.</summary>
public readonly record struct Layer(Material Material, double MassFraction);

/// <summary>
/// A Tillotson isentrope u(ρ) from (ρ₀, u₀), integrated as du/dρ = P(ρ, u)/ρ² (the adiabat de = −P dV), tabulated in
/// ln ρ from ρ₀ to <c>RhoMaxFactor</c>·ρ₀ with RK4. Along it P rises with ρ (dP/dρ|ₛ = c² &gt; 0), so a pressure maps back
/// to one density. SI.
/// </summary>
public sealed class Isentrope
{
    public Material Material { get; }
    /// <summary>u at ρ₀, J/kg.</summary>
    public double SurfaceEnergy { get; }
    private readonly double[] rho, u, p;

    public const double RhoMaxFactor = 20;

    public Isentrope(Material m, double u0, int steps = 4000)
    {
        Material = m;
        SurfaceEnergy = u0;
        rho = new double[steps + 1]; u = new double[steps + 1]; p = new double[steps + 1];
        var ds = Math.Log(RhoMaxFactor) / steps;
        double s = Math.Log(m.Rho0), e = u0;
        // du/ds = ρ du/dρ = P/ρ with s = ln ρ.
        double F(double ss, double ee)
        {
            var r = Math.Exp(ss);
            return Tillotson.Pressure(m, r, ee) / r;
        }
        for (var k = 0; k <= steps; k++)
        {
            rho[k] = Math.Exp(s);
            u[k] = e;
            p[k] = Math.Max(Tillotson.Pressure(m, rho[k], e), 0);
            if (k == steps) break;
            var k1 = F(s, e);
            var k2 = F(s + 0.5 * ds, e + 0.5 * ds * k1);
            var k3 = F(s + 0.5 * ds, e + 0.5 * ds * k2);
            var k4 = F(s + ds, e + ds * k3);
            e += ds * (k1 + 2 * k2 + 2 * k3 + k4) / 6;
            s += ds;
        }
    }

    /// <summary>Density (kg/m³) and energy (J/kg) at pressure <paramref name="pressure"/> (Pa); ρ₀ at or below the table's first pressure.</summary>
    public (double Rho, double U) AtPressure(double pressure)
    {
        if (pressure <= p[0]) return (rho[0], u[0]);
        var last = p.Length - 1;
        if (pressure >= p[last]) return (rho[last], u[last]);
        int lo = 0, hi = last;
        while (hi - lo > 1)
        {
            var mid = (lo + hi) >> 1;
            if (p[mid] <= pressure) lo = mid; else hi = mid;
        }
        var t = (pressure - p[lo]) / (p[hi] - p[lo]);
        return (rho[lo] + t * (rho[hi] - rho[lo]), u[lo] + t * (u[hi] - u[lo]));
    }

    /// <summary>Energy (J/kg) on the isentrope at density <paramref name="density"/> (kg/m³); u₀ below ρ₀.</summary>
    public double EnergyAt(double density)
    {
        if (density <= rho[0]) return u[0];
        var last = rho.Length - 1;
        if (density >= rho[last]) return u[last];
        var k = Math.Log(density / rho[0]) / Math.Log(RhoMaxFactor) * last;
        var i = Math.Min((int)k, last - 1);
        var t = k - i;
        return u[i] + t * (u[i + 1] - u[i]);
    }
}

/// <summary>
/// Hydrostatic, isentropic radial structure of a layered body: dm/dr = 4πr²ρ, dP/dr = −Gmρ/r², ρ from each layer's
/// isentrope, P = 0 at the surface. The surface radius is found by shooting: integrate inward from a trial R and bisect
/// until the mass runs out exactly at the centre (WoMa-style, Ruiz-Bonilla et al. 2021). Layers are given by mass
/// fraction because a Tillotson body's radii follow from its mass; they are outputs here. SI; ascending radius.
/// </summary>
public sealed class PlanetProfile
{
    public double Mass { get; }
    public double Radius { get; }
    public IReadOnlyList<Layer> Layers { get; }
    public Isentrope[] Isentropes { get; }
    /// <summary>Outer radius of each layer, m (the last is <see cref="Radius"/>).</summary>
    public double[] LayerRadius { get; }
    public double[] R { get; }
    public double[] M { get; }
    public double[] Rho { get; }
    public double[] P { get; }
    public double[] U { get; }
    public int[] LayerOf { get; }

    private PlanetProfile(double mass, double radius, IReadOnlyList<Layer> layers, Isentrope[] isen, double[] r, double[] m, double[] rho, double[] p, double[] u, int[] layer)
    {
        Mass = mass; Radius = radius; Layers = layers; Isentropes = isen;
        R = r; M = m; Rho = rho; P = p; U = u; LayerOf = layer;
        LayerRadius = new double[layers.Count];
        double cum = 0;
        for (var k = 0; k < layers.Count; k++)
        {
            cum += layers[k].MassFraction;
            LayerRadius[k] = k == layers.Count - 1 ? radius : RadiusAtMass(cum * mass);
        }
    }

    /// <summary>
    /// Solve for a body of <paramref name="mass"/> kg made of <paramref name="layers"/> (innermost first, fractions summing
    /// to 1), every layer on the isentrope through (ρ₀, <paramref name="surfaceEnergy"/> J/kg).
    /// </summary>
    public static PlanetProfile Solve(double mass, IReadOnlyList<Layer> layers, double surfaceEnergy = 0, int steps = 4000, double g = Constants.G * 1e9)
    {
        if (layers.Count == 0) throw new ArgumentException("at least one layer", nameof(layers));
        var isen = new Isentrope[layers.Count];
        double fsum = 0, rhoMin = double.PositiveInfinity, rhoMax = 0;
        for (var k = 0; k < layers.Count; k++)
        {
            isen[k] = new Isentrope(layers[k].Material, surfaceEnergy);
            fsum += layers[k].MassFraction;
            rhoMin = Math.Min(rhoMin, layers[k].Material.Rho0);
            rhoMax = Math.Max(rhoMax, layers[k].Material.Rho0);
        }
        if (Math.Abs(fsum - 1) > 1e-9) throw new ArgumentException($"mass fractions sum to {fsum}, not 1", nameof(layers));
        var inner = new double[layers.Count];
        double acc = 0;
        for (var k = 0; k < layers.Count; k++)
        {
            inner[k] = acc * mass;
            acc += layers[k].MassFraction;
        }
        // Too small: even Isentrope.RhoMaxFactor compression leaves mass over. Too big: uncompressed lightest material runs out early.
        var lo = Math.Cbrt(3 * mass / (4 * Math.PI * rhoMax * Isentrope.RhoMaxFactor));
        var hi = 1.05 * Math.Cbrt(3 * mass / (4 * Math.PI * rhoMin));
        for (var it = 0; it < 200 && (hi - lo) > 1e-11 * hi; it++)
        {
            var mid = 0.5 * (lo + hi);
            if (Shoot(mid, mass, inner, isen, steps, g, null) > 0) lo = mid; else hi = mid;
        }
        var radius = 0.5 * (lo + hi);
        var store = new List<(double R, double M, double P, double Rho, double U, int Layer)>(steps + 1);
        Shoot(radius, mass, inner, isen, steps, g, store);
        store.Reverse();
        var n = store.Count;
        // The centre point closes the table: m = 0 at r = 0 with the innermost stored density.
        var r = new double[n + 1]; var m = new double[n + 1]; var rho = new double[n + 1]; var p = new double[n + 1]; var u = new double[n + 1]; var lay = new int[n + 1];
        r[0] = 0; m[0] = 0; rho[0] = store[0].Rho; p[0] = store[0].P; u[0] = store[0].U; lay[0] = store[0].Layer;
        for (var k = 0; k < n; k++)
        {
            r[k + 1] = store[k].R; m[k + 1] = Math.Max(store[k].M, 0); rho[k + 1] = store[k].Rho;
            p[k + 1] = store[k].P; u[k + 1] = store[k].U; lay[k + 1] = store[k].Layer;
        }
        // Make the mass column monotonic and exact at the surface despite the shooting residual.
        for (var k = 1; k <= n; k++) if (m[k] < m[k - 1]) m[k] = m[k - 1];
        m[n] = mass;
        return new PlanetProfile(mass, radius, layers, isen, r, m, rho, p, u, lay);
    }

    /// <summary>
    /// Integrate inward from r = <paramref name="radius"/>; returns the mass left at the last step minus what a uniform
    /// ball of the central density would hold there (&gt; 0: radius too small), or a negative value if the mass ran out
    /// before the centre (radius too large).
    /// </summary>
    private static double Shoot(double radius, double mass, double[] inner, Isentrope[] isen, int steps, double g,
        List<(double R, double M, double P, double Rho, double U, int Layer)>? store)
    {
        var dr = radius / steps;
        double r = radius, m = mass, p = 0;
        var layer = isen.Length - 1;
        (double dm, double dp) F(double rr, double mm, double pp, int l)
        {
            var rho = isen[l].AtPressure(pp).Rho;
            return (4 * Math.PI * rr * rr * rho, -g * mm * rho / (rr * rr));
        }
        for (var k = 0; k < steps - 1; k++)
        {
            while (layer > 0 && m <= inner[layer]) layer--;
            if (store != null)
            {
                var (rh, uu) = isen[layer].AtPressure(p);
                store.Add((r, m, p, rh, uu, layer));
            }
            var h = -dr;
            var (a1, b1) = F(r, m, p, layer);
            var (a2, b2) = F(r + 0.5 * h, m + 0.5 * h * a1, p + 0.5 * h * b1, layer);
            var (a3, b3) = F(r + 0.5 * h, m + 0.5 * h * a2, p + 0.5 * h * b2, layer);
            var (a4, b4) = F(r + h, m + h * a3, p + h * b3, layer);
            m += h * (a1 + 2 * a2 + 2 * a3 + a4) / 6;
            p += h * (b1 + 2 * b2 + 2 * b3 + b4) / 6;
            r += h;
            if (m <= 0)
            {
                if (store != null) store.Add((r, 0, p, isen[layer].AtPressure(p).Rho, isen[layer].AtPressure(p).U, layer));
                return -(r / radius) - 1e-300;
            }
        }
        while (layer > 0 && m <= inner[layer]) layer--;
        var (rc, uc) = isen[layer].AtPressure(p);
        if (store != null) store.Add((r, m, p, rc, uc, layer));
        return m - 4.0 / 3 * Math.PI * rc * r * r * r;
    }

    private static double Interp(double[] xs, double[] ys, double x)
    {
        var last = xs.Length - 1;
        if (x <= xs[0]) return ys[0];
        if (x >= xs[last]) return ys[last];
        int lo = 0, hi = last;
        while (hi - lo > 1)
        {
            var mid = (lo + hi) >> 1;
            if (xs[mid] <= x) lo = mid; else hi = mid;
        }
        var span = xs[hi] - xs[lo];
        var t = span > 0 ? (x - xs[lo]) / span : 0;
        return ys[lo] + t * (ys[hi] - ys[lo]);
    }

    /// <summary>Enclosed mass at radius r (m), kg.</summary>
    public double MassAt(double r) => Interp(R, M, r);
    /// <summary>Radius (m) enclosing mass m (kg).</summary>
    public double RadiusAtMass(double m) => Interp(M, R, m);
    public double DensityAt(double r) => Interp(R, Rho, r);
    public double EnergyAt(double r) => Interp(R, U, r);

    /// <summary>Index of the layer at radius r.</summary>
    public int LayerAt(double r)
    {
        for (var k = 0; k < LayerRadius.Length; k++) if (r <= LayerRadius[k]) return k;
        return LayerRadius.Length - 1;
    }
}

/// <summary>
/// Particle initial conditions for a layered body, from the idea of Kegerreis et al. (2019, MNRAS 487:5029, "SEAGen"):
/// equal-mass particles on concentric shells, each shell's count set by the profile's mass in it so the particle density
/// follows ρ(r), and each shell's particles spread by an equal-area partition of the sphere. The partition is Leopardi's
/// (2006, ETNA 25:309) recursive zonal equal-area construction; every shell gets an independent random rotation so shells
/// do not line up. Written from the papers (SEAGen itself is GPL; no code was used). Then an optional damped relaxation.
/// </summary>
public static class PlanetBuilder
{
    /// <summary>
    /// <paramref name="n"/> points on the unit sphere, one per region of Leopardi's (2006) equal-area partition: polar caps
    /// of area 4π/n, then collars whose region counts are rounded with carry, collar boundaries re-fitted so every region
    /// has area 4π/n exactly; each point sits at its region's equal-area mid-colatitude and mid-longitude.
    /// </summary>
    public static Vec3[] EqualAreaPoints(int n)
    {
        if (n <= 0) return [];
        if (n == 1) return [new Vec3(0, 0, 1)];
        var pts = new List<Vec3>(n) { new(0, 0, 1) };
        if (n > 2)
        {
            var thetaC = 2 * Math.Asin(Math.Sqrt(1.0 / n));
            var ideal = Math.Sqrt(4 * Math.PI / n);
            var collars = Math.Max(1, (int)Math.Round((Math.PI - 2 * thetaC) / ideal));
            var fit = (Math.PI - 2 * thetaC) / collars;
            var counts = new int[collars];
            double carry = 0;
            for (var j = 0; j < collars; j++)
            {
                double t0 = thetaC + j * fit, t1 = thetaC + (j + 1) * fit;
                var y = n * (Math.Cos(t0) - Math.Cos(t1)) / 2;
                counts[j] = (int)Math.Round(y + carry);
                carry += y - counts[j];
            }
            // The ideal counts sum to n − 2; rounding with carry keeps that, but guard the last collar anyway.
            var sum = 0;
            for (var j = 0; j < collars; j++) sum += counts[j];
            counts[collars - 1] += n - 2 - sum;
            var cum = 1;
            var top = thetaC;
            const double golden = 0.6180339887498949;
            for (var j = 0; j < collars; j++)
            {
                cum += counts[j];
                var bottom = 2 * Math.Asin(Math.Sqrt(Math.Min(1.0, (double)cum / n)));
                if (counts[j] > 0)
                {
                    var cosMid = 0.5 * (Math.Cos(top) + Math.Cos(bottom));
                    var sinMid = Math.Sqrt(Math.Max(0, 1 - cosMid * cosMid));
                    var offset = 2 * Math.PI * ((j * golden) % 1);
                    for (var k = 0; k < counts[j]; k++)
                    {
                        var phi = offset + 2 * Math.PI * (k + 0.5) / counts[j];
                        pts.Add(new Vec3(sinMid * Math.Cos(phi), sinMid * Math.Sin(phi), cosMid));
                    }
                }
                top = bottom;
            }
        }
        pts.Add(new Vec3(0, 0, -1));
        return [.. pts];
    }

    /// <summary>Uniformly random rotation matrix (Shoemake 1992 unit quaternion), rows.</summary>
    private static (Vec3 A, Vec3 B, Vec3 C) RandomRotation(Func<double> rand)
    {
        double u1 = rand(), u2 = rand(), u3 = rand();
        double s1 = Math.Sqrt(1 - u1), s2 = Math.Sqrt(u1);
        double x = s1 * Math.Sin(2 * Math.PI * u2), y = s1 * Math.Cos(2 * Math.PI * u2);
        double z = s2 * Math.Sin(2 * Math.PI * u3), w = s2 * Math.Cos(2 * Math.PI * u3);
        return (
            new Vec3(1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w)),
            new Vec3(2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w)),
            new Vec3(2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y)));
    }

    /// <summary>
    /// Write <paramref name="n"/> equal-mass particles of the body <paramref name="p"/> into <paramref name="s"/> from index
    /// <paramref name="offset"/>, centred on the origin with their centre of mass exactly there, at rest.
    /// <paramref name="materialIndex"/>[k] is layer k's index into <c>s.Materials</c>; <paramref name="body"/> goes into
    /// <c>s.Body</c>. h = <paramref name="eta"/>·(m/ρ)^{1/3} from the profile density.
    /// </summary>
    public static void Place(SphState s, int offset, PlanetProfile p, int n, int body, int[] materialIndex, Func<double> rand, double eta = 1.2)
    {
        var mp = p.Mass / n;
        int Cum(double r) => (int)Math.Round(p.MassAt(r) / mp);
        var at = offset;
        for (var layer = p.Layers.Count - 1; layer >= 0; layer--)
        {
            var top = p.LayerRadius[layer];
            var bottom = layer > 0 ? p.LayerRadius[layer - 1] : 0;
            if (top <= bottom) continue;
            var spacing = Math.Cbrt(mp / p.DensityAt(0.5 * (top + bottom)));
            var shells = Math.Max(1, (int)Math.Round((top - bottom) / spacing));
            var dr = (top - bottom) / shells;
            for (var k = 0; k < shells; k++)
            {
                var outer = top - k * dr;
                var inner = k == shells - 1 ? bottom : outer - dr;
                var count = Cum(outer) - Cum(inner);
                if (count <= 0) continue;
                var rs = p.RadiusAtMass(0.5 * (p.MassAt(inner) + p.MassAt(outer)));
                var rho = p.DensityAt(rs);
                var u = p.EnergyAt(rs);
                var h = eta * Math.Cbrt(mp / rho);
                Vec3[] pts = inner <= 0 && count == 1 ? new[] { Vec3.Zero } : EqualAreaPoints(count);
                var (ra, rb, rc) = RandomRotation(rand);
                foreach (var q in pts)
                {
                    if (at - offset >= n) throw new InvalidOperationException("shell counts exceed the particle budget");
                    s.X[at] = rs * Vec3.Dot(ra, q); s.Y[at] = rs * Vec3.Dot(rb, q); s.Z[at] = rs * Vec3.Dot(rc, q);
                    s.Vx[at] = 0; s.Vy[at] = 0; s.Vz[at] = 0;
                    s.M[at] = mp; s.Rho[at] = rho; s.U[at] = u; s.H[at] = h;
                    s.Mat[at] = materialIndex[layer]; s.Body[at] = body;
                    at++;
                }
            }
        }
        if (at - offset != n) throw new InvalidOperationException($"placed {at - offset} particles, expected {n}");
        double cx = 0, cy = 0, cz = 0;
        for (var i = offset; i < at; i++) { cx += s.X[i]; cy += s.Y[i]; cz += s.Z[i]; }
        cx /= n; cy /= n; cz /= n;
        for (var i = offset; i < at; i++) { s.X[i] -= cx; s.Y[i] -= cy; s.Z[i] -= cz; }
    }

    /// <summary>A state holding only this body: materials are the profile's layer materials, in layer order.</summary>
    public static SphState Build(PlanetProfile p, int n, uint seed = 1, double eta = 1.2)
    {
        var mats = new Material[p.Layers.Count];
        var idx = new int[p.Layers.Count];
        for (var k = 0; k < mats.Length; k++) { mats[k] = p.Layers[k].Material; idx[k] = k; }
        var s = new SphState(n, mats);
        Place(s, 0, p, n, 0, idx, Rng.Mulberry32(seed), eta);
        return s;
    }

    /// <summary>
    /// Damped relaxation of an isolated body built by <see cref="Build"/>: <paramref name="steps"/> solver steps with
    /// velocities decaying on <paramref name="dampingTime"/> (s; 0 = 0.3 R/c̄, near critical for the fundamental mode) and
    /// u held on each layer's isentrope at the particle's current density, so the body settles without heating. Ends at
    /// rest with its centre of mass at the origin. Returns the simulated time spent, s.
    /// </summary>
    public static double Relax(SphState s, PlanetProfile p, SphOptions options, int steps, double dampingTime = 0)
    {
        var solver = new SphSolver(s, options) { FreezeInternalEnergy = true };
        solver.Initialize();
        if (dampingTime <= 0)
        {
            double mc = 0, mt = 0;
            for (var i = 0; i < s.Count; i++) { mc += s.M[i] * s.Cs[i]; mt += s.M[i]; }
            dampingTime = 0.3 * p.Radius / (mc / mt);
        }
        solver.DampingTime = dampingTime;
        var isen = p.Isentropes;
        solver.AfterStep = sv =>
        {
            var st = sv.State;
            for (var i = 0; i < st.Count; i++) st.U[i] = isen[Math.Min(st.Mat[i], isen.Length - 1)].EnergyAt(st.Rho[i]);
        };
        for (var k = 0; k < steps; k++) solver.Step();
        Recentre(s, 0, s.Count, true);
        return solver.Time;
    }

    /// <summary>Move particles [from, from + count) so their centre of mass is at the origin; optionally zero their velocities.</summary>
    public static void Recentre(SphState s, int from, int count, bool stop)
    {
        double m = 0, x = 0, y = 0, z = 0, vx = 0, vy = 0, vz = 0;
        for (var i = from; i < from + count; i++)
        {
            m += s.M[i];
            x += s.M[i] * s.X[i]; y += s.M[i] * s.Y[i]; z += s.M[i] * s.Z[i];
            vx += s.M[i] * s.Vx[i]; vy += s.M[i] * s.Vy[i]; vz += s.M[i] * s.Vz[i];
        }
        if (!(m > 0)) return;
        x /= m; y /= m; z /= m; vx /= m; vy /= m; vz /= m;
        for (var i = from; i < from + count; i++)
        {
            s.X[i] -= x; s.Y[i] -= y; s.Z[i] -= z;
            if (stop) { s.Vx[i] = 0; s.Vy[i] = 0; s.Vz[i] = 0; }
            else { s.Vx[i] -= vx; s.Vy[i] -= vy; s.Vz[i] -= vz; }
        }
    }
}
