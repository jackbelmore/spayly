using static Spayly.Sim.Constants;

namespace Spayly.Sim.Tests;

// C#-only (no vitest counterpart): Forecast steps a copy of the universe, so each test sets it against the thing it claims to
// predict, stepped the way the game steps.
public class ForecastTests
{
    static Body Earth(Universe u) => u.ById("earth")!;
    static Body Moon(Universe u) => u.ById("moon")!;

    static Body Rock(string id, Vec3 pos, Vec3 vel, double radiusKm = 0.05) =>
        HandBuilt.Make(id, BodyKind.Asteroid, Surface.Rock, 4e11, radiusKm, pos, vel);

    static Body Hole(string id, double massKg, Vec3 pos, Vec3 vel)
    {
        var b = Body.MakeBody(new BodySpec
        {
            Id = id, Name = id, Kind = BodyKind.BlackHole, Surface = Surface.Star, Mass = massKg, Radius = 0, Pos = pos, Vel = vel,
            Visual = new BodyVisual { Color = "#000", Albedo = 0 }, Seed = 7,
        });
        return b;
    }

    /// <summary>A copy of the snapshot's universe with <paramref name="extra"/> added, for the "real" side of a comparison.</summary>
    static Universe RealWith(UniverseSnapshot snap, Body? extra)
    {
        var u = new Universe(2451545) { Time = snap.Time };
        foreach (var b in snap.Bodies) u.Add(b.Clone());
        if (extra != null) u.Add(extra.Clone());
        return u;
    }

    [Fact]
    public void WithNoHypotheticalBodyItReproducesUniverseStep()
    {
        var u = HandBuilt.Build(withPlanets: true);
        var snap = u.Snapshot();
        var result = Forecast.Run(snap, null, new ForecastOptions { Horizon = 90 * Day });
        var live = HandBuilt.Build(withPlanets: true);
        // The same chunks as the forecast's, so every substep matches.
        for (var i = 0; i < 90; i++) live.Step(Day);
        Assert.Equal(live.Bodies.Count, result.Final.Count);
        for (var i = 0; i < live.Bodies.Count; i++)
        {
            var d = Vec3.Distance(live.Bodies[i].Pos, result.Final[i].Pos);
            Assert.True(d < 1e-6, $"{live.Bodies[i].Id}: forecast {d} km from Universe.Step");
        }
        // One long Step has other substep boundaries, so only the integrator's own truncation error separates them: about
        // 1e-8 of Earth's orbital radius after a quarter year, 2e-6 of Mercury's (a whole orbit of phase) and 31 km for the Moon.
        // The bound is 1e-5 of the distance from the Sun.
        var once = HandBuilt.Build(withPlanets: true);
        once.Step(90 * Day);
        for (var i = 0; i < once.Bodies.Count; i++)
        {
            var d = Vec3.Distance(once.Bodies[i].Pos, result.Final[i].Pos);
            var bound = 1e-5 * Math.Max(Vec3.Length(once.Bodies[i].Pos - once.Bodies[0].Pos), 1e3);
            Assert.True(d < bound, $"{once.Bodies[i].Id}: forecast {d} km from one Step(90 d), bound {bound}");
        }
        Assert.Equal(ForecastEnd.Horizon, result.End);
        Assert.Empty(result.Events);
    }

    [Fact]
    public void NeverTouchesTheUniverseItWasTakenFrom()
    {
        var u = HandBuilt.Build(withPlanets: true);
        var before = u.Snapshot();
        var snap = u.Snapshot();
        Forecast.Run(snap, Rock("r", Earth(u).Pos + new Vec3(-1e6, 0, 0), Earth(u).Vel + new Vec3(10, 0, 0)), new ForecastOptions { Horizon = 10 * Day });
        for (var i = 0; i < u.Bodies.Count; i++)
        {
            Assert.Equal(before.Bodies[i].Pos, u.Bodies[i].Pos);
            Assert.Equal(before.Bodies[i].Vel, u.Bodies[i].Vel);
            Assert.Equal(before.Bodies[i].Pos, snap.Bodies[i].Pos);
        }
        Assert.Equal(before.Bodies.Count, u.Bodies.Count);
        Assert.Equal(before.Time, u.Time);
    }

