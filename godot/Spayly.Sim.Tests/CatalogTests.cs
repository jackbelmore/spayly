using System.Text.Json;
using Xunit.Abstractions;

namespace Spayly.Sim.Tests;

// tests/moons.test.ts, describe('satellites') — the its that need only createSolarSystem, not a stepping Universe:
//   1. gives every planet its moons and adds the dwarf planets
//   2. places every satellite on a bound orbit of the right size and period
//   3. orients the moon orbits the way the sky does
//   4. turns each locked moon to face its planet
//   5. keeps the Galilean moons in their Laplace resonance for a month   (deferred: needs Universe, see the end)
//   6. puts the dwarf planets where they belong
//   7. counts Charon and the big moons as major
// The vitest file builds `new Universe(epochJD)` and reads bodies back with u.byId; before the first step that is the
// createSolarSystem output unchanged, so these use it directly. After that: C#-only coverage (body order, golden
// fidelity against the web build).
public class CatalogTests(ITestOutputHelper output)
{
    private static (Dictionary<string, Body> ById, List<Body> Bodies, double EpochJd) Build(DateTime? date = null)
    {
        var (bodies, epochJd) = Catalog.CreateSolarSystem(date ?? new DateTime(2026, 9, 22, 0, 0, 0, DateTimeKind.Utc));
        return (bodies.ToDictionary(b => b.Id), bodies, epochJd);
    }

    /// <summary>Inclination of a relative orbit to the ecliptic, degrees.</summary>
    private static double Inclination(Dictionary<string, Body> u, string id, string primaryId)
    {
        var b = u[id];
        var p = u[primaryId];
        var h = Vec3.Normalize(Vec3.Cross(b.Pos - p.Pos, b.Vel - p.Vel));
        return (Math.Acos(Math.Max(-1, Math.Min(1, h.Z))) * 180) / Math.PI;
    }

    private static Osculating Orbit(Dictionary<string, Body> u, string id, string primaryId)
    {
        var b = u[id];
        var p = u[primaryId];
        return Kepler.StateToElements(Constants.G * (p.Mass + b.Mass), b.Pos - p.Pos, b.Vel - p.Vel);
    }

    [Fact]
    public void GivesEveryPlanetItsMoonsAndAddsTheDwarfPlanets()
    {
        var (u, bodies, _) = Build();
        foreach (var s in SatelliteTable.Satellites)
        {
            Assert.True(u.ContainsKey(s.Id), s.Name);
            Assert.Equal(s.PrimaryId, u[s.Id].PrimaryId);
        }
        foreach (var m in SatelliteTable.MinorBodies) Assert.True(u.ContainsKey(m.Id), m.Name);
        Assert.True(u.ContainsKey("pluto"));
        Assert.True(u.ContainsKey("charon"));
        Assert.Equal(9 + SatelliteTable.Satellites.Count + SatelliteTable.MinorBodies.Count, bodies.Count);
        Assert.Equal(39, bodies.Count);
    }

    [Fact]
    public void PlacesEverySatelliteOnABoundOrbitOfTheRightSizeAndPeriod()
    {
        var (u, _, _) = Build();
        foreach (var s in SatelliteTable.Satellites)
        {
            var el = Orbit(u, s.Id, s.PrimaryId);
            Assert.True(el.E < 1, $"{s.Name} eccentricity {el.E}");
            // Osculating elements wobble around the mean ones the table publishes.
            var da = Math.Abs(el.A / s.A - 1);
            Assert.True(da < 0.05, $"{s.Name} semi-major axis off by {da}");
            var dp = Math.Abs(el.Period / (s.PeriodDays * Constants.Day) - 1);
            Assert.True(dp < 0.08, $"{s.Name} period off by {dp}");
        }
    }

