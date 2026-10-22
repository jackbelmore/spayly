using System;
using System.Collections.Generic;
using System.Globalization;
using Godot;
using Spayly.Game.Render;
using Spayly.Sim;

namespace Spayly.Game.App;

public enum EventKind { Impact, Destruction, Swallowed, TidalDisruption, HoleMerger, GiantImpact, GiantImpactDone }

/// <summary>How much a watcher should care; see <see cref="App.ClassifyImpact"/> for the rule. The Director cuts at or above its setting.</summary>
public enum EventSeverity { Minor, Notable, Major }

/// <summary>One thing that happened in the sim and is worth telling the player about (the feed, UI/Hud.Feed.cs).</summary>
public sealed class GameEvent
{
    /// <summary>Counts up from 1 for the session; what <c>clickEvent:N</c> takes.</summary>
    public int Id { get; internal set; }
    public required EventKind Kind { get; init; }
    public required EventSeverity Severity { get; init; }
    public required string Title { get; set; }
    public string Detail { get; set; } = "";
    /// <summary>Universe time of the event, s; an undo to before it removes it from the feed.</summary>
    public required double SimTime { get; set; }
    /// <summary>Real time (s since launch) it was raised, or last coalesced into.</summary>
    public double RealTime { get; internal set; }
    /// <summary>Bodies involved, the first that still exists is where a click flies to.</summary>
    public required string[] BodyIds { get; init; }
    /// <summary>World position of the event, km.</summary>
    public required Vec3 Position { get; init; }
    /// <summary>The collision behind it, which is what the report card shows; null for tidal disruptions and SPH.</summary>
    public ImpactOutcome? Outcome { get; internal set; }
    /// <summary>Minor events of one kind on one body coalesce into one; this many so far.</summary>
    public int Count { get; internal set; } = 1;
    /// <summary>Which events coalesce (kind + target); null never does.</summary>
    public string? CoalesceKey { get; init; }
    /// <summary>The coalesced title; {0} is the count.</summary>
    public string? CoalescedFormat { get; init; }
    /// <summary>Camera distance a click or cut frames a no-outcome event from, km; 0 for a default.</summary>
    public double FrameKm { get; init; }
    public double? FramePitch { get; init; }
    /// <summary>The Director cut to it when it was raised.</summary>
    public bool Cut { get; internal set; }
}

/// <summary>
/// Events and the Director camera. Nothing here ever takes time or the camera unless the player asked for it: events go into
/// a feed (UI/Hud.Feed.cs) the player clicks, and the Director, when its setting allows, cuts to a <i>notable</i> one.
///
/// Severity (<see cref="ClassifyImpact"/>): Major is a named body (star, planet or moon) destroyed, disrupted or swallowed, a
/// black-hole merger, or an SPH giant impact; Notable is a named body hit or a crater at least <see cref="BigCraterM"/> wide;
/// Minor is everything else, above all fragment and rock impacts, which coalesce.
///
/// The Director cuts at most once per <see cref="DirectorCooldown"/> real seconds and never twice for one body within it. A cut
/// flies the camera to the event and eases the effective warp down to 1× for <see cref="SlowHold"/> s and back up; the
/// Director never writes <see cref="App.Warp"/>, and the ramp is dropped the moment the player sets one of their own; the app's approach slowdown (App.Approach.cs) and its ease back are not the player's.
/// </summary>
public partial class App
{
    /// <summary>[GAME] Real seconds between Director cuts, and before it cuts to the same body again.</summary>
    const double DirectorCooldown = 8;
    /// <summary>[GAME] Slow-motion shape, real seconds: ease down to 1×, hold, ease back to the player's warp.</summary>
    const double SlowDown = 0.3, SlowHold = 2.5, SlowUp = 0.5;
    /// <summary>[GAME] A crater at least this wide makes an impact Notable, m.</summary>
    const double BigCraterM = 50_000;
    /// <summary>[GAME] The feed keeps this many events.</summary>
    public const int MaxEvents = 50;
    /// <summary>[GAME] Minor events of one kind on one body within this many real seconds are one toast.</summary>
    const double CoalesceSeconds = 12;

    readonly List<GameEvent> events = [];
    int eventSeq;
    double lastCutAt = double.NegativeInfinity;
    readonly Dictionary<string, double> cutBodies = [];
    double slowStart = double.NegativeInfinity, slowPlayerWarp = 1;
    int slowEpoch;

