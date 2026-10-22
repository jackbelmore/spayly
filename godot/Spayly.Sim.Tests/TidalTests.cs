using Spayly.Sim.Debris;
using static Spayly.Sim.Constants;
using static Spayly.Sim.Vec3;

namespace Spayly.Sim.Tests;

// No vitest file: tidal disruption exists only in the Godot port. Facts, in order:
//   MoonRocheLimitAtTheEarthIsTheTextbookValue, SunTidalRadiusAtAMillionSolarMassesIs100SolarRadii,
//   ASunLikeStarIsSwallowedWholeAboveTheHillsMass, DisruptConservesMassMomentumAndAngularMomentumExactly,
//   AFullTdeIsHalfBoundHalfUnboundWithTheFrozenInSpread, DisruptIsDeterministicPerSeed,
//   TidalCheckFindsAMoonInsideRocheAndIgnoresOneOutside, TidalCheckCatchesAPeriapsisPassageBetweenChecks,
//   TheCataloguedSolarSystemHasNoCandidates, FallbackRateFallsAsTMinusFiveThirdsAndIntegratesToTheBoundMass,
//   APartialDisruptionLeavesACoreOutsideTheStreamThatIsNoLongerACandidate
// Reference numbers come from outside the code under test: the Roche limit from the densities, r_t = 100 R☉ and the
// Hills mass from R☉ and c, the most-bound return time from its closed form (π/√2)(M_BH/M_*)^(1/2)(R_*³/GM_*)^(1/2).
public class TidalTests
{
    private const double EarthMass = 5.9722e24;
    private const double MoonMass = 7.342e22;
    private const double EarthRadius = 6371;
    private const double MoonRadius = 1737.4;
    private static readonly BodyVisual Vis = new() { Color = "#fff", Albedo = 0.1 };

    private static Body Make(string id, BodyKind kind, Surface surface, double mass, double radius, Vec3? pos = null, Vec3? vel = null) =>
        Body.MakeBody(new BodySpec
        {
            Id = id, Name = id, Kind = kind, Surface = surface, Mass = mass, Radius = radius, Pos = pos, Vel = vel, Visual = Vis, Seed = 11,
        });

    private static Body Earth(Vec3? pos = null, Vec3? vel = null) => Make("earth", BodyKind.Planet, Surface.Rock, EarthMass, EarthRadius, pos, vel);

    private static Body Moon(string id = "moon") => Make(id, BodyKind.Moon, Surface.Rock, MoonMass, MoonRadius);

    private static Body Hole(double solar, Vec3? pos = null, Vec3? vel = null) => Make("bh", BodyKind.BlackHole, Surface.Star, solar * SunMass, 0, pos, vel);

    private static Body Star() => Make("star", BodyKind.Star, Surface.Star, SunMass, SunRadius);

    /// <summary>
    /// Relative state at distance <paramref name="r"/> on the conic of periapsis <paramref name="rp"/> and eccentricity
    /// <paramref name="e"/>, on +x (rotated by <paramref name="turn"/> about z), in the +z-angular-momentum sense.
    /// </summary>
    private static (Vec3 Pos, Vec3 Vel) OnConic(double mu, double rp, double e, double r, bool inbound, double turn = 0)
    {
        var p = rp * (1 + e);
        var h = Math.Sqrt(mu * p);
        var v2 = mu * (2 / r - (1 - e * e) / p);
        var vt = h / r;
        var vr = Math.Sqrt(Math.Max(0, v2 - vt * vt)) * (inbound ? -1 : 1);
        var (sn, cs) = Math.SinCos(turn);
        return (new Vec3(r * cs, r * sn, 0), new Vec3(vr * cs - vt * sn, vr * sn + vt * cs, 0));
    }

    private static void Place(Body primary, Body secondary, (Vec3 Pos, Vec3 Vel) rel)
    {
        secondary.Pos = primary.Pos + rel.Pos;
        secondary.Vel = primary.Vel + rel.Vel;
    }

    private static Universe With(params Body[] bodies)
    {
        var u = new Universe(J2000Jd);
        u.SetBodies([.. bodies]);
        return u;
    }

