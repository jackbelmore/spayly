using static Spayly.Sim.Collide;
using static Spayly.Sim.Constants;
using static Spayly.Sim.Vec3;

namespace Spayly.Sim.Tests;

// No vitest file: black holes exist only in the Godot port. The formulas are checked against GR results that do not come
// from the code under test — the Sun's r_s, the ISCO as the minimum of the circular-orbit angular momentum, the weak-field
// series 4M/b + 15πM²/4b² + 128M³/3b³, Bozza's (2002) strong-field limit, the capture threshold found by integrating the
// orbit rather than by the e² > 4/27 criterion — and the tables are held to the RK4 reference ray by ray.
public class BlackHoleTests
{
    private static readonly BodyVisual Vis = new() { Color = "#8a7fb0", Albedo = 0 };
    private static readonly CollideOptions Identity = new() { BodyFrame = (_, d) => d };
    private static readonly Lazy<LensingTables> Tables = new(() => BlackHole.BuildLensingTables());

    private static Body Make(string id, BodyKind kind, double mass, double radius, Vec3 pos, Vec3 vel) => Body.MakeBody(new BodySpec
    {
        Id = id, Name = id, Kind = kind, Surface = kind == BodyKind.Planet ? Surface.Rock : Surface.Star, Mass = mass, Radius = radius,
        Pos = pos, Vel = vel, Visual = Vis, Seed = 7,
    });

    private static Vec3 Momentum(Universe u)
    {
        var p = Vec3.Zero;
        foreach (var b in u.Bodies) p += b.Vel * b.Mass;
        return p;
    }

    private static double MomentumScale(IEnumerable<Body> bodies)
    {
        double s = 0;
        foreach (var b in bodies) s += b.Mass * Length(b.Vel);
        return s;
    }

    [Fact]
    public void SchwarzschildRadiusOfTheSunIs2953Km()
    {
        var rs = BlackHole.SchwarzschildRadiusKm(SunMass);
        Assert.True(Math.Abs(rs / 2.953 - 1) < 1e-3, $"r_s(Sun) = {rs} km");
    }

    [Fact]
    public void PhotonSphereIsAt1Point5RsAndIscoAt3Rs()
    {
        var m = 10 * SunMass;
        var rs = BlackHole.SchwarzschildRadiusKm(m);
        Close.To(1.5, BlackHole.PhotonSphereRadiusKm(m) / rs, 12);
        Close.To(3, BlackHole.IscoRadiusKm(m) / rs, 12);

        // The photon sphere is the circular solution of the orbit equation: a tangential ray there stays put (it is unstable,
        // so only for a couple of radians), and its shadow covers exactly half the sky.
        var u = BlackHole.OrbitU(2.0 / 3, 0, 2);
        Assert.True(Math.Abs(u - 2.0 / 3) < 1e-9, $"u after 2 rad at the photon sphere = {u}");
        // sin θ rounds to within an ulp of 1, and asin turns an ulp there into ~1e-8.
        Close.To(Math.PI / 2, BlackHole.ShadowAngularRadius(1.5), 6);

        // The ISCO is where the angular momentum of circular geodesics is least (golden-section search).
        double lo = 1.6, hi = 20;
        var g = (Math.Sqrt(5) - 1) / 2;
        for (var i = 0; i < 200; i++)
        {
            double a = hi - g * (hi - lo), b = lo + g * (hi - lo);
            if (BlackHole.CircularOrbitAngularMomentum(a) < BlackHole.CircularOrbitAngularMomentum(b)) hi = b;
            else lo = a;
        }
        Assert.True(Math.Abs(0.5 * (lo + hi) - 3) < 1e-4, $"ISCO at {0.5 * (lo + hi)} r_s");
    }