    [Fact]
    public void ARockAimedAtEarthHitsItWhenTheRealSimDoes()
    {
        var u = HandBuilt.Build(withPlanets: true);
        var earth = Earth(u);
        var rock = Rock("rock", earth.Pos + new Vec3(-1e6, 0, 0), earth.Vel + new Vec3(10, 0, 0));
        var snap = u.Snapshot();
        var f = Forecast.Run(snap, rock, new ForecastOptions { Horizon = 2 * Year });
        Assert.Equal(ForecastEnd.Contact, f.End);
        var hit = Assert.Single(f.Events, e => e.Kind == ForecastKind.Contact);
        Assert.Equal("earth", hit.SubjectId);

        var real = RealWith(snap, rock);
        double realT = double.NaN;
        var substep = 0.0;
        real.CollisionHandlers.Add((ev, uu) =>
        {
            if (!double.IsNaN(realT)) return;
            realT = ev.Time - snap.Time;
            substep = real.DynFraction * real.ShortestDynamicalTime;
        });
        for (var i = 0; i < 400 && double.IsNaN(realT); i++) real.Step(600);
        Assert.False(double.IsNaN(realT), "the real sim never saw the contact");
        var bound = Math.Min(real.MaxSubstep, Math.Max(real.MinSubstep, substep));
        Assert.True(Math.Abs(hit.Time - realT) <= bound, $"forecast contact {hit.Time} s, real {realT} s, one substep {bound} s");
        Assert.Equal(f.Duration, hit.Time, 6);
    }

    static (Body Hole, Vec3 Rel) MoonFlyby(Universe u, double massKg, double missKm)
    {
        var moon = Moon(u);
        // 30 km/s straight down −y past the Moon, 1e6 km out, offset in x by the miss distance; Earth lies 384 400 km off to the side.
        var hole = Hole("hole", massKg, moon.Pos + new Vec3(missKm, 1e6, 0), moon.Vel + new Vec3(0, -30, 0));
        return (hole, Vec3.Zero);
    }

    [Fact]
    public void AHolePassingTheMoonEntersItsTidalRadiusWhenTheRealTidalCheckFires()
    {
        var u = HandBuilt.Build(withPlanets: true);
        var (hole, _) = MoonFlyby(u, 1e25, 5000);
        var snap = u.Snapshot();
        var f = Forecast.Run(snap, hole, new ForecastOptions { Horizon = 30 * Day });
        var roche = Assert.Single(f.Events, e => e.Kind == ForecastKind.RocheEntry && e.SubjectId == "moon");
        Assert.Equal("hole", roche.CauseId);
        Assert.Contains(f.Events, e => e.Kind == ForecastKind.ClosestApproach && e.SubjectId == "moon");

        // The game's own check, on a copy stepped in 5 s frames until it names the pair.
        var real = RealWith(snap, hole);
        var found = new List<TidalCandidate>();
        double realT = double.NaN;
        for (var i = 0; i < 30000 && double.IsNaN(realT); i++)
        {
            real.Step(5);
            Tidal.TidalCheck(real, 0, found);
            if (found.Any(c => c.Primary.Id == "hole" && c.Secondary.Id == "moon")) realT = real.Time - snap.Time;
        }
        Assert.False(double.IsNaN(realT), "the real tidal check never fired");
        Assert.True(Math.Abs(roche.Time - realT) < 120, $"forecast Roche entry {roche.Time} s, real {realT} s");
        Assert.True(roche.DistanceKm > 5000, $"limit {roche.DistanceKm} km should reach past the 5000 km miss");
    }

    [Fact]
    public void AHoleThatMissesTheMoonByMoreThanItsTidalRadiusRaisesNoRocheEvent()
    {
        var u = HandBuilt.Build(withPlanets: true);
        var (hole, _) = MoonFlyby(u, 1e25, 60000);
        var f = Forecast.Run(u.Snapshot(), hole, new ForecastOptions { Horizon = 30 * Day });
        Assert.DoesNotContain(f.Events, e => e.Kind == ForecastKind.RocheEntry);
        var near = Assert.Single(f.Events, e => e.Kind == ForecastKind.ClosestApproach && e.SubjectId == "moon");
        Assert.InRange(near.DistanceKm, 50000, 70000);
    }

    [Fact]
    public void ABudgetTooSmallForTheHorizonSaysTheForecastIsTruncated()
    {
        var u = HandBuilt.Build(withPlanets: true);
        var f = Forecast.Run(u.Snapshot(), Rock("r", Earth(u).Pos + new Vec3(0, 1e7, 0), Earth(u).Vel), new ForecastOptions { Horizon = 2 * Year, SubstepBudget = 200 });
        Assert.True(f.Truncated);
        Assert.Equal(ForecastEnd.Budget, f.End);
        Assert.True(f.Duration < f.Horizon, $"covered {f.Duration} of {f.Horizon} s");
        Assert.True(f.Substeps <= 200, $"{f.Substeps} substeps against a budget of 200");
    }

