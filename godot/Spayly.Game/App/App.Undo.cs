using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using Godot;
using Spayly.Game.Render;
using Spayly.Game.Sim;
using Spayly.Sim;
using Spayly.Sim.Debris;

namespace Spayly.Game.App;

/// <summary>
/// Undo and redo for every action (M7.1–M7.3). Ctrl+Z, the HUD's Undo buttons and the <c>undo</c> verb all call
/// <see cref="Undo"/>; Ctrl+Shift+Z, Ctrl+Y and <c>redo</c> call <see cref="Redo"/>.
///
/// A point is the universe's body snapshot plus <see cref="AppUndoState"/>, attached through
/// <see cref="Universe.AttachState"/> so automatic points (collisions, tidal disruptions, SPH hits) carry it too: the debris
/// tier (a VRAM-side copy of its buffers) and its ledger, each hole's disc feed and fallback flare, the SPH reservoir, a
/// hero run's pre-collision pair, moon detail, and the selected and focused ids. Restoring puts all of it back, so a tidal
/// stream or an ejecta cloud disappears or returns with its event. View settings (camera, warp, pause, toggles, the next
/// launch's panel) are not part of a point: undo changes the universe, not how it is looked at.
///
/// Every player action records one forced point first, labelled for the "Undone: …" hint (<see cref="RecordUndo"/>).
/// Capture verbs: <c>undo</c>, <c>redo</c>, <c>logFullState[:tag]</c>; the history panel's are in UI/Hud.History.cs.
/// </summary>
public partial class App
{
    /// <summary>What an undo point keeps of the app beside the bodies. Body references are the ones live when it was taken.</summary>
    sealed class AppUndoState
    {
        public DebrisTier.UndoCopy? Debris;
        /// <summary>The particles cannot come back: the copy was shed for VRAM, or could not be made.</summary>
        public bool DebrisLost;
        public DebrisLedger? Ledger;
        public Dictionary<Body, KnownBody>? Known;
        public readonly List<Body> Involved = [], NewFragments = [];
        public readonly Dictionary<Body, double> LedgerDm = [];
        public double LsInjectedFrame, CollideResidualKg;
        public Tally FreshExternal;
        public Dictionary<string, int> AbsorbedBy = [];
        public readonly List<DiscCopy> Discs = [];
        public readonly Dictionary<string, double> Flares = [];
        public string LastTidal = "none";
        public (double Mass, Vec3 Momentum, Vec3 AngularMomentum, int Events) SphReservoir;
        public SphPairCopy? Sph;
        public MoonDetail Moons;
        public string? SelectedId, FocusId;
    }

    sealed record DiscCopy(string HoleId, AccretionFeed Feed, TidalDisruption? TdeEvent, double TdeSimStart, double TdeArtReal, double TdeAbsorbed,
        double FeedMdot, double FallbackMdot, double Luminosity, double Brightness, double PeakK);

    /// <summary>A hero run in flight when the point was taken: its pair before contact, and the proxy standing in for them.</summary>
    sealed record SphPairCopy(Body Target, Body Impactor, Vec3 CmPosition, Vec3 CmVelocity, string ProxyId);

    /// <summary>RAM an undo point's app state takes besides the debris mirror, bytes per entry: a rough scale for the budget.</summary>
    const long UndoEntryBytes = 160;

    bool undoWired;

    /// <summary>
    /// [GAME] What undo does after an impact; the HUD calls this when it builds the Realism panel. The history's depth and
    /// memory budget, also [GAME], are the built-in "Undo" entry (Realism.cs).
    /// </summary>
    public static void RegisterUndoRealism()
    {
        Realism.Register("Undoing an impact", RealismTag.Game,
            "Undo goes back to just before an impact or disruption; if time then runs on it happens again, as physics says it must, but records no new point, so the next Ctrl+Z goes back past it to the launch that caused it.",
            RealismArea.UI);
    }

