namespace Spayly.Sim;

/// <summary>
/// Schwarzschild (non-rotating) black holes: characteristic radii, tidal limits, the thin-disc model, and the photon orbits
/// behind the lensing tables. km, kg, s like the rest of Sim, except the photon-orbit functions, which work in units of the
/// Schwarzschild radius (r_s = 1, u = r_s / r) because the geometry is scale-free there.
///
/// Photon orbits (standard GR; the table method is Bruneton 2020, arXiv:2010.08735): with r_s = 1 and u = 1/r a null
/// geodesic in its orbital plane obeys d²u/dφ² = 3u²/2 − u, with first integral (du/dφ)² = e² − u²(1 − u), where e = 1/b
/// is the inverse impact parameter. The potential u²(1 − u) peaks at the photon sphere u = 2/3 at μ = 4/27: a ray from
/// infinity with e² &gt; μ (b &lt; √27/2) falls in; otherwise it turns at the periapsis u_a, the smallest positive root of
/// u³ − u² + e² = 0, and escapes after sweeping 2φ_a.
/// </summary>
public static class BlackHole
{
    /// <summary>km/s.</summary>
    public const double SpeedOfLight = 299792.458;
    /// <summary>Peak of the photon potential u²(1 − u), reached at the photon sphere u = 2/3. A ray from infinity with e² &gt; μ is captured.</summary>
    public const double Mu = 4.0 / 27;
    /// <summary>√μ: the critical inverse impact parameter, 1/r_s.</summary>
    public static readonly double SqrtMu = Math.Sqrt(Mu);
    /// <summary>Critical impact parameter b_c = √27/2 = 1/√μ, r_s: the shadow's radius seen from far away.</summary>
    public static readonly double CriticalImpact = Math.Sqrt(27) / 2;
    /// <summary>Numerical coefficient of the fluid Roche limit, d = 2.44 R (ρ_M/ρ_m)^(1/3).</summary>
    public const double RocheFluid = 2.44;
    /// <summary>Rigid-body Roche coefficient 2^(1/3) ≈ 1.26.</summary>
    public static readonly double RocheRigid = Math.Cbrt(2);

    const double GSi = Constants.G * 1e9;          // m^3 kg^-1 s^-2
    const double CSi = SpeedOfLight * 1000;        // m/s
    const double ProtonMass = 1.67262192369e-27;   // kg
    const double ThomsonCross = 6.6524587321e-29;  // m^2

    // ---------- radii ----------

    /// <summary>Schwarzschild radius r_s = 2GM/c², km, of a mass in kg. The Sun's is 2.953 km.</summary>
    public static double SchwarzschildRadiusKm(double massKg) => 2 * Constants.G * massKg / (SpeedOfLight * SpeedOfLight);

    /// <summary>Photon sphere, 1.5 r_s (3GM/c²), km: the radius of the unstable circular light orbit.</summary>
    public static double PhotonSphereRadiusKm(double massKg) => 1.5 * SchwarzschildRadiusKm(massKg);

    /// <summary>Innermost stable circular orbit, 3 r_s (6GM/c²), km: where a thin disc's inner edge sits.</summary>
    public static double IscoRadiusKm(double massKg) => 3 * SchwarzschildRadiusKm(massKg);

    /// <summary>Radius of the shadow seen from far away, b_c r_s = (√27/2) r_s ≈ 2.6 r_s, km.</summary>
    public static double ShadowRadiusKm(double massKg) => CriticalImpact * SchwarzschildRadiusKm(massKg);

    /// <summary>A black hole's radius is its horizon: after any change to its mass, r_s follows. No-op for other bodies.</summary>
    public static void SyncHorizon(Body b)
    {
        if (b.Kind == BodyKind.BlackHole) b.Radius = SchwarzschildRadiusKm(b.Mass);
    }

    /// <summary>
    /// Angular radius of the shadow, rad, seen by an observer at rest at <paramref name="r"/> (in r_s): sin θ = (b_c/r)√(1 − 1/r).
    /// π/2 at the photon sphere; inside it the escape cone shrinks round the outward direction, so the shadow is π − θ.
    /// </summary>
    public static double ShadowAngularRadius(double r)
    {
        if (r <= 1) return Math.PI;
        var s = Math.Min(1, CriticalImpact / r * Math.Sqrt(1 - 1 / r));
        return r >= 1.5 ? Math.Asin(s) : Math.PI - Math.Asin(s);
    }

