using Spayly.Sim.Debris;
using Xunit.Abstractions;
using static Spayly.Sim.Vec3;

namespace Spayly.Sim.Tests;

// No vitest counterpart: the debris ledger exists only in the C# port. Facts, in order:
//   InvariantHoldsOverAScriptedInjectAbsorbDustAccreteSequence, AbsorbMergesMomentumIntoTheBody,
//   InjectFromBodyAppliesTheRecoil, DustReaccretesOntoItsBody, MovingAReservoirKeepsTheBook
// "Exact" here is double-double: the book's T0 is compared with the exact sum over the bodies to 1e-20, well past the
// 1e-12 the tiers must hold.
public class DebrisLedgerTests(ITestOutputHelper log)
{
    private static readonly BodyVisual Grey = new() { Color = "#888", Albedo = 0.1 };

    private static Body Make(string id, double mass, double radius, Vec3 pos, Vec3 vel) => Body.MakeBody(new BodySpec
    {
        Id = id, Name = id, Kind = BodyKind.Moon, Surface = Surface.Rock, Mass = mass, Radius = radius, Pos = pos, Vel = vel, Visual = Grey, Seed = 1,
    });

    private static (List<Body> Bodies, Body Earth, Body Moon) EarthMoon()
    {
        var earth = Make("earth", 5.972e24, 6371, new Vec3(1.496e8, 1.3e6, -2.1e3), new Vec3(-0.26, 29.78, 0.001));
        var moon = Make("moon", 7.342e22, 1737.4, earth.Pos + new Vec3(384400, 1200, 3100), earth.Vel + new Vec3(0.02, 1.022, 0.09));
        return ([earth, moon], earth, moon);
    }

    /// <summary>Ejecta from the Moon's +x limb, in world km and km/s, as the game builds them.</summary>
    private static (double[] M, Vec3[] R, Vec3[] V) Ejecta(Body moon, int n, uint seed)
    {
        var s = EjectaSampler.Sample(15.4, 1, 25, 2500, 3000, EjectaScaling.Rock, 1.0203e16, n, Rng.Mulberry32(seed));
        var nrm = new Vec3(1, 0, 0);
        var t1 = AnyPerpendicular(nrm);
        var t2 = Cross(nrm, t1);
        var contact = moon.Pos + nrm * moon.Radius;
        var r = new Vec3[n];
        var v = new Vec3[n];
        for (var i = 0; i < n; i++)
        {
            var p = s.Position[i];
            var u = s.Velocity[i];
            r[i] = contact + t1 * p.X + t2 * p.Y + nrm * (p.Z + 0.01);
            v[i] = moon.Vel + t1 * u.X + t2 * u.Y + nrm * u.Z;
        }
        return (s.Mass, r, v);
    }

    private static Tally Flow(double[] m, Vec3[] r, Vec3[] v, Vec3 origin, int from = 0, int to = -1)
    {
        var t = Tally.Zero;
        for (var i = from; i < (to < 0 ? m.Length : to); i++) t += Tally.Of(m[i], r[i], v[i], origin);
        return t;
    }

    private static double Rel(DD a, DD b) => Math.Abs((a - b).Value) / Math.Max(Math.Abs(b.Value), double.Epsilon);