    void EnsureUndo()
    {
        if (undoWired) return;
        undoWired = true;
        var h = Universe.History;
        Universe.AttachState = AttachUndoState;
        h.Dropped = ReleaseUndoState;
        h.ShedVram = ShedUndoVram;
    }

    /// <summary>
    /// Record a forced undo point before a player action, labelled for the hint ("placing Black hole 2", "Earth's mass
    /// change"). A new point clears the redo list.
    /// </summary>
    public void RecordUndo(UndoKind kind, string label, string? subject = null)
    {
        FinishJump();
        FlushShower();
        EnsureUndo();
        Universe.PushHistory(label, force: true, kind: kind, subject: subject);
    }

    // ---------------------------------------------------------------- taking a point

    /// <summary>Universe.AttachState: the app's half of a point. Main thread only (every PushHistory caller is on it).</summary>
    void AttachUndoState(UniverseSnapshot s)
    {
        var t0 = Stopwatch.GetTimestamp();
        EnsureDebris();
        var a = new AppUndoState
        {
            Moons = Settings.Moons, SelectedId = Selected?.Id, FocusId = Rig.Focus?.Id, SphReservoir = SphReservoir, LastTidal = lastTidal,
            AbsorbedBy = new Dictionary<string, int>(absorbedBy),
        };
        long entries = 16 + absorbedBy.Count;
        if (ledger != null)
        {
            a.Ledger = ledger.Clone();
            a.Known = new Dictionary<Body, KnownBody>(known.Count);
            foreach (var (b, k) in known) a.Known[b] = new KnownBody { Mass = k.Mass, Pos = k.Pos, Vel = k.Vel, Seen = k.Seen };
            a.Involved.AddRange(involved);
            a.NewFragments.AddRange(newFragments);
            foreach (var (b, dm) in ledgerDm) a.LedgerDm[b] = dm;
            a.LsInjectedFrame = lsInjectedFrame;
            a.CollideResidualKg = collideResidualKg;
            a.FreshExternal = freshExternal;
            entries += known.Count + ledger.Reservoirs.Count + involved.Count + newFragments.Count + ledgerDm.Count;
        }
        if (debris != null)
        {
            try
            {
                a.Debris = debris.SaveCopy();
            }
            catch (Exception e)
            {
                a.DebrisLost = debris.Occupied > 0;
                GD.PushError($"[undo] debris copy failed, this point will restore the bodies only: {e.Message}");
            }
        }
        foreach (var (id, d) in holeDiscs)
        {
            a.Discs.Add(new DiscCopy(id, d.Feed.Clone(), d.Tde?.Event, d.Tde?.SimStart ?? 0, d.Tde?.ArtReal ?? 0, d.Tde?.Absorbed ?? 0,
                d.FeedMdot, d.FallbackMdot, d.Luminosity, d.Brightness, d.PeakK));
        }
        foreach (var v in views.Values)
            if (v is SunView sv && sv.Flare > 0) a.Flares[v.Body.Id] = sv.Flare;
        if (sph is { } run) a.Sph = new SphPairCopy(run.Hero.Target.Clone(), run.Hero.Impactor.Clone(), run.Hero.CmPosition, run.Hero.CmVelocity, run.Proxy.Id);
        entries += a.Discs.Count + a.Flares.Count;
        s.Attachment = a;
        s.AttachmentBytes = entries * UndoEntryBytes + (a.Debris?.RamBytes ?? 0);
        s.VramBytes = a.Debris?.VramBytes ?? 0;
        var h = Universe.History;
        GD.Print(string.Create(CultureInfo.InvariantCulture,
            $"[undo] take kind={s.Kind} label='{s.Label}' bytes={s.Bytes} vram={s.VramBytes} particles={a.Debris?.Particles ?? 0} ms={Stopwatch.GetElapsedTime(t0).TotalMilliseconds:F3} history={h.Count}/{h.Redo.Count} total_bytes={h.Bytes} total_vram={h.VramBytes} pool={debris?.UndoPoolBytes ?? 0}"));
    }

