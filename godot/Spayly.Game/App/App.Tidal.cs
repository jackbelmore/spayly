using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading.Tasks;
using Godot;
using Spayly.Game.Render;
using Spayly.Sim;
using Spayly.Sim.Debris;

namespace Spayly.Game.App;

/// <summary>
/// Tidal disruption in the running game (Spayly.Sim.Tidal does the physics): every <see cref="TidalCheckEvery"/> frames the
/// check looks for a body whose periapsis lies inside its Roche limit or a black hole's tidal radius; each hit pushes an
/// undo point, removes the body (or shrinks it to its core), hands the stream to the debris tier's T2 particles, books the
/// hand-off in the debris ledger as T0 → T2, flashes and hints. A black-hole TDE also feeds that hole's disc with the
/// t^(−5/3) fallback (App.HoleDisc.cs).
///
/// Setting: <see cref="Settings.Tidal"/> (on). Capture verbs (App.Capture.cs routes them here): <c>tidal:0|1</c>,
/// <c>tidalDemo:bodyId[,apoapsisKm]</c> (default the Moon, onto an orbit whose periapsis lies inside its limit),
/// <c>tdeDemo:massSolar[,beta]</c> (a new hole far from the Sun and a Sun-like star falling in on a parabola) and
/// <c>logTidal</c>.
/// </summary>
public partial class App
{
    /// <summary>Frames between tidal checks; the check's window covers the sim time in between.</summary>
    const int TidalCheckEvery = 10;
    /// <summary>Most disruptions one check carries out (each samples and queues up to DebrisPerImpact particles).</summary>
    const int TidalMaxPerCheck = 4;
    /// <summary>[GAME] A TDE stream lives at least this many most-bound return times, so the bound half is seen coming back.</summary>
    const double TdeLifetimeReturns = 4;
    /// <summary>tdeDemo puts the new hole this far from the Sun, AU, so its pull barely touches the solar system.</summary>
    const double TdeDemoDistanceAu = 2000;

    readonly List<TidalCandidate> tidalHits = [];
    /// <summary>Hits of the last check still to be carried out, one at a time, in the check's order.</summary>
    readonly Queue<TidalCandidate> tidalQueue = new();

    /// <summary>What a disruption computed off the main thread hands back: the sampled stream and its tally about Origin.</summary>
    sealed record TidalWork(TidalDisruption D, Tally Flow, Vec3 Origin);

    /// <summary>A disruption being sampled on a worker, and the state it was sampled from.</summary>
    sealed class TidalJob
    {
        public required TidalCandidate Hit;
        public required Task<TidalWork> Work;
        public required int N;
        public required double Time;
        public required Vec3 PrimaryPos, SecondaryPos;
        public required double SecondaryMass;
        public long Started;
        public ulong Frame;
    }

    TidalJob? tidalJob;

    /// <summary>
    /// True while a disruption is being sampled: the sim holds still (App.Tick passes it as a pause), so the stream is
    /// applied at exactly the sim time and state it was sampled from and the physics is what a same-frame disruption gives.
    /// Sampling 50,000 particles (Tidal.Disrupt 140 ms, then queueing and tallying them 86 ms) cost a 132 ms frame (capture
    /// m7-perf-tde-before); on a worker the frames stay short and the sim waits a few of them.
    /// </summary>
    bool TidalHold => tidalJob != null;
    int tidalFrames;
    double tidalWindow;
    int tidalStars;
    string lastTidal = "none";

