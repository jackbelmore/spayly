using Spayly.Sim.Debris;
using static Spayly.Sim.Vec3;

namespace Spayly.Sim;

/// <summary>Which limit a tidal candidate crossed.</summary>
public enum TidalLimit
{
    /// <summary>Rock or ice: d = 2^(1/3) R_m (M/m)^(1/3) ≈ 1.26 R_M (ρ_M/ρ_m)^(1/3).</summary>
    RigidRoche,
    /// <summary>Gas, stars, molten rock: d = 2.44 R_m (M/m)^(1/3).</summary>
    FluidRoche,
    /// <summary>Any body near a black hole: r_t = R_* (M_BH/M_*)^(1/3).</summary>
    TidalRadius,
}

/// <summary>A body whose periapsis about a heavier one lies inside its tidal limit, found by <see cref="Tidal.TidalCheck"/>.</summary>
/// <param name="LimitKm">The limit crossed, km.</param>
/// <param name="PeriapsisKm">Two-body periapsis of the pair, km.</param>
/// <param name="DistanceKm">Centre distance when found, km.</param>
public readonly record struct TidalCandidate(Body Primary, Body Secondary, TidalLimit Limit, double LimitKm, double PeriapsisKm, double DistanceKm)
{
    /// <summary>Penetration factor β = limit / periapsis; at least 1 for a candidate.</summary>
    public double Beta => LimitKm / PeriapsisKm;
}

/// <summary>What survives a partial disruption: mass kg, radius km, position km and velocity km/s relative to the primary.</summary>
public readonly record struct TidalCore(double Mass, double Radius, Vec3 Position, Vec3 Velocity);

/// <summary>Relative residuals of the stream's Σ m r, Σ m v and Σ r × m v against the body it came from (double-double sums).</summary>
public readonly record struct TidalResiduals(double Position, double Momentum, double Angular);

/// <summary>
/// One tidal disruption from <see cref="Tidal.Disrupt"/>: the stream particles (relative to the primary), the surviving core
/// if any, and the numbers the fallback hook needs.
/// </summary>
public sealed class TidalDisruption
{
    public required TidalLimit Limit { get; init; }
    /// <summary>km.</summary>
    public required double LimitKm { get; init; }
    /// <summary>Two-body periapsis, km.</summary>
    public required double PeriapsisKm { get; init; }
    /// <summary>Centre distance at the disruption, km.</summary>
    public required double DistanceKm { get; init; }
    /// <summary>limit / periapsis.</summary>
    public required double Beta { get; init; }
    /// <summary>Fraction of the body's mass in the stream, 0..1 (1: full disruption).</summary>
    public required double StrippedFraction { get; init; }
    /// <summary>Frozen-in half-width of the energy spread, ΔE = G M_P R / d_limit², km²/s².</summary>
    public required double SpreadEnergy { get; init; }
    /// <summary>Specific energy of the body's centre in the primary's field, v²/2 − G M_P / d, km²/s².</summary>
    public required double CentreEnergy { get; init; }
    /// <summary>kg; equals Σ <see cref="Mass"/> exactly (and the body's whole mass when <see cref="Core"/> is null).</summary>
    public required double StreamMass { get; init; }
    /// <summary>Null for a full disruption.</summary>
    public required TidalCore? Core { get; init; }
    /// <summary>kg per particle; Σ is <see cref="StreamMass"/> exactly in any order.</summary>
    public required double[] Mass { get; init; }
    /// <summary>km, relative to the primary.</summary>
    public required Vec3[] Position { get; init; }
    /// <summary>km/s, relative to the primary.</summary>
    public required Vec3[] Velocity { get; init; }
    /// <summary>
    /// Stream mass bound to the primary, kg, which drives the fallback: Σ m of the particles with negative specific energy
    /// about the primary, or <see cref="LinearBoundMass"/> when there are none.
    /// </summary>
    public required double BoundMass { get; init; }
    /// <summary>
    /// Bound mass of the linear frozen-in spread ε_cm + ΔE x, kg. Exact for a star (R/d ~ 0.01); for a moon (R/d ~ 0.2) the
    /// second-order term −G M_P x²/d³ binds both sides more, and the particles' own energies are the better count.
    /// </summary>
    public required double LinearBoundMass { get; init; }
    /// <summary>Period of the most-bound debris (ε_cm − ΔE), s: when fallback starts. PositiveInfinity if nothing is bound.</summary>
    public required double ReturnTime { get; init; }
    /// <summary>n of Ṁ ∝ t^(−n): 5/3 for a full disruption, 9/4 for a partial one.</summary>
    public required double FallbackExponent { get; init; }
    public required TidalResiduals Residuals { get; init; }

