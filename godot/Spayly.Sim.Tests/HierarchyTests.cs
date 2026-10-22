using System.Diagnostics;
using System.Globalization;
using Xunit.Abstractions;

namespace Spayly.Sim.Tests;

/// <summary>
/// The hierarchical (multiple-time-step) integrator against the flat one it replaces for speed: energy drift, orbit
/// elements of the fastest moons, and positions of Earth, the Moon and Mars against a reference run with a finer flat step.
/// SPAYLY_BENCH=1 runs the long comparison (SPAYLY_YEARS, default 100) and prints the table the decisions row cites.
/// </summary>
public class HierarchyTests(ITestOutputHelper o)
{
    static Universe Build(bool hier, double dynFraction = 0.02, double kappa = 0.5, double inner = 0.005)
    {
        var (bodies, epochJd) = Catalog.CreateSolarSystem(new DateTime(2000, 1, 1, 12, 0, 0, DateTimeKind.Utc));
        var u = new Universe(epochJd) { Hierarchical = hier, DynFraction = dynFraction, HierarchyFraction = kappa, HierarchyInnerFraction = inner };
        u.SetBodies(bodies);
        return u;
    }

    record Result(Universe U, double Drift, double Wall, int Outer, int Aborts, double MaxMoonPeriodErr);

    static Result Run(Universe u, double years)
    {
        var e0 = u.Energy().Total;
        var sw = Stopwatch.StartNew();
        var days = (int)(years * 365.25);
        double drift = 0;
        var outer = 0; var aborts = 0;
        for (var d = 0; d < days; d++)
        {
            u.Step(Constants.Day);
            outer += u.HierarchicalStepsLastFrame; aborts += u.HierarchyAbortsLastFrame;
            if (d % 365 == 364) drift = Math.Max(drift, Math.Abs(u.Energy().Total / e0 - 1));
        }
        return new Result(u, Math.Max(drift, Math.Abs(u.Energy().Total / e0 - 1)), sw.Elapsed.TotalSeconds, outer, aborts, 0);
    }

    static Osculating El(Universe u, string id, string prim)
    {
        var b = u.ById(id)!; var p = u.ById(prim)!;
        return Kepler.StateToElements(Constants.G * (p.Mass + b.Mass), b.Pos - p.Pos, b.Vel - p.Vel);
    }

    string Report(string name, Result r, Universe reference)
    {
        double Dpos(string id) => Vec3.Distance(r.U.ById(id)!.Pos, reference.ById(id)!.Pos);
        string El3(string id, string prim)
        {
            var a = El(r.U, id, prim); var b = El(reference, id, prim);
            return string.Create(CultureInfo.InvariantCulture, $"{id}: da/a={(a.A - b.A) / b.A:E1} de={a.E - b.E:E1}");
        }
        return string.Create(CultureInfo.InvariantCulture,
            $"{name,-18} wall={r.Wall,7:F1}s outer={r.Outer} aborts={r.Aborts} dE/E={r.Drift:E2} | dpos km earth={Dpos("earth"):E1} moon={Dpos("moon"):E1} mars={Dpos("mars"):E1} jupiter={Dpos("jupiter"):E1} | {El3("moon", "earth")} {El3("phobos", "mars")} {El3("io", "jupiter")}");
    }

    [Fact]
    public void AYearOfHierarchicalStepsKeepsEnergyAndMoonOrbitsAsWellAsTheFlatStep()
    {
        var reference = Run(Build(false, 0.005), 1);
        var flat = Run(Build(false), 1);
        var hier = Run(Build(true), 1);
        o.WriteLine(Report("flat 0.02", flat, reference.U));
        o.WriteLine(Report("hier default", hier, reference.U));
        Assert.True(hier.Outer > 0, "the hierarchical path never ran");
        Assert.True(hier.Drift < 1e-9, $"energy drift {hier.Drift:E2} against flat {flat.Drift:E2}");
        var moonF = El(flat.U, "moon", "earth"); var moonH = El(hier.U, "moon", "earth"); var moonR = El(reference.U, "moon", "earth");
        Assert.True(Math.Abs(moonH.A - moonR.A) < 1e-4 * moonR.A, $"Moon a: hier {moonH.A:F0} flat {moonF.A:F0} reference {moonR.A:F0}");
    }

