using static Spayly.Sim.Body;
using static Spayly.Sim.Collide;
using static Spayly.Sim.Constants;
using static Spayly.Sim.Vec3;

namespace Spayly.Sim.Tests;

// tests/collide.test.ts, in order:
//   describe('collisions')
//     it('a small asteroid craters Earth and merges, conserving momentum')                          ported
//     it('a Mars-mass body at 60 km/s shatters Earth into fragments with momentum conserved')       ported
//     it('keeps one undo point for a cascade of collisions on consecutive steps')                   ported
//     it('never spawns fragments inside each other or the remnant')                                 ported
//     it('starts a new undo point only after a quiet spell')                                        ported
//     it('auto-aim hits Earth from 0.05 AU away')                                                   deferred (ported with Aim)
//   describe('rock on rock')
//     it('catches a fast little asteroid hitting another one instead of letting it pass through')   ported
//     it('merges rocks that nudge each other and shatters ones that slam together')                 ported
//     it('needs far more than self-gravity to break a boulder, and no more than before to break a planet')  ported
//   describe('what a body orbits')
//     it('follows gravity rather than the catalogue')                                               ported
public class CollideTests
{
    private static readonly BodyVisual White = new() { Color = "#fff", Albedo = 0.1 };
    // These are the ported Legacy tests; LS12, the default, is covered by CollisionRegimesTests and StellarCollisionTests.
    private static readonly CollideOptions Identity = new() { BodyFrame = (_, d) => d, OutcomeModel = OutcomeModel.Legacy };

    private static (Universe U, List<ImpactOutcome> Outcomes) Build()
    {
        var (bodies, epochJd) = Catalog.CreateSolarSystem(new DateTime(2000, 1, 1, 12, 0, 0, DateTimeKind.Utc));
        var u = new Universe(epochJd);
        u.SetBodies(bodies);
        var outcomes = new List<ImpactOutcome>();
        u.CollisionHandlers.Add((ev, uni) => outcomes.Add(ResolveCollision(uni, ev, Identity)));
        return (u, outcomes);
    }

    private static Vec3 TotalMomentum(Universe u)
    {
        var p = Vec3.Zero;
        foreach (var b in u.Bodies) p += b.Vel * b.Mass;
        return p;
    }

    /// <summary>Floating-point noise floor of the momentum sum (the barycentric total is ~0, so use the term scale).</summary>
    private static double MomentumNoise(Universe u)
    {
        double s = 0;
        foreach (var b in u.Bodies) s += b.Mass * Length(b.Vel);
        return s * 1e-14;
    }

    private static Body Rock(string id, string name, double mass, double radius, Vec3? pos = null, Vec3? vel = null, int? seed = null, string? primaryId = null) =>
        MakeBody(new BodySpec
        {
            Id = id, Name = name, Kind = BodyKind.Asteroid, Surface = Surface.Rock, Mass = mass, Radius = radius, Pos = pos, Vel = vel,
            Seed = seed, PrimaryId = primaryId, Visual = White,
        });

    [Fact]
    public void ASmallAsteroidCratersEarthAndMergesConservingMomentum()
    {
        var (u, outcomes) = Build();
        var earth = u.ById("earth")!;
        var dir = Normalize(new Vec3(1, 0.2, 0.1));
        var ast = Rock("ast", "Test rock", 1.5e12, 0.5, earth.Pos + dir * (earth.Radius + 400), earth.Vel + dir * -20);
        u.Add(ast);
        var p0 = TotalMomentum(u);
        var m0 = earth.Mass;
        for (var i = 0; i < 200 && outcomes.Count == 0; i++) u.Step(2);
        Assert.Single(outcomes);
        var o = outcomes[0];
        Assert.Equal(OutcomeKind.Crater, o.Kind);
        Assert.Equal("earth", o.Target.Id);
        var d = o.Report?.Crater?.FinalDiameter;
        Assert.True(d > 5000, $"finalDiameter {d} m");
        Close.To(m0 + 1.5e12, earth.Mass, -3);
        Assert.Null(u.ById("ast"));
        Assert.Single(earth.Damage.Craters);
        var p1 = TotalMomentum(u);
        var drift = Length(p1 - p0);
        var noise = MomentumNoise(u);
        Assert.True(drift < noise, $"momentum drift {drift} vs noise {noise}");
        Assert.Single(u.History);
    }