    public int Count => Mass.Length;
    public bool Full => Core is null;
    /// <summary>Ṁ at the most-bound return, kg/s.</summary>
    public double PeakFallbackRate => Tidal.FallbackRate(ReturnTime, ReturnTime, BoundMass, FallbackExponent);
    /// <summary>Ṁ at <paramref name="t"/> s after the disruption, kg/s.</summary>
    public double FallbackRate(double t) => Tidal.FallbackRate(t, ReturnTime, BoundMass, FallbackExponent);
}

/// <summary>
/// Tidal disruption: a body whose periapsis lies inside its Roche limit about a planet or star, or inside its tidal radius
/// about a black hole, is torn into a stream of test particles. km, kg, s like the rest of Sim.
///
/// [SCI] Limits (docs/research/physics-and-visuals.md §3.3, checked): fluid Roche 2.44 R_m (M/m)^(1/3) (= 2.44 R_M (ρ_M/ρ_m)^(1/3));
/// rigid 2^(1/3) R_m (M/m)^(1/3) ≈ 1.26; TDE r_t = R_* (M_BH/M_*)^(1/3), torn outside the horizon only if r_t &gt; r_s, i.e.
/// below the Hills mass (R_* c²/2G)^(3/2) M_*^(−1/2) = 1.14e8 M☉ for the Sun (r_t is still 1.09 r_s at 10^8 M☉). The
/// report's alternative 5e7 is the relativistic version: a parabolic orbit is captured inside 2 r_s, which gives 4.0e7.
/// Which limit: a fluid (gas giant, star, molten rock) stretches into a prolate spheroid along the tidal axis, which
/// weakens its own grip at the tips, so it fails at 2.44; rock and ice keep their shape by shear strength, so only the
/// rigid sphere's 1.26 applies (surface material lifts off where the tidal pull beats surface gravity).
///
/// [SCI] Stream: frozen-in energy spread (Rees 1988; Stone, Sari &amp; Loeb 2013 fix it at the limit, not at periapsis):
/// material at radial offset x from the centre has ε = ε_cm + G M_P x / d_limit², so ΔE = G M_P R / d_limit² and, for a
/// parabolic star (ε_cm ≈ 0), half is bound. The same potential difference across the body sets a moon's spread. With a
/// flat dM/dE, Kepler's third law gives the fallback Ṁ ∝ t^(−5/3) (Phinney 1989) from the most-bound return time
/// t_min = 2π G M_P / (2|ε_cm − ΔE|)^(3/2): 41 d (0.11 yr) for the Sun at 10^6 M☉. The report's "0.03 M₆^(1/2) yr ≈ 11 d"
/// does not follow from its own ΔE (it needs a spread 2.4× larger). Partial disruptions fall as t^(−9/4) (Coughlin &amp;
/// Nixon 2019).
///
/// [GAME] How much comes off: a linear ramp in β = limit/periapsis from 10 % at the limit to all of it at β = 2, loosely
/// after Guillochon &amp; Ramirez-Ruiz (2013), whose mass loss climbs from a trace to total over about a factor two in β.
/// The surviving core keeps the centre's position and velocity, and its radius is set so that its own limit falls just
/// inside the periapsis (the part that fits inside its Roche lobe survives, denser than the whole was), so it is not
/// re-disrupted on the same orbit.
/// </summary>
public static class Tidal
{
    /// <summary>Heaviest bodies checked as primaries; the check is O(MaxPrimaries · n).</summary>
    public const int MaxPrimaries = 8;
    /// <summary>
    /// [GAME] Smallest body the check tears apart, km. Below ~50 km the rock's tensile strength (MPa) exceeds the central
    /// self-gravity pressure (2π/3)Gρ²R² (3 MPa at 50 km, ρ = 3000 kg/m³): tides crack a small body rather than stream it.
    /// </summary>
    public const double MinSecondaryRadiusKm = 50;
    /// <summary>[GAME] Fraction stripped at β = 1.</summary>
    public const double StripAtLimit = 0.1;
    /// <summary>[GAME] β at and beyond which the whole body goes.</summary>
    public const double BetaFull = 2;
    /// <summary>[GAME] A stripped fraction at or above this is a full disruption: no core.</summary>
    public const double FullFraction = 0.95;
    /// <summary>[GAME] The core's own limit sits this fraction of the periapsis, so it is not a candidate again.</summary>
    public const double CoreMargin = 0.99;
    /// <summary>
    /// [GAME] A partial disruption's stripped layers leave the core radially at this multiple of its escape speed from
    /// where they start (the passage's tidal impulse, which the frozen-in shear leaves out).
    /// </summary>
    public const double StripEscapeMargin = 1.05;
    /// <summary>Fewer particles than this and <see cref="Disrupt"/> samples none (the caller books the stream as dust).</summary>
    public const int MinParticles = 8;
    public const double FullFallbackExponent = 5.0 / 3;
    public const double PartialFallbackExponent = 9.0 / 4;

