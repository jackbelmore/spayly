using System;
using System.Collections.Generic;
using System.Globalization;
using Godot;
using Spayly.Game.Render;
using Spayly.Sim;

namespace Spayly.Game.App;

/// <summary>
/// What asteroid and erase mode tell the HUD. The HUD implements it on <see cref="UI.Hud"/> (or sets
/// <see cref="App.HudSink"/>); until then aim text falls back to the hint line and the brush is simply not drawn.
/// </summary>
public interface IHudSink
{
    /// <summary>The aim readout under the asteroid panel; "" clears it.</summary>
    void SetAimText(string text);
    /// <summary>The erase brush cursor at a viewport point, <paramref name="radiusPx"/> px; null hides it.</summary>
    void SetBrush(Vector2? at, double radiusPx);
}

/// <summary>
/// Asteroid mode (app.ts startAim/updateAim/fireManual/fireAt/launch): a hand aim on the near-target plane with a live
/// predicted path, or a click on a body that auto-aims at it. Every number comes from Spayly.Sim's <see cref="Aim"/>;
/// nothing here integrates anything.
/// </summary>
public partial class App
{
    sealed class AimState
    {
        public Vec3 Spawn;
        public Vec3 Current;
        public Vector2 Start;
        public Vector2 At;
        public bool Dirty = true;
        /// <summary>km/s relative to <see cref="Primary"/>.</summary>
        public double Speed;
        public Body? Primary;
        public Vec3? Vel;
        public PredictResult? Result;
    }

    /// <summary>Where aim text and the brush go; defaults to the HUD when it implements <see cref="IHudSink"/>.</summary>
    public IHudSink? HudSink { get; set; }
    IHudSink? Sink => HudSink ?? (hud as IHudSink);

    AimState? aim;
    AimOverlay? aimOverlay;
    List<Vec3>? lastPath;
    double pathUntil;
    int asteroidCount;
    string aimText = "";

    /// <summary>What the last launch predicted, for the impact log.</summary>
    struct LaunchRecord
    {
        public string Id;
        public string? TargetId;
        public double SimTime;
        public double PredictedTime;
        public double PredictedSpeed;
        public int CratersBefore;
        public int VersionBefore;
        public int BodiesBefore;
    }

    LaunchRecord lastLaunch;

    void EnsureAimOverlay()
    {
        if (aimOverlay != null) return;
        aimOverlay = new AimOverlay();
        AddChild(aimOverlay);
    }

    void SetAimText(string text)
    {
        if (text == aimText) return;
        aimText = text;
        if (Sink is IHudSink s) s.SetAimText(text);
        else if (text.Length > 0) Hint(text, 2.5);
    }

    partial void StartAim(Vec3 spawn, Vector2 at)
    {
        aim = new AimState { Spawn = spawn, Current = spawn, Start = at, At = at, Primary = Universe.DominantBody(spawn) };
        aiming = true;
    }

    partial void MoveAim(Vec3 current, Vector2 at)
    {
        if (aim == null) return;
        aim.Current = current;
        aim.At = at;
        aim.Dirty = true;
    }

    partial void CancelAim()
    {
        aim = null;
        aiming = false;
        aimOverlay?.Clear();
        SetAimText("");
    }

