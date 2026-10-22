namespace Spayly.Sim.Tests;

// C#-only (the web build has no shower): the spread generator behind the game's meteor shower.
public class ShowerTests
{
    static readonly Vec3 From = new(1.2e5, -3e4, 2e3);
    static readonly Vec3 TargetVel = new(-12, 27, 0.4);
    static readonly Vec3 Aimed = TargetVel + Vec3.Normalize(new Vec3(-1, 0.3, 0.05)) * 20;
    const double EarthRadius = 6371;
    const double Flight = 6000;

    static ShowerRock[] Earthbound(int n = 24, double rockRadius = 0.0035, uint seed = 7) =>
        Shower.Spread(n, From, Aimed, TargetVel, EarthRadius, Flight, rockRadius, seed);

    [Fact]
    public void TheSameSeedGivesTheSameShowerBitForBitAndAnotherSeedDoesNot()
    {
        Assert.Equal(Earthbound(), Earthbound());
        Assert.NotEqual(Earthbound(), Earthbound(seed: 8));
    }

    [Fact]
    public void CountsAreClampedTo8Through24AndPiecesConserveMass()
    {
        Assert.Equal(8, Shower.Clamp(1));
        Assert.Equal(16, Shower.Clamp(16));
        Assert.Equal(24, Shower.Clamp(100));
        foreach (var n in new[] { 8, 13, 24 })
        {
            var d = Shower.PieceDiameter(10000, n);
            Close.To(1e12, n * d * d * d, -2);
        }
    }

    [Fact]
    public void NoTwoRocksStartCloserThanTheMinimumSeparation()
    {
        // A 20 m rock's pieces, and Theia's (3,000 km radius, so ~1,040 km each at 24): the window stretches for the big ones.
        foreach (var (r, n) in new[] { (0.0035, 24), (0.0035, 8), (1040.0, 24), (1040.0, 8) })
        {
            var rocks = Earthbound(n, r);
            Assert.Equal(n, rocks.Length);
            var min = double.PositiveInfinity;
            for (var i = 0; i < n; i++)
            for (var j = i + 1; j < n; j++)
                min = Math.Min(min, Vec3.Distance(rocks[i].Pos, rocks[j].Pos));
            Assert.True(min >= Shower.MinSeparationRadii * r, $"r={r} km n={n}: closest pair {min} km apart");
        }
    }

    [Fact]
    public void SpeedsStayInTheSpreadWithTheFastestLeadingAndDelaysInsideTheWindow()
    {
        var rocks = Earthbound();
        var aimed = Vec3.Length(Aimed - TargetVel);
        var window = Shower.Window(24, Flight, aimed, 0.0035);
        Close.To(0.1 * Flight, window, 9);
        for (var k = 0; k < rocks.Length; k++)
        {
            var s = Vec3.Length(rocks[k].Vel - TargetVel);
            Assert.True(Math.Abs(s / aimed - 1) <= Shower.SpeedSpread + 1e-12, $"rock {k}: {s} km/s against {aimed}");
            Assert.True(rocks[k].Delay > 0 && rocks[k].Delay < window, $"rock {k}: delay {rocks[k].Delay} s outside (0, {window})");
            if (k == 0) continue;
            Assert.True(rocks[k].Delay > rocks[k - 1].Delay, $"rock {k} starts before rock {k - 1}");
            Assert.True(s <= Vec3.Length(rocks[k - 1].Vel - TargetVel), $"rock {k} is faster than rock {k - 1} ahead of it");
        }
    }

    [Fact]
    public void EveryRockPassesTheLaunchPointAndAimsInsideTheTargetsDisc()
    {
        var rocks = Earthbound();
        var rel = Aimed - TargetVel;
        var aimPoint = From + rel * Flight;
        foreach (var r in rocks)
        {
            var dir = Vec3.Normalize(r.Vel - TargetVel);
            // On its own line, the launch point `speed × delay` ahead of where it starts.
            var toFrom = From - r.Pos;
            Close.To(Vec3.Length(rel) * r.Delay, Vec3.Length(toFrom), 6);
            Assert.True(Vec3.Length(Vec3.Cross(toFrom, dir)) < 1e-6 * Vec3.Length(toFrom), "the rock's line misses the launch point");
            // Its miss distance from the aimed point, across the line of flight, is inside the fill disc.
            var off = aimPoint - From;
            var miss = Vec3.Length(off - dir * Vec3.Dot(off, dir));
            Assert.True(miss <= Shower.AimFill * EarthRadius, $"aims {miss} km off the aimed point");
        }
    }

    [Fact]
    public void RefusesAShotWithNoSpeedRelativeToTheTarget()
    {
        Assert.Throws<ArgumentException>(() => Shower.Spread(8, From, TargetVel, TargetVel, EarthRadius, Flight, 0.01, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Shower.Spread(0, From, Aimed, TargetVel, EarthRadius, Flight, 0.01, 1));
    }
}
