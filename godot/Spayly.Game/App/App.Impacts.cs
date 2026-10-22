using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using Godot;
using Spayly.Game.Render;
using Spayly.Sim;

namespace Spayly.Game.App;

/// <summary>
/// What an impact looks like and where the camera goes (app.ts handleOutcomes/lookAtImpact/updateSiteLock): effects parented
/// to the struck body, the <see cref="ImpactOccurred"/> event for the HUD's report, and the cinematic camera, which flies to
/// the site and then holds it in body-fixed coordinates while the body turns, until 9 s pass or the player takes the camera.
/// Also injects the Earth ocean mask into Collide's <c>SurfaceAt</c>, so a sea impact is modelled as one.
/// </summary>
public partial class App
{
    /// <summary>Real seconds the cinematic camera holds the site (and caps warp at 1).</summary>
    const double CinematicSeconds = 9;
    const string OceanMaskPath = "res://textures/earth_specular.jpg";
    /// <summary>Ocean depth handed to the impact model for a sea impact, m (the web's constant).</summary>
    const double OceanDepthM = 3700;

    sealed class SiteLock
    {
        public required Body Body;
        public Vec3 DirBody;
        public double Until;
    }

    ImpactEffects? effects;
    SiteLock? siteLock;
    volatile SurfaceMask? oceanMask;
    bool impactsReady;
    bool maskLogged;
    double cinematicStart = double.NegativeInfinity;
    int cinematicLogStage = int.MaxValue;
    static readonly double[] CinematicLogAt = [0, 0.6, 1.2, 3, 6, 8.9];

    /// <summary>First-frame setup; the skeleton has no Ready hook for this file, so it runs from the first UpdateEffects.</summary>
    void EnsureImpacts()
    {
        if (impactsReady) return;
        impactsReady = true;
        effects = new ImpactEffects();
        AddChild(effects);
        ImpactOccurred += o => GD.Print($"[impact] ImpactOccurred fired: {o.Kind} {o.Impactor.Id} -> {o.Target.Id}");
        ImpactUndone += () => GD.Print($"[impact] ImpactUndone fired: bodies={Universe.Bodies.Count}");
        var path = ProjectSettings.GlobalizePath(OceanMaskPath);
        if (!File.Exists(path))
        {
            GD.Print($"[mask] {OceanMaskPath} missing; every impact reads as land");
            return;
        }
        // Water only on Earth, the one body with a specular ocean map; a missing or still-loading mask reads as land.
        Sim.SurfaceAt = (b, d) => b.Id == "earth" && oceanMask is SurfaceMask m && m.IsWater(d) ? (true, OceanDepthM) : (false, 0);
        // An 8K JPEG decodes in a few hundred ms; off the main thread so the first frames do not hitch.
        Task.Run(() =>
        {
            try { oceanMask = SurfaceMask.Load(path); }
            catch (Exception e) { GD.PushError($"[mask] {path}: {e.Message}"); }
        });
    }

    partial void UpdateEffects(double dtReal)
    {
        EnsureImpacts();
        if (!maskLogged && oceanMask is SurfaceMask m)
        {
            maskLogged = true;
            var water = 0;
            for (var i = 0; i < 64; i++)
            for (var j = 0; j < 32; j++)
            {
                // Equal-area sample: uniform in longitude and in sin(latitude).
                var lon = (i + 0.5) / 64 * 2 * Math.PI;
                var z = (j + 0.5) / 32 * 2 - 1;
                var r = Math.Sqrt(1 - z * z);
                if (m.IsWater(new Vec3(r * Math.Cos(lon), z, r * Math.Sin(lon)))) water++;
            }
            GD.Print(string.Create(CultureInfo.InvariantCulture, $"[mask] ocean mask {SurfaceMask.Width}x{SurfaceMask.Height} loaded, water fraction {water / 2048.0:F3}"));
        }
        effects!.Update(dtReal, ctx, views);
        LogCinematic();
    }