    [Fact]
    public void WeakFieldDeflectionMatchesTheSeriesAt100RsAndTheLeadingTermAt1000Rs()
    {
        // At b = 100 r_s the second-order term 15πM²/4b² is +1.47 % of the leading 2 r_s/b, so "within 1 % of 2/b" is not true
        // of the exact deflection there; it is held to the three-term series instead, and to 2/b alone at 1000 r_s (+0.15 %).
        var b = 100.0;
        var alpha = BlackHole.DeflectionAngle(b);
        var series = 2 / b + 15 * Math.PI / 16 / (b * b) + 16.0 / 3 / (b * b * b);
        Assert.True(Math.Abs(alpha / series - 1) < 1e-4, $"alpha(100) = {alpha}, series {series}");
        var excess = alpha / (2 / b) - 1;
        Assert.True(excess > 0.012 && excess < 0.018, $"alpha(100) / (2/b) - 1 = {excess}");

        var far = BlackHole.DeflectionAngle(1000);
        Assert.True(Math.Abs(far / (2.0 / 1000) - 1) < 0.01, $"alpha(1000) = {far}");

        // The RK4 reference from a camera a million r_s away agrees with the quadrature (the missing far leg bends by ~1e-11).
        var uc = 1e-6;
        var psi = Math.Asin(uc * Math.Sqrt(1 - uc) * b);
        var ray = BlackHole.TraceRay(uc, psi);
        Assert.False(ray.Captured);
        Assert.True(Math.Abs(ray.Deflection / alpha - 1) < 1e-5, $"RK4 {ray.Deflection} vs quadrature {alpha}");
    }

    [Fact]
    public void StrongFieldDeflectionFollowsTheLogarithmicLimit()
    {
        // Bozza (2002): alpha = -ln(b/b_c - 1) + ln(216 (7 - 4 sqrt 3)) - pi + O(delta), for Schwarzschild.
        var delta = 1e-4;
        var alpha = BlackHole.DeflectionAngle(BlackHole.CriticalImpact * (1 + delta));
        var limit = -Math.Log(delta) + Math.Log(216 * (7 - 4 * Math.Sqrt(3))) - Math.PI;
        Assert.True(Math.Abs(alpha / limit - 1) < 5e-3, $"alpha = {alpha}, strong-field limit {limit}");
    }

    [Fact]
    public void CaptureThresholdIsAtSqrt27Over2Rs()
    {
        // Found by integrating rays, not from the criterion e^2 > 4/27 the tables use.
        double lo = 2, hi = 3;
        Assert.False(BlackHole.Escapes(lo));
        Assert.True(BlackHole.Escapes(hi));
        for (var i = 0; i < 40; i++)
        {
            var mid = 0.5 * (lo + hi);
            if (BlackHole.Escapes(mid)) hi = mid;
            else lo = mid;
        }
        var bc = 0.5 * (lo + hi);
        Assert.True(Math.Abs(bc / (Math.Sqrt(27) / 2) - 1) < 5e-3, $"capture threshold b = {bc} r_s");
    }

    [Fact]
    public void DiscTemperaturePeaksJustOutsideTheInnerEdge()
    {
        var m = 10 * SunMass;
        var rIn = BlackHole.IscoRadiusKm(m);
        var mdot = BlackHole.EddingtonAccretionRate(m);
        Assert.Equal(0, BlackHole.DiscTemperatureK(m, mdot, rIn, rIn));
        double best = 0, bestR = 0;
        for (var r = rIn * 1.0001; r < 10 * rIn; r += rIn * 1e-4)
        {
            var t = BlackHole.DiscTemperatureK(m, mdot, r, rIn);
            if (t > best)
            {
                best = t;
                bestR = r;
            }
        }
        var expected = BlackHole.DiscPeakRadius(rIn);
        Assert.True(Math.Abs(bestR / expected - 1) < 5e-3, $"peak at {bestR / rIn} r_in, expected {expected / rIn}");
        Assert.True(bestR > rIn && bestR < 1.5 * rIn, $"peak at {bestR / rIn} r_in");
        // A stellar-mass hole at the Eddington rate: an X-ray disc of ~10^7 K.
        Assert.True(best > 1e6 && best < 1e8, $"peak {best} K");
        Close.To(1, BlackHole.DiscTemperatureShape(expected, rIn), 12);
        Close.To(1, BlackHole.DiscTemperatureK(m, mdot, 2 * rIn, rIn) / best / BlackHole.DiscTemperatureShape(2 * rIn, rIn), 3);
    }