    /// <summary>A Sun-like star just inside r_t, inbound on the parabola of penetration β, about a hole (optionally moving, off the origin).</summary>
    /// <summary>
    /// Relative state at distance <paramref name="r"/> on the zero-energy orbit of the Paczyński–Wiita potential −μ/(r − r_s)
    /// with periapsis <paramref name="rp"/>: v² = 2μ/(r − r_s), angular momentum rp·v(rp). With r_s = 0 it is OnConic's parabola.
    /// </summary>
    private static (Vec3 Pos, Vec3 Vel) OnPwParabola(double mu, double rs, double rp, double r, double turn)
    {
        var h = rp * Math.Sqrt(2 * mu / (rp - rs));
        var v2 = 2 * mu / (r - rs);
        var vt = h / r;
        var vr = -Math.Sqrt(Math.Max(0, v2 - vt * vt));
        var (sn, cs) = Math.SinCos(turn);
        return (new Vec3(r * cs, r * sn, 0), new Vec3(vr * cs - vt * sn, vr * sn + vt * cs, 0));
    }

    /// <summary>A Sun-like star inbound at 0.999 r_t on the zero-energy (PW) orbit whose periapsis is r_t/β: the classic TDE.</summary>
    private static (Body Hole, Body Star) Tde(double holeSolar, double beta, bool offOrigin = false)
    {
        var bh = offOrigin ? Hole(holeSolar, new Vec3(3.1e9, -1.7e9, 4.2e8), new Vec3(41, -12, 3)) : Hole(holeSolar);
        var star = Star();
        var rt = BlackHole.TidalRadiusKm(bh.Mass, star.Mass, star.Radius);
        Place(bh, star, OnPwParabola(G * (bh.Mass + star.Mass), PseudoNewtonian.HoleRs(bh), rt / beta, 0.999 * rt, turn: 0.7));
        return (bh, star);
    }

    /// <summary>The Moon at 9,000 km from the Earth, inbound on an orbit from 56,000 km down to 8,800 km.</summary>
    private static (Body Earth, Body Moon) MoonPlunge(bool offOrigin = false)
    {
        var earth = offOrigin ? Earth(new Vec3(AU, 2.5e6, -1.1e4), new Vec3(-0.4, 29.8, 0.001)) : Earth();
        var moon = Moon();
        const double rp = 8800, ra = 56000;
        Place(earth, moon, OnConic(G * (EarthMass + MoonMass), rp, (ra - rp) / (ra + rp), 9000, inbound: true, turn: -0.3));
        return (earth, moon);
    }

    [Fact]
    public void MoonRocheLimitAtTheEarthIsTheTextbookValue()
    {
        var earth = Earth();
        var moon = Moon();
        var ratio = Math.Cbrt(Body.Density(earth) / Body.Density(moon));
        var fluid = BlackHole.RocheLimitFluidKm(EarthMass, MoonMass, MoonRadius);
        // d = 2.44 R_M (ρ_M/ρ_m)^(1/3), from the densities rather than the mass ratio the code uses.
        Assert.True(Math.Abs(fluid / (2.44 * EarthRadius * ratio) - 1) < 1e-12, $"fluid {fluid} km vs 2.44 R_E (ρ_E/ρ_M)^(1/3) = {2.44 * EarthRadius * ratio} km");
        Assert.True(Math.Abs(fluid / 18400 - 1) < 0.01, $"fluid Roche limit {fluid:F0} km, textbook ≈ 18,400 km");
        // The Moon is rock, so the check uses the rigid limit, 1.26 R_E (ρ_E/ρ_M)^(1/3) ≈ 9,480 km.
        var (kind, limit) = Tidal.LimitFor(earth, moon);
        Assert.Equal(TidalLimit.RigidRoche, kind);
        Assert.True(Math.Abs(limit / (1.26 * EarthRadius * ratio) - 1) < 1e-3, $"rigid {limit} km vs 1.26 R_E (ρ_E/ρ_M)^(1/3)");
        Assert.True(Math.Abs(limit - 9484) < 5, $"rigid Roche limit {limit:F1} km");
        // Gas and molten rock are fluid; anything about a black hole uses r_t.
        Assert.Equal(TidalLimit.FluidRoche, Tidal.LimitFor(Star(), Make("jup", BodyKind.Planet, Surface.Gas, 1.898e27, 69911)).Kind);
        Assert.Equal(TidalLimit.FluidRoche, Tidal.LimitFor(earth, Make("melt", BodyKind.Fragment, Surface.Lava, 1e20, 200)).Kind);
        Assert.Equal(TidalLimit.TidalRadius, Tidal.LimitFor(Hole(10), moon).Kind);
    }