    /// <summary>UndoHistory.Dropped: a point left the history for good; its VRAM copy goes back to the pool.</summary>
    void ReleaseUndoState(UniverseSnapshot s)
    {
        if (s.Attachment is AppUndoState { Debris: { } copy }) debris?.ReleaseCopy(copy);
        GD.Print(string.Create(CultureInfo.InvariantCulture,
            $"[undo] evict kind={s.Kind} label='{s.Label}' bytes={s.Bytes} vram={s.VramBytes}"));
    }

    /// <summary>UndoHistory.ShedVram: over the VRAM budget, an old point gives up its particles and keeps its bodies.</summary>
    void ShedUndoVram(UniverseSnapshot s)
    {
        if (s.Attachment is not AppUndoState { Debris: { } copy } a) return;
        var vram = s.VramBytes;
        s.AttachmentBytes = Math.Max(0, s.AttachmentBytes - copy.RamBytes);
        a.DebrisLost = copy.Particles > 0;
        debris?.ReleaseCopy(copy);
        s.VramBytes = 0;
        GD.Print(string.Create(CultureInfo.InvariantCulture,
            $"[undo] shed kind={s.Kind} label='{s.Label}' freed_vram={vram} particles={copy.Particles}: this point will restore the bodies without the debris"));
    }

    // ---------------------------------------------------------------- scene reset

    /// <summary>The system as launched, with the app's half attached. Never in the history, so no eviction can take it.</summary>
    UniverseSnapshot? startScene;

    /// <summary>Once, at the end of _Ready: the state <see cref="ResetScene"/> goes back to.</summary>
    void TakeStartScene()
    {
        EnsureUndo();
        startScene = Universe.Snapshot("start", UndoKind.Reset);
        AttachUndoState(startScene);
    }

    /// <summary>
    /// Put the whole system back as it was at launch (bodies, debris, discs, moon detail, clock): the inspector's Reset and
    /// the <c>resetScene</c> verb. An undo point first, so Ctrl+Z brings the wrecked system back. The undo history (50 points)
    /// cannot do this itself: its oldest points are evicted.
    /// </summary>
    public void ResetScene()
    {
        if (startScene == null) return;
        var hadFocus = Rig.Focus != null;
        // As StepHistory: gestures in progress hold bodies that are about to be replaced, and a hero run cannot survive it.
        CancelAim();
        aiming = false;
        CancelHoleDrag();
        eraseStrokeSaved = false;
        CancelSphForUndo();
        RecordUndo(UndoKind.Reset, "resetting the scene");

        // Restore clones the bodies, and the start copy's debris holds no particles, so the snapshot can be restored again.
        Universe.Restore(startScene);
        var a = startScene.Attachment as AppUndoState;
        RestoreUndoState(a);
        RebindAfterRestore();
        RestoreSelection(a, hadFocus);
        ClearDangling(startScene);
        LastImpact = null;
        cinematicUntil = 0;
        OnImpactUndone();
        foreach (var v in views.Values)
            if (v is SunView sv) sv.Flare = a?.Flares.GetValueOrDefault(v.Body.Id) ?? 0;
        Hint("Scene reset to launch (Ctrl+Z brings it back)", 3);
        LogState("after-reset-scene");
    }

    // ---------------------------------------------------------------- undo and redo

    /// <summary>Undo the last action or event: Ctrl+Z, the HUD's Undo buttons, the <c>undo</c> verb.</summary>
    public void Undo()
    {
        FinishJump();
        StepHistory(false, true);
    }

    /// <summary>Redo what the last undo took back: Ctrl+Shift+Z, Ctrl+Y, the <c>redo</c> verb.</summary>
    public void Redo()
    {
        FinishJump();
        StepHistory(true, true);
    }

    /// <summary>The HUD's former entry point; kept for the <c>undoImpact</c> verb. Undo covers impacts and every other action.</summary>
    public void UndoImpact() => Undo();

