using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Godot;
using Spayly.Game.Render;
using Spayly.Sim;
using Spayly.Sim.Debris;

namespace Spayly.Game.App;

/// <summary>
/// The tiered debris system: crater ejecta (Housen &amp; Holsapple 2011) and LS12 particle-tier mass become T2 GPU test
/// particles (<see cref="DebrisTier"/>); absorbed particles merge into their body, expired or escaped ones become T3 dust
/// that re-accretes onto the body that dominates where they went; <see cref="DebrisLedger"/> books every hand-off exactly.
///
/// The ledger is audited against the sim every frame: bodies that appear or vanish outside a collision (a fired asteroid,
/// erase, moon detail) are external; mass changes of the bodies a collision touched are Collide's, and what they fail to
/// explain beyond the LS12 particle tier is Collide's rounding (logged as the collide residual). An undo puts the particles
/// and the book back from its point, rebound to the restored bodies (App.Undo.cs); a fault rollback, or an undo point that
/// lost its particles, replaces every Body with a clone the audit does not know, which clears the particles and starts a
/// new book.
///
/// User args: --debris-capacity N, --debris-contacts, --debris-stress N (N particles in orbit round the focus, for timing),
/// --debris-lifetime-days D, --debris-off. Capture verbs: setOutcomeModel:ls12|legacy, logDebris.
/// </summary>
public partial class App
{
    /// <summary>Most particles one impact spawns.</summary>
    const int DebrisPerImpact = 50_000;
    /// <summary>Crater ejecta never exceed this fraction of the target: past it the crater law has long stopped applying.</summary>
    const double EjectaMaxFraction = 0.01;
    /// <summary>Re-accretion time constant of a dust reservoir, sim seconds.</summary>
    const double DustTau = 300;
    const int DebrisLogEvery = 60;

    DebrisTier? debris;
    DebrisLedger? ledger;
    bool debrisInit;
    int debrisStressTarget;
    long debrisStressQueued;
    Func<double>? stressRand;
    long debrisFrames;
    long debrisAlloc;
    double collideResidualKg;
    double lsInjectedFrame;
    Tally freshExternal;

    sealed class KnownBody
    {
        public double Mass;
        public Vec3 Pos;
        public Vec3 Vel;
        public bool Seen;
    }

    readonly Dictionary<Body, KnownBody> known = [];
    readonly Dictionary<string, Body> knownIds = [];
    readonly HashSet<Body> involved = [], newFragments = [];
    readonly Dictionary<Body, double> ledgerDm = [];
    readonly List<Body> goneBodies = [], reservoirHomes = [];

    void EnsureDebris()
    {
        if (debrisInit) return;
        debrisInit = true;
        var capacity = DebrisTier.DefaultCapacity;
        var contacts = false;
        var off = false;
        var lifetimeDays = 30.0;
        var args = OS.GetCmdlineUserArgs();
        for (var i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : "";
            switch (args[i])
            {
                case "--debris-capacity": capacity = int.Parse(Next().Replace("_", ""), CultureInfo.InvariantCulture); break;
                case "--debris-stress": debrisStressTarget = int.Parse(Next().Replace("_", ""), CultureInfo.InvariantCulture); break;
                case "--debris-contacts": contacts = true; break;
                case "--debris-lifetime-days": lifetimeDays = double.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--debris-off": off = true; break;
            }
        }
        var (bary, _, _) = Universe.Barycentre();
        ledger = new DebrisLedger(bary);
        ledger.Baseline(Universe.Bodies, bary);
        RememberBodies();
        GD.Print(string.Create(CultureInfo.InvariantCulture,
            $"[debris] ledger baseline: {Universe.Bodies.Count} bodies, {ledger.Initial.Mass.Value:G9} kg, L about the barycentre ({bary.X:G6}, {bary.Y:G6}, {bary.Z:G6}) km"));
        if (off) return;
        debrisStressTarget = Math.Min(debrisStressTarget, capacity);
        debris = new DebrisTier(Math.Max(capacity, debrisStressTarget), contacts)
        {
            Lifetime = debrisStressTarget > 0 ? double.PositiveInfinity : lifetimeDays * Constants.Day,
            BoundRadius = debrisStressTarget > 0 ? double.PositiveInfinity : 2e6,
            OnEvent = OnDebrisEvent,
        };
        AddChild(debris);
    }