    /// <summary>
    /// Specific angular momentum of a circular geodesic at <paramref name="r"/> (r_s), in r_s·c: L² = M r²/(r − 3M) with
    /// M = 1/2, i.e. r²/(2r − 3). Its minimum is the ISCO; it diverges at the photon sphere.
    /// </summary>
    public static double CircularOrbitAngularMomentum(double r) => r > 1.5 ? Math.Sqrt(r * r / (2 * r - 3)) : double.PositiveInfinity;

    // ---------- tides ----------

    /// <summary>Tidal-disruption radius r_t = R_* (M_BH / M_*)^(1/3), km. A star is torn apart outside the horizon only if r_t &gt; r_s.</summary>
    public static double TidalRadiusKm(double bhMass, double starMass, double starRadiusKm) => starRadiusKm * Math.Cbrt(bhMass / starMass);

    /// <summary>
    /// Fluid Roche limit, km: d = 2.44 R_M (ρ_M/ρ_m)^(1/3), written with the satellite's radius so it holds for a primary with no
    /// meaningful density (a black hole): 2.44 R_m (M/m)^(1/3).
    /// </summary>
    public static double RocheLimitFluidKm(double primaryMass, double satelliteMass, double satelliteRadiusKm) =>
        RocheFluid * satelliteRadiusKm * Math.Cbrt(primaryMass / satelliteMass);

    /// <summary>Rigid Roche limit, km: d = 2^(1/3) R_m (M/m)^(1/3) ≈ 1.26 R_M (ρ_M/ρ_m)^(1/3).</summary>
    public static double RocheLimitRigidKm(double primaryMass, double satelliteMass, double satelliteRadiusKm) =>
        RocheRigid * satelliteRadiusKm * Math.Cbrt(primaryMass / satelliteMass);

    // ---------- the thin disc ----------

    /// <summary>Eddington luminosity 4πGMm_p c/σ_T, W: 1.26e31 W per solar mass.</summary>
    public static double EddingtonLuminosity(double massKg) => 4 * Math.PI * GSi * massKg * ProtonMass * CSi / ThomsonCross;

    /// <summary>Accretion rate that radiates the Eddington luminosity at radiative efficiency η, kg/s: L_Edd / (η c²).</summary>
    public static double EddingtonAccretionRate(double massKg, double efficiency = 0.1) => EddingtonLuminosity(massKg) / (efficiency * CSi * CSi);

    /// <summary>
    /// Shakura–Sunyaev (1973) thin-disc effective temperature, K: T(r) = [3GMṀ/(8πσr³) · (1 − √(r_in/r))]^(1/4). Zero at and
    /// inside <paramref name="rInKm"/>; SI inside, km at the boundary.
    /// </summary>
    public static double DiscTemperatureK(double massKg, double mdotKgPerS, double rKm, double rInKm)
    {
        if (rKm <= rInKm) return 0;
        var r = rKm * 1000;
        var flux = 3 * GSi * massKg * mdotKgPerS / (8 * Math.PI * Constants.StefanBoltzmann * r * r * r) * (1 - Math.Sqrt(rInKm / rKm));
        return Math.Pow(flux, 0.25);
    }

    /// <summary>Where T(r) peaks: d/dr[r⁻³(1 − √(r_in/r))] = 0 at r = (7/6)² r_in ≈ 1.36 r_in. Same unit as <paramref name="rIn"/>.</summary>
    public static double DiscPeakRadius(double rIn) => 49.0 / 36 * rIn;

    /// <summary>T(r)/T(peak) of the Shakura–Sunyaev profile; any length unit, as long as both radii share it.</summary>
    public static double DiscTemperatureShape(double r, double rIn)
    {
        if (r <= rIn) return 0;
        var rp = DiscPeakRadius(rIn);
        var f = (1 - Math.Sqrt(rIn / r)) / (r * r * r);
        var fp = (1 - Math.Sqrt(rIn / rp)) / (rp * rp * rp);
        return Math.Pow(f / fp, 0.25);
    }