    /// <summary>Fluid bodies have no shear strength: gas giants, stars, molten rock.</summary>
    public static bool IsFluid(Body b) => b.Kind == BodyKind.Star || b.Surface is Surface.Gas or Surface.Star or Surface.Lava;

    /// <summary>Which limit applies to <paramref name="secondary"/> about <paramref name="primary"/>, and its value, km.</summary>
    public static (TidalLimit Kind, double Km) LimitFor(Body primary, Body secondary)
    {
        if (primary.Kind == BodyKind.BlackHole)
            return (TidalLimit.TidalRadius, BlackHole.TidalRadiusKm(primary.Mass, secondary.Mass, secondary.Radius));
        return IsFluid(secondary)
            ? (TidalLimit.FluidRoche, BlackHole.RocheLimitFluidKm(primary.Mass, secondary.Mass, secondary.Radius))
            : (TidalLimit.RigidRoche, BlackHole.RocheLimitRigidKm(primary.Mass, secondary.Mass, secondary.Radius));
    }

    /// <summary>
    /// Hills mass, kg: the black-hole mass whose r_s equals the tidal radius of a star of <paramref name="starMass"/> kg and
    /// <paramref name="starRadiusKm"/> km, (R c² / 2G)^(3/2) / √M_*. Heavier holes swallow the star whole.
    /// </summary>
    public static double HillsMassKg(double starMass, double starRadiusKm)
    {
        var k = starRadiusKm * BlackHole.SpeedOfLight * BlackHole.SpeedOfLight / (2 * Constants.G);
        return k * Math.Sqrt(k / starMass);
    }

    /// <summary>[GAME] Fraction of the body stripped at penetration β (see the class remarks).</summary>
    public static double StrippedFraction(double beta) =>
        beta >= BetaFull ? 1 : Math.Clamp(StripAtLimit + (1 - StripAtLimit) * (beta - 1) / (BetaFull - 1), StripAtLimit, 1);

    /// <summary>Periapsis of the two-body orbit through relative state (r km, v km/s) with μ km³/s², km: h²/(μ(1 + e)).</summary>
    public static double Periapsis(Vec3 r, Vec3 v, double mu)
    {
        var d = Length(r);
        var h = Cross(r, v);
        var e = Length(Cross(v, h) * (1 / mu) - r * (1 / d));
        return Dot(h, h) / (mu * (1 + e));
    }