    [Fact]
    public void OrientsTheMoonOrbitsTheWayTheSkyDoes()
    {
        var (u, _, _) = Build();
        // Published inclinations to the ecliptic; the mean elements are a degree or two loose.
        (string Id, string Primary, double Deg)[] expected =
        [
            ("moon", "earth", 5.15),
            ("phobos", "mars", 26.04),
            ("io", "jupiter", 2.21),
            ("callisto", "jupiter", 2.02),
            ("titan", "saturn", 27.7),
            ("iapetus", "saturn", 17.3),
            ("miranda", "uranus", 81.5), // Uranus is tipped over; its moons follow its equator
            ("oberon", "uranus", 82.3),
            ("triton", "neptune", 129.6), // retrograde
            ("charon", "pluto", 112.8),
        ];
        foreach (var (id, primary, deg) in expected)
        {
            var inc = Inclination(u, id, primary);
            Assert.True(inc > deg - 2.5, $"{id}: {inc} <= {deg - 2.5}");
            Assert.True(inc < deg + 2.5, $"{id}: {inc} >= {deg + 2.5}");
        }
    }

    /// <summary>
    /// A simulation-frame direction in a body's sphere-geometry frame (pole +y, prime meridian +x), the inverse of
    /// src/render/orientation.ts bodyOrientation composed with the sim-to-render swap. That function is
    /// qToRender · qEps · Rz(α0 + 90) · Rx(90 − δ0) · Rz(W) · qGeom; the render swap cancels qToRender, so the direction is
    /// taken ecliptic → ICRF, through the three inverse IAU rotations, then IAU body frame (pole +z) → geometry.
    /// Written out with rotations instead of three.js quaternions: the result agrees to rounding, far inside the test's
    /// 0.25 rad and 0.3 bounds.
    /// </summary>
    private static Vec3 ToGeometryFrame(Rotation rot, double jd, Vec3 dirSim)
    {
        static Vec3 Rz(Vec3 v, double a) => new(v.X * Math.Cos(a) - v.Y * Math.Sin(a), v.X * Math.Sin(a) + v.Y * Math.Cos(a), v.Z);
        static Vec3 Rx(Vec3 v, double a) => new(v.X, v.Y * Math.Cos(a) - v.Z * Math.Sin(a), v.Y * Math.Sin(a) + v.Z * Math.Cos(a));
        var d = jd - Constants.J2000Jd;
        var W = (rot.W0 + rot.Rate * d) * Constants.Deg;
        var v = Frames.EclipticToEquatorial(dirSim);
        v = Rz(v, -(rot.PoleRA + 90) * Constants.Deg);
        v = Rx(v, -(90 - rot.PoleDec) * Constants.Deg);
        v = Rz(v, -W);
        // qGeom is +90° about x, taking geometry (x, y, z) to body (x, −z, y); its inverse is below.
        return new Vec3(v.X, v.Z, -v.Y);
    }

    [Fact]
    public void TurnsEachLockedMoonToFaceItsPlanet()
    {
        var (u, _, epochJd) = Build();
        foreach (var s in SatelliteTable.Satellites)
        {
            if (!s.Locked) continue;
            var moon = u[s.Id];
            if (moon.Rotation is not Rotation rot) continue;
            var primary = u[s.PrimaryId];
            // The IAU prime meridian of a synchronous satellite points at its primary, so the body-frame +x axis should
            // line up with the direction to the planet.
            var rel = Vec3.Normalize(primary.Pos - moon.Pos);
            var (x, y, z) = ToGeometryFrame(rot, epochJd, rel);
            var lon = Math.Atan2(z, x);
            var shown = Math.Abs(Math.Sqrt(x * x + z * z) > 0.2 ? lon : 0);
            Assert.True(shown < 0.25, $"{s.Name} sub-planetary longitude {shown}");
            Assert.True(y < 0.3, $"{s.Name} sub-planetary latitude {y}");
        }
    }