    /// <summary>
    /// Redshift factor g = ν_obs/ν_em from an emitter on a circular Keplerian orbit to an observer at rest far away:
    /// g = √(1 − 3M/r) / (1 − Ω b_z) with Ω = √(M/r³); in r_s units (M = 1/2) √(1 − 1.5/r) / (1 − √(0.5/r³) b_z).
    /// <paramref name="r"/> and <paramref name="bz"/> in r_s; b_z is the photon's angular momentum per unit energy along the
    /// orbit's axis, positive when it leaves along the emitter's motion. T_obs = g T and the bolometric intensity goes as g⁴.
    /// </summary>
    public static double CircularEmitterG(double r, double bz) => Math.Sqrt(1 - 1.5 / r) / (1 - Math.Sqrt(0.5 / (r * r * r)) * bz);

    /// <summary>Gravitational redshift of an emitter at rest at <paramref name="r"/> (r_s) seen from far away: √(1 − 1/r).</summary>
    public static double StaticEmitterG(double r) => Math.Sqrt(1 - 1 / r);

    // ---------- photon orbits (units of r_s) ----------

    /// <summary>
    /// Periapsis u_a (1/r_s) of a ray from infinity with inverse impact parameter e ≤ √μ: the trigonometric root
    /// u_a = 1/3 + (2/3) sin(asin(2e²/μ − 1)/3), replaced by its series e + e²/2 + 5e³/8 + e⁴ + 231e⁵/128 below e = 0.03 (where
    /// the asin loses digits in float32, so the shader does the same). The trigonometric root is polished by one Newton step
    /// on u²(1 − u) = e² except near the photon sphere, where the slope u(2 − 3u) vanishes and float32 residuals would make
    /// the step worse than the root.
    /// </summary>
    public static double PeriapsisU(double e)
    {
        if (e < 0.03) return e * (1 + e * (0.5 + e * (0.625 + e * (1 + e * (231.0 / 128)))));
        var u = 1.0 / 3 + 2.0 / 3 * Math.Sin(Math.Asin(Math.Clamp(2 * e * e / Mu - 1, -1, 1)) / 3);
        var d = u * (2 - 3 * u);
        if (d > 0.05) u -= (u * u * (1 - u) - e * e) / d;
        return u;
    }

    /// <summary>The three roots u1 &lt; 0 ≤ u_a ≤ 2/3 ≤ u3 of u³ − u² + e² = 0, for e² ≤ μ.</summary>
    static (double U1, double Ua, double U3) Roots(double e)
    {
        // Double precision throughout (the tables are built here): the trigonometric roots are good to ~1e-16 above e = 1e-4.
        var t = Math.Asin(Math.Clamp(2 * e * e / Mu - 1, -1, 1)) / 3;
        var ua = e < 1e-4 ? e * (1 + e * (0.5 + e * 0.625)) : 1.0 / 3 + 2.0 / 3 * Math.Sin(t);
        var d = ua * (2 - 3 * ua);
        if (d > 1e-4) ua -= (ua * ua * (1 - ua) - e * e) / d;
        var u3 = 1.0 / 3 + 2.0 / 3 * Math.Sin(t + 2 * Math.PI / 3);
        return (1 - ua - u3, ua, u3);
    }

    static readonly double[] GlX = [0.1834346424956498, 0.5255324099163290, 0.7966664774136267, 0.9602898564975363];
    static readonly double[] GlW = [0.3626837833783620, 0.3137066458778873, 0.2223810344533745, 0.1012285362903763];

    /// <summary>
    /// dφ/dt of an escape-type orbit in t = √(1 − u/u_a): 2√u_a / √((u − u1)(u3 − u)), smooth through the periapsis (t = 0)
    /// where dφ/du has its inverse-square-root singularity.
    /// </summary>
    static double EscapeIntegrand(double ua, double u1, double u3, double t)
    {
        var u = ua * (1 - t * t);
        return 2 * Math.Sqrt(ua) / Math.Sqrt((u - u1) * (u3 - u));
    }

    /// <summary>∫ EscapeIntegrand dt over [t0, t1] by 8-point Gauss–Legendre on <paramref name="sub"/> equal panels.</summary>
    static double EscapePanel(double ua, double u1, double u3, double t0, double t1, int sub)
    {
        double sum = 0;
        var h = (t1 - t0) / sub;
        for (var s = 0; s < sub; s++)
        {
            var half = h / 2;
            var mid = t0 + s * h + half;
            for (var k = 0; k < 4; k++)
                sum += GlW[k] * half * (EscapeIntegrand(ua, u1, u3, mid - half * GlX[k]) + EscapeIntegrand(ua, u1, u3, mid + half * GlX[k]));
        }
        return sum;
    }

