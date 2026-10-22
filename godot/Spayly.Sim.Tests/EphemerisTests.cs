namespace Spayly.Sim.Tests;

// No vitest file covers ephemeris.ts directly (tests/universe.test.ts and moons.test.ts exercise it through
// createSolarSystem). These pin the Julian-day arithmetic and that the Standish table reads back at T = 0.
// Expected values marked "TS" were printed by the web build (ephemeris.ts under vitest).
public class EphemerisTests
{
    [Fact]
    public void JulianDayOfJ2000IsTheJ2000Constant()
    {
        var jd = Ephemeris.JulianDay(new DateTime(2000, 1, 1, 12, 0, 0, DateTimeKind.Utc));
        Assert.Equal(2451545.0, jd);
        Assert.Equal(0, Ephemeris.CenturiesSinceJ2000(jd));
    }

    [Fact]
    public void JulianDayOfAModernDateMatchesTheWebBuild()
    {
        // TS: julianDay(new Date('2026-09-22T00:00:00Z')) === 2461305.5
        Assert.Equal(2461305.5, Ephemeris.JulianDay(new DateTime(2026, 9, 22, 0, 0, 0, DateTimeKind.Utc)));
        Assert.Equal(1.0, Ephemeris.CenturiesSinceJ2000(Constants.J2000Jd + 36525));
    }

    [Fact]
    public void DateFromJdRoundTripsJulianDay()
    {
        foreach (var date in new[]
        {
            new DateTime(2000, 1, 1, 12, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 9, 22, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(1969, 7, 20, 20, 17, 40, DateTimeKind.Utc),
        })
        {
            var back = Ephemeris.DateFromJd(Ephemeris.JulianDay(date));
            Assert.Equal(DateTimeKind.Utc, back.Kind);
            Assert.True(Math.Abs((back - date).TotalMilliseconds) <= 1, $"{date:o} came back as {back:o}");
        }
        // TS: dateFromJD(2461305.5).toISOString() === '2026-09-22T00:00:00.000Z'
        Assert.Equal(new DateTime(2026, 9, 22, 0, 0, 0, DateTimeKind.Utc), Ephemeris.DateFromJd(2461305.5));
    }

    [Fact]
    public void LocalTimesAreConvertedToUtc()
    {
        var utc = new DateTime(2000, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        Assert.Equal(Ephemeris.JulianDay(utc), Ephemeris.JulianDay(utc.ToLocalTime()));
    }

    [Fact]
    public void EarthElementsAtJ2000MatchTheTable()
    {
        var m = Ephemeris.PlanetElements["earth"];
        var el = Ephemeris.PlanetElementsAt("earth", 0);
        Assert.Equal(m.A.Value * Constants.AU, el.A);
        Assert.Equal(m.E.Value, el.E);
        Assert.Equal(m.I.Value * Constants.Deg, el.I);
        Assert.Equal(m.Node.Value * Constants.Deg, el.Omega);
        Assert.Equal((m.Lp.Value - m.Node.Value) * Constants.Deg, el.ArgPeri);
        Assert.Equal(((m.L.Value - m.Lp.Value) % 360 + 360) % 360 * Constants.Deg, el.M);
        // TS: planetElementsAt('earth', 0)
        Assert.Equal(149598261.1504425, el.A);
        Assert.Equal(1.7966014740491711, el.ArgPeri);
        Assert.Equal(6.2400213902032, el.M, 1e-13);
    }

    [Fact]
    public void PlanetTableHasTheEightPlanetsWithStandishValues()
    {
        Assert.Equal(["mercury", "venus", "earth", "mars", "jupiter", "saturn", "uranus", "neptune"], Ephemeris.PlanetElements.Keys);
        Assert.Equal((0.38709927, 0.00000037), Ephemeris.PlanetElements["mercury"].A);
        Assert.Equal((-55.12002969, 218.45945325), Ephemeris.PlanetElements["neptune"].L);
        Assert.Throws<ArgumentException>(() => Ephemeris.PlanetElementsAt("pluto", 0));
    }

    [Fact]
    public void RatesAdvanceTheElementsLinearlyInT()
    {
        var m = Ephemeris.PlanetElements["jupiter"];
        var el = Ephemeris.PlanetElementsAt("jupiter", 0.5);
        Assert.Equal((m.A.Value + m.A.Rate * 0.5) * Constants.AU, el.A);
        Assert.Equal((m.Node.Value + m.Node.Rate * 0.5) * Constants.Deg, el.Omega);
        Assert.InRange(el.M, 0, 2 * Math.PI);
    }

    [Fact]
    public void MoonElementsFollowMeeus()
    {
        var el = Ephemeris.MoonElementsAt(0);
        Assert.Equal(384400, el.A);
        Assert.Equal(0.0549, el.E);
        Assert.Equal(5.145 * Constants.Deg, el.I);
        Assert.Equal(125.0445479 * Constants.Deg, el.Omega, 1e-15);
        Assert.Equal(134.9633964 * Constants.Deg, el.M, 1e-15);
        // TS: moonElementsAt(0.26). No transcendental functions are involved, so the port is bit-exact.
        var later = Ephemeris.MoonElementsAt(0.26);
        Assert.Equal(0.08979719001510825, later.I);
        Assert.Equal(5.971980064481234, later.Omega);
        Assert.Equal(1.38102091941149, later.ArgPeri);
        Assert.Equal(0.11640541075310615, later.M);
    }
}