    partial void UpdateAim()
    {
        // The brush belongs to erase mode; SetMode's hooks are the HUD's, so the brush is dropped here on the next frame.
        if (brushShown && Mode != Mode.Erase)
        {
            brushShown = false;
            Sink?.SetBrush(null, brushRadius);
        }
        EnsureAimOverlay();
        var overlay = aimOverlay!;
        var a = aim;
        if (a == null)
        {
            if (pathUntil > realTime && lastPath != null) overlay.ShowPath(lastPath, ctx.Origin, 0.8f);
            else
            {
                overlay.HidePath();
                if (overlay.HitVisible) overlay.HitVisible = false;
            }
            return;
        }
        var dragPx = a.At.DistanceTo(a.Start);
        var dirVec = a.Current - a.Spawn;
        overlay.PlaceSpawn(a.Spawn, 10, ctx);
        if (Vec3.Length(dirVec) < 1 || dragPx < 4)
        {
            a.Speed = 0;
            overlay.HidePath();
            overlay.HitVisible = false;
            SetAimText("Drag to set the direction; drag further for more speed");
            return;
        }
        a.Speed = Math.Clamp(Asteroid.SpeedKms * dragPx / 150, 0.3, 500);
        var prim = a.Primary ?? Star();
        a.Vel = prim.Vel + Vec3.Normalize(dirVec) * a.Speed;
        if (a.Dirty)
        {
            a.Result = Spayly.Sim.Aim.PredictPath(Universe, new Probe(a.Spawn, a.Vel.Value, Asteroid.DiameterM / 2000), 2 * Constants.Year,
                new PredictOptions { MaxSteps = 2500, MaxPoints = 600, PairBudget = 3e6 });
            a.Dirty = false;
        }
        var res = a.Result;
        if (res == null) return;
        overlay.ShowPath(res.Points, ctx.Origin, 1);
        if (res.Hit is PredictHit hit)
        {
            overlay.PlaceHit(hit.Pos, 14, ctx);
            var target = Universe.ById(hit.BodyId);
            SetAimText($"{FormatSpeed(a.Speed)} relative to {prim.Name} → hits {target?.Name ?? hit.BodyId} in {FormatDuration(hit.Time)} at {FormatSpeed(hit.Speed)}");
        }
        else
        {
            overlay.HitVisible = false;
            SetAimText($"{FormatSpeed(a.Speed)} relative to {prim.Name} · no impact within {FormatDuration(res.Duration)}");
        }
    }

    partial void FireManual()
    {
        var a = aim;
        if (a?.Vel is Vec3 vel && a.Speed > 0)
        {
            cinematicUntil = 0;
            var hit = a.Result?.Hit;
            var target = hit is PredictHit h ? Universe.ById(h.BodyId) : null;
            // A shower needs something to fall on: a hand aim that hits nothing flies one rock.
            if (ShowerRocksWanted() is > 0 and var n && target != null && hit is PredictHit ph)
                FireShower(a.Spawn, vel, target, ph.Time, n);
            else RecordLaunch(Launch(a.Spawn, vel, target), target, hit);
            lastPath = a.Result?.Points;
            pathUntil = realTime + 3;
        }
        CancelAim();
    }

    partial void FireAt(Body target) => FireAtBody(target, ShowerRocksWanted());

    /// <summary>Auto-aim at <paramref name="target"/> from the camera and launch one rock, or a shower of <paramref name="showerRocks"/> when above 0.</summary>
    void FireAtBody(Body target, int showerRocks)
    {
        EnsureAimOverlay();
        if (showerRocks > 0 && Asteroid.Composition == Composition.Star)
        {
            Hint("A star is fired whole: meteor showers are for rocks", 3);
            showerRocks = 0;
        }
        var astR = (showerRocks > 0 ? Shower.PieceDiameter(Asteroid.DiameterM, Shower.Clamp(showerRocks)) : Asteroid.DiameterM) / 2000;
        var from = LaunchPoint();
        var res = Spayly.Sim.Aim.AutoAim(Universe, from, target.Id, Asteroid.SpeedKms, astR);
        if (res == null) return;
        cinematicUntil = 0;
        if (showerRocks > 0)
        {
            // Unconverged, the aimed shot still flies; its flight time is then the straight-line estimate.
            var flight = res.Prediction.Hit is PredictHit sh ? sh.Time : Vec3.Length(target.Pos - from) / Math.Max(Asteroid.SpeedKms, 1e-3);
            FireShower(from, res.Vel, target, flight, showerRocks);
            lastPath = res.Prediction.Points;
            pathUntil = realTime + 4;
            return;
        }
        var ast = Launch(from, res.Vel, target);
        RecordLaunch(ast, target, res.Prediction.Hit);
        // A giant impactor spawned at the viewpoint would swallow the camera; back off to watch it fall.
        if (Vec3.Length(Rig.Position - ast.Pos) < astR * 1.8)
            Rig.FlyTo(new ViewSpec { Focus = target, SetFocus = true, Distance = Math.Max(Rig.Distance, Vec3.Length(from - target.Pos) * 1.7) }, 1.2);
        lastPath = res.Prediction.Points;
        pathUntil = realTime + 4;
        var hit = res.Prediction.Hit;
        GD.Print(string.Create(CultureInfo.InvariantCulture,
            $"[fire] {ast.Id} '{ast.Name}' at {target.Id}: d={Asteroid.DiameterM:G4} m {Asteroid.Composition} v={Asteroid.SpeedKms:G4} km/s mass={ast.Mass:G4} kg from {Vec3.Length(from - target.Pos):G6} km converged={res.Converged} predicted_hit={(hit is PredictHit ph ? $"{ph.BodyId} in {ph.Time:F1} s at {ph.Speed:F3} km/s" : "none")} pairEvals={res.PairEvals:G3} bodies={Universe.Bodies.Count} {MomentumLedger()}"));
        if (res.Converged && hit is PredictHit hh)
            Hint($"{ast.Name} away! Hits {target.Name} in {FormatDuration(hh.Time)} at {FormatSpeed(hh.Speed)}. Warp time to skip ahead.", 5);
        else
            Hint($"{ast.Name} launched toward {target.Name}; gravity may bend it off course", 4);
    }

