using Spayly.Sim.Sph;
using Xunit.Abstractions;

namespace Spayly.Sim.Tests;

// No vitest counterpart. Friends-of-friends (Davis et al. 1985) and the conservative demotion of research §2.4.
public class SphClumpsTests(ITestOutputHelper log)
{
    private sealed record Spec(Vec3 Centre, double Radius, Vec3 Velocity, Vec3 Omega);

    private static readonly Spec A = new(new Vec3(-20, 0, 0), 5, new Vec3(1, 2, 0), new Vec3(0, 0, 0.3));
    private static readonly Spec B = new(new Vec3(20, 3, 0), 4, new Vec3(-2, 0.5, 1), new Vec3(0.1, -0.2, 0.05));

    /// <summary>Two rigidly spinning lattice balls (spacing 1, uneven masses) and a sparse halo that never comes within 5 of either.</summary>
    private static (SphState S, int NA, int NB, int Halo) Synthetic()
    {
        var rand = Rng.Mulberry32(2024);
        var pts = new List<(Vec3 R, Vec3 V, double M, int Body)>();
        int Ball(Spec c, int body)
        {
            var count = 0;
            var k = (int)Math.Ceiling(c.Radius);
            for (var i = -k; i <= k; i++)
            for (var j = -k; j <= k; j++)
            for (var l = -k; l <= k; l++)
            {
                var d = new Vec3(i, j, l);
                if (Vec3.Length(d) > c.Radius) continue;
                pts.Add((c.Centre + d, c.Velocity + Vec3.Cross(c.Omega, d), 1 + 0.2 * rand(), body));
                count++;
            }
            return count;
        }
        var na = Ball(A, 0);
        var nb = Ball(B, 1);
        var halo = 0;
        while (halo < 300)
        {
            var p = new Vec3(rand() * 200 - 100, rand() * 200 - 100, rand() * 200 - 100);
            if (Vec3.Length(p) > 100 || Vec3.Distance(p, A.Centre) < A.Radius + 5 || Vec3.Distance(p, B.Centre) < B.Radius + 5) continue;
            pts.Add((p, new Vec3(rand() * 6 - 3, rand() * 6 - 3, rand() * 6 - 3), 0.5 + rand(), 2));
            halo++;
        }
        var s = new SphState(pts.Count, [Materials.Granite]);
        for (var i = 0; i < pts.Count; i++)
        {
            var (r, v, m, body) = pts[i];
            s.X[i] = r.X; s.Y[i] = r.Y; s.Z[i] = r.Z;
            s.Vx[i] = v.X; s.Vy[i] = v.Y; s.Vz[i] = v.Z;
            s.M[i] = m; s.Rho[i] = m; s.H[i] = 1.2; s.Body[i] = body;
        }
        return (s, na, nb, halo);
    }

    [Fact]
    public void DemoteConservesMassMomentumAndAngularMomentumExactly()
    {
        var (s, _, _, _) = Synthetic();
        var d = Clumps.Demote(s, new DemoteOptions { LinkingLength = 1.5, MinParticles = 20 });
        var total = s.Totals();
        var (pScale, lScale) = s.Scales();
        double dScale = 0;
        for (var i = 0; i < s.Count; i++) dScale += s.M[i] * Vec3.Length(s.Position(i));
        var closure = d.Closure;
        log.WriteLine($"clumps={d.Clumps.Count} remainder={d.RemainderMembers.Length} particles, closure ΔM={closure.Mass:E2} ΔD={Vec3.Length(closure.FirstMoment):E2} ΔP={Vec3.Length(closure.Momentum):E2} ΔL={Vec3.Length(closure.AngularMomentum):E2}");
        Assert.True(Math.Abs(closure.Mass) <= 1e-12 * total.Mass, $"mass closure {closure.Mass}");
        Assert.True(Vec3.Length(closure.FirstMoment) <= 1e-12 * dScale, $"first-moment closure {closure.FirstMoment}");
        Assert.True(Vec3.Length(closure.Momentum) <= 1e-12 * pScale, $"momentum closure {closure.Momentum}");
        Assert.True(Vec3.Length(closure.AngularMomentum) <= 1e-12 * lScale, $"angular momentum closure {closure.AngularMomentum}");

        // The exact-difference remainder is also, to rounding, what the halo particles carry.
        var direct = Ledger.Zero;
        foreach (var i in d.RemainderMembers) direct += Ledger.Point(s.M[i], s.Position(i), s.Velocity(i), Vec3.Zero);
        Assert.True(Math.Abs(direct.Mass - d.Remainder.Mass) <= 1e-10 * total.Mass, $"remainder mass {d.Remainder.Mass} vs halo {direct.Mass}");
        Assert.True(Vec3.Length(direct.Momentum - d.Remainder.Momentum) <= 1e-10 * pScale, "remainder momentum differs from the halo's");
        Assert.True(Vec3.Length(direct.AngularMomentum - d.Remainder.AngularMomentum) <= 1e-10 * lScale, "remainder angular momentum differs from the halo's");
    }

    [Fact]
    public void FriendsOfFriendsFindsTheTwoClumpsAndTheirSpin()
    {
        var (s, na, nb, halo) = Synthetic();
        var d = Clumps.Demote(s, new DemoteOptions { LinkingLength = 1.5, MinParticles = 20 });
        Assert.Equal(2, d.Clumps.Count);
        Assert.Equal(halo, d.RemainderMembers.Length);
        var byBody = d.Clumps.OrderBy(c => c.MassBySource.Length > 1 && c.MassBySource[1] > 0 ? 1 : 0).ToList();
        Assert.Equal(na, byBody[0].Members.Length);
        Assert.Equal(nb, byBody[1].Members.Length);
        foreach (var (c, spec) in new[] { (byBody[0], A), (byBody[1], B) })
        {
            var err = Vec3.Length(c.Omega - spec.Omega) / Vec3.Length(spec.Omega);
            Assert.True(err < 1e-10, $"ω = {c.Omega}, expected {spec.Omega}");
            // Rigid rotation: L = Iω, so the spin is non-zero and along a sensible axis.
            Assert.True(Vec3.Length(c.Spin) > 0);
            Close.To(1, c.Radius / Math.Cbrt(3.0 * c.Members.Length / (4 * Math.PI)), 9);
        }
    }
}