    void RememberBodies()
    {
        known.Clear();
        knownIds.Clear();
        foreach (var b in Universe.Bodies)
        {
            known[b] = new KnownBody { Mass = b.Mass, Pos = b.Pos, Vel = b.Vel };
            knownIds[b.Id] = b;
        }
    }

    void RebaselineDebris(string reason)
    {
        debris?.Reset();
        var (bary, _, _) = Universe.Barycentre();
        ledger!.Baseline(Universe.Bodies, bary);
        RememberBodies();
        involved.Clear();
        newFragments.Clear();
        ledgerDm.Clear();
        lsInjectedFrame = 0;
        freshExternal = Tally.Zero;
        collideResidualKg = 0;
        GD.Print($"[debris] rebaseline ({reason}): particles cleared, new book over {Universe.Bodies.Count} bodies");
    }

    /// <summary>Every Body replaced by a clone with the same id: an undo or a fault rollback happened since the last audit.</summary>
    bool RestoreHappened()
    {
        foreach (var b in Universe.Bodies)
            if (!known.ContainsKey(b) && knownIds.TryGetValue(b.Id, out var twin) && !ReferenceEquals(twin, b)) return true;
        return false;
    }

    void NoteLedger(Body b, double dm)
    {
        if (dm != 0) ledgerDm[b] = ledgerDm.GetValueOrDefault(b) + dm;
    }

    // ---------------------------------------------------------------- hook: HandleOutcomes

    /// <summary>Called from HandleOutcomes: book what each collision did and turn its debris into particles.</summary>
    void DebrisOnOutcomes(List<ImpactOutcome> outcomes)
    {
        EnsureDebris();
        if (ledger == null) return;
        if (RestoreHappened()) RebaselineDebris("bodies replaced before an impact");
        foreach (var o in outcomes)
        {
            involved.Add(o.Target);
            involved.Add(o.Impactor);
            foreach (var f in o.Fragments) newFragments.Add(f);
            // An asteroid fired and consumed within one frame was never seen by the audit: it arrived from outside.
            if (!known.ContainsKey(o.Impactor) && !newFragments.Contains(o.Impactor) && Universe.ById(o.Impactor.Id) != o.Impactor)
                freshExternal += Tally.Of(o.Impactor.Mass, o.Impactor.Pos, o.Impactor.Vel, ledger.Origin);
            // A black-hole merger's gravitational waves leave the system: an external loss of exactly the pair's tally minus
            // the remnant's, so the remnant's lighter mass and its recoil are neither Collide residual nor a book error.
            if (o.Merger is { } merger) freshExternal -= merger.Radiated(ledger.Origin);
            if (o.ParticleTierMass > 0) InjectParticleTier(o);
            else if (o.Kind is OutcomeKind.Crater or OutcomeKind.Merge && o.Report?.Crater is CraterResult crater) InjectCraterEjecta(o, crater);
        }
    }