    /// <summary>Step 13, before the debris tier steps, so this frame's stream is uploaded at this frame's positions.</summary>
    /// <summary>Tidal disruption's Realism entries; the HUD calls this when it builds the Realism panel.</summary>
    public static void RegisterTidalRealism()
    {
        const RealismArea a = RealismArea.Collisions;
        Realism.Register("Roche and tidal limits", RealismTag.Sci, "Fluid Roche limit 2.44 R(ρ_M/ρ_m)^⅓ for gas, stars and melt; rigid 1.26 for rock and ice; TDE radius R*(M/M*)^⅓ (Rees 1988).", a);
        Realism.Register("Swallowed whole", RealismTag.Sci, "Above ≈1.1×10⁸ M☉ a Sun-like star's tidal radius lies inside the horizon, so it vanishes without a stream.", a);
        Realism.Register("Tidal energy spread", RealismTag.Sci, "Stream debris gets ΔE ≈ GM R*/r_t²; about half is bound, and fallback decays as t^−5/3 (Phinney 1989).", a);
        Realism.Register("Partial disruption ramp", RealismTag.Game, "10 % of the body is stripped at β = 1 rising to all of it at β = 2; the core is resized so one pass tears it once, and the stripped layers leave at just over its escape speed.", a);
        Realism.Register("Stream particle lifetime", RealismTag.Game, "TDE particles live longer than impact ejecta so the bound half can return; streams under 50 km are not spawned.", a);
        Realism.Register("Plunging into a hole", RealismTag.Sci, "A star headed straight into a black hole is torn apart as it crosses the tidal radius, before it reaches the hole; a body headed into a planet or star simply hits it.", a);
        Realism.Register("Stream particles drawn", RealismTag.Art, "Tidal-stream particles are drawn at least 3 px across and glowing like hot gas, whatever their age, so a stream 10⁸ km long reads from far enough away to see all of it.", RealismArea.Rendering);
        Realism.Register("Compressed fallback clock", RealismTag.Art, "The most-bound debris returns after about a month; the disc shows that return 10 real seconds after the disruption and each tenfold of the decline 5 s later, unless the time warp is already faster.", RealismArea.Time);
        Realism.Register("Tidal-disruption flare", RealismTag.Game, "Only the disrupting hole's disc flares: it radiates ε Ṁ c² of the t^−5/3 fallback rate, capped at Eddington, assuming the returning debris circularises and accretes at once.", RealismArea.Rendering);
    }

    void UpdateTidal(double dtReal)
    {
        if (tidalJob != null || tidalQueue.Count > 0)
        {
            PumpTidal();
            return;
        }
        if (!Settings.Tidal)
        {
            tidalWindow = 0;
            return;
        }
        tidalWindow += Math.Abs(Sim.LastAdvanced);
        // With a black hole about, every frame: a star falls from its tidal radius to the horizon in minutes of sim time,
        // which ten frames of warp can step over, and the pair loop costs microseconds.
        var holes = false;
        foreach (var b in Universe.Bodies)
        {
            if (b.Kind != BodyKind.BlackHole) continue;
            holes = true;
            break;
        }
        if (++tidalFrames % (holes ? 1 : TidalCheckEvery) != 0) return;
        var window = tidalWindow;
        tidalWindow = 0;
        // Tearing a body apart is not time-reversible: rewinding runs gravity backwards and never un-disrupts anything.
        if (Rewind || Paused || !(window > 0)) return;
        Tidal.TidalCheck(Universe, window, tidalHits);
        // A giant impact can leave a dozen molten fragments inside the fluid limit at once; the rest wait for the next check.
        for (var i = 0; i < Math.Min(tidalHits.Count, TidalMaxPerCheck); i++) tidalQueue.Enqueue(tidalHits[i]);
        PumpTidal();
    }

    /// <summary>Apply the finished disruption, if any, then start sampling the next queued hit; the sim holds meanwhile.</summary>
    void PumpTidal()
    {
        if (tidalJob is { } job)
        {
            if (!job.Work.IsCompleted) return;
            tidalJob = null;
            var c = job.Hit;
            var ms = Stopwatch.GetElapsedTime(job.Started).TotalMilliseconds;
            if (job.Work.IsFaulted)
            {
                GD.PushError($"[tidal] sampling {c.Secondary.Id} failed off the main thread, sampling it here: {job.Work.Exception?.GetBaseException().Message}");
                DisruptTidally(c);
            }
            // Undo, erase or a slider can change the pair while it is sampled: then the sample is not of this state.
            else if (Universe.Time != job.Time || !ReferenceEquals(Universe.ById(c.Secondary.Id), c.Secondary) || !ReferenceEquals(Universe.ById(c.Primary.Id), c.Primary)
                     || c.Primary.Pos != job.PrimaryPos || c.Secondary.Pos != job.SecondaryPos || c.Secondary.Mass != job.SecondaryMass)
            {
                GD.Print($"[tidal] {c.Secondary.Id}'s disruption dropped: the pair changed while it was sampled");
                tidalQueue.Clear();
            }
            else
            {
                GD.Print(string.Create(CultureInfo.InvariantCulture, $"[tidal] {c.Secondary.Id} sampled off the main thread in {ms:F1} ms, sim held {Engine.GetProcessFrames() - job.Frame} frames"));
                DisruptTidally(c, job.Work.Result, job.N);
            }
        }
        while (tidalJob == null && tidalQueue.Count > 0) StartTidal(tidalQueue.Dequeue());
    }

