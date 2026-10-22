using System;
using System.Globalization;
using Godot;
using Spayly.Sim;
using static Spayly.Sim.Body;

namespace Spayly.Game.UI;

/// <summary>
/// Right-hand inspector (hud.ts syncInspector / updateInspector). The mass and radius sliders write
/// <c>Body.Mass</c>/<c>Body.Radius</c> as multiples of the catalogue values, exactly as the web does; Mass0/Radius0 stay
/// the catalogue's. Temperatures come from the body's view (PlanetView computes them); the HUD only formats them.
/// </summary>
public partial class Hud
{
    HudLabel inspName = null!, inspKind = null!, massVal = null!, radiusVal = null!;
    HSlider mass = null!, radius = null!;
    Button removeHole = null!;
    readonly Held massHeld = new(), radiusHeld = new();
    const int StatRows = 16;
    readonly HudLabel[] statKeys = new HudLabel[StatRows];
    readonly HudLabel[] statVals = new HudLabel[StatRows];
    int statCount;
    GridContainer stats = null!;

    void BuildRight()
    {
        var panel = NewPanel();
        panel.AnchorLeft = panel.AnchorRight = 1;
        panel.OffsetLeft = -292;
        panel.OffsetRight = -12;
        panel.OffsetTop = panel.OffsetBottom = 74;
        panel.GrowHorizontal = Control.GrowDirection.Begin;
        var v = VBox(2);
        inspName = Wrapped("—", HudTheme.LabelTitle);
        inspKind = Text("", HudTheme.LabelMuted);
        v.AddChild(inspName);
        v.AddChild(inspKind);
        var buttons = HBox(6);
        foreach (var (text, act) in new (string, Action)[]
                 {
                     ("Follow", () => { if (app?.Selected is Body b) app.Follow(b); }),
                     ("Above", () => { if (app?.Selected is Body b) app.FlyAbove(b); }),
                     ("Reset", () => app?.ResetScene()),
                 })
        {
            var b = Btn(text, act);
            b.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            buttons.AddChild(b);
        }
        removeHole = Btn("Remove", () => { if (app?.Selected is Body b) app.RemoveBlackHole(b); }, "Delete this black hole (Undo brings it back)");
        removeHole.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        removeHole.Visible = false;
        buttons.AddChild(removeHole);
        var bm = new MarginContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        bm.AddThemeConstantOverride("margin_top", 6);
        bm.AddThemeConstantOverride("margin_bottom", 6);
        bm.AddChild(buttons);
        v.AddChild(bm);

        mass = Slider(-3, 3, 0.01, 0, x =>
        {
            if (app?.Selected is not Body b) return;
            RecordOnce(massHeld, b, "mass", UndoKind.Mass);
            b.Mass = b.Mass0 * Math.Pow(10, x);
            BlackHole.SyncHorizon(b);
            massVal.Set(Format.Mass(b.Mass));
            if (b.Kind == BodyKind.BlackHole) radiusVal.Set(Format.Distance(b.Radius));
        }, massHeld);
        radius = Slider(-1, 1, 0.01, 0, x =>
        {
            if (app?.Selected is not Body b) return;
            RecordOnce(radiusHeld, b, "radius", UndoKind.Radius);
            b.Radius = b.Radius0 * Math.Pow(10, x);
            radiusVal.Set(Format.Distance(b.Radius));
        }, radiusHeld);
        v.AddChild(Field("Mass", mass, out massVal));
        v.AddChild(Field("Radius", radius, out radiusVal));

        stats = new GridContainer { Columns = 2, MouseFilter = Control.MouseFilterEnum.Ignore };
        for (var i = 0; i < StatRows; i++)
        {
            statKeys[i] = Text("", HudTheme.LabelDt);
            statKeys[i].SizeFlagsVertical = Control.SizeFlags.ShrinkBegin;
            statVals[i] = Wrapped("", HudTheme.LabelDd);
            statVals[i].HorizontalAlignment = HorizontalAlignment.Right;
            statKeys[i].Visible = statVals[i].Visible = false;
            stats.AddChild(statKeys[i]);
            stats.AddChild(statVals[i]);
        }
        var sm = new MarginContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        sm.AddThemeConstantOverride("margin_top", 8);
        sm.AddChild(stats);
        v.AddChild(sm);
        panel.AddChild(v);
        root.AddChild(panel);
        inspectorPanel = panel;
    }

