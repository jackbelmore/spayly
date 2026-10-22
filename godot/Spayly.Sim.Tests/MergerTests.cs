using Spayly.Sim.Debris;
using static Spayly.Sim.Constants;
using static Spayly.Sim.Vec3;

namespace Spayly.Sim.Tests;

// No vitest file: black-hole mergers exist only in the Godot port. Reference values from outside the code: the equal-mass
// remnant M0 = 0.951507 and the test-particle limit 1 − η(1 − √(8/9)) (Healy, Lousto & Zlochower 2014), and the González
// et al. (2007) recoil's zero at q = 1 and ≈ 175 km/s maximum near η ≈ 0.195.
public class MergerTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private static readonly CollideOptions Identity = new() { BodyFrame = (_, d) => d };

    private static Body Hole(string id, double solar, Vec3 pos, Vec3 vel) =>
        HandBuilt.Make(id, BodyKind.BlackHole, Surface.Star, solar * SunMass, BlackHole.SchwarzschildRadiusKm(solar * SunMass), pos, vel, 3);

    [Fact]
    public void EqualMassRemnantKeeps0Point952OfTheMass()
    {
        var f = GravitationalWaves.RemnantMassFraction(30 * SunMass, 30 * SunMass);
        output.WriteLine($"equal mass: M_rem/M = {f:F6} (radiated {100 * (1 - f):F2} %); q = 1/2: {GravitationalWaves.RemnantMassFraction(2, 1):F5}; q = 1/4: {GravitationalWaves.RemnantMassFraction(4, 1):F5}");
        Assert.Equal(0.951507, f, 12);
        // Test-particle limit: a tiny hole radiates its ISCO binding energy, 1 − √(8/9) of its mass.
        const double q = 1e-5;
        var small = GravitationalWaves.RemnantMassFraction(1, q);
        var eta = GravitationalWaves.SymmetricMassRatio(1, q);
        Assert.True(Math.Abs((1 - small) / (eta * (1 - Math.Sqrt(8.0 / 9))) - 1) < 1e-3, $"radiated {(1 - small) / eta} of the small hole's mass");
        // Monotonic in between: less is radiated the more unequal the pair.
        var last = f;
        foreach (var r in new[] { 0.8, 0.5, 0.25, 0.1, 0.01 })
        {
            var x = GravitationalWaves.RemnantMassFraction(1, r);
            Assert.True(x > last, $"q = {r}: {x} after {last}");
            last = x;
        }
    }

    [Fact]
    public void KickIsZeroAtEqualMassAndPeaksNear175KmsAtEta0Point195()
    {
        Assert.Equal(0, GravitationalWaves.RecoilSpeed(0.25));
        double best = 0, at = 0;
        for (var eta = 0.0; eta <= 0.25; eta += 1e-5)
        {
            var v = GravitationalWaves.RecoilSpeed(eta);
            if (v > best) { best = v; at = eta; }
        }
        output.WriteLine($"kick peak {best:F2} km/s at eta {at:F4} (q {(1 - 2 * at - Math.Sqrt(1 - 4 * at)) / (2 * at):F3})");
        Assert.True(Math.Abs(best - 175) < 3, $"peak {best} km/s");
        Assert.True(Math.Abs(at - 0.195) < 0.005, $"at eta {at}");
    }

    /// <summary>A 30 + 11 M☉ pair (η ≈ 0.196) touching at their 2 r_s capture radii, the lighter one on a tangential orbit.</summary>
    private static (Universe U, Body A, Body B) Pair()
    {
        var rsA = BlackHole.SchwarzschildRadiusKm(30 * SunMass);
        var rsB = BlackHole.SchwarzschildRadiusKm(11 * SunMass);
        var d = 1.9 * (rsA + rsB);
        var a = Hole("a", 30, new Vec3(1e8, 2e7, -3e5), new Vec3(12, -30, 0.4));
        var b = Hole("b", 11, a.Pos + new Vec3(d, 0, 0), a.Vel + new Vec3(-3000, 90000, 500));
        var u = new Universe(J2000Jd);
        u.SetBodies([a, b]);
        return (u, a, b);
    }

    [Fact]
    public void MergerBooksTheMomentumTheKickTookAndTheLedgerCloses()
    {
        var (u, a, b) = Pair();
        var origin = new Vec3(3e7, -1e6, 2e4);
        var ledger = new DebrisLedger(origin);
        ledger.Baseline(u.Bodies, origin);
        var before = DebrisLedger.Measure(u.Bodies, origin);
        var o = Collide.ResolveCollision(u, new CollisionEvent(a, b, 0), Identity);
        Assert.Equal(OutcomeKind.Merge, o.Kind);
        Assert.Single(u.Bodies);
        var rem = u.Bodies[0];
        Assert.Same(a, rem);
        var m = o.Merger!.Value;
        var eta = GravitationalWaves.SymmetricMassRatio(30, 11);
        // The pair's mass as Collide sums it: 41 × SunMass rounds one ulp away from 30 × SunMass + 11 × SunMass.
        Close.To(GravitationalWaves.RemnantMassFraction(30 * SunMass, 11 * SunMass) * (30 * SunMass + 11 * SunMass), rem.Mass, 14);
        Close.To(BlackHole.SchwarzschildRadiusKm(rem.Mass), rem.Radius, 14);
        Close.To(GravitationalWaves.RecoilSpeed(eta), Length(m.Kick), 12);
        // The kick lies in the orbital plane.
        var normal = Normalize(Cross(m.R2 - m.R1, m.V2 - m.V1));
        Assert.True(Math.Abs(Dot(Normalize(m.Kick), normal)) < 1e-12, "kick out of the orbital plane");

        // Momentum with the kick: the waves carried p_before − p_remnant, and booking that closes it to rounding.
        var gw = m.Radiated(origin);
        var after = DebrisLedger.Measure(u.Bodies, origin);
        var missing = before - after - gw;
        output.WriteLine($"30+11 Msun: radiated {m.RadiatedMass / SunMass:F4} Msun, kick {Length(m.Kick):F2} km/s, GW momentum {gw.Momentum.Length:G4} kg km/s, residual mass {missing.Mass.Value:G3} p {missing.Momentum.Length:G3} L {missing.Angular.Length:G3}");
        Assert.True(Math.Abs(missing.Mass.Value) <= 1e-30 * rem.Mass, $"mass residual {missing.Mass.Value}");
        Assert.True(missing.Momentum.Length <= 1e-28 * gw.Momentum.Length + 1e-300, $"momentum residual {missing.Momentum.Length}");
        Assert.True(gw.Momentum.Length > 0.9 * rem.Mass * Length(m.Kick), "the kick's momentum is in the book");

        // The ledger: the remnant's change is external (waves leave the system), and T0 then matches the bodies exactly.
        ledger.AddExternal(-gw);
        var err = ledger.Error();
        var t0Gap = DebrisLedger.Measure(u.Bodies, origin) - ledger.Bodies;
        output.WriteLine($"ledger error mass {err.Mass:G3} p {err.Momentum:G3} L {err.Angular:G3}; T0 gap mass {t0Gap.Mass.Value:G3} p {t0Gap.Momentum.Length:G3} L {t0Gap.Angular.Length:G3}");
        Assert.Equal(0, err.Mass);
        Assert.True(err.Momentum < 1e-15 && err.Angular < 1e-15);
        Assert.True(Math.Abs(t0Gap.Mass.Value) <= 1e-30 * rem.Mass);
        Assert.True(t0Gap.Momentum.Length <= 1e-28 * ledger.Scales.Momentum);
        Assert.True(t0Gap.Angular.Length <= 1e-28 * ledger.Scales.Angular);
    }

    [Fact]
    public void StellarHolesFallingTogetherTouchBelowCAndMergeThroughTheUniverse()
    {
        // 30 + 11 M☉ from rest 5000 km apart: under PW they meet at the pair's 2 r_s at about c. The substep has to drop to
        // tens of microseconds for that; at the 0.25 s floor the pair overshot contact at 3.5 c.
        var a = Hole("a", 30, Vec3.Zero, Vec3.Zero);
        var b = Hole("b", 11, new Vec3(5000, 0, 0), Vec3.Zero);
        var u = new Universe(J2000Jd);
        u.SetBodies([a, b]);
        double relAtContact = double.NaN;
        u.CollisionHandlers.Add((ev, uu) =>
        {
            relAtContact = Length(ev.B.Vel - ev.A.Vel);
            Collide.ResolveCollision(uu, ev, Identity);
        });
        var steps = 0;
        for (var k = 0; k < 100 && u.Bodies.Count > 1; k++)
        {
            u.Step(0.05);
            steps += u.SubstepsLastFrame;
        }
        output.WriteLine($"30+11 Msun from 5000 km: contact at {relAtContact / BlackHole.SpeedOfLight:F4} c after {steps} substeps");
        Assert.Single(u.Bodies);
        // PW alone touched at 0.9883 c. The 2.5PN reaction force (M6.4) does work (8/5)η(m²/r³)(2ṙ² + 8m/3r)ṙ² > 0 on purely
        // radial motion, in the harmonic and Burke–Thorne gauges alike (its orbit average is Peters' loss), and PN has no
        // meaning this deep anyway: it touches at 1.0004 c.
        Assert.True(relAtContact < 1.001 * BlackHole.SpeedOfLight, $"{relAtContact / BlackHole.SpeedOfLight} c at contact");
        Assert.True(relAtContact > 0.9 * BlackHole.SpeedOfLight, $"{relAtContact / BlackHole.SpeedOfLight} c at contact");
    }

    [Fact]
    public void EqualMassMergerHasNoKickAndHeadOnRecoilsAlongTheAxis()
    {
        var a = Hole("a", 20, new Vec3(0, 0, 0), new Vec3(0, 0, 0));
        var b = Hole("b", 20, new Vec3(200, 0, 0), new Vec3(0, 5000, 0));
        var u = new Universe(J2000Jd);
        u.SetBodies([a, b]);
        var o = Collide.ResolveCollision(u, new CollisionEvent(a, b, 0), Identity);
        Assert.Equal(0, Length(o.Merger!.Value.Kick));
        Close.To(0.951507 * 40 * SunMass, u.Bodies[0].Mass, 12);

        var c = Hole("c", 30, Vec3.Zero, Vec3.Zero);
        var d = Hole("d", 10, new Vec3(300, 0, 0), new Vec3(-2000, 0, 0));
        u.SetBodies([c, d]);
        var h = Collide.ResolveCollision(u, new CollisionEvent(c, d, 0), Identity).Merger!.Value;
        Assert.True(Math.Abs(Math.Abs(Normalize(h.Kick).X) - 1) < 1e-12, $"head-on kick {h.Kick}");
    }

    // M6.5: HLZ14 Eq. (16) remnant spin, and Eq. (14) with the Kerr ISCO energy of that spin.
    [Fact]
    public void RemnantSpinIsL0AtEqualMassAndTheKerrIscoMovesQQuarterByUnderOnePercent()
    {
        Assert.Equal(6, GravitationalWaves.KerrIscoRadius(0), 12);
        Assert.Equal(1, GravitationalWaves.KerrIscoRadius(1), 12);
        Assert.Equal(4.233, GravitationalWaves.KerrIscoRadius(0.5), 3);
        Assert.Equal(2 * Math.Sqrt(3), GravitationalWaves.KerrIscoAngularMomentum(0), 12);
        Assert.Equal(Math.Sqrt(8.0 / 9), GravitationalWaves.KerrIscoEnergy(0), 12);
        Assert.Equal(0.686710, GravitationalWaves.RemnantSpin(30 * SunMass, 30 * SunMass), 12);
        Close.To(0.951507, GravitationalWaves.RemnantMassFraction(30 * SunMass, 30 * SunMass), 12);
        // Test-particle limit: the small hole brings its ISCO angular momentum, 2√3 η, and radiates its ISCO binding energy.
        const double q = 1e-5;
        var eta = GravitationalWaves.SymmetricMassRatio(1, q);
        var spin = GravitationalWaves.RemnantSpin(1, q);
        Assert.True(Math.Abs(spin / (2 * Math.Sqrt(3) * eta) - 1) < 1e-3, $"spin {spin} vs 2√3 η {2 * Math.Sqrt(3) * eta}");
        var small = GravitationalWaves.RemnantMassFraction(1, q);
        Assert.True(Math.Abs((1 - small) / (eta * (1 - Math.Sqrt(8.0 / 9))) - 1) < 1e-3, $"radiated {(1 - small) / eta} of the small hole's mass");
        var m5 = 1 - GravitationalWaves.RemnantMassFraction(4, 1, GravitationalWaves.IscoEnergy);
        var m6 = 1 - GravitationalWaves.RemnantMassFraction(4, 1);
        output.WriteLine($"q = 1/4: spin {GravitationalWaves.RemnantSpin(4, 1):F6}, E_rad/M {m6:F6} (Schwarzschild ISCO {m5:F6}, {100 * (m6 / m5 - 1):+0.000;-0.000} %); q = 1/2: spin {GravitationalWaves.RemnantSpin(2, 1):F6}, M_rem/M {GravitationalWaves.RemnantMassFraction(2, 1):F6}");
        Assert.True(Math.Abs(m6 / m5 - 1) < 0.01, $"q = 1/4 E_rad moved {100 * (m6 / m5 - 1)} %");
    }

    [Fact]
    public void MergedHoleCarriesItsSpinThroughSnapshotsAndClones()
    {
        var a = Hole("a", 30, Vec3.Zero, Vec3.Zero);
        var b = Hole("b", 10, new Vec3(300, 0, 0), new Vec3(0, 20000, 0));
        var u = new Universe(J2000Jd);
        u.SetBodies([a, b]);
        Collide.ResolveCollision(u, new CollisionEvent(a, b, 0), Identity);
        var spin = u.Bodies[0].Spin;
        Assert.Equal(GravitationalWaves.RemnantSpin(30 * SunMass, 10 * SunMass), spin, 15);
        Assert.Equal(spin, u.Bodies[0].Clone().Spin);
        var snap = u.Snapshot("s");
        u.Bodies[0].Spin = 0;
        u.Restore(snap);
        Assert.Equal(spin, u.Bodies[0].Spin);
    }
}
