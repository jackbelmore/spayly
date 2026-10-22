using System.Security.Cryptography;
using System.Text.Json;
using static Spayly.Sim.Constants;
using static Spayly.Sim.Vec3;

namespace Spayly.Sim.Tests;

// No vitest file: black holes exist only in the Godot port. Paczyński–Wiita (1980) is checked against what it is built to
// reproduce: the circular period 2π(r − r_s)√(r/μ), an unstable orbit inside the ISCO (3 r_s) and a stable one outside, a
// radial plunge that crosses the marginally bound radius (2 r_s) below c, and exact pair symmetry. A system without a hole
// must come out bit for bit as it did before PW existed (Golden/no-hole-pre-pw.json, written by the pre-M5.2 build).
public class PseudoNewtonianTests(Xunit.Abstractions.ITestOutputHelper output)
{
    /// <summary>10⁸ M☉: r_s ≈ 2.95e8 km, so the orbits near it take hours and the 0.25 s minimum substep never binds.</summary>
    private const double HoleMass = 1e8 * SunMass;
    private const double TestMass = 1e20;
    private static readonly double Rs = BlackHole.SchwarzschildRadiusKm(HoleMass);
    private static readonly double Mu = G * (HoleMass + TestMass);
    private static readonly CollideOptions Identity = new() { BodyFrame = (_, d) => d };

    private static Universe HoleAnd(double r, double vTangential, double vRadial = 0)
    {
        var hole = HandBuilt.Make("bh", BodyKind.BlackHole, Surface.Star, HoleMass, Rs, seed: 9);
        var body = HandBuilt.Make("rock", BodyKind.Asteroid, Surface.Rock, TestMass, 1, new Vec3(r, 0, 0), new Vec3(vRadial, vTangential, 0), 3);
        var u = HandBuilt.AtRest([hole, body]);
        u.CollisionHandlers.Add((ev, uu) => Collide.ResolveCollision(uu, ev, Identity));
        return u;
    }

    private static (double R, double Angle) Relative(Universe u)
    {
        var r = u.ById("rock")!.Pos - u.ById("bh")!.Pos;
        return (Length(r), Math.Atan2(r.Y, r.X));
    }

    [Fact]
    public void CircularOrbitAt10RsHasThePwPeriod()
    {
        var r = 10 * Rs;
        var u = HoleAnd(r, PseudoNewtonian.CircularSpeed(Mu, r, Rs));
        u.DynFraction = 0.002;
        var expected = PseudoNewtonian.CircularPeriod(Mu, r, Rs);
        var newton = 2 * Math.PI * Math.Sqrt(r * r * r / Mu);
        double swept = 0, last = 0, t0 = u.Time, period = double.NaN, rMin = double.MaxValue, rMax = 0;
        while (double.IsNaN(period))
        {
            var before = swept;
            var tBefore = u.Time;
            u.Step(expected / 400);
            var (d, ang) = Relative(u);
            rMin = Math.Min(rMin, d);
            rMax = Math.Max(rMax, d);
            var da = ang - last;
            da -= 2 * Math.PI * Math.Round(da / (2 * Math.PI));
            swept += da;
            last = ang;
            if (swept >= 2 * Math.PI) period = tBefore - t0 + (u.Time - tBefore) * (2 * Math.PI - before) / (swept - before);
        }
        output.WriteLine($"10 r_s circular: period {period:G9} s, PW {expected:G9} s (rel {period / expected - 1:G3}), Newton {newton:G6} s; r {rMin / Rs:F6}–{rMax / Rs:F6} r_s");
        Assert.True(Math.Abs(period / expected - 1) < 1e-4, $"period {period} s, PW {expected} s, Newton {newton} s");
        Assert.True(Math.Abs(newton / expected - 1) > 0.05, "the test must tell PW from Newton");
        Assert.True((rMax - rMin) / r < 1e-4, $"radius wandered {rMin / Rs}–{rMax / Rs} r_s");
    }