    /// <summary>∫ du/√(e² − u² + u³) over [u0, u1] by 8-point Gauss–Legendre; panels near the photon sphere are split finer.</summary>
    static double CapturePanel(double e2, double u0, double u1, int sub)
    {
        var near = Math.Abs(0.5 * (u0 + u1) - 2.0 / 3) < 0.02;
        if (near) sub *= 32;
        double sum = 0;
        var h = (u1 - u0) / sub;
        for (var s = 0; s < sub; s++)
        {
            var half = h / 2;
            var mid = u0 + s * h + half;
            for (var k = 0; k < 4; k++)
            {
                double a = mid - half * GlX[k], b = mid + half * GlX[k];
                sum += GlW[k] * half * (1 / Math.Sqrt(e2 - a * a + a * a * a) + 1 / Math.Sqrt(e2 - b * b + b * b * b));
            }
        }
        return sum;
    }

    /// <summary>
    /// Angle (rad) a ray from infinity with inverse impact parameter <paramref name="e"/> sweeps coming in to
    /// <paramref name="u"/>: F(e, u) = ∫₀ᵘ du'/√(e² − u'²(1 − u')). For e² &lt; μ, u is clamped to the periapsis and the integral
    /// taken in t = √(1 − u/u_a); for e² ≥ μ it runs to u ≤ 1 (the horizon).
    /// </summary>
    public static double SweptAngle(double e, double u)
    {
        if (e <= 0) return u > 0 ? Math.PI / 2 : 0;
        if (e * e < Mu)
        {
            var (u1, ua, u3) = Roots(e);
            if (ua <= 0) return 0;
            var t0 = Math.Sqrt(Math.Max(0, 1 - Math.Min(u, ua) / ua));
            return EscapePanel(ua, u1, u3, t0, 1, 256);
        }
        return CapturePanel(e * e, 0, Math.Min(u, 1), 256);
    }

    /// <summary>
    /// Total deflection of a ray passing a hole from infinity to infinity, rad: 2φ_a − π, with b in r_s. +∞ for b ≤ b_c (captured).
    /// Weak field: 2/b + (15π/16)/b² + (16/3)/b³ (the 4M/b + 15πM²/4b² + 128M³/3b³ series with M = 1/2).
    /// </summary>
    public static double DeflectionAngle(double b)
    {
        if (b <= CriticalImpact) return double.PositiveInfinity;
        return 2 * PeriapsisAngle(1 / b) - Math.PI;
    }

    /// <summary>
    /// φ_a = F(e, u_a), the angle swept from infinity to the periapsis, rad (e² &lt; μ). Integrated from t = 0 exactly: going
    /// through <see cref="SweptAngle"/> with a separately computed u_a would cut off √(root error) of the integral.
    /// </summary>
    public static double PeriapsisAngle(double e)
    {
        if (e <= 0) return Math.PI / 2;
        var (u1, ua, u3) = Roots(e);
        return EscapePanel(ua, u1, u3, 0, 1, 256);
    }

    static double Accel(double u) => 1.5 * u * u - u;

    /// <summary>One RK4 step of d²u/dφ² = 3u²/2 − u (state u, du/dφ; step h rad).</summary>
    static (double U, double Du) Rk4(double u, double du, double h)
    {
        var k1u = du;
        var k1v = Accel(u);
        var k2u = du + 0.5 * h * k1v;
        var k2v = Accel(u + 0.5 * h * k1u);
        var k3u = du + 0.5 * h * k2v;
        var k3v = Accel(u + 0.5 * h * k2u);
        var k4u = du + h * k3v;
        var k4v = Accel(u + h * k3u);
        return (u + h / 6 * (k1u + 2 * k2u + 2 * k3u + k4u), du + h / 6 * (k1v + 2 * k2v + 2 * k3v + k4v));
    }

    /// <summary>Cubic Hermite through (u0, d0) and (u1, d1) a step h apart, at fraction s of the step.</summary>
    static double Hermite(double u0, double d0, double u1, double d1, double h, double s)
    {
        var s2 = s * s;
        var s3 = s2 * s;
        return (2 * s3 - 3 * s2 + 1) * u0 + (s3 - 2 * s2 + s) * h * d0 + (3 * s2 - 2 * s3) * u1 + (s3 - s2) * h * d1;
    }