    [Fact]
    public void CircularEmitterRedshiftSeparatesApproachingFromReceding()
    {
        // No motion along the line of sight: time dilation and gravity only, sqrt(1 - 3M/r).
        Close.To(Math.Sqrt(1 - 1.5 / 6), BlackHole.CircularEmitterG(6, 0), 12);
        Assert.True(BlackHole.CircularEmitterG(3, 3) > 1, "approaching side at the ISCO is blueshifted");
        Assert.True(BlackHole.CircularEmitterG(3, -3) < BlackHole.CircularEmitterG(3, 0), "receding side is redder");
        Close.To(1, BlackHole.CircularEmitterG(1e8, 0), 6);
        Close.To(Math.Sqrt(0.5), BlackHole.StaticEmitterG(2), 12);
    }

    [Fact]
    public void TidalAndRocheRadii()
    {
        Close.To(100, BlackHole.TidalRadiusKm(1e6 * SunMass, SunMass, SunRadius) / SunRadius, 9);
        // A Sun-like star is torn apart outside the horizon of a 10^7 solar-mass hole and swallowed whole by a 10^9 one.
        Assert.True(BlackHole.TidalRadiusKm(1e7 * SunMass, SunMass, SunRadius) > BlackHole.SchwarzschildRadiusKm(1e7 * SunMass));
        Assert.True(BlackHole.TidalRadiusKm(1e9 * SunMass, SunMass, SunRadius) < BlackHole.SchwarzschildRadiusKm(1e9 * SunMass));
        // The textbook fluid Roche limit of the Moon about the Earth, 2.44 R_E (rho_E/rho_M)^(1/3) ≈ 18,400 km.
        var roche = BlackHole.RocheLimitFluidKm(5.9722e24, 7.342e22, 1737.4);
        Assert.True(Math.Abs(roche / 18370 - 1) < 0.01, $"Moon Roche limit {roche} km");
        Close.To(2.44 / Math.Cbrt(2), roche / BlackHole.RocheLimitRigidKm(5.9722e24, 7.342e22, 1737.4), 9);
    }

    [Fact]
    public void MakeBodyGivesABlackHoleItsSchwarzschildRadius()
    {
        var b = Make("bh", BodyKind.BlackHole, 10 * SunMass, 12345, Vec3.Zero, Vec3.Zero);
        var rs = BlackHole.SchwarzschildRadiusKm(10 * SunMass);
        Assert.Equal(rs, b.Radius);
        Assert.Equal(rs, b.Radius0);
        Assert.Equal(10 * SunMass, b.Mass0);
    }

    [Fact]
    public void DebrisFallingInGrowsTheHorizonWithTheMass()
    {
        var b = Make("bh", BodyKind.BlackHole, 10 * SunMass, 0, Vec3.Zero, Vec3.Zero);
        var rock = Make("rock", BodyKind.Planet, 1e22, 1000, new Vec3(1e6, 0, 0), Vec3.Zero);
        var ledger = new Debris.DebrisLedger(Vec3.Zero);
        ledger.Absorb(b, 1e29, new Vec3(100, 0, 0), Vec3.Zero);
        ledger.Absorb(rock, 1e18, rock.Pos, Vec3.Zero);
        Assert.Equal(BlackHole.SchwarzschildRadiusKm(b.Mass), b.Radius);
        Assert.True(b.Radius > b.Radius0, $"r_s {b.Radius} km did not grow from {b.Radius0} km");
        Assert.Equal(1000, rock.Radius);
    }

    [Fact]
    public void ABlackHoleAbsorbingABodyConservesMassAndMomentumExactly()
    {
        var mh = 10 * SunMass;
        var hole = Make("bh", BodyKind.BlackHole, mh, 0, new Vec3(1e8, 2e7, -3e6), new Vec3(12, -7, 0.5));
        var rs = hole.Radius;
        var planet = Make("p", BodyKind.Planet, 5.9722e24, 6371, hole.Pos + new Vec3(rs + 6000, 0, 0), new Vec3(-900, 40, 3));
        var u = new Universe(2451545);
        u.SetBodies([hole, planet]);
        var p0 = Momentum(u);
        var scale = MomentumScale(u.Bodies);
        var mp = planet.Mass;
        var o = ResolveCollision(u, new CollisionEvent(planet, hole, 0), Identity);
        Assert.Equal(OutcomeKind.Absorb, o.Kind);
        Assert.Same(hole, o.Target);
        Assert.Single(u.Bodies);
        Assert.Same(hole, u.Bodies[0]);
        Assert.Equal(BodyKind.BlackHole, hole.Kind);
        Assert.Equal(mh + mp, hole.Mass);
        Assert.Equal(BlackHole.SchwarzschildRadiusKm(mh + mp), hole.Radius);
        Assert.Equal(hole.Mass, hole.Mass0);
        var drift = Length(Momentum(u) - p0);
        Assert.True(drift <= 1e-14 * scale, $"momentum drift {drift} vs scale {scale}");
        Assert.True(o.TargetDeltaV > 0);
    }

