using Xunit.Abstractions;
using static Spayly.Sim.Body;
using static Spayly.Sim.CollisionRegimes;
using static Spayly.Sim.Constants;
using static Spayly.Sim.Vec3;

namespace Spayly.Sim.Tests;

// No vitest counterpart: the LS12 model exists only in the C# port. Anchors are Leinhardt & Stewart (2012) for c* = 1.9,
// quoted in docs/research/physics-and-visuals.md §1.4. They are density dependent (V/V_esc ∝ ρ_bulk^(−1/6) at fixed
// ρ₁); the paper's numbers are reproduced with a bulk density of 3000 kg/m³, which is what these tests use.
public class CollisionRegimesTests(ITestOutputHelper log)
{
    private const double Rho = 3000;
    private const double MTot = 1e24;
    private static readonly BodyVisual White = new() { Color = "#fff", Albedo = 0.1 };

    private static (double Mt, double Mp, double Rt, double Rp) Pair(double gamma, double mTot = MTot, double rho = Rho)
    {
        var mt = mTot / (1 + gamma);
        var mp = mTot - mt;
        return (mt, mp, RadiusForDensity(mt, rho), RadiusForDensity(mp, rho));
    }

    private static double VEscOf((double Mt, double Mp, double Rt, double Rp) p) => Math.Sqrt(2 * G * (p.Mt + p.Mp) / (p.Rt + p.Rp));

    /// <summary>V_i/V_esc at which M_lr first drops below <paramref name="mass"/>, by bisection (M_lr falls monotonically with speed).</summary>
    private static double SpeedWhereRemnantDropsTo(double gamma, double b, Func<(double Mt, double Mp), double> mass)
    {
        var p = Pair(gamma);
        var vEsc = VEscOf(p);
        var goal = mass((p.Mt, p.Mp));
        double lo = 0.01, hi = 200;
        for (var i = 0; i < 200; i++)
        {
            var mid = 0.5 * (lo + hi);
            var r = Evaluate(p.Mt, p.Mp, p.Rt, p.Rp, mid * vEsc, b);
            if (r.LargestRemnantMass < goal) hi = mid; else lo = mid;
        }
        return 0.5 * (lo + hi);
    }

    private void Within10Percent(string what, double paper, double predicted)
    {
        var err = (predicted - paper) / paper;
        log.WriteLine($"{what}: predicted {predicted:F3} V_esc, paper {paper} V_esc ({err * 100:+0.0;-0.0}%)");
        Assert.True(Math.Abs(err) < 0.1, $"{what}: predicted {predicted} vs paper {paper} ({err * 100:F1}%)");
    }

    [Fact]
    public void OneToTwoHeadOnErodesAt2VescAndDisruptsAt25Vesc()
    {
        Within10Percent("1:2 erosion onset", 2.0, SpeedWhereRemnantDropsTo(0.5, 0, m => m.Mt));
        Within10Percent("1:2 catastrophic", 2.5, SpeedWhereRemnantDropsTo(0.5, 0, m => 0.5 * (m.Mt + m.Mp)));
    }

    [Fact]
    public void OneToTenHeadOnErodesAt25VescAndDisruptsAt59Vesc()
    {
        Within10Percent("1:10 erosion onset", 2.5, SpeedWhereRemnantDropsTo(0.1, 0, m => m.Mt));
        Within10Percent("1:10 catastrophic", 5.9, SpeedWhereRemnantDropsTo(0.1, 0, m => 0.5 * (m.Mt + m.Mp)));
    }

    [Fact]
    public void OneToFortyNeedsAtLeast18VescToDisrupt()
    {
        Within10Percent("1:40 catastrophic", 18, SpeedWhereRemnantDropsTo(1.0 / 40, 0, m => 0.5 * (m.Mt + m.Mp)));
    }

    [Fact]
    public void EqualMassCriticalSpeedIsWhereQREqualsQStar()
    {
        var p = Pair(1);
        var r = Evaluate(p.Mt, p.Mp, p.Rt, p.Rp, 1, 0);
        var q = SpecificImpactEnergy(p.Mt, p.Mp, r.VStarGamma1);
        Close.To(1, q / r.QStarRDGamma1, 12);
        Close.To(1, r.QStarRD / r.QStarRDGamma1, 12); // the mass-ratio correction is 1 at γ = 1
        // Head-on, the whole projectile interacts and V'* is the same speed.
        Assert.Equal(1, r.Alpha);
        Close.To(1, r.VPrimeStar / r.VStarGamma1, 12);
    }

