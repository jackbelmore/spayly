using Spayly.Sim.Sph;
using Xunit.Abstractions;

namespace Spayly.Sim.Tests;

// No vitest counterpart. Sized for a Debug build of the tests: each fact is a few hundred solver steps at most.
public class SphSolverTests(ITestOutputHelper log)
{
    [Fact]
    public void SedovBlastConservesEnergyToOnePercentAndMomentumToRoundoff()
    {
        // A 16³ aluminium lattice with the blast energy smoothed over the central kernel volume.
        const int k = 16;
        const double dx = 1;
        var mat = Materials.Aluminium;
        var s = new SphState(k * k * k, [mat]);
        var h0 = 1.2 * dx;
        // Mass such that an interior particle's summation density is ρ₀ exactly, so the medium starts unstressed.
        double sw = 0;
        for (var i = -3; i <= 3; i++)
        for (var j = -3; j <= 3; j++)
        for (var l = -3; l <= 3; l++) sw += Kernel.W(dx * Math.Sqrt(i * i + j * j + l * l), h0);
        var m = mat.Rho0 / sw;
        const double uBackground = 1e3;
        var n = 0;
        for (var i = 0; i < k; i++)
        for (var j = 0; j < k; j++)
        for (var l = 0; l < k; l++)
        {
            s.X[n] = (i - 0.5 * (k - 1)) * dx; s.Y[n] = (j - 0.5 * (k - 1)) * dx; s.Z[n] = (l - 0.5 * (k - 1)) * dx;
            s.M[n] = m; s.H[n] = h0; s.Rho[n] = mat.Rho0; s.U[n] = uBackground;
            n++;
        }
        const double blast = 1e13;
        double wsum = 0;
        for (var i = 0; i < n; i++) wsum += Kernel.W(Vec3.Length(s.Position(i)), h0);
        for (var i = 0; i < n; i++) s.U[i] += blast * Kernel.W(Vec3.Length(s.Position(i)), h0) / (wsum * s.M[i]);

        var solver = new SphSolver(s, new SphOptions { Gravity = GravityMode.None });
        solver.Initialize();
        var e0 = s.KineticEnergy() + s.InternalEnergy();
        double worst = 0;
        var p0 = s.Totals().Momentum;
        for (var step = 0; step < 60; step++)
        {
            solver.Step();
            var e = s.KineticEnergy() + s.InternalEnergy();
            worst = Math.Max(worst, Math.Abs(e - e0) / e0);
        }
        var (pScale, _) = s.Scales();
        var dp = Vec3.Length(s.Totals().Momentum - p0) / pScale;
        var ke = s.KineticEnergy();
        log.WriteLine($"Sedov 16³: t = {solver.Time:E3} s, worst |ΔE|/E = {worst:E3}, |ΔP|/Σm|v| = {dp:E3}, KE/E = {ke / e0:F3}, pairs/pass = {solver.PairsLastPass}");
        Assert.True(ke > 0.05 * e0, $"the blast barely moved anything: KE/E = {ke / e0}");
        Assert.True(worst < 0.01, $"energy drifted by {worst:P3}");
        Assert.True(dp < 1e-12, $"momentum drifted by {dp:E3} of Σm|v|");
    }

    private static double RmsRadius(SphState s)
    {
        double m = 0, r2 = 0;
        for (var i = 0; i < s.Count; i++)
        {
            m += s.M[i];
            r2 += s.M[i] * (s.X[i] * s.X[i] + s.Y[i] * s.Y[i] + s.Z[i] * s.Z[i]);
        }
        return Math.Sqrt(r2 / m);
    }

    [Fact]
    public void RelaxedPlanetStaysNearEquilibriumOverSoundCrossingTimes()
    {
        var p = PlanetProfile.Solve(6.417e23, [new(Materials.Iron, 0.3), new(Materials.Granite, 0.7)]);
        var s = PlanetBuilder.Build(p, 800, 3);
        var relaxTime = PlanetBuilder.Relax(s, p, new SphOptions { Gravity = GravityMode.Tree }, 150);

        var solver = new SphSolver(s, new SphOptions { Gravity = GravityMode.Tree });
        solver.Initialize();
        double mc = 0, mt = 0;
        for (var i = 0; i < s.Count; i++) { mc += s.M[i] * s.Cs[i]; mt += s.M[i]; }
        var crossing = p.Radius / (mc / mt);
        var w = s.PotentialEnergy(solver.Options.G, solver.Softening);
        var e0 = solver.TotalEnergy();
        var r0 = RmsRadius(s);
        double keMax = 0;
        var steps = 0;
        while (solver.Time < 3 * crossing && steps < 1500)
        {
            solver.Step();
            steps++;
            keMax = Math.Max(keMax, s.KineticEnergy());
            Assert.True(double.IsFinite(s.X[0] + s.Vx[0] + s.U[0]), $"non-finite state after {steps} steps");
        }
        var r1 = RmsRadius(s);
        var e1 = solver.TotalEnergy();
        log.WriteLine($"R = {p.Radius / 1e3:F0} km, relaxed for {relaxTime:F0} s, crossing {crossing:F0} s, {steps} steps: KE_max/|W| = {keMax / Math.Abs(w):E3}, Δr_rms/r_rms = {(r1 - r0) / r0:E3}, ΔE/|W| = {(e1 - e0) / Math.Abs(w):E3}");
        Assert.True(solver.Time >= 3 * crossing, $"only reached {solver.Time} s of {3 * crossing} s in {steps} steps");
        Assert.True(keMax < 1e-2 * Math.Abs(w), $"kinetic energy reached {keMax / Math.Abs(w):E3} of |W|");
        Assert.True(Math.Abs(r1 - r0) < 0.03 * r0, $"rms radius moved {(r1 - r0) / r0:P2}");
        Assert.True(Math.Abs(e1 - e0) < 1e-2 * Math.Abs(w), $"energy moved {(e1 - e0) / Math.Abs(w):E3} of |W|");
    }
}
