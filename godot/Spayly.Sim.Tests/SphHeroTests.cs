using Spayly.Sim.Sph;
using Xunit.Abstractions;
using static Spayly.Sim.Body;

namespace Spayly.Sim.Tests;

// No vitest counterpart. The hero collision end to end at N = 2000 (direct gravity, so momentum is exact).
public class SphHeroTests(ITestOutputHelper log)
{
    private static readonly BodyVisual Grey = new() { Color = "#888", Albedo = 0.2 };

    private static Body Planet(string id, double mass, double radius, Surface surface = Surface.Rock, BodyKind kind = BodyKind.Planet) =>
        MakeBody(new BodySpec { Id = id, Name = id, Kind = kind, Surface = surface, Mass = mass, Radius = radius, Visual = Grey, Seed = 1 });

    private static (Vec3 Rt, Vec3 Vt, Vec3 Rp, Vec3 Vp) BodyCentres(SphState s)
    {
        var (mt, mp) = (0.0, 0.0);
        Vec3 rt = Vec3.Zero, vt = Vec3.Zero, rp = Vec3.Zero, vp = Vec3.Zero;
        for (var i = 0; i < s.Count; i++)
        {
            if (s.Body[i] == 0) { mt += s.M[i]; rt += s.Position(i) * s.M[i]; vt += s.Velocity(i) * s.M[i]; }
            else { mp += s.M[i]; rp += s.Position(i) * s.M[i]; vp += s.Velocity(i) * s.M[i]; }
        }
        return (rt * (1 / mt), vt * (1 / mt), rp * (1 / mp), vp * (1 / mp));
    }

    [Fact]
    public void TwoBodiesBelowEscapeSpeedMergeAndConserveMomentum()
    {
        var target = Planet("mars", 6.417e23, 3390);
        target.Pos = new Vec3(2.2e8, 1.0e7, 0);
        target.Vel = new Vec3(-1.0, 24.0, 0.3);
        var impactor = Planet("rock", 1.6e23, 2200);
        (impactor.Pos, impactor.Vel) = HeroCollision.ContactState(target, impactor.Mass, impactor.Radius, 20, 0.6, new Vec3(1, 0.2, 0), new Vec3(0, 1, 0.1));

        var hero = new HeroCollision(target, impactor, new HeroOptions { Particles = 2000 });
        hero.Build();
        var s = hero.State;
        Assert.Equal(2000, s.Count);
        Assert.Equal(GravityMode.Direct, hero.ResolvedOptions().Gravity);

        var p0 = s.Totals();
        var (pScale, lScale) = s.Scales();
        var c0 = BodyCentres(s);
        var sep0 = Vec3.Distance(c0.Rt, c0.Rp);
        // The pair starts on the N-body's conic: its internal angular momentum is the bodies' (kg km²/s → kg m²/s).
        Assert.True(Vec3.Length(p0.AngularMomentum - hero.AngularMomentum * 1e6) < 1e-9 * lScale,
            $"initial L {p0.AngularMomentum} vs pair {hero.AngularMomentum * 1e6}");
        Assert.True(Vec3.Length(p0.Momentum) < 1e-12 * pScale, $"initial momentum {p0.Momentum} in the CM frame");

        hero.Step(1800);
        var p1 = s.Totals();
        var c1 = BodyCentres(s);
        var sep1 = Vec3.Distance(c1.Rt, c1.Rp);
        var dp = Vec3.Length(p1.Momentum - p0.Momentum) / pScale;
        log.WriteLine($"t = {hero.Time:F0} s in {hero.Solver.Steps} steps: separation {sep0 / 1e3:F0} → {sep1 / 1e3:F0} km, |ΔP|/Σm|v| = {dp:E3}, R_t,sph = {hero.TargetProfile!.Radius / 1e3:F0} km");
        Assert.True(dp < 1e-12, $"momentum drifted by {dp:E3} of Σm|v|");
        Assert.True(sep1 < 0.8 * sep0, $"the impactor did not sink in: separation {sep0} → {sep1} m");

        var d = hero.Demote(hero.CmPosition, hero.CmVelocity);
        var m = target.Mass + impactor.Mass;
        var largest = d.Bodies[0];
        log.WriteLine($"{d.Bodies.Count} bodies, largest {largest.Mass / m:P2} of the mass ({largest.MassBySource[1] / impactor.Mass:P1} of the impactor), remainder {d.Remainder.Mass / m:P3}, drift P {d.Drift.Momentum:E2} L {d.Drift.AngularMomentum:E2}");
        Assert.True(largest.Mass > 0.8 * m, $"largest body holds only {largest.Mass / m:P1}");
        Assert.True(largest.MassBySource.Length > 1 && largest.MassBySource[1] > 0.7 * impactor.Mass, "the impactor did not end up in the largest body");

        // World-frame ledger: bodies + remainder = truth, exactly.
        var closure = d.Closure;
        var pWorld = m * Vec3.Length(hero.CmVelocity) + pScale * 1e-3;
        var lWorld = Vec3.Length(Vec3.Cross(hero.CmPosition, hero.CmVelocity)) * m + lScale * 1e-6;
        Assert.True(Math.Abs(closure.Mass) <= 1e-12 * m, $"mass closure {closure.Mass}");
        Assert.True(Vec3.Length(closure.Momentum) <= 1e-12 * pWorld, $"momentum closure {closure.Momentum}");
        Assert.True(Vec3.Length(closure.AngularMomentum) <= 1e-12 * lWorld, $"angular momentum closure {closure.AngularMomentum}");

        var bodies = HeroCollision.CreateBodies(d, target, impactor, 1234.5, s.Materials);
        Assert.Equal(target.Id, bodies[0].Id);
        Assert.DoesNotContain(bodies, b => b.Id == impactor.Id);
        var sumM = d.Remainder.Mass;
        var sumP = d.Remainder.Momentum;
        foreach (var b in bodies)
        {
            sumM += b.Mass;
            sumP += b.Vel * b.Mass;
            Assert.Equal(b.Mass, b.Mass0);
            Assert.True(b.Radius > 0 && double.IsFinite(b.Radius));
        }
        Assert.True(Math.Abs(sumM - m) <= 1e-12 * m, $"bodies + remainder mass {sumM} vs {m}");
        Assert.True(Vec3.Length(sumP - hero.CmVelocity * m) <= 1e-12 * pWorld, $"bodies + remainder momentum {sumP} vs {hero.CmVelocity * m}");
    }

