using System;
using System.Collections.Generic;
using System.Globalization;
using Godot;
using Spayly.Game.App;
using Spayly.Sim;

namespace Spayly.Game.UI;

/// <summary>
/// The black-hole panel under the mode bar (built like the asteroid panel): mass presets, a log mass slider from 3 M☉ to
/// 10¹⁰ M☉, the starting-velocity choice, the r_s / tidal-radius / Hill-sphere readout the app fills, and Undo.
/// </summary>
public partial class Hud
{
    Control holePanel = null!;
    HSlider holeMass = null!;
    readonly Held holeMassHeld = new();
    HudLabel holeMassVal = null!, holeReadout = null!, holeForecast = null!;
    OptionButton holeVelocity = null!;
    readonly List<Button> holePresetButtons = [];

    void BuildHolePanel(VBoxContainer col)
    {
        var v = VBox(4);
        v.Visible = false;
        holePanel = v;
        var presets = new HFlowContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        foreach (var p in HolePresets.All)
        {
            var preset = p;
            var b = Btn(p.Name, () =>
            {
                if (app == null) return;
                app.HoleSettings.MassSolar = preset.MassSolar;
                SyncHolePanel();
            }, $"{p.Blurb} · {Format.Num(p.MassSolar)} M☉", HudTheme.PresetButton);
            holePresetButtons.Add(b);
            presets.AddChild(b);
        }
        v.AddChild(presets);
        var fields = HBox(10);
        holeMass = Slider(Math.Log10(BlackHoleSettings.MinSolar), Math.Log10(BlackHoleSettings.MaxSolar), 0.01, 1, x =>
        {
            if (app == null) return;
            app.HoleSettings.MassSolar = Math.Pow(10, x);
            SyncHolePanel();
        }, holeMassHeld);
        holeVelocity = Select(("Circular orbit about the dominant body", (int)HoleVelocity.Orbit),
            ("At rest relative to it", (int)HoleVelocity.Rest), ("Drag to set", (int)HoleVelocity.Drag));
        holeVelocity.ItemSelected += i =>
        {
            if (app == null) return;
            app.HoleSettings.Velocity = (HoleVelocity)holeVelocity.GetItemId((int)i);
            SyncHolePanel();
        };
        var fm = Field("Mass", holeMass, out holeMassVal);
        var fv = Field("Velocity", holeVelocity, out _);
        fm.SizeFlagsHorizontal = fv.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        fields.AddChild(fm);
        fields.AddChild(fv);
        var undo = Btn("Undo", () => app?.Undo(), "Take back the last action or impact (Ctrl+Z; Ctrl+Shift+Z redoes)");
        undo.SizeFlagsVertical = Control.SizeFlags.ShrinkEnd;
        fields.AddChild(undo);
        v.AddChild(fields);
        holeReadout = Wrapped("", HudTheme.LabelMonoMuted);
        holeReadout.CustomMinimumSize = new Vector2(0, 32);
        v.AddChild(holeReadout);
        holeForecast = ForecastLabel();
        v.AddChild(holeForecast);
        col.AddChild(v);
    }

    /// <summary>The panel follows <c>app.HoleSettings</c>; a slider the user holds keeps its value.</summary>
    public void SyncHolePanel()
    {
        var s = app?.HoleSettings;
        if (s == null) return;
        if (!holeMassHeld.Dragging) holeMass.SetValueNoSignal(Math.Log10(s.MassSolar));
        var vi = holeVelocity.GetItemIndex((int)s.Velocity);
        if (holeVelocity.Selected != vi) holeVelocity.Select(vi);
        holeMassVal.Set($"{Format.Num(s.MassSolar)} M☉ · r_s {Format.Distance(BlackHole.SchwarzschildRadiusKm(s.MassSolar * Constants.SunMass))}");
        for (var i = 0; i < holePresetButtons.Count; i++)
            Variation(holePresetButtons[i], Math.Abs(HolePresets.All[i].MassSolar / s.MassSolar - 1) < 1e-9 ? HudTheme.PresetActive : HudTheme.PresetButton);
    }

    /// <summary>The forecast line of the mode panels (black hole and asteroid), in a warning colour; "" empties it.</summary>
    static HudLabel ForecastLabel()
    {
        var l = Wrapped("", HudTheme.LabelMonoMuted);
        l.CustomMinimumSize = new Vector2(0, 32);
        l.AddThemeColorOverride("font_color", new Color("#ffb454"));
        l.Visible = false;
        return l;
    }

    /// <summary>What placing here would do (App.Forecast.cs): shown in whichever of the two panels is open; hidden when empty.</summary>
    public void SetForecast(string text)
    {
        holeForecast.Set(text);
        holeForecast.Visible = text.Length > 0;
        astForecast.Set(text);
        astForecast.Visible = text.Length > 0;
    }

    /// <summary>Capture helper: the forecast line as shown.</summary>
    public string ForecastSummary() => $"hole_panel_visible={holeForecast.IsVisibleInTree()} ast_panel_visible={astForecast.IsVisibleInTree()} text={holeForecast.Text}";

    /// <summary>The app's readout under the panel: r_s and tidal radii, then the Hill sphere where the pointer is.</summary>
    public void SetHoleReadout(string text) => holeReadout.Set(text);

    /// <summary>Capture helper: the panel's text (mass, velocity choice, readout).</summary>
    public string HolePanelSummary() => string.Create(CultureInfo.InvariantCulture,
        $"visible={holePanel.IsVisibleInTree()} mass={holeMassVal.Text} slider={holeMass.Value:F2} velocity={holeVelocity.GetItemText(holeVelocity.Selected)} readout={holeReadout.Text.Replace('\n', '|')}");
}