    [Fact]
    public void SunTidalRadiusAtAMillionSolarMassesIs100SolarRadii()
    {
        var (kind, rt) = Tidal.LimitFor(Hole(1e6), Star());
        Assert.Equal(TidalLimit.TidalRadius, kind);
        Close.To(100, rt / SunRadius, 9);
        Close.To(0.465, rt / AU, 3);
    }

    [Fact]
    public void ASunLikeStarIsSwallowedWholeAboveTheHillsMass()
    {
        var hills = Tidal.HillsMassKg(SunMass, SunRadius) / SunMass;
        Assert.True(Math.Abs(hills / 1.14e8 - 1) < 0.01, $"Hills mass {hills:G4} Msun, expected ≈ 1.14e8");
        double Rt(double solar) => BlackHole.TidalRadiusKm(solar * SunMass, SunMass, SunRadius);
        double Rs(double solar) => BlackHole.SchwarzschildRadiusKm(solar * SunMass);
        // 10^8 is still (just) below the crossover: r_t = 1.09 r_s.
        Assert.True(Rt(1e8) > Rs(1e8), $"at 1e8 Msun r_t {Rt(1e8):G4} km should exceed r_s {Rs(1e8):G4} km");
        Assert.True(Rt(0.99 * hills) > Rs(0.99 * hills), "just below the Hills mass the star is torn outside the horizon");
        Assert.True(Rt(1.01 * hills) < Rs(1.01 * hills), "just above it the tidal radius is inside the horizon");
        Assert.True(Rt(1e9) < Rs(1e9), "a 1e9 Msun hole swallows the Sun whole");

        // The check agrees: the same plunge is a candidate at 10^6 and left to Collide (swallowed) at 2e8.
        var (bh6, star6) = Tde(1e6, 2);
        Assert.Single(Tidal.TidalCheck(With(bh6, star6)));
        var (bh8, star8) = Tde(2e8, 2);
        Assert.Empty(Tidal.TidalCheck(With(bh8, star8)));
    }

    /// <summary>Σ m, Σ m r, Σ m v and Σ r × m v of the stream plus the core, in double-double, in the given frame offset.</summary>
    private static (DD M, DV X, DV P, DV L) Totals(TidalDisruption d, Vec3 at, Vec3 moving)
    {
        var m = DD.Zero;
        var x = DV.Zero;
        var p = DV.Zero;
        var l = DV.Zero;
        void Add(double mi, Vec3 r, Vec3 v)
        {
            var pi = DV.Product(mi, v);
            m += mi;
            x += DV.Product(mi, r);
            p += pi;
            l += DV.Cross(r, pi);
        }
        for (var i = 0; i < d.Count; i++) Add(d.Mass[i], at + d.Position[i], moving + d.Velocity[i]);
        if (d.Core is TidalCore c) Add(c.Mass, at + c.Position, moving + c.Velocity);
        return (m, x, p, l);
    }

