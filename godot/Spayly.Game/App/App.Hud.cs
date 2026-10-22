using System;
using Godot;
using Spayly.Sim;

namespace Spayly.Game.App;

/// <summary>
/// The HUD hooks: the full HUD (UI/Hud*.cs) is bound on the first hook call, since the Hud is created in _Ready and the
/// first <see cref="Select(Body?)"/> there fires <see cref="OnSelectionChanged"/>. The report card listens on
/// <see cref="ImpactOccurred"/>/<see cref="ImpactUndone"/> so the impact code never references the HUD.
/// </summary>
public partial class App
{
    bool hudBound;
    double markersAt;

    void EnsureHud()
    {
        if (hudBound || hud == null) return;
        hudBound = true;
        // A drag that began on the stage keeps orbiting when the pointer crosses a panel.
        hud.StageDragActive = () => drag != null;
        hud.ForwardToStage = _UnhandledInput;
        hud.Bind(this);
        ImpactOccurred += o => hud.ShowReport(o, o.TargetDeltaV);
        ImpactUndone += hud.HideReport;
    }

    partial void UpdateHud()
    {
        EnsureHud();
        hud.SetBrush((float)brushRadius);
        hud.UpdateInspector(sunState.Luminosity, sunState.TempK);
        if (Mode == Mode.BlackHole) hud.SetHoleReadout(HoleReadout());
        // PickBody under the pointer is a screen-space scan: four times a second is plenty for tick marks.
        if (Mode == Mode.Asteroid && realTime >= markersAt)
        {
            markersAt = realTime + 0.25;
            hud.SetSpeedMarkers(LaunchMarkers());
        }
        hud.SetSphProgress(SphProgressText());
    }

    partial void OnSelectionChanged()
    {
        EnsureHud();
        hud.SyncInspector();
        hud.RefreshBodyList();
    }

    partial void OnModeChanged(Mode m)
    {
        EnsureHud();
        hud.SetMode(m);
        if (m != Mode.Asteroid) hud.SetAimText("");
    }

    // OnImpactUndone is implemented in App.Impacts.cs and raises ImpactUndone; the HUD hides the report from that event.

    partial void ToggleHelp() => hud.ToggleHelp();

    partial void OnEscape()
    {
        hud.CloseOverlays();
        hud.HideReport();
    }

    // ---------- capture verbs (App.Capture.cs dispatches) ----------

    /// <summary>Format.SelfTest's ten checks of the format.ts port, printed as "[hud] selftest N/10".</summary>
    void HudSelftest()
    {
        var pass = UI.Format.SelfTest();
        GD.Print($"[hud] selftest {pass}/10 {(pass == 10 ? "PASS" : "FAIL")}");
        GD.Print($"[hud] layout {hud.LayoutSummary()}");
    }

    /// <summary>
    /// Render the report card for a Chelyabinsk-preset rock hitting Earth at 45°: the real impact model's report on a
    /// fabricated outcome. Nothing enters the universe, so Undo and Look-at-it do nothing here.
    /// </summary>
    void ShowReportDemo()
    {
        EnsureHud();
        var earth = Universe.ById("earth");
        if (earth == null) return;
        var p = Presets.All[0];
        var rho = Presets.Density(p.Composition);
        var angle = 45 * Constants.Deg;
        var tgt = new TargetSpec
        {
            Radius = earth.Radius * 1000,
            Mass = earth.Mass,
            Gravity = Body.SurfaceGravity(earth),
            Surface = TargetSurface.Rock,
            SurfaceDensity = 2500,
            Atmosphere = earth.Atmosphere,
        };
        var report = Impact.ImpactReport(new ImpactorSpec { Diameter = p.DiameterM, Density = rho, Speed = p.SpeedKms * 1000, Angle = angle }, tgt);
        var massKg = rho * Math.PI / 6 * Math.Pow(p.DiameterM, 3);
        var impactor = Body.MakeBody(new BodySpec
        {
            Id = "demo-" + p.Name.ToLowerInvariant(),
            Name = p.Name,
            Kind = BodyKind.Asteroid,
            Surface = Surface.Rock,
            Mass = massKg,
            Radius = p.DiameterM / 2000,
            Visual = new BodyVisual { Color = Presets.Color(p.Composition), Albedo = 0.1 },
        });
        var up = new Vec3(0, 0, 1);
        var outcome = new ImpactOutcome
        {
            Kind = report.Entry.Airburst ? OutcomeKind.Airburst : report.Crater != null ? OutcomeKind.Crater : OutcomeKind.Absorb,
            Target = earth,
            Impactor = impactor,
            ContactWorld = earth.Pos + up * earth.Radius,
            NormalWorld = up,
            DirBody = up,
            VelDirWorld = -up,
            RelSpeed = p.SpeedKms,
            Angle = angle,
            Energy = report.Energy,
            TargetDeltaV = massKg * p.SpeedKms / earth.Mass,
            Report = report,
            Fragments = [],
            Time = Universe.Time,
        };
        hud.ShowReport(outcome, outcome.TargetDeltaV);
    }
}
