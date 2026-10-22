using System;
using System.Collections.Generic;
using System.Globalization;
using Godot;
using Spayly.Sim;

namespace Spayly.Game.App;

/// <summary>
/// Slow time before something is torn apart or swallowed (M6.2). At warp 1e6 one frame is ~4.6 hours, and a star crosses a
/// 10⁶ M☉ hole's tidal radius and horizon within minutes, so a frame stepped over the whole event and the star was
/// swallowed whole. When <see cref="Approach"/> says a body reaches a hole's event radius within <see cref="ApproachLookFrames"/>
/// frames, the warp drops so that the event takes about <see cref="ApproachEventFrames"/> frames. It drops once per
/// approach and stays down; the player raises it again with ] if they want to.
/// </summary>
public partial class App
{
    /// <summary>[GAME] Frames of warning: an event this close at the current warp slows time.</summary>
    const double ApproachLookFrames = 3;
    /// <summary>[GAME] Frames the event zone (event radius to contact) should span.</summary>
    const double ApproachEventFrames = 6;

    /// <summary>[GAME] Real seconds the warp takes to ease back up once the slowdown is over.</summary>
    const double ApproachRestoreSeconds = 1;
    /// <summary>[GAME] A slowdown still going after this many real seconds ends anyway: a body orbiting inside the zone would otherwise hold it for good.</summary>
    const double ApproachMaxHold = 30;

    /// <summary>A body the approach slowed for: <see cref="Radius"/> is its event radius, km; it counts as in the zone from the trigger until it has entered that radius and left it again, or is gone.</summary>
    sealed class ZonePair
    {
        public double Radius;
        public bool Entered;
    }

    readonly List<ApproachInfo> approaches = [];
    readonly Dictionary<(string Body, string Hole), ZonePair> approachZones = [];
    /// <summary>The player's warp from before the first slowdown of this episode, sim s per real s; NaN when no slowdown is in effect.</summary>
    double approachBaseWarp = double.NaN;
    /// <summary>The last warp this file wrote: if <see cref="Warp"/> differs, something else (the player) changed it.</summary>
    double approachSetWarp;
    double approachSlowSince, approachRestoreStart = double.NegativeInfinity, approachRestoreFrom;
    /// <summary>Counts the player's own warp changes (<see cref="SetWarp"/>), so a slow-motion can tell them from the app's.</summary>
    int warpEpoch;
    readonly HashSet<(string, string)> approachSlowed = [];
    readonly HashSet<(string, string)> approachLive = [];

    /// <summary>Realism entry for the slow-down; the HUD calls this when it builds the Realism panel.</summary>
    public static void RegisterApproachRealism()
    {
        Realism.Register("Slowing time near black holes", RealismTag.Game,
            "When a body is a few frames from a black hole's tidal radius or horizon, time warp drops so the disruption or plunge takes several frames on screen, then eases back to your warp over about a second once the body has been torn apart, swallowed or has passed. A warp you set yourself during the slowdown stays. Besides the impact camera and Skip, it is the only automatic change to the warp.",
            RealismArea.Time);
    }

    /// <summary>Step 3, before physics: lower <see cref="Warp"/> for an imminent event near a hole.</summary>
    void SlowForApproach(double dtReal)
    {
        if (skipping)
        {
            // A skip owns the warp (App.Skip.cs eases it near holes itself) and puts the player's back when it ends.
            ForgetApproach("skip running");
            return;
        }
        if (Paused || Rewind || !(dtReal > 0)) return;
        if (!double.IsNaN(approachBaseWarp) && Warp != approachSetWarp) ForgetApproach($"warp changed from {approachSetWarp:G4} to {Warp:G4} by the player");
        var holes = false;
        foreach (var b in Universe.Bodies)
        {
            if (b.Kind != BodyKind.BlackHole) continue;
            holes = true;
            break;
        }
        if (!holes)
        {
            approachSlowed.Clear();
            approachZones.Clear();
            RestoreAfterApproach();
            return;
        }
        Approach.Find(Universe, Settings.Tidal, approaches);
        approachLive.Clear();
        ApproachInfo? worst = null;
        var cap = double.PositiveInfinity;
        var frame = dtReal * Warp;
        foreach (var a in approaches)
        {
            var key = (a.Body.Id, a.Hole.Id);
            approachLive.Add(key);
            if (approachSlowed.Contains(key) || a.Arrive > ApproachLookFrames * frame) continue;
            approachSlowed.Add(key);
            approachZones[(a.Body.Id, a.Hole.Id)] = new ZonePair { Radius = a.Radius };
            var w = Approach.WarpFor(a, dtReal, ApproachEventFrames);
            if (w < cap)
            {
                cap = w;
                worst = a;
            }
        }
        // An approach that is over (reached, receding, gone) is forgotten, so the next one slows time again.
        approachSlowed.IntersectWith(approachLive);
        TrackZones();
        if (worst is not ApproachInfo a0 || !(cap < Warp))
        {
            RestoreAfterApproach();
            return;
        }
        var before = Warp;
        // The first slowdown of an episode remembers the player's warp; later ones, and one that interrupts the ease back, keep it.
        if (double.IsNaN(approachBaseWarp))
        {
            approachBaseWarp = Warp;
            approachSlowSince = realTime;
        }
        approachRestoreStart = double.NegativeInfinity;
        Warp = approachSetWarp = cap;
        GD.Print(string.Create(CultureInfo.InvariantCulture,
            $"[slow] {a0.Body.Id} '{a0.Body.Name}' -> {a0.Hole.Id} {a0.Event} radius={a0.Radius:G4} km contact={a0.Contact:G4} km arrive={a0.Arrive:G4} s zone={a0.Zone:G4} s frame_dt={dtReal:G3} s warp {before:G4} -> {Warp:G4}"));
        Hint($"Slowing time: {a0.Body.Name} is about to reach {a0.Hole.Name}. Your speed comes back afterwards.", 5);
    }