    [Fact]
    public void BCritIsTargetRadiusOverSumAndSplitsTheTree()
    {
        var p = Pair(0.1);
        var bc = GrazingThreshold(p.Rt, p.Rp);
        Close.To(p.Rt / (p.Rt + p.Rp), bc, 15);
        var vEsc = VEscOf(p);
        var grazing = new[] { CollisionRegime.GrazeAndMerge, CollisionRegime.HitAndRun, CollisionRegime.Erosion };
        foreach (var x in new[] { 1.5, 3, 10 })
        {
            var below = Evaluate(p.Mt, p.Mp, p.Rt, p.Rp, x * vEsc, bc - 1e-6);
            var above = Evaluate(p.Mt, p.Mp, p.Rt, p.Rp, x * vEsc, bc + 1e-6);
            Assert.DoesNotContain(below.Regime, grazing);
            Assert.Contains(above.Regime, grazing);
            Close.To(bc, above.BCrit, 15);
        }
    }

    [Fact]
    public void HitAndRunSpeedForEqualMassesHeadOnIs1Point08Plus1Point86()
    {
        var r = HitAndRunVelocityRatio(1e24, 1e24, 0);
        Assert.True(Math.Abs(r - (1.08 + 1.86)) < 1e-12, $"V_hr/V_esc {r}");
        // Unequal and oblique: every term of Kokubo & Genda's fit, evaluated by hand.
        var zeta = (3.0 - 1.0) / 4.0;
        var theta = Math.Pow(1 - 0.6, 2.5);
        var expected = 2.43 * zeta * zeta * theta - 0.0408 * zeta * zeta + 1.86 * theta + 1.08;
        Assert.True(Math.Abs(HitAndRunVelocityRatio(3e23, 1e23, 0.6) - expected) < 1e-12);
    }

    [Fact]
    public void RegimesAreMonotonicInSpeedAtFixedGeometry()
    {
        foreach (var gamma in new[] { 1.0, 0.5, 0.1, 0.025 })
        {
            var p = Pair(gamma);
            var vEsc = VEscOf(p);
            foreach (var b in new[] { 0, 0.3, 0.6, 0.8, 0.95 })
            {
                var prev = Evaluate(p.Mt, p.Mp, p.Rt, p.Rp, 0.5 * vEsc, b);
                Assert.Equal(CollisionRegime.PerfectMerge, prev.Regime);
                for (var x = 0.5; x <= 60; x *= 1.02)
                {
                    var r = Evaluate(p.Mt, p.Mp, p.Rt, p.Rp, x * vEsc, b);
                    Assert.True(r.Regime >= prev.Regime, $"γ={gamma} b={b} x={x:F3}: {prev.Regime} -> {r.Regime}");
                    Assert.True(r.LargestRemnantMass <= prev.LargestRemnantMass, $"γ={gamma} b={b} x={x:F3}: M_lr rose");
                    prev = r;
                }
            }
        }
    }

    [Fact]
    public void LargestRemnantIsContinuousAtTheSuperCatastrophicOnset()
    {
        var eps = 1e-12;
        var below = LargestRemnantFraction(SuperCatastrophicOnset - eps);
        var above = LargestRemnantFraction(SuperCatastrophicOnset + eps);
        Close.To(0.1, LargestRemnantFraction(SuperCatastrophicOnset), 14);
        Assert.True(Math.Abs(below - above) < 1e-11, $"{below} vs {above}");
        Close.To(0.5, LargestRemnantFraction(1), 14);
        Close.To(1, LargestRemnantFraction(0), 14);
    }