    /// <summary>Fraction s in [0, 1] of the step where the Hermite cubic equals <paramref name="target"/> (bisection; the caller brackets it).</summary>
    static double HermiteRoot(double u0, double d0, double u1, double d1, double h, double target)
    {
        double lo = 0, hi = 1;
        var rising = u1 > u0;
        for (var i = 0; i < 50; i++)
        {
            var mid = 0.5 * (lo + hi);
            var above = Hermite(u0, d0, u1, d1, h, mid) > target;
            if (above == rising) hi = mid;
            else lo = mid;
        }
        return 0.5 * (lo + hi);
    }

    /// <summary>u after integrating the photon orbit from (<paramref name="u0"/>, <paramref name="du0"/>) over <paramref name="phi"/> rad with RK4.</summary>
    public static double OrbitU(double u0, double du0, double phi, double step = 1e-3)
    {
        var n = Math.Max(1, (int)Math.Ceiling(phi / step));
        var h = phi / n;
        double u = u0, v = du0;
        for (var i = 0; i < n; i++) (u, v) = Rk4(u, v, h);
        return u;
    }

    /// <summary>
    /// Whether a ray from infinity with impact parameter <paramref name="b"/> (r_s) escapes, decided by integrating the orbit (RK4)
    /// until it turns (du/dφ &lt; 0) or reaches the horizon; independent of the e² ≤ μ criterion it should reproduce.
    /// </summary>
    public static bool Escapes(double b, double step = 1e-3)
    {
        double u = 0, v = 1 / b;
        for (double phi = 0; phi < 400; phi += step)
        {
            (u, v) = Rk4(u, v, step);
            if (v < 0) return true;
            if (u >= 1) return false;
        }
        return false;
    }

    /// <summary>Where a traced ray crosses the disc plane: swept angle from the camera (rad) and radius (r_s).</summary>
    public readonly record struct DiscCrossing(double Phi, double R);

    /// <summary>Result of <see cref="TraceRay"/>.</summary>
    /// <param name="Captured">The ray reaches the horizon.</param>
    /// <param name="Swept">Angle swept round the hole from the camera to infinity (or to the horizon), rad.</param>
    /// <param name="Deflection">Swept − (π − ψ): 0 in flat space, the bending an escaping ray picks up; NaN when captured.</param>
    /// <param name="Crossings">Crossings of the plane whose nodes are at nodePhi + kπ, in order, radius in r_s.</param>
    public sealed record RayTrace(bool Captured, double Swept, double Deflection, IReadOnlyList<DiscCrossing> Crossings);

    /// <summary>
    /// Reference trace (RK4 on the orbit equation, no tables) of a ray from a camera at rest at u_c = r_s/r, leaving at angle
    /// <paramref name="psi"/> (rad) from the direction to the hole — the static observer's aberration gives e = u_c√(1 − u_c)/sin ψ
    /// and du/dφ = u_c√(1 − u_c) cot ψ. It sweeps φ from 0 at the camera until u reaches 0 (escape) or 1 (horizon), recording
    /// the crossings of the plane whose nodes lie at <paramref name="nodePhi"/> + kπ (nodePhi in [0, π); NaN for none).
    /// </summary>
    public static RayTrace TraceRay(double uCamera, double psi, double nodePhi = double.NaN, int maxCrossings = 4, double step = 1e-3)
    {
        var crossings = new List<DiscCrossing>();
        var sin = Math.Sin(psi);
        var cos = Math.Cos(psi);
        if (Math.Abs(sin) < 1e-12) return cos > 0 ? new RayTrace(true, 0, double.NaN, crossings) : new RayTrace(false, 0, 0, crossings);
        var uc = uCamera;
        double phi = 0, u = uc, v = uc * Math.Sqrt(1 - uc) * cos / sin;
        var node = double.IsNaN(nodePhi) ? double.PositiveInfinity : nodePhi;
        var h = step;
        while (phi < 400)
        {
            var (u1, v1) = Rk4(u, v, h);
            // Where this step ends the ray, if it does: escape (u crosses 0) or the horizon (u crosses 1).
            var end = double.PositiveInfinity;
            var captured = false;
            if (u1 <= 0) end = phi + h * HermiteRoot(u, v, u1, v1, h, 0);
            else if (u1 >= 1)
            {
                end = phi + h * HermiteRoot(u, v, u1, v1, h, 1);
                captured = true;
            }
            while (node <= Math.Min(phi + h, end) && crossings.Count < maxCrossings)
            {
                var un = Hermite(u, v, u1, v1, h, (node - phi) / h);
                if (un > 0) crossings.Add(new DiscCrossing(node, 1 / un));
                node += Math.PI;
            }
            if (double.IsFinite(end))
                return new RayTrace(captured, end, captured ? double.NaN : end - (Math.PI - psi), crossings);
            (u, v) = (u1, v1);
            phi += h;
        }
        return new RayTrace(true, phi, double.NaN, crossings);
    }