    /// <summary>Start sampling <paramref name="c"/> on a worker from clones of the pair, with this frame's particle budget and seed.</summary>
    void StartTidal(TidalCandidate c)
    {
        var p = c.Primary;
        var s = c.Secondary;
        // An earlier hit in the same check may have consumed either body.
        if (!ReferenceEquals(Universe.ById(s.Id), s) || !ReferenceEquals(Universe.ById(p.Id), p)) return;
        EnsureDebris();
        var n = TidalParticles();
        var rand = TidalRng(s);
        var pc = p.Clone();
        var sc = s.Clone();
        var origin = ledger!.Origin;
        tidalJob = new TidalJob
        {
            Hit = c, N = n, Time = Universe.Time, PrimaryPos = p.Pos, SecondaryPos = s.Pos, SecondaryMass = s.Mass, Started = Stopwatch.GetTimestamp(), Frame = Engine.GetProcessFrames(),
            // The tally sums in DisruptTidally's order, so it is the same double-double result.
            Work = Task.Run(() =>
            {
                var d = Tidal.Disrupt(pc, sc, n, rand);
                var flow = Tally.Zero;
                for (var i = 0; i < d.Count; i++) flow += Tally.Of(d.Mass[i], pc.Pos + d.Position[i], pc.Vel + d.Velocity[i], origin);
                return new TidalWork(d, flow, origin);
            }),
        };
    }

    /// <summary>Stream particles a disruption may take now: the tier's free room, capped per event, even, or none below the minimum.</summary>
    int TidalParticles()
    {
        var tier = debris is { IsReady: true } t ? t : null;
        var n = tier != null ? Math.Min(DebrisPerImpact, tier.CanQueue) & ~1 : 0;
        return n < Tidal.MinParticles ? 0 : n;
    }

    Func<double> TidalRng(Body s) => Rng.Mulberry32(unchecked((uint)(s.Seed ^ (int)(long)Math.Floor(Universe.Time)) ^ 0x27D4EB2Fu));

    /// <summary>Drop a disruption in flight and the rest of its check (undo, reset): the state it was sampled from is gone.</summary>
    void CancelTidal()
    {
        tidalJob = null;
        tidalQueue.Clear();
    }

    public void SetTidal(bool on)
    {
        Settings.Tidal = on;
        tidalWindow = 0;
        GD.Print($"[tidal] tidal disruption {(on ? "on" : "off")}");
    }

