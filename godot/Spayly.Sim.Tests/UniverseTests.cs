using static Spayly.Sim.Constants;

namespace Spayly.Sim.Tests;

// tests/universe.test.ts — describe('solar system'), in order:
//   it('places the planets at sensible heliocentric distances at J2000')        deferred (needs Catalog)
//   it('has the barycentre at rest')                                            ported, hand-built 10-body system
//   it('conserves energy over a year of integration')                           ported, hand-built Sun+Earth+Jupiter;
//                                                                                39-body run deferred
//   it('brings Earth back after one orbital period')                            deferred (needs Catalog)
//   it('keeps the Moon on a monthly orbit')                                     deferred (needs Catalog)
//   it('is exactly time reversible with fixed substeps')                        ported, hand-built Sun+Earth+Moon
//   it('returns close to the start with adaptive substeps')                     ported, hand-built Sun+Earth+Moon
//   it('keeps to its pair budget when a shatter adds bodies partway through a frame')
//                                                                               ported, hand-built 10 bodies + Theia; a test
//                                                                                handler stands in for resolveCollision
//   it('throttles the warp instead of skipping physics')                        ported, hand-built Sun+Earth+Moon
// After those, C#-only: undo coalescing and history, and bit-for-bit agreement with the web build on the hand-built
// systems (Golden/web-universe-handbuilt.json).
public class UniverseTests
{
    [Fact]
    public void HasTheBarycentreAtRest()
    {
        var u = HandBuilt.Build(withPlanets: true);
        var b = u.Barycentre();
        Assert.True(Vec3.Length(b.Pos) < 1, $"barycentre offset {Vec3.Length(b.Pos)} km");
        Assert.True(Vec3.Length(b.Vel) < 1e-9, $"barycentre drift {Vec3.Length(b.Vel)} km/s");
    }

    [Fact]
    public void ConservesEnergyOverAYearOfIntegration()
    {
        var vE = Math.Sqrt((G * SunMass) / AU);
        var u = HandBuilt.AtRest([
            HandBuilt.Sun(),
            HandBuilt.Make("earth", BodyKind.Planet, Surface.Rock, 5.9722e24, 6371, new Vec3(AU, 0, 0), new Vec3(0, vE, 0.001), 2),
            HandBuilt.Planet("jupiter", 5.2028, 1.898e27, 69911, 1),
        ]);
        var e0 = u.Energy().Total;
        double t = 0;
        while (t < Year)
        {
            u.Step(Day);
            t += Day;
        }
        var e1 = u.Energy().Total;
        var rel = Math.Abs((e1 - e0) / e0);
        Assert.True(rel < 1e-6, $"relative energy drift {rel}");
    }

    [Fact]
    public void IsExactlyTimeReversibleWithFixedSubsteps()
    {
        var u = HandBuilt.Build();
        u.MaxSubstep = 1800;
        u.MinSubstep = 1800;
        var snap = u.Snapshot();
        for (var i = 0; i < 30; i++) u.Step(Day);
        for (var i = 0; i < 30; i++) u.Step(-Day);
        foreach (var b in u.Bodies)
        {
            var orig = snap.Bodies.Find(x => x.Id == b.Id)!;
            var d = Vec3.Length(b.Pos - orig.Pos);
            Assert.True(d < 1e-3, $"{b.Id} came back {d} km off");
        }
    }

    [Fact]
    public void ReturnsCloseToTheStartWithAdaptiveSubsteps()
    {
        var u = HandBuilt.Build();
        var snap = u.Snapshot();
        for (var i = 0; i < 60; i++) u.Step(Day);
        for (var i = 0; i < 60; i++) u.Step(-Day);
        foreach (var b in u.Bodies)
        {
            var orig = snap.Bodies.Find(x => x.Id == b.Id)!;
            var d = Vec3.Length(b.Pos - orig.Pos);
            Assert.True(d < 200, $"{b.Id} came back {d} km off");
        }
    }