    partial void HandleOutcomes(List<ImpactOutcome> outcomes)
    {
        EnsureImpacts();
        DebrisOnOutcomes(outcomes);
        foreach (var o in outcomes)
        {
            if (!views.TryGetValue(o.Target.Id, out var view)) continue;
            var velDirBody = Render.Orientation.WorldToBodyFrame(o.Target, o.VelDirWorld, Universe.Jd);
            // Two holes merging in vacuum emit gravitational waves and no light: no flash, fireball or ring.
            if (o.Merger == null) effects!.Spawn(o, view, velDirBody, ctx);
            if (view is SunView sun) sun.Flare = 1.5;
            LastImpact = o;
            LastImpactDeltaV = o.TargetDeltaV;
            LogImpact(o);
            RaiseImpact(o);
            // A shower's camera flies to its first impact only, and wide, without holding the warp: the rest land in view.
            if (OnShowerImpact(o) && Settings.Cinematic)
            {
                if (!IsShowerRock(o.Impactor)) cinematicUntil = realTime + CinematicSeconds;
                LookAtImpact(o, view);
            }
            Hint($"{o.Impactor.Name} hit {o.Target.Name} at {FormatSpeed(o.RelSpeed)}", 4);
        }
    }

    partial void LookAtImpact(ImpactOutcome o, BodyView view)
    {
        // A coincident pair has no contact normal (ImpactEffects.SiteDir): look from where the camera already is.
        var n = Vec3.Length(o.NormalWorld) > 0 ? o.NormalWorld : Vec3.Normalize(Rig.Position - o.Target.Pos);
        var dirBody = Vec3.Length(o.DirBody) > 0 ? o.DirBody : Render.Orientation.WorldToBodyFrame(o.Target, n, Universe.Jd);
        var r = view.VisualRadiusKm;
        var rt = o.Target.Radius;
        var rf = (o.Report?.FireballRadius ?? 0) / 1000;
        var d = (o.Report?.Crater?.FinalDiameter ?? 0) / 1000;
        var shattered = o.Kind is OutcomeKind.Shatter or OutcomeKind.Disperse;
        // High enough to take in the fireball and the crater, low enough that the site fills the frame.
        // A hole has no surface to hover over: 1.12 r_s would put the camera inside its shadow. Stand FlyAbove's 40 r_s off.
        var altitude = o.Target.Kind == BodyKind.BlackHole ? 39 * rt
            : IsShowerRock(o.Impactor) ? 1.3 * rt
            : shattered ? 3.5 * rt : Math.Clamp(Math.Max(rf * 18, d * 5), 0.12 * rt, 0.7 * rt);
        var dist = r * (1 + altitude / rt);
        var hasSite = Vec3.Length(n) > 0;
        siteLock = hasSite ? new SiteLock { Body = o.Target, DirBody = dirBody, Until = realTime + CinematicSeconds } : null;
        Rig.FlyTo(new ViewSpec
        {
            Focus = o.Target, SetFocus = true, Distance = dist,
            Yaw = hasSite ? CameraRig.YawTowards(n) : null, Pitch = hasSite ? Math.Asin(Math.Clamp(n.Z, -1, 1)) : null,
        }, 1.2);
        cinematicStart = realTime;
        cinematicLogStage = 0;
        GD.Print(string.Create(CultureInfo.InvariantCulture,
            $"[cinematic] look at {o.Target.Id}: altitude={altitude:G4} km goal_distance={dist:G6} km from distance={Rig.Distance:G6} km fov={Rig.Fov:F1}"));
    }

    partial void UpdateSiteLock(double now)
    {
        var lk = siteLock;
        if (lk == null) return;
        if (now > lk.Until || !ReferenceEquals(Rig.Focus, lk.Body) || Universe.ById(lk.Body.Id) != lk.Body)
        {
            siteLock = null;
            return;
        }
        if (Rig.Flying) return;
        var dirSim = Render.Orientation.BodyFrameToWorld(lk.Body, lk.DirBody, Universe.Jd);
        Rig.Yaw = Rig.GoalYaw = CameraRig.YawTowards(dirSim);
        Rig.Pitch = Rig.GoalPitch = Math.Asin(Math.Clamp(dirSim.Z, -1, 1));
    }

