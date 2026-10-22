using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using Godot;
using Spayly.Game.UI;
using Spayly.Sim;

namespace Spayly.Game.App;

/// <summary>
/// "Skip to next event": run time as fast as the physics can afford until something notable happens, then watch it. It asks
/// for the top of the warp range and lets the substep budget (<c>Universe.WarpLimited</c>, the "limited" chip) throttle it, so
/// nothing is skipped: every substep still runs, and a slow system simply takes longer in real time.
///
/// It stops on the first <see cref="GameEvent"/> at or above <see cref="Settings.SkipStop"/>, at <see cref="Settings.SkipHorizonYears"/>
/// of sim time, or on any player input: a click or key, a warp, pause, rewind, mode, focus or camera-view change, an undo. The
/// events it runs into record their undo points as usual; the skip itself records none.
/// </summary>
public partial class App
{
    /// <summary>[GAME] The warp a skip asks for, sim s per real s: the top of the dial.</summary>
    const double SkipWarp = 1e8;
    const double SecondsPerYear = 365.25 * 86400;

    bool skipping;
    double skipPrevWarp = 1, skipSim0, skipReal0, skipMaxDeltaMs;
    long skipStamp;
    int skipFrames, skipLimitedFrames, skipSlowFrames, skipEvents;
    double skipMaxProcessMs, skipMaxPhysicsMs;
    bool skipSlowHinted;
    string skipLast = "none yet";

    /// <summary>True from <see cref="StartSkip"/> until it stops; the HUD button shows Cancel.</summary>
    public bool Skipping => skipping;
    /// <summary>Sim seconds this skip has run, 0 when none is.</summary>
    public double SkipElapsed => skipping ? Universe.Time - skipSim0 : 0;

    /// <summary>Start a skip. Time runs forward again if it was paused or rewinding: a skip is a forward-only act.</summary>
    public void StartSkip()
    {
        if (skipping) return;
        Paused = false;
        if (Rewind)
        {
            Rewind = false;
            Hint("Time runs forward again");
        }
        skipping = true;
        // The warp the player had before an approach slowdown is what a skip puts back, if it ends without an event.
        if (!double.IsNaN(approachBaseWarp)) Warp = approachBaseWarp;
        ForgetApproach("a skip started");
        EndSlowMo();
        skipPrevWarp = Warp;
        skipSim0 = Universe.Time;
        skipReal0 = realTime;
        skipStamp = Stopwatch.GetTimestamp();
        skipFrames = skipLimitedFrames = skipSlowFrames = skipEvents = 0;
        skipMaxDeltaMs = skipMaxProcessMs = skipMaxPhysicsMs = 0;
        skipSlowHinted = false;
        Warp = SkipWarp;
        EventRaised += OnSkipEvent;
        GD.Print(string.Create(CultureInfo.InvariantCulture,
            $"[skip] start warp {skipPrevWarp:G4} -> {Warp:G4} stop_at={Settings.SkipStop} horizon={Settings.SkipHorizonYears:G4} y director={Settings.Director} moons={Settings.Moons} sim_t={Universe.Time:F1}"));
        Hint($"Skipping to the next {Settings.SkipStop.ToString().ToLowerInvariant()} event. Any key or click stops it", 3);
    }

    /// <summary>Stop a skip at the player's request and put their warp back. A no-op when none runs.</summary>
    public void CancelSkip() => StopSkip("cancelled by the player", null, true);

    /// <summary>Step 3, before physics: the stop conditions that are not events, and the warp the skip wants.</summary>
    void UpdateSkip(double delta)
    {
        if (!skipping) return;
        skipFrames++;
        skipMaxDeltaMs = Math.Max(skipMaxDeltaMs, delta * 1000);
        if (Paused || Rewind)
        {
            StopSkip(Paused ? "paused" : "rewind", null, true);
            return;
        }
        if (Universe.Time - skipSim0 >= Settings.SkipHorizonYears * SecondsPerYear)
        {
            StopSkip("horizon", null, true);
            return;
        }
        // The substep budget decides how fast a skip really is (all moons: ~7e5 s/s, so 100 years would take over an hour).
        // Say so once, with the way out; the moon detail is the player's to change.
        var real = realTime - skipReal0;
        if (!skipSlowHinted && real > 5)
        {
            skipSlowHinted = true;
            var rate = (Universe.Time - skipSim0) / real;
            var eta = (Settings.SkipHorizonYears * SecondsPerYear - (Universe.Time - skipSim0)) / Math.Max(rate, 1);
            if (eta > 120)
            {
                GD.Print(string.Create(CultureInfo.InvariantCulture, $"[skip] slow: {rate:G3} sim s per real s, {Settings.SkipHorizonYears:G4} y would take {eta / 60:F0} min"));
                Hint($"Running at {Format.Warp(rate)}: {Settings.SkipHorizonYears:G4} years would take {Format.Num(eta / 60)} min. Fewer moons (View panel) skip faster", 8);
            }
        }
        if (Sim.WarpLimited) skipLimitedFrames++;
        if (EffectiveWarp < Warp) skipSlowFrames++;
        Warp = SkipWarpFor(Math.Clamp(delta, 0, 0.1));
    }

    readonly List<ApproachInfo> skipApproaches = [];

