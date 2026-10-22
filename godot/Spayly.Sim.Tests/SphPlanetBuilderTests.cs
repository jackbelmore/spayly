using Spayly.Sim.Sph;
using Xunit.Abstractions;

namespace Spayly.Sim.Tests;

// No vitest counterpart. Shell placement after Kegerreis et al. (2019); partition after Leopardi (2006).
public class SphPlanetBuilderTests(ITestOutputHelper log)
{
    private const double MarsMass = 6.417e23;
    private static readonly Layer[] Rocky = [new(Materials.Iron, 0.3), new(Materials.Granite, 0.7)];

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(17)]
    [InlineData(100)]
    [InlineData(1000)]
    public void EqualAreaPointsAreUnitVectorsWellSpread(int n)
    {
        var pts = PlanetBuilder.EqualAreaPoints(n);
        Assert.Equal(n, pts.Length);
        foreach (var p in pts) Close.To(1, Vec3.Length(p), 12);
        if (n < 10) return;
        var ideal = Math.Sqrt(4 * Math.PI / n);
        var sum = Vec3.Zero;
        var closest = double.PositiveInfinity;
        for (var i = 0; i < n; i++)
        {
            sum += pts[i];
            for (var j = i + 1; j < n; j++) closest = Math.Min(closest, Vec3.Distance(pts[i], pts[j]));
        }
        Assert.True(closest > 0.5 * ideal, $"closest pair {closest} vs ideal spacing {ideal}");
        Assert.True(Vec3.Length(sum) / n < 0.02, $"|Σp|/n = {Vec3.Length(sum) / n}");
    }

    [Fact]
    public void ProfileIsHydrostaticAndHoldsItsMass()
    {
        var p = PlanetProfile.Solve(MarsMass, Rocky);
        log.WriteLine($"Mars-mass iron/granite: R = {p.Radius / 1e3:F1} km, core R = {p.LayerRadius[0] / 1e3:F1} km, P_c = {p.P[0] / 1e9:F1} GPa, ρ_c = {p.Rho[0]:F0}");
        Close.To(1, p.MassAt(p.Radius) / MarsMass, 12);
        Close.To(0.3, p.MassAt(p.LayerRadius[0]) / MarsMass, 3);
        // Denser than uncompressed rock, less dense than uncompressed iron everywhere: plausible bounds on R.
        var rFloor = Math.Cbrt(3 * MarsMass / (4 * Math.PI * 7800));
        var rCeil = Math.Cbrt(3 * MarsMass / (4 * Math.PI * 2680));
        Assert.InRange(p.Radius, rFloor, rCeil);
        for (var k = 1; k < p.R.Length; k++)
        {
            Assert.True(p.P[k] <= p.P[k - 1] + 1e-6 * p.P[0], $"pressure rises outward at r={p.R[k]}");
            Assert.True(p.M[k] >= p.M[k - 1], $"mass falls outward at r={p.R[k]}");
        }
        // dP/dr = −Gmρ/r² by central differences at a few radii inside each layer.
        var g = Constants.G * 1e9;
        foreach (var f in new[] { 0.2, 0.4, 0.7, 0.9 })
        {
            var r = f * p.Radius;
            var dr = 1e-3 * p.Radius;
            if (Math.Abs(r - p.LayerRadius[0]) < 5 * dr) continue;
            var numeric = (Interp(p, r + dr) - Interp(p, r - dr)) / (2 * dr);
            var model = -g * p.MassAt(r) * p.DensityAt(r) / (r * r);
            Assert.True(Math.Abs(numeric - model) < 0.01 * Math.Abs(model), $"r = {f}R: dP/dr {numeric} vs −Gmρ/r² {model}");
        }
    }

    private static double Interp(PlanetProfile p, double r)
    {
        for (var k = 1; k < p.R.Length; k++)
        {
            if (p.R[k] < r) continue;
            var t = (r - p.R[k - 1]) / (p.R[k] - p.R[k - 1]);
            return p.P[k - 1] + t * (p.P[k] - p.P[k - 1]);
        }
        return p.P[^1];
    }

    [Fact]
    public void PlacedParticlesCarryTheMassWithTheCentreOfMassAtTheOrigin()
    {
        var p = PlanetProfile.Solve(MarsMass, Rocky);
        const int n = 3000;
        var s = PlanetBuilder.Build(p, n, 42);
        double m = 0, core = 0;
        var cm = Vec3.Zero;
        var outside = 0;
        for (var i = 0; i < n; i++)
        {
            m += s.M[i];
            cm += s.Position(i) * s.M[i];
            if (s.Mat[i] == 0) core += s.M[i];
            if (Vec3.Length(s.Position(i)) > p.Radius * 1.001) outside++;
        }
        // 3000 equal addends can round by up to ~3e-13 of the sum; 1e-11 is the summation floor, not a physics tolerance.
        Close.To(1, m / MarsMass, 11);
        Assert.True(Vec3.Length(cm) / m < 1e-9 * p.Radius, $"centre of mass {cm * (1 / m)} m from the origin");
        Assert.InRange(core / m, 0.28, 0.32);
        Assert.Equal(0, outside);
        // Particle count inside r follows the profile's m(r), to within the half shell that straddles r.
        foreach (var f in new[] { 0.6, 0.75, 0.9 })
        {
            var r = f * p.Radius;
            var inside = Enumerable.Range(0, n).Count(i => Vec3.Length(s.Position(i)) < r);
            var expected = p.MassAt(r) / (MarsMass / n);
            log.WriteLine($"N(<{f}R) = {inside} vs m(r)/m_p = {expected:F1}");
            Assert.InRange(inside / expected, 0.75, 1.25);
        }
    }
}