    [Fact]
    public void FragmentSamplerConservesMassExactlyAndIsDeterministic()
    {
        foreach (var fLr in new[] { 0.95, 0.5, 0.1, 0.01 })
        {
            foreach (var cap in new[] { 0, 1, 6, 64, 5000 })
            {
                for (uint seed = 1; seed < 6; seed++)
                {
                    var mTot = 6.6e24;
                    var remnant = fLr * mTot;
                    var esc = mTot - remnant;
                    var a = SampleFragments(esc, Rng.Mulberry32(seed), cap, largestCap: remnant);
                    var b = SampleFragments(esc, Rng.Mulberry32(seed), cap, largestCap: remnant);
                    Assert.Equal(a.Masses, b.Masses);
                    Assert.Equal(a.ParticleTierMass, b.ParticleTierMass);
                    Assert.True(a.Masses.Length <= cap, $"{a.Masses.Length} > {cap}");
                    Assert.True(a.ParticleTierMass >= 0);
                    var sum = remnant + a.ParticleTierMass;
                    foreach (var m in a.Masses)
                    {
                        Assert.True(m > 0 && m <= remnant, $"fragment {m} vs remnant {remnant}");
                        sum += m;
                    }
                    Assert.True(Math.Abs(sum - mTot) <= 1e-12 * mTot, $"fLr={fLr} cap={cap} seed={seed}: {sum} vs {mTot}");
                    for (var k = 1; k < a.Masses.Length; k++) Assert.True(a.Masses[k] <= a.Masses[k - 1]);
                }
            }
        }
        var x = SampleFragments(1e22, Rng.Mulberry32(1), 64);
        var y = SampleFragments(1e22, Rng.Mulberry32(2), 64);
        Assert.NotEqual(x.Masses, y.Masses);
        // β near 3 puts most of the mass in the small end: 64 draws hold only about a fifth of it.
        var discrete = x.Masses.Sum() / 1e22;
        log.WriteLine($"64 fragments carry {discrete:P1} of the escaping mass");
        Assert.True(discrete > 0.05 && discrete < 0.6, $"discrete fraction {discrete}");
    }

    // ---- end-to-end through Collide.ResolveCollision ----

    private static readonly CollideOptions Ls12 = new() { BodyFrame = (_, d) => d, OutcomeModel = OutcomeModel.LS12 };

    private static Body Rock(string id, double mass, double radius, Vec3 pos, Vec3 vel, int seed) => MakeBody(new BodySpec
    {
        Id = id, Name = id, Kind = BodyKind.Planet, Surface = Surface.Rock, Mass = mass, Radius = radius, Pos = pos, Vel = vel, Seed = seed, Visual = White,
    });

    /// <summary>
    /// Target at rest in a frame moving at <paramref name="drift"/>, projectile touching it and closing at
    /// <paramref name="vRel"/> along −x with impact parameter <paramref name="b"/>.
    /// </summary>
    private static (Universe U, Body T, Body P, CollisionEvent Ev) Setup(double mt, double rt, double mp, double rp, double vRel, double b, Vec3 drift)
    {
        var u = new Universe(2451545);
        var origin = new Vec3(1.5e8, -2e7, 3e5);
        var n = new Vec3(Math.Sqrt(1 - b * b), b, 0);
        var t = Rock("t", mt, rt, origin, drift, 7);
        var p = Rock("p", mp, rp, origin + n * ((rt + rp) * 0.999), drift + new Vec3(-vRel, 0, 0), 11);
        u.SetBodies([t, p]);
        return (u, t, p, new CollisionEvent(t, p, 1234.5, t.Pos, p.Pos));
    }

    private static (Vec3 P, double Noise, double M) Ledger(Universe u, ImpactOutcome? o = null)
    {
        var p = Vec3.Zero;
        double s = 0, m = 0;
        foreach (var b in u.Bodies)
        {
            p += b.Vel * b.Mass;
            s += b.Mass * Length(b.Vel);
            m += b.Mass;
        }
        if (o is not null)
        {
            p += o.ParticleTierVelocity * o.ParticleTierMass;
            s += o.ParticleTierMass * Length(o.ParticleTierVelocity);
            m += o.ParticleTierMass;
        }
        return (p, s * 1e-14, m);
    }

    private static void AssertNoOverlaps(Universe u)
    {
        var all = u.Bodies;
        for (var a = 0; a < all.Count; a++)
        {
            for (var b = a + 1; b < all.Count; b++)
            {
                var sep = Length(all[a].Pos - all[b].Pos);
                var touch = all[a].Radius + all[b].Radius;
                Assert.True(sep > touch, $"{all[a].Id}/{all[b].Id} {sep} <= {touch}");
            }
        }
    }