    partial void ClearSiteLock() => siteLock = null;

    partial void OnImpactUndone()
    {
        siteLock = null;
        effects?.Clear();
        RaiseImpactUndone();
    }

    /// <summary>A few samples of the camera over the cinematic window, for scripted checks that it moved.</summary>
    void LogCinematic()
    {
        if (cinematicLogStage >= CinematicLogAt.Length) return;
        var age = realTime - cinematicStart;
        if (age < CinematicLogAt[cinematicLogStage]) return;
        cinematicLogStage++;
        var f = Rig.Focus;
        var alt = f != null ? Vec3.Length(Rig.Position - f.Pos) - f.Radius : double.NaN;
        GD.Print(string.Create(CultureInfo.InvariantCulture,
            $"[cinematic] t={age:F2}s distance={Rig.Distance:G6} km altitude={alt:G6} km fov={Rig.Fov:F2} yaw={Rig.Yaw:F3} pitch={Rig.Pitch:F3} flying={Rig.Flying} site_lock={siteLock != null} warp_capped={realTime < cinematicUntil}"));
    }

    void LogImpact(ImpactOutcome o)
    {
        var rep = o.Report;
        var dmg = o.Target.Damage;
        var launched = lastLaunch.Id == o.Impactor.Id;
        var water = o.Target.Id == "earth" && oceanMask is SurfaceMask m ? (m.IsWater(o.DirBody) ? "water" : "land") : "n/a";
        var ttiPred = launched ? lastLaunch.PredictedTime : double.NaN;
        var ttiSim = launched ? o.Time - lastLaunch.SimTime : double.NaN;
        var cratersBefore = launched && lastLaunch.TargetId == o.Target.Id ? lastLaunch.CratersBefore.ToString(CultureInfo.InvariantCulture) : "?";
        var versionBefore = launched && lastLaunch.TargetId == o.Target.Id ? lastLaunch.VersionBefore.ToString(CultureInfo.InvariantCulture) : "?";
        GD.Print(string.Create(CultureInfo.InvariantCulture,
            $"[impact] frame={Engine.GetProcessFrames()} kind={o.Kind} {o.Impactor.Id} -> {o.Target.Id} rel={o.RelSpeed:F3} km/s angle={o.Angle * 180 / Math.PI:F1} deg E={o.Energy:G4} J tti_pred={ttiPred:F1} s tti_sim={ttiSim:F1} s surface={water}"));
        GD.Print(string.Create(CultureInfo.InvariantCulture,
            $"[impact] report: class='{rep?.Classification}' airburst={rep?.Entry.Airburst} burst_alt={rep?.Entry.BurstAltitude:G4} m crater_D={rep?.Crater?.FinalDiameter:G4} m fireball_R={rep?.FireballRadius:G4} m deposited={rep?.EnergyDeposited:G4} J ({rep?.EnergyMT:G4} Mt)"));
        GD.Print(string.Create(CultureInfo.InvariantCulture,
            $"[impact] damage {o.Target.Id}: craters {cratersBefore}->{dmg.Craters.Count} version {versionBefore}->{dmg.Version} hits={dmg.Hits} heat={dmg.Heat.Count} magma={dmg.Magma:G3} fragments={o.Fragments.Count} bodies {(launched ? lastLaunch.BodiesBefore + 1 : -1)}->{Universe.Bodies.Count} target_dv={o.TargetDeltaV:G4} km/s effects={effects?.Count} {MomentumLedger()}"));
    }