    /// <summary>
    /// Theia grazing Earth at 60 km/s, as in the TS test. Without Collide, the handler merges Theia into Earth and adds
    /// twelve fragments well clear of everything: what matters here is the body count growing mid-frame.
    /// </summary>
    private static (Universe U, int N0, List<CollisionEvent> Events) ShatterSetup()
    {
        var u = HandBuilt.Build(withPlanets: true);
        var earth = u.ById("earth")!;
        var dir = new Vec3(0, 1, 0);
        u.Add(HandBuilt.Make("theia", BodyKind.Asteroid, Surface.Rock, 6.4e23, 3390,
            earth.Pos + dir * (earth.Radius + 3390 + 300), earth.Vel + dir * -60, 6));
        var n0 = u.Bodies.Count;
        u.MaxPairEvalsPerFrame = 60 * (n0 * (n0 - 1)) / 2;
        var events = new List<CollisionEvent>();
        u.CollisionHandlers.Add((ev, uni) =>
        {
            events.Add(ev);
            var e = uni.ById("earth")!;
            var t = uni.ById("theia");
            if (t == null) return;
            var m = e.Mass + t.Mass;
            var v = (e.Vel * e.Mass + t.Vel * t.Mass) * (1 / m);
            uni.Remove(t);
            e.Mass = m;
            e.Vel = v;
            for (var k = 0; k < 12; k++)
            {
                var off = HandBuilt.Quad(k % 4, 20000 + 1000 * k, 0);
                uni.Add(HandBuilt.Make("frag" + k, BodyKind.Fragment, Surface.Rock, 1e18, 50, e.Pos + off, e.Vel, 7));
            }
        });
        return (u, n0, events);
    }

    [Fact]
    public void KeepsToItsPairBudgetWhenAShatterAddsBodiesPartwayThroughAFrame()
    {
        var (u, n0, events) = ShatterSetup();
        u.Step(3600);
        Assert.NotEmpty(events);
        Assert.True(u.Bodies.Count > n0, $"{u.Bodies.Count} bodies after, {n0} before");
        Assert.True(u.WarpLimited);
        // At most one collision re-pack past the budget: the check runs before each substep.
        var n = u.Bodies.Count;
        Assert.True(u.PairEvalsLastFrame <= u.MaxPairEvalsPerFrame + (n * (n - 1)) / 2,
            $"{u.PairEvalsLastFrame} pair evaluations against a budget of {u.MaxPairEvalsPerFrame} (+{(n * (n - 1)) / 2})");
    }

    [Fact]
    public void ThrottlesTheWarpInsteadOfSkippingPhysics()
    {
        var u = HandBuilt.Build();
        u.MaxSubstepsPerFrame = 5;
        var advanced = u.Step(30 * Day);
        Assert.True(u.WarpLimited);
        Assert.True(advanced < 30 * Day, $"advanced {advanced} s");
        Assert.True(advanced > 0, $"advanced {advanced} s");
    }

    // ---- C#-only ----

    [Fact]
    public void CoalescesUndoPointsWithinACascade()
    {
        var u = HandBuilt.Build();
        u.Step(60);
        u.PushHistory("hit");
        var t0 = u.Time;
        // Staggered 10 steps apart, the five span 40 steps: the quiet gap runs from the latest collision, not the first.
        for (var c = 0; c < 4; c++)
        {
            for (var s = 0; s < 10; s++) u.Step(60);
            u.PushHistory("cascade " + c);
        }
        Assert.Single(u.History);
        Assert.Equal("hit", u.History[0].Label);
        Assert.Equal(t0, u.History[0].Time);

        for (var s = 0; s < Universe.HistoryQuietSteps; s++) u.Step(60);
        u.PushHistory("still the cascade");
        Assert.Single(u.History);

        for (var s = 0; s < Universe.HistoryQuietSteps + 1; s++) u.Step(60);
        u.PushHistory("next");
        Assert.Equal(2, u.History.Count);
        Assert.Equal("next", u.History[1].Label);
    }

    [Fact]
    public void ForcedUndoPointSkipsTheCascadeThrottleAndLeavesItsClock()
    {
        var u = HandBuilt.Build();
        u.Step(60);
        u.PushHistory("hit");
        u.Step(60);
        u.PushHistory("before black hole", force: true);
        Assert.Equal(2, u.History.Count);
        Assert.Equal("before black hole", u.History[1].Label);
        // The forced point did not restart the cascade: a collision after the quiet gap still records its own.
        for (var s = 0; s < Universe.HistoryQuietSteps; s++) u.Step(60);
        u.PushHistory("next");
        Assert.Equal(3, u.History.Count);
    }