    /// <summary>Tangential speed 0.1 % short of circular at <paramref name="rInRs"/> r_s; the smallest and largest radius over 20 orbits, and whether it was captured.</summary>
    private static (double MinRs, double MaxRs, bool Captured) Perturbed(double rInRs)
    {
        var r = rInRs * Rs;
        var u = HoleAnd(r, 0.999 * PseudoNewtonian.CircularSpeed(Mu, r, Rs));
        var period = PseudoNewtonian.CircularPeriod(Mu, r, Rs);
        double min = double.MaxValue, max = 0;
        for (var k = 0; k < 20 * 200; k++)
        {
            u.Step(period / 200);
            if (u.ById("rock") == null) return (min / Rs, max / Rs, true);
            var d = Relative(u).R;
            min = Math.Min(min, d);
            max = Math.Max(max, d);
        }
        return (min / Rs, max / Rs, false);
    }

    [Fact]
    public void PerturbedOrbitInsideTheIscoPlungesAndOutsideItStaysBound()
    {
        var inside = Perturbed(2.8);
        var outsideLog = Perturbed(3.5);
        output.WriteLine($"2.8 r_s: captured={inside.Captured} range {inside.MinRs:F4}–{inside.MaxRs:F4} r_s; 3.5 r_s: captured={outsideLog.Captured} range {outsideLog.MinRs:F4}–{outsideLog.MaxRs:F4} r_s");
        Assert.True(inside.Captured, $"2.8 r_s survived 20 orbits between {inside.MinRs}–{inside.MaxRs} r_s");
        var outside = Perturbed(3.5);
        Assert.False(outside.Captured, "3.5 r_s was captured");
        Assert.True(outside.MinRs > 3.2 && outside.MaxRs < 3.6, $"3.5 r_s wandered {outside.MinRs}–{outside.MaxRs} r_s");
    }

    [Fact]
    public void RadialPlungeFrom1000RsCrosses2RsBelowCAndIsCaptured()
    {
        var r0 = 1000 * Rs;
        var u = HoleAnd(r0, 0);
        var e0 = PseudoNewtonian.Potential(Mu, r0, Rs);
        var fall = Math.PI / 2 * Math.Sqrt(r0 * r0 * r0 / (2 * Mu));
        double lastR = r0, lastV = 0, worstE = 0;
        for (var k = 0; k < 200_000; k++)
        {
            // Frames shrink with the distance left to 2 r_s, so the last sample before capture sits just outside it.
            u.Step(Math.Min(fall / 1000, 2e-3 * (lastR - 2 * Rs) / Math.Max(lastV, 1)) + 1e-3);
            if (u.ById("rock") is not Body rock) break;
            lastR = Relative(u).R;
            lastV = Length(rock.Vel - u.ById("bh")!.Vel);
            // Relative motion: v²/2 − μ/(r − r_s) is the pair's conserved energy per unit reduced mass.
            var e = 0.5 * lastV * lastV + PseudoNewtonian.Potential(Mu, lastR, Rs);
            worstE = Math.Max(worstE, Math.Abs(e - e0) / Math.Abs(PseudoNewtonian.Potential(Mu, lastR, Rs)));
        }
        output.WriteLine($"plunge from 1000 r_s: last sample {lastR / Rs:F6} r_s at {lastV / BlackHole.SpeedOfLight:F6} c (PW {Math.Sqrt(2 * (e0 - PseudoNewtonian.Potential(Mu, lastR, Rs))) / BlackHole.SpeedOfLight:F6} c), worst energy error {worstE:G3}, captured={u.ById("rock") == null}");
        Assert.Null(u.ById("rock"));
        Assert.Equal(HoleMass + TestMass, u.ById("bh")!.Mass);
        Assert.True(lastR > 2 * Rs && lastR < 2.01 * Rs, $"last sample at {lastR / Rs} r_s");
        var vPw = Math.Sqrt(2 * (e0 - PseudoNewtonian.Potential(Mu, lastR, Rs)));
        Assert.True(lastV < BlackHole.SpeedOfLight, $"{lastV / BlackHole.SpeedOfLight} c at {lastR / Rs} r_s");
        Assert.True(Math.Abs(lastV / vPw - 1) < 1e-3, $"{lastV} km/s vs PW {vPw} km/s");
        Assert.True(worstE < 1e-3, $"energy error {worstE} of the local potential");
        // Newton from rest at 1000 r_s would reach c at r_s itself and pass it inside; PW is at 0.9995 c by 2 r_s.
        Assert.True(lastV / BlackHole.SpeedOfLight > 0.99, $"{lastV / BlackHole.SpeedOfLight} c");
    }