    [Fact]
    public void LongRunComparison()
    {
        if (Environment.GetEnvironmentVariable("SPAYLY_BENCH") != "1") return;
        var years = double.Parse(Environment.GetEnvironmentVariable("SPAYLY_YEARS") ?? "100", CultureInfo.InvariantCulture);
        var reference = Run(Build(false, 0.005), years);
        o.WriteLine($"reference flat 0.005: wall={reference.Wall:F1}s dE/E={reference.Drift:E2}");
        o.WriteLine(Report("flat 0.02", Run(Build(false), years), reference.U));
        // SPAYLY_CONFIGS="kappa:inner,..." picks the hierarchical settings to compare.
        var configs = (Environment.GetEnvironmentVariable("SPAYLY_CONFIGS") ?? "0.25:0.005,0.5:0.005,1:0.005").Split(',');
        foreach (var c in configs)
        {
            var parts = c.Split(':');
            var k = double.Parse(parts[0], CultureInfo.InvariantCulture); var inner = double.Parse(parts[1], CultureInfo.InvariantCulture);
            o.WriteLine(Report($"hier k={k} in={inner}", Run(Build(true, 0.02, k, inner), years), reference.U));
        }
    }

    // A rock aimed at a planet (or at its moon) must hit at the same moment whichever integrator carries it: the
    // hierarchy only changes how the bodies far from the contact are stepped.
    static (double T, int Events, double Dist) Strike(bool hier, string target, double offsetKm, double speed)
    {
        var u = Build(hier);
        var t = u.ById(target)!;
        var dir = Vec3.Normalize(new Vec3(1, 0.3, 0.1));
        var rock = HandBuilt.Make("rock", BodyKind.Asteroid, Surface.Rock, 1e12, 0.5, t.Pos + dir * offsetKm, t.Vel - dir * speed);
        u.Add(rock);
        var events = 0;
        double hitTime = double.NaN;
        u.CollisionHandlers.Add((ev, uu) => { events++; hitTime = ev.Time; uu.Remove(ev.A.Id == "rock" ? ev.A : ev.B); });
        for (var i = 0; i < 400 && events == 0; i++) u.Step(600);
        return (hitTime, events, Vec3.Distance(u.ById(target)!.Pos, u.ById("sun")!.Pos));
    }

    [Theory]
    [InlineData("earth", 4e5, 12)]
    [InlineData("moon", 1e5, 8)]
    [InlineData("mars", 6e5, 15)]
    public void ARockAimedAtABodyInAMoonSystemHitsItAtTheSameMomentOnEitherIntegrator(string target, double offset, double speed)
    {
        var flat = Strike(false, target, offset, speed);
        var hier = Strike(true, target, offset, speed);
        o.WriteLine($"{target}: flat hit t={flat.T:F1} s, hier hit t={hier.T:F1} s");
        Assert.Equal(1, flat.Events);
        Assert.Equal(1, hier.Events);
        // An event is stamped with the end of the step that found it, so the two may differ by up to one outer step.
        Assert.InRange(hier.T - flat.T, -5000, 5000);
    }

    [Fact]
    public void RewindingAHierarchicalRunComesBackToWhereItStarted()
    {
        var u = Build(true);
        var start = u.Bodies.ToDictionary(b => b.Id, b => b.Pos);
        for (var d = 0; d < 30; d++) u.Step(Constants.Day);
        Assert.True(u.Bodies.Count > 0 && Vec3.Distance(u.ById("moon")!.Pos, start["moon"]) > 1e5, "the run did not move");
        for (var d = 0; d < 30; d++) u.Step(-Constants.Day);
        // Adaptive steps are not exactly reversible (the flat integrator's own test allows the same), so the bound is the
        // size of a step's worth of error, not zero.
        Assert.InRange(Vec3.Distance(u.ById("earth")!.Pos, start["earth"]), 0, 50);
        Assert.InRange(Vec3.Distance(u.ById("moon")!.Pos, start["moon"]), 0, 200);
        Assert.InRange(Vec3.Distance(u.ById("mars")!.Pos, start["mars"]), 0, 50);
    }
}