    // ---------- the lensing tables ----------

    /// <summary>
    /// Precompute the two lensing tables (Bruneton 2020's method, own derivation and layout) as RGBA float32 arrays for the
    /// caller to turn into textures. Both are <paramref name="width"/> × <paramref name="height"/>, row-major, texel (i, j) at
    /// 4(j·width + i), texel centres on the grid x = i/(width − 1), y = j/(height − 1).
    ///
    /// Escape table (e² &lt; μ), x = 1 − √(1 − e/√μ), which packs columns towards the critical ray:
    ///   R = F(e, u)/φ_a at u = u_a(1 − (1 − y)²), i.e. y = 1 − √(1 − u/u_a), in which F is linear at the periapsis;
    ///   G = u(θ)/u_a on the incoming half-orbit at θ = y φ_a;
    ///   B = φ_a + ln(1 − x), the periapsis angle without its logarithmic divergence at the photon sphere;
    ///   A = u_a.
    /// Capture table (e² ≥ μ), x = 1 − √(1 − b/b_c):
    ///   R = F(e, u)/φ_h at u = y;  G = u(θ) at θ = y φ_h;  B = φ_h + 2 ln(1 − x);  A = 1,
    /// where φ_h is the angle swept from infinity to the horizon. F comes from Gauss–Legendre quadrature (in t = √(1 − u/u_a)
    /// for escapes), u(θ) from RK4 on the orbit equation with Hermite interpolation. <see cref="LensingTables"/> reads them
    /// back exactly as the shader does.
    /// </summary>
    public static LensingTables BuildLensingTables(int width = 512, int height = 512, double step = 2e-3)
    {
        if (width < 2 || height < 2) throw new ArgumentOutOfRangeException(nameof(width), "tables need at least 2 × 2 texels");
        var esc = new float[width * height * 4];
        var cap = new float[width * height * 4];
        // The last column sits on the critical ray itself, where φ diverges; its regular part is taken half a column inside.
        var xLast = 1 - 0.5 / (width - 1);
        var us = new List<double>(8192);
        var dus = new List<double>(8192);
        var f = new double[height];
        for (var i = 0; i < width; i++)
        {
            var x = Math.Min((double)i / (width - 1), xLast);

            // Escape column.
            if (i == 0)
            {
                // e → 0: a straight line, u = e sin θ, φ_a = π/2.
                for (var j = 0; j < height; j++)
                {
                    var y = (double)j / (height - 1);
                    Put(esc, width, i, j, Math.Asin(1 - (1 - y) * (1 - y)) / (Math.PI / 2), Math.Sin(y * Math.PI / 2), Math.PI / 2, 0);
                }
            }
            else
            {
                var e = SqrtMu * (1 - (1 - x) * (1 - x));
                var (u1, ua, u3) = Roots(e);
                // Cumulative quadrature from t = 1 (u = 0) down to t = 0 (the periapsis), one panel per texel.
                f[0] = 0;
                for (var j = 1; j < height; j++)
                {
                    double tPrev = 1 - (double)(j - 1) / (height - 1), t = 1 - (double)j / (height - 1);
                    f[j] = f[j - 1] + EscapePanel(ua, u1, u3, t, tPrev, 2);
                }
                var phiA = f[height - 1];
                Integrate(0, e, step, us, dus, stopAtPeriapsis: true);
                for (var j = 0; j < height; j++)
                {
                    var y = (double)j / (height - 1);
                    var uTheta = Math.Min(Sample(us, dus, step, y * phiA), ua);
                    Put(esc, width, i, j, f[j] / phiA, uTheta / ua, phiA + Math.Log(1 - x), ua);
                }
            }

            // Capture column.
            if (i == 0)
            {
                // b → 0: radial, φ_h → 0; in the limit F/φ_h = u and u(y φ_h) = y.
                for (var j = 0; j < height; j++)
                {
                    var y = (double)j / (height - 1);
                    Put(cap, width, i, j, y, y, 0, 1);
                }
            }
            else
            {
                var b = CriticalImpact * (1 - (1 - x) * (1 - x));
                var e = 1 / b;
                var e2 = e * e;
                f[0] = 0;
                for (var j = 1; j < height; j++)
                    f[j] = f[j - 1] + CapturePanel(e2, (double)(j - 1) / (height - 1), (double)j / (height - 1), 2);
                var phiH = f[height - 1];
                Integrate(0, e, step, us, dus, stopAtPeriapsis: false);
                for (var j = 0; j < height; j++)
                {
                    var y = (double)j / (height - 1);
                    var uTheta = Math.Clamp(Sample(us, dus, step, y * phiH), 0, 1);
                    Put(cap, width, i, j, f[j] / phiH, uTheta, phiH + 2 * Math.Log(1 - x), 1);
                }
            }
        }
        return new LensingTables(width, height, esc, cap);
    }