    [Fact]
    public void ABlackHoleSwallowsAHeavierStarRatherThanTheReverse()
    {
        var hole = Make("bh", BodyKind.BlackHole, 0.5 * SunMass, 0, new Vec3(0, 0, 0), new Vec3(0, 30, 0));
        var sun = Make("sun", BodyKind.Star, SunMass, SunRadius, new Vec3(SunRadius, 0, 0), new Vec3(-5, 0, 0));
        var u = new Universe(2451545);
        u.SetBodies([sun, hole]);
        var p0 = Momentum(u);
        var scale = MomentumScale(u.Bodies);
        var o = ResolveCollision(u, new CollisionEvent(sun, hole, 0), Identity);
        Assert.Same(hole, o.Target);
        Assert.Single(u.Bodies);
        Assert.Null(u.ById("sun"));
        Assert.Equal(0.5 * SunMass + SunMass, hole.Mass);
        Assert.Equal(BlackHole.SchwarzschildRadiusKm(hole.Mass), hole.Radius);
        var drift = Length(Momentum(u) - p0);
        Assert.True(drift <= 1e-14 * scale, $"momentum drift {drift} vs scale {scale}");
    }

    [Fact]
    public void TwoBlackHolesMergeIntoOneWithTheRemnantsHorizon()
    {
        // M5.4: the remnant is lighter by what the waves radiate and recoils (MergerTests has the details); momentum
        // including the waves' share still balances.
        var a = Make("a", BodyKind.BlackHole, 30 * SunMass, 0, new Vec3(0, 0, 0), new Vec3(0, 100, 0));
        var b = Make("b", BodyKind.BlackHole, 20 * SunMass, 0, new Vec3(a.Radius + 1, 0, 0), new Vec3(0, -200, 0));
        var u = new Universe(2451545);
        u.SetBodies([a, b]);
        var p0 = Momentum(u);
        var scale = MomentumScale(u.Bodies);
        var o = ResolveCollision(u, new CollisionEvent(b, a, 0), Identity);
        Assert.Same(a, o.Target);
        Assert.Single(u.Bodies);
        Assert.Equal(GravitationalWaves.RemnantMassFraction(30 * SunMass, 20 * SunMass) * 50 * SunMass, a.Mass, 1e17);
        Assert.Equal(BlackHole.SchwarzschildRadiusKm(a.Mass), a.Radius);
        var m = o.Merger!.Value;
        var pWaves = (m.V1 * m.M1 + m.V2 * m.M2) - m.Vf * m.Mf;
        Assert.True(Length(Momentum(u) + pWaves - p0) <= 1e-14 * scale);
    }

