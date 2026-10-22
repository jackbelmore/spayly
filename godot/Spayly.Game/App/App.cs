using System;
using System.Collections.Generic;
using Godot;
using Spayly.Game.Capture;
using Spayly.Game.Render;
using Spayly.Game.Sim;
using Spayly.Game.UI;
using Spayly.Sim;

namespace Spayly.Game.App;

/// <summary>
/// The application: owns the sim runner, the views, the camera rig and the HUD, and runs the frame; port of src/app/app.ts.
/// A partial class split by concern so later work adds a file rather than editing this one:
///   App.cs         fields, settings, the public API (the web's <c>window.spayly</c>)
///   App.Tick.cs    the 16-step frame, with named hooks for later work
///   App.Input.cs   pointer and key bindings for every mode
///   App.Views.cs   SyncViews / CreateView / RemoveView / RebindAfterRestore
///   App.Sun.cs     sun state from live mass, auto-exposure, the Sun's light
///   App.Lines.cs   orbit conics relative to the dynamic primary
///   App.Labels.cs  label occlusion and declutter
///   App.Capture.cs scripted actions and the jitter probe for CaptureDriver
/// </summary>
public partial class App : Node3D, ICaptureTarget
{
    public SimRunner Sim { get; private set; } = null!;
    public Universe Universe => Sim.Universe;
    public CameraRig Rig { get; private set; } = null!;
    public readonly Settings Settings = new();
    public readonly AsteroidSettings Asteroid = new();
    public Mode Mode { get; private set; } = Mode.Explore;
    public Body? Selected { get; private set; }
    /// <summary>Sim seconds per real second, in [1, 1e8].</summary>
    public double Warp { get; private set; } = 1;
    public bool Paused { get; private set; }
    public bool Rewind { get; private set; }
    public double Fps { get; private set; }
    /// <summary>Bumped whenever bodies appear, vanish or are rebuilt; the HUD's body list keys off it.</summary>
    public int BodiesVersion { get; private set; }
    public double Exposure { get; private set; } = 1;
    public ImpactOutcome? LastImpact { get; private set; }
    public double LastImpactDeltaV { get; private set; }

    readonly Dictionary<string, BodyView> views = [];
    readonly ViewContext ctx = new();
    Camera3D camera = null!;
    Godot.Environment environment = null!;
    OmniLight3D sunLight = null!;
    Labels labels = null!;
    Hud hud = null!;
    Render.Sky sky = null!;
    Belt belt = null!;

    double dioramaAmount;
    double realTime;
    /// <summary>Real time (s) until which the cinematic camera caps warp at 1.</summary>
    double cinematicUntil;
    double hudTimer;
    double textureSweep;
    int frames;
    double fpsTime;

    public override void _Ready()
    {
        environment = new Godot.Environment
        {
            TonemapMode = Godot.Environment.ToneMapper.Aces,
            AmbientLightSource = Godot.Environment.AmbientSource.Color,
            // A whisper of fill so a night side reads as a body, not a hole; the web's shaders do the same.
            AmbientLightColor = new Color(1, 1, 1),
            AmbientLightEnergy = 0.012f,
        };
        Meshes.WarmRocks();
        sky = new Render.Sky(environment);
        AddChild(new WorldEnvironment { Environment = environment });

        camera = new Camera3D { Current = true, Fov = (float)CameraRig.WideFov };
        AddChild(camera);
        Rig = new CameraRig(camera);

        sunLight = new OmniLight3D { ShadowEnabled = false, LightSpecular = 0, OmniRange = 1e9f };
        AddChild(sunLight);

        labels = new Labels(camera)
        {
            // app.ts bodyActions
            Select = b => Select(b),
            Activate = b =>
            {
                Select(b);
                FlyAbove(b);
            },
        };
        AddChild(labels);
        hud = new Hud();
        AddChild(hud);

        Sim = new SimRunner(Settings.Moons, DateTime.UtcNow);
        belt = new Belt(Universe.Particles);
        ctx.Jd = Universe.Jd;
        ctx.Bodies = Universe.Bodies;

        SyncViews();
        EnsureUndo();
        WarmSph();
        var earth = Universe.ById("earth")!;
        Select(earth);
        FlyAbove(earth, true);
        TakeStartScene();
        Hint("Drag or Q/E to orbit, WASD to pan, scroll to zoom, X for asteroid mode, H for help", 6);
    }

