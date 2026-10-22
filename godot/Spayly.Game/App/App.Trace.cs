using System;
using System.Globalization;
using Godot;
using Spayly.Sim;

namespace Spayly.Game.App;

/// <summary>
/// Capture-only accounting of what time and the camera did, so a run-capture log can prove "the warp was never capped" and
/// "the focus never moved" instead of anyone judging it by eye: <c>traceWarp</c> starts it, <c>logTrace[:tag]</c> prints it.
/// </summary>
public partial class App
{
    bool tracing;
    double traceSim0, traceReal0, traceMinEff = double.PositiveInfinity, traceMaxWarp;
    int traceFrames, traceCapped, traceFocusChanges;
    string? traceFocus;
    Body? traceFocusBody;
    /// <summary>Focus changes where the old focus was still in the scene: the camera was moved, not just re-pointed off a body that is gone.</summary>
    int traceMovedFocus;
    bool traceWasCapped;
    double traceSampleAt;

    void TraceStart(bool on)
    {
        tracing = on;
        if (!on) return;
        traceSim0 = Universe.Time;
        traceReal0 = realTime;
        traceMinEff = double.PositiveInfinity;
        traceMaxWarp = 0;
        traceFrames = traceCapped = traceFocusChanges = traceMovedFocus = 0;
        traceFocus = Rig.Focus?.Id;
        traceFocusBody = Rig.Focus;
        traceWasCapped = false;
        GD.Print($"[trace] start warp={Warp:G4} focus={traceFocus ?? "none"} sim_t={Universe.Time:F1}");
    }

    /// <summary>Per frame, after the physics step; prints only when the capped state or the focus changes.</summary>
    void TraceWarp()
    {
        if (!tracing || Paused) return;
        traceFrames++;
        var capped = EffectiveWarp < Warp * (1 - 1e-9);
        if (capped) traceCapped++;
        traceMinEff = Math.Min(traceMinEff, EffectiveWarp);
        traceMaxWarp = Math.Max(traceMaxWarp, Warp);
        if (capped != traceWasCapped)
            GD.Print(string.Create(CultureInfo.InvariantCulture, $"[trace] real_t={realTime - traceReal0:F2} {(capped ? "warp capped" : "warp uncapped")}: warp={Warp:G4} effective={EffectiveWarp:G4}"));
        traceWasCapped = capped;
        if (capped && realTime >= traceSampleAt)
        {
            traceSampleAt = realTime + 0.25;
            GD.Print(string.Create(CultureInfo.InvariantCulture, $"[trace] real_t={realTime - traceReal0:F2} warp={Warp:G4} effective={EffectiveWarp:G4}"));
        }
        var focus = Rig.Focus?.Id;
        if (focus != traceFocus)
        {
            traceFocusChanges++;
            var gone = traceFocusBody != null && !ReferenceEquals(Universe.ById(traceFocusBody.Id), traceFocusBody);
            if (!gone) traceMovedFocus++;
            GD.Print($"[trace] real_t={realTime - traceReal0:F2} focus {traceFocus ?? "none"} -> {focus ?? "none"} ({(gone ? "old focus is gone from the scene" : "old focus still there: camera moved")})");
            traceFocus = focus;
            traceFocusBody = Rig.Focus;
        }
    }

    void LogTrace(string tag)
    {
        var real = realTime - traceReal0;
        var sim = Universe.Time - traceSim0;
        GD.Print(string.Create(CultureInfo.InvariantCulture,
            $"[trace] {tag} frames={traceFrames} capped_frames={traceCapped} min_effective_warp={traceMinEff:G4} max_warp={traceMaxWarp:G4} focus_changes={traceFocusChanges} focus_moves_with_old_focus_alive={traceMovedFocus} real_s={real:F2} sim_advanced_s={sim:F1} mean_sim_per_real={(real > 0 ? sim / real : 0):G5}"));
    }
}
