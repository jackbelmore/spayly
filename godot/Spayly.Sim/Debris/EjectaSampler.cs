namespace Spayly.Sim.Debris;

/// <summary>
/// Housen &amp; Holsapple (2011, Icarus 211, 856–875) ejecta constants. The launch speed at launch radius x is (their eq. 14)
/// <c>v/U = C₁ [(x/a)(ρ/δ)^ν]^(−1/μ) (1 − x/(n₂R))^p</c> for n₁a ≤ x ≤ n₂R, and the mass launched inside x is
/// <c>M(&lt;x) = kρ(x³ − (n₁a)³)</c>. k only normalises that law; the sampler takes the total ejecta mass instead.
/// </summary>
/// <param name="Mu">Velocity exponent μ: ≈ 0.4 porous / sand-like targets, ≈ 0.55 non-porous rock and water.</param>
/// <param name="C1">Velocity constant C₁: 1.5 for the μ = 0.55 materials, 0.55 for μ ≈ 0.4 sand (HH11 Table 3).</param>
/// <param name="N1">Inner launch limit in projectile radii.</param>
/// <param name="N2">Outer launch limit in crater radii.</param>
/// <param name="P">Rim exponent p: the speed falls to 0 at n₂R.</param>
/// <param name="Nu">Density exponent ν of the (ρ/δ)^ν term (ρ target, δ projectile density).</param>
/// <param name="AngleNear">Launch angle above the horizontal at n₁a, rad.</param>
/// <param name="AngleRim">Launch angle above the horizontal at n₂R, rad.</param>
public readonly record struct EjectaScaling(double Mu, double C1, double N1 = 1.2, double N2 = 1.0, double P = 0.3, double Nu = 0.4,
    double AngleNear = 50 * Constants.Deg, double AngleRim = 40 * Constants.Deg)
{
    /// <summary>Non-porous rock (and water, ice): μ = 0.55, C₁ = 1.5.</summary>
    public static EjectaScaling Rock => new(0.55, 1.5);
    /// <summary>Porous, sand-like: μ = 0.4, C₁ = 0.55.</summary>
    public static EjectaScaling Porous => new(0.4, 0.55);
}

/// <summary>
/// N ejecta particles in the local impact frame: +z is the outward surface normal at the impact point, x and y span the
/// tangent plane, the origin is the impact point. Lengths are in the unit the crater and projectile radii were given in,
/// speeds in the unit of the impact speed.
/// </summary>
public sealed class EjectaSample
{
    /// <summary>kg. Sums to the requested ejecta mass exactly, in any order of summation.</summary>
    public required double[] Mass { get; init; }
    /// <summary>Launch point in the tangent plane (z = 0).</summary>
    public required Vec3[] Position { get; init; }
    public required Vec3[] Velocity { get; init; }
    /// <summary>Launch radius x of each particle.</summary>
    public required double[] LaunchRadius { get; init; }
    /// <summary>The target's momentum change, −Σ m v (kg × the speed unit), correctly rounded from the exact sum.</summary>
    public required Vec3 Recoil { get; init; }
    /// <summary>n₁a and n₂R as used (the inner limit is pulled in when a tiny crater would put it outside the outer one).</summary>
    public required double Inner { get; init; }
    public required double Outer { get; init; }

    public int Count => Mass.Length;
}

/// <summary>
/// Crater ejecta after Housen &amp; Holsapple (2011): launch radius drawn by inverse transform of the mass law (stratified, one
/// particle per equal-mass shell), speed from the launch radius, angle from 50° near the impact point to 40° at the rim,
/// azimuth uniform. Unit-agnostic: every formula is a ratio.
/// </summary>
public static class EjectaSampler
{
    /// <summary>(ρ/δ)^(−ν/μ), the density term of eq. 14 pulled out of the bracket.</summary>
    public static double DensityFactor(double targetDensity, double projectileDensity, EjectaScaling s) =>
        projectileDensity > 0 && targetDensity > 0 ? Math.Pow(targetDensity / projectileDensity, -s.Nu / s.Mu) : 1;

    /// <summary>n₁a and n₂R; when n₁a ≥ n₂R (a crater hardly bigger than its projectile) the inner limit becomes n₂R/2.</summary>
    public static (double Inner, double Outer) LaunchRange(double craterRadius, double projectileRadius, EjectaScaling s)
    {
        var outer = s.N2 * craterRadius;
        var inner = s.N1 * projectileRadius;
        if (!(inner < outer)) inner = 0.5 * outer;
        return (inner, outer);
    }

    /// <summary>Launch speed at launch radius <paramref name="x"/>, in the unit of <paramref name="speed"/> (eq. 14).</summary>
    public static double LaunchSpeed(double x, double craterRadius, double projectileRadius, double speed, double densityFactor, EjectaScaling s)
    {
        var rim = Math.Max(0, 1 - x / (s.N2 * craterRadius));
        return speed * s.C1 * densityFactor * Math.Pow(x / projectileRadius, -1 / s.Mu) * Math.Pow(rim, s.P);
    }

    /// <summary>Fraction of the ejecta mass launched inside <paramref name="x"/>: (x³ − inner³)/(outer³ − inner³), clamped to 0..1.</summary>
    public static double MassFractionInside(double x, double inner, double outer)
    {
        var xc = Math.Clamp(x, inner, outer);
        return (xc * xc * xc - inner * inner * inner) / (outer * outer * outer - inner * inner * inner);
    }