    [Fact]
    public void ATenMetreRockOnEarthUnderLs12AirburstsOrCratersAndNeverMeltsTheEarth()
    {
        // Regression: the LS12 largest-remnant mass of a rock this small rounded below Earth's own mass, so the regime read as
        // erosion and Earth became a lava ball (mass ratio 3e-19, far under one ulp).
        var (bodies, epochJd) = Catalog.CreateSolarSystem(new DateTime(2000, 1, 1, 12, 0, 0, DateTimeKind.Utc));
        var u = new Universe(epochJd);
        u.SetBodies(bodies);
        var outcomes = new List<ImpactOutcome>();
        var opts = new CollideOptions { BodyFrame = (_, d) => d, OutcomeModel = OutcomeModel.LS12 };
        u.CollisionHandlers.Add((ev, uni) => outcomes.Add(ResolveCollision(uni, ev, opts)));
        var earth = u.ById("earth")!;
        var dir = Normalize(new Vec3(1, 0.2, 0.1));
        u.Add(Rock("ast", "Ten metre rock", 1.57e6, 0.005, earth.Pos + dir * (earth.Radius + 400), earth.Vel + dir * -20));
        for (var i = 0; i < 400 && outcomes.Count == 0; i++) u.Step(2);
        Assert.Single(outcomes);
        Assert.True(outcomes[0].Kind is OutcomeKind.Airburst or OutcomeKind.Crater, $"kind {outcomes[0].Kind}");
        Assert.NotEqual(Surface.Lava, earth.Surface);
        Assert.Empty(outcomes[0].Fragments);
    }

    [Fact]
    public void AMarsMassBodyAt60KmSShattersEarthIntoFragmentsWithMomentumConserved()
    {
        var (u, outcomes) = Build();
        var earth = u.ById("earth")!;
        var dir = Normalize(new Vec3(0, 1, 0));
        var big = Rock("theia", "Theia", 6.4e23, 3390, earth.Pos + dir * (earth.Radius + 3390 + 300), earth.Vel + dir * -60);
        u.Add(big);
        var p0 = TotalMomentum(u);
        var n0 = u.Bodies.Count;
        for (var i = 0; i < 400 && outcomes.Count == 0; i++) u.Step(1);
        Assert.Single(outcomes);
        var o = outcomes[0];
        Assert.Contains(o.Kind, new[] { OutcomeKind.Shatter, OutcomeKind.Disperse });
        Assert.True(o.Fragments.Count >= 6, $"{o.Fragments.Count} fragments");
        Assert.Equal(n0 - 1 + o.Fragments.Count, u.Bodies.Count);
        var p1 = TotalMomentum(u);
        var drift = Length(p1 - p0);
        var bound = MomentumNoise(u) + 1e-9 * 6.4e23 * 60;
        Assert.True(drift < bound, $"momentum drift {drift} vs bound {bound}");
        var mass = earth.Mass;
        foreach (var f in o.Fragments) mass += f.Mass;
        Close.To(1, mass / (5.9722e24 + 6.4e23), 9);
        Assert.Equal(Surface.Lava, earth.Surface);
    }

