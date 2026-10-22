using Spayly.Sim.Sph;

namespace Spayly.Sim.Tests;

// No vitest counterpart: the SPH module exists only in the C# port. The Tillotson form is Melosh (1989) appendix II.
public class SphTillotsonTests
{
    public static TheoryData<string> Names() => [.. Materials.All.Select(m => m.Name)];

    private static Material Get(string name) => Materials.All.First(m => m.Name == name);

    [Theory]
    [MemberData(nameof(Names))]
    public void PressureIsZeroAtReferenceDensityWithNoEnergy(string name)
    {
        var m = Get(name);
        Assert.Equal(0.0, Tillotson.Pressure(m, m.Rho0, 0));
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void CompressedBranchRisesWithDensity(string name)
    {
        var m = Get(name);
        foreach (var u in new[] { 0, 1e5, 1e6, m.Eiv, 5 * m.Ecv })
        {
            var prev = double.NegativeInfinity;
            for (var k = 0; k <= 200; k++)
            {
                var rho = m.Rho0 * (1 + 4.0 * k / 200);
                var p = Tillotson.Pressure(m, rho, u);
                Assert.True(p > prev, $"{name} u={u}: P({rho}) = {p} not above P = {prev} at the previous density");
                prev = p;
            }
        }
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void ExpandedBranchIsContinuousAtTheInterpolationEnds(string name)
    {
        var m = Get(name);
        foreach (var f in new[] { 0.3, 0.6, 0.9, 0.99 })
        {
            var rho = f * m.Rho0;
            foreach (var edge in new[] { m.Eiv, m.Ecv })
            {
                var d = 1e-9 * edge;
                var below = Tillotson.Pressure(m, rho, edge - d);
                var above = Tillotson.Pressure(m, rho, edge + d);
                var at = Tillotson.Pressure(m, rho, edge);
                var tol = 1e-6 * m.A;
                Assert.True(Math.Abs(above - below) < tol, $"{name} ρ={f}ρ₀ u={edge}: jump {above - below} Pa (tol {tol})");
                Assert.True(Math.Abs(at - below) < tol, $"{name} ρ={f}ρ₀ u={edge}: P(edge) − P(edge⁻) = {at - below} Pa");
            }
        }
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void AnalyticDerivativesMatchFiniteDifferences(string name)
    {
        var m = Get(name);
        (double Rho, double U)[] points =
        [
            (1.5 * m.Rho0, 1e6),                  // compressed
            (0.8 * m.Rho0, 0.5 * m.Eiv),          // cold expanded (compressed formula)
            (0.7 * m.Rho0, 0.5 * (m.Eiv + m.Ecv)), // interpolated
            (0.5 * m.Rho0, 2 * m.Ecv),            // expanded hot
        ];
        foreach (var (rho, u) in points)
        {
            Tillotson.Evaluate(m, rho, u, out var p, out var dr, out var du);
            double hr = 1e-6 * rho, hu = 1e-6 * u;
            var fr = (Tillotson.Pressure(m, rho + hr, u) - Tillotson.Pressure(m, rho - hr, u)) / (2 * hr);
            var fu = (Tillotson.Pressure(m, rho, u + hu) - Tillotson.Pressure(m, rho, u - hu)) / (2 * hu);
            // Scales: |P|/ρ and |P|/u bound what rounding can do to a central difference here.
            var sr = Math.Max(Math.Abs(dr), Math.Max(Math.Abs(p), m.A * 1e-3) / rho);
            var su = Math.Max(Math.Abs(du), Math.Max(Math.Abs(p), m.A * 1e-3) / u);
            Assert.True(Math.Abs(fr - dr) < 1e-5 * sr, $"{name} ρ={rho} u={u}: ∂P/∂ρ analytic {dr} vs numeric {fr}");
            Assert.True(Math.Abs(fu - du) < 1e-5 * su, $"{name} ρ={rho} u={u}: ∂P/∂u analytic {du} vs numeric {fu}");
        }
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void SoundSpeedAtReferenceIsTheBulkSoundSpeed(string name)
    {
        var m = Get(name);
        var (p, c) = Tillotson.PressureAndSoundSpeed(m, m.Rho0, 0, 0);
        Assert.Equal(0.0, p);
        Close.To(1, c / Math.Sqrt(m.A / m.Rho0), 12);
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void ColdEnergyIsTheIntegralOfColdPressure(string name)
    {
        var m = Get(name);
        foreach (var eta in new[] { 1.01, 1.3, 2.0, 3.0 })
        {
            // Simpson on ∫ (Aμ + Bμ²)/ρ² dρ from ρ₀ to ηρ₀.
            const int n = 2000;
            double a = m.Rho0, b = eta * m.Rho0, h = (b - a) / n, sum = 0;
            for (var k = 0; k <= n; k++)
            {
                var rho = a + k * h;
                var mu = rho / m.Rho0 - 1;
                var f = (m.A * mu + m.B * mu * mu) / (rho * rho);
                sum += f * (k == 0 || k == n ? 1 : k % 2 == 1 ? 4 : 2);
            }
            var numeric = sum * h / 3;
            var analytic = Tillotson.ColdEnergy(m, b);
            Assert.True(Math.Abs(analytic - numeric) < 1e-8 * numeric, $"{name} η={eta}: analytic {analytic} vs numeric {numeric}");
        }
    }

    [Fact]
    public void DisplayTemperatureRisesMonotonicallyWithPlateaus()
    {
        foreach (var m in Materials.All)
        {
            var prev = double.NegativeInfinity;
            for (var k = 0; k <= 4000; k++)
            {
                var u = 3 * m.Ecv * k / 4000;
                var t = Tillotson.DisplayTemperature(m, u);
                Assert.True(t >= prev, $"{m.Name}: T fell at u={u}");
                prev = t;
            }
            Assert.Equal(m.MeltTemperature, Tillotson.DisplayTemperature(m, m.HeatCapacity * (m.MeltTemperature - m.ReferenceTemperature) + 0.5 * m.MeltLatent));
        }
    }
}