    /// <summary>
    /// The warp for this frame: <see cref="SkipWarp"/>, unless a body is falling toward a hole's tidal or capture radius.
    /// SlowForApproach does that once, at three frames' notice, and then holds the low warp that suits the event zone for the
    /// whole fall, which at a hole 6 AU out is an hour of real time. A skip instead eases off: the warp that would put the
    /// body at the event radius four frames from now (so SlowForApproach never fires), floored at the warp that spreads the
    /// zone itself over <see cref="ApproachEventFrames"/> frames, and full speed again once nothing is falling in.
    /// </summary>
    double SkipWarpFor(double dtReal)
    {
        var target = SkipWarp;
        if (dtReal <= 0) return target;
        foreach (var b in Universe.Bodies)
        {
            if (b.Kind != BodyKind.BlackHole) continue;
            Approach.Find(Universe, Settings.Tidal, skipApproaches);
            foreach (var a in skipApproaches)
                target = Math.Min(target, Math.Max(Approach.WarpFor(a, dtReal, ApproachEventFrames), a.Arrive / (4 * dtReal)));
            break;
        }
        return Math.Max(1, target);
    }

    /// <summary>What a frame cost the CPU, ms: the engine's frame time also holds Xvfb's copy (~45 ms a frame here), which a skip does not cause.</summary>
    void NoteSkipFrame(double processMs, double physicsMs)
    {
        if (!skipping) return;
        skipMaxProcessMs = Math.Max(skipMaxProcessMs, processMs);
        skipMaxPhysicsMs = Math.Max(skipMaxPhysicsMs, physicsMs);
    }

    void OnSkipEvent(GameEvent e)
    {
        skipEvents++;
        if (e.Severity >= Settings.SkipStop) StopSkip("event", e, true);
    }

    /// <summary>
    /// End the skip. <paramref name="restoreWarp"/> is false when the player just set a warp of their own, which wins.
    /// After an event the warp goes to 1×, not back to the player's previous setting: the point of a skip is to watch what
    /// happens next, the consequences of a hit take real seconds to unfold, and at their old 10⁶× a plunge or a crater would
    /// be over inside one frame. Every other stop gives them their previous warp back, since nothing is there to watch.
    /// </summary>
    void StopSkip(string reason, GameEvent? e, bool restoreWarp)
    {
        if (!skipping) return;
        skipping = false;
        EventRaised -= OnSkipEvent;
        var sim = Universe.Time - skipSim0;
        var real = realTime - skipReal0;
        var wall = Stopwatch.GetElapsedTime(skipStamp).TotalSeconds;
        var before = Warp;
        if (restoreWarp) Warp = e != null ? 1 : skipPrevWarp;
        // A Director cut on this very event started a slow-motion from the skip's warp; the warp is 1× now, so there is
        // nothing to ease down from.
        if (e != null) EndSlowMo();
        var years = sim / SecondsPerYear;
        var what = Fmt(years);
        var hint = e != null ? $"Skipped {what}: {e.Title}"
            : reason == "horizon" ? (skipEvents == 0 ? $"Nothing happened in {Fmt(Settings.SkipHorizonYears)}" : $"Nothing {Settings.SkipStop.ToString().ToLowerInvariant()} happened in {Fmt(Settings.SkipHorizonYears)} ({skipEvents} smaller)")
            : $"Skip stopped after {what}";
        Hint(hint, e != null || reason == "horizon" ? 5 : 2.5);
        skipLast = string.Create(CultureInfo.InvariantCulture,
            $"reason={reason} sim_skipped_s={sim:F0} ({years:G4} y) real_s={real:F2} wall_s={wall:F2} mean_sim_per_real={(real > 0 ? sim / real : 0):G4} frames={skipFrames} limited_frames={skipLimitedFrames} slowed_frames={skipSlowFrames} max_frame_ms={skipMaxDeltaMs:F1} max_process_ms={skipMaxProcessMs:F1} max_physics_ms={skipMaxPhysicsMs:F1} events_seen={skipEvents} stop_event={(e != null ? $"#{e.Id} {e.Severity} '{e.Title}' sim_t={e.SimTime:F0}" : "none")} warp {before:G4} -> {Warp:G4} (previous {skipPrevWarp:G4}) director_cut={(e?.Cut == true)} hint='{hint}' focus={Rig.Focus?.Id ?? "none"}");
        GD.Print("[skip] stop " + skipLast);

        static string Fmt(double y) => y >= 2 ? $"{Format.Num(y)} years" : $"{Format.Num(y * 365.25)} days";
    }

    /// <summary>skip[:minor|notable|major], cancelSkip, logSkip, skipHorizon:YEARS.</summary>
    bool SkipAction(string verb, string arg)
    {
        switch (verb)
        {
            case "skip":
                if (arg.Length > 0) Settings.SkipStop = Enum.Parse<EventSeverity>(arg, true);
                StartSkip();
                return true;
            case "cancelSkip":
                CancelSkip();
                return true;
            case "skipHorizon":
                Settings.SkipHorizonYears = double.Parse(arg, CultureInfo.InvariantCulture);
                return true;
            case "logSkip":
                GD.Print(string.Create(CultureInfo.InvariantCulture,
                    $"[skip] {(skipping ? $"running {SkipElapsed:F0} s sim, {realTime - skipReal0:F2} s real" : "idle")}; last: {skipLast}"));
                return true;
            default:
                return false;
        }
    }
}