    /// <summary>
    /// Fraction of the ejecta mass launched faster than <paramref name="v"/>. The speed falls monotonically with launch
    /// radius, so this is the mass inside the radius where it equals v (found by bisection).
    /// </summary>
    public static double MassFractionAboveSpeed(double v, double craterRadius, double projectileRadius, double speed, double densityFactor, EjectaScaling s)
    {
        var (inner, outer) = LaunchRange(craterRadius, projectileRadius, s);
        if (v >= LaunchSpeed(inner, craterRadius, projectileRadius, speed, densityFactor, s)) return 0;
        if (v <= 0) return 1;
        double lo = inner, hi = outer;
        for (var i = 0; i < 200 && hi - lo > 1e-15 * outer; i++)
        {
            var mid = 0.5 * (lo + hi);
            if (LaunchSpeed(mid, craterRadius, projectileRadius, speed, densityFactor, s) > v) lo = mid;
            else hi = mid;
        }
        return MassFractionInside(0.5 * (lo + hi), inner, outer);
    }

    /// <summary>Launch angle above the horizontal at <paramref name="x"/>, rad: linear from AngleNear at the inner limit to AngleRim at the outer.</summary>
    public static double LaunchAngle(double x, double inner, double outer, EjectaScaling s) =>
        s.AngleNear + (s.AngleRim - s.AngleNear) * Math.Clamp((x - inner) / (outer - inner), 0, 1);

    /// <summary>
    /// <paramref name="n"/> near-equal positive masses whose sum is <paramref name="total"/> exactly in any order: the total's
    /// 53-bit significand K is split into n integers K_i (they differ by at most 1) and each m_i = K_i·2^e. Every partial sum
    /// is then an integer multiple of 2^e below 2^53·2^e, which a double holds exactly.
    /// </summary>
    public static double[] SplitMassExactly(double total, int n)
    {
        if (n <= 0) return [];
        if (!(total > 0) || !double.IsFinite(total)) throw new ArgumentOutOfRangeException(nameof(total), total, "mass must be positive and finite");
        var bits = BitConverter.DoubleToInt64Bits(total);
        var biased = (int)((bits >> 52) & 0x7FF);
        var significand = bits & 0xF_FFFF_FFFF_FFFFL;
        int exponent;
        if (biased == 0) exponent = -1074;
        else
        {
            significand |= 1L << 52;
            exponent = biased - 1075;
        }
        if (significand < n) throw new ArgumentOutOfRangeException(nameof(n), n, "more particles than the mass has significand units");
        var q = significand / n;
        var r = significand % n;
        var m = new double[n];
        for (var i = 0; i < n; i++) m[i] = Math.ScaleB(q + (i < r ? 1 : 0), exponent);
        return m;
    }

    /// <summary>
    /// Sample <paramref name="n"/> ejecta particles carrying <paramref name="ejectaMass"/> kg in total.
    /// <paramref name="craterRadius"/> (R) and <paramref name="projectileRadius"/> (a) share one length unit;
    /// <paramref name="speed"/> (U) sets the speed unit. Densities in any one unit (only their ratio enters).
    /// </summary>
    public static EjectaSample Sample(double craterRadius, double projectileRadius, double speed, double targetDensity, double projectileDensity,
        EjectaScaling scaling, double ejectaMass, int n, Func<double>? rng = null)
    {
        rng ??= Random.Shared.NextDouble;
        var (inner, outer) = LaunchRange(craterRadius, projectileRadius, scaling);
        var k = DensityFactor(targetDensity, projectileDensity, scaling);
        var masses = SplitMassExactly(ejectaMass, n);
        var pos = new Vec3[n];
        var vel = new Vec3[n];
        var radius = new double[n];
        var i3 = inner * inner * inner;
        var span = outer * outer * outer - i3;
        DD px = DD.Zero, py = DD.Zero, pz = DD.Zero;
        for (var i = 0; i < n; i++)
        {
            // Stratified inverse transform of M(<x): particle i owns the i-th equal-mass shell.
            var u = (i + rng()) / n;
            var x = Math.Cbrt(i3 + u * span);
            var az = 2 * Math.PI * rng();
            var theta = LaunchAngle(x, inner, outer, scaling);
            var v = LaunchSpeed(x, craterRadius, projectileRadius, speed, k, scaling);
            var (sa, ca) = Math.SinCos(az);
            var ct = Math.Cos(theta);
            radius[i] = x;
            pos[i] = new Vec3(x * ca, x * sa, 0);
            vel[i] = new Vec3(v * ct * ca, v * ct * sa, v * Math.Sin(theta));
            px += DD.Product(masses[i], vel[i].X);
            py += DD.Product(masses[i], vel[i].Y);
            pz += DD.Product(masses[i], vel[i].Z);
        }
        return new EjectaSample
        {
            Mass = masses, Position = pos, Velocity = vel, LaunchRadius = radius,
            Recoil = new Vec3(-px.Value, -py.Value, -pz.Value), Inner = inner, Outer = outer,
        };
    }

    /// <summary>
    /// Transient crater volume, Collins, Melosh &amp; Marcus (2005): a paraboloid of rim diameter D_tc and depth
    /// D_tc/(2√2), V = π D_tc³/(16√2). SI (m³ from m), like the rest of the impact model.
    /// </summary>
    public static double TransientCraterVolume(double transientDiameter) =>
        Math.PI * transientDiameter * transientDiameter * transientDiameter / (16 * Math.Sqrt(2));

    /// <summary>
    /// Ejecta mass when the impact report carries none: transient crater volume × target surface density, kg (SI in). An
    /// upper bound: part of that volume is pushed down rather than thrown out, and integrating Collins et al.'s own ejecta
    /// thickness law (t = D_tc⁴/(112 r³)) from the rim outward gives π D_tc³/28, 0.81 of it.
    /// </summary>
    public static double EjectaMassFromCrater(double transientDiameter, double targetDensity) => targetDensity * TransientCraterVolume(transientDiameter);
}
