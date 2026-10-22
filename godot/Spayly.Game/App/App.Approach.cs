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

    readonly List<ApproachInfo> approaches = [];
    readonly HashSet<(string, string)> approachSlowed = [];
    readonly HashSet<(string, string)> approachLive = [];

    /// <summary>Realism entry for the slow-down; the HUD calls this when it builds the Realism panel.</summary>
    public static void RegisterApproachRealism()
    {
        Realism.Register("Slowing time near black holes", RealismTag.Game,
            "When a body is a few frames from a black hole's tidal radius or horizon, time warp drops once so the disruption or plunge takes several frames on screen; it is the only automatic change to the warp besides the impact camera, and ] speeds it up again.",
            RealismArea.Time);
    }

    /// <summary>Step 3, before physics: lower <see cref="Warp"/> for an imminent event near a hole.</summary>
    void SlowForApproach(double dtReal)
    {
        if (Paused || Rewind || !(dtReal > 0)) return;
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
            var w = Approach.WarpFor(a, dtReal, ApproachEventFrames);
            if (w < cap)
            {
                cap = w;
                worst = a;
            }
        }
        // An approach that is over (reached, receding, gone) is forgotten, so the next one slows time again.
        approachSlowed.IntersectWith(approachLive);
        if (worst is not ApproachInfo a0 || !(cap < Warp)) return;
        var before = Warp;
        Warp = cap;
        GD.Print(string.Create(CultureInfo.InvariantCulture,
            $"[slow] {a0.Body.Id} '{a0.Body.Name}' -> {a0.Hole.Id} {a0.Event} radius={a0.Radius:G4} km contact={a0.Contact:G4} km arrive={a0.Arrive:G4} s zone={a0.Zone:G4} s frame_dt={dtReal:G3} s warp {before:G4} -> {Warp:G4}"));
        Hint($"Slowing time: {a0.Body.Name} is about to reach {a0.Hole.Name}. Press ] to speed up again.", 5);
    }
}