    /// <summary>
    /// Carry out one disruption: undo point, stream into the tier, ledger, flash. <paramref name="work"/> is the stream a
    /// worker sampled with <paramref name="n"/> particles from this very state (<see cref="PumpTidal"/>); without it the
    /// stream is sampled here.
    /// </summary>
    void DisruptTidally(TidalCandidate c, TidalWork? work = null, int n = 0)
    {
        var p = c.Primary;
        var s = c.Secondary;
        if (!ReferenceEquals(Universe.ById(s.Id), s) || !ReferenceEquals(Universe.ById(p.Id), p)) return;
        EnsureDebris();
        var l = ledger!;
        var tier = debris is { IsReady: true } t ? t : null;
        if (work == null) n = TidalParticles();
        Universe.PushHistory($"{s.Name} being torn apart by {p.Name}", kind: UndoKind.TidalDisruption, subject: s.Id);

        var d = work?.D ?? Tidal.Disrupt(p, s, n, TidalRng(s));
        var origin = l.Origin;
        var pre = work != null && work.Origin == origin ? work : null;
        var pos0 = s.Pos;
        var vel0 = s.Vel;
        var radius = d.Count > 0 ? Body.RadiusForDensity(d.Mass[0], Body.Density(s)) : 0;
        var flow = Tally.Zero;
        var queued = 0;
        for (; queued < d.Count; queued++)
        {
            var r = p.Pos + d.Position[queued];
            var v = p.Vel + d.Velocity[queued];
            if (!tier!.Queue(r, v, d.Mass[queued], radius, stream: true)) break;
            if (pre == null) flow += Tally.Of(d.Mass[queued], r, v, origin);
        }
        if (pre != null)
        {
            if (queued == d.Count) flow = pre.Flow;
            else for (var i = 0; i < queued; i++) flow += Tally.Of(d.Mass[i], p.Pos + d.Position[i], p.Vel + d.Velocity[i], origin);
        }
        // What found no slot (nothing, while CanQueue holds) waits as dust at the primary; with no tier, all of it does.
        var rest = Tally.Zero;
        for (var i = queued; i < d.Count; i++) rest += Tally.Of(d.Mass[i], p.Pos + d.Position[i], p.Vel + d.Velocity[i], origin);
        if (d.Count == 0) rest = Tally.Of(d.StreamMass, pos0, vel0, origin);
        // A fragment Collide made this frame is unknown to the audit, which counts only what is left of it as Collide's
        // new mass; the stream is mass Collide produced and T2 took, like the LS12 particle tier.
        if (newFragments.Contains(s) && !known.ContainsKey(s)) lsInjectedFrame += d.StreamMass;

        if (d.Core is TidalCore core)
        {
            // T0 → T2 through the body: it loses the stream's mass and momentum, the sub-ulp remainder goes to its reservoir.
            var m0 = s.Mass;
            l.InjectFromBody(s, flow + rest);
            if (d.Count == 0) l.ToDust(p, d.StreamMass, pos0, vel0);
            else for (var i = queued; i < d.Count; i++) l.ToDust(p, d.Mass[i], p.Pos + d.Position[i], p.Vel + d.Velocity[i]);
            NoteLedger(s, s.Mass - m0);
            Tidal.ShrinkToCore(s, core.Radius);
        }
        else
        {
            // T0 → T2 at exactly the body's tally, so the book's T0 stays the exact sum over the bodies; the stream carries it
            // to the rounding of its last particle.
            var body = Tally.Of(s.Mass, s.Pos, s.Vel, origin);
            NoteLedger(s, -s.Mass);
            Universe.Remove(s);
            if (queued == 0) l.DustDetached(p, body);
            else
            {
                l.InjectDetached(body - rest);
                if (!rest.IsZero) l.DustDetached(p, rest);
            }
            if (ReferenceEquals(Selected, s)) Select(p);
            if (ReferenceEquals(Rig.Focus, s)) Rig.Focus = p;
        }

        TidalFlash(p, s, d, pos0);
        if (p.Kind == BodyKind.BlackHole && tier != null && double.IsFinite(d.ReturnTime))
        {
            // [GAME] The tier's global lifetime and bound radius, raised so the bound half lives to come back and the unbound
            // half is not turned to dust on its way out.
            var life = TdeLifetimeReturns * d.ReturnTime;
            var vOut = Math.Sqrt(2 * Math.Max(0, d.CentreEnergy + d.SpreadEnergy));
            if (life > tier.Lifetime) tier.Lifetime = life;
            tier.BoundRadius = Math.Max(tier.BoundRadius, 2 * (d.DistanceKm + vOut * tier.Lifetime));
            GD.Print(string.Create(CultureInfo.InvariantCulture,
                $"[tidal] debris tier lifetime={tier.Lifetime / Constants.Day:G4} d bound_radius={tier.BoundRadius:G4} km (TDE stream)"));
        }
        if (p.Kind == BodyKind.BlackHole && d.BoundMass > 0 && double.IsFinite(d.ReturnTime)) StartTdeFallback(p, d);

        var err = l.Error();
        var inv = CultureInfo.InvariantCulture;
        var coreText = d.Core is TidalCore kept ? string.Create(inv, $"{kept.Mass:G6} kg r={kept.Radius:G5} km") : "none";
        var dustKg = d.Core is null && queued == 0 ? d.StreamMass : rest.Mass.Value;
        lastTidal = string.Create(inv,
            $"{(d.Full ? "full" : "partial")} {s.Id} by {p.Id}: {d.Limit} limit={d.LimitKm:G6} km rp={d.PeriapsisKm:G6} km d={d.DistanceKm:G6} km beta={d.Beta:G4} stripped={d.StrippedFraction:G4} stream={d.StreamMass:G6} kg core={coreText} n={queued} dust={dustKg:G4} kg dE={d.SpreadEnergy:G5} km2/s2 eps_cm={d.CentreEnergy:G5} km2/s2 bound={d.BoundMass / d.StreamMass:F4} (linear {d.LinearBoundMass / d.StreamMass:F4}) t_min={d.ReturnTime / Constants.Day:G5} d n_fallback={d.FallbackExponent:G4} residual X={d.Residuals.Position:G3} P={d.Residuals.Momentum:G3} L={d.Residuals.Angular:G3} | book err mass={err.Mass:G3} p={err.Momentum:G3} L={err.Angular:G3}");
        GD.Print("[tidal] " + lastTidal);
    }

