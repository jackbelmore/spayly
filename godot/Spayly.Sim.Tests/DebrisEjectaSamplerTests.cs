using Spayly.Sim.Debris;
using static Spayly.Sim.Vec3;

namespace Spayly.Sim.Tests;

// No vitest counterpart: the debris tiers exist only in the C# port. Facts, in order:
//   MassSumsToTheEjectaMassExactlyInAnyOrder, MomentumPlusRecoilIsZero, SpeedDistributionFollowsTheMassLaw,
//   MassAboveSpeedIsAPowerLawOfSlopeMinusThreeMu, LaunchGeometryStaysInsideTheModel, SameSeedGivesTheSameEjecta,
//   SplitMassIsExactForAwkwardTotals, CraterEjectaMassIsTheCollinsTransientVolume
// The scenario is the capture run's: a 2 km rock at 25 km/s into the Moon, D_tc ≈ 31 km, lengths in km and speeds in
// km/s as the game passes them (the sampler is unit-agnostic).
public class DebrisEjectaSamplerTests
{
    private const double R = 15.4;
    private const double A = 1.0;
    private const double U = 25.0;
    private const double RhoT = 2500;
    private const double RhoP = 3000;
    private const double EjectaMass = 1.0203e16;

    private static EjectaSample Moon(int n, uint seed = 7, EjectaScaling? s = null) =>
        EjectaSampler.Sample(R, A, U, RhoT, RhoP, s ?? EjectaScaling.Rock, EjectaMass, n, Rng.Mulberry32(seed));

    [Fact]
    public void MassSumsToTheEjectaMassExactlyInAnyOrder()
    {
        var e = Moon(50_000);
        double fwd = 0, back = 0, shuffled = 0;
        foreach (var m in e.Mass)
        {
            Assert.True(m > 0, $"mass {m} not positive");
            fwd += m;
        }
        for (var i = e.Count - 1; i >= 0; i--) back += e.Mass[i];
        var order = Enumerable.Range(0, e.Count).ToArray();
        var r = Rng.Mulberry32(99);
        for (var i = order.Length - 1; i > 0; i--)
        {
            var j = (int)Math.Floor(r() * (i + 1));
            (order[i], order[j]) = (order[j], order[i]);
        }
        foreach (var i in order) shuffled += e.Mass[i];
        Assert.True(fwd == EjectaMass, $"forward sum {fwd:R} != {EjectaMass:R}");
        Assert.True(back == EjectaMass, $"reverse sum {back:R} != {EjectaMass:R}");
        Assert.True(shuffled == EjectaMass, $"shuffled sum {shuffled:R} != {EjectaMass:R}");
    }

    [Fact]
    public void MomentumPlusRecoilIsZero()
    {
        var e = Moon(50_000);
        DD px = DD.Zero, py = DD.Zero, pz = DD.Zero;
        var naive = Vec3.Zero;
        double abs = 0;
        for (var i = 0; i < e.Count; i++)
        {
            var m = e.Mass[i];
            var v = e.Velocity[i];
            px += DD.Product(m, v.X);
            py += DD.Product(m, v.Y);
            pz += DD.Product(m, v.Z);
            naive += v * m;
            abs += m * Length(v);
        }
        var exact = new Vec3(px.Value + e.Recoil.X, py.Value + e.Recoil.Y, pz.Value + e.Recoil.Z);
        Assert.True(Length(exact) / abs < 1e-12, $"|Σmv + recoil| / Σm|v| = {Length(exact) / abs:G3} (exact sum)");
        Assert.True(Length(naive + e.Recoil) / abs < 1e-12, $"|Σmv + recoil| / Σm|v| = {Length(naive + e.Recoil) / abs:G3} (plain sum)");
        // The curtain goes up and out, so the target is pushed into itself.
        Assert.True(e.Recoil.Z < 0, $"recoil z {e.Recoil.Z} should point into the target");
        Assert.True(Math.Abs(e.Recoil.X) < 0.05 * -e.Recoil.Z && Math.Abs(e.Recoil.Y) < 0.05 * -e.Recoil.Z, $"recoil {e.Recoil} should be nearly vertical");
    }

    [Fact]
    public void SpeedDistributionFollowsTheMassLaw()
    {
        const int n = 100_000;
        var s = EjectaScaling.Rock;
        var e = Moon(n);
        var k = EjectaSampler.DensityFactor(RhoT, RhoP, s);
        const int bins = 12;
        const double vLo = 0.05, vHi = 20;
        var edges = new double[bins + 1];
        for (var b = 0; b <= bins; b++) edges[b] = vLo * Math.Pow(vHi / vLo, (double)b / bins);
        var observed = new double[bins];
        for (var i = 0; i < n; i++)
        {
            var v = Length(e.Velocity[i]);
            for (var b = 0; b < bins; b++)
            {
                if (v < edges[b] || v >= edges[b + 1]) continue;
                observed[b] += e.Mass[i] / EjectaMass;
                break;
            }
        }
        for (var b = 0; b < bins; b++)
        {
            var expected = EjectaSampler.MassFractionAboveSpeed(edges[b], R, A, U, k, s) - EjectaSampler.MassFractionAboveSpeed(edges[b + 1], R, A, U, k, s);
            // Stratified sampling: the cumulative count at any launch radius is within one particle of n·F.
            Assert.True(Math.Abs(observed[b] - expected) <= 3.0 / n,
                $"bin {edges[b]:G3}-{edges[b + 1]:G3} km/s: observed mass fraction {observed[b]:G6}, law {expected:G6}");
        }
    }