    /// <summary>
    /// Crater ejecta. The report carries no ejecta mass, so it is Collins et al.'s transient crater volume π D_tc³/(16√2)
    /// times the surface density Collide used; launched in the impact frame (surface normal from the body-frame impact
    /// direction at the current Jd, plus the surface's spin velocity) and taken out of the target, recoil included.
    /// </summary>
    void InjectCraterEjecta(ImpactOutcome o, CraterResult crater)
    {
        var tier = debris;
        var target = o.Target;
        if (tier is not { IsReady: true } || ledger == null || target.Kind == BodyKind.Star || target.Surface == Surface.Gas) return;
        var n = Math.Min(DebrisPerImpact, tier.CanQueue);
        if (n < 64)
        {
            GD.Print($"[debris] no room for {o.Target.Id} ejecta ({tier.CanQueue} slots free): the crater keeps its mass");
            return;
        }
        var surf = Sim.SurfaceAt?.Invoke(target, o.DirBody);
        var water = surf is { Water: true };
        var rhoT = water ? 1000.0 : target.Surface == Surface.Ice ? 900.0 : 2500.0;
        var rhoP = Body.Density(o.Impactor);
        var mass = Math.Min(EjectaSampler.EjectaMassFromCrater(crater.TransientDiameter, rhoT), EjectaMaxFraction * target.Mass);
        if (!(mass > 0) || !double.IsFinite(mass)) return;
        var scaling = target.Kind is BodyKind.Asteroid or BodyKind.Fragment ? EjectaScaling.Porous : EjectaScaling.Rock;
        var speed = o.Report is ImpactReport rep && rep.Entry.SurfaceSpeed > 0 ? rep.Entry.SurfaceSpeed / 1000 : o.RelSpeed;
        var rand = Rng.Mulberry32(unchecked((uint)(target.Seed ^ (int)(long)Math.Floor(o.Time)) ^ 0x9E3779B9u));
        var s = EjectaSampler.Sample(crater.TransientDiameter / 2000, o.Impactor.Radius, speed, rhoT, rhoP, scaling, mass, n, rand);

        var jd = Universe.Jd;
        var nrm = Render.Orientation.BodyFrameToWorld(target, o.DirBody, jd);
        var surfaceVel = (Render.Orientation.BodyFrameToWorld(target, o.DirBody, jd + 1.0 / Constants.Day) - nrm) * target.Radius;
        var t1 = Vec3.AnyPerpendicular(nrm);
        var t2 = Vec3.Cross(nrm, t1);
        var contact = target.Pos + nrm * target.Radius;
        // Start clear of the surface by more than the float32 rounding of a position relative to the debris origin.
        var originPos = (tier.Origin ?? Rig.Focus ?? target).Pos;
        var lift = Math.Max(Math.Max(0.01, 1e-5 * target.Radius), 1e-6 * Vec3.Length(contact - originPos));
        var radius = Body.RadiusForDensity(s.Mass[0], rhoT);
        var flow = Tally.Zero;
        var queued = 0;
        for (var i = 0; i < s.Count; i++)
        {
            var p = s.Position[i];
            var w = s.Velocity[i];
            var r = contact + t1 * p.X + t2 * p.Y + nrm * (p.Z + lift);
            var v = target.Vel + surfaceVel + t1 * w.X + t2 * w.Y + nrm * w.Z;
            if (!tier.Queue(r, v, s.Mass[i], radius)) break;
            flow += Tally.Of(s.Mass[i], r, v, ledger.Origin);
            queued++;
        }
        var m0 = target.Mass;
        var vBefore = target.Vel;
        ledger.InjectFromBody(target, flow);
        NoteLedger(target, target.Mass - m0);
        var vFast = Vec3.Length(s.Velocity[0]);
        var vSlow = Vec3.Length(s.Velocity[s.Count - 1]);
        var vEsc = Body.EscapeVelocity(target);
        double above = 0;
        for (var i = 0; i < queued; i++) if (Vec3.Length(s.Velocity[i]) > vEsc) above += s.Mass[i];
        GD.Print(string.Create(CultureInfo.InvariantCulture,
            $"[debris] crater ejecta {o.Impactor.Id} -> {target.Id}: D_tc={crater.TransientDiameter:G4} m mass={mass:G6} kg (Collins V_tc x {rhoT:G4} kg/m3, {mass / o.Impactor.Mass:G3} x impactor) n={queued} mu={scaling.Mu} U={speed:G4} km/s v={vSlow:G3}..{vFast:G3} km/s escaping={above / mass:P2} (v_esc {vEsc:G3}) radius={radius * 1000:G3} m lift={lift * 1000:G3} m recoil_dv={Vec3.Length(target.Vel - vBefore):G3} km/s launch_x=[{s.Inner:G3},{s.Outer:G3}] km"));
    }