    /// <summary>Wall time a history jump may spend in one frame, ms: a step restores up to 10⁶ debris particles (~3.5 ms measured).</summary>
    const double JumpBudgetMs = 8;

    bool jumping, jumpRedo, jumpLost;
    int jumpLeft, jumpAsked, jumpDone, jumpFrames;
    string? jumpLabel;
    long jumpStart;

    /// <summary>True while a history jump is still stepping (it spreads over frames).</summary>
    public bool HistoryJumping => jumping;

    /// <summary>
    /// The history panel's jump: undo <paramref name="steps"/> points (redo −<paramref name="steps"/> when negative), one after
    /// another as Ctrl+Z would, with one hint at the end ("Undone 3 actions"). Steps run within <see cref="JumpBudgetMs"/> a
    /// frame, so a long jump over a heavy debris cloud takes a few frames instead of dropping one; any other undo, redo or
    /// action ends it where it got to. Stops early when the list runs out.
    /// </summary>
    public void JumpHistory(int steps)
    {
        if (steps == 0) return;
        FinishJump();
        jumping = true;
        jumpRedo = steps < 0;
        jumpLeft = jumpAsked = Math.Abs(steps);
        jumpDone = jumpFrames = 0;
        jumpLost = false;
        jumpLabel = null;
        jumpStart = Stopwatch.GetTimestamp();
        PumpHistoryJump();
    }

    /// <summary>Once a frame (App.Tick.cs, before physics): the next steps of a jump in flight, then its hint once it is done.</summary>
    void PumpHistoryJump()
    {
        if (!jumping) return;
        var t0 = Stopwatch.GetTimestamp();
        jumpFrames++;
        do
        {
            if (StepHistory(jumpRedo, false) is not { } step)
            {
                jumpLeft = 0;
                break;
            }
            jumpLeft--;
            jumpDone++;
            jumpLabel = step.Label;
            jumpLost |= !step.DebrisBack;
        } while (jumpLeft > 0 && Stopwatch.GetElapsedTime(t0).TotalMilliseconds < JumpBudgetMs);
        if (jumpLeft <= 0) FinishJump();
    }

    /// <summary>End a jump in flight where it got to, with its one hint.</summary>
    void FinishJump()
    {
        if (!jumping) return;
        jumping = false;
        var verb = jumpRedo ? "Redone" : "Undone";
        var hint = jumpDone == 0 ? (jumpRedo ? "Nothing to redo" : "Nothing to undo")
            : jumpDone == 1 ? $"{verb}: {jumpLabel}"
            : $"{verb} {jumpDone} actions";
        if (jumpLost) hint += " (the debris could not be restored)";
        Hint(hint, 3);
        GD.Print(string.Create(CultureInfo.InvariantCulture,
            $"[history] jump {(jumpRedo ? "redo" : "undo")} asked={jumpAsked} done={jumpDone} frames={jumpFrames} ms={Stopwatch.GetElapsedTime(jumpStart).TotalMilliseconds:F3}{(jumpLeft > 0 ? " cut short" : "")} hint='{hint}'"));
        jumpLeft = 0;
    }

    /// <summary>True when this point will restore the bodies without the debris particles (its copy was shed for VRAM, or failed).</summary>
    public static bool UndoPointLostDebris(UniverseSnapshot s) => s.Attachment is AppUndoState { DebrisLost: true };

    readonly record struct HistoryStep(string Label, bool DebrisBack);

