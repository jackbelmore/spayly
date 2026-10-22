using Godot;

namespace Spayly.Game.UI;

/// <summary>Help overlay (index.html #help) and the erase brush cursor (#brush).</summary>
public partial class Hud
{
    Control help = null!, helpPanel = null!;
    Panel brush = null!;
    StyleBoxFlat brushStyle = null!;
    float brushRadius = 46;
    bool erasing;
    /// <summary>The web shows the brush from the first pointer move in erase mode, not on entering it.</summary>
    bool brushArmed;

    static readonly (string Key, string Text)[] HelpRows =
    [
        ("Drag", "orbit the view · right-drag or shift-drag pans · wheel zooms (log scale; a quick scroll speeds up, shift-wheel jumps ×3)"),
        ("W A S D", "pan the view; hold shift to move faster. Panning keeps following whatever you are following."),
        ("Q · E", "swing the camera left and right around what it is looking at"),
        ("Click / double-click", "select a body / fly to it — on the body itself or on its name in the view"),
        ("Space, [ ]", "pause · slower · faster"),
        ("N", "skip to the next notable event: runs time as fast as the physics allows, then drops to real time. Stops at the first notable or major event, after 100 years of nothing, or on any key or click."),
        ("R", "rewind time (gravity is time-reversible; Ctrl+Z undoes impacts)"),
        ("Ctrl+Z · Ctrl+Shift+Z", "undo · redo any action: launches, showers, impacts, holes, erase strokes, slider drags"),
        ("C", "Director camera: off · cuts to notable and major events · cuts to major events only. Events always land in the Events list on the right; click one to fly there and open its report."),
        ("M", "hide or show the bottom mode bar (the tab it leaves behind names the mode you are in, and brings it back)"),
        ("U", "undo history: every undo point, newest first; click one to jump back to it, or forward to a greyed one"),
        ("Z", "erase mode: drag over the view to delete asteroids and fragments, wheel resizes the brush"),
        ("X", "asteroid mode: click a body to fire from the camera with auto-aim, or drag on empty space to aim by hand. Rocks you fire are ordinary bodies — shoot one at another and they pull on each other, swing around each other, and collide. Tick Meteor shower to break the rock into 8–24 pieces that fall one after another."),
        ("B", "black-hole mode: pick a mass from 3 M☉ to M87*'s 6.5 × 10⁹, then click the view to place a hole on a circular orbit, at rest, or dragged out. Undo takes it back; the inspector's Remove deletes one."),
        ("F · T · 1–9, 0", "fly above the selection · tabletop view · select Sun, Mercury … Neptune, Pluto"),
        ("Sliders", "change any body's mass or radius live. The Sun's luminosity and colour follow its mass; watch Earth freeze or scorch."),
        ("Escape", "back to explore mode, cancel aiming, close panels, stop following (camera holds)"),
    ];

    void BuildHelp()
    {
        // #help { background: rgba(0,0,0,0.45); pointer-events: auto }: the backdrop swallows clicks while help is open.
        var backdrop = new ColorRect { Color = new Color(0, 0, 0, 0.45f), MouseFilter = Control.MouseFilterEnum.Stop, Visible = false };
        backdrop.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        var centre = new CenterContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        centre.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        var panel = NewPanel();
        panel.CustomMinimumSize = new Vector2(620, 0);
        var v = VBox(8);
        v.AddChild(H3("spayly · controls"));
        var grid = new GridContainer { Columns = 2, MouseFilter = Control.MouseFilterEnum.Ignore };
        grid.AddThemeConstantOverride("v_separation", 5);
        foreach (var (key, text) in HelpRows)
        {
            var dt = Text(key, HudTheme.LabelHelpDt, 170);
            dt.SizeFlagsVertical = Control.SizeFlags.ShrinkBegin;
            var dd = Wrapped(text);
            dd.AddThemeFontSizeOverride("font_size", 12);
            grid.AddChild(dt);
            grid.AddChild(dd);
        }
        v.AddChild(grid);
        v.AddChild(Wrapped("Physics: full mutual N-body gravity (symplectic leapfrog, adaptive substeps with swept-sphere contact detection; Paczyński–Wiita near black holes), " +
            "IAU rotation poles, Standish planetary ephemeris, JPL satellite and small-body elements, Collins–Melosh–Marcus impact effects, " +
            "Benz–Asphaug disruption thresholds, momentum-conserving merges and fragmentation. Visual scale never changes the physics.", HudTheme.LabelMuted));
        var close = Btn("Close", () => ToggleHelp(false));
        close.SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin;
        v.AddChild(close);
        panel.AddChild(v);
        centre.AddChild(panel);
        backdrop.AddChild(centre);
        help = backdrop;
        helpPanel = panel;
        root.AddChild(backdrop);
    }

    /// <summary>Show or hide help; no argument flips it (hud.ts toggleHelp).</summary>
    public void ToggleHelp(bool? force = null) => help.Visible = force ?? !help.Visible;

    public bool HelpVisible => help.Visible;

    /// <summary>Esc closes the overlays (hud.ts closeOverlays) and the history panel.</summary>
    public void CloseOverlays()
    {
        help.Visible = false;
        ToggleHistory(false);
    }

    void BuildBrush()
    {
        brushStyle = new StyleBoxFlat
        {
            BgColor = new Color(1, 107 / 255f, 107 / 255f, 0.12f),
            BorderColor = new Color(1, 107 / 255f, 107 / 255f, 0.9f),
            ShadowColor = new Color(1, 107 / 255f, 107 / 255f, 0.35f),
            ShadowSize = 18,
            AntiAliasing = true,
        };
        brushStyle.SetBorderWidthAll(1);
        brush = new Panel { MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
        brush.AddThemeStyleboxOverride("panel", brushStyle);
        root.AddChild(brush);
        SetBrush(brushRadius);
    }

    /// <summary>Brush radius, px (the app's wheel-resized brushRadius).</summary>
    public void SetBrush(float px)
    {
        if (px == brushRadius && brush.Size.X == px * 2) return;
        brushRadius = px;
        brush.Size = new Vector2(px * 2, px * 2);
        brushStyle.SetCornerRadiusAll((int)Mathf.Ceil(px));
    }

    /// <summary>Show the brush centred on <paramref name="at"/> (viewport px), or hide it with null (app.ts showBrush).</summary>
    public void ShowBrush(Vector2? at)
    {
        if (at is not Vector2 p)
        {
            brushArmed = false;
            brush.Visible = false;
            return;
        }
        brushArmed = true;
        brush.Position = p - new Vector2(brushRadius, brushRadius);
        brush.Visible = erasing;
    }

    /// <summary>Per frame: the brush tracks the pointer once the pointer has moved since erase mode began.</summary>
    void UpdateBrush()
    {
        if (!erasing) return;
        var at = GetViewport().GetMousePosition();
        if (!brushArmed && at == eraseEnteredAt) return;
        ShowBrush(at);
    }

    Vector2 eraseEnteredAt;
}