    [Fact]
    public void HoleAndBodyConserveMomentumToRoundingAndPwEnergyTo1e9Over1e4Steps()
    {
        var r = 10 * Rs;
        var u = HoleAnd(r, PseudoNewtonian.CircularSpeed(Mu, r, Rs), 0);
        // Leapfrog's energy error goes as h²: at the default 2 % of the orbital timescale it is 1e-8, at 0.5 % 6e-10.
        u.DynFraction = 0.005;
        var p0 = Momentum(u);
        var scale = MomentumScale(u);
        var e0 = u.Energy().Total;
        var steps = 0;
        double worst = 0;
        while (steps < 10_000)
        {
            u.Step(PseudoNewtonian.CircularPeriod(Mu, r, Rs) / 500);
            steps += u.SubstepsLastFrame;
            worst = Math.Max(worst, Math.Abs(u.Energy().Total / e0 - 1));
        }
        var dp = Length(Momentum(u) - p0) / scale;
        output.WriteLine($"hole + body at 10 r_s: {steps} substeps, PW energy max drift {worst:G3}, momentum drift {dp:G3} of Σ|p|");
        // Each pair kick is equal and opposite up to rounding G·m_j·k vs G·m_i·k: exact to rounding, a random walk of ~1e-16 a step.
        Assert.True(worst < 1e-9 && dp < 1e-13, $"PW energy drift {worst}, momentum drift {dp} of Σ|p|, over {steps} substeps");
    }

    [Fact]
    public void HoleHolePairIsSymmetricWithTheTotalMassRs()
    {
        var a = HandBuilt.Make("a", BodyKind.BlackHole, Surface.Star, 30 * SunMass, BlackHole.SchwarzschildRadiusKm(30 * SunMass), new Vec3(0, 0, 0), seed: 1);
        var b = HandBuilt.Make("b", BodyKind.BlackHole, Surface.Star, 10 * SunMass, BlackHole.SchwarzschildRadiusKm(10 * SunMass), new Vec3(5000, 0, 0), seed: 2);
        var u = HandBuilt.AtRest([a, b]);
        u.Step(1e-3);
        var fa = a.Acc * a.Mass;
        var fb = b.Acc * b.Mass;
        Assert.True(Length(fa + fb) / Length(fa) < 1e-15, $"net force {Length(fa + fb)} of {Length(fa)}");
        var d = Length(b.Pos - a.Pos);
        var rsPair = BlackHole.SchwarzschildRadiusKm(40 * SunMass);
        var expected = PseudoNewtonian.Acceleration(G * 30 * SunMass, d, rsPair);
        Assert.True(Math.Abs(Length(b.Acc) / expected - 1) < 1e-6, $"{Length(b.Acc)} vs {expected} km/s²");
    }

    [Fact]
    public void PwReproducesIscoAndMarginallyBoundRadii()
    {
        // The ISCO is the minimum of the circular orbit's angular momentum; 2 r_s is where the circular speed is c.
        var best = double.MaxValue;
        var at = 0.0;
        for (var x = 2.5; x <= 4; x += 1e-4)
        {
            var l = PseudoNewtonian.CircularAngularMomentum(Mu, x * Rs, Rs);
            if (l < best) { best = l; at = x; }
        }
        Assert.True(Math.Abs(at - 3) < 2e-4, $"ISCO at {at} r_s");
        var vc = PseudoNewtonian.CircularSpeed(G * HoleMass, 2 * Rs, Rs);
        Assert.True(Math.Abs(vc / BlackHole.SpeedOfLight - 1) < 1e-12, $"{vc} km/s at 2 r_s");
    }

