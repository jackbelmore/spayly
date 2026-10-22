using static Spayly.Sim.Constants;

namespace Spayly.Sim.Tests;

/// <summary>
/// The catalogue-backed half of tests/universe.test.ts and tests/moons.test.ts, deferred until Catalog and Universe
/// both existed. vitest titles, in order:
///   universe: 'places the planets at sensible heliocentric distances at J2000' (CatalogTests), 'has the barycentre
///   at rest' (CatalogTests), 'conserves energy over a year of integration', 'brings Earth back after one orbital
///   period', 'keeps the Moon on a monthly orbit', 'is exactly time reversible with fixed substeps', 'returns close to
///   the start with adaptive substeps', 'keeps to its pair budget…' (UniverseTests, hand-built), 'throttles the warp…'
///   (UniverseTests, hand-built).
///   moons: 'keeps the Galilean moons in their Laplace resonance for a month'.
/// Plus the golden fidelity checks against the web build (Golden/web-universe-j2000.json).
/// </summary>
public class SolarSystemTests
{
    private static Universe Build(DateTime? date = null)
    {
        var (bodies, epochJd) = Catalog.CreateSolarSystem(date ?? new DateTime(2000, 1, 1, 12, 0, 0, DateTimeKind.Utc));
        var u = new Universe(epochJd);
        u.SetBodies(bodies);
        return u;
    }

    private static Osculating Orbit(Universe u, string id, string primaryId)
    {
        var b = u.ById(id)!;
        var p = u.ById(primaryId)!;
        return Kepler.StateToElements(G * (p.Mass + b.Mass), b.Pos - p.Pos, b.Vel - p.Vel);
    }

    [Fact]
    public void ConservesEnergyOverAYearOfIntegration()
    {
        var u = Build();
        var e0 = u.Energy().Total;
        double t = 0;
        while (t < Year) { u.Step(Day); t += Day; }
        var e1 = u.Energy().Total;
        Assert.True(Math.Abs((e1 - e0) / e0) < 1e-6, $"relative energy drift {(e1 - e0) / e0}");
    }

    [Fact]
    public void BringsEarthBackAfterOneOrbitalPeriod()
    {
        var u = Build();
        var earth = u.ById("earth")!;
        var sun = u.ById("sun")!;
        var start = earth.Pos - sun.Pos;
        var el = Kepler.StateToElements(G * (SunMass + earth.Mass), start, earth.Vel - sun.Vel);
        double t = 0;
        while (t < el.Period)
        {
            var h = Math.Min(Day, el.Period - t);
            u.Step(h);
            t += h;
        }
        var end = earth.Pos - sun.Pos;
        var miss = Vec3.Length(end - start) / AU;
        Assert.True(miss < 0.01, $"Earth missed its start by {miss} AU");
    }

    [Fact]
    public void KeepsTheMoonOnAMonthlyOrbit()
    {
        var u = Build();
        var earth = u.ById("earth")!;
        var moon = u.ById("moon")!;
        double Angle() { var r = moon.Pos - earth.Pos; return Math.Atan2(r.Y, r.X); }
        var a0 = Angle();
        double turned = 0, t = 0;
        while (turned < 2 * Math.PI)
        {
            u.Step(3600);
            t += 3600;
            var a1 = Angle();
            var d = a1 - a0;
            if (d > Math.PI) d -= 2 * Math.PI;
            if (d < -Math.PI) d += 2 * Math.PI;
            turned += d;
            a0 = a1;
            if (t > 40 * Day) break;
        }
        Assert.True(t / Day > 26.5 && t / Day < 28.5, $"sidereal month came out at {t / Day} days");
    }

    [Fact]
    public void IsExactlyTimeReversibleWithFixedSubsteps()
    {
        var u = Build();
        u.MaxSubstep = 1800;
        u.MinSubstep = 1800;
        var snap = u.Snapshot();
        for (var i = 0; i < 30; i++) u.Step(Day);
        for (var i = 0; i < 30; i++) u.Step(-Day);
        foreach (var b in u.Bodies)
        {
            var orig = snap.Bodies.First(x => x.Id == b.Id);
            var drift = Vec3.Length(b.Pos - orig.Pos);
            Assert.True(drift < 1e-3, $"{b.Id} drifted {drift} km");
        }
    }

    [Fact]
    public void ReturnsCloseToTheStartWithAdaptiveSubsteps()
    {
        var u = Build();
        var snap = u.Snapshot();
        for (var i = 0; i < 60; i++) u.Step(Day);
        for (var i = 0; i < 60; i++) u.Step(-Day);
        foreach (var b in u.Bodies)
        {
            var orig = snap.Bodies.First(x => x.Id == b.Id);
            var drift = Vec3.Length(b.Pos - orig.Pos);
            Assert.True(drift < 200, $"{b.Id} drifted {drift} km");
        }
    }

    [Fact]
    public void KeepsTheGalileanMoonsInTheirLaplaceResonanceForAMonth()
    {
        var u = Build();
        double Period(string id) => Orbit(u, id, "jupiter").Period;
        double[] before = [Period("io"), Period("europa"), Period("ganymede")];
        for (var d = 0; d < 30; d++) u.Step(Day);
        double[] after = [Period("io"), Period("europa"), Period("ganymede")];
        for (var i = 0; i < 3; i++) Assert.True(Math.Abs(after[i] / before[i] - 1) < 0.02, $"period {i} drifted {after[i] / before[i] - 1}");
        Assert.InRange(after[1] / after[0], 1.9, 2.1);
        Assert.InRange(after[2] / after[1], 1.9, 2.1);
    }

    // Fidelity against the web build, not part of the vitest suite. Golden/web-universe-j2000.json, see its README.
    // Observed 2026-09-24: e0 and the substep count are bit-identical; after 30 days the energy differs by 2.3e-14
    // relative, after a year by 1.0e-13, Earth–Sun distance by 4.8e-14. Hand-built systems match to the bit, so this is
    // accumulated last-digit rounding over 3e5 substeps of 741 pairs, not a logic difference. Tolerances sit 10× above.
    [Fact]
    public void MatchesTheWebBuildsSubstepCountAndEnergyOverThirtyDays()
    {
        var u = Build();
        var e0 = u.Energy().Total;
        double t = 0; var substeps = 0;
        while (t < 30 * Day) { u.Step(Day); t += Day; substeps += u.SubstepsLastFrame; }
        var e30 = u.Energy().Total;
        while (t < Year) { u.Step(Day); t += Day; }
        var e1y = u.Energy().Total;
        var earthSunAu = Vec3.Length(u.ById("earth")!.Pos - u.ById("sun")!.Pos) / AU;
        var report = $"e0={e0:R} (web -1.9827759819233447e+35) substeps={substeps} (web 29631) e30={e30:R} (web -1.982775981923583e+35) e1y={e1y:R} (web -1.9827759819156524e+35) earthSunAu={earthSunAu:R} (web 0.9833245972149051)";
        Assert.True(e0 == -1.9827759819233447e+35, report);
        Assert.True(substeps == 29631, report);
        Assert.True(Math.Abs(e30 - -1.982775981923583e+35) <= 2e-13 * 1.98e35, report);
        Assert.True(Math.Abs(e1y - -1.9827759819156524e+35) <= 1e-12 * 1.98e35, report);
        Assert.True(Math.Abs(earthSunAu - 0.9833245972149051) <= 1e-12, report);
    }
}