    [Fact]
    public void DisruptConservesMassMomentumAndAngularMomentumExactly()
    {
        var (bh, star) = Tde(1e6, 2, offOrigin: true);
        var (earth, moon) = MoonPlunge(offOrigin: true);
        foreach (var (primary, secondary, full) in new[] { (bh, star, true), (earth, moon, false) })
        {
            var d = Tidal.Disrupt(primary, secondary, 20_000, Rng.Mulberry32(5));
            Assert.Equal(full, d.Full);
            Assert.Equal(20_000, d.Count);
            // Relative to the primary, where the construction works, and in the world frame the game books in.
            foreach (var world in new[] { false, true })
            {
                var at = world ? primary.Pos : Vec3.Zero;
                var moving = world ? primary.Vel : Vec3.Zero;
                var (m, x, p, l) = Totals(d, at, moving);
                var r = secondary.Pos - (world ? Vec3.Zero : primary.Pos);
                var v = secondary.Vel - (world ? Vec3.Zero : primary.Vel);
                var ms = secondary.Mass;
                var dm = m - ms;
                Assert.True(dm.Hi == 0 && dm.Lo == 0, $"{secondary.Id}: Σm − M = {dm.Value:G3} kg");
                var px = DV.Product(ms, r);
                var pp = DV.Product(ms, v);
                var pl = DV.Cross(r, pp);
                var ex = (x - px).Length / (ms * Length(r));
                var ep = (p - pp).Length / (ms * Length(v));
                var el = (l - pl).Length / (ms * Length(r) * Length(v));
                var frame = world ? "world" : "primary";
                Assert.True(ex < 1e-12, $"{secondary.Id} ({frame}): |Σmr − MR| / M|R| = {ex:G3}");
                Assert.True(ep < 1e-12, $"{secondary.Id} ({frame}): |Σmv − MV| / M|V| = {ep:G3}");
                Assert.True(el < 1e-12, $"{secondary.Id} ({frame}): |Σ r×mv − R×MV| / M|R||V| = {el:G3}");
            }
            Assert.True(d.Residuals.Momentum < 1e-14 && d.Residuals.Angular < 1e-14 && d.Residuals.Position < 1e-14, $"{d.Residuals}");
        }
    }

    [Fact]
    public void AFullTdeIsHalfBoundHalfUnboundWithTheFrozenInSpread()
    {
        var (bh, star) = Tde(1e6, 2);
        var d = Tidal.Disrupt(bh, star, 20_000, Rng.Mulberry32(9));
        Assert.True(d.Full, $"β = {d.Beta} should disrupt the star fully");
        var sampled = d.BoundMass / d.StreamMass;
        var analytic = d.LinearBoundMass / d.StreamMass;
        Assert.True(Math.Abs(sampled - 0.5) < 0.05, $"sampled bound fraction {sampled:F4}");
        Assert.True(Math.Abs(analytic - 0.5) < 0.05, $"analytic bound fraction {analytic:F4}");
        // ΔE = R_* dΦ/dr at r_t = G M_BH R_* / (r_t − r_s)² in the Paczyński–Wiita potential (G M R_*/r_t² in Newton's; r_t is
        // 23.6 r_s here, so they differ by 9 %), and the most-bound particle sits near ε_cm − ΔE.
        var rt = BlackHole.TidalRadiusKm(bh.Mass, star.Mass, star.Radius);
        var hrs = PseudoNewtonian.HoleRs(bh);
        Close.To(1, d.SpreadEnergy / (G * bh.Mass * SunRadius / ((rt - hrs) * (rt - hrs))), 12);
        var gm = G * bh.Mass;
        var minEps = double.PositiveInfinity;
        for (var i = 0; i < d.Count; i++) minEps = Math.Min(minEps, 0.5 * Dot(d.Velocity[i], d.Velocity[i]) - gm / (Length(d.Position[i]) - hrs));
        Assert.True(Math.Abs((minEps - d.CentreEnergy) / -d.SpreadEnergy - 1) < 0.03, $"most bound ε {minEps:G4} vs ε_cm − ΔE = {d.CentreEnergy - d.SpreadEnergy:G4} km²/s²");
        // t_min = (π/√2)(M_BH/M_*)^(1/2)(R_*³/GM_*)^(1/2): 41 days, 0.11 yr, for the Sun at 10^6 Msun under Newton. t ∝ ΔE^(−3/2),
        // so PW's larger spread shortens it by ((r_t − r_s)/r_t)³ = 0.87.
        var pw = Math.Pow((rt - hrs) / rt, 3);
        var tMin = pw * Math.PI / Math.Sqrt(2) * Math.Sqrt(bh.Mass / SunMass) * Math.Sqrt(Math.Pow(SunRadius, 3) / (G * SunMass));
        Assert.True(Math.Abs(d.ReturnTime / tMin - 1) < 0.01, $"return time {d.ReturnTime / Day:F2} d vs closed form {tMin / Day:F2} d");
        Assert.True(Math.Abs(d.ReturnTime / (pw * 0.112 * Year) - 1) < 0.02, $"return time {d.ReturnTime / Year:F4} yr");
        Assert.Equal(Tidal.FullFallbackExponent, d.FallbackExponent);
    }