    [Fact]
    public void InvariantHoldsOverAScriptedInjectAbsorbDustAccreteSequence()
    {
        var (bodies, earth, moon) = EarthMoon();
        var bary = (earth.Pos * earth.Mass + moon.Pos * moon.Mass) * (1 / (earth.Mass + moon.Mass));
        var ledger = new DebrisLedger(bary);
        ledger.Baseline(bodies, bary);
        const int n = 4000;
        var (m, r, v) = Ejecta(moon, n, 5);
        var alive = new bool[n];

        // Inject: the Moon gives up the ejecta mass and its momentum (the recoil).
        ledger.InjectFromBody(moon, Flow(m, r, v, bary));
        for (var i = 0; i < n; i++) alive[i] = true;

        // Particles fall back with velocities T0 gravity would have changed: the book must still balance.
        var fall = new Vec3(-0.3, 0.05, 0.01);
        for (var i = 0; i < n / 2; i++)
        {
            ledger.Absorb(moon, m[i], r[i] - new Vec3(0.02, 0, 0), v[i] + fall);
            alive[i] = false;
        }
        for (var i = n / 2; i < n / 2 + 300; i++)
        {
            ledger.Absorb(earth, m[i], earth.Pos + new Vec3(6371, 0, 0), v[i] + new Vec3(0, -1.1, 0));
            alive[i] = false;
        }
        for (var i = n / 2 + 300; i < 3 * n / 4; i++)
        {
            ledger.ToDust(i % 2 == 0 ? earth : moon, m[i], r[i] + new Vec3(5e4, 0, 0), v[i] * 0.9);
            alive[i] = false;
        }
        for (var k = 0; k < 10; k++)
        {
            ledger.Accrete(earth, 0.3);
            ledger.Accrete(moon, 0.3);
        }
        ledger.Accrete(moon, 1);

        // A spawned asteroid arrives from outside the tiers.
        var ast = Make("ast-1", 1.13e13, 1, moon.Pos + new Vec3(-2e4, 3e3, 0), new Vec3(10, 20, 3));
        bodies.Add(ast);
        ledger.AddExternal(Tally.Of(ast.Mass, ast.Pos, ast.Vel, bary));
        ledger.Absorb(ast, m[n - 1], ast.Pos, v[n - 1]);
        alive[n - 1] = false;

        var err = ledger.Error();
        log.WriteLine($"ledger error: mass {err.Mass:G3} momentum {err.Momentum:G3} angular {err.Angular:G3}; reservoirs {ledger.Reservoirs.Count}, dust {ledger.Dust.Mass.Value:G6} kg");
        Assert.True(err.Mass < 1e-12, $"mass error {err.Mass:G3}");
        Assert.True(err.Momentum < 1e-12, $"momentum error {err.Momentum:G3}");
        Assert.True(err.Angular < 1e-12, $"angular momentum error {err.Angular:G3}");

        // The book's T0 is the bodies' exact tally, not an approximation of it.
        var measured = DebrisLedger.Measure(bodies, bary);
        var massGap = Rel(ledger.Bodies.Mass, measured.Mass);
        var pGap = (ledger.Bodies.Momentum - measured.Momentum).Length / measured.Momentum.Length;
        log.WriteLine($"T0 book vs bodies: mass {massGap:G3}, momentum {pGap:G3}");
        Assert.True(massGap < 1e-20, $"T0 book mass differs from Σ body mass by {massGap:G3}");
        Assert.True(pGap < 1e-20, $"T0 book momentum differs from Σ m v by {pGap:G3}");

        // T2's book mass is exactly what is still alive.
        var left = DD.Zero;
        for (var i = 0; i < n; i++) if (alive[i]) left += m[i];
        Assert.True(Rel(ledger.Particles.Mass, left) < 1e-20, $"T2 book {ledger.Particles.Mass.Value:R} vs alive {left.Value:R}");
        Assert.True(ledger.Reservoirs.ContainsKey(earth) && ledger.Reservoirs.ContainsKey(moon), "dust homes lost their reservoirs");
    }

    [Fact]
    public void AbsorbMergesMomentumIntoTheBody()
    {
        var (bodies, _, moon) = EarthMoon();
        var ledger = new DebrisLedger(Vec3.Zero);
        ledger.Baseline(bodies, Vec3.Zero);
        double m0 = moon.Mass, m = 3.3e12;
        var v0 = moon.Vel;
        var vp = v0 + new Vec3(1.5, -0.4, 2.2);
        ledger.Absorb(moon, m, moon.Pos + new Vec3(0, 0, moon.Radius), vp);
        var want = (v0 * m0 + vp * m) * (1 / (m0 + m));
        Assert.Equal(m0 + m, moon.Mass);
        Assert.True(Length(moon.Vel - want) <= 1e-15 * Length(want), $"vel {moon.Vel} want {want}");
        Assert.True(ledger.Error().Mass < 1e-25 && ledger.Error().Momentum < 1e-25, $"{ledger.Error()}");
    }

