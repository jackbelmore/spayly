using Xunit.Abstractions;
using static Spayly.Sim.Body;
using static Spayly.Sim.Constants;

namespace Spayly.Sim.Tests;

/// <summary>Star-star energy-scaled outcomes and Moon into Vesta by speed, through the default (LS12) options.</summary>
public class StellarCollisionTests(ITestOutputHelper log)
{
    private static readonly BodyVisual White = new() { Color = "#fff", Albedo = 0.1 };
    private static readonly CollideOptions Default = new() { BodyFrame = (_, d) => d };

    private static Body Make(string id, BodyKind kind, Surface surface, double mass, double radius, Vec3 pos, Vec3 vel, int seed) =>
        MakeBody(new BodySpec { Id = id, Name = id, Kind = kind, Surface = surface, Mass = mass, Radius = radius, Pos = pos, Vel = vel, Seed = seed, Visual = White });

    /// <summary>Two equal stars touching, closing at <paramref name="vRel"/> with impact parameter <paramref name="b"/>.</summary>
    private static (Universe U, Body A, Body B, CollisionEvent Ev) Stars(double vRel, double b, double massB = SunMass, double radiusB = SunRadius)
    {
        var n = new Vec3(Math.Sqrt(1 - b * b), b, 0);
        var drift = new Vec3(3, -2, 1);
        var a = Make("a", BodyKind.Star, Surface.Star, SunMass, SunRadius, Vec3.Zero, drift, 1);
        var c = Make("b", BodyKind.Star, Surface.Star, massB, radiusB, n * ((SunRadius + radiusB) * 0.999), drift + new Vec3(-vRel, 0, 0), 2);
        var u = new Universe(2451545);
        u.SetBodies([a, c]);
        return (u, a, c, new CollisionEvent(a, c, 10));
    }

    private static Vec3 Momentum(Universe u, ImpactOutcome o)
    {
        var p = o.ParticleTierVelocity * o.ParticleTierMass;
        foreach (var b in u.Bodies) p += b.Vel * b.Mass;
        return p;
    }

    private const double VescSuns = 617.5; // sqrt(2G·2M☉/2R☉), km/s: two Suns meeting on a parabola

    [Fact]
    public void TwoSunsOnAParabolaMergeAndShedAFewPercent()
    {
        var (u, a, _, ev) = Stars(VescSuns, 0);
        var p0 = Momentum(u, new ImpactOutcome { Kind = OutcomeKind.Absorb, Target = a, Impactor = a, ContactWorld = Vec3.Zero, NormalWorld = Vec3.Zero, DirBody = Vec3.Zero, VelDirWorld = Vec3.Zero, RelSpeed = 0, Angle = 0, Energy = 0, TargetDeltaV = 0, Fragments = [], Time = 0 });
        var o = Collide.ResolveCollision(u, ev, Default);
        var loss = o.ParticleTierMass / (2 * SunMass);
        log.WriteLine($"shock {o.StellarShock:F3}, mass loss {loss:P2}");
        Assert.Equal(OutcomeKind.StellarMerge, o.Kind);
        Assert.Single(u.Bodies);
        Assert.InRange(loss, 0.01, 0.06);
        Assert.Equal(BodyKind.Star, o.Target.Kind);
        Assert.InRange(Vec3.Length(Momentum(u, o) - p0), 0, 1e-12 * 2 * SunMass * 617);
    }

    [Fact]
    public void FasterStarsShedMoreAndNeverGoBelowARemnant()
    {
        double last = -1;
        foreach (var v in new[] { 100.0, VescSuns, 2 * VescSuns, 5 * VescSuns, 50 * VescSuns })
        {
            var (u, _, _, ev) = Stars(v, 0);
            var o = Collide.ResolveCollision(u, ev, Default);
            var f = o.ParticleTierMass / (2 * SunMass);
            log.WriteLine($"{v / VescSuns:F2} v_esc: shock {o.StellarShock:F3}, lost {f:P1}, remnant {o.Target.Mass / SunMass:F3} M☉");
            Assert.True(f >= last, $"{v}: {f} < {last}");
            if (last < Collide.StellarMassLossCap) Assert.True(f > last, $"{v}: {f} <= {last}");
            Assert.True(o.Target.Mass >= 0.1 * 2 * SunMass * (1 - 1e-12));
            Assert.Equal(BodyKind.Star, o.Target.Kind);
            last = f;
        }
        Assert.Equal(Collide.StellarMassLossCap, last, 1e-9);
    }

