using static Spayly.Sim.Constants;

namespace Spayly.Sim.Tests;

// No vitest file covers particles.ts; these pin PopulateBelts' determinism and the particle leapfrog, and compare
// against the web build (Golden/web-universe-handbuilt.json, "p" and "pAfter").
public class ParticlesTests
{
    private static readonly BeltCounts Small = new(20, 10, 10);

    private static (Body Sun, Body Jupiter) SunAndJupiter() => (
        HandBuilt.Sun(),
        HandBuilt.Make("jupiter", BodyKind.Planet, Surface.Gas, 1.898e27, 69911, new Vec3(0, 5.2 * AU, 0), seed: 4));

    [Fact]
    public void PopulatedBeltIsDeterministic()
    {
        var (sun, jupiter) = SunAndJupiter();
        var a = new TestParticles(4200);
        var b = new TestParticles(4200);
        Particles.PopulateBelts(a, sun, jupiter);
        Particles.PopulateBelts(b, sun, jupiter);
        Assert.Equal(4200, a.Count);
        Assert.Equal(a.Pos, b.Pos);
        Assert.Equal(a.Vel, b.Vel);
        Assert.Equal(a.Family, b.Family);
        Assert.All(a.Alive, x => Assert.Equal(1, x));

        var c = new TestParticles(4200);
        Particles.PopulateBelts(c, sun, jupiter, seed: 8);
        Assert.NotEqual(a.Pos[0], c.Pos[0]);
    }

    [Fact]
    public void FamiliesLandWhereTheyBelong()
    {
        var (sun, jupiter) = SunAndJupiter();
        var p = new TestParticles(4200);
        Particles.PopulateBelts(p, sun, jupiter);
        for (var i = 0; i < p.Count; i++)
        {
            var r = Math.Sqrt(p.Pos[3 * i] * p.Pos[3 * i] + p.Pos[3 * i + 1] * p.Pos[3 * i + 1] + p.Pos[3 * i + 2] * p.Pos[3 * i + 2]) / AU;
            var (lo, hi) = p.Family[i] switch { 0 => (2.1 * 0.67, 3.3 * 1.33), 1 => (5.2028 * 0.985 * 0.79, 5.2028 * 1.015 * 1.21), _ => (39 * 0.79, 48 * 1.21) };
            Assert.True(r > lo && r < hi, $"particle {i} (family {p.Family[i]}) at {r} AU");
        }
        Assert.Equal(2600, p.Family.Count(f => f == 0));
        Assert.Equal(700, p.Family.Count(f => f == 1));
        Assert.Equal(900, p.Family.Count(f => f == 2));
    }

    [Fact]
    public void ParticlesStayBoundAroundASunMassBody()
    {
        var sun = HandBuilt.Sun();
        var p = new TestParticles(400);
        Particles.PopulateBelts(p, sun, null, new BeltCounts(300, 0, 100));
        Assert.Equal(400, p.Count);
        var bodies = new List<Body> { sun };
        p.ComputeAccelerations(bodies);
        for (var s = 0; s < 20; s++) p.Step(Day, bodies);
        var mu = G * sun.Mass;
        for (var i = 0; i < p.Count; i++)
        {
            Assert.Equal(1, p.Alive[i]);
            var r = new Vec3(p.Pos[3 * i], p.Pos[3 * i + 1], p.Pos[3 * i + 2]);
            var v = new Vec3(p.Vel[3 * i], p.Vel[3 * i + 1], p.Vel[3 * i + 2]);
            var energy = Vec3.Dot(v, v) / 2 - mu / Vec3.Length(r);
            Assert.True(energy < 0, $"particle {i} unbound, specific energy {energy}");
        }
        Assert.Equal(0, p.Absorbed);
    }

    [Fact]
    public void AParticleInsideABodyIsAbsorbed()
    {
        var sun = HandBuilt.Sun();
        var p = new TestParticles(2);
        p.Pos[0] = 1000;
        p.Pos[3] = AU;
        p.Alive[0] = 1;
        p.Alive[1] = 1;
        p.ComputeAccelerations([sun]);
        Assert.Equal(0, p.Alive[0]);
        Assert.Equal(1, p.Alive[1]);
        Assert.Equal(1, p.Absorbed);
        Assert.Equal(1, sun.Damage.Absorbed);
        Assert.Equal(0, p.Acc[0]);
        Assert.True(p.Acc[3] < 0, $"acceleration {p.Acc[3]} should point at the Sun");
    }

    /// <summary>
    /// The random draws are bit-exact (Rng), but ElementsToState and Rayleigh go through cos/sin/atan2/log, which .NET
    /// and V8 need not round identically; hence 1e-12 relative, not equality.
    /// </summary>
    [Fact]
    public void MatchesTheWebBuildParticles()
    {
        var (sun, jupiter) = SunAndJupiter();
        var p = new TestParticles(40);
        Particles.PopulateBelts(p, sun, jupiter, Small);
        Assert.Equal(40, p.Count);
        var golden = new (int I, double[] Pos, double[] Vel, int Family)[]
        {
            (0, [-215153886.931492, -238348876.25693473, -38194693.8148264], [13.750028376367588, -13.632581747173788, 5.127776773671023], 0),
            (20, [-746046974.8023275, 269892085.1706235, -33034546.63774972], [-4.490766936251605, -11.655972762652347, 2.4649880614155637], 1),
            (21, [687976523.2860663, 256084347.5234309, -87281710.31754024], [-4.965363918864531, 12.596223116342006, 1.8767732368868595], 1),
            (30, [-2827285597.6403074, -6273647434.013023, -14398769.324685708], [3.9679507123138094, -1.620851264971935, 0.1335799399913925], 2),
        };
        foreach (var (i, pos, vel, family) in golden)
        {
            Assert.Equal(family, p.Family[i]);
            for (var k = 0; k < 3; k++)
            {
                AssertRel(pos[k], p.Pos[3 * i + k], 1e-12, $"pos[{i}].{k}");
                AssertRel(vel[k], p.Vel[3 * i + k], 1e-12, $"vel[{i}].{k}");
            }
        }
        p.ComputeAccelerations([sun, jupiter]);
        for (var s = 0; s < 10; s++) p.Step(Day, [sun, jupiter]);
        double[] after = [-202964746.720016, -249772797.7358049, -33710551.18191411];
        for (var k = 0; k < 3; k++) AssertRel(after[k], p.Pos[k], 1e-12, $"pos[0].{k} after 10 days");
    }

    private static void AssertRel(double expected, double actual, double rel, string what)
    {
        var d = Math.Abs(actual - expected) / Math.Abs(expected);
        Assert.True(d <= rel, $"{what}: {actual} vs web {expected} (relative {d})");
    }
}