    /// <summary>The recent events, oldest first, at most <see cref="MaxEvents"/>.</summary>
    public IReadOnlyList<GameEvent> Events => events;
    /// <summary>A new event, or an existing one that coalesced another (its <see cref="GameEvent.Count"/> grew).</summary>
    public event Action<GameEvent>? EventRaised;
    /// <summary>Events were removed (an undo to before them); the feed drops them.</summary>
    public event Action? EventsPruned;

    /// <summary>Stars, planets and moons: what the world calls named bodies; rocks, fragments and holes placed by hand are not.</summary>
    static bool IsNamedBody(Body b) => b.Kind is BodyKind.Star or BodyKind.Planet or BodyKind.Moon;

    /// <summary>The severity rule, in one place so it can be tuned; see the class comment.</summary>
    internal EventSeverity ClassifyImpact(ImpactOutcome o)
    {
        if (o.Merger != null) return EventSeverity.Major;
        // The named body is the one destroyed: shattered or dispersed, swallowed by a hole, or merged into something larger.
        if (IsNamedBody(o.Target) && o.Kind is OutcomeKind.Shatter or OutcomeKind.Disperse) return EventSeverity.Major;
        if (IsNamedBody(o.Impactor) && o.Kind is OutcomeKind.Absorb or OutcomeKind.Merge or OutcomeKind.StellarMerge) return EventSeverity.Major;
        if (o.Impactor.Kind == BodyKind.Fragment || IsShowerRock(o.Impactor)) return EventSeverity.Minor;
        if (IsNamedBody(o.Target)) return EventSeverity.Notable;
        if (o.Report?.Crater is { } c && c.FinalDiameter >= BigCraterM) return EventSeverity.Notable;
        return EventSeverity.Minor;
    }

    /// <summary>The feed event for one resolved collision (HandleOutcomes); black-hole mergers have theirs in OnHoleMerger.</summary>
    void PostImpactEvent(ImpactOutcome o)
    {
        var t = o.Target.Name;
        var i = o.Impactor.Name;
        var sev = ClassifyImpact(o);
        var swallowed = o.Target.Kind == BodyKind.BlackHole && o.Impactor.Kind != BodyKind.BlackHole;
        var kind = swallowed ? EventKind.Swallowed : o.Kind is OutcomeKind.Shatter or OutcomeKind.Disperse ? EventKind.Destruction : EventKind.Impact;
        var title = swallowed ? $"{i} swallowed by {t}"
            : o.Kind == OutcomeKind.Shatter ? $"{t} shattered by {i}"
            : o.Kind == OutcomeKind.Disperse ? $"{t} destroyed by {i}"
            : $"{i} hit {t}";
        var detail = $"{FormatSpeed(o.RelSpeed)} · {UI.Format.Energy(o.Energy)}";
        string? format = null;
        if (kind is EventKind.Impact or EventKind.Swallowed && sev == EventSeverity.Minor)
        {
            var noun = swallowed ? "bodies swallowed by" : o.Impactor.Kind == BodyKind.Fragment ? "fragment impacts on" : "rock impacts on";
            format = "{0} " + noun + " " + t;
        }
        PostEvent(new GameEvent
        {
            Kind = kind, Severity = sev, Title = title, Detail = detail, SimTime = o.Time, BodyIds = [o.Target.Id, o.Impactor.Id],
            Position = o.ContactWorld, Outcome = o, CoalesceKey = format == null ? null : $"{kind}:{o.Target.Id}:{(swallowed ? "s" : o.Impactor.Kind.ToString())}", CoalescedFormat = format,
        });
    }

    /// <summary>
    /// Record an event, tell the feed, and let the Director decide whether to cut to it. A Minor event that matches a recent one
    /// merges into it instead (a 30-fragment cascade is one toast), and never reaches the Director.
    /// </summary>
    internal GameEvent PostEvent(GameEvent e)
    {
        e.RealTime = realTime;
        if (e.CoalesceKey != null && e.Severity == EventSeverity.Minor)
        {
            for (var k = events.Count - 1; k >= 0; k--)
            {
                var old = events[k];
                if (old.CoalesceKey != e.CoalesceKey || realTime - old.RealTime > CoalesceSeconds) continue;
                old.Count++;
                old.Title = string.Format(CultureInfo.InvariantCulture, old.CoalescedFormat ?? old.Title, old.Count);
                old.RealTime = realTime;
                old.SimTime = e.SimTime;
                old.Outcome = e.Outcome;
                // Newest last, so the list and the toasts stay in the order things last happened.
                events.RemoveAt(k);
                events.Add(old);
                EventRaised?.Invoke(old);
                return old;
            }
        }
        e.Id = ++eventSeq;
        events.Add(e);
        if (events.Count > MaxEvents) events.RemoveAt(0);
        GD.Print(string.Create(CultureInfo.InvariantCulture,
            $"[event] #{e.Id} {e.Severity} {e.Kind} sim_t={e.SimTime:F1} s real_t={realTime:F2} '{e.Title}' {e.Detail} bodies={string.Join(',', e.BodyIds)}"));
        e.Cut = TryDirect(e);
        EventRaised?.Invoke(e);
        return e;
    }