    [Fact]
    public void ARockFallingIntoABlackHoleIsAbsorbedThroughTheUniverse()
    {
        var hole = Make("bh", BodyKind.BlackHole, 10 * SunMass, 0, Vec3.Zero, Vec3.Zero);
        var rock = Body.MakeBody(new BodySpec
        {
            Id = "rock", Name = "Rock", Kind = BodyKind.Asteroid, Surface = Surface.Rock, Mass = 1.5e12, Radius = 0.5,
            // Head-on: the swept contact test sees the segment through the centre on the first 0.25 s substep, whereas an
            // offset fall at these speeds can overshoot the 30 km contact radius between samples.
            Pos = new Vec3(3000, 0, 0), Vel = new Vec3(-2000, 0, 0), Visual = Vis, Seed = 3,
        });
        var u = new Universe(2451545);
        u.SetBodies([hole, rock]);
        var outcomes = new List<ImpactOutcome>();
        u.CollisionHandlers.Add((ev, uni) => outcomes.Add(ResolveCollision(uni, ev, Identity)));
        var p0 = Momentum(u);
        var m0 = hole.Mass + rock.Mass;
        for (var i = 0; i < 200 && outcomes.Count == 0; i++) u.Step(0.5);
        Assert.Single(outcomes);
        Assert.Same(hole, outcomes[0].Target);
        Assert.Single(u.Bodies);
        Assert.Equal(m0, hole.Mass);
        // Gravity and the merge both conserve momentum; before and after the rock carries all of it at ~1e17 kg km/s.
        var drift = Length(Momentum(u) - p0);
        var scale = 1.5e12 * 2e5;
        Assert.True(drift <= 1e-12 * scale, $"momentum drift {drift}");
    }

    /// <summary>ψ values that probe each branch of the table lookup for a camera at u_c: inward escapes, inward captures, the
    /// grazing ray, outward escapes of both orbit types. Rays within 10 % of the critical e² are left out: the photon ring is
    /// a sub-pixel band the tables are not asked to resolve.</summary>
    private static IEnumerable<double> Probes(double uc)
    {
        var sc = uc * Math.Sqrt(1 - uc) / BlackHole.SqrtMu;
        var psiC = sc < 1 ? Math.Asin(sc) : Math.PI / 2;
        foreach (var k in new[] { 0.3, 0.6, 0.8, 1.25, 1.5, 2, 4, 10 })
        {
            var psi = k * psiC;
            if (psi < Math.PI / 2 - 1e-3) yield return psi;
        }
        foreach (var psi in new[] { Math.PI / 2, 1.8, 2.2, 2.6, 3.0 }) yield return psi;
        foreach (var k in new[] { 0.3, 0.8 }) yield return Math.PI - k * psiC;
    }

    [Fact]
    public void TablesReproduceTheReferenceTrace()
    {
        var t = Tables.Value;
        foreach (var uc in new[] { 1e-3, 0.02, 0.1, 0.3, 0.55 })
        {
            foreach (var psi in Probes(uc))
            {
                var sin = Math.Sin(psi);
                var e2 = uc * uc * (1 - uc) / (sin * sin);
                if (Math.Abs(e2 / BlackHole.Mu - 1) < 0.1) continue;
                var reference = BlackHole.TraceRay(uc, psi);
                var ray = t.Trace(uc, Math.Cos(psi), sin);
                Assert.True(ray.Escapes == !reference.Captured, $"uc={uc} psi={psi}: table escapes={ray.Escapes}, RK4 captured={reference.Captured}");
                var tol = ray.Escapes ? 1e-3 : 2e-3;
                Assert.True(Math.Abs(ray.Swept - reference.Swept) < tol, $"uc={uc} psi={psi}: swept {ray.Swept} vs RK4 {reference.Swept}");
            }
        }
    }

    [Fact]
    public void TablesReproduceTheDiscCrossings()
    {
        var t = Tables.Value;
        var checkedCrossings = 0;
        foreach (var (uc, psi) in new[] { (0.05, 0.08), (0.05, 0.2), (0.1, 0.3), (0.2, 0.6), (0.3, 1.2), (0.3, 2.0), (0.02, 0.03) })
        {
            foreach (var node in new[] { 0.3, 1.1, 2.4 })
            {
                var reference = BlackHole.TraceRay(uc, psi, node);
                var ray = t.Trace(uc, Math.Cos(psi), Math.Sin(psi));
                foreach (var c in reference.Crossings)
                {
                    if (c.Phi > ray.Swept || c.R < 1.05 || c.R > 40) continue;
                    var r = 1 / t.OrbitU(ray, c.Phi);
                    Assert.True(Math.Abs(r / c.R - 1) < 0.01, $"uc={uc} psi={psi} node={node} phi={c.Phi}: table r={r}, RK4 r={c.R}");
                    checkedCrossings++;
                }
            }
        }
        Assert.True(checkedCrossings > 12, $"{checkedCrossings} crossings compared");
    }
}