    // ---------- public API (the web's window.spayly) ----------

    public Body? BodyById(string id) => Universe.ById(id);

    public BodyView? ViewFor(Body b) => views.GetValueOrDefault(b.Id);

    /// <summary>
    /// The body <paramref name="b"/> orbits, for the inspector and the orbit lines. A catalogued planet or moon keeps its
    /// catalogued primary while still bound to it; otherwise the physics rule (nearest heavier body it is bound to), which
    /// alone would swing a heavy moon's neighbours' lines between planet and moon frame to frame.
    /// </summary>
    public Body? PrimaryOf(Body b)
    {
        if ((b.Kind == BodyKind.Moon || b.Kind == BodyKind.Planet) && b.PrimaryId != null)
        {
            var p = Universe.ById(b.PrimaryId);
            if (p != null && !ReferenceEquals(p, b) && p.Mass > b.Mass)
            {
                var r = Vec3.Length(b.Pos - p.Pos);
                var v = b.Vel - p.Vel;
                if (r > 0 && Vec3.Dot(v, v) / 2 < Constants.G * (p.Mass + b.Mass) / r) return p;
            }
        }
        return Universe.PrimaryOf(b);
    }

    public void Select(string id) => Select(Universe.ById(id));

    public void Select(Body? b)
    {
        Selected = b;
        OnSelectionChanged();
    }

    public void Follow(string id)
    {
        if (Universe.ById(id) is Body b) Follow(b);
    }

    public void Follow(Body b) => Rig.FlyTo(new ViewSpec { Focus = b, SetFocus = true, Distance = Math.Max(Rig.Distance, b.Radius * 3) }, 1.2);

    public void FlyAbove(string id, bool instant = false)
    {
        if (Universe.ById(id) is Body b) FlyAbove(b, instant);
    }

    public void FlyAbove(Body b, bool instant = false)
    {
        // The true radius: the diorama is switched off for close-ups and far bodies carry an inflated on-screen radius.
        var r = b.Radius;
        var sun = Star();
        var yaw = Rig.GoalYaw;
        if (!ReferenceEquals(b, sun)) yaw = CameraRig.YawTowards(Vec3.Normalize(sun.Pos - b.Pos)) + 0.55;
        // A black hole's r_s is its horizon: from 4.2 r_s the shadow alone would fill 65 degrees, and the disc reaches 20 r_s.
        var spec = new ViewSpec { Focus = b, SetFocus = true, Distance = b.Kind == BodyKind.Star ? r * 6 : b.Kind == BodyKind.BlackHole ? r * 40 : r * 4.2, Yaw = yaw, Pitch = 0.3 };
        Settings.Diorama = false;
        if (instant) Rig.Set(spec);
        else Rig.FlyTo(spec, 1.6);
    }

    public void SetWarp(double w)
    {
        Warp = Math.Clamp(w, 1, 1e8);
        Paused = false;
    }

    public void TogglePause() => Paused = !Paused;

    public void ToggleRewind()
    {
        Rewind = !Rewind;
        Hint(Rewind ? "Rewinding. Gravity runs backwards exactly; impacts are undone from the report card." : "Time runs forward again");
    }

    public void SetMode(Mode m)
    {
        Mode = m;
        if (m != Mode.Asteroid)
        {
            CancelAim();
            aiming = false;
        }
        if (m != Mode.BlackHole) CancelHoleDrag();
        OnModeChanged(m);
        if (m == Mode.Asteroid) Hint("Asteroid mode: click any body — planet, moon or rock you fired earlier — to shoot at it, or drag on empty space to aim by hand. Right-drag orbits.", 5.5);
        if (m == Mode.Erase) Hint("Erase mode: drag over the view to delete asteroids and fragments. Wheel resizes the brush; right-drag still orbits.", 5.5);
        if (m == Mode.BlackHole) Hint("Black-hole mode: pick a mass below, then click the view to place a hole on the plane through what you are looking at. Right-drag orbits; Undo takes it back.", 5.5);
    }

