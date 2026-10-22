using Spayly.Sim.Sph;
using Xunit.Abstractions;

namespace Spayly.Sim.Tests;

// No vitest counterpart. Barnes & Hut (1986) with the Barnes (1994) δ-guarded opening test.
public class SphOctreeTests(ITestOutputHelper log)
{
    private const int N = 2000;
    private const double G = 1;
    private const double Eps = 0.01;

    /// <summary>A uniform ball of radius 1 and unit total mass.</summary>
    private static (double[] X, double[] Y, double[] Z, double[] M) Ball(uint seed)
    {
        var rand = Rng.Mulberry32(seed);
        double[] x = new double[N], y = new double[N], z = new double[N], m = new double[N];
        for (var i = 0; i < N; i++)
        {
            Vec3 p;
            do p = new Vec3(rand() * 2 - 1, rand() * 2 - 1, rand() * 2 - 1);
            while (Vec3.Length(p) > 1);
            x[i] = p.X; y[i] = p.Y; z[i] = p.Z; m[i] = 1.0 / N;
        }
        return (x, y, z, m);
    }

    private static Vec3 Direct(double[] x, double[] y, double[] z, double[] m, Vec3 at)
    {
        double ax = 0, ay = 0, az = 0;
        for (var j = 0; j < x.Length; j++)
        {
            double dx = at.X - x[j], dy = at.Y - y[j], dz = at.Z - z[j];
            var r2 = dx * dx + dy * dy + dz * dz + Eps * Eps;
            var inv3 = G * m[j] / (r2 * Math.Sqrt(r2));
            ax -= dx * inv3; ay -= dy * inv3; az -= dz * inv3;
        }
        return new Vec3(ax, ay, az);
    }

    /// <summary>Particles in the outer part of the ball (where the net pull is not a near-cancellation) plus two outside points.</summary>
    private static List<Vec3> Probes(double[] x, double[] y, double[] z)
    {
        var list = new List<Vec3>();
        for (var i = 0; i < N; i += 7)
        {
            var p = new Vec3(x[i], y[i], z[i]);
            if (Vec3.Length(p) > 0.6) list.Add(p);
        }
        list.Add(new Vec3(1.5, 0.2, -0.1));
        list.Add(new Vec3(-0.3, 3.0, 0.4));
        return list;
    }

    [Fact]
    public void ForceIsWithinOnePercentOfDirectSummationAtTheta05()
    {
        var (x, y, z, m) = Ball(3);
        var tree = new Octree();
        tree.Build(x, y, z, m, N);
        double worst = 0;
        var probes = Probes(x, y, z);
        foreach (var p in probes)
        {
            var exact = Direct(x, y, z, m, p);
            var approx = tree.Accel(p.X, p.Y, p.Z, G, Eps * Eps, 0.5, true);
            var err = Vec3.Length(approx - exact) / Vec3.Length(exact);
            worst = Math.Max(worst, err);
            Assert.True(err < 0.01, $"at {p}: tree {approx} vs direct {exact}, relative error {err:E3}");
        }
        log.WriteLine($"quadrupole θ=0.5: worst relative error {worst:E3} over {probes.Count} probes, {tree.NodeCount} nodes");
    }

    [Fact]
    public void MonopoleRmsErrorIsBelowOnePercentAtTheta05()
    {
        var (x, y, z, m) = Ball(5);
        var tree = new Octree();
        tree.Build(x, y, z, m, N);
        double sum = 0;
        var probes = Probes(x, y, z);
        foreach (var p in probes)
        {
            var exact = Direct(x, y, z, m, p);
            var approx = tree.Accel(p.X, p.Y, p.Z, G, Eps * Eps, 0.5, false);
            var err = Vec3.Length(approx - exact) / Vec3.Length(exact);
            sum += err * err;
        }
        var rms = Math.Sqrt(sum / probes.Count);
        log.WriteLine($"monopole θ=0.5: rms relative error {rms:E3}");
        Assert.True(rms < 0.01, $"rms relative error {rms}");
    }

    [Fact]
    public void TinyOpeningAngleReproducesDirectSummation()
    {
        var (x, y, z, m) = Ball(9);
        var tree = new Octree();
        tree.Build(x, y, z, m, N);
        foreach (var p in Probes(x, y, z).Take(40))
        {
            var exact = Direct(x, y, z, m, p);
            var approx = tree.Accel(p.X, p.Y, p.Z, G, Eps * Eps, 1e-6, true);
            Assert.True(Vec3.Length(approx - exact) < 1e-12 * Vec3.Length(exact), $"at {p}: {approx} vs {exact}");
        }
    }
}