    /// <summary>One undo or redo; <paramref name="hint"/> false leaves the hint to a caller stepping several at once. Null when there was nothing to step.</summary>
    HistoryStep? StepHistory(bool redo, bool hint)
    {
        FlushShower();
        EnsureUndo();
        var h = Universe.History;
        if ((redo ? h.NextRedo : h.NextUndo) == null)
        {
            if (hint) Hint(redo ? "Nothing to redo" : "Nothing to undo", 2);
            GD.Print($"[undo] {(redo ? "redo" : "undo")}: nothing to {(redo ? "redo" : "undo")}");
            return null;
        }
        var t0 = Stopwatch.GetTimestamp();
        var li = LastImpact;
        var hadFocus = Rig.Focus != null;
        // Gestures in progress hold bodies that are about to be replaced.
        CancelAim();
        aiming = false;
        CancelHoleDrag();
        eraseStrokeSaved = false;
        CancelSphForUndo();

        var gc0 = GC.CollectionCount(0) + GC.CollectionCount(1) * 1000 + GC.CollectionCount(2) * 1000000;
        var s = redo ? Universe.Redo() : Universe.Undo();
        if (s == null) return null;
        var tPop = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        var a = s.Attachment as AppUndoState;
        var debrisBack = RestoreUndoState(a);
        var tState = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;

        // An impact's own undo drops the player's rock, so it never flies again (M6.1); anything else the snapshot had stays.
        Body? dropped = null;
        if (!redo && s.Kind is UndoKind.Collision or UndoKind.SphCollision && li != null && li.Impactor.Kind == BodyKind.Asteroid &&
            s.Subject == li.Impactor.Id && Universe.ById(li.Impactor.Id) is Body rock)
        {
            Universe.Remove(rock);
            dropped = rock;
        }

        RebindAfterRestore();
        var tRebind = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        RestoreSelection(a, hadFocus);
        var impactGone = ClearDangling(s);
        if (impactGone || s.Kind is UndoKind.Collision or UndoKind.SphCollision or UndoKind.TidalDisruption)
        {
            cinematicUntil = 0;
            OnImpactUndone();
        }
        if (a != null)
            foreach (var (id, flare) in a.Flares)
                if (views.TryGetValue(id, out var v) && v is SunView sv) sv.Flare = flare;

        // The point has left the history; what it held on the GPU goes back to the pool.
        if (a?.Debris is { } copy) debris?.ReleaseCopy(copy);

        var verb = redo ? "Redone" : "Undone";
        var text = $"{verb}: {s.Label}";
        if (dropped != null) text += $"; {dropped.Name} removed";
        if (!debrisBack) text += " (the debris could not be restored)";
        if (hint) Hint(text, 3);
        GD.Print(string.Create(CultureInfo.InvariantCulture,
            $"[undo] restore {(redo ? "redo" : "undo")} kind={s.Kind} label='{s.Label}' debris={(debrisBack ? "restored" : "lost")} particles={debris?.Occupied ?? 0} ms={Stopwatch.GetElapsedTime(t0).TotalMilliseconds:F3} (pop {tPop:F1}, state {tState - tPop:F1}, rebind {tRebind - tState:F1}) gc0/1/2+={GC.CollectionCount(0) + GC.CollectionCount(1) * 1000 + GC.CollectionCount(2) * 1000000 - gc0} history={h.Count}/{h.Redo.Count} total_bytes={h.Bytes} total_vram={h.VramBytes} pool={debris?.UndoPoolBytes ?? 0}"));
        GD.Print($"[undo] {(hint ? "hint" : "step")} {text}");
        LogState(redo ? "after-redo" : "after-undo");
        return new HistoryStep(s.Label, debrisBack);
    }

    /// <summary>
    /// A hero run in flight cannot survive a restore: end it without applying anything and put its pair back where the
    /// proxy has taken them (as <see cref="FallBackToAnalytic"/> does), so the state an undo moves to the redo list holds real
    /// bodies, not a proxy with no run behind it. The cooldown is lifted: a pair restored at contact runs as SPH again.
    /// </summary>
    void CancelSphForUndo()
    {
        if (sph is not { } run) return;
        var h = run.Hero;
        var u = Universe;
        var proxy = run.Proxy;
        EndSph(run);
        sphCooldownUntil = 0;
        if (!ReferenceEquals(u.ById(proxy.Id), proxy)) return;
        var dr = proxy.Pos - h.CmPosition;
        var dv = proxy.Vel - h.CmVelocity;
        u.Remove(proxy);
        var a = h.Target;
        var b = h.Impactor;
        a.Pos += dr; a.Vel += dv;
        b.Pos += dr; b.Vel += dv;
        u.Add(a);
        u.Add(b);
        GD.Print($"[sph] cancelled by undo: {proxy.Id} replaced by {a.Id} + {b.Id}");
    }