    [Fact]
    public void AStarPlungingIntoAHoleIsTornApartButAMoonHittingEarthIsLeftToTheCollision()
    {
        // Straight in from 0.8 r_t: the PW periapsis is a plunge, inside contact. A hole still tears the star apart on the way
        // (β = limit/contact); a planet leaves the impact to Collide.
        var bh = Hole(1e6);
        var star = Star();
        var rt = BlackHole.TidalRadiusKm(bh.Mass, star.Mass, star.Radius);
        star.Pos = new Vec3(0.8 * rt, 0, 0);
        star.Vel = new Vec3(-Math.Sqrt(2 * G * bh.Mass / (0.8 * rt)), 0, 0);
        var u = new Universe(J2000Jd);
        u.SetBodies([bh, star]);
        var hits = Tidal.TidalCheck(u, 100);
        Assert.Single(hits);
        var contact = PseudoNewtonian.ContactRadius(bh) + star.Radius;
        Close.To(rt / contact, hits[0].LimitKm / hits[0].PeriapsisKm, 12);
        Assert.True(Tidal.Disrupt(bh, star, 0).Full);

        var earth = Earth();
        var moon = Moon();
        moon.Pos = new Vec3(9000, 0, 0);
        moon.Vel = new Vec3(-3, 0, 0);
        u.SetBodies([earth, moon]);
        Assert.Empty(Tidal.TidalCheck(u, 100));
    }

    [Fact]
    public void DisruptIsDeterministicPerSeed()
    {
        var (bh, star) = Tde(1e6, 2);
        var a = Tidal.Disrupt(bh, star, 4096, Rng.Mulberry32(77));
        var b = Tidal.Disrupt(bh, star, 4096, Rng.Mulberry32(77));
        var c = Tidal.Disrupt(bh, star, 4096, Rng.Mulberry32(78));
        Assert.Equal(a.Mass, b.Mass);
        Assert.Equal(a.Position, b.Position);
        Assert.Equal(a.Velocity, b.Velocity);
        Assert.NotEqual(a.Position, c.Position);
    }

    [Fact]
    public void TidalCheckFindsAMoonInsideRocheAndIgnoresOneOutside()
    {
        var mu = G * (EarthMass + MoonMass);
        var (earth, inside) = MoonPlunge();
        var outside = Moon("outside");
        Place(earth, outside, OnConic(mu, 30000, 0, 30000, inbound: false, turn: 2));
        // Inside the limit too, but headed for the surface: that one is Collide's, not a tidal disruption.
        var crashing = Moon("crashing");
        Place(earth, crashing, OnConic(mu, 5000, 0.8, 9000, inbound: true, turn: 3.5));
        var found = Tidal.TidalCheck(With(earth, inside, outside, crashing));
        var c = Assert.Single(found);
        Assert.Same(inside, c.Secondary);
        Assert.Same(earth, c.Primary);
        Assert.Equal(TidalLimit.RigidRoche, c.Limit);
        Assert.True(Math.Abs(c.PeriapsisKm - 8800) < 1e-6 * 8800, $"periapsis {c.PeriapsisKm} km");
        Assert.True(c.Beta > 1, $"β {c.Beta}");
    }

    [Fact]
    public void TidalCheckCatchesAPeriapsisPassageBetweenChecks()
    {
        var mu = G * (EarthMass + MoonMass);
        const double rp = 8800, ra = 56000;
        var e = (ra - rp) / (ra + rp);
        var earth = Earth();
        var moon = Moon();
        // Outbound at 11,000 km: out of the 9,484 km limit already, a few minutes after periapsis.
        Place(earth, moon, OnConic(mu, rp, e, 11000, inbound: false));
        var u = With(earth, moon);
        Assert.Empty(Tidal.TidalCheck(u));
        Assert.Single(Tidal.TidalCheck(u, window: 3600));
        // Inbound at the same distance it has not been inside yet; the next check will see it.
        Place(earth, moon, OnConic(mu, rp, e, 11000, inbound: true));
        Assert.Empty(Tidal.TidalCheck(u, window: 3600));
    }