    [Fact]
    public void ATheiaLikeImpactConservesMomentumAndMassUnderLs12()
    {
        const double mEarth = 5.9722e24, rEarth = 6371, mTheia = 6.4e23, rTheia = 3390;
        var vEsc = Math.Sqrt(2 * G * (mEarth + mTheia) / (rEarth + rTheia));
        var (u, t, p, ev) = Setup(mEarth, rEarth, mTheia, rTheia, 2.5 * vEsc, 0.3, new Vec3(0, 29.8, 0.4));
        var (p0, _, m0) = Ledger(u);
        var o = Collide.ResolveCollision(u, ev, Ls12);
        Assert.NotNull(o.Ls12);
        var r = o.Ls12!.Value;
        log.WriteLine($"Theia-like: {r.Regime}, Q_R/Q'* = {r.QR / r.QPrimeStarRD:F3}, M_lr/M_tot = {r.LargestRemnantFraction:F4}, " +
                      $"{o.Fragments.Count} fragments, particle tier {o.ParticleTierMass / (mEarth + mTheia):P2} of M_tot");
        Assert.Contains(r.Regime, new[] { CollisionRegime.PartialAccretion, CollisionRegime.PartialErosion, CollisionRegime.SuperCatastrophic });
        Assert.True(o.Fragments.Count > 0, "no fragments");
        Assert.Null(u.ById("p"));
        Assert.Equal(1 + o.Fragments.Count, u.Bodies.Count);
        Close.To(1, t.Mass / r.LargestRemnantMass, 12);
        var (p1, noise, m1) = Ledger(u, o);
        var drift = Length(p1 - p0);
        Assert.True(drift < noise, $"momentum drift {drift} vs noise {noise}");
        Close.To(1, m1 / m0, 9);
        AssertNoOverlaps(u);
    }

    [Fact]
    public void AGrazingFastImpactIsAHitAndRunThatBothBodiesSurvive()
    {
        var pr = Pair(0.1, 7e23, 3500);
        var vEsc = VEscOf(pr);
        var (u, t, p, ev) = Setup(pr.Mt, pr.Rt, pr.Mp, pr.Rp, 2 * vEsc, 0.9, new Vec3(12, -3, 0.5));
        var (p0, _, m0) = Ledger(u);
        var mp0 = p.Mass;
        var tVel = t.Vel;
        var o = Collide.ResolveCollision(u, ev, Ls12);
        var r = o.Ls12!.Value;
        log.WriteLine($"hit-and-run: V_hr/V_esc = {r.VHrOverVEsc:F3}, projectile keeps {p.Mass / mp0:P1}, reverse Q/Q* = {r.ProjectileQRatio:F3}, " +
                      $"{o.Fragments.Count} fragments, particle tier {o.ParticleTierMass / mp0:P1} of M_p");
        Assert.Equal(CollisionRegime.HitAndRun, r.Regime);
        Assert.Equal(OutcomeKind.HitAndRun, o.Kind);
        Assert.Same(t, u.ById("t"));
        Assert.Same(p, u.ById("p"));
        Assert.Equal(pr.Mt, t.Mass);
        Assert.Equal(tVel, t.Vel);
        Assert.True(p.Mass <= mp0 && p.Mass > 0, $"projectile {p.Mass} vs {mp0}");
        var (p1, noise, m1) = Ledger(u, o);
        var drift = Length(p1 - p0);
        Assert.True(drift < noise, $"momentum drift {drift} vs noise {noise}");
        Close.To(1, m1 / m0, 9);
        AssertNoOverlaps(u);
        // It left along its path: clear of the target and receding from it.
        var rel = p.Pos - t.Pos;
        Assert.True(Dot(rel, p.Vel - t.Vel) > 0, "projectile still approaching the target");
        // And the next substeps do not see the same pair collide again.
        var again = new List<ImpactOutcome>();
        u.CollisionHandlers.Add((e, uni) =>
        {
            var ids = new[] { e.A.Id, e.B.Id };
            if (ids.Contains("t") && ids.Contains("p")) again.Add(Collide.ResolveCollision(uni, e, Ls12));
        });
        for (var i = 0; i < 20; i++) u.Step(1);
        Assert.Empty(again);
    }

    [Fact]
    public void LegacyStaysTheDefaultAndLeavesTheNewFieldsEmpty()
    {
        var opts = new CollideOptions { BodyFrame = (_, d) => d };
        Assert.Equal(OutcomeModel.Legacy, opts.OutcomeModel);
        var pr = Pair(0.1, 7e23, 3500);
        var (u, _, _, ev) = Setup(pr.Mt, pr.Rt, pr.Mp, pr.Rp, 2 * VEscOf(pr), 0.9, Vec3.Zero);
        var o = Collide.ResolveCollision(u, ev, opts);
        Assert.Null(o.Ls12);
        Assert.Equal(0, o.ParticleTierMass);
        Assert.NotEqual(OutcomeKind.HitAndRun, o.Kind);
    }
}