    [Fact]
    public void KeepsFiftyUndoPointsAndRestoresADeepCopy()
    {
        var u = HandBuilt.Build();
        var dropped = new List<string>();
        u.History.Dropped = s => dropped.Add(s.Label);
        for (var k = 0; k < UndoHistory.DefaultMaxPoints + 2; k++)
        {
            for (var s = 0; s <= Universe.HistoryQuietSteps; s++) u.Step(60);
            u.PushHistory(k.ToString());
        }
        Assert.Equal(UndoHistory.DefaultMaxPoints, u.History.Count);
        Assert.Equal("2", u.History[0].Label);
        // The oldest go first, and the app hears about each so it can free what the point held.
        Assert.Equal(["0", "1"], dropped);

        var top = u.History[^1];
        var earthThen = top.Bodies.Find(b => b.Id == "earth")!.Pos;
        u.Step(Day);
        var s0 = u.PopHistory();
        Assert.Same(top, s0);
        Assert.Equal(UndoHistory.DefaultMaxPoints - 1, u.History.Count);
        Assert.Equal(top.Time, u.Time);
        Assert.Equal(double.NegativeInfinity, u.LastCollisionStep);
        var earth = u.ById("earth")!;
        Assert.Equal(earthThen, earth.Pos);
        // The restored bodies are copies: mutating them leaves the snapshot able to restore again.
        Assert.DoesNotContain(earth, top.Bodies);
        earth.Damage.Hits = 99;
        earth.Mass = 1;
        Assert.Equal(0, top.Bodies.Find(b => b.Id == "earth")!.Damage.Hits);
        Assert.Equal(5.9722e24, top.Bodies.Find(b => b.Id == "earth")!.Mass);

        while (u.PopHistory() != null) { }
        Assert.Empty(u.History);
        Assert.Null(u.PopHistory());
    }

    /// <summary>Every body's id, mass, radius, position and velocity bits, in order, and the clock: equal means exactly equal.</summary>
    private static List<(string, long, long, long, long, long, long, long, long)> Bits(Universe u)
    {
        static long B(double x) => BitConverter.DoubleToInt64Bits(x);
        var list = new List<(string, long, long, long, long, long, long, long, long)>();
        foreach (var b in u.Bodies) list.Add((b.Id, B(b.Mass), B(b.Radius), B(b.Pos.X), B(b.Pos.Y), B(b.Pos.Z), B(b.Vel.X), B(b.Vel.Y), B(b.Vel.Z)));
        list.Add(("time", B(u.Time), 0, 0, 0, 0, 0, 0, 0));
        return list;
    }

    [Fact]
    public void EvictsTheOldestPointsPastTheByteBudget()
    {
        var u = HandBuilt.Build();
        var perPoint = u.Snapshot().Bytes;
        Assert.True(perPoint > 0);
        // Room for three points' bodies plus a little: the fourth push must drop the first.
        u.History.ByteBudget = 3 * perPoint + perPoint / 2;
        var dropped = new List<string>();
        u.History.Dropped = s => dropped.Add(s.Label);
        foreach (var label in new[] { "a", "b", "c", "d", "e" }) u.PushHistory(label, force: true, kind: UndoKind.Other);
        Assert.Equal(["c", "d", "e"], u.History.Select(h => h.Label));
        Assert.Equal(["a", "b"], dropped);
        Assert.Equal(2, u.History.Evictions);
        Assert.True(u.History.Bytes <= u.History.ByteBudget, $"{u.History.Bytes} > {u.History.ByteBudget}");
        // The newest point stays even when it alone is over budget: the last action must stay undoable.
        u.History.ByteBudget = 1;
        u.PushHistory("f", force: true, kind: UndoKind.Other);
        Assert.Equal(["f"], u.History.Select(h => h.Label));
    }

    [Fact]
    public void ShedsTheOldestPointsGpuStateButKeepsThePoints()
    {
        var u = HandBuilt.Build();
        // A stand-in for the app: every point holds 100 "VRAM bytes" until asked to let go of them.
        u.AttachState = s =>
        {
            s.Attachment = s.Label;
            s.VramBytes = 100;
        };
        var shed = new List<string>();
        u.History.ShedVram = s =>
        {
            shed.Add(s.Label);
            s.VramBytes = 0;
        };
        u.History.VramBudget = 250;
        foreach (var label in new[] { "a", "b", "c", "d" }) u.PushHistory(label, force: true, kind: UndoKind.Other);
        Assert.Equal(4, u.History.Count);
        Assert.Equal(["a", "b"], shed);
        Assert.Equal(200, u.History.VramBytes);
        Assert.Equal(0, u.History[0].VramBytes);
        Assert.Equal(100, u.History[^1].VramBytes);
        Assert.Equal(2, u.History.Sheds);
    }