    /// <summary>
    /// One undo point per drag, taken before its first value is written. At the first change rather than on DragStarted:
    /// a click on the track changes the value before the slider reports the drag, which would record the changed body.
    /// </summary>
    void RecordOnce(Held held, Body b, string what, UndoKind kind)
    {
        if (held.Recorded) return;
        held.Recorded = true;
        app?.RecordUndo(kind, $"{b.Name}'s {what} change", b.Id);
    }

    /// <summary>Selection changed or the body was reset: name, kind, and the sliders at the body's current multiples.</summary>
    public void SyncInspector()
    {
        var b = app?.Selected;
        if (b == null)
        {
            inspName.Set("Nothing selected");
            inspKind.Set("click a body or a label");
            removeHole.Visible = false;
            SetStatCount(0);
            return;
        }
        inspName.Set(b.Name);
        inspKind.Set(KindLabel(b.Kind));
        if (!massHeld.Dragging) mass.SetValueNoSignal(Math.Log10(b.Mass / b.Mass0));
        if (!radiusHeld.Dragging) radius.SetValueNoSignal(Math.Log10(b.Radius / b.Radius0));
        // A hole's radius is r_s of its mass: the mass slider moves it, this one would make the horizon lie.
        radius.Editable = b.Kind != BodyKind.BlackHole;
        removeHole.Visible = b.Kind == BodyKind.BlackHole;
        massVal.Set(Format.Mass(b.Mass));
        radiusVal.Set(Format.Distance(b.Radius));
    }

    void SetStatCount(int n)
    {
        for (var i = 0; i < StatRows; i++)
        {
            var show = i < n;
            if (statKeys[i].Visible != show) statKeys[i].Visible = statVals[i].Visible = show;
        }
        statCount = n;
    }

    void Row(string key, string value)
    {
        if (statCount >= StatRows) return;
        statKeys[statCount].Set(key);
        statVals[statCount].Set(value);
        statCount++;
    }