    static void Put(float[] tab, int width, int i, int j, double r, double g, double b, double a)
    {
        var k = (j * width + i) * 4;
        tab[k] = (float)r;
        tab[k + 1] = (float)g;
        tab[k + 2] = (float)b;
        tab[k + 3] = (float)a;
    }

    /// <summary>RK4 from (u0, du0) in fixed steps, storing u and du/dφ at every step until one step past the periapsis or the horizon.</summary>
    static void Integrate(double u0, double du0, double h, List<double> us, List<double> dus, bool stopAtPeriapsis)
    {
        us.Clear();
        dus.Clear();
        double u = u0, v = du0;
        us.Add(u);
        dus.Add(v);
        for (var n = 0; n < 400_000; n++)
        {
            (u, v) = Rk4(u, v, h);
            us.Add(u);
            dus.Add(v);
            if (stopAtPeriapsis ? v < 0 : u >= 1) return;
        }
    }

    /// <summary>u at angle θ from the stored steps, by cubic Hermite; clamped to the stored range.</summary>
    static double Sample(List<double> us, List<double> dus, double h, double theta)
    {
        var last = us.Count - 1;
        var fk = Math.Max(0, theta / h);
        var k = Math.Min((int)fk, last - 1);
        if (k < 0) return us[0];
        var s = Math.Clamp(fk - k, 0, 1);
        return Hermite(us[k], dus[k], us[k + 1], dus[k + 1], h, s);
    }
}

/// <summary>
/// The two lensing tables of <see cref="BlackHole.BuildLensingTables"/> and a CPU reader that does, in double, exactly what
/// the lensing shader does in float32 with them — the tests hold the shader's arithmetic to the RK4 reference through it.
/// </summary>
public sealed class LensingTables
{
    /// <summary>Columns (the e or b axis).</summary>
    public int Width { get; }
    /// <summary>Rows (u or θ).</summary>
    public int Height { get; }
    /// <summary>Escape-type orbits (e² &lt; μ), RGBA float32 row-major.</summary>
    public float[] Escape { get; }
    /// <summary>Capture-type orbits (e² ≥ μ), RGBA float32 row-major.</summary>
    public float[] Capture { get; }

    /// <summary>Largest x the reader uses; the shader's float32 cannot resolve rays closer to the critical one than this anyway.</summary>
    public const double XMax = 0.999999;

    internal LensingTables(int width, int height, float[] escape, float[] capture)
    {
        Width = width;
        Height = height;
        Escape = escape;
        Capture = capture;
    }

    /// <summary>Bilinear read of one channel at grid coordinates (x, y) in [0, 1]², texel centres on the grid points; mirrors tab().</summary>
    public double Sample(float[] tab, int channel, double x, double y)
    {
        var fx = Math.Clamp(x, 0, 1) * (Width - 1);
        var fy = Math.Clamp(y, 0, 1) * (Height - 1);
        var i = Math.Min((int)fx, Width - 2);
        var j = Math.Min((int)fy, Height - 2);
        double tx = fx - i, ty = fy - j;
        double V(int a, int b) => tab[(b * Width + a) * 4 + channel];
        return (V(i, j) * (1 - tx) + V(i + 1, j) * tx) * (1 - ty) + (V(i, j + 1) * (1 - tx) + V(i + 1, j + 1) * tx) * ty;
    }