    /// <summary>
    /// LS12's particle tier: ParticleTierMass (already gone from the bodies) as pairs of equal particles launched ± along
    /// isotropic directions from a shell at 1.3 radii round the remnant (the projectile after a hit-and-run), so the cloud's
    /// centre-of-mass velocity is ParticleTierVelocity to rounding. Speeds carry the dispersal energy: per unit mass
    /// v_esc(shell)²/2 + max(0, Q_R − Q_bind), Q_bind = 3GM_tot/(5R_tot), shared out with a ±40 % spread and renormalised.
    /// </summary>
    void InjectParticleTier(ImpactOutcome o)
    {
        var tier = debris;
        var ptm = o.ParticleTierMass;
        var vpt = o.ParticleTierVelocity;
        var center = o.Kind == OutcomeKind.HitAndRun ? o.Impactor : o.Target;
        lsInjectedFrame += ptm;
        var n = tier is { IsReady: true } ? Math.Min(DebrisPerImpact, tier.CanQueue) & ~1 : 0;
        if (n < 2 || !double.IsFinite(ptm) || o.Ls12 is not CollisionRegimeResult ls)
        {
            ledger!.DustDetached(center, Tally.Of(ptm, center.Pos, vpt, ledger.Origin));
            GD.Print(string.Create(CultureInfo.InvariantCulture, $"[debris] ls12 particle tier {ptm:G6} kg straight to {center.Id}'s dust reservoir (no particle slots)"));
            return;
        }
        var masses = EjectaSampler.SplitMassExactly(ptm, n);
        var mTot = ls.TargetMass + ls.ProjectileMass;
        var rho = Body.Density(center);
        var qBind = 0.6 * Constants.G * mTot / Body.RadiusForDensity(mTot, rho);
        var dq = Math.Max(0, ls.QR / 1e6 - qBind);
        var shell = 1.3 * center.Radius;
        var vEsc2 = 2 * Constants.G * center.Mass / shell;
        var rand = Rng.Mulberry32(unchecked((uint)(center.Seed ^ (int)(long)Math.Floor(o.Time)) ^ 0x85EBCA6Bu));
        var pairs = n / 2;
        var dirs = new Vec3[pairs];
        var speeds = new double[pairs];
        double eRaw = 0, eGoal = 0;
        for (var j = 0; j < pairs; j++)
        {
            dirs[j] = Vec3.RandomUnit(rand);
            speeds[j] = 0.6 + 0.8 * rand();
            var mp = masses[2 * j] + masses[2 * j + 1];
            eRaw += 0.5 * mp * speeds[j] * speeds[j];
            eGoal += mp * (0.5 * vEsc2 + dq);
        }
        var scale = Math.Sqrt(eGoal / eRaw);
        var radius = Body.RadiusForDensity(masses[0], rho);
        var flow = Tally.Zero;
        var unplaced = Tally.Zero;
        for (var j = 0; j < pairs; j++)
        {
            var u = dirs[j] * (speeds[j] * scale);
            for (var side = 0; side < 2; side++)
            {
                var sign = side == 0 ? 1.0 : -1.0;
                var m = masses[2 * j + side];
                var r = center.Pos + dirs[j] * (sign * shell);
                var v = vpt + u * sign;
                // n ≤ CanQueue, so this never fails; if it did, the mass still has to go somewhere booked.
                if (tier!.Queue(r, v, m, radius)) flow += Tally.Of(m, r, v, ledger!.Origin);
                else unplaced += Tally.Of(m, r, v, ledger!.Origin);
            }
        }
        ledger!.InjectDetached(flow);
        if (!unplaced.IsZero) ledger.DustDetached(center, unplaced);
        flow += unplaced;
        var cmErr = (flow.Momentum - DV.Product(ptm, vpt)).Length / Math.Max(ptm * Vec3.Length(vpt), double.Epsilon);
        GD.Print(string.Create(CultureInfo.InvariantCulture,
            $"[debris] ls12 particle tier {o.Kind} {o.Impactor.Id} -> {o.Target.Id}: regime={ls.Regime} M_lr/M_target={ls.LargestRemnantMass / ls.TargetMass:G6} Q_R/Q*_RD={ls.QR / ls.QPrimeStarRD:G4} ParticleTierMass={ptm:G9} kg n={n} around {center.Id} v_disp_rms={Math.Sqrt(2 * eGoal / ptm):G4} km/s (v_esc(shell)={Math.Sqrt(vEsc2):G4}, dQ={dq:G4} km2/s2) cm_velocity_err={cmErr:G3}"));
    }

