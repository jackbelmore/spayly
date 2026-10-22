using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using Godot;
using Spayly.Game.Render;
using Spayly.Game.UI;
using Spayly.Sim;

namespace Spayly.Game.App;

/// <summary>
/// The frame, in the web's order (app.ts:493-588; map §1). The order is load-bearing: exposure reads this frame's sun
/// state, the camera stop reads this frame's diorama, labels read the views' fresh pixel radii. Steps that belong to later
/// work are <c>partial void</c> hooks, compiled away until another file implements them:
///   UpdateSiteLock (cinematic camera), UpdateEffects, HandleOutcomes, UpdateAim, SweepTextures, UpdateHud, PumpTextureUploads.
/// </summary>
public partial class App
{
    public override void _Process(double delta)
    {
        var t0 = Stopwatch.GetTimestamp();
        // 1. dt clamp, fps accounting
        var dtReal = Math.Clamp(delta, 0, 0.1);
        realTime += dtReal;
        frames++;
        fpsTime += dtReal;
        if (fpsTime >= 0.5)
        {
            Fps = frames / fpsTime;
            frames = 0;
            fpsTime = 0;
        }

        // 2. diorama amount eased toward the setting
        var dioramaTarget = Settings.Diorama ? 1.0 : 0.0;
        dioramaAmount += Math.Clamp(dioramaTarget - dioramaAmount, -dtReal * 1.4, dtReal * 1.4);

        // 3. physics; the cinematic camera caps warp at 1 for 9 s after an impact, and an imminent tidal disruption or plunge
        // into a hole lowers the warp itself (App.Approach.cs); NaN recovery
        SlowForApproach(dtReal);
        PumpHistoryJump();
        PumpShower();
        Sim.Step(dtReal, Warp, Rewind, Paused || TidalHold, realTime < cinematicUntil ? 1 : double.PositiveInfinity);
        if (!Paused && !Sim.StateIsFinite()) RecoverFromFault();
        var t1 = Stopwatch.GetTimestamp();

        // 4. views follow the body list
        SyncViews();
        var tViews = Stopwatch.GetTimestamp();

        // 5. sun state from live mass
        UpdateSunState();

        // 6. auto-exposure from irradiance at the focus
        UpdateExposure(dtReal);

        // 7. camera surface stop from SolidRadius (never VisualRadius: the pixel floor would shove the camera off distant bodies)
        ctx.DioramaAmount = dioramaAmount;
        ctx.Bodies = Universe.Bodies;
        var camFocus = Rig.Focus;
        Rig.FocusRadiusKm = camFocus != null ? ctx.SolidRadius(camFocus) / RenderUnits.Scale * (camFocus.Kind == BodyKind.Star ? 1.25 : 1.03) : 0;

        // 8. site lock, keyboard flight, rig, near/far
        UpdateSiteLock(realTime);
        FlyWithKeys(dtReal);
        Rig.Update(dtReal);
        ctx.Origin.Origin = Rig.Position;
        UpdateDepthRange();

        // 9. fill the view context
        FillViewContext();

        // 10. every view
        foreach (var v in views.Values) v.Update(ctx);

        var tUpdate = Stopwatch.GetTimestamp();
        // 11. labels and lines
        UpdateLabels();
        UpdateLines();

        // 12. belt and sky
        belt.Visible = Settings.Belt;
        if (Settings.Belt) belt.Update(ctx);
        sky.Update(ctx.ExposureComp);

        var tLines = Stopwatch.GetTimestamp();
        // 13. effects, outcomes, aim
        UpdateEffects(dtReal);
        if (Sim.Outcomes.Count > 0)
        {
            HandleOutcomes(Sim.Outcomes);
            ApplyOutcomes();
        }
        UpdateTidal(dtReal);
        UpdateHoleDiscs(dtReal);
        UpdateDebris(dtReal);
        UpdateAim();
        UpdateHoleMode();

        var tEvents = Stopwatch.GetTimestamp();
        // 14. label-layer visibility, texture sweep timer
        labels.Shown = Settings.Labels;
        textureSweep += dtReal;
        if (textureSweep > 4)
        {
            textureSweep = 0;
            SweepTextures();
        }

        // 15. HUD at ~8 Hz
        hudTimer += dtReal;
        if (hudTimer > 0.12)
        {
            hudTimer = 0;
            hud.Update(HudInfo());
            UpdateHud();
        }

        var tHud = Stopwatch.GetTimestamp();
        // 16. exposure/bloom to the scene, texture uploads; Godot renders after _Process
        environment.TonemapExposure = (float)Exposure;
        environment.GlowEnabled = Settings.Bloom;
        UpdateSunLight();
        PumpTextureUploads();
        UpdateProbe();
        var t2 = Stopwatch.GetTimestamp();
        AccountFrame(t0, t1, t2);
        // A frame loop over the 33 ms budget is named with its frame and phases, so a slow dt in a capture has a cause.
        if (Stopwatch.GetElapsedTime(t0, t2).TotalMilliseconds > 33)
        {
            static double Ms(long a, long b) => Stopwatch.GetElapsedTime(a, b).TotalMilliseconds;
            GD.Print(string.Create(CultureInfo.InvariantCulture,
                $"[hitch] frame={Engine.GetProcessFrames()} process_ms={Ms(t0, t2):F2}: physics {Ms(t0, t1):F2} views {Ms(t1, tViews):F2} update {Ms(tViews, tUpdate):F2} lines {Ms(tUpdate, tLines):F2} events {Ms(tLines, tEvents):F2} hud {Ms(tEvents, tHud):F2} rest {Ms(tHud, t2):F2}"));
        }
    }