    public void SetDiorama(bool on) => Settings.Diorama = on;

    public void ViewTabletop()
    {
        Settings.Diorama = true;
        Rig.FlyTo(new ViewSpec { Focus = Star(), SetFocus = true, Distance = 36 * Constants.AU, Pitch = 0.78 }, 2.2);
    }

    public void ViewInner()
    {
        Settings.Diorama = true;
        Rig.FlyTo(new ViewSpec { Focus = Star(), SetFocus = true, Distance = 2.7 * Constants.AU, Pitch = 0.62 }, 2);
    }

    public void ViewOuter()
    {
        Settings.Diorama = true;
        Rig.FlyTo(new ViewSpec { Focus = Star(), SetFocus = true, Distance = 75 * Constants.AU, Pitch = 0.9 }, 2.2);
    }

    /// <summary>Put a body's mass and radius back to the catalogue values; an undo point first when that changes anything.</summary>
    public void ResetBody(string id)
    {
        if (Universe.ById(id) is not Body b) return;
        if (b.Mass != b.Mass0 || b.Radius != b.Radius0) RecordUndo(UndoKind.Reset, $"resetting {b.Name}", b.Id);
        b.Mass = b.Mass0;
        b.Radius = b.Radius0;
        OnSelectionChanged();
    }

    /// <summary>Next asteroid's diameter, composition and speed from a preset. Firing it is the asteroid task's.</summary>
    public void ApplyPreset(Preset p)
    {
        Asteroid.DiameterM = p.DiameterM;
        Asteroid.Composition = p.Composition;
        Asteroid.SpeedKms = p.SpeedKms;
        Asteroid.Name = p.Name;
    }

    /// <summary>Fly to the last impact site; the cinematic-camera task fills <see cref="LookAtImpact"/>.</summary>
    public void LookAtLastImpact()
    {
        var o = LastImpact;
        if (o == null) return;
        if (views.TryGetValue(o.Target.Id, out var view)) LookAtImpact(o, view);
    }

    /// <summary>Add or drop satellites; focus and selection move to the primary of anything removed. An undo point first.</summary>
    public void SetMoonDetail(MoonDetail level)
    {
        if (level != Settings.Moons) RecordUndo(UndoKind.Moons, $"setting moons to {level.ToString().ToLowerInvariant()}");
        Settings.Moons = level;
        Sim.SetMoonDetail(level, present =>
        {
            var primary = present.PrimaryId != null ? Universe.ById(present.PrimaryId) : null;
            if (ReferenceEquals(Rig.Focus, present)) Rig.Focus = primary;
            if (ReferenceEquals(Selected, present)) Select(primary);
        });
        SyncViews();
    }

    /// <summary>A transient message for the player, seconds on screen.</summary>
    public void Hint(string text, double seconds = 3) => hud?.Hint(text, seconds);

    Body Star() => Universe.ById("sun") ?? Universe.Bodies.Find(b => b.Kind == BodyKind.Star) ?? Universe.Bodies[0];

    // ---------- hooks for the HUD task (App.Hud*.cs) ----------

    /// <summary>Selection or the selected body's parameters changed: the inspector re-syncs.</summary>
    /// <summary>Raised once per resolved collision, after effects are spawned. The HUD's impact report subscribes here so
    /// the impact and HUD code never reference each other's files.</summary>
    public event Action<ImpactOutcome>? ImpactOccurred;
    internal void RaiseImpact(ImpactOutcome outcome) => ImpactOccurred?.Invoke(outcome);
    /// <summary>Raised after an undo or redo cleared the last impact (or restored an impact's point) and views were rebound.</summary>
    public event Action? ImpactUndone;
    internal void RaiseImpactUndone() => ImpactUndone?.Invoke();

    partial void OnSelectionChanged();
    /// <summary>Mode buttons and panels follow the mode.</summary>
    partial void OnModeChanged(Mode m);
    /// <summary>The impact report card closes.</summary>
    partial void OnImpactUndone();
    /// <summary>Cinematic flight to an impact site (app.ts lookAtImpact); sets the site lock.</summary>
    partial void LookAtImpact(ImpactOutcome o, BodyView view);
}