    // ---------------------------------------------------------------- GPU events (async readback, between frames)

    // Which bodies soak up the particles, for logDebris: a stream that falls straight back onto its core reads very
    // differently from ejecta raining onto a neighbour.
    readonly Dictionary<string, int> absorbedBy = [];

    void OnDebrisEvent(in DebrisEvent e)
    {
        if (ledger == null) return;
        var src = e.Body;
        var r = src.Pos + e.RelPos;
        var v = src.Vel + e.RelVel;
        // The body may have merged away since the frame the particle met it.
        var b = ReferenceEquals(Universe.ById(src.Id), src) ? src : Universe.DominantBody(r) ?? Star();
        if (e.Kind == DebrisEventKind.Absorbed)
        {
            absorbedBy[b.Id] = absorbedBy.GetValueOrDefault(b.Id) + 1;
            var m0 = b.Mass;
            ledger.Absorb(b, e.Mass, r, v);
            NoteLedger(b, b.Mass - m0);
            FeedHole(b, e.Mass, "");
        }
        else ledger.ToDust(b, e.Mass, r, v);
    }

    // ---------------------------------------------------------------- tick step

    /// <summary>Step 13, after the outcomes: audit the book, re-accrete dust, run the GPU tier, log.</summary>
    void UpdateDebris(double dtReal)
    {
        EnsureDebris();
        if (ledger == null) return;
        var a0 = GC.GetAllocatedBytesForCurrentThread();
        AuditBodies();
        if (debrisStressQueued < debrisStressTarget) QueueStress();
        var dtSim = Sim.LastAdvanced;
        if (dtSim != 0) AccreteReservoirs(Math.Abs(dtSim));
        debris?.Step(Universe.Bodies, Rig.Focus ?? Star(), dtSim, ctx);
        debrisAlloc += GC.GetAllocatedBytesForCurrentThread() - a0;
        if (++debrisFrames % DebrisLogEvery == 0) LogDebris(false);
    }

    void AccreteReservoirs(double dt)
    {
        var l = ledger!;
        l.ReservoirHomes(reservoirHomes);
        var f = 1 - Math.Exp(-dt / DustTau);
        foreach (var home in reservoirHomes)
        {
            if (!ReferenceEquals(Universe.ById(home.Id), home))
            {
                l.MoveReservoir(home, Universe.DominantBody(home.Pos) ?? Star());
                continue;
            }
            var m0 = home.Mass;
            l.Accrete(home, f);
            NoteLedger(home, home.Mass - m0);
        }
    }

