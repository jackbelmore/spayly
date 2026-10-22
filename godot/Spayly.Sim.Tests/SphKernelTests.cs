using Spayly.Sim.Sph;

namespace Spayly.Sim.Tests;

// No vitest counterpart. M4 cubic spline, Monaghan (1992) eq. 2.6.
public class SphKernelTests
{
    [Theory]
    [InlineData(1.0)]
    [InlineData(0.37)]
    [InlineData(4.2e5)]
    public void NormalisesToOneRadially(double h)
    {
        // Simpson on ∫₀^{2h} 4πr² W dr; the kernel is piecewise polynomial with a knot at h, so split there.
        double Seg(double a, double b)
        {
            const int n = 2000;
            var dh = (b - a) / n;
            double s = 0;
            for (var k = 0; k <= n; k++)
            {
                var r = a + k * dh;
                s += 4 * Math.PI * r * r * Kernel.W(r, h) * (k == 0 || k == n ? 1 : k % 2 == 1 ? 4 : 2);
            }
            return s * dh / 3;
        }
        var total = Seg(0, h) + Seg(h, 2 * h);
        Assert.True(Math.Abs(total - 1) < 1e-9, $"∫W dV = {total}");
    }

    [Fact]
    public void NormalisesToOneOnA3DGrid()
    {
        // Midpoint sum over a cube grid, offset so no node sits on the origin: what a lattice of particles sees.
        const double h = 1;
        const int n = 96;
        var dx = 4 * h / n;
        double sum = 0;
        for (var i = 0; i < n; i++)
        for (var j = 0; j < n; j++)
        for (var k = 0; k < n; k++)
        {
            double x = -2 * h + (i + 0.5) * dx, y = -2 * h + (j + 0.5) * dx, z = -2 * h + (k + 0.5) * dx;
            sum += Kernel.W(Math.Sqrt(x * x + y * y + z * z), h);
        }
        var total = sum * dx * dx * dx;
        Assert.True(Math.Abs(total - 1) < 1e-3, $"Σ W ΔV = {total}");
    }

    [Fact]
    public void GradientIsExactlyAntisymmetric()
    {
        var rand = Rng.Mulberry32(7);
        for (var k = 0; k < 1000; k++)
        {
            var r = new Vec3(rand() * 4 - 2, rand() * 4 - 2, rand() * 4 - 2);
            var h = 0.5 + rand();
            var gij = Kernel.Gradient(r, h);
            var gji = Kernel.Gradient(-r, h);
            Assert.Equal(gij.X, -gji.X);
            Assert.Equal(gij.Y, -gji.Y);
            Assert.Equal(gij.Z, -gji.Z);
        }
    }

    [Fact]
    public void GradientIsTheDerivativeOfW()
    {
        var rand = Rng.Mulberry32(11);
        for (var k = 0; k < 500; k++)
        {
            var h = 0.5 + rand();
            var r = new Vec3(rand() * 2 - 1, rand() * 2 - 1, rand() * 2 - 1) * (1.9 * h / Math.Sqrt(3));
            if (Vec3.Length(r) < 0.05 * h) continue;
            var g = Kernel.Gradient(r, h);
            const double e = 1e-6;
            double Fd(Vec3 d) => (Kernel.W(Vec3.Length(r + d * e), h) - Kernel.W(Vec3.Length(r - d * e), h)) / (2 * e);
            var num = new Vec3(Fd(new Vec3(1, 0, 0)), Fd(new Vec3(0, 1, 0)), Fd(new Vec3(0, 0, 1)));
            var scale = 1 / (Math.PI * h * h * h * h);
            Assert.True(Vec3.Length(num - g) < 1e-6 * scale, $"r={r} h={h}: analytic {g} numeric {num}");
        }
    }

    [Fact]
    public void KernelIsContinuousAndCompact()
    {
        const double h = 1.3;
        Close.To(Kernel.W(h * (1 - 1e-12), h), Kernel.W(h * (1 + 1e-12), h), 9);
        Close.To(Kernel.DwDr(h * (1 - 1e-12), h), Kernel.DwDr(h * (1 + 1e-12), h), 9);
        Assert.Equal(0.0, Kernel.W(2 * h, h));
        Assert.Equal(0.0, Kernel.DwDr(2 * h, h));
        Assert.Equal(0.0, Kernel.DwDr(0, h));
        Assert.True(Kernel.W(0, h) > 0);
    }
}