    /// <summary>
    /// A flash (ImpactEffects, flash only) on the core facing the primary for a partial disruption, or on the primary facing
    /// where the body was for a full one (the body's view goes with it); a star primary flares too.
    /// </summary>
    void TidalFlash(Body p, Body s, TidalDisruption d, Vec3 where)
    {
        EnsureImpacts();
        var full = d.Full;
        var on = full ? p : s;
        var dir = Vec3.Normalize(full ? where - p.Pos : p.Pos - s.Pos);
        if (views.TryGetValue(on.Id, out var view))
        {
            var o = new ImpactOutcome
            {
                Kind = OutcomeKind.Absorb, Target = on, Impactor = full ? s : p, ContactWorld = on.Pos + dir * on.Radius, NormalWorld = dir,
                DirBody = Render.Orientation.WorldToBodyFrame(on, dir, Universe.Jd), VelDirWorld = Vec3.Normalize(s.Vel - p.Vel),
                RelSpeed = Vec3.Length(s.Vel - p.Vel), Angle = 0, Energy = d.StreamMass * d.SpreadEnergy * 1e6, TargetDeltaV = 0,
                Fragments = [], Time = Universe.Time,
            };
            effects!.Spawn(o, view, null, ctx);
        }
        if (views.TryGetValue(p.Id, out var pv) && pv is SunView sun) sun.Flare = 1.5;
        var inv = CultureInfo.InvariantCulture;
        var bound = d.StreamMass > 0 ? d.BoundMass / d.StreamMass : 0;
        var notice = full
            ? string.Create(inv, $"Tidal disruption: {s.Name} was torn apart by {p.Name} (β {d.Beta:G3}); {bound:P0} of the stream is bound and will fall back")
            : string.Create(inv, $"Tidal disruption: {s.Name} was partly torn apart by {p.Name} (β {d.Beta:G3}): {d.StrippedFraction:P0} stripped, {bound:P0} of that bound");
        Hint(notice, 7);
        GD.Print("[tidal] notice " + notice);
        FrameStream(p, d);
    }

    /// <summary>
    /// The cinematic camera takes in the stream: over the primary, looking down on the orbit from far enough that the debris,
    /// already a few times the disruption distance long, fits the frame. Off with the cinematic setting.
    /// </summary>
    void FrameStream(Body primary, TidalDisruption d)
    {
        if (!Settings.Cinematic) return;
        Settings.Diorama = false;
        var dist = Math.Max(Rig.Distance, 6 * d.DistanceKm);
        Rig.FlyTo(new ViewSpec { Focus = primary, SetFocus = true, Distance = dist, Pitch = 1.15 }, 1.5);
        GD.Print(string.Create(CultureInfo.InvariantCulture, $"[tidal] cinematic frames the stream about {primary.Id} from {dist:G4} km"));
    }

    // ---------------------------------------------------------------- capture verbs