    [Fact]
    public void CarriesLabelsKindsSubjectsAndTheAppsAttachmentOnEveryPoint()
    {
        var u = HandBuilt.Build();
        var captured = 0;
        u.AttachState = s =>
        {
            s.Attachment = ++captured;
            s.AttachmentBytes = 1000;
        };
        u.PushHistory("placing Black hole 1", force: true, kind: UndoKind.PlaceHole, subject: "bh-1");
        for (var s = 0; s <= Universe.HistoryQuietSteps; s++) u.Step(60);
        // An automatic point, as Collide records one: it carries the app's state too.
        u.PushHistory("Rock hitting Earth", subject: "ast-1");
        Assert.Equal(2, u.History.Count);
        var hole = u.History[0];
        Assert.Equal("placing Black hole 1", hole.Label);
        Assert.Equal(UndoKind.PlaceHole, hole.Kind);
        Assert.Equal("bh-1", hole.Subject);
        Assert.Equal(1, hole.Attachment);
        Assert.Equal(hole.BodyBytes + 1000, hole.Bytes);
        var hit = u.History[1];
        Assert.Equal(UndoKind.Collision, hit.Kind);
        Assert.Equal("ast-1", hit.Subject);
        Assert.Equal(2, hit.Attachment);

        // Undo hands back the point, and the state it replaced waits on the redo list under the same label, attached.
        var undone = u.Undo();
        Assert.Same(hit, undone);
        var redo = Assert.Single(u.History.Redo);
        Assert.Equal("Rock hitting Earth", redo.Label);
        Assert.Equal(UndoKind.Collision, redo.Kind);
        Assert.Equal("ast-1", redo.Subject);
        Assert.Equal(3, redo.Attachment);
        var redone = u.Redo();
        Assert.Same(redo, redone);
        Assert.Equal("Rock hitting Earth", u.History[^1].Label);
        Assert.Equal(4, u.History[^1].Attachment);
    }

    [Fact]
    public void ANewPointClearsRedo()
    {
        var u = HandBuilt.Build();
        var dropped = new List<string>();
        u.History.Dropped = s => dropped.Add(s.Label);
        u.PushHistory("one", force: true, kind: UndoKind.Mass);
        u.PushHistory("two", force: true, kind: UndoKind.Radius);
        u.Undo();
        u.Undo();
        Assert.Equal(2, u.History.Redo.Count);
        Assert.Empty(u.History);
        Assert.Null(u.Undo());
        u.PushHistory("three", force: true, kind: UndoKind.Reset);
        Assert.Empty(u.History.Redo);
        Assert.Equal(["one", "two"], dropped.Order());
        Assert.Null(u.Redo());
        Assert.Equal(["three"], u.History.Select(h => h.Label));
    }

    [Fact]
    public void UndoThenRedoRoundTripsTheBodiesBitForBit()
    {
        var u = HandBuilt.Build(withPlanets: true);
        u.Step(Day);
        var before = Bits(u);
        u.PushHistory("Earth's mass change", force: true, kind: UndoKind.Mass, subject: "earth");
        u.ById("earth")!.Mass *= 3;
        for (var s = 0; s < 20; s++) u.Step(3600);
        var after = Bits(u);
        Assert.NotEqual(before, after);

        Assert.NotNull(u.Undo());
        Assert.Equal(before, Bits(u));
        Assert.NotNull(u.Redo());
        Assert.Equal(after, Bits(u));
        Assert.NotNull(u.Undo());
        Assert.Equal(before, Bits(u));
        // The restored bodies are clones: changing them leaves the redo point able to restore again.
        u.ById("earth")!.Mass = 1;
        Assert.NotNull(u.Redo());
        Assert.Equal(after, Bits(u));
    }

    [Fact]
    public void TheDebrisBookAndAHolesFeedCloneForUndoWithHomesRemappedToTheRestoredBodies()
    {
        var u = HandBuilt.Build();
        var earth = u.ById("earth")!;
        var ledger = new Debris.DebrisLedger(Vec3.Zero);
        ledger.Baseline(u.Bodies, Vec3.Zero);
        ledger.ToDust(earth, 1e15, earth.Pos + new Vec3(1e4, 0, 0), earth.Vel);
        var snap = u.Snapshot();
        u.Restore(snap);
        var restored = u.ById("earth")!;
        Assert.NotSame(earth, restored);

        var copy = ledger.Clone(b => u.ById(b.Id) ?? b);
        Assert.Equal(ledger.Dust, copy.Dust);
        Assert.Equal(ledger.Particles, copy.Particles);
        Assert.Equal(ledger.Error(), copy.Error());
        Assert.Equal(ledger.Dustings, copy.Dustings);
        Assert.True(copy.Reservoirs.ContainsKey(restored));
        Assert.False(copy.Reservoirs.ContainsKey(earth));
        // Independent: the original moves on, the copy does not.
        ledger.ToDust(earth, 1e15, earth.Pos, earth.Vel);
        Assert.NotEqual(ledger.Dust, copy.Dust);

        var feed = new AccretionFeed(100);
        feed.Deposit(5e20);
        feed.Advance(50);
        var f2 = feed.Clone();
        Assert.Equal((feed.Reservoir, feed.Deposited, feed.Accreted, feed.ViscousTime), (f2.Reservoir, f2.Deposited, f2.Accreted, f2.ViscousTime));
        feed.Advance(50);
        Assert.NotEqual(feed.Reservoir, f2.Reservoir);
    }