    static bool CanDisrupt(Body b) => b.Kind != BodyKind.BlackHole && b.Radius >= MinSecondaryRadiusKm && b.Mass > 0;

    /// <summary>
    /// Pairs (primary, secondary) whose two-body periapsis lies inside the secondary's limit and outside contact (a body
    /// that will touch the primary first is Collide's), with the secondary inside the limit now or — when
    /// <paramref name="window"/> (s, the sim time since the previous check) is positive — possibly inside it during the
    /// window: outbound and closer to the limit than the fastest it can have moved since. Primaries are the
    /// <see cref="MaxPrimaries"/> heaviest bodies, each secondary is reported once (its deepest penetration). O(k·n); meant
    /// for every few frames, not every substep. <paramref name="into"/> is cleared and reused if given.
    /// </summary>
    public static List<TidalCandidate> TidalCheck(Universe u, double window = 0, List<TidalCandidate>? into = null)
    {
        var found = into ?? [];
        found.Clear();
        var bodies = u.Bodies;
        Span<int> heavy = stackalloc int[MaxPrimaries];
        var k = Heaviest(bodies, heavy);
        for (var j = 0; j < bodies.Count; j++)
        {
            var s = bodies[j];
            if (!CanDisrupt(s)) continue;
            TidalCandidate best = default;
            var any = false;
            for (var a = 0; a < k; a++)
            {
                var p = bodies[heavy[a]];
                if (ReferenceEquals(p, s) || !(p.Mass > s.Mass)) continue;
                if (Test(p, s, window, out var c) && (!any || c.Beta > best.Beta))
                {
                    best = c;
                    any = true;
                }
            }
            if (any) found.Add(best);
        }
        return found;
    }

    /// <summary>Indices of the heaviest bodies, heaviest first; returns how many were filled.</summary>
    static int Heaviest(List<Body> bodies, Span<int> into)
    {
        var n = 0;
        for (var i = 0; i < bodies.Count; i++)
        {
            var m = bodies[i].Mass;
            if (n == into.Length && m <= bodies[into[n - 1]].Mass) continue;
            var at = n < into.Length ? n++ : n - 1;
            while (at > 0 && bodies[into[at - 1]].Mass < m)
            {
                into[at] = into[at - 1];
                at--;
            }
            into[at] = i;
        }
        return n;
    }

    static bool Test(Body p, Body s, double window, out TidalCandidate c)
    {
        c = default;
        var (kind, limit) = LimitFor(p, s);
        var contact = PseudoNewtonian.ContactRadius(p) + s.Radius;
        // A limit inside contact (the Earth about the Sun, a star about a hole above the Hills mass) cannot be reached.
        if (!(limit > contact)) return false;
        var r = s.Pos - p.Pos;
        var v = s.Vel - p.Vel;
        var d = Length(r);
        var mu = Constants.G * (p.Mass + s.Mass);
        if (d > limit)
        {
            if (!(window > 0) || Dot(r, v) <= 0) return false;
            // Energy bounds the speed outside the limit by √(v² + 2μ/limit): farther out than that times the window, it
            // cannot have been inside since the last check.
            if (d - limit > Math.Sqrt(Dot(v, v) + 2 * mu / (limit - PseudoNewtonian.HoleRs(p))) * window) return false;
        }
        var rp = PseudoNewtonian.Periapsis(r, v, mu, PseudoNewtonian.HoleRs(p));
        if (!(rp < limit)) return false;
        if (!(rp > contact))
        {
            // Headed into contact. A body falling onto a planet hits it first (the collision is the event); one plunging into a
            // black hole crosses its tidal radius on the way and is torn apart there, at β of at least limit/contact.
            if (p.Kind != BodyKind.BlackHole || !(d <= limit)) return false;
            rp = contact;
        }
        c = new TidalCandidate(p, s, kind, limit, rp, d);
        return true;
    }