    [Fact]
    public void PutsTheDwarfPlanetsWhereTheyBelong()
    {
        var (u, _, _) = Build();
        var sun = u["sun"];
        double AuFromSun(string id) => Vec3.Length(u[id].Pos - sun.Pos) / Constants.AU;
        Assert.True(AuFromSun("ceres") > 2.5, $"ceres {AuFromSun("ceres")}");
        Assert.True(AuFromSun("ceres") < 3.0, $"ceres {AuFromSun("ceres")}");
        Assert.True(AuFromSun("pluto") > 29, $"pluto {AuFromSun("pluto")}");
        Assert.True(AuFromSun("pluto") < 50, $"pluto {AuFromSun("pluto")}");
        Assert.True(AuFromSun("eris") > 38, $"eris {AuFromSun("eris")}");
        var plutoYears = Orbit(u, "pluto", "sun").Period / (365.25 * Constants.Day);
        Assert.True(plutoYears > 240, $"pluto period {plutoYears} yr");
        Assert.True(plutoYears < 255, $"pluto period {plutoYears} yr");
    }

    [Fact]
    public void CountsCharonAndTheBigMoonsAsMajor()
    {
        var major = SatelliteTable.Satellites.Where(s => s.Radius >= Catalog.MajorMoonRadiusKm).Select(s => s.Id).ToList();
        Assert.Contains("moon", major);
        Assert.Contains("titan", major);
        Assert.Contains("charon", major);
        Assert.DoesNotContain("phobos", major);
        Assert.DoesNotContain("mimas", major);
    }

    // ---- C#-only coverage ----

    [Fact]
    public void BodiesComeBackInDisplayOrder()
    {
        var (_, bodies, _) = Build();
        // The TS order, printed by createSolarSystem under vitest.
        string[] ts =
        [
            "sun", "mercury", "venus", "earth", "moon", "mars", "phobos", "deimos", "ceres", "pallas", "vesta", "jupiter", "io",
            "europa", "ganymede", "callisto", "saturn", "mimas", "enceladus", "tethys", "dione", "rhea", "titan", "iapetus",
            "uranus", "miranda", "ariel", "umbriel", "titania", "oberon", "neptune", "triton", "proteus", "nereid", "pluto",
            "charon", "haumea", "makemake", "eris",
        ];
        Assert.Equal(ts, bodies.Select(b => b.Id));
        Assert.Equal(ts, Catalog.DisplayOrder);
        Assert.Equal(10, Catalog.PlanetOrder.Count);
        Assert.Equal(["io", "europa", "ganymede", "callisto"], Catalog.SatellitesOf("jupiter").Select(s => s.Id));
    }

    [Fact]
    public void CatalogueValuesReachTheBodies()
    {
        var (u, _, _) = Build();
        Assert.Equal(9, Catalog.Specs.Count);
        Assert.Equal(Constants.SunMass, u["sun"].Mass);
        Assert.Equal(u["earth"].Mass, u["earth"].Mass0);
        Assert.Equal(new AtmosphereModel(5.4, 20600), u["titan"].Atmosphere);
        Assert.Equal(2.4, u["titan"].Visual.Atmosphere!.Value.Strength);
        Assert.Null(SatelliteTable.Satellites.Single(s => s.Id == "titan").Visual.Atmosphere);
        Assert.Equal(BodyKind.Moon, u["charon"].Kind);
        Assert.Null(u["nereid"].Rotation);
        // Eris has only a rotation period, so it gets the upright ecliptic pole.
        Assert.Equal(new Rotation(270, 90 - 23.4392911, 0, (360 * 24) / 25.9), u["eris"].Rotation);
        // Triton is retrograde, so its locked rotation runs backwards.
        Assert.True(u["triton"].Rotation!.Value.Rate < 0);
    }

    [Fact]
    public void BarycentreIsAtRestAtTheOrigin()
    {
        var (_, bodies, _) = Build();
        double m = 0;
        var p = Vec3.Zero;
        var r = Vec3.Zero;
        foreach (var b in bodies)
        {
            m += b.Mass;
            p += b.Vel * b.Mass;
            r += b.Pos * b.Mass;
        }
        Assert.True(Vec3.Length(r * (1 / m)) < 1e-6, $"barycentre at {r * (1 / m)} km");
        Assert.True(Vec3.Length(p * (1 / m)) < 1e-12, $"barycentre moving at {p * (1 / m)} km/s");
    }