    [Fact]
    public void PwPeriapsisMatchesTheIntegratedOrbit()
    {
        // An eccentric orbit from 50 r_s: the periapsis the root finder predicts is the closest the integration gets.
        var r = 50 * Rs;
        var v = 0.5 * PseudoNewtonian.CircularSpeed(Mu, r, Rs);
        var u = HoleAnd(r, v);
        var predicted = PseudoNewtonian.Periapsis(u.ById("rock")!.Pos - u.ById("bh")!.Pos, u.ById("rock")!.Vel - u.ById("bh")!.Vel, Mu, Rs);
        u.DynFraction = 0.002;
        double min = double.MaxValue;
        var period = 2 * Math.PI * Math.Sqrt(r * r * r / Mu);
        for (var k = 0; k < 4000; k++)
        {
            u.Step(period / 4000);
            min = Math.Min(min, Relative(u).R);
        }
        output.WriteLine($"periapsis from 50 r_s at half circular speed: integrated {min / Rs:F5} r_s, PW root {predicted / Rs:F5} r_s, Kepler {Tidal.Periapsis(new Vec3(r, 0, 0), new Vec3(0, v, 0), Mu) / Rs:F5} r_s");
        Assert.True(Math.Abs(min / predicted - 1) < 1e-3, $"integrated periapsis {min / Rs} r_s, predicted {predicted / Rs} r_s");
        var kepler = Tidal.Periapsis(new Vec3(r, 0, 0), new Vec3(0, v, 0), Mu);
        Assert.True(Math.Abs(kepler / predicted - 1) > 0.01, $"PW {predicted / Rs} vs Kepler {kepler / Rs} r_s should differ");
    }

    [Fact]
    public void PredictorAndBeltParticlesFollowThePwOrbit()
    {
        // A probe and a belt particle on the PW circular orbit at 10 r_s both come back to their start after the PW period,
        // where Newton's shorter pull would leave them 10 % of an orbit behind.
        var hole = HandBuilt.Make("bh", BodyKind.BlackHole, Surface.Star, HoleMass, Rs, seed: 9);
        var u = HandBuilt.AtRest([hole]);
        var r = 10 * Rs;
        var mu = G * HoleMass;
        var v = PseudoNewtonian.CircularSpeed(mu, r, Rs);
        var period = PseudoNewtonian.CircularPeriod(mu, r, Rs);
        var path = Aim.PredictPath(u, new Probe(new Vec3(r, 0, 0), new Vec3(0, v, 0), 1), period,
            new PredictOptions { MaxSteps = 200_000, MaxPoints = 200_000, DynFraction = 0.002, MaxSubstep = period / 2000 });
        var end = path.Points[^1];
        var probeMiss = Length(end - new Vec3(r, 0, 0)) / r;

        var parts = new TestParticles(1) { Count = 1, MaxSubstep = period / 4000, MaxSubsteps = 100_000 };
        parts.Pos[0] = r; parts.Vel[1] = v; parts.Alive[0] = 1;
        parts.ComputeAccelerations(u.Bodies);
        parts.Step(period, u.Bodies);
        var partMiss = Length(new Vec3(parts.Pos[0], parts.Pos[1], parts.Pos[2]) - new Vec3(r, 0, 0)) / r;
        output.WriteLine($"after one PW period at 10 r_s: probe {probeMiss:G3} r from start ({path.Steps} steps), particle {partMiss:G3} r");
        Assert.Null(path.Hit);
        Assert.True(probeMiss < 1e-3, $"probe ended {probeMiss} r from its start");
        Assert.Equal(1, parts.Alive[0]);
        Assert.True(partMiss < 1e-3, $"particle ended {partMiss} r from its start");

        // A particle dropped at 1.9 r_s is inside the capture radius and is absorbed on the next force evaluation.
        var inside = new TestParticles(1) { Count = 1 };
        inside.Pos[0] = 1.9 * Rs; inside.Alive[0] = 1;
        inside.ComputeAccelerations(u.Bodies);
        Assert.Equal(0, inside.Alive[0]);
    }

    // ---- no hole: bit-identical to before ----

