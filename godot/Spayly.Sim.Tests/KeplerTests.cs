namespace Spayly.Sim.Tests;

// tests/kepler.test.ts, describe('kepler'):
//   1. solves Kepler equation
//   2. places a circular 1 AU orbit at the right speed and period
//   3. round-trips elements through state vectors
//   4. samples a closed conic through the current point
// Everything after that is C#-only coverage of the ported API surface.
public class KeplerTests
{
    private const double Mu = Constants.G * Constants.SunMass;
    private const double AU = Constants.AU;
    private const double Deg = Constants.Deg;

    [Fact]
    public void SolvesKeplerEquation()
    {
        foreach (var e in new[] { 0, 0.1, 0.5, 0.9, 0.99 })
        {
            // Accumulate M exactly as the JS loop does, so the same float sequence is tested.
            for (double M = -3; M <= 3; M += 0.37)
            {
                var E = Kepler.SolveKepler(M, e);
                Close.To(((M + Math.PI) % (2 * Math.PI) + 2 * Math.PI) % (2 * Math.PI) - Math.PI, E - e * Math.Sin(E), 10);
            }
        }
    }

    [Fact]
    public void PlacesACircular1AuOrbitAtTheRightSpeedAndPeriod()
    {
        var (r, v) = Kepler.ElementsToState(Mu, new Elements(A: AU, E: 0, I: 0, Omega: 0, ArgPeri: 0, M: 0));
        Close.To(1, Vec3.Length(r) / AU, 9);
        Close.To(29.78, Vec3.Length(v), 1);
        var el = Kepler.StateToElements(Mu, r, v);
        Close.To(1, el.Period / Constants.Year, 3);
    }

    [Fact]
    public void RoundTripsElementsThroughStateVectors()
    {
        var el0 = new Elements(A: 2.3 * AU, E: 0.31, I: 12 * Deg, Omega: 80 * Deg, ArgPeri: 200 * Deg, M: 1.1);
        var (r, v) = Kepler.ElementsToState(Mu, el0);
        var el = Kepler.StateToElements(Mu, r, v);
        Close.To(1, el.A / el0.A, 9);
        Close.To(el0.E, el.E, 9);
        Close.To(el0.I, el.I, 9);
        Close.To(el0.Omega, el.Omega, 9);
        Close.To(el0.ArgPeri, el.ArgPeri, 9);
    }

    [Fact]
    public void SamplesAClosedConicThroughTheCurrentPoint()
    {
        var el0 = new Elements(A: 1.5 * AU, E: 0.2, I: 5 * Deg, Omega: 1, ArgPeri: 2, M: 0.7);
        var (r, v) = Kepler.ElementsToState(Mu, el0);
        var pts = Kepler.ConicPoints(Mu, r, v, 256);
        Assert.Equal(257, pts.Count);
        var minDist = pts.Min(p => Vec3.Length(new Vec3(p.X - r.X, p.Y - r.Y, p.Z - r.Z)));
        Assert.True(minDist / AU < 0.02, $"minDist/AU = {minDist / AU}, expected < 0.02");
        var el = Kepler.StateToElements(Mu, r, v);
        var radii = pts.Select(Vec3.Length).ToList();
        Close.To(1, radii.Min() / el.Periapsis, 3);
        Close.To(1, radii.Max() / el.Apoapsis, 3);
    }

    [Fact]
    public void ConicPointsIntoAListClearsItAndMatchesTheAllocatingOverload()
    {
        var (r, v) = Kepler.ElementsToState(Mu, new Elements(1.5 * AU, 0.2, 5 * Deg, 1, 2, 0.7));
        var reuse = Enumerable.Repeat(new Vec3(9, 9, 9), 500).ToList();
        var same = Kepler.ConicPoints(Mu, r, v, 64, 1e10, 0.3, 0.1, reuse);
        Assert.Same(reuse, same);
        Assert.Equal(Kepler.ConicPoints(Mu, r, v, 64, 1e10, 0.3, 0.1), reuse);
        Kepler.ConicPoints(Mu, r, v, 16, 1e10, 0, 0, reuse);
        Assert.Equal(17, reuse.Count);
    }

    [Fact]
    public void ConicPointsClipsAHyperbolaAtRMaxAndEmptiesOnADegenerateState()
    {
        var r = new Vec3(AU, 0, 0);
        var v = new Vec3(0, 60, 0); // well above the ~42 km/s escape speed at 1 AU
        var el = Kepler.StateToElements(Mu, r, v);
        Assert.True(el.E > 1, $"e = {el.E}, expected > 1");
        Assert.Equal(double.PositiveInfinity, el.Period);
        Assert.Equal(double.PositiveInfinity, el.Apoapsis);
        var rMax = 5 * AU;
        var pts = Kepler.ConicPoints(Mu, r, v, 100, rMax);
        Assert.Equal(101, pts.Count);
        Assert.All(pts, p => Assert.True(Vec3.Length(p) <= rMax, $"|p| = {Vec3.Length(p)} > rMax"));

        var radial = new List<Vec3> { Vec3.Zero };
        Kepler.ConicPoints(Mu, r, new Vec3(10, 0, 0), 32, 1e10, 0, 0, radial);
        Assert.Empty(radial);
    }

    [Fact]
    public void SolveKeplerHandlesHighEccentricityAndSignedZero()
    {
        // e >= 0.8 starts from pi * sign(M || 1); -0 and 0 must both take the +pi start, not throw or NaN.
        Assert.Equal(0, Kepler.SolveKepler(0, 0.95), 12);
        Assert.Equal(0, Kepler.SolveKepler(-0.0, 0.95), 12);
        var E = Kepler.SolveKepler(-2.5, 0.97);
        Close.To(-2.5, E - 0.97 * Math.Sin(E), 10);
    }
}