    // M6.6: the bodies bring their own spin. A lone planet turning once a sidereal day, grazed by a negligible impactor, must
    // come out of the demotion with the same day, and the spin's angular momentum must be in the truth the ledger closes on.
    // Relaxed first: an unrelaxed SPH planet puffs up, and its moment of inertia grew 1.4 % in 1800 s (L held to 1e-4).
    [Fact]
    public void ALoneSpinningPlanetKeepsItsDayThroughTheDemotion()
    {
        var target = Planet("earth", 5.972e24, 6371);
        // Pole tilted off the ecliptic like Earth's (ICRF pole at Dec 90°), 360.9856°/day: a 23.93 h day.
        target.Rotation = new Rotation(0, 90, 190.147, 360.9856235);
        target.Pos = new Vec3(1.496e8, 0, 0);
        target.Vel = new Vec3(0, 29.78, 0);
        var impactor = Planet("pebble", 5.972e20, 400, kind: BodyKind.Asteroid);
        (impactor.Pos, impactor.Vel) = HeroCollision.ContactState(target, impactor.Mass, impactor.Radius, 0, 0.5, new Vec3(1, 0, 0), new Vec3(0, 1, 0));

        var hero = new HeroCollision(target, impactor, new HeroOptions { Particles = 1000, RelaxSteps = 100 });
        hero.Build();
        var omega = HeroCollision.SpinOf(target);
        var day = 2 * Math.PI / Vec3.Length(omega);
        Assert.Equal(86164.1, day, 0.5);
        // The particles' L is the pair's orbital L plus the spin Build put in, which lies along ω.
        var l0 = hero.State.Totals().AngularMomentum;
        Assert.True(Vec3.Length(l0 - (hero.AngularMomentum + hero.SpinAngularMomentum) * 1e6) < 1e-9 * Vec3.Length(l0), $"initial L {l0}");
        // Along ω only as far as 900 particles make an isotropic inertia tensor.
        var tilt = Math.Acos(Math.Min(1, Vec3.Dot(Vec3.Normalize(hero.SpinAngularMomentum), Vec3.Normalize(omega)))) / Constants.Deg;
        Assert.True(tilt < 1, $"spin L {tilt}° off ω");

        hero.Step(900);
        var d = hero.Demote(hero.CmPosition, hero.CmVelocity);
        var bodies = HeroCollision.CreateBodies(d, target, impactor, 0, hero.State.Materials);
        var heir = bodies.Find(b => b.Id == target.Id)!;
        var rate = heir.Rotation!.Value.Rate;
        var period = 360 / rate * 86400;
        var pole = Frames.PoleToEcliptic(heir.Rotation.Value.PoleRA, heir.Rotation.Value.PoleDec);
        log.WriteLine($"t = {hero.Time:F0} s: day {day / 3600:F3} h -> {period / 3600:F3} h ({100 * (period / day - 1):+0.00;-0.00} %), pole off by {Math.Acos(Math.Min(1, Vec3.Dot(pole, Vec3.Normalize(omega)))) / Constants.Deg:F3}°, closure L {Vec3.Length(d.Closure.AngularMomentum) / Vec3.Length(d.Truth.AngularMomentum):E2}");
        Assert.True(Math.Abs(period / day - 1) < 0.01, $"day {day / 3600} h came out {period / 3600} h");
        Assert.True(Vec3.Dot(pole, Vec3.Normalize(omega)) > Math.Cos(1 * Constants.Deg), "the pole moved more than 1°");
        Assert.True(Vec3.Length(d.Closure.AngularMomentum) <= 1e-12 * Vec3.Length(d.Truth.AngularMomentum), $"L closure {d.Closure.AngularMomentum}");
    }