    [Fact]
    public void AGrazingFastPairSurvivesAndASlowOneMerges()
    {
        var (u, a, c, ev) = Stars(3 * VescSuns, 0.95);
        var o = Collide.ResolveCollision(u, ev, Default);
        Assert.Equal(OutcomeKind.HitAndRun, o.Kind);
        Assert.Equal(2, u.Bodies.Count);
        Assert.Equal(SunMass, a.Mass);
        Assert.Equal(SunMass, c.Mass);
        Assert.True(Vec3.Length(c.Pos - a.Pos) > a.Radius + c.Radius, "still overlapping");

        var (u2, _, _, ev2) = Stars(0.3 * VescSuns, 0.95);
        var o2 = Collide.ResolveCollision(u2, ev2, Default);
        Assert.Equal(OutcomeKind.StellarMerge, o2.Kind);
        Assert.Single(u2.Bodies);
    }

    [Fact]
    public void ASwallowedPlanetIsStillJustAbsorbed()
    {
        var sun = Make("sun", BodyKind.Star, Surface.Star, SunMass, SunRadius, Vec3.Zero, Vec3.Zero, 1);
        var earth = Make("earth", BodyKind.Planet, Surface.Rock, 5.9722e24, 6371, new Vec3(SunRadius, 0, 0), new Vec3(-300, 0, 0), 2);
        var u = new Universe(2451545);
        u.SetBodies([sun, earth]);
        var o = Collide.ResolveCollision(u, new CollisionEvent(sun, earth, 0), Default);
        Assert.Equal(OutcomeKind.Absorb, o.Kind);
        Assert.Equal(0, o.ParticleTierMass);
        Assert.Equal(SunMass + 5.9722e24, sun.Mass);
    }

    [Fact]
    public void MoonIntoVestaBreaksUpWithSpeedInsteadOfMeltingToABall()
    {
        const double mVesta = 2.59e20, rVesta = 262.7, mMoon = 7.342e22, rMoon = 1737.4;
        // The Moon is 280 Vesta masses, so LS12 makes it the target and Vesta the projectile it swallows; what rises with speed
        // is the debris the hit throws off, from none (merge) through a few percent of the pair's mass in 64 fragments.
        var results = new List<(double V, CollisionRegime Regime, OutcomeKind Kind, int Frags, double Lr)>();
        foreach (var v in new[] { 1.0, 5.0, 15.0, 40.0 })
        {
            var n = new Vec3(0.9, Math.Sqrt(1 - 0.81), 0);
            var moon = Make("moon", BodyKind.Moon, Surface.Rock, mMoon, rMoon, Vec3.Zero, Vec3.Zero, 3);
            var vesta = Make("vesta", BodyKind.Asteroid, Surface.Rock, mVesta, rVesta, n * ((rMoon + rVesta) * 0.999), new Vec3(-v, 0, 0), 4);
            var u = new Universe(2451545);
            u.SetBodies([moon, vesta]);
            var o = Collide.ResolveCollision(u, new CollisionEvent(moon, vesta, 1), Default);
            Assert.NotNull(o.Ls12);
            results.Add((v, o.Ls12!.Value.Regime, o.Kind, o.Fragments.Count, o.Ls12.Value.LargestRemnantFraction));
            log.WriteLine($"{v} km/s: {results[^1]}");
        }
        Assert.Equal(CollisionRegime.PerfectMerge, results[0].Regime);
        Assert.NotEqual(results[0].Regime, results[^1].Regime);
        Assert.True(results[^1].Lr < results[0].Lr);
        Assert.True(results[2].Frags > 0 && results[3].Frags > 0, "no debris at 15 and 40 km/s");
    }
}