    /// <summary>Drop every event at or after the restored state's time; the feed hears of it through <see cref="EventsPruned"/>.</summary>
    void PruneEvents()
    {
        var t = Universe.Time;
        var n = events.RemoveAll(e => e.SimTime >= t);
        if (n == 0) return;
        GD.Print($"[event] pruned {n} after undo/reset to sim_t={t:F1} s, {events.Count} left");
        EventsPruned?.Invoke();
    }

    // ---------------------------------------------------------------- Director

    /// <summary>Cut to <paramref name="e"/> if the setting, the cooldown and the body allow it; true when the camera went.</summary>
    bool TryDirect(GameEvent e)
    {
        var min = Settings.Director switch { DirectorMode.Major => EventSeverity.Major, DirectorMode.Notable => EventSeverity.Notable, _ => (EventSeverity?)null };
        if (min == null || e.Severity < min) return false;
        if (realTime - lastCutAt < DirectorCooldown)
        {
            GD.Print(string.Create(CultureInfo.InvariantCulture, $"[director] #{e.Id} no cut: {realTime - lastCutAt:F1} s since the last, cooldown {DirectorCooldown:F0} s"));
            return false;
        }
        foreach (var id in e.BodyIds)
            if (cutBodies.TryGetValue(id, out var at) && realTime - at < DirectorCooldown)
            {
                GD.Print($"[director] #{e.Id} no cut: already cut to {id} within the cooldown");
                return false;
            }
        if (!FlyToEvent(e)) return false;
        lastCutAt = realTime;
        cutBodies.Clear();
        foreach (var id in e.BodyIds) cutBodies[id] = realTime;
        StartSlowMo();
        GD.Print(string.Create(CultureInfo.InvariantCulture,
            $"[director] cut #{e.Id} {e.Severity} '{e.Title}' real_t={realTime:F2} focus={Rig.Focus?.Id} warp={Warp:G4} slow_mo={(Warp > 1 ? "yes" : "no (warp already 1)")}"));
        return true;
    }

    /// <summary>
    /// Fly to an event: the impact site when there is a collision to look at, else the first involved body still in the scene
    /// (framed from <see cref="GameEvent.FrameKm"/>), else the body nearest where it happened. False when nothing is left.
    /// </summary>
    bool FlyToEvent(GameEvent e)
    {
        if (e.Outcome is { } o && ReferenceEquals(Universe.ById(o.Target.Id), o.Target) && views.TryGetValue(o.Target.Id, out var view))
        {
            LookAtImpact(o, view);
            return true;
        }
        Body? to = null;
        foreach (var id in e.BodyIds)
            if (Universe.ById(id) is Body b)
            {
                to = b;
                break;
            }
        if (to == null)
        {
            var best = double.PositiveInfinity;
            foreach (var b in Universe.Bodies)
            {
                if (b.Kind is BodyKind.Fragment or BodyKind.Asteroid) continue;
                var d = Vec3.Length(b.Pos - e.Position);
                if (d < best)
                {
                    best = d;
                    to = b;
                }
            }
        }
        if (to == null) return false;
        ClearSiteLock();
        Rig.FlyTo(new ViewSpec { Focus = to, SetFocus = true, Distance = e.FrameKm > 0 ? e.FrameKm : Math.Max(Rig.Distance, 3 * to.Radius), Pitch = e.FramePitch }, 1.5);
        return true;
    }

    /// <summary>A click on a toast or a row of the Events list: fly there and open the report card, if the event has one.</summary>
    public void OpenEvent(GameEvent e)
    {
        CancelSkip();
        EnsureHud();
        if (!FlyToEvent(e)) Hint("Nothing of that event is left in the scene", 3);
        if (e.Outcome is { } o) hud.ShowReport(o, o.TargetDeltaV);
        GD.Print($"[event] open #{e.Id} '{e.Title}': focus={Rig.Focus?.Id ?? "none"} report={(e.Outcome != null ? "shown" : "none")}");
    }

    /// <summary>The report card's "Look at it": fly to that collision's site, whichever it shows.</summary>
    public void LookAtOutcome(ImpactOutcome o)
    {
        if (ReferenceEquals(Universe.ById(o.Target.Id), o.Target) && views.TryGetValue(o.Target.Id, out var view)) LookAtImpact(o, view);
    }