    /// <summary>Classify every body change since the last audit as the ledger's, Collide's or external, then remember the bodies.</summary>
    void AuditBodies()
    {
        var l = ledger!;
        var bodies = Universe.Bodies;
        var ext = freshExternal;
        var collide = new DD(-freshExternal.Mass.Hi, -freshExternal.Mass.Lo);
        foreach (var k in known.Values) k.Seen = false;
        for (var i = 0; i < bodies.Count; i++)
        {
            var b = bodies[i];
            if (known.TryGetValue(b, out var k))
            {
                k.Seen = true;
                var dm = DD.Sum(b.Mass, -k.Mass) - ledgerDm.GetValueOrDefault(b);
                if (dm.Hi == 0 && dm.Lo == 0) continue;
                if (involved.Contains(b)) collide += dm;
                else ext += Tally.Of(dm.Value, b.Pos, b.Vel, l.Origin);
            }
            else if (knownIds.TryGetValue(b.Id, out var twin) && !ReferenceEquals(twin, b))
            {
                RebaselineDebris($"{b.Id} was replaced (undo or fault rollback)");
                return;
            }
            else if (newFragments.Contains(b)) collide += b.Mass;
            else ext += Tally.Of(b.Mass, b.Pos, b.Vel, l.Origin);
        }
        goneBodies.Clear();
        foreach (var (b, k) in known)
        {
            if (k.Seen) continue;
            goneBodies.Add(b);
            var m = k.Mass + ledgerDm.GetValueOrDefault(b);
            if (involved.Contains(b)) collide -= m;
            else ext -= Tally.Of(m, k.Pos, k.Vel, l.Origin);
        }
        if (!ext.IsZero) l.AddExternal(ext);
        collideResidualKg += (collide + lsInjectedFrame).Value;
        foreach (var b in goneBodies)
        {
            if (l.Reservoirs.ContainsKey(b)) l.MoveReservoir(b, Universe.DominantBody(known[b].Pos) ?? Star());
            known.Remove(b);
            if (knownIds.TryGetValue(b.Id, out var t) && ReferenceEquals(t, b)) knownIds.Remove(b.Id);
        }
        for (var i = 0; i < bodies.Count; i++)
        {
            var b = bodies[i];
            if (!known.TryGetValue(b, out var k))
            {
                known[b] = k = new KnownBody();
                knownIds[b.Id] = b;
            }
            k.Mass = b.Mass;
            k.Pos = b.Pos;
            k.Vel = b.Vel;
        }
        involved.Clear();
        newFragments.Clear();
        ledgerDm.Clear();
        lsInjectedFrame = 0;
        freshExternal = Tally.Zero;
    }

    /// <summary><c>--debris-stress N</c>: N light particles on circular orbits 1.6–3.2 radii round the focus, from outside the book.</summary>
    void QueueStress()
    {
        if (debris is not { IsReady: true } tier || ledger == null) return;
        var center = Rig.Focus ?? Universe.ById("earth") ?? Star();
        stressRand ??= Rng.Mulberry32(20260924);
        var rand = stressRand;
        var n = (int)Math.Min(Math.Min(debrisStressTarget - debrisStressQueued, tier.CanQueue), DebrisTier.StagingCapacity);
        const double m = 1e9;
        var gm = Constants.G * center.Mass;
        var flow = Tally.Zero;
        for (var k = 0; k < n; k++)
        {
            var rr = center.Radius * Math.Sqrt(1.6 * 1.6 + rand() * (3.2 * 3.2 - 1.6 * 1.6));
            var th = 2 * Math.PI * rand();
            var (sn, cs) = Math.SinCos(th);
            var off = new Vec3(rr * cs, rr * sn, (rand() - 0.5) * 0.02 * rr);
            var vc = Math.Sqrt(gm / rr);
            var jitter = new Vec3(rand() - 0.5, rand() - 0.5, rand() - 0.5) * (0.004 * vc);
            var pos = center.Pos + off;
            var vel = center.Vel + new Vec3(-sn * vc, cs * vc, 0) + jitter;
            if (!tier.Queue(pos, vel, m, 0.05)) break;
            flow += Tally.Of(m, pos, vel, ledger.Origin);
            debrisStressQueued++;
        }
        ledger.AddExternalParticles(flow);
        if (debrisStressQueued >= debrisStressTarget)
        {
            GD.Print(string.Create(CultureInfo.InvariantCulture, $"[debris] stress: {debrisStressQueued} particles queued round {center.Id} (target {debrisStressTarget})"));
            debrisStressTarget = (int)debrisStressQueued;
        }
    }