    /// <summary>Capture verbs for asteroid, erase and impact checks; App.Capture routes them here.</summary>
    bool ImpactAction(string verb, string arg)
    {
        double[] Nums() => Array.ConvertAll(arg.Split(','), s => double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture));
        switch (verb)
        {
            case "fireAt":
                if (Universe.ById(arg) is Body t) FireAt(t);
                else GD.PushError($"[fire] no body '{arg}'");
                return true;
            case "setAsteroid":
            {
                var p = arg.Split(',');
                Asteroid.DiameterM = double.Parse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture);
                if (p.Length > 1) Asteroid.Composition = Enum.Parse<Composition>(p[1], true);
                if (p.Length > 2) Asteroid.SpeedKms = double.Parse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture);
                Asteroid.Name = null;
                hud.SyncAsteroid();
                return true;
            }
            case "applyPreset":
                foreach (var pr in Presets.All)
                {
                    if (!string.Equals(pr.Name, arg, StringComparison.OrdinalIgnoreCase)) continue;
                    ApplyPreset(pr);
                    hud.SyncAsteroid();
                    return true;
                }
                GD.PushError($"[fire] no preset '{arg}'");
                return true;
            case "erase":
            {
                var n = Nums();
                if (n.Length > 2) brushRadius = n[2];
                var count = 0;
                var before = Universe.Bodies.Count;
                eraseStrokeSaved = false;
                EraseAt(new Vector2((float)n[0], (float)n[1]), ref count);
                GD.Print($"[erase] at {n[0]},{n[1]} r={brushRadius} erased={count} bodies {before}->{Universe.Bodies.Count} version={BodiesVersion}");
                return true;
            }
            // Hand aim by script: press on empty space, drag, release (what PointerDown/Move/Up do in asteroid mode).
            case "aimStart":
            {
                var n = Nums();
                var at = new Vector2((float)n[0], (float)n[1]);
                if (AimPlanePoint(at) is Vec3 p) StartAim(p, at);
                GD.Print($"[aim] start at {at} aiming={aiming}");
                return true;
            }
            case "aimMove":
            {
                var n = Nums();
                var at = new Vector2((float)n[0], (float)n[1]);
                if (aiming && AimPlanePoint(at) is Vec3 p) MoveAim(p, at);
                return true;
            }
            case "aimFire":
                GD.Print($"[aim] fire: {aimText}");
                FireManual();
                return true;
            // logMarkers[:id]: the Speed slider's markers for a body (else the pointer's or the selected one) and where they sit.
            case "logMarkers":
            {
                var m = LaunchMarkers(arg.Length > 0 ? Universe.ById(arg) : null);
                if (arg.Length > 0 || m != null) hud.SetSpeedMarkers(m);
                GD.Print(m == null ? "[markers] none" : string.Create(CultureInfo.InvariantCulture,
                    $"[markers] {m.Target} d={m.DistanceKm:G6} km circular={m.Circular:G6} km/s escape={m.Escape:G6} km/s {hud.SpeedMarkerSummary} | {m.CircularTip} | {m.EscapeTip}"));
                return true;
            }
            // speedAtMarker:id,circular|escape: set the next launch speed to that marker (what a click on the tick would mean).
            case "speedAtMarker":
            {
                var p = arg.Split(',');
                var m = Universe.ById(p[0]) is Body mt ? LaunchMarkers(mt) : null;
                if (m == null)
                {
                    GD.PushError($"[markers] none for '{p[0]}'");
                    return true;
                }
                Asteroid.SpeedKms = p.Length > 1 && p[1] == "circular" ? m.Circular : m.Escape;
                hud.SyncAsteroid();
                GD.Print(string.Create(CultureInfo.InvariantCulture, $"[markers] speed set to {(p.Length > 1 ? p[1] : "escape")} {Asteroid.SpeedKms:G6} km/s for {m.Target}"));
                return true;
            }
            case "logImpact":
            {
                var b = Universe.ById(arg.Length > 0 ? arg : "earth");
                GD.Print(string.Create(CultureInfo.InvariantCulture,
                    $"[impact] state: bodies={Universe.Bodies.Count} version={BodiesVersion} {b?.Id}: craters={b?.Damage.Craters.Count} damage_version={b?.Damage.Version} hits={b?.Damage.Hits} last={LastImpact?.Kind.ToString() ?? "none"} effects={effects?.Count} camera_distance={Rig.Distance:G6} km fov={Rig.Fov:F2} {MomentumLedger()}"));
                return true;
            }
            default:
                return false;
        }
    }
}