    [Fact]
    public void KeepsOneUndoPointForACascadeOfCollisionsOnConsecutiveSteps()
    {
        // Five pairs of rocks far apart, each timed to touch one step after the last: a cascade the way a
        // debris cloud makes one, where every hit used to clone the whole universe into the undo stack.
        var u = new Universe(2451545);
        var rocks = new List<Body>();
        for (var k = 1; k <= 5; k++)
        {
            double y = k * 1e5;
            rocks.Add(Rock($"a{k}", $"Rock A{k}", 1.5e12, 0.5, new Vec3(0, y, 0), new Vec3(0, 0, 0)));
            rocks.Add(Rock($"b{k}", $"Rock B{k}", 1.5e12, 0.5, new Vec3(1 + k - 0.5, y, 0), new Vec3(-1, 0, 0)));
        }
        u.SetBodies(rocks);
        var outcomes = new List<ImpactOutcome>();
        u.CollisionHandlers.Add((ev, uni) => outcomes.Add(ResolveCollision(uni, ev, Identity)));
        for (var i = 0; i < 8; i++) u.Step(1);
        Assert.Equal(5, outcomes.Count);
        Assert.Single(u.History);
        u.PopHistory();
        Assert.Equal(10, u.Bodies.Count);
    }

    [Fact]
    public void NeverSpawnsFragmentsInsideEachOtherOrTheRemnant()
    {
        // Overlapping fragments would all collide on the very next substep and start a cascade by
        // construction. Sweep sizes, speeds and seeds, including the most violent dispersals.
        var shatters = 0;
        foreach (var R in new[] { 0.5, 50, 3000 })
        {
            foreach (var speed in new double[] { 5, 20, 80, 160 })
            {
                for (var seed = 0; seed < 6; seed++)
                {
                    var u = new Universe(2451545);
                    var M = 3000 * (4.0 / 3) * Math.PI * Math.Pow(R * 1000, 3);
                    var t = Rock("t", "T", M, R, new Vec3(0, 0, 0), new Vec3(0, 0, 0), seed);
                    var i = Rock("i", "I", M * 0.3, R * 0.67, new Vec3(R * 1.6, 0, 0), new Vec3(-speed, 0, 0), seed + 99);
                    u.SetBodies([t, i]);
                    var o = ResolveCollision(u, new CollisionEvent(t, i, seed * 1000), Identity);
                    if (o.Fragments.Count == 0) continue;
                    shatters++;
                    var all = u.Bodies;
                    for (var a = 0; a < all.Count; a++)
                    {
                        for (var b = a + 1; b < all.Count; b++)
                        {
                            var sep = Length(all[a].Pos - all[b].Pos);
                            var touch = all[a].Radius + all[b].Radius;
                            Assert.True(sep > touch, $"R={R} speed={speed} seed={seed}: {all[a].Id}/{all[b].Id} {sep} <= {touch}");
                        }
                    }
                }
            }
        }
        Assert.True(shatters > 20, $"{shatters} shatters");
    }

    [Fact]
    public void StartsANewUndoPointOnlyAfterAQuietSpell()
    {
        var u = new Universe(2451545);
        u.PushHistory("first");
        u.Step(1);
        u.PushHistory("second");
        u.Step(1);
        u.PushHistory("third");
        Assert.Equal(["first"], u.History.Select(h => h.Label));
        for (var i = 0; i < 40; i++) u.Step(1);
        u.PushHistory("later");
        Assert.Equal(["first", "later"], u.History.Select(h => h.Label));
    }

    /// <summary>Two loose rocks on their own, so nothing else sets the substep.</summary>
    private static (Universe U, Body A, Body B, List<ImpactOutcome> Outcomes) RockPair(double radiusKm, double separationKm, double closingKms)
    {
        var u = new Universe(2451545);
        const double rho = 3000;
        var mass = rho * (4.0 / 3) * Math.PI * Math.Pow(radiusKm * 1000, 3);
        var a = Rock("a", "Rock A", mass, radiusKm, new Vec3(0, 0, 0), new Vec3(0, 0, 0));
        var b = Rock("b", "Rock B", mass, radiusKm, new Vec3(separationKm, 0, 0), new Vec3(-closingKms, 0, 0));
        u.SetBodies([a, b]);
        var outcomes = new List<ImpactOutcome>();
        u.CollisionHandlers.Add((ev, uni) => outcomes.Add(ResolveCollision(uni, ev, Identity)));
        return (u, a, b, outcomes);
    }