    /// <summary>
    /// Ṁ (kg/s) of debris returning to periapsis <paramref name="t"/> s after the disruption: 0 before the most-bound return
    /// <paramref name="returnTime"/>, then (n − 1) M_b / t_min · (t / t_min)^(−n), whose integral from t_min is
    /// <paramref name="boundMass"/> exactly.
    /// </summary>
    public static double FallbackRate(double t, double returnTime, double boundMass, double exponent = FullFallbackExponent)
    {
        if (!(t >= returnTime) || !double.IsFinite(returnTime) || !(returnTime > 0) || !(boundMass > 0)) return 0;
        return (exponent - 1) * boundMass / returnTime * Math.Pow(t / returnTime, -exponent);
    }

    /// <summary>
    /// Make <paramref name="b"/> the core of a partial disruption once its mass is the core's (the caller applies the mass,
    /// through the debris ledger in the game): its radius, and the catalogue values, which Collide likewise rewrites.
    /// </summary>
    public static void ShrinkToCore(Body b, double radiusKm)
    {
        b.Radius = radiusKm;
        b.Mass0 = b.Mass;
        b.Radius0 = radiusKm;
        b.Damage.Version++;
    }

    // ---------- the stream ----------

    static double Q(double x) => x - x * x * x / 3;

    /// <summary>x in [0, 1] with Q(x) = y (0 ≤ y ≤ 2/3): the trigonometric root of x³ − 3x + 3y = 0, x = 2 sin(asin(3y/2)/3).</summary>
    static double InverseQ(double y) => 2 * Math.Sin(Math.Asin(Math.Clamp(1.5 * y, 0, 1)) / 3);

    /// <summary>
    /// ∫ from <paramref name="xc"/> to <paramref name="x"/> of the stripped region's cross-section over π, in body radii: the
    /// sphere's 1 − x² less the core sphere's max(0, c² − x²). The region is the two caps |x| ≥ xc outside the core.
    /// </summary>
    static double RegionIntegral(double xc, double x, double c)
    {
        var outer = Q(x) - Q(xc);
        if (xc >= c) return outer;
        var hi = Math.Min(x, c);
        return outer - ((c * c * hi - hi * hi * hi / 3) - (c * c * xc - xc * xc * xc / 3));
    }

    /// <summary>
    /// The cut xc (body radii) at which the caps |x| ≥ xc outside a core of radius <paramref name="c"/> hold fraction
    /// <paramref name="f"/> of the body's volume (1.5 × RegionIntegral(xc, 1, c)); 0 when f takes everything outside the core.
    /// </summary>
    public static double CapCut(double f, double c)
    {
        if (f >= 1 - c * c * c) return 0;
        double lo = 0, hi = 1;
        for (var i = 0; i < 80; i++)
        {
            var mid = 0.5 * (lo + hi);
            if (1.5 * RegionIntegral(mid, 1, c) > f) lo = mid;
            else hi = mid;
        }
        return 0.5 * (lo + hi);
    }

    /// <summary>|x| at cumulative fraction u of the region's mass (inverse of RegionIntegral, piecewise closed form).</summary>
    static double RegionX(double u, double xc, double c, double total)
    {
        var target = u * total;
        if (xc < c)
        {
            // Across the core the cross-section is the annulus 1 − c², constant in x.
            var flat = (1 - c * c) * (c - xc);
            if (target <= flat) return xc + target / (1 - c * c);
            return Math.Min(1, InverseQ(Q(c) + (target - flat)));
        }
        return Math.Min(1, InverseQ(Q(xc) + target));
    }

    /// <summary>Fraction of the region's mass at signed radial offset below <paramref name="xb"/> (body radii); symmetric in x.</summary>
    static double FractionBelow(double xb, double xc, double c, double total)
    {
        if (xb <= -1) return 0;
        if (xb >= 1) return 1;
        var a = Math.Abs(xb);
        var half = a <= xc ? 0 : 0.5 * RegionIntegral(xc, a, c) / total;
        return xb >= 0 ? 0.5 + half : 0.5 - half;
    }

