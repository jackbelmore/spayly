using System;
using System.Globalization;
using System.Text;
using Godot;
using Spayly.Sim;

namespace Spayly.Game.UI;

/// <summary>
/// The impact report card (hud.ts showReport / hideReport), built branch by branch from <see cref="ImpactOutcome.Report"/>.
/// It opens when an event is clicked (or for the rock the player fired by hand), and folds away after
/// <see cref="ReportSeconds"/> s unless pinned; the pointer over it holds it open.
/// </summary>
public partial class Hud
{
    PanelContainer report = null!;
    HudLabel reportTitle = null!, reportClass = null!, reportCompare = null!;
    const int ReportRows = 16;
    readonly HudLabel[] reportKeys = new HudLabel[ReportRows];
    readonly HudLabel[] reportVals = new HudLabel[ReportRows];
    int reportCount;
    const double ReportSeconds = 10;
    Button reportPin = null!;
    bool reportPinned;
    double reportShownAt;
    ImpactOutcome? reportOutcome;
    readonly StringBuilder sb = new();

    /// <summary>True while the card is on screen.</summary>
    public bool ReportVisible => report.Visible;

    void BuildReport()
    {
        report = NewPanel(HudTheme.ReportPanel);
        report.AnchorLeft = report.AnchorRight = 1;
        report.AnchorTop = report.AnchorBottom = 1;
        report.OffsetLeft = -352;
        report.OffsetRight = -12;
        report.OffsetTop = report.OffsetBottom = -14;
        report.GrowHorizontal = Control.GrowDirection.Begin;
        report.GrowVertical = Control.GrowDirection.Begin;
        report.Visible = false;
        var v = VBox(8);
        var head = HBox(6);
        reportTitle = Wrapped("Impact", HudTheme.LabelTitle);
        reportTitle.AddThemeFontSizeOverride("font_size", 14);
        head.AddChild(reportTitle);
        reportPin = Btn("Pin", () => SetReportPinned(!reportPinned), $"Keep this card open; otherwise it folds away after {ReportSeconds:F0} s", HudTheme.ButtonSmall);
        reportPin.SizeFlagsVertical = Control.SizeFlags.ShrinkBegin;
        head.AddChild(reportPin);
        var close = Btn("×", HideReport, "Close", HudTheme.ButtonSmall);
        close.SizeFlagsVertical = Control.SizeFlags.ShrinkBegin;
        head.AddChild(close);
        v.AddChild(head);
        reportClass = Wrapped("", HudTheme.LabelClass);
        v.AddChild(reportClass);
        var grid = new GridContainer { Columns = 2, MouseFilter = Control.MouseFilterEnum.Ignore };
        for (var i = 0; i < ReportRows; i++)
        {
            reportKeys[i] = Text("", HudTheme.LabelDt);
            reportKeys[i].SizeFlagsVertical = Control.SizeFlags.ShrinkBegin;
            reportVals[i] = Wrapped("", HudTheme.LabelDd);
            reportKeys[i].Visible = reportVals[i].Visible = false;
            grid.AddChild(reportKeys[i]);
            grid.AddChild(reportVals[i]);
        }
        v.AddChild(grid);
        reportCompare = Wrapped("", HudTheme.LabelMuted);
        v.AddChild(reportCompare);
        var buttons = HBox(6);
        var undo = Btn("Undo impact", () => app?.Undo(), "Back to just before the impact (Ctrl+Z)");
        var look = Btn("Look at it", () => { if (reportOutcome != null) app?.LookAtOutcome(reportOutcome); });
        undo.SizeFlagsHorizontal = look.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        buttons.AddChild(undo);
        buttons.AddChild(look);
        v.AddChild(buttons);
        report.AddChild(v);
        root.AddChild(report);
    }

    public void HideReport() => report.Visible = false;

    void SetReportPinned(bool on)
    {
        reportPinned = on;
        reportPin.Text = on ? "Pinned" : "Pin";
        SetVariation(reportPin, on ? HudTheme.ButtonSmallActive : HudTheme.ButtonSmall);
        reportShownAt = now;
    }

    /// <summary>Every frame: fold the card away once it has been open <see cref="ReportSeconds"/> s, unless pinned or under the pointer.</summary>
    void UpdateReportTimer()
    {
        if (!report.Visible) return;
        if (reportPinned || report.GetGlobalRect().HasPoint(GetViewport().GetMousePosition())) reportShownAt = now;
        else if (now - reportShownAt > ReportSeconds) report.Visible = false;
    }

    /// <summary>Capture helper: the card's state, for the acceptance logs.</summary>
    public string ReportSummary() => report.Visible
        ? $"open pinned={reportPinned} '{reportTitle.Text}' ({report.GlobalPosition.X:F0},{report.GlobalPosition.Y:F0} {report.Size.X:F0}x{report.Size.Y:F0})"
        : "closed";

    void ReportRow(string key, string value)
    {
        if (reportCount >= ReportRows) return;
        reportKeys[reportCount].Set(key);
        reportVals[reportCount].Set(value);
        reportCount++;
    }