    [Fact]
    public void CatchesAFastLittleAsteroidHittingAnotherOneInsteadOfLettingItPassThrough()
    {
        // 10 m radius closing at 20 km/s: a substep is 5 km long, 250 times wider than the target.
        var (u, _, _, outcomes) = RockPair(0.01, 400, 20);
        for (var i = 0; i < 400 && outcomes.Count == 0; i++) u.Step(1);
        Assert.Single(outcomes);
        var o = outcomes[0];
        Close.To(20, o.RelSpeed, 3);
        // The contact normal comes from the closest approach, so it points along the approach line.
        Assert.True(Math.Abs(o.NormalWorld.X) > 0.9, $"normal {o.NormalWorld}");
    }

    [Fact]
    public void MergesRocksThatNudgeEachOtherAndShattersOnesThatSlamTogether()
    {
        var gentle = RockPair(0.01, 1, 0.001); // 1 m/s
        var each = gentle.A.Mass;
        for (var i = 0; i < 4000 && gentle.Outcomes.Count == 0; i++) gentle.U.Step(1);
        Assert.Single(gentle.Outcomes);
        Assert.Empty(gentle.Outcomes[0].Fragments);
        Assert.Single(gentle.U.Bodies);
        Close.To(1, gentle.U.Bodies[0].Mass / (2 * each), 9);

        var hard = RockPair(0.01, 20, 0.1); // 100 m/s, well past the strength limit for a 20 m boulder
        for (var i = 0; i < 4000 && hard.Outcomes.Count == 0; i++) hard.U.Step(1);
        Assert.Single(hard.Outcomes);
        Assert.Contains(hard.Outcomes[0].Kind, new[] { OutcomeKind.Shatter, OutcomeKind.Disperse });
        Assert.True(hard.Outcomes[0].Fragments.Count >= 6, $"{hard.Outcomes[0].Fragments.Count} fragments");
    }

    [Fact]
    public void NeedsFarMoreThanSelfGravityToBreakABoulderAndNoMoreThanBeforeToBreakAPlanet()
    {
        var boulder = Rock("r", "r", 1.26e7, 0.01);
        var ratio = DisruptionEnergy(boulder) / BindingEnergy(boulder);
        Assert.True(ratio > 1e5, $"boulder ratio {ratio}");
        var (u, _) = Build();
        var earth = u.ById("earth")!;
        // Gravity still rules at planet scale: the threshold is the old one, a fifth of the binding energy.
        var planet = DisruptionEnergy(earth) / (0.2 * BindingEnergy(earth));
        Assert.True(planet < 1.001, $"Earth ratio {planet}");
    }

    [Fact]
    public void FollowsGravityRatherThanTheCatalogue()
    {
        var (u, _) = Build();
        Assert.Equal("earth", u.PrimaryOf(u.ById("moon")!)?.Id);
        Assert.Equal("sun", u.PrimaryOf(u.ById("earth")!)?.Id);
        Assert.Equal("jupiter", u.PrimaryOf(u.ById("io")!)?.Id);
        Assert.Equal("pluto", u.PrimaryOf(u.ById("charon")!)?.Id);

        // A rock dropped into a circular orbit 5000 km above the Moon belongs to the Moon, not the Sun,
        // even though it was fired at Earth.
        var moon = u.ById("moon")!;
        double r = 5000;
        var speed = Math.Sqrt((G * moon.Mass) / r);
        var rock = Rock("rock", "Rock", 1e9, 0.05, moon.Pos + new Vec3(r, 0, 0), moon.Vel + new Vec3(0, speed, 0), primaryId: "earth");
        u.Add(rock);
        Assert.Equal("moon", u.PrimaryOf(rock)?.Id);
    }
}