    [Theory]
    [InlineData(0.55, 1.5)]
    [InlineData(0.4, 0.55)]
    public void MassAboveSpeedIsAPowerLawOfSlopeMinusThreeMu(double mu, double c1)
    {
        // Without the rim factor (p = 0) and with the inner limit far below the range looked at, M(>v) ∝ (v/U)^(−3μ).
        const int n = 200_000;
        var s = new EjectaScaling(mu, c1, N1: 0.01, P: 0);
        var e = EjectaSampler.Sample(R, A, U, RhoT, RhoP, s, EjectaMass, n, Rng.Mulberry32(3));
        var k = EjectaSampler.DensityFactor(RhoT, RhoP, s);
        var v1 = EjectaSampler.LaunchSpeed(2, R, A, U, k, s);
        var v2 = EjectaSampler.LaunchSpeed(12, R, A, U, k, s);
        double above1 = 0, above2 = 0;
        for (var i = 0; i < n; i++)
        {
            var v = Length(e.Velocity[i]);
            if (v > v1) above1 += e.Mass[i];
            if (v > v2) above2 += e.Mass[i];
        }
        var slope = Math.Log(above1 / above2) / Math.Log(v1 / v2);
        Assert.True(Math.Abs(slope + 3 * mu) < 0.01 * 3 * mu, $"d ln M(>v) / d ln v = {slope:F4}, want {-3 * mu:F4}");
    }

    [Fact]
    public void LaunchGeometryStaysInsideTheModel()
    {
        var s = EjectaScaling.Rock;
        var e = Moon(20_000);
        var k = EjectaSampler.DensityFactor(RhoT, RhoP, s);
        Assert.Equal(1.2 * A, e.Inner);
        Assert.Equal(R, e.Outer);
        for (var i = 0; i < e.Count; i++)
        {
            var x = e.LaunchRadius[i];
            var p = e.Position[i];
            var v = e.Velocity[i];
            Assert.True(x >= e.Inner && x <= e.Outer, $"launch radius {x} outside [{e.Inner}, {e.Outer}]");
            Assert.True(p.Z == 0, $"launch point {p} off the tangent plane");
            Assert.True(Math.Abs(Length(p) - x) <= 1e-12 * x, $"|pos| {Length(p)} != x {x}");
            var speed = Length(v);
            var law = EjectaSampler.LaunchSpeed(x, R, A, U, k, s);
            Assert.True(Math.Abs(speed - law) <= 1e-12 * law, $"speed {speed} != law {law} at x {x}");
            if (speed == 0) continue;
            var angle = Math.Asin(v.Z / speed);
            Assert.True(angle >= 40 * Constants.Deg - 1e-9 && angle <= 50 * Constants.Deg + 1e-9, $"launch angle {angle / Constants.Deg} deg");
            Assert.True(v.X * p.X + v.Y * p.Y > 0, "the curtain must open away from the impact point");
        }
        // The fastest ejecta leave from next to the projectile at about C1·U·(n1)^(-1/μ), the slowest from the rim.
        Assert.True(Length(e.Velocity[0]) > 15 && Length(e.Velocity[^1]) < 0.2, $"speeds {Length(e.Velocity[0])} .. {Length(e.Velocity[^1])} km/s");
    }

    [Fact]
    public void SameSeedGivesTheSameEjecta()
    {
        var a = Moon(5000, 11);
        var b = Moon(5000, 11);
        var c = Moon(5000, 12);
        Assert.Equal(a.Mass, b.Mass);
        Assert.Equal(a.Position, b.Position);
        Assert.Equal(a.Velocity, b.Velocity);
        Assert.Equal(a.Recoil, b.Recoil);
        Assert.NotEqual(a.Velocity, c.Velocity);
    }

    [Fact]
    public void SplitMassIsExactForAwkwardTotals()
    {
        double[] totals = [1.0203e16, 0.1, 7.342e22 * (1 + Math.Pow(2, -52)), Math.PI, 1e-300, 1.7976931348623157e308 / 3];
        int[] counts = [1, 2, 3, 7, 49_999, 50_000];
        foreach (var total in totals)
        foreach (var n in counts)
        {
            var m = EjectaSampler.SplitMassExactly(total, n);
            Assert.Equal(n, m.Length);
            double fwd = 0, back = 0;
            for (var i = 0; i < n; i++) fwd += m[i];
            for (var i = n - 1; i >= 0; i--) back += m[i];
            Assert.True(fwd == total && back == total, $"total {total:R} n {n}: sums {fwd:R} / {back:R}");
            // Parts differ by one significand unit at most: (max − min)/max ≤ n / 2^52.
            Assert.True(m.Max() - m.Min() <= m.Max() * n * Math.Pow(2, -52), $"total {total:R} n {n}: masses {m.Min():R} .. {m.Max():R} not near-equal");
        }
    }

    [Fact]
    public void CraterEjectaMassIsTheCollinsTransientVolume()
    {
        const double dtc = 30890;
        var m = EjectaSampler.EjectaMassFromCrater(dtc, RhoT);
        var want = RhoT * Math.PI * dtc * dtc * dtc / (16 * Math.Sqrt(2));
        Assert.True(Math.Abs(m - want) <= 1e-14 * want, $"{m:G17} vs {want:G17}");
        Assert.InRange(m, 5e15, 2e16);
    }
}