    [Fact]
    public void ACancelledForecastThrows()
    {
        var u = HandBuilt.Build(withPlanets: true);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => Forecast.Run(u.Snapshot(), null, cancel: cts.Token));
    }

    [Fact]
    public void ABoundRockSlungOutByAHeadOnHoleIsReportedEjectedOnce()
    {
        var u = HandBuilt.Build(withPlanets: true);
        var sun = u.ById("sun")!;
        // Just under the 42.1 km/s solar escape speed at 1 AU, on the far side from every planet.
        var rock = Rock("rock", sun.Pos + new Vec3(-AU, 0, 0), sun.Vel + new Vec3(0, 41.9, 0));
        u.Add(rock);
        // A hole coming the other way with a 1.7e5 km impact parameter, about its own 90° deflection radius at 62 km/s.
        var hole = Hole("hole", 1e28, rock.Pos + new Vec3(1.7e5, 3e6, 0), sun.Vel + new Vec3(0, -20, 0));
        var f = Forecast.Run(u.Snapshot(), hole, new ForecastOptions { Horizon = 60 * Day });
        var ejected = Assert.Single(f.Events, e => e.Kind == ForecastKind.Ejection);
        Assert.Equal("rock", ejected.SubjectId);
    }

    [Fact]
    public void ARockAlreadyFasterThanEscapeSpeedIsNotReportedEjected()
    {
        var u = HandBuilt.Build(withPlanets: true);
        var earth = Earth(u);
        var dir = Vec3.Normalize(earth.Vel - u.ById("sun")!.Vel);
        var rock = Rock("rock", earth.Pos + new Vec3(0, 0, 5e6), earth.Vel + dir * 20);
        var f = Forecast.Run(u.Snapshot(), rock, new ForecastOptions { Horizon = 0.5 * Year });
        Assert.DoesNotContain(f.Events, e => e.Kind == ForecastKind.Ejection);
    }

    [Fact]
    public void AHeavyHoleBetweenTheSunAndEarthCapturesAndDeflects()
    {
        var u = HandBuilt.Build(withPlanets: true);
        var earth = Earth(u);
        // A solar-mass hole 0.05 AU from Earth at rest relative to it: Earth is bound to it, and its path bends.
        var hole = Hole("hole", SunMass, earth.Pos + new Vec3(0.05 * AU, 0, 0), earth.Vel);
        var f = Forecast.Run(u.Snapshot(), hole, new ForecastOptions { Horizon = 0.25 * Year });
        Assert.Contains(f.Events, e => e.Kind == ForecastKind.Capture && e.SubjectId == "earth");
        Assert.Contains(f.Paths, p => p.Id == "earth" && !p.Hypothetical);
        Assert.True(f.Paths[0].Hypothetical);
    }

    [Fact]
    public void ADistantRockDeflectsNothingAndDrawsOnlyItself()
    {
        var u = HandBuilt.Build(withPlanets: true);
        var f = Forecast.Run(u.Snapshot(), Rock("r", Earth(u).Pos + new Vec3(0, 1e7, 0), Earth(u).Vel + new Vec3(1, 0, 0)), new ForecastOptions { Horizon = 100 * Day });
        Assert.Single(f.Paths);
    }

    [Fact]
    public void TheGhostPathRunsFromThePlacementToTheContactEvenWhenShort()
    {
        var u = HandBuilt.Build(withPlanets: true);
        var earth = Earth(u);
        var rock = Rock("rock", earth.Pos + new Vec3(-1e6, 0, 0), earth.Vel + new Vec3(10, 0, 0));
        var f = Forecast.Run(u.Snapshot(), rock, new ForecastOptions { Horizon = 2 * Year });
        var path = f.Paths[0];
        Assert.True(path.Hypothetical);
        Assert.True(path.Points.Count >= 10, $"{path.Points.Count} points for a one-day flight");
        Assert.Equal(rock.Pos, path.Points[0]);
        Assert.Equal(0, path.Times[0]);
        Assert.Equal(f.Duration, path.Times[^1], 6);
    }

    [Fact]
    public void ALongPathIsThinnedToTheCapAndKeepsItsEnds()
    {
        var u = HandBuilt.Build(withPlanets: true);
        var earth = Earth(u);
        var rock = Rock("rock", earth.Pos + new Vec3(0, 0, 3e6), earth.Vel + new Vec3(0, 0, 1));
        var f = Forecast.Run(u.Snapshot(), rock, new ForecastOptions { Horizon = 3 * Year, MaxPathPoints = 300 });
        var path = f.Paths[0];
        Assert.InRange(path.Points.Count, 50, 300);
        Assert.Equal(rock.Pos, path.Points[0]);
        Assert.Equal(f.Duration, path.Times[^1], 6);
    }
}