    /// <summary>
    /// The 8 Hz stats refresh (hud.ts updateInspector). <paramref name="sunLuminosity"/> is relative to the present Sun and
    /// <paramref name="sunTempK"/> in K: the app's live sun state, which only a star's rows use.
    /// </summary>
    public void UpdateInspector(double sunLuminosity, double sunTempK)
    {
        var a = app;
        var b = a?.Selected;
        if (a == null || b == null) return;
        inspName.Set(b.Name);
        if (!massHeld.Dragging) massVal.Set(Format.Mass(b.Mass));
        if (!radiusHeld.Dragging) radiusVal.Set(Format.Distance(b.Radius));
        var inv = CultureInfo.InvariantCulture;
        statCount = 0;
        // A day from the IAU rotation rate, or the one an SPH demotion gave the body (M6.6).
        if (b.Kind != BodyKind.BlackHole && b.Rotation is Rotation rot && rot.Rate != 0)
            Row("Day (sidereal)", $"{Format.Duration(Math.Abs(360 / rot.Rate) * 86400)}{(rot.Rate < 0 ? ", retrograde" : "")}");
        if (b.Kind == BodyKind.BlackHole)
        {
            // Density, surface gravity and an escape velocity of c say nothing about a hole; its radii do.
            Row("Schwarzschild radius", Format.Distance(BlackHole.SchwarzschildRadiusKm(b.Mass)));
            Row("ISCO (disc inner edge)", Format.Distance(BlackHole.IscoRadiusKm(b.Mass)));
            Row("Shadow radius", Format.Distance(BlackHole.ShadowRadiusKm(b.Mass)));
            // Only a merger spins a hole up (HLZ14); gravity, lensing and the disc stay Schwarzschild whatever it says.
            Row("Spin a/M", b.Spin > 0 ? $"{Format.Num(b.Spin)} (from its merger; drawn and pulled as non-spinning)" : "0 (non-spinning)");
            var o = a.BlackHoles;
            var lensed = a.ViewFor(b) is Render.BlackHoleView { Lensed: true };
            Row("Lensing", !o.Lensing ? "off (View panel)" : lensed ? "on" : $"not lensed: over {Render.LensingPass.MaxHoles} holes");
            Row("Accretion disc", !o.Disc ? "off (View panel)" : a.DiscSummary(b));
        }
        else
        {
            Row("Density", Format.Num(Density(b)) + " kg/m³");
            Row("Gravity", Format.Num(SurfaceGravity(b) / 9.80665) + " g");
            Row("Escape velocity", Format.Speed(EscapeVelocity(b)));
        }
        if (b.Kind == BodyKind.Star)
        {
            Row("Luminosity", Format.Num(sunLuminosity) + " L☉");
            Row("Temperature", Math.Floor(sunTempK + 0.5).ToString(inv) + " K");
            Row("Class", SpectralClass(sunTempK, b.Mass));
        }
        else
        {
            if (a.PrimaryOf(b) is Body p)
            {
                var r = b.Pos - p.Pos;
                var vel = b.Vel - p.Vel;
                var el = Kepler.StateToElements(Constants.G * (p.Mass + b.Mass), r, vel);
                Row("Orbits", p.Name);
                Row("Period", double.IsFinite(el.Period) ? Format.Duration(el.Period) : "unbound");
                Row("Distance", Format.Distance(Vec3.Length(r)));
                Row("Speed", Format.Speed(Vec3.Length(vel)));
                Row("Eccentricity", Format.Num(el.E));
                if (double.IsFinite(el.Apoapsis)) Row("Peri / apo", $"{Format.Distance(el.Periapsis)} / {Format.Distance(el.Apoapsis)}");
            }
            // One row, as hud.ts: the surface estimate where the catalogue has a baseline, else the bare equilibrium value.
            var view = b.Kind == BodyKind.BlackHole ? null : a.ViewFor(b);
            if (view?.SurfaceTemp is double ts) Row("Surface temp", Format.Temperature(ts));
            else if (view?.EquilibriumTemp is double te) Row("Equilibrium temp", Format.Temperature(te));
        }
        if (b.Damage.Hits > 0) Row("Impacts", $"{b.Damage.Hits} · {Format.Energy(b.Damage.Energy)}");
        if (b.Damage.Absorbed > 0) Row("Rocks swallowed", b.Damage.Absorbed.ToString(inv));
        SetStatCount(statCount);
    }

    /// <summary>Capture helper: the inspector as text (kind, mass, radius, whether radius is editable, the visible stat rows).</summary>
    public string InspectorSummary()
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"name={inspName.Text} kind={inspKind.Text} mass={massVal.Text} radius={radiusVal.Text} radius_editable={radius.Editable}");
        for (var i = 0; i < statCount; i++) sb.Append(" | ").Append(statKeys[i].Text).Append('=').Append(statVals[i].Text);
        return sb.ToString();
    }

    /// <summary>Capture helper: move the mass slider to 10^<paramref name="log10"/> × the body's original mass, through its signal as a drag would.</summary>
    public void SetMassSlider(double log10)
    {
        // Each call is one gesture with its own undo point, as a drag and release would be.
        massHeld.Recorded = false;
        mass.Value = log10;
        massHeld.Recorded = false;
    }

    /// <summary>Capture helper: the radius slider to 10^<paramref name="log10"/> × the original radius, one gesture with its own undo point.</summary>
    public void SetRadiusSlider(double log10)
    {
        radiusHeld.Recorded = false;
        radius.Value = log10;
        radiusHeld.Recorded = false;
    }

    static string SpectralClass(double T, double mass)
    {
        if (mass < 0.08 * Constants.SunMass) return "brown dwarf";
        if (T >= 30000) return "O-type blue giant";
        if (T >= 10000) return "B-type blue-white";
        if (T >= 7500) return "A-type white";
        if (T >= 6000) return "F-type yellow-white";
        if (T >= 5200) return "G-type yellow (like our Sun)";
        if (T >= 3700) return "K-type orange";
        return "M-type red dwarf";
    }
}