    [Fact]
    public void InjectFromBodyAppliesTheRecoil()
    {
        // A target at rest, so its velocity holds the recoil to full precision.
        var moon = Make("moon", 7.342e22, 1737.4, Vec3.Zero, Vec3.Zero);
        var ledger = new DebrisLedger(Vec3.Zero);
        ledger.Baseline([moon], Vec3.Zero);
        var s = EjectaSampler.Sample(15.4, 1, 25, 2500, 3000, EjectaScaling.Rock, 1.0203e16, 20_000, Rng.Mulberry32(21));
        var flow = Tally.Zero;
        for (var i = 0; i < s.Count; i++) flow += Tally.Of(s.Mass[i], new Vec3(0, 0, moon.Radius) + s.Position[i], s.Velocity[i], Vec3.Zero);
        ledger.InjectFromBody(moon, flow);
        Assert.Equal(7.342e22 - 1.0203e16, moon.Mass);
        var want = s.Recoil * (1 / moon.Mass);
        Assert.True(Length(moon.Vel - want) <= 1e-12 * Length(want), $"recoil velocity {moon.Vel} want {want}");
        // Body plus its reservoir took exactly −Σ m v.
        var res = ledger.Reservoirs[moon];
        var gap = (DV.Product(moon.Mass, moon.Vel) + res.Momentum + flow.Momentum).Length / flow.Momentum.Length;
        Assert.True(gap < 1e-20, $"body + reservoir momentum change misses −Σmv by {gap:G3}");
        Assert.True(ledger.Error().Mass < 1e-25, $"{ledger.Error()}");
    }

    [Fact]
    public void DustReaccretesOntoItsBody()
    {
        var (bodies, earth, _) = EarthMoon();
        var ledger = new DebrisLedger(Vec3.Zero);
        ledger.Baseline(bodies, Vec3.Zero);
        var m0 = earth.Mass;
        const double m = 7.5e14;
        ledger.ToDust(earth, m, earth.Pos + new Vec3(4e4, 0, 0), earth.Vel + new Vec3(0, 3, 0));
        Assert.True(Math.Abs(ledger.Dust.Mass.Value - m) == 0, $"dust {ledger.Dust.Mass.Value}");
        // Exponential re-accretion over sim time: after 20 τ the reservoir holds under 1e-8 of the dust.
        for (var k = 0; k < 20; k++) ledger.Accrete(earth, 1 - Math.Exp(-1));
        var gained = DD.Sum(earth.Mass, -m0).Value;
        Assert.True(Math.Abs(gained - m) <= 1e-8 * m + Math.Pow(2, -52) * m0, $"earth gained {gained:G6} of {m:G6} kg");
        ledger.Accrete(earth, 1);
        Assert.True(Math.Abs(ledger.Dust.Mass.Value) <= Math.Pow(2, -52) * m0, $"reservoir left {ledger.Dust.Mass.Value:G3} kg, more than one ulp of the body");
        var err = ledger.Error();
        Assert.True(err.Mass < 1e-25 && err.Momentum < 1e-25 && err.Angular < 1e-25, $"{err}");
    }

    [Fact]
    public void MovingAReservoirKeepsTheBook()
    {
        var (bodies, earth, moon) = EarthMoon();
        var ledger = new DebrisLedger(Vec3.Zero);
        ledger.Baseline(bodies, Vec3.Zero);
        ledger.ToDust(moon, 1e13, moon.Pos, moon.Vel);
        var before = ledger.Dust;
        ledger.MoveReservoir(moon, earth);
        Assert.False(ledger.Reservoirs.ContainsKey(moon));
        Assert.Equal(before, ledger.Reservoirs[earth]);
        Assert.Equal(before, ledger.Dust);
        Assert.True(ledger.Error().Mass < 1e-25, $"{ledger.Error()}");
    }
}