    /// <summary>
    /// Tear <paramref name="secondary"/> apart about <paramref name="primary"/> at their current states. Returns the core
    /// (partial, [GAME]) and <paramref name="particleCount"/> (rounded down to even; none below <see cref="MinParticles"/>)
    /// stream particles relative to the primary:
    /// <list type="bullet">
    /// <item>Positions: antipodal pairs sampled uniformly from the stripped part of the body's volume — all of it, or the
    /// two tidal caps outside the core — stratified in |x| along the radial direction.</item>
    /// <item>Velocities: the centre's plus a symmetric shear u = A s with V·(A s) = k (r̂·s), k = G M_P (1/d_limit² − 1/d²),
    /// which tops the positional potential difference G M_P x/d² up to the frozen-in G M_P x/d_limit² whatever distance the
    /// check caught the body at. A symmetric gradient carries no spin for an isotropic sample. After a partial disruption
    /// each particle also leaves the core radially at <see cref="StripEscapeMargin"/> × its escape speed [GAME].</item>
    /// <item>Conservation: masses split exactly; the cloud is translated onto the centre, given a uniform velocity and a
    /// rigid rotation that cancel the sampling noise in Σ m v and Σ r × m v (double-double sums), and the last particle is
    /// the exact difference in Σ m r and Σ m v. Stream plus core then carries the body's m exactly and its Σ m r, Σ m v,
    /// Σ r × m v to the rounding of that last particle (<see cref="TidalDisruption.Residuals"/>).</item>
    /// </list>
    /// </summary>
    public static TidalDisruption Disrupt(Body primary, Body secondary, int particleCount, Func<double>? rng = null)
    {
        rng ??= Random.Shared.NextDouble;
        var n = particleCount >= MinParticles ? particleCount & ~1 : 0;
        var rel = secondary.Pos - primary.Pos;
        var vel = secondary.Vel - primary.Vel;
        var d = Length(rel);
        var (kind, limit) = LimitFor(primary, secondary);
        var ms = secondary.Mass;
        var rs = secondary.Radius;
        // Near a black hole the potential is Paczyński–Wiita's (PseudoNewtonian): the periapsis, the tidal spread dΦ/dr·R*,
        // the shear and every orbital energy below use −GM/(d − r_s), which is Newton's where r_s = 0.
        var hrs = PseudoNewtonian.HoleRs(primary);
        var rp = PseudoNewtonian.Periapsis(rel, vel, Constants.G * (primary.Mass + ms), hrs);
        var beta = rp > 0 ? limit / rp : double.PositiveInfinity;
        var fs = StrippedFraction(beta);
        var full = fs >= FullFraction;
        double streamMass, coreMass;
        // Sterbenz: whichever part is at least half the body is rounded, the other is its exact difference, so the two
        // sum to the body's mass exactly.
        if (full)
        {
            streamMass = ms;
            coreMass = 0;
        }
        else if (fs <= 0.5)
        {
            coreMass = (1 - fs) * ms;
            streamMass = ms - coreMass;
        }
        else
        {
            streamMass = fs * ms;
            coreMass = ms - streamMass;
        }
        // c R is the sphere whose own limit, c R (M/m_core)^(1/3) scaled, would sit exactly at the periapsis. The stream is
        // sampled outside it and the core is CoreMargin of it, so its limit lands inside the periapsis and no particle
        // starts within 1 % of its surface (the GPU tier absorbs anything that starts inside a body).
        var c = full ? 0 : Math.Min(1, rp / limit) * Math.Cbrt(coreMass / ms);
        var xc = full ? 0 : CapCut(streamMass / ms, c);
        var total = RegionIntegral(xc, 1, c);

        var gm = Constants.G * primary.Mass;
        var spread = gm * rs / ((limit - hrs) * (limit - hrs));
        var epsCm = 0.5 * Dot(vel, vel) - gm / (d - hrs);
        var rhat = rel * (1 / d);
        var vlen = Length(vel);
        var vhat = vlen > 0 ? vel * (1 / vlen) : Vec3.Zero;
        var rv = Dot(rhat, vhat);
        var shear = vlen > 0 ? gm * (1 / ((limit - hrs) * (limit - hrs)) - 1 / ((d - hrs) * (d - hrs))) / vlen : 0;
        // Without a kick the stripped layers start at rest on the core, whose gravity takes every one of them back within
        // hours (a capture had the Moon re-absorb 44,385 of 50,000 at β 1.078, then keep the core's smaller radius). ±ŝ on
        // an antipodal pair adds no momentum and no spin; Balance absorbs what the pair's unequal masses leave.
        var gmCore = full ? 0 : Constants.G * coreMass;
        var e1 = Normalize(AnyPerpendicular(rhat));
        var e2 = Cross(rhat, e1);

        var masses = n > 0 ? EjectaSampler.SplitMassExactly(streamMass, n) : [];
        var pos = new Vec3[n];
        var vels = new Vec3[n];
        var pairs = n / 2;
        for (var j = 0; j < pairs; j++)
        {
            var x = RegionX((j + rng()) / pairs, xc, c, total);
            var rin2 = Math.Max(0, c * c - x * x);
            var rout2 = Math.Max(0, 1 - x * x);
            var rho = Math.Sqrt(rin2 + rng() * (rout2 - rin2));
            var (sn, cs) = Math.SinCos(2 * Math.PI * rng());
            var s = (rhat * x + e1 * (rho * cs) + e2 * (rho * sn)) * rs;
            // u = A s, A = (k/v)[r̂ v̂ᵀ + v̂ r̂ᵀ − (r̂·v̂) v̂ v̂ᵀ]: symmetric, and v̂ᵀA = (k/v) r̂ᵀ.
            var sv = Dot(vhat, s);
            var u = (rhat * sv + vhat * (Dot(rhat, s) - rv * sv)) * shear;
            if (gmCore > 0)
            {
                var sl = Length(s);
                u += s * (StripEscapeMargin * Math.Sqrt(2 * gmCore / sl) / sl);
            }
            pos[2 * j] = rel + s;
            vels[2 * j] = vel + u;
            pos[2 * j + 1] = rel - s;
            vels[2 * j + 1] = vel - u;
        }
        var residuals = n > 0 ? Balance(masses, pos, vels, streamMass, rel, vel, rs) : new TidalResiduals(0, 0, 0);

        double sampledBound = 0;
        for (var i = 0; i < n; i++)
            if (0.5 * Dot(vels[i], vels[i]) - gm / (Length(pos[i]) - hrs) < 0) sampledBound += masses[i];
        var epsMostBound = epsCm - spread;
        var returnTime = double.PositiveInfinity;
        if (epsMostBound < 0)
        {
            // Kepler's period for the energy: the most-bound debris spends its orbit far out, where PW is Newton to r_s/a.
            var a = gm / (-2 * epsMostBound);
            returnTime = 2 * Math.PI * Math.Sqrt(a * a * a / gm);
        }
        var linear = streamMass * FractionBelow(spread > 0 ? -epsCm / spread : (epsCm < 0 ? 1 : -1), xc, c, total);

        return new TidalDisruption
        {
            Limit = kind, LimitKm = limit, PeriapsisKm = rp, DistanceKm = d, Beta = beta, StrippedFraction = streamMass / ms,
            SpreadEnergy = spread, CentreEnergy = epsCm, StreamMass = streamMass,
            Core = full ? null : new TidalCore(coreMass, CoreMargin * c * rs, rel, vel),
            Mass = masses, Position = pos, Velocity = vels, BoundMass = n > 0 ? sampledBound : linear, LinearBoundMass = linear,
            ReturnTime = returnTime, FallbackExponent = full ? FullFallbackExponent : PartialFallbackExponent,
            Residuals = residuals,
        };
    }