    [Fact]
    public void TheCataloguedSolarSystemHasNoCandidates()
    {
        var (bodies, epochJd) = Catalog.CreateSolarSystem(new DateTime(2000, 1, 1, 12, 0, 0, DateTimeKind.Utc));
        var u = new Universe(epochJd);
        u.SetBodies(bodies);
        var found = Tidal.TidalCheck(u, window: 3 * 3600);
        Assert.True(found.Count == 0, $"unexpected candidates: {string.Join(", ", found.Select(c => $"{c.Secondary.Id} about {c.Primary.Id} (rp {c.PeriapsisKm:G4} < {c.LimitKm:G4} km)"))}");
    }

    [Fact]
    public void FallbackRateFallsAsTMinusFiveThirdsAndIntegratesToTheBoundMass()
    {
        const double tMin = 3.5e6, mb = 1e30;
        Assert.Equal(0, Tidal.FallbackRate(0.999 * tMin, tMin, mb));
        Close.To(1, Tidal.FallbackRate(8 * tMin, tMin, mb) / Tidal.FallbackRate(tMin, tMin, mb) * 32, 12);
        Close.To(1, Tidal.FallbackRate(tMin, tMin, mb) / (2.0 / 3 * mb / tMin), 12);
        // ∫ from t_min to 10^6 t_min by the trapezoid rule on a log grid: M_b (1 − 10^−4).
        double sum = 0;
        const int steps = 200_000;
        var prevT = tMin;
        var prevF = Tidal.FallbackRate(tMin, tMin, mb);
        for (var k = 1; k <= steps; k++)
        {
            var t = tMin * Math.Pow(1e6, (double)k / steps);
            var f = Tidal.FallbackRate(t, tMin, mb);
            sum += 0.5 * (f + prevF) * (t - prevT);
            prevT = t;
            prevF = f;
        }
        Assert.True(Math.Abs(sum / (mb * (1 - 1e-4)) - 1) < 1e-4, $"∫Ṁ dt = {sum / mb:F6} M_b");
        // Partial disruptions decay as t^(−9/4) and still return the whole bound mass.
        Close.To(1, Tidal.FallbackRate(16 * tMin, tMin, mb, Tidal.PartialFallbackExponent) / Tidal.FallbackRate(tMin, tMin, mb, Tidal.PartialFallbackExponent) * 512, 12);
    }

    [Fact]
    public void APartialDisruptionLeavesACoreOutsideTheStreamThatIsNoLongerACandidate()
    {
        var (earth, moon) = MoonPlunge();
        var d = Tidal.Disrupt(earth, moon, 10_000, Rng.Mulberry32(3));
        var core = Assert.IsType<TidalCore>(d.Core);
        var beta = BlackHole.RocheLimitRigidKm(EarthMass, MoonMass, MoonRadius) / 8800;
        Close.To(Tidal.StrippedFraction(beta), d.StrippedFraction, 12);
        Assert.True(d.StrippedFraction > Tidal.StripAtLimit && d.StrippedFraction < 0.5, $"stripped {d.StrippedFraction:F4} at β {beta:F4}");
        Assert.Equal(Tidal.PartialFallbackExponent, d.FallbackExponent);
        var rel = moon.Pos - earth.Pos;
        var vel = moon.Vel - earth.Vel;
        for (var i = 0; i < d.Count; i++)
        {
            var s = Length(d.Position[i] - rel);
            Assert.True(s > core.Radius && s <= 1.0000001 * MoonRadius, $"particle {i} at {s:F3} km from the centre, core {core.Radius:F3} km");
            Assert.True(Length(d.Position[i]) > EarthRadius, $"particle {i} starts inside the Earth");
            // Unbound from the core, or it falls straight back and the disruption undoes itself.
            var u = d.Velocity[i] - vel;
            var eps = 0.5 * Dot(u, u) - Constants.G * core.Mass / s;
            Assert.True(eps > 0, $"particle {i} bound to the core: ε = {eps:G4} km²/s² at {s:F3} km");
        }
        // The core, at the centre's state, has its own limit inside the periapsis: the check lets it go.
        moon.Mass = core.Mass;
        Tidal.ShrinkToCore(moon, core.Radius);
        Assert.Equal(core.Mass, moon.Mass0);
        Assert.True(Tidal.LimitFor(earth, moon).Km < d.PeriapsisKm, $"core limit {Tidal.LimitFor(earth, moon).Km} km vs periapsis {d.PeriapsisKm} km");
        Assert.Empty(Tidal.TidalCheck(With(earth, moon), window: 3600));
    }
}