    private static JsonElement LoadGolden()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Spayly.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var path = Path.Combine(dir!.FullName, "Spayly.Sim.Tests", "Golden", "web-universe-j2000.json");
        return JsonDocument.Parse(File.ReadAllText(path)).RootElement;
    }

    private static Vec3 V(JsonElement e) => new(e.GetProperty("x").GetDouble(), e.GetProperty("y").GetDouble(), e.GetProperty("z").GetDouble());

    /// <summary>Largest per-component |actual − expected| / |expected|.</summary>
    private static double MaxRel(Vec3 expected, Vec3 actual) => new[]
    {
        Math.Abs(actual.X - expected.X) / Math.Abs(expected.X),
        Math.Abs(actual.Y - expected.Y) / Math.Abs(expected.Y),
        Math.Abs(actual.Z - expected.Z) / Math.Abs(expected.Z),
    }.Max();

    /// <summary>
    /// The whole catalogue chain (Julian day → Standish/Meeus/JPL elements → Kepler → Laplace-plane frames → system
    /// recentring → barycentre shift → sort) against the web build at J2000. See Golden/README.md.
    /// </summary>
    [Fact]
    public void MatchesTheWebBuildAtJ2000()
    {
        var golden = LoadGolden();
        var (u, bodies, epochJd) = Build(new DateTime(2000, 1, 1, 12, 0, 0, DateTimeKind.Utc));
        Assert.Equal(golden.GetProperty("epochJD").GetDouble(), epochJd);
        Assert.Equal(golden.GetProperty("n").GetInt32(), bodies.Count);
        Assert.Equal(golden.GetProperty("earth0").GetProperty("mass").GetDouble(), u["earth"].Mass);
        Assert.Equal(golden.GetProperty("earth0").GetProperty("radius").GetDouble(), u["earth"].Radius);
        Assert.Equal(golden.GetProperty("sun0").GetProperty("mass").GetDouble(), u["sun"].Mass);
        // Summed in the same (display) order as Universe.barycentre, so the total mass is bit-identical.
        double mTot = 0;
        foreach (var b in bodies) mTot += b.Mass;
        Assert.Equal(golden.GetProperty("bary").GetProperty("mass").GetDouble(), mTot);

        foreach (var (key, id) in new[] { ("earth0", "earth"), ("moon0", "moon"), ("sun0", "sun") })
        {
            var g = golden.GetProperty(key);
            var relPos = MaxRel(V(g.GetProperty("pos")), u[id].Pos);
            var relVel = MaxRel(V(g.GetProperty("vel")), u[id].Vel);
            output.WriteLine($"{id}: pos max rel {relPos:E2}, vel max rel {relVel:E2}");
            Assert.True(relPos < 1e-9, $"{id} pos {u[id].Pos} vs {V(g.GetProperty("pos"))}: rel {relPos}");
            Assert.True(relVel < 1e-9, $"{id} vel {u[id].Vel} vs {V(g.GetProperty("vel"))}: rel {relVel}");
        }
    }

    // ---- Deferred: these its need a stepping Universe (src/sim/universe.ts), which is not ported yet. ----
    //
    // it('keeps the Galilean moons in their Laplace resonance for a month')
    //   const u = build()
    //   const period = (id) => orbit(u, id, 'jupiter').period
    //   before = [io, europa, ganymede] periods; 30 × u.step(DAY); after = the same
    //   for each i: |after[i] / before[i] − 1| < 0.02
    //   1.9 < after[1] / after[0] < 2.1;  1.9 < after[2] / after[1] < 2.1
    //
    // When Universe lands, also switch the six Facts above from Build()'s dictionary to `u.ById` on a Universe built
    // with SetBodies, as the vitest file does, and read the locked-moon test's jd from `u.Jd`.
}