    /// <summary>
    /// Make the cloud carry Σ m r = M R, Σ m v = M V and Σ r × m v = R × M V: translate it onto R, add a uniform velocity
    /// and a rigid rotation about R that cancel the momentum and angular-momentum noise, then set the last particle's
    /// position and velocity to the exact (double-double) difference. Two double-double passes; returns the residuals.
    /// </summary>
    static TidalResiduals Balance(double[] m, Vec3[] r, Vec3[] v, double total, Vec3 rel, Vec3 vel, double scale)
    {
        var n = m.Length;
        var xt = DV.Product(total, rel);
        var pt = DV.Product(total, vel);
        var lt = DV.Cross(rel, pt);

        var x = DV.Zero;
        var p = DV.Zero;
        var l = DV.Zero;
        // Inertia about R in units of total × scale², so its determinant stays O(1) whatever the body.
        double ixx = 0, iyy = 0, izz = 0, ixy = 0, ixz = 0, iyz = 0;
        for (var i = 0; i < n; i++)
        {
            var pi = DV.Product(m[i], v[i]);
            x += DV.Product(m[i], r[i]);
            p += pi;
            l += DV.Cross(r[i], pi);
            var s = (r[i] - rel) / scale;
            var w = m[i] / total;
            ixx += w * (s.Y * s.Y + s.Z * s.Z);
            iyy += w * (s.X * s.X + s.Z * s.Z);
            izz += w * (s.X * s.X + s.Y * s.Y);
            ixy -= w * s.X * s.Y;
            ixz -= w * s.X * s.Z;
            iyz -= w * s.Y * s.Z;
        }
        // Translating every r by δ adds δ × P to L; the translation is sub-metre, so it leaves the inertia alone.
        var shift = (xt - x).Value / total;
        l += DV.Cross(shift, p);
        var a = (pt - p).Value / total;
        // L after v += a + ω × (r − R) is L + M R × a + I ω.
        var b = ((lt - l).Value - Cross(rel, a) * total) / (total * scale * scale);
        var a11 = iyy * izz - iyz * iyz;
        var a12 = ixz * iyz - ixy * izz;
        var a13 = ixy * iyz - ixz * iyy;
        var a22 = ixx * izz - ixz * ixz;
        var a23 = ixy * ixz - ixx * iyz;
        var a33 = ixx * iyy - ixy * ixy;
        var det = ixx * a11 + ixy * a12 + ixz * a13;
        var omega = det > 1e-12 && double.IsFinite(det)
            ? new Vec3(a11 * b.X + a12 * b.Y + a13 * b.Z, a12 * b.X + a22 * b.Y + a23 * b.Z, a13 * b.X + a23 * b.Y + a33 * b.Z) / det
            : Vec3.Zero;

        var xr = DV.Zero;
        var pr = DV.Zero;
        var lr = DV.Zero;
        for (var i = 0; i < n - 1; i++)
        {
            r[i] += shift;
            v[i] += a + Cross(omega, r[i] - rel);
            var pi = DV.Product(m[i], v[i]);
            xr += DV.Product(m[i], r[i]);
            pr += pi;
            lr += DV.Cross(r[i], pi);
        }
        var last = n - 1;
        r[last] = (xt - xr).Value / m[last];
        v[last] = (pt - pr).Value / m[last];
        var pl = DV.Product(m[last], v[last]);
        var dr = Math.Max(Length(rel), double.Epsilon);
        var dv = Math.Max(Length(vel), double.Epsilon);
        return new TidalResiduals(
            (xr + DV.Product(m[last], r[last]) - xt).Length / (total * dr),
            (pr + pl - pt).Length / (total * dv),
            (lr + DV.Cross(r[last], pl) - lt).Length / (total * dr * dv));
    }
}