    void LogDebris(bool full)
    {
        var l = ledger!;
        var measured = DebrisLedger.Measure(Universe.Bodies, l.Origin);
        var t2 = debris?.MassTotal ?? l.Particles.Mass;
        var total = measured.Mass + t2 + l.Dust.Mass;
        var expected = l.Expected.Mass;
        var absErr = (total - expected).Value;
        var relErr = Math.Abs(absErr) / Math.Abs(expected.Value);
        var err = l.Error();
        var (pScale, lScale) = l.Scales;
        var pAudit = (measured.Momentum - l.Bodies.Momentum).Length / Math.Max(pScale, double.Epsilon);
        // Not an error: hand-offs away from a body's centre put r×p into its spin, which Body does not carry, so the book's
        // T0 angular momentum exceeds the bodies' orbital one by exactly that spin.
        var spinL = (l.Bodies.Angular - measured.Angular).Length / Math.Max(lScale, double.Epsilon);
        var mirrorGap = (t2 - l.Particles.Mass).Value;
        var t = debris;
        var (cMean, cMax, cInt, cN) = t?.RecentCompute(DebrisLogEvery) ?? (0, 0, 0, 0);
        GD.Print(string.Create(CultureInfo.InvariantCulture,
            $"[debris] frame={debrisFrames} particles={t?.Occupied ?? 0} hwm={t?.HighWater ?? 0} queued={t?.Queued ?? 0} absorbed={t?.AbsorbedCount ?? 0} dust={t?.DustCount ?? 0} busy={t?.BusyFrames ?? 0} stale={t?.StaleEvents ?? 0} | T0={measured.Mass.Value:G17} T2={t2.Value:G9} dust={l.Dust.Mass.Value:G6} total={total.Value:G17} initial+injected={expected.Value:G17} kg rel_err={relErr:G3} abs_err={absErr:G3} kg | book mass={err.Mass:G3} p={err.Momentum:G3} L={err.Angular:G3} mirror-book={mirrorGap:G3} kg | T0 audit p={pAudit:G3} spin_L={spinL:G3} collide_residual={collideResidualKg:G3} kg injected={l.MassInjected:G6} kg | compute_ms mean={cMean:F3} max={cMax:F3} integrate={cInt:F3} n={cN} | alloc={debrisAlloc} B gc0={GC.CollectionCount(0)}"));
        debrisAlloc = 0;
        if (!full || t == null) return;
        var recount = t.RecountMass();
        GD.Print(string.Create(CultureInfo.InvariantCulture,
            $"[debris] recount: mirror Σ={recount.Value:G17} kg incremental={t.MassTotal.Value:G17} kg diff={(recount - t.MassTotal).Value:G3} kg; injections={l.Injections} absorptions={l.Absorptions} dustings={l.Dustings} accretions={l.Accretions} reservoirs={l.Reservoirs.Count} absorbed_by=[{string.Join(' ', absorbedBy.Select(kv => $"{kv.Key}:{kv.Value}"))}]; {t.ComputePercentiles()}; rd {t.MemMb()}"));
    }

    // ---------------------------------------------------------------- capture verbs

    /// <summary>Collide's options are SimRunner's and immutable, so switching the model swaps in an equivalent handler.</summary>
    void SetOutcomeModel(OutcomeModel model)
    {
        if (Universe.CollisionHandlers.Count == 0) return;
        var opts = new CollideOptions
        {
            BodyFrame = (b, d) => Render.Orientation.WorldToBodyFrame(b, d, Universe.Jd),
            SurfaceAt = (b, d) => Sim.SurfaceAt?.Invoke(b, d) ?? (false, 0),
            OutcomeModel = model,
        };
        Universe.CollisionHandlers[0] = (ev, u) => Sim.Outcomes.Add(Collide.ResolveCollision(u, ev, opts));
        GD.Print($"[debris] outcome model = {model}");
    }

    bool DebrisAction(string verb, string arg)
    {
        switch (verb)
        {
            case "setOutcomeModel":
                if (string.Equals(arg, "ls12", StringComparison.OrdinalIgnoreCase)) SetOutcomeModel(OutcomeModel.LS12);
                else if (string.Equals(arg, "legacy", StringComparison.OrdinalIgnoreCase)) SetOutcomeModel(OutcomeModel.Legacy);
                else GD.PushError($"[debris] setOutcomeModel: '{arg}' is not ls12 or legacy");
                return true;
            case "logDebris":
                EnsureDebris();
                if (ledger != null) LogDebris(true);
                return true;
            // Hide the particle draw (the physics runs on): a shot with and without isolates the cloud's pixels.
            case "debrisVisible":
                if (debris != null) debris.Hidden = !(arg is "1" or "true" or "on" or "");
                return true;
            default:
                return false;
        }
    }
}