    long statFrames;
    double statProcessMs, statPhysicsMs, statMaxMs;

    /// <summary>
    /// Every 60 frames, print what the frame loop costs on the CPU (the harness's dt includes Xvfb's copy, gpu_ms only the
    /// GPU) and the gen-0 GC count, which must stay flat: nothing on the render path allocates per frame.
    /// </summary>
    void AccountFrame(long t0, long t1, long t2)
    {
        var total = Stopwatch.GetElapsedTime(t0, t2).TotalMilliseconds;
        statProcessMs += total;
        statPhysicsMs += Stopwatch.GetElapsedTime(t0, t1).TotalMilliseconds;
        statMaxMs = Math.Max(statMaxMs, total);
        if (++statFrames % 60 != 0) return;
        GD.Print(string.Create(CultureInfo.InvariantCulture,
            $"[app] frame={statFrames} process_ms={statProcessMs / 60:F3} physics_ms={statPhysicsMs / 60:F3} max_ms={statMaxMs:F3} near={Rig.Near:G3} far={Rig.Far:G3} squeeze={ctx.Origin.SqueezeStart:G3} gc0={GC.CollectionCount(0)}"));
        statProcessMs = statPhysicsMs = statMaxMs = 0;
    }

    /// <summary>
    /// Near plane at half the nearest surface in front of the camera, far at 1e6 × near (M1c), squeeze in between. Uses
    /// SolidRadius: the pixel floor only inflates dots, whose surface distance is their centre distance anyway.
    /// </summary>
    void UpdateDepthRange()
    {
        var fwd = -camera.Basis.Z;
        var origin = Rig.Position;
        var nearest = double.PositiveInfinity;
        var bodies = Universe.Bodies;
        for (var i = 0; i < bodies.Count; i++)
        {
            var b = bodies[i];
            var p = b.Pos - origin;
            // Sim → render axes for the facing test only.
            double rx = p.X * RenderUnits.Scale, ry = p.Z * RenderUnits.Scale, rz = -p.Y * RenderUnits.Scale;
            var d = Math.Sqrt(rx * rx + ry * ry + rz * rz);
            var r = ctx.SolidRadius(b);
            if (rx * fwd.X + ry * fwd.Y + rz * fwd.Z < -r) continue;
            var surface = d - r;
            if (surface < nearest) nearest = surface;
        }
        Rig.SetDepthRange(nearest, ctx.Origin);
    }