    /// <summary>
    /// Put the app's half of a point back, after the bodies. True when the debris particles came back with it (or there were
    /// none); false when they could not, and then the debris book starts afresh over the restored bodies.
    /// </summary>
    bool RestoreUndoState(AppUndoState? a)
    {
        var u = Universe;
        Sim.Outcomes.Clear();
        tidalWindow = 0;
        CancelTidal();
        if (a == null)
        {
            // Taken before the app wired its state in (none in practice): the bodies alone, the book restarted.
            RebaselineDebris("undo point without app state");
            return false;
        }

        // A hero run in flight then: its proxy is back in the bodies, without a run. Its pair replaces it.
        if (a.Sph is { } pair && u.ById(pair.ProxyId) is Body proxy)
        {
            var dr = proxy.Pos - pair.CmPosition;
            var dv = proxy.Vel - pair.CmVelocity;
            u.Remove(proxy);
            foreach (var src in new[] { pair.Target, pair.Impactor })
            {
                var b = src.Clone();
                b.Pos += dr;
                b.Vel += dv;
                u.Add(b);
            }
        }

        Settings.Moons = a.Moons;
        SphReservoir = a.SphReservoir;
        lastTidal = a.LastTidal;
        absorbedBy.Clear();
        foreach (var (id, n) in a.AbsorbedBy) absorbedBy[id] = n;

        // Discs, keyed by id and rebound to the restored holes: DiscFor would otherwise take each clone for a new, dark hole.
        holeDiscs.Clear();
        foreach (var d in a.Discs)
        {
            if (u.ById(d.HoleId) is not { Kind: BodyKind.BlackHole } hole) continue;
            holeDiscs[d.HoleId] = new HoleDisc
            {
                Hole = hole, Feed = d.Feed.Clone(),
                Tde = d.TdeEvent is { } ev ? new TdeFallback { Event = ev, SimStart = d.TdeSimStart, ArtReal = d.TdeArtReal, Absorbed = d.TdeAbsorbed } : null,
                FeedMdot = d.FeedMdot, FallbackMdot = d.FallbackMdot, Luminosity = d.Luminosity, Brightness = d.Brightness, PeakK = d.PeakK,
            };
        }

        // Debris: the particles, then the book that accounts for them. Without both the book cannot match the bodies.
        EnsureDebris();
        var back = !a.DebrisLost;
        if (back && debris != null)
            back = a.Debris != null ? debris.RestoreCopy(a.Debris, id => u.ById(id)) : debris.Occupied == 0;
        if (!back || a.Ledger == null || a.Known == null)
        {
            RebaselineDebris(back ? "undo point without a debris book" : "undo point's particles could not be restored");
            return back;
        }
        // Every map keyed by Body gets the restored clone with the same id; a body gone by then keeps its old object, which
        // is exactly what the audit treats as gone.
        Body Remap(Body b) => u.ById(b.Id) ?? b;
        ledger = a.Ledger.Clone(Remap);
        known.Clear();
        knownIds.Clear();
        foreach (var (b, k) in a.Known)
        {
            var to = Remap(b);
            known[to] = new KnownBody { Mass = k.Mass, Pos = k.Pos, Vel = k.Vel, Seen = k.Seen };
            knownIds[to.Id] = to;
        }
        involved.Clear();
        foreach (var b in a.Involved) involved.Add(Remap(b));
        newFragments.Clear();
        foreach (var b in a.NewFragments) newFragments.Add(Remap(b));
        ledgerDm.Clear();
        foreach (var (b, dm) in a.LedgerDm) ledgerDm[Remap(b)] = dm;
        lsInjectedFrame = a.LsInjectedFrame;
        collideResidualKg = a.CollideResidualKg;
        freshExternal = a.FreshExternal;
        return true;
    }