    [Fact]
    public void UndoOpensAQuietWindowSoTheUndoneImpactDoesNotRecordItselfAgain()
    {
        var u = HandBuilt.Build();
        u.PushHistory("launching Rock 1", force: true, kind: UndoKind.Launch);
        for (var s = 0; s <= Universe.HistoryQuietSteps; s++) u.Step(60);
        u.PushHistory("Rock 1 hitting Earth");
        Assert.Equal(2, u.History.Count);
        u.Undo();
        Assert.Equal((double)u.Steps, u.LastCollisionStep);
        // The pair is at contact again: its collision on the next step is the same event, so it gets no point...
        u.Step(60);
        u.PushHistory("Rock 1 hitting Earth");
        Assert.Single(u.History);
        // ...and the next undo goes back before the launch.
        Assert.Equal("launching Rock 1", u.Undo()!.Label);
    }

    [Fact]
    public void TheHistoryVersionMovesOnEveryChangeAndOnlyThen()
    {
        var u = HandBuilt.Build();
        var h = u.History;
        var seen = h.Version;
        void Moved(string what)
        {
            Assert.True(h.Version > seen, $"{what}: version {h.Version} did not move from {seen}");
            seen = h.Version;
        }
        void Still(string what) => Assert.True(h.Version == seen, $"{what}: version moved from {seen} to {h.Version}");

        u.PushHistory("one", force: true, kind: UndoKind.Mass);
        Moved("push");
        _ = h.NextUndo;
        _ = h.Bytes;
        foreach (var _ in h) { }
        Still("reading");
        u.Undo();
        Moved("undo");
        u.Redo();
        Moved("redo");
        Assert.Null(u.Redo());
        Still("a redo with nothing to redo");
        u.PopHistory();
        Moved("pop");
        h.MaxPoints = 2;
        for (var i = 0; i < 3; i++) u.PushHistory($"p{i}", force: true, kind: UndoKind.Other);
        Assert.Equal(1, h.Evictions);
        Moved("pushes with an eviction");
        h.VramBudget = 10;
        h.ShedVram = s => s.VramBytes = 0;
        u.AttachState = s => s.VramBytes = 8;
        u.PushHistory("p3", force: true, kind: UndoKind.Other);
        Moved("push, evicting p1");
        u.PushHistory("p4", force: true, kind: UndoKind.Other);
        Assert.Equal(1, h.Sheds);
        Moved("push with a shed");
        h.Clear();
        Moved("clear");
        h.Clear();
        Still("clearing an empty history");
    }

    [Fact]
    public void AShowersPointHoldsItsOwnRocksImpactsUntilAnotherPointLandsOnTop()
    {
        var u = HandBuilt.Build();
        void Quiet()
        {
            for (var s = 0; s <= Universe.HistoryQuietSteps; s++) u.Step(60);
        }
        u.PushHistory("a meteor shower of 8 rocks", force: true, kind: UndoKind.Shower, subject: "shower1-");
        Quiet();
        u.PushHistory("Meteor 3/8 hitting Earth", subject: "shower1-3");
        Quiet();
        u.PushHistory("Meteor 5/8 hitting Earth", subject: "shower1-5");
        Assert.Equal(["a meteor shower of 8 rocks"], u.History.Select(p => p.Label));
        // Someone else's rock is its own event.
        Quiet();
        u.PushHistory("Rock 2 hitting Earth", subject: "ast-2");
        Assert.Equal(2, u.History.Count);
        // With another point on top, a straggler from the shower records its own again.
        Quiet();
        u.PushHistory("Meteor 7/8 hitting Mars", subject: "shower1-7");
        Assert.Equal(3, u.History.Count);
        // A later shower's prefix does not match an earlier one's rocks.
        u.PushHistory("a meteor shower of 8 rocks", force: true, kind: UndoKind.Shower, subject: "shower2-");
        Quiet();
        u.PushHistory("Meteor 8/8 hitting Earth", subject: "shower1-8");
        Assert.Equal(5, u.History.Count);
        // One undo from after the shower's impacts goes back to before the whole volley.
        u.History.Clear();
        u.PushHistory("a meteor shower of 8 rocks", force: true, kind: UndoKind.Shower, subject: "shower3-");
        Quiet();
        u.PushHistory("Meteor 1/8 hitting Earth", subject: "shower3-1");
        Assert.Equal("a meteor shower of 8 rocks", u.Undo()!.Label);
        Assert.Empty(u.History);
    }