    /// <summary>Fill and show the card; <paramref name="deltaV"/> is the target's recoil, km/s.</summary>
    public void ShowReport(ImpactOutcome o, double deltaV)
    {
        var inv = CultureInfo.InvariantCulture;
        var r = o.Report;
        var target = o.Target.Name;
        reportOutcome = o;
        reportShownAt = now;
        reportTitle.Set($"{o.Impactor.Name} → {target}");
        var cls = r?.Classification ?? "";
        if (o.Kind == OutcomeKind.Absorb) cls = $"Swallowed by {target}. A brief flare, nothing more.";
        if (o.Kind == OutcomeKind.StellarMerge)
            cls = string.Create(inv, $"{o.Impactor.Name} merged into {target}, which threw off {o.ParticleTierMass / (o.Target.Mass + o.ParticleTierMass):P0} of the pair's mass as hot gas.");
        if (o.Kind == OutcomeKind.HitAndRun && o.Target.Kind == BodyKind.Star) cls = $"{o.Impactor.Name} grazed {target} and flew on; both survive.";
        if (o.Kind == OutcomeKind.Shatter)
        {
            var fragMass = 0.0;
            foreach (var f in o.Fragments) fragMass += f.Mass;
            var keep = Math.Floor(o.Target.Mass / (o.Target.Mass + fragMass) * 100 + 0.5);
            cls = string.Create(inv, $"{target} shattered into {o.Fragments.Count} pieces. The molten remnant keeps {keep}% of the mass.");
        }
        if (o.Kind == OutcomeKind.Disperse) cls = $"{target} destroyed. {o.Fragments.Count} fragments scatter; only a molten core remains.";
        reportClass.Set(cls);

        reportCount = 0;
        ReportRow("Impact speed", $"{Format.Speed(o.RelSpeed)} at {Format.Angle(o.Angle)}");
        ReportRow("Energy", Format.Energy(o.Energy));
        if (r is ImpactReport rep && rep.PlanetScale)
        {
            ReportRow("Scale", "planet-wide: the crust is remelted, no crater survives");
            ReportRow("Fireball", "engulfs the planet");
            if (double.IsFinite(rep.SeismicMagnitude)) ReportRow("Earthquake", $"magnitude {rep.SeismicMagnitude.ToString("F1", inv)}, felt everywhere");
            ReportRow("Binding energy", $"{Format.ToPrecision(rep.BindingRatio * 100, 2)}% of {target}'s");
        }
        else if (r is ImpactReport rp)
        {
            var en = rp.Entry;
            if (en.Airburst) ReportRow("Entry", $"broke up at {Format.LengthM(en.BreakupAltitude)}, airburst at {Format.LengthM(en.BurstAltitude)}");
            else if (en.Breakup) ReportRow("Entry", $"fragmented at {Format.LengthM(en.BreakupAltitude)}, hit the ground at {Format.Speed(en.SurfaceSpeed / 1000)}");
            else if (o.Target.Atmosphere != null) ReportRow("Entry", $"intact, hit the ground at {Format.Speed(en.SurfaceSpeed / 1000)}");
            if (rp.Crater is CraterResult c) ReportRow("Crater", $"{Format.LengthM(c.FinalDiameter)} wide, {Format.LengthM(c.Depth)} deep ({(c.Complex ? "complex" : "simple")})");
            else if (o.Kind != OutcomeKind.Gas) ReportRow("Crater", "none");
            if (rp.FireballRadius > 1) ReportRow("Fireball", $"{Format.LengthM(rp.FireballRadius)} radius");
            if (rp.Thermal.BurnRadius > 100) ReportRow("Thermal", $"3rd-degree burns to {Format.LengthM(rp.Thermal.BurnRadius)}{(rp.Thermal.HorizonLimited ? " (to the horizon)" : "")}");
            if (rp.Crater != null && double.IsFinite(rp.SeismicMagnitude))
                ReportRow("Earthquake", $"magnitude {rp.SeismicMagnitude.ToString("F1", inv)}{(rp.SeismicDamageRadius > 0 ? $", serious damage to {Format.LengthM(rp.SeismicDamageRadius)}" : "")}");
            if (rp.Airblast.GlassRadius > 0)
                ReportRow("Air blast", $"windows break to {Format.LengthM(rp.Airblast.GlassRadius)}{(rp.Airblast.CollapseRadius > 0 ? $", buildings collapse to {Format.LengthM(rp.Airblast.CollapseRadius)}" : "")}");
            if (rp.Crater != null && rp.EjectaOneMetreRadius > 0) ReportRow("Ejecta", $"≥ 1 m deep out to {Format.LengthM(rp.EjectaOneMetreRadius)}");
            if (rp.Tsunami is TsunamiResult t) ReportRow("Tsunami", $"{Format.LengthM(t.Amplitude100km)} wave at 100 km, {Format.LengthM(t.Amplitude1000km)} at 1000 km");
            if (rp.BindingRatio > 1e-4) ReportRow("Binding energy", $"{Format.ToPrecision(rp.BindingRatio * 100, 2)}% of {target}'s");
        }
        if (o.Fragments.Count > 0)
        {
            var largest = 0.0;
            foreach (var f in o.Fragments) largest = Math.Max(largest, f.Mass);
            ReportRow("Fragments", $"{o.Fragments.Count}, largest {Format.Mass(largest)}");
        }
        ReportRow($"{target} recoil", Format.Speed(deltaV));
        for (var i = 0; i < ReportRows; i++)
        {
            var show = i < reportCount;
            if (reportKeys[i].Visible != show) reportKeys[i].Visible = reportVals[i].Visible = show;
        }

        sb.Clear();
        if (r is ImpactReport withComparisons)
            for (var i = 0; i < withComparisons.Comparisons.Length; i++)
            {
                if (i > 0) sb.Append(" · ");
                sb.Append(withComparisons.Comparisons[i]);
            }
        reportCompare.Set(sb.ToString());
        reportCompare.Visible = sb.Length > 0;
        report.Visible = true;
    }
}
