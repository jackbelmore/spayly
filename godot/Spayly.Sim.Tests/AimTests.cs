using static Spayly.Sim.Constants;

namespace Spayly.Sim.Tests;

// tests/aim.test.ts — describe('aiming'), in order:
//   it('still hits through a debris cloud, within its budget')
//   it('costs about the same with a debris cloud as without one')
//   it('stops at its pair budget and says so')
//   it('stays finite when two bodies sit on top of each other')
// Then the one auto-aim case from tests/collide.test.ts — describe('collisions'):
//   it('auto-aim hits Earth from 0.05 AU away')
// That test builds the system with a resolveCollision handler, but never steps it, so the handler is left out here;
// the rest of collide.test.ts belongs to CollideTests.
public class AimTests
{
    private static readonly BodyVisual FragmentVisual = new() { Color = "#888", Albedo = 0.1 };

    private static Universe SolarSystem()
    {
        var (bodies, epochJd) = Catalog.CreateSolarSystem(new DateTime(2000, 1, 1, 12, 0, 0, DateTimeKind.Utc));
        var u = new Universe(epochJd);
        u.SetBodies(bodies);
        return u;
    }

    /// <summary>The solar system plus a debris ring of light fragments round the Moon, as a shatter leaves.</summary>
    internal static Universe Crowded(int fragments)
    {
        var u = SolarSystem();
        var moon = u.ById("moon")!;
        for (var i = 0; i < fragments; i++)
        {
            var a = ((double)i / fragments) * Math.PI * 2;
            u.Add(Body.MakeBody(new BodySpec
            {
                Id = "frag" + i, Name = "Fragment", Kind = BodyKind.Fragment, Surface = Surface.Rock, Mass = 1e18 * (1 + (i % 7)), Radius = 40,
                Pos = moon.Pos + new Vec3(4000 * Math.Cos(a), 4000 * Math.Sin(a), (i % 5) * 300),
                Vel = moon.Vel + new Vec3(Math.Sin(a) * 0.3, -Math.Cos(a) * 0.3, 0),
                Visual = FragmentVisual,
            }));
        }
        return u;
    }

    // Auto-aim runs synchronously on a click. It used to integrate every fragment against every other,
    // and 200 of them made one aim take 33 s and still miss. Counting pair evaluations rather than
    // timing keeps this deterministic.
    [Fact]
    public void StillHitsThroughADebrisCloudWithinItsBudget()
    {
        var u = Crowded(201);
        Assert.Equal(240, u.Bodies.Count);
        var earth = u.ById("earth")!;
        var from = earth.Pos + Vec3.Normalize(earth.Vel) * (0.05 * AU);
        var aim = Aim.AutoAim(u, from, "earth", 25, 0.5)!;
        Assert.True(aim.Converged);
        Assert.Equal("earth", aim.Prediction.Hit!.Value.BodyId);
        Assert.True(aim.PairEvals <= Aim.AimPairBudget, $"pairEvals {aim.PairEvals} > {Aim.AimPairBudget}");
    }

    [Fact]
    public void CostsAboutTheSameWithADebrisCloudAsWithoutOne()
    {
        static double Cost(int f)
        {
            var u = Crowded(f);
            var io = u.ById("io")!;
            var from = io.Pos + Vec3.Normalize(u.ById("earth")!.Vel) * (0.02 * AU);
            var aim = Aim.AutoAim(u, from, "io", 20, 0.5)!;
            Assert.True(aim.Converged, $"no hit on Io with {f} fragments");
            return aim.PairEvals;
        }
        var crowded = Cost(201);
        var clear = Cost(0);
        Assert.True(crowded < clear * 2.5, $"crowded {crowded} pair evals vs clear {clear}");
    }

    [Fact]
    public void StopsAtItsPairBudgetAndSaysSo()
    {
        var u = Crowded(50);
        var earth = u.ById("earth")!;
        var r = Aim.PredictPath(u, new Probe(earth.Pos + new Vec3(2e6, 0, 0), earth.Vel, 0.5), 1e9, new PredictOptions { PairBudget = 50_000 });
        Assert.True(r.Truncated);
        Assert.True(r.PairEvals <= 50_000, $"pairEvals {r.PairEvals}");
    }

    [Fact]
    public void StaysFiniteWhenTwoBodiesSitOnTopOfEachOther()
    {
        var u = Crowded(0);
        var moon = u.ById("moon")!;
        u.Add(Body.MakeBody(new BodySpec
        {
            Id = "twin", Name = "Twin", Kind = BodyKind.Fragment, Surface = Surface.Rock, Mass = moon.Mass, Radius = 10, Pos = moon.Pos, Vel = moon.Vel,
            Visual = FragmentVisual,
        }));
        var earth = u.ById("earth")!;
        var r = Aim.PredictPath(u, new Probe(earth.Pos + new Vec3(5e5, 0, 0), earth.Vel, 0.5), 30 * 86400, new PredictOptions { MaxSteps = 500 });
        foreach (var p in r.Points) Assert.True(double.IsFinite(p.X + p.Y + p.Z), $"non-finite point {p}");
    }

    [Fact]
    public void AutoAimHitsEarthFrom005AuAway()
    {
        var u = SolarSystem();
        var earth = u.ById("earth")!;
        var sun = u.ById("sun")!;
        var ahead = Vec3.Normalize(earth.Vel);
        var from = earth.Pos + ahead * (0.05 * AU);
        var aim = Aim.AutoAim(u, from, "earth", 25, 0.5);
        Assert.NotNull(aim);
        Assert.True(aim.Converged);
        var hit = aim.Prediction.Hit!.Value;
        Assert.Equal("earth", hit.BodyId);
        Assert.True(hit.Speed > 25, $"hit speed {hit.Speed} km/s");
        var fromSun = Vec3.Length(from - sun.Pos) / AU;
        Assert.True(fromSun > 0.9, $"launch point {fromSun} AU from the Sun");
    }

    // Godot-only (M6.3): the launch-speed markers. Newton's 1 AU values away from holes; zero PW energy at the escape speed.
    [Fact]
    public void LaunchSpeedsAreCircularAndEscapeSpeeds()
    {
        var sun = HandBuilt.Sun();
        var (d, vc, ve) = Aim.LaunchSpeeds(sun, sun.Pos + new Vec3(AU, 0, 0), 0);
        Assert.Equal(AU, d, 1e-6);
        Assert.Equal(29.78, vc, 0.01);
        Assert.Equal(vc * Math.Sqrt(2), ve, 1e-9);
        var mass = 1e6 * SunMass;
        var rs = BlackHole.SchwarzschildRadiusKm(mass);
        var hole = HandBuilt.Make("bh", BodyKind.BlackHole, Surface.Star, mass, rs, seed: 9);
        var (_, hc, he) = Aim.LaunchSpeeds(hole, hole.Pos + new Vec3(10 * rs, 0, 0), SunMass);
        var mu = G * (mass + SunMass);
        Assert.Equal(PseudoNewtonian.CircularSpeed(mu, 10 * rs, rs), hc, 1e-9 * hc);
        Assert.Equal(0, 0.5 * he * he + PseudoNewtonian.Potential(mu, 10 * rs, rs), 1e-6 * he * he);
    }
}