    void FillViewContext()
    {
        var sun = Star();
        var size = GetViewport().GetVisibleRect().Size;
        ctx.Jd = Universe.Jd;
        ctx.SimTime = Universe.Time;
        ctx.RealTime = realTime;
        ctx.SunPos = sun.Pos;
        ctx.SunRadiusKm = sun.Radius;
        ctx.SunColor = sunState.Color;
        ctx.SunLuminosity = sunState.Luminosity;
        ctx.SunWatts = sunState.Watts;
        ctx.Falloff = Settings.Falloff;
        ctx.ExposureComp = 1 / Exposure;
        ctx.DioramaAmount = dioramaAmount;
        ctx.PixelsPerRadian = Rig.PixelsPerRadian(size.Y);
        ctx.Bodies = Universe.Bodies;
        ctx.CameraQuaternion = camera.Quaternion;
        ctx.CameraBasis = camera.Basis;
        ctx.TanHalfFovY = Math.Tan(Rig.Fov * Math.PI / 360);
        ctx.Aspect = size.X / Math.Max(size.Y, 1);
        ctx.TextureCeiling = Settings.Textures;
        ctx.PixelRatio = 1;
        ctx.FocusId = Rig.Focus?.Id;
    }

    /// <summary>A non-finite state rolled back (or not); pause either way so the player sees it (app.ts recoverFromFault).</summary>
    void RecoverFromFault()
    {
        // The rollback may land before a shower that is still launching: its remaining rocks must not follow it there.
        launching = null;
        var restored = Sim.RecoverFromFault();
        Paused = true;
        if (!restored)
        {
            Hint("The simulation hit a numerical fault and has been paused. Reset the scene to carry on.", 8);
            return;
        }
        RebindAfterRestore();
        LastImpact = null;
        cinematicUntil = 0;
        OnImpactUndone();
        Hint("The simulation hit a numerical fault, so it was rolled back to before the last impact and paused.", 8);
    }

    /// <summary>
    /// The bookkeeping every outcome needs whether or not effects exist yet: remember it, move selection and focus off an
    /// impactor that no longer exists (app.ts handleOutcomes, minus effects, report and cinematic camera, which are the hook's).
    /// </summary>
    void ApplyOutcomes()
    {
        foreach (var o in Sim.Outcomes)
        {
            // A body a hole swallows lights its disc; a hole swallowing a hole radiates gravitational waves, not light.
            if (o.Target.Kind == BodyKind.BlackHole && o.Impactor.Kind != BodyKind.BlackHole) FeedHole(o.Target, o.Impactor.Mass, o.Impactor.Id);
            if (o.Merger is { } m) OnHoleMerger(o, m);
            LastImpact = o;
            LastImpactDeltaV = o.TargetDeltaV;
            if (ReferenceEquals(Selected, o.Impactor) || Selected == null) Select(o.Target);
            if (ReferenceEquals(Rig.Focus, o.Impactor)) Rig.Focus = o.Target;
        }
        Sim.Outcomes.Clear();
        BodiesVersion++;
    }

    HudInfo HudInfo() => new()
    {
        Jd = Universe.Jd,
        Warp = Warp,
        Rewind = Rewind,
        Paused = Paused,
        Fps = Fps,
        Bodies = Universe.Bodies.Count,
        Substeps = Sim.Substeps,
        Limited = Sim.WarpLimited,
        Mode = Mode.ToString().ToLowerInvariant(),
        Selected = Selected?.Name,
        Focus = Rig.Focus?.Name,
    };

    // ---------- hooks for later work; each is compiled away until a file implements it ----------

    /// <summary>Step 8: keep the camera over an impact site while the body rotates (cinematic camera task).</summary>
    partial void UpdateSiteLock(double now);
    /// <summary>Step 13: advance impact effects (flash, fireball, ejecta), parented to the struck body.</summary>
    partial void UpdateEffects(double dtReal);
    /// <summary>
    /// Step 13: spawn effects, show the report, start the cinematic camera for each new outcome. The list is cleared right
    /// after this returns; selection/focus bookkeeping is already done in <see cref="ApplyOutcomes"/>.
    /// </summary>
    partial void HandleOutcomes(List<ImpactOutcome> outcomes);
    /// <summary>Step 13: live hand-aim prediction, path line and markers (asteroid task).</summary>
    partial void UpdateAim();
    /// <summary>Step 14: every 4 s, release maps nobody has asked for (texture task; engine streaming may make it a no-op).</summary>
    partial void SweepTextures();
    /// <summary>Step 15: the full HUD (panels, inspector, report) at 8 Hz; the minimal readout is already updated.</summary>
    partial void UpdateHud();
    /// <summary>Step 16: focus-first texture forcing (MaxLodOverride + flush, spike M1b).</summary>
    partial void PumpTextureUploads();
}