    /// <summary>
    /// The adaptive step, the budget and the swept contact test reproduce the web build exactly (values from
    /// Golden/web-universe-handbuilt.json). If the step loop's floating-point order drifts, the substep counts here are
    /// the first thing to move, and 29631 on the 39-body system (web-universe-j2000.json) the next.
    /// </summary>
    [Fact]
    public void MatchesTheWebBuildBitForBitOnHandBuiltSystems()
    {
        var u = HandBuilt.Build();
        var e0 = u.Energy();
        Assert.Equal(2.683875988051168e+33, e0.Kinetic);
        Assert.Equal(-5.363147525348918e+33, e0.Potential);
        Assert.Equal(-2.67927153729775e+33, e0.Total);
        var substeps = 0;
        for (var i = 0; i < 30; i++)
        {
            u.Step(Day);
            substeps += u.SubstepsLastFrame;
        }
        Assert.Equal(368, substeps);
        Assert.Equal(2591999.999999997, u.Time);
        Assert.Equal(new Vec3(130118641.72614422, 73848385.10605937, 3799.9451272012034), u.ById("earth")!.Pos);
        Assert.Equal(new Vec3(-15.401247003180917, 26.639608724121203, 0.03549899775487142), u.ById("moon")!.Vel);
        Assert.Equal(-2.679271537009786e+33, u.Energy().Total);
        Assert.Equal(372927.9705154545, u.ShortestDynamicalTime);

        var w = HandBuilt.Build(withPlanets: true);
        substeps = 0;
        for (var i = 0; i < 30; i++)
        {
            w.Step(Day);
            substeps += w.SubstepsLastFrame;
        }
        Assert.Equal(368, substeps);
        Assert.Equal(new Vec3(130326050.6813037, 73239000.23332568, 3799.9436796363248), w.ById("earth")!.Pos);
        Assert.Equal(-1.9820018577753874e+35, w.Energy().Total);
    }

    [Fact]
    public void MatchesTheWebBuildThroughAMidFrameCollision()
    {
        var (u, n0, events) = ShatterSetup();
        Assert.Equal(11, n0);
        var advanced = u.Step(3600);
        Assert.Equal(1015.7021754661628, advanced);
        var ev = Assert.Single(events);
        Assert.Equal("earth", ev.A.Id);
        Assert.Equal("theia", ev.B.Id);
        Assert.Equal(6.63985142182703, ev.Time);
        Assert.Equal(new Vec3(149773548.0913771, -616516.7047186842, 0.0066398192638052874), ev.PosA);
        Assert.Equal(new Vec3(149773548.0913771, -606854.1938727633, 0.006639819263805134), ev.PosB);
        Assert.Equal(22, u.Bodies.Count);
        Assert.True(u.WarpLimited);
        Assert.Equal(3113, u.PairEvalsLastFrame);
        Assert.Equal(14, u.SubstepsLastFrame);
        Assert.Equal(new Vec3(149773557.31107715, -592319.678126966, 1.0156979824707126), u.ById("earth")!.Pos);
        Assert.Equal(new Vec3(149792990.29273504, -592319.6781305183, 1.0156981083570154), u.ById("frag0")!.Pos);
    }
}

// Deferred to the catalogue-backed pass (need CreateSolarSystem, the 39-body J2000 system):
//   it('places the planets at sensible heliocentric distances at J2000')
//   it('conserves energy over a year of integration')           — on the real system; the hand-built one is above
//   it('brings Earth back after one orbital period')
//   it('keeps the Moon on a monthly orbit')                     — the Moon-period check
// and, once Catalog lands, re-run the hand-built its above on the real system as the TS does, and check the
// Golden/web-universe-j2000.json substep count (29631 after 30 day-steps) and energies.