    /// <summary>Realism entry for the Speed slider's markers; the HUD calls this when it builds the Realism panel.</summary>
    public static void RegisterLaunchRealism()
    {
        Realism.Register("Launch speed markers", RealismTag.Sci,
            "The Speed slider marks the circular-orbit speed √(μ r)/(r − r_s) and escape speed √(2μ/(r − r_s)) at the launch point relative to the body aimed at, the Paczyński–Wiita values near a black hole and Newton's elsewhere, counting only that body's pull.",
            RealismArea.UI);
    }

    /// <summary>Where click-to-fire launches from, km: the camera, pushed out of any body it sits in.</summary>
    Vec3 LaunchPoint()
    {
        var astR = Asteroid.DiameterM / 2000;
        // Spawning inside a body (the camera sits 3 radii above a planet) would be an instant collision.
        var from = Rig.Position;
        foreach (var b in Universe.Bodies)
        {
            var d = from - b.Pos;
            var min = b.Radius * 1.6 + astR * 1.8;
            if (Vec3.Length(d) < min) from = b.Pos + Vec3.Normalize(d) * min;
        }
        return from;
    }

    /// <summary>
    /// The Speed slider's markers (M6.3) for a click-to-fire at <paramref name="target"/>, or else the body under the pointer,
    /// or else the selected one: the circular-orbit and escape speeds relative to it at the launch point, from
    /// <see cref="Spayly.Sim.Aim.LaunchSpeeds"/>. Null when there is no such body or the launch point is inside its horizon.
    /// </summary>
    public SpeedMarkers? LaunchMarkers(Body? target = null)
    {
        target ??= (holePointer is Vector2 pointer ? PickBody(pointer) : null) ?? Selected;
        if (target == null) return null;
        var a = Asteroid;
        var mass = Presets.Density(a.Composition) * (Math.PI / 6) * Math.Pow(a.DiameterM, 3);
        var (d, vc, ve) = Spayly.Sim.Aim.LaunchSpeeds(target, LaunchPoint(), mass);
        if (!double.IsFinite(vc) || !double.IsFinite(ve)) return null;
        var star = a.Composition == Composition.Star;
        var hole = target.Kind == BodyKind.BlackHole;
        var thing = star ? "the star" : "the rock";
        var at = $"{UI.Format.Distance(d)} from {target.Name}";
        return new SpeedMarkers(target.Name, d, vc, ve,
            $"Circular-orbit speed {at}: slower than this, {thing} drops in steeply on a tightly bound orbit.",
            star && hole
                ? $"Escape speed {at}: a star arriving this fast is on a parabolic orbit, so about half of its torn-apart gas escapes, the classic tidal disruption."
                : $"Escape speed {at}: faster than this, {thing} would never come back if it missed {target.Name}.");
    }