    bool TidalAction(string verb, string arg)
    {
        switch (verb)
        {
            case "tidal":
                SetTidal(arg is "1" or "true" or "on" or "");
                return true;
            case "tidalDemo":
                TidalDemo(arg);
                return true;
            case "tdeDemo":
                TdeDemo(arg);
                return true;
            case "logTidal":
                LogTidal();
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// <c>tidalDemo:bodyId[,apoapsisKm]</c>: put the body (default the Moon) at apoapsis, default 6 limits out, on the orbit
    /// about its primary whose periapsis lies halfway between contact and its limit, and look down on it. The change of
    /// state is booked in the debris ledger as external.
    /// </summary>
    void TidalDemo(string arg)
    {
        var p = arg.Split(',', StringSplitOptions.TrimEntries);
        var id = p.Length > 0 && p[0].Length > 0 ? p[0] : "moon";
        var s = Universe.ById(id);
        var primary = s != null ? PrimaryOf(s) : null;
        if (s == null || primary == null)
        {
            GD.PushError($"[tidal] tidalDemo: no body '{id}' or no primary for it");
            return;
        }
        var (kind, limit) = Tidal.LimitFor(primary, s);
        var contact = primary.Radius + s.Radius;
        if (!(limit > contact))
        {
            GD.PushError(string.Create(CultureInfo.InvariantCulture, $"[tidal] tidalDemo: {s.Id}'s {kind} limit {limit:G5} km lies inside contact {contact:G5} km: it would hit {primary.Id} first"));
            return;
        }
        var rp = contact + 0.5 * (limit - contact);
        var apo = p.Length > 1 ? double.Parse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture) : 6 * limit;
        var mu = Constants.G * (primary.Mass + s.Mass);
        var rel = s.Pos - primary.Pos;
        var rhat = Vec3.Normalize(rel);
        var h = Vec3.Cross(rel, s.Vel - primary.Vel);
        var normal = Vec3.Length(h) > 0 ? Vec3.Normalize(h) : new Vec3(0, 0, 1);
        var along = Vec3.Normalize(Vec3.Cross(normal, rhat));
        var va = Math.Sqrt(2 * mu * rp / (apo * (apo + rp)));
        RecordUndo(UndoKind.Demo, $"sending {s.Name} towards {primary.Name}", s.Id);
        EnsureDebris();
        var before = Tally.Of(s.Mass, s.Pos, s.Vel, ledger!.Origin);
        s.Pos = primary.Pos + rhat * apo;
        s.Vel = primary.Vel + along * va;
        ledger.AddExternal(Tally.Of(s.Mass, s.Pos, s.Vel, ledger.Origin) - before);
        var a = 0.5 * (apo + rp);
        var fall = Math.PI * Math.Sqrt(a * a * a / mu);
        GD.Print(string.Create(CultureInfo.InvariantCulture,
            $"[tidal] demo {s.Id} about {primary.Id}: {kind} limit={limit:G6} km contact={contact:G6} km apo={apo:G6} km rp={rp:G6} km beta={limit / rp:G4} stripped={Tidal.StrippedFraction(limit / rp):G4} periapsis in {fall:G5} s ({fall / Constants.Hour:G4} h)"));
        Settings.Diorama = false;
        Select(s);
        Rig.FlyTo(new ViewSpec { Focus = primary, SetFocus = true, Distance = 1.6 * apo, Pitch = 1.2 }, 1.2);
        Hint(string.Create(CultureInfo.InvariantCulture, $"{s.Name} is falling towards {primary.Name}: periapsis {rp:N0} km, inside its {limit:N0} km Roche limit"), 5);
    }

    /// <summary>
    /// <c>tdeDemo:massSolar[,beta]</c>: a new black hole of that mass <see cref="TdeDemoDistanceAu"/> from the Sun, and a new
    /// Sun-like star falling in on a zero-energy orbit (ε = 0 in the PW potential, the classic TDE: half the debris bound) with periapsis r_t/β (β default 2,
    /// a full disruption), starting 6 r_t out. Above the Hills mass r_t is inside the horizon and the star is swallowed whole.
    /// </summary>
    void TdeDemo(string arg)
    {
        var p = arg.Split(',', StringSplitOptions.TrimEntries);
        var massSolar = p.Length > 0 && p[0].Length > 0 ? double.Parse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture) : 1e6;
        var beta = p.Length > 1 ? double.Parse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture) : 2;
        var sun = Star();
        RecordUndo(UndoKind.Demo, "the tidal-disruption demo");
        var hole = SpawnBlackHole(massSolar, sun.Id, new Vec3(TdeDemoDistanceAu * Constants.AU, 0, 0));
        if (hole == null) return;
        var rt = BlackHole.TidalRadiusKm(hole.Mass, Constants.SunMass, Constants.SunRadius);
        var rs = hole.Radius;
        var contact = rs + Constants.SunRadius;
        var swallowed = !(rt > contact);
        // Swallowed whole: aim inside the horizon so Collide absorbs it; otherwise keep the periapsis clear of contact.
        var rp = swallowed ? 0.5 * rs : Math.Max(rt / beta, 1.05 * contact);
        var scale = Math.Max(rt, 2 * contact);
        var d0 = 6 * scale;
        var mu = Constants.G * (hole.Mass + Constants.SunMass);
        var semiLatus = 2 * rp;
        var nu = -Math.Acos(Math.Clamp(semiLatus / d0 - 1, -1, 1));
        var e1 = Vec3.Normalize(hole.Pos - sun.Pos);
        var e2 = Vec3.Normalize(Vec3.Cross(new Vec3(0, 0, 1), e1));
        var (sn, cs) = Math.SinCos(nu);
        var rel = (e1 * cs + e2 * sn) * d0;
        // Zero energy in the Paczyński–Wiita potential the pair feels (a Newtonian parabola would be bound by μ r_s/d²):
        // v² = 2μ/(d − r_s), with the angular momentum of that orbit's periapsis at rp.
        var holeRs = PseudoNewtonian.HoleRs(hole);
        var rhat = rel * (1 / d0);
        var that = Vec3.Normalize(Vec3.Cross(new Vec3(0, 0, 1), rhat));
        var vt = rp * Math.Sqrt(2 * mu / (rp - holeRs)) / d0;
        var vr = -Math.Sqrt(Math.Max(0, 2 * mu / (d0 - holeRs) - vt * vt));
        var vrel = rhat * vr + that * vt;
        var dd = Math.Tan(nu / 2);
        var toPeri = 0.5 * Math.Sqrt(semiLatus * semiLatus * semiLatus / mu) * Math.Abs(dd + dd * dd * dd / 3);
        string id;
        do id = $"star-{++tidalStars}";
        while (Universe.ById(id) != null);
        var star = Body.MakeBody(new BodySpec
        {
            Id = id, Name = $"Star {tidalStars}", Kind = BodyKind.Star, Surface = Surface.Star, Mass = Constants.SunMass, Radius = Constants.SunRadius,
            Pos = hole.Pos + rel, Vel = hole.Vel + vrel, Rotation = sun.Rotation, Visual = sun.Visual, Seed = 1000 + tidalStars, Born = Universe.Time,
        });
        Universe.Add(star);
        SyncViews();
        GD.Print(string.Create(CultureInfo.InvariantCulture,
            $"[tidal] tde demo: {hole.Id} {massSolar:G4} Msun rs={rs:G5} km r_t={rt:G5} km ({rt / rs:G4} rs, {rt / Constants.AU:G4} AU) hills={Tidal.HillsMassKg(Constants.SunMass, Constants.SunRadius) / Constants.SunMass:G4} Msun swallowed_whole={swallowed} {id} from {d0:G5} km rp={rp:G5} km beta={rt / rp:G4} periapsis in {toPeri:G5} s ({toPeri / Constants.Hour:G4} h)"));
        Settings.Diorama = false;
        Select(hole);
        Rig.FlyTo(new ViewSpec { Focus = hole, SetFocus = true, Distance = 10 * scale, Pitch = 1.35 }, 1.2);
        Hint(swallowed
            ? string.Create(CultureInfo.InvariantCulture, $"A {massSolar:G3} solar-mass hole swallows a Sun-like star whole: its tidal radius lies inside the horizon")
            : string.Create(CultureInfo.InvariantCulture, $"A Sun-like star falls towards a {massSolar:G3} solar-mass black hole; it will be torn apart at {rt / Constants.AU:G2} AU"), 6);
    }

    /// <summary><c>logTidal</c>: the last disruption, the flare, and the debris ledger's state.</summary>
    void LogTidal()
    {
        EnsureDebris();
        var err = ledger!.Error();
        var flare = "none";
        foreach (var (id, hd) in holeDiscs)
        {
            if (hd.Tde is not { } f) continue;
            flare = string.Create(CultureInfo.InvariantCulture,
                $"{id} t_sim={Universe.Time - f.SimStart:G4} s t_shown={FallbackTime(f):G4} s mdot={hd.FallbackMdot:G4} kg/s L/L_Edd={hd.Luminosity / BlackHole.EddingtonLuminosity(hd.Hole.Mass):G4} brightness={hd.Brightness:G4}");
        }
        GD.Print(string.Create(CultureInfo.InvariantCulture,
            $"[tidal] on={Settings.Tidal} last: {lastTidal} | flare: {flare} | particles={debris?.Occupied ?? 0} T2={ledger.Particles.Mass.Value:G6} kg dust={ledger.Dust.Mass.Value:G6} kg book err mass={err.Mass:G3} p={err.Momentum:G3} L={err.Angular:G3}"));
        LogDebris(true);
    }
}