    /// <summary>Set the Director (the View panel, the key, the <c>director</c> verb).</summary>
    public void SetDirector(DirectorMode m, bool hint = false)
    {
        Settings.Director = m;
        if (hint) Hint(m switch { DirectorMode.Off => "Director camera off: the camera is yours", DirectorMode.Notable => "Director camera: cuts to notable and major events", _ => "Director camera: cuts to major events" }, 2.5);
    }

    // ---------------------------------------------------------------- slow motion

    /// <summary>A slow-motion is in flight (it may already be back at the player's warp, but is not over).</summary>
    bool SlowMoActive => realTime - slowStart <= SlowDown + SlowHold + SlowUp;

    void StartSlowMo()
    {
        // An approach slowdown has already lowered Warp; the warp to ramp back to is the one it will restore, not the lowered one.
        var player = double.IsNaN(approachBaseWarp) ? Warp : approachBaseWarp;
        if (player <= 1) return;
        slowStart = realTime;
        slowPlayerWarp = player;
        slowEpoch = warpEpoch;
    }

    /// <summary>Forget a slow-motion in flight (undo, reset, a rock being fired).</summary>
    void EndSlowMo() => slowStart = double.NegativeInfinity;

    /// <summary>
    /// The cap on this frame's warp: the player's warp eased (in log space, smoothstep) down to 1× over <see cref="SlowDown"/>,
    /// held, and eased back. Infinity when no cut is in progress, or once the player changed the warp: their value wins at
    /// once. The app's own changes (an approach slowdown and its ease back) do not count: <c>Math.Min(Warp, cap)</c> already
    /// lets the lower of the two win, and the restore waits for this ramp to finish.
    /// </summary>
    double SlowMoCap()
    {
        var t = realTime - slowStart;
        if (t > SlowDown + SlowHold + SlowUp) return double.PositiveInfinity;
        // A skip rewrites Warp every frame, so there the old rule stands: any change drops it.
        if (warpEpoch != slowEpoch || (skipping && Warp != slowPlayerWarp))
        {
            EndSlowMo();
            GD.Print($"[director] slow-motion dropped: warp changed from {slowPlayerWarp:G4} to {Warp:G4}");
            return double.PositiveInfinity;
        }
        var f = t < SlowDown ? t / SlowDown : t < SlowDown + SlowHold ? 1 : 1 - (t - SlowDown - SlowHold) / SlowUp;
        f = f * f * (3 - 2 * f);
        return Math.Pow(slowPlayerWarp, 1 - f);
    }

    // ---------------------------------------------------------------- capture verbs

    /// <summary>logEvents, clickEvent:N (an event's #id, or -1 for the newest, -2 the one before), director:off|notable|major, eventsPanel:1|0, traceWarp, logTrace.</summary>
    bool EventAction(string verb, string arg)
    {
        switch (verb)
        {
            case "logEvents":
                EnsureHud();
                GD.Print($"[events] {events.Count} in the feed, director={Settings.Director}, slow_mo_cap={SlowMoCap():G4}");
                foreach (var e in events)
                    GD.Print(string.Create(CultureInfo.InvariantCulture,
                        $"[events]   #{e.Id} {e.Severity} {e.Kind} x{e.Count} sim_t={e.SimTime:F1} real_t={e.RealTime:F2} cut={e.Cut} report={(e.Outcome != null ? "yes" : "no")} '{e.Title}' | {e.Detail}"));
                GD.Print($"[events] toasts {hud.FeedSummary()}");
                GD.Print($"[events] report {hud.ReportSummary()} focus={Rig.Focus?.Id ?? "none"} diorama={Settings.Diorama}");
                return true;
            case "clickEvent":
            {
                var n = int.Parse(arg, CultureInfo.InvariantCulture);
                var e = n < 0 ? (-n <= events.Count ? events[events.Count + n] : null) : events.Find(x => x.Id == n);
                if (e == null) GD.PushError($"[events] clickEvent: no event '{arg}'");
                else OpenEvent(e);
                return true;
            }
            case "director":
                SetDirector(Enum.Parse<DirectorMode>(arg, true));
                GD.Print($"[director] setting {Settings.Director}");
                return true;
            case "eventsPanel":
                EnsureHud();
                hud.ToggleEventsList(arg is "1" or "true" or "on" or "");
                return true;
            case "traceWarp":
                TraceStart(arg is "1" or "true" or "on" or "");
                return true;
            case "logTrace":
                LogTrace(arg);
                return true;
            default:
                return false;
        }
    }
}