    /// <summary>What the shader knows about one ray after the table lookups; units r_s and rad.</summary>
    /// <param name="Escapes">The ray reaches infinity (false: the horizon).</param>
    /// <param name="Swept">Angle swept from the camera to infinity or to the horizon.</param>
    /// <param name="EscapeOrbit">The orbit is escape-type (e² &lt; μ), read from the escape table.</param>
    /// <param name="X">Table column coordinate.</param>
    /// <param name="ThetaCamera">The camera's angle along the orbit measured from its start at infinity.</param>
    /// <param name="ThetaMax">φ_a (escape-type) or φ_h (capture-type).</param>
    /// <param name="Ua">Periapsis u_a (escape-type), else 1.</param>
    /// <param name="Inward">The ray starts towards the hole.</param>
    public readonly record struct LensRay(bool Escapes, double Swept, bool EscapeOrbit, double X, double ThetaCamera, double ThetaMax, double Ua, bool Inward);

    /// <summary>
    /// Trace a ray from a camera at rest at u_c leaving at an angle ψ from the direction to the hole, given as cos ψ and sin ψ ≥ 0,
    /// through the tables; mirrors trace() in the shader. The camera's orbit parameter comes from
    /// t_c² = 1 − u_c/u_a = e² cos²ψ / (u_a [(u_a + u_c) − (u_a² + u_a u_c + u_c²)]), which (unlike 1 − u_c/u_a itself) keeps its
    /// digits for a ray grazing the camera's own radius.
    /// </summary>
    public LensRay Trace(double uc, double cosPsi, double sinPsi)
    {
        var e = uc * Math.Sqrt(1 - uc) / Math.Max(sinPsi, 1e-9);
        var inward = cosPsi > 0;
        if (e * e < BlackHole.Mu)
        {
            var x = Math.Min(1 - Math.Sqrt(Math.Max(1 - e / BlackHole.SqrtMu, 0)), XMax);
            var phiA = Sample(Escape, 2, x, 0) - Math.Log(1 - x);
            var ua = BlackHole.PeriapsisU(e);
            // Inside the photon sphere on the inner branch (u_c beyond u3): every such ray falls in.
            if (uc > ua * 1.000001) return new LensRay(false, 0, false, 0, 0, 0, 1, inward);
            var den = ua + uc - (ua * ua + ua * uc + uc * uc);
            var tc = Math.Min(1, e * Math.Abs(cosPsi) / Math.Sqrt(Math.Max(ua * den, 1e-30)));
            var fc = Sample(Escape, 0, x, 1 - tc) * phiA;
            return inward
                ? new LensRay(true, 2 * phiA - fc, true, x, fc, phiA, ua, true)
                : new LensRay(true, fc, true, x, 2 * phiA - fc, phiA, ua, false);
        }
        var xc = Math.Min(1 - Math.Sqrt(Math.Max(1 - 1 / (e * BlackHole.CriticalImpact), 0)), XMax);
        var phiH = Sample(Capture, 2, xc, 0) - 2 * Math.Log(1 - xc);
        var f = Sample(Capture, 0, xc, uc) * phiH;
        return inward
            ? new LensRay(false, phiH - f, false, xc, f, phiH, 1, true)
            : new LensRay(true, f, false, xc, f, phiH, 1, false);
    }

    /// <summary>u (1/r_s) at swept angle <paramref name="s"/> from the camera along a traced ray, 0 ≤ s ≤ Swept; mirrors orbit_u().</summary>
    public double OrbitU(in LensRay r, double s)
    {
        if (r.EscapeOrbit)
        {
            var th = r.ThetaCamera + s;
            if (th > r.ThetaMax) th = 2 * r.ThetaMax - th;
            return r.Ua * Sample(Escape, 1, r.X, th / r.ThetaMax);
        }
        var t = r.Inward ? r.ThetaCamera + s : r.ThetaCamera - s;
        return Sample(Capture, 1, r.X, t / Math.Max(r.ThetaMax, 1e-12));
    }
}