    /// <summary>
    /// Drop the pairs whose event is over: the body or hole is gone (torn apart, swallowed, merged, erased), or the body has
    /// been inside its event radius and is out again, or is no longer falling in. Approach.Find stops reporting a body the
    /// moment it crosses the event radius, which is when the slowdown matters most, so the zone is tracked here instead.
    /// </summary>
    void TrackZones()
    {
        if (approachZones.Count == 0) return;
        List<(string, string)>? over = null;
        foreach (var (key, z) in approachZones)
        {
            var body = Universe.ById(key.Body);
            var hole = Universe.ById(key.Hole);
            var done = body == null || hole == null;
            if (!done)
            {
                var r = body!.Pos - hole!.Pos;
                var d = Vec3.Length(r);
                if (d <= z.Radius) z.Entered = true;
                done = d > z.Radius && (z.Entered || Vec3.Dot(r, body.Vel - hole.Vel) >= 0);
            }
            if (done) (over ??= []).Add(key);
        }
        if (over == null) return;
        foreach (var k in over) approachZones.Remove(k);
    }

    /// <summary>
    /// While no body is in an event zone any more, ease <see cref="Warp"/> from where the slowdown left it back up to the
    /// player's, geometrically over <see cref="ApproachRestoreSeconds"/> (the dial is logarithmic, so equal ratios per second
    /// feel even). Waits out a Director slow-motion: that one eases to 1× and back on its own clock.
    /// </summary>
    void RestoreAfterApproach()
    {
        if (double.IsNaN(approachBaseWarp)) return;
        if (approachZones.Count > 0)
        {
            if (realTime - approachSlowSince < ApproachMaxHold) return;
            GD.Print($"[slow] held {ApproachMaxHold:G3} s, ending the slowdown with {approachZones.Count} body(ies) still in the zone");
            approachZones.Clear();
        }
        if (SlowMoActive) return;
        if (double.IsNegativeInfinity(approachRestoreStart))
        {
            approachRestoreStart = realTime;
            approachRestoreFrom = Warp;
            GD.Print(string.Create(CultureInfo.InvariantCulture, $"[slow] over: warp {Warp:G4} back to {approachBaseWarp:G4} over {ApproachRestoreSeconds:G2} s real_t={realTime:F2}"));
        }
        var t = Math.Clamp((realTime - approachRestoreStart) / ApproachRestoreSeconds, 0, 1);
        var f = t * t * (3 - 2 * t);
        Warp = approachSetWarp = t >= 1 ? approachBaseWarp : Math.Exp(Math.Log(approachRestoreFrom) * (1 - f) + Math.Log(approachBaseWarp) * f);
        if (t < 1) return;
        GD.Print(string.Create(CultureInfo.InvariantCulture, $"[slow] restored: warp {Warp:G4} real_t={realTime:F2}"));
        approachBaseWarp = double.NaN;
        approachRestoreStart = double.NegativeInfinity;
    }

    /// <summary>Stop watching a slowdown without restoring anything: the player set a warp, a skip took over, or the state was rolled back.</summary>
    void ForgetApproach(string why)
    {
        approachZones.Clear();
        if (double.IsNaN(approachBaseWarp)) return;
        GD.Print(string.Create(CultureInfo.InvariantCulture, $"[slow] restore dropped ({why}); warp stays {Warp:G4}, was {approachBaseWarp:G4} before the slowdown"));
        approachBaseWarp = double.NaN;
        approachRestoreStart = double.NegativeInfinity;
    }
}