    [Fact]
    public void MovingAlongTheConicKeepsEnergyAndAngularMomentum()
    {
        const double mu = 3.9e14;
        var r0 = new Vec3(6.0e6, 1.0e6, -2.0e5);
        var v0 = new Vec3(-7000, 2500, 300);
        foreach (var d in new[] { 5.0e6, 6.2e6, 8.0e6 })
        {
            var (r, v) = HeroCollision.AlongConic(r0, v0, mu, d);
            Close.To(d, Vec3.Length(r), 3);
            var e0 = 0.5 * Vec3.Dot(v0, v0) - mu / Vec3.Length(r0);
            var e1 = 0.5 * Vec3.Dot(v, v) - mu / d;
            Assert.True(Math.Abs(e1 - e0) < 1e-10 * Math.Abs(e0), $"d={d}: energy {e0} → {e1}");
            var l0 = Vec3.Cross(r0, v0);
            var l1 = Vec3.Cross(r, v);
            Assert.True(Vec3.Length(l1 - l0) < 1e-10 * Vec3.Length(l0), $"d={d}: L {l0} → {l1}");
            Assert.True(Vec3.Dot(r, v) < 0, "no longer approaching");
        }
    }

    [Fact]
    public void TheCanonicalMoonFormingImpactIsAHeroCollision()
    {
        var earth = Planet("earth", 5.972e24, 6371);
        var theia = Planet("theia", 6.417e23, 3390);
        (theia.Pos, theia.Vel) = HeroCollision.ContactState(earth, theia.Mass, theia.Radius, 45, 1.0, new Vec3(0, 1, 0), new Vec3(1, 0, 0));
        Assert.True(HeroCollision.Qualifies(earth, theia, new HeroTrigger(), out var r));
        log.WriteLine($"Earth–Theia: Q_R = {r.QR:E3} J/kg, Q*_RD = {r.QStarRD:E3} J/kg, ratio {r.QR / r.QStarRD:E3}, b = {r.B:F3}, regime {r.Regime}");
        Close.To(Math.Sin(45 * Constants.Deg), r.B, 6);

        var rock = Planet("rock", 1e20, 200, kind: BodyKind.Asteroid);
        (rock.Pos, rock.Vel) = HeroCollision.ContactState(earth, rock.Mass, rock.Radius, 45, 1.5, new Vec3(0, 1, 0), new Vec3(1, 0, 0));
        Assert.False(HeroCollision.Qualifies(earth, rock, new HeroTrigger(), out _));

        var jupiter = Planet("jupiter", 1.898e27, 69911, Surface.Gas);
        (theia.Pos, theia.Vel) = HeroCollision.ContactState(jupiter, theia.Mass, theia.Radius, 45, 1.0, new Vec3(0, 1, 0), new Vec3(1, 0, 0));
        Assert.False(HeroCollision.Qualifies(jupiter, theia, new HeroTrigger(), out _));
        Assert.Null(HeroCollision.LayersFor(jupiter));
    }

    [Fact]
    public void TheWideTriggerTakesMoonIntoVestaAndStillRefusesSmallRocksAndGas()
    {
        var moon = Planet("moon", 7.342e22, 1737, kind: BodyKind.Moon);
        var vesta = Planet("vesta", 2.59e20, 262.7, kind: BodyKind.Asteroid);
        (vesta.Pos, vesta.Vel) = HeroCollision.ContactState(moon, vesta.Mass, vesta.Radius, 30, 3.0, new Vec3(0, 1, 0), new Vec3(1, 0, 0));
        var wide = new HeroTrigger { Wide = true };
        Assert.False(HeroCollision.Qualifies(moon, vesta, new HeroTrigger(), out _));
        Assert.True(HeroCollision.Qualifies(moon, vesta, wide, out var r));
        log.WriteLine($"Moon-Vesta: gamma {r.Gamma:F4}, Q_R/Q*_RD {r.QR / r.QStarRD:E2}, regime {r.Regime}");
        var boulder = Planet("boulder", 1e16, 5, kind: BodyKind.Asteroid);
        (boulder.Pos, boulder.Vel) = HeroCollision.ContactState(moon, boulder.Mass, boulder.Radius, 30, 3.0, new Vec3(0, 1, 0), new Vec3(1, 0, 0));
        Assert.False(HeroCollision.Qualifies(moon, boulder, wide, out _));
    }
}