    /// <summary>Make the asteroid the panel describes and add it to the universe (app.ts launch). Pos km, vel km/s.</summary>
    Body Launch(Vec3 pos, Vec3 vel, Body? target)
    {
        var a = Asteroid;
        asteroidCount++;
        var star = a.Composition == Composition.Star;
        var name = $"{a.Name ?? "Asteroid"} {asteroidCount}";
        // Forced: a launch is a deliberate act, like placing a hole, and the collision cascade clock must not swallow it.
        RecordUndo(UndoKind.Launch, $"launching {name}");
        var b = NewRock(star ? $"star-launch-{asteroidCount}" : $"ast-{asteroidCount}", name, a.DiameterM, a.Composition, pos, vel, target);
        Universe.Add(b);
        SyncViews();
        return b;
    }

    /// <summary>A rock (or star) of <paramref name="diameterM"/> m and the given composition, not yet added. Pos km, vel km/s.</summary>
    Body NewRock(string id, string name, double diameterM, Composition composition, Vec3 pos, Vec3 vel, Body? target)
    {
        var d = diameterM;
        var rho = Presets.Density(composition);
        var mass = rho * (Math.PI / 6) * Math.Pow(d, 3);
        // A launched star is a star: it shines, it is fluid for the Roche test, and a black hole can tear it apart.
        var star = composition == Composition.Star;
        var sun = Star();
        return Body.MakeBody(new BodySpec
        {
            Id = id,
            Name = name,
            Kind = star ? BodyKind.Star : BodyKind.Asteroid,
            Surface = star ? Surface.Star : composition == Composition.Ice ? Surface.Ice : Surface.Rock,
            Mass = mass,
            Radius = d / 2000,
            Pos = pos,
            Vel = vel,
            PrimaryId = target?.Id,
            Rotation = star ? sun.Rotation : null,
            Visual = star ? sun.Visual : new BodyVisual { Color = Presets.Color(composition), Albedo = composition == Composition.Ice ? 0.5 : 0.1 },
            Born = Universe.Time,
        });
    }

    void RecordLaunch(Body ast, Body? target, PredictHit? hit)
    {
        lastLaunch = new LaunchRecord
        {
            Id = ast.Id,
            TargetId = target?.Id,
            SimTime = Universe.Time,
            PredictedTime = hit?.Time ?? double.NaN,
            PredictedSpeed = hit?.Speed ?? double.NaN,
            CratersBefore = target?.Damage.Craters.Count ?? 0,
            VersionBefore = target?.Damage.Version ?? 0,
            BodiesBefore = Universe.Bodies.Count - 1,
        };
    }

    /// <summary>
    /// Σ m·v over every body (kg·km/s) and its size relative to Σ |m·v|: mutual gravity and Collide's momentum exchange both
    /// conserve it, so it only drifts by round-off, and only an added or removed body moves it.
    /// </summary>
    string MomentumLedger()
    {
        Vec3 p = Vec3.Zero;
        double abs = 0;
        foreach (var b in Universe.Bodies)
        {
            p += b.Vel * b.Mass;
            abs += Vec3.Length(b.Vel) * b.Mass;
        }
        return string.Create(CultureInfo.InvariantCulture, $"sum_p=({p.X:G9},{p.Y:G9},{p.Z:G9}) |sum_p|/sum|p|={Vec3.Length(p) / Math.Max(abs, 1e-300):G4}");
    }

    static string FormatSpeed(double kms) => kms < 1
        ? string.Create(CultureInfo.InvariantCulture, $"{kms * 1000:G3} m/s")
        : string.Create(CultureInfo.InvariantCulture, $"{kms:G3} km/s");

    static string FormatDuration(double s)
    {
        var a = Math.Abs(s);
        var c = CultureInfo.InvariantCulture;
        return a < 90 ? string.Create(c, $"{s:G3} s")
            : a < 5400 ? string.Create(c, $"{s / 60:G3} min")
            : a < 2 * Constants.Day ? string.Create(c, $"{s / 3600:G3} h")
            : a < 2 * Constants.Year ? string.Create(c, $"{s / Constants.Day:G3} days")
            : string.Create(c, $"{s / Constants.Year:G3} years");
    }
}

/// <summary>The Speed slider's markers: target, launch distance km, circular and escape speeds km/s, one tooltip each.</summary>
public sealed record SpeedMarkers(string Target, double DistanceKm, double Circular, double Escape, string CircularTip, string EscapeTip);