    /// <summary>
    /// Selection comes back as the point had it. The camera stays where the player has it: its focus is kept (rebound by
    /// id) unless that body is gone, and only then falls back to the point's focus, else the star. A free camera (no focus)
    /// stays free.
    /// </summary>
    void RestoreSelection(AppUndoState? a, bool hadFocus)
    {
        if (a?.SelectedId is string sel && Universe.ById(sel) is Body b && !ReferenceEquals(b, Selected)) Select(b);
        if (hadFocus && Rig.Focus == null)
            Rig.Focus = (a?.FocusId is string f ? Universe.ById(f) : null) ?? Star();
    }

    /// <summary>
    /// Drop or rebind everything that points at a body the restore replaced or removed. True when the last impact was
    /// cleared (the report card and its effects go with it).
    /// </summary>
    bool ClearDangling(UniverseSnapshot s)
    {
        var u = Universe;
        siteLock = null;
        var impactGone = false;
        if (LastImpact is { } li && (s.Time <= li.Time || u.ById(li.Target.Id) == null))
        {
            LastImpact = null;
            impactGone = true;
        }
        staleIds.Clear();
        foreach (var (id, _) in blackHoleNear)
            if (u.ById(id) == null) staleIds.Add(id);
        foreach (var id in staleIds) blackHoleNear.Remove(id);
        staleIds.Clear();
        foreach (var (id, o) in holeOrbits)
        {
            if (u.ById(o.Hole.Id) is not Body hole || u.ById(o.Primary.Id) is not Body prim)
            {
                staleIds.Add(id);
                continue;
            }
            // Rebound, and the angle re-anchored so the unwrapped sweep does not jump across the restore.
            o.Hole = hole;
            o.Primary = prim;
            var r = hole.Pos - prim.Pos;
            o.LastAngle = Math.Atan2(Vec3.Dot(r, o.E2), Vec3.Dot(r, o.E1));
            o.LastTime = u.Time;
        }
        foreach (var id in staleIds) holeOrbits.Remove(id);
        staleIds.Clear();
        if (probeBody != null && u.ById(probeBody.Id) is Body pb) probeBody = pb;
        return impactGone;
    }

    // ---------------------------------------------------------------- proof