    /// <summary>
    /// A month of the hand-built ten-body system with a 5 km rock striking Earth and 40 belt particles, then a year's aim
    /// prediction: every body's state bits, a SHA-256 of the particle arrays and of the predicted path, and the energy bits.
    /// </summary>
    internal static Dictionary<string, object> NoHoleRun()
    {
        var u = HandBuilt.Build(withPlanets: true);
        var earth = u.ById("earth")!;
        u.Add(HandBuilt.Make("rock", BodyKind.Asteroid, Surface.Rock, 2.6e17, 2.5, earth.Pos + new Vec3(20000, 0, 0), earth.Vel + new Vec3(-5, 0.3, 0), 11));
        u.CollisionHandlers.Add((ev, uu) => Collide.ResolveCollision(uu, ev, Identity));
        var particles = new TestParticles(40);
        Particles.PopulateBelts(particles, u.ById("sun")!, u.ById("jupiter"), new BeltCounts(20, 10, 10));
        u.Particles = particles;
        for (var k = 0; k < 30; k++) u.Step(Day);
        var bodies = new List<string>();
        foreach (var b in u.Bodies)
        {
            foreach (var x in new[] { b.Mass, b.Radius, b.Pos.X, b.Pos.Y, b.Pos.Z, b.Vel.X, b.Vel.Y, b.Vel.Z })
                bodies.Add($"{b.Id}:{BitConverter.DoubleToInt64Bits(x):x16}");
        }
        static string Sha(IEnumerable<double> xs)
        {
            var bytes = new List<byte>();
            foreach (var x in xs) bytes.AddRange(BitConverter.GetBytes(x));
            return Convert.ToHexString(SHA256.HashData(bytes.ToArray()));
        }
        var path = Aim.PredictPath(u, new Probe(earth.Pos + new Vec3(0, 1e6, 0), earth.Vel + new Vec3(1, -2, 0.1), 0.01), Year);
        var pts = new List<double>();
        foreach (var p in path.Points) { pts.Add(p.X); pts.Add(p.Y); pts.Add(p.Z); }
        return new Dictionary<string, object>
        {
            ["bodies"] = bodies,
            ["particles"] = Sha(particles.Pos.Concat(particles.Vel)),
            ["path"] = Sha(pts),
            ["pathSteps"] = path.Steps,
            ["energy"] = $"{BitConverter.DoubleToInt64Bits(u.Energy().Total):x16}",
            ["time"] = $"{BitConverter.DoubleToInt64Bits(u.Time):x16}",
        };
    }

    private static string GoldenPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Spayly.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "Spayly.Sim.Tests", "Golden", "no-hole-pre-pw.json");
    }

    [Fact]
    public void SystemWithoutAHoleIsBitIdenticalToBeforePw()
    {
        var run = NoHoleRun();
        var json = JsonSerializer.Serialize(run, new JsonSerializerOptions { WriteIndented = true });
        // SPAYLY_WRITE_GOLDEN=1 regenerates the file; it was written once by the build before PW (see Golden/README.md).
        if (Environment.GetEnvironmentVariable("SPAYLY_WRITE_GOLDEN") == "1")
        {
            File.WriteAllText(GoldenPath(), json + "\n");
            return;
        }
        var expected = JsonDocument.Parse(File.ReadAllText(GoldenPath())).RootElement;
        var actual = JsonDocument.Parse(json).RootElement;
        Assert.Equal(expected.GetProperty("time").GetString(), actual.GetProperty("time").GetString());
        Assert.Equal(expected.GetProperty("energy").GetString(), actual.GetProperty("energy").GetString());
        Assert.Equal(expected.GetProperty("particles").GetString(), actual.GetProperty("particles").GetString());
        Assert.Equal(expected.GetProperty("path").GetString(), actual.GetProperty("path").GetString());
        Assert.Equal(expected.GetProperty("pathSteps").GetInt32(), actual.GetProperty("pathSteps").GetInt32());
        var eb = expected.GetProperty("bodies").EnumerateArray().Select(e => e.GetString()).ToList();
        var ab = actual.GetProperty("bodies").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Equal(eb, ab);
    }

    private static Vec3 Momentum(Universe u)
    {
        var p = Vec3.Zero;
        foreach (var b in u.Bodies) p += b.Vel * b.Mass;
        return p;
    }

    private static double MomentumScale(Universe u)
    {
        double s = 0;
        foreach (var b in u.Bodies) s += b.Mass * Length(b.Vel);
        return s;
    }
}
