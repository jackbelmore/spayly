using System.Diagnostics;
using System.Globalization;
using Xunit.Abstractions;

namespace Spayly.Sim.Tests;

/// <summary>
/// Throughput profile of Universe.Step at a huge warp, the way the app drives it (dtReal 1/60 s, pair budget 3e5).
/// Opt-in because it measures wall time: SPAYLY_BENCH=1 dotnet test --filter SpeedBench -c Release --logger "console;verbosity=detailed".
/// </summary>
public class SpeedBench(ITestOutputHelper o)
{
    static Universe Build(string moons, bool hier)
    {
        var (bodies, epochJd) = Catalog.CreateSolarSystem(new DateTime(2026, 10, 6, 0, 0, 0, DateTimeKind.Utc));
        var u = new Universe(epochJd) { MaxStepMs = 8, Hierarchical = hier };
        u.SetBodies(bodies);
        foreach (var sat in SatelliteTable.Satellites)
        {
            var wanted = moons == "all" || (moons == "major" && sat.Radius >= Catalog.MajorMoonRadiusKm);
            var present = u.ById(sat.Id);
            if (wanted && present == null && u.ById(sat.PrimaryId) is { } p) u.Add(Catalog.MakeSatellite(sat, p, u.Jd));
            else if (!wanted && present != null) u.Remove(present);
        }
        return u;
    }

    [Theory]
    [InlineData("all", false)]
    [InlineData("major", false)]
    [InlineData("none", false)]
    [InlineData("all", true)]
    [InlineData("major", true)]
    [InlineData("none", true)]
    public void Throughput(string moons, bool hier)
    {
        if (Environment.GetEnvironmentVariable("SPAYLY_BENCH") != "1") return;
        var u = Build(moons, hier);
        const double warp = 1e8, dtReal = 1.0 / 60;
        for (var i = 0; i < 30; i++) u.Step(warp * dtReal); // warm the JIT
        var frames = 300;
        double adv = 0, ms = 0, maxMs = 0, subs = 0, pairs = 0;
        var limited = 0;
        for (var i = 0; i < frames; i++)
        {
            var t0 = Stopwatch.GetTimestamp();
            adv += u.Step(warp * dtReal);
            var m = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
            ms += m; maxMs = Math.Max(maxMs, m);
            subs += u.SubstepsLastFrame; pairs += u.PairEvalsLastFrame;
            if (u.WarpLimited) limited++;
        }
        var n = u.Bodies.Count;
        o.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"moons={moons} hier={hier} bodies={n} sim-s/real-s={adv / (frames * dtReal):G4} ({adv / frames:G4} per frame) ms/frame mean={ms / frames:F2} max={maxMs:F2} substeps/frame={subs / frames:F0} pairEvals/frame={pairs / frames:G4} ns/pair={ms * 1e6 / pairs:F1} limited={limited}/{frames}"));

        // Which pairs set the substep: smallest sqrt(r^3/mu), the quantity Accelerate floors the step on.
        var list = new List<(double T, string Pair)>();
        for (var i = 0; i < n; i++)
            for (var j = i + 1; j < n; j++)
            {
                var a = u.Bodies[i]; var b = u.Bodies[j];
                var r = Vec3.Distance(a.Pos, b.Pos);
                var rr = Math.Max(r, a.Radius + b.Radius);
                list.Add((Math.Sqrt(rr * rr * rr / (Constants.G * (a.Mass + b.Mass))), a.Id + "-" + b.Id));
            }
        list.Sort((x, y) => x.T.CompareTo(y.T));
        foreach (var (t, p) in list.Take(6)) o.WriteLine($"  gravTime {p} = {t:F0} s (step {0.02 * t:F1} s)");
    }

    /// <summary>
    /// What keeping the Sun costs: the Earth system (Earth, Moon) with and without the Sun in the list, at warp 1e8 under the
    /// 8 ms budget. Without it the pair is not physically right (nothing holds Earth), so this is a timing, not a use case.
    /// </summary>
    [Theory]
    [InlineData("earth", true, false)]
    [InlineData("earth", true, true)]
    [InlineData("earth", false, false)]
    [InlineData("earth", false, true)]
    [InlineData("mars", true, true)]
    [InlineData("mars", false, true)]
    public void CostOfKeepingTheSun(string planet, bool withSun, bool hier)
    {
        if (Environment.GetEnvironmentVariable("SPAYLY_BENCH") != "1") return;
        var (all, epochJd) = Catalog.CreateSolarSystem(new DateTime(2026, 10, 6, 0, 0, 0, DateTimeKind.Utc));
        var keep = all.Where(b => b.Id == planet || b.PrimaryId == planet || (withSun && b.Id == "sun")).ToList();
        var u = new Universe(epochJd) { MaxStepMs = 8, Hierarchical = hier };
        u.SetBodies(keep);
        const double warp = 1e8, dtReal = 1.0 / 60;
        for (var i = 0; i < 30; i++) u.Step(warp * dtReal);
        double adv = 0, ms = 0, subs = 0, pairs = 0;
        var frames = 300;
        for (var i = 0; i < frames; i++)
        {
            var t0 = Stopwatch.GetTimestamp();
            adv += u.Step(warp * dtReal);
            ms += Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
            subs += u.SubstepsLastFrame; pairs += u.PairEvalsLastFrame;
        }
        o.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"keep {planet} sun={withSun} hier={hier} bodies={keep.Count} sim-s/real-s={adv / (frames * dtReal):G4} ms/frame={ms / frames:F3} substeps/frame={subs / frames:F0} pairEvals/frame={pairs / frames:G4} limited={u.WarpLimited}"));
    }
}