    /// <summary>
    /// <c>logFullState[:tag]</c>: <see cref="LogState"/>'s body hash extended with spin and damage, and an app hash over what
    /// an undo point restores besides the bodies (the debris mirror, the ledger's tallies and counters, each disc, the SPH
    /// reservoir, moon detail), then the same values in words and the history's memory. Equal hashes mean exactly equal.
    /// </summary>
    void LogFullState(string tag)
    {
        EnsureDebris();
        ulong hb = 14695981039346656037, ha = hb;
        static void Mix(ref ulong h, ulong x)
        {
            for (var i = 0; i < 8; i++)
            {
                h ^= (x >> (8 * i)) & 0xff;
                h *= 1099511628211;
            }
        }
        static ulong Bits(double x) => (ulong)BitConverter.DoubleToInt64Bits(x);
        void B(double x) => Mix(ref hb, Bits(x));
        void A(double x) => Mix(ref ha, Bits(x));
        void AD(DD x) { A(x.Hi); A(x.Lo); }
        void AV(DV v) { AD(v.X); AD(v.Y); AD(v.Z); }
        void AT(Tally t) { AD(t.Mass); AV(t.Momentum); AV(t.Angular); }
        void AS(string s) { foreach (var c in s) Mix(ref ha, c); }

        foreach (var b in Universe.Bodies)
        {
            foreach (var c in b.Id) Mix(ref hb, c);
            B(b.Mass); B(b.Radius);
            B(b.Pos.X); B(b.Pos.Y); B(b.Pos.Z);
            B(b.Vel.X); B(b.Vel.Y); B(b.Vel.Z);
            B(b.Spin);
            var d = b.Damage;
            B(d.Hits); B(d.Energy); B(d.Absorbed); B(d.Craters.Count); B(d.Heat.Count); B(d.Magma); B(d.Dust); B(d.Version);
        }
        B(Universe.Time);

        var t = debris;
        var particles = t?.Occupied ?? 0;
        A(particles);
        A(t?.HighWater ?? 0);
        AD(t?.MassTotal ?? DD.Zero);
        var l = ledger!;
        AT(l.Initial); AT(l.External); AT(l.Bodies); AT(l.Particles); AT(l.Dust);
        A(l.Injections); A(l.Absorptions); A(l.Dustings); A(l.Accretions); A(l.MassInjected);
        var resMass = DD.Zero;
        var homes = new List<string>();
        foreach (var (home, r) in l.Reservoirs)
        {
            resMass += r.Mass;
            homes.Add(home.Id);
        }
        homes.Sort(StringComparer.Ordinal);
        foreach (var id in homes) AS(id);
        AD(resMass);
        var inv = CultureInfo.InvariantCulture;
        var discIds = new List<string>(holeDiscs.Keys);
        discIds.Sort(StringComparer.Ordinal);
        var discs = new List<string>();
        foreach (var id in discIds)
        {
            var d = holeDiscs[id];
            AS(id);
            A(d.Feed.Reservoir); A(d.Feed.Deposited); A(d.Feed.Accreted);
            A(d.Tde != null ? d.Tde.SimStart : -1);
            A(d.Tde?.Absorbed ?? 0);
            discs.Add(string.Create(inv, $"{id}:reservoir={d.Feed.Reservoir:R},deposited={d.Feed.Deposited:R},accreted={d.Feed.Accreted:R},tde={(d.Tde != null ? "on" : "off")}"));
        }
        var sr = SphReservoir;
        A(sr.Mass); A(sr.Momentum.X); A(sr.Momentum.Y); A(sr.Momentum.Z); A(sr.Events);
        AS(Settings.Moons.ToString());
        var h = Universe.History;
        GD.Print(string.Create(inv,
            $"[fullstate] {tag} bodies={Universe.Bodies.Count} time={Universe.Time:R} hash={hb:x16} app_hash={ha:x16}"));
        GD.Print(string.Create(inv,
            $"[fullstate] {tag} debris particles={particles} hwm={t?.HighWater ?? 0} t2_mirror={(t?.MassTotal ?? DD.Zero).Value:R} kg | ledger T0={l.Bodies.Mass.Value:R} T2={l.Particles.Mass.Value:R} dust={l.Dust.Mass.Value:R} external={l.External.Mass.Value:R} kg injections={l.Injections} absorptions={l.Absorptions} dustings={l.Dustings} accretions={l.Accretions} reservoirs={l.Reservoirs.Count} reservoir_mass={resMass.Value:R} kg"));
        GD.Print(string.Create(inv,
            $"[fullstate] {tag} discs=[{string.Join(' ', discs)}] sph_reservoir M={sr.Mass:R} kg events={sr.Events} sph_active={sph != null} moons={Settings.Moons} selected={Selected?.Id ?? "none"} focus={Rig.Focus?.Id ?? "none"}"));
        GD.Print(string.Create(inv,
            $"[fullstate] {tag} history undo={h.Count} redo={h.Redo.Count} bytes={h.Bytes} ({h.Bytes / 1048576.0:F1} MB of {h.ByteBudget / 1048576.0:F0}) vram={h.VramBytes} ({h.VramBytes / 1048576.0:F1} MB of {h.VramBudget / 1048576.0:F0}) pool={t?.UndoPoolBytes ?? 0} evictions={h.Evictions} sheds={h.Sheds} next_undo='{h.NextUndo?.Label}' next_redo='{h.NextRedo?.Label}'{(t != null && t.IsReady ? " rd " + t.MemMb() : "")}"));
    }
}
