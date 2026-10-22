using static Spayly.Sim.Constants;
using static Spayly.Sim.Vec3;

namespace Spayly.Sim.Tests;

// No vitest file: black-hole inspirals exist only in the Godot port (M6.4). The reference is Peters (1964): a circular pair
// shrinks as a⁴ = a₀⁴ (1 − t/τ), τ = (5/256) c⁵ a₀⁴ / (G³ m₁ m₂ M); an eccentric one keeps a ∝ g(e) =
// e^(12/19) / (1 − e²) · (1 + 121 e²/304)^(870/2299). The conservative force is Paczyński–Wiita, which moves the inspiral time
// by ≈ −3.3 r_s/a₀, so the pairs start at 400 pair r_s, where that is −0.8 %.
public class InspiralTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private const double Solar = 30;
    private static readonly double M1 = Solar * SunMass;
    private static readonly double RsPair = 2 * BlackHole.SchwarzschildRadiusKm(M1);

    /// <summary>Two 30 M☉ holes about their centre of mass: separation a₀ km at apoapsis of an orbit of eccentricity e₀.</summary>
    private static Universe Pair(double a0, double e0)
    {
        var mu = G * 2 * M1;
        // Start at apoapsis r = a(1 + e) with the Newtonian apoapsis speed, the PW circular speed when e = 0.
        var r = a0 * (1 + e0);
        var v = e0 == 0 ? PseudoNewtonian.CircularSpeed(mu, r, RsPair) : Math.Sqrt(mu / a0 * (1 - e0) / (1 + e0));
        var h1 = HandBuilt.Make("bh-1", BodyKind.BlackHole, Surface.Star, M1, BlackHole.SchwarzschildRadiusKm(M1), new Vec3(r / 2, 0, 0), new Vec3(0, v / 2, 0), 3);
        var h2 = HandBuilt.Make("bh-2", BodyKind.BlackHole, Surface.Star, M1, BlackHole.SchwarzschildRadiusKm(M1), new Vec3(-r / 2, 0, 0), new Vec3(0, -v / 2, 0), 4);
        var u = HandBuilt.AtRest([h1, h2]);
        u.MaxSubstepsPerFrame = int.MaxValue;
        return u;
    }

    private static double Separation(Universe u) => Length(u.Bodies[1].Pos - u.Bodies[0].Pos);

    [Fact]
    public void CircularPairFollowsPetersThroughADecade()
    {
        var a0 = 400 * RsPair;
        var u = Pair(a0, 0);
        var tau = 5.0 / 256 * Math.Pow(BlackHole.SpeedOfLight, 5) * Math.Pow(a0, 4) / (G * G * G * M1 * M1 * 2 * M1);
        var p0 = 2 * Math.PI * Math.Sqrt(a0 * a0 * a0 / (G * 2 * M1));
        var p = u.Bodies[0].Mass * u.Bodies[0].Vel + u.Bodies[1].Mass * u.Bodies[1].Vel;
        var pScale = u.Bodies[0].Mass * Length(u.Bodies[0].Vel) * 2;
        double[] marks = [0.9, 0.7, 0.5, 0.3, 0.2, 0.1];
        var k = 0;
        var worst = 0.0;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (k < marks.Length && u.Time < 2 * tau)
        {
            // A small fraction of the current period, so a crossing is timed to far better than 1 %.
            var r = Separation(u);
            u.Step(0.05 * p0 * Math.Pow(r / a0, 1.5));
            r = Separation(u);
            while (k < marks.Length && r <= marks[k] * a0)
            {
                var peters = tau * (1 - Math.Pow(marks[k], 4));
                var err = u.Time / peters - 1;
                output.WriteLine($"a/a0 {marks[k]}: t {u.Time:G6} s, Peters {peters:G6} s, {100 * err:+0.000;-0.000} %");
                worst = Math.Max(worst, Math.Abs(err));
                k++;
            }
        }
        var p1 = u.Bodies[0].Mass * u.Bodies[0].Vel + u.Bodies[1].Mass * u.Bodies[1].Vel;
        output.WriteLine($"tau {tau:G6} s, P0 {p0:G4} s, {clock.Elapsed.TotalSeconds:F1} s wall; |Δp|/Σ|p| {Length(p1 - p) / pScale:G3}");
        Assert.Equal(marks.Length, k);
        Assert.True(worst < 0.01, $"worst {100 * worst:F3} %");
        Assert.True(Length(p1 - p) / pScale < 1e-12, $"momentum drift {Length(p1 - p) / pScale}");
    }

    private static double PetersG(double e) => Math.Pow(e, 12.0 / 19) / (1 - e * e) * Math.Pow(1 + 121.0 / 304 * e * e, 870.0 / 2299);

    [Fact]
    public void EccentricPairLosesEccentricityAsPetersPredicts()
    {
        const double e0 = 0.6;
        var a0 = 300 * RsPair;
        var u = Pair(a0, e0);
        // The substep follows the dynamical time round an eccentric orbit, which leaves leapfrog neither symplectic nor
        // reversible: at the default 0.02 that drift in e over ~10⁴ orbits (−7 % from 600 r_s) swamped the radiation
        // reaction's. At 0.005 the residual scales as 1/a₀, as PW's does (+2.3 / +1.1 / +0.4 % from 150 / 300 / 600 r_s).
        u.DynFraction = 0.005;
        var g0 = PetersG(e0);
        var p0 = 2 * Math.PI * Math.Sqrt(a0 * a0 * a0 / (G * 2 * M1));
        double rMax = 0, rMin = double.PositiveInfinity, last = Separation(u), lastDr = 0;
        var checks = 0;
        var worst = 0.0;
        double aNow = a0, eNow = e0;
        while (aNow > 0.5 * a0)
        {
            u.Step(0.002 * p0 * Math.Pow(aNow / a0, 1.5));
            var r = Separation(u);
            rMax = Math.Max(rMax, r);
            rMin = Math.Min(rMin, r);
            var dr = r - last;
            // One orbit ends at each apoapsis: r turns from rising to falling.
            if (lastDr > 0 && dr <= 0 && rMin < rMax)
            {
                aNow = (rMax + rMin) / 2;
                eNow = (rMax - rMin) / (rMax + rMin);
                // Peters' e at this a, by bisection on a/a0 = g(e)/g(e0).
                double lo = 1e-6, hi = e0;
                for (var it = 0; it < 80; it++)
                {
                    var mid = (lo + hi) / 2;
                    if (PetersG(mid) / g0 < aNow / a0) lo = mid; else hi = mid;
                }
                var ePeters = (lo + hi) / 2;
                var err = eNow / ePeters - 1;
                if (checks++ % 2000 == 0 || aNow <= 0.5 * a0) output.WriteLine($"a/a0 {aNow / a0:F4}: e {eNow:F5}, Peters {ePeters:F5}, {100 * err:+0.00;-0.00} %");
                worst = Math.Max(worst, Math.Abs(err));
                rMax = 0;
                rMin = double.PositiveInfinity;
            }
            lastDr = dr;
            last = r;
        }
        output.WriteLine($"{checks} orbits, worst {100 * worst:F3} %");
        Assert.True(eNow < 0.65 * e0, $"e only fell to {eNow}");
        Assert.True(worst < 0.02, $"worst {100 * worst:F3} %");
    }

    [Fact]
    public void UnequalMovingPairConservesMomentumToRounding()
    {
        // 30 + 11 M☉, eccentric, the whole pair moving at 300 km/s: a symmetric pair would conserve momentum by symmetry alone.
        var m2 = 11 * SunMass;
        var mu = G * (M1 + m2);
        var rs = BlackHole.SchwarzschildRadiusKm(M1) + BlackHole.SchwarzschildRadiusKm(m2);
        var a0 = 100 * rs;
        var v = Math.Sqrt(mu / a0 * 0.5 / 1.5);
        var drift = new Vec3(300, -120, 40);
        var h1 = HandBuilt.Make("bh-1", BodyKind.BlackHole, Surface.Star, M1, BlackHole.SchwarzschildRadiusKm(M1), new Vec3(1.5 * a0 * m2 / (M1 + m2), 0, 0), drift + new Vec3(0, v * m2 / (M1 + m2), 0), 3);
        var h2 = HandBuilt.Make("bh-2", BodyKind.BlackHole, Surface.Star, m2, BlackHole.SchwarzschildRadiusKm(m2), new Vec3(-1.5 * a0 * M1 / (M1 + m2), 0, 0), drift - new Vec3(0, v * M1 / (M1 + m2), 0), 4);
        var u = HandBuilt.AtRest([h1, h2]);
        u.MaxSubstepsPerFrame = int.MaxValue;
        Vec3 P() => u.Bodies[0].Mass * u.Bodies[0].Vel + u.Bodies[1].Mass * u.Bodies[1].Vel;
        double Scale() => u.Bodies[0].Mass * Length(u.Bodies[0].Vel) + u.Bodies[1].Mass * Length(u.Bodies[1].Vel);
        var p0 = P();
        var e0 = u.Energy().Total;
        var period = 2 * Math.PI * Math.Sqrt(a0 * a0 * a0 / mu);
        for (var k = 0; k < 2000; k++) u.Step(period);
        var rel = Length(P() - p0) / Scale();
        output.WriteLine($"2000 orbits: |Δp|/Σ|p| {rel:G3}, energy {e0:G6} -> {u.Energy().Total:G6} J");
        Assert.True(rel < 1e-13, $"momentum drift {rel}");
        Assert.True(u.Energy().Total < e0, "the pair should have radiated energy");
    }
}
