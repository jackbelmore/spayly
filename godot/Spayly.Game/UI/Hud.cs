using System;
using Godot;
using Spayly.Game.App;
using AppNode = Spayly.Game.App.App;

namespace Spayly.Game.UI;

/// <summary>What the top bar shows; filled by the app at 8 Hz.</summary>
public readonly record struct HudInfo
{
    public double Jd { get; init; }
    /// <summary>Sim s per real s.</summary>
    public double Warp { get; init; }
    public bool Rewind { get; init; }
    public bool Paused { get; init; }
    public double Fps { get; init; }
    public int Bodies { get; init; }
    public int Substeps { get; init; }
    /// <summary>The substep budget cut the last frame short.</summary>
    public bool Limited { get; init; }
    public string Mode { get; init; }
    public string? Selected { get; init; }
    public string? Focus { get; init; }
}

/// <summary>A label that only crosses into the engine when its text changes: reading <c>Label.Text</c> back allocates.</summary>
public partial class HudLabel : Label
{
    string last = "";

    public void Set(string s)
    {
        if (s == last) return;
        last = s;
        Text = s;
    }
}

/// <summary>
/// The HUD of src/ui/hud.ts + index.html + style.css, built from Control nodes in code. Regions and sizes follow the web
/// at 1600×900: top bar centred at 12 px, left column 240 px at (12, 74), inspector 280 px at the right, mode panel 880 px
/// centred at the bottom, report card 340 px bottom-right, help centred over a dim backdrop, hint pill at 72 px.
///
/// Pointer rule (the web's <c>#hud { pointer-events: none }</c> + <c>.panel { auto }</c>): every container is
/// <see cref="Control.MouseFilterEnum.Ignore"/> and only panels stop events, so a click over the stage reaches
/// App._UnhandledInput and a click over a panel never does. A drag that began on the stage keeps going when the pointer
/// crosses a panel: <see cref="_Input"/> hands those motions and the release straight to the stage.
/// Split: Hud.Panels.cs (top bar, left column, mode panel), Hud.Inspector.cs, Hud.Report.cs, Hud.Overlays.cs (help, brush),
/// Hud.Realism.cs (the Realism panel and the View panel's Realism tooltips), Hud.History.cs (the undo history panel).
/// </summary>
public partial class Hud : CanvasLayer
{
    AppNode? app;
    readonly Control root;
    Control topPanel = null!, leftColumn = null!, inspectorPanel = null!, bottomPanel = null!;
    readonly Button[] viewButtons = new Button[3];
    readonly Control[] covers;
    readonly PanelContainer hintPanel;
    readonly HudLabel hint = new();
    readonly PanelContainer sphPanel;
    readonly HudLabel sphLine = new();
    double hintUntil;
    double now;

    /// <summary>True while a pointer drag that began on the stage is held; set by the app (App.Hud.cs).</summary>
    public Func<bool>? StageDragActive;
    /// <summary>Where stage drag events that crossed a panel go (the app's pointer dispatcher).</summary>
    public Action<InputEvent>? ForwardToStage;

    public Hud()
    {
        Layer = 2;
        root = new Control { Name = "HudRoot", MouseFilter = Control.MouseFilterEnum.Ignore, Theme = HudTheme.Build() };
        root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(root);

        BuildTop();
        BuildLeft();
        BuildRight();
        BuildBottom();
        BuildBrush();

        hintPanel = new PanelContainer { ThemeTypeVariation = HudTheme.HintPanel, MouseFilter = Control.MouseFilterEnum.Ignore, Modulate = new Color(1, 1, 1, 0) };
        AnchorTopCentre(hintPanel, 72);
        hint.ThemeTypeVariation = HudTheme.LabelMono;
        hint.AddThemeFontOverride("font", HudTheme.Sans);
        hint.AddThemeFontSizeOverride("font_size", 13);
        hintPanel.AddChild(hint);
        root.AddChild(hintPanel);

        // The hero collision's progress, under the hint: it runs on its own clock and budget, so say how far it has got.
        sphPanel = new PanelContainer { ThemeTypeVariation = HudTheme.HintPanel, MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
        AnchorTopCentre(sphPanel, 112);
        sphLine.ThemeTypeVariation = HudTheme.LabelMono;
        sphPanel.AddChild(sphLine);
        root.AddChild(sphPanel);

        BuildReport();
        BuildHelp();
        // Features merged into this build: confirm their pre-written Realism entries (see Realism.cs, pending block).
        Realism.ConfirmPending(Realism.DebrisBranch);
        Realism.ConfirmPending(Realism.BlackHoleBranch);
        Realism.ConfirmPending(Realism.SphBranch);
        Spayly.Game.App.App.RegisterTidalRealism();
        Spayly.Game.App.App.RegisterBlackHoleRealism();
        Spayly.Game.App.App.RegisterApproachRealism();
        Spayly.Game.App.App.RegisterLaunchRealism();
        Spayly.Game.App.App.RegisterInspiralRealism();
        Spayly.Game.App.App.RegisterSphSpinRealism();
        Spayly.Game.App.App.RegisterUndoRealism();
        Spayly.Game.App.App.RegisterShowerRealism();
        _ = Spayly.Game.Render.StarColor.Lut; // the static constructor registers the star entries
        BuildRealism();
        BuildHistory();

        var cover = new System.Collections.Generic.List<Control> { topPanel, inspectorPanel, bottomPanel, report, helpPanel, realismPanel, historyPanel };
        foreach (var c in leftColumn.GetChildren())
            if (c is PanelContainer p) cover.Add(p);
        covers = [.. cover];
    }

    /// <summary>
    /// Whether a viewport-px rect overlaps a visible panel. The web's #hud sits above #labels, and the panels'
    /// <c>backdrop-filter: blur(16px)</c> smears a label under them into nothing; Godot has no backdrop blur, so the label
    /// layer asks here and hides such labels instead of showing them through the 66 % panel.
    /// </summary>
    public bool CoversRect(Rect2 r)
    {
        foreach (var c in covers)
            if (c.IsVisibleInTree() && c.GetGlobalRect().Intersects(r)) return true;
        return false;
    }

    /// <summary>Hand the HUD its app (the web's <c>new Hud(app)</c>); controls call the app's public API from here on.</summary>
    public void Bind(AppNode a)
    {
        app = a;
        BindPanels();
        SyncAsteroid();
        SyncHolePanel();
        SyncInspector();
        SetMode(a.Mode);
    }

    public override void _Process(double delta)
    {
        now += delta;
        // #hint { transition: opacity 0.3s }
        var target = now < hintUntil ? 1f : 0f;
        var alpha = hintPanel.Modulate.A;
        if (alpha != target)
        {
            alpha = Mathf.MoveToward(alpha, target, (float)delta / 0.3f);
            hintPanel.Modulate = new Color(1, 1, 1, alpha);
            hintPanel.Visible = alpha > 0;
        }
        UpdateBrush();
    }

    public override void _Input(InputEvent e)
    {
        if (StageDragActive == null || ForwardToStage == null || !StageDragActive()) return;
        var stageEvent = e is InputEventMouseMotion || (e is InputEventMouseButton { Pressed: false } mb && mb.ButtonIndex is MouseButton.Left or MouseButton.Right or MouseButton.Middle);
        if (!stageEvent) return;
        ForwardToStage(e);
        GetViewport().SetInputAsHandled();
    }

    /// <summary>A transient message, <paramref name="seconds"/> on screen (the web's hint toast).</summary>
    public void Hint(string text, double seconds)
    {
        hint.Set(text);
        hintUntil = now + seconds;
        hintPanel.Visible = true;
    }

    /// <summary>The SPH progress line; null hides it.</summary>
    public void SetSphProgress(string? text)
    {
        sphPanel.Visible = text != null;
        if (text != null) sphLine.Set(text);
    }

    /// <summary>Capture helper: the SPH line as shown, or "hidden".</summary>
    public string SphProgressSummary => sphPanel.Visible ? $"({sphPanel.GlobalPosition.X:F0},{sphPanel.GlobalPosition.Y:F0}) {sphLine.Text}" : "hidden";

    /// <summary>The 8 Hz top-bar update; the panels follow in <see cref="UpdatePanels"/>.</summary>
    public void Update(in HudInfo info)
    {
        UpdateTop(info);
        UpdatePanels(info);
    }

    /// <summary>Screen rects of the regions and one list row, for scripted layout checks against style.css.</summary>
    public string LayoutSummary()
    {
        static string R(Control c) => c.IsVisibleInTree() ? $"({c.GlobalPosition.X:F0},{c.GlobalPosition.Y:F0} {c.Size.X:F0}x{c.Size.Y:F0})" : "hidden";
        return $"top={R(topPanel)} left={R(leftColumn)} row0={R(rows[0])} row1={R(rows[1])} tabletopBtn={R(viewButtons[0])} innerBtn={R(viewButtons[1])} " +
               $"inspector={R(inspectorPanel)} bottom={R(bottomPanel)} report={R(report)} help={R(help)} hint={R(hintPanel)}";
    }

    /// <summary>The panels' 8 Hz update (Hud.Panels.cs); never writes an input the user is dragging.</summary>
    partial void UpdatePanels(in HudInfo info);

    // ---------- construction helpers ----------

    static void AnchorTopCentre(Control c, float top)
    {
        c.AnchorLeft = c.AnchorRight = 0.5f;
        c.AnchorTop = c.AnchorBottom = 0;
        c.OffsetLeft = c.OffsetRight = 0;
        c.OffsetTop = c.OffsetBottom = top;
        c.GrowHorizontal = Control.GrowDirection.Both;
        c.GrowVertical = Control.GrowDirection.End;
    }

    static PanelContainer NewPanel(string variation = "")
    {
        var p = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Stop };
        if (variation.Length > 0) p.ThemeTypeVariation = variation;
        return p;
    }

    static VBoxContainer VBox(int separation = -1)
    {
        var v = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        if (separation >= 0) v.AddThemeConstantOverride("separation", separation);
        return v;
    }

    static HBoxContainer HBox(int separation = -1)
    {
        var h = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        if (separation >= 0) h.AddThemeConstantOverride("separation", separation);
        return h;
    }

    static HudLabel Text(string text, string variation = "", int minWidth = 0)
    {
        var l = new HudLabel { ThemeTypeVariation = variation, MouseFilter = Control.MouseFilterEnum.Ignore };
        l.Set(text);
        if (minWidth > 0) l.CustomMinimumSize = new Vector2(minWidth, 0);
        return l;
    }

    static HudLabel Wrapped(string text, string variation = "")
    {
        var l = Text(text, variation);
        l.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        l.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        return l;
    }

    /// <summary>A button that never takes keyboard focus, so Space and the letter keys stay the app's.</summary>
    static Button Btn(string text, Action onPress, string tooltip = "", string variation = "")
    {
        var b = new Button { Text = text, FocusMode = Control.FocusModeEnum.None, TooltipText = tooltip, MouseFilter = Control.MouseFilterEnum.Stop };
        if (variation.Length > 0) b.ThemeTypeVariation = variation;
        b.Pressed += onPress;
        return b;
    }

    /// <summary>A button whose label carries a <c>.key</c> badge, e.g. "Asteroid mode [X]".</summary>
    static Button KeyBtn(string text, string key, Action onPress, string tooltip = "")
    {
        var b = Btn("", onPress, tooltip);
        var row = HBox(6);
        row.Alignment = BoxContainer.AlignmentMode.Center;
        row.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        row.AddChild(Text(text));
        var badge = Text(key, HudTheme.LabelKey);
        badge.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        row.AddChild(badge);
        b.AddChild(row);
        b.CustomMinimumSize = new Vector2(0, 30);
        return b;
    }

    static void SetVariation(Control c, string variation)
    {
        if (c.ThemeTypeVariation != variation) c.ThemeTypeVariation = variation;
    }

    /// <summary>An input the 8 Hz update must not write while the user holds it (the web's <c>document.activeElement</c> test).</summary>
    sealed class Held
    {
        public bool Dragging;
        /// <summary>This gesture already recorded its undo point; cleared when the drag ends.</summary>
        public bool Recorded;
    }

    static HSlider Slider(double min, double max, double step, double value, Action<double> onChange, Held held, float width = 0)
    {
        var s = new HSlider
        {
            MinValue = min, MaxValue = max, Step = step, Value = value, FocusMode = Control.FocusModeEnum.None, Scrollable = false,
            MouseFilter = Control.MouseFilterEnum.Stop, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(width, 18),
        };
        s.DragStarted += () => held.Dragging = true;
        s.DragEnded += _ =>
        {
            held.Dragging = false;
            held.Recorded = false;
        };
        s.ValueChanged += v => onChange(v);
        return s;
    }

    static OptionButton Select(params (string Text, int Id)[] items)
    {
        var o = new OptionButton { FocusMode = Control.FocusModeEnum.None, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, FitToLongestItem = false };
        foreach (var (text, id) in items) o.AddItem(text, id);
        return o;
    }

    static CheckBox Toggle(string text, Action<bool> onToggle)
    {
        var c = new CheckBox { Text = text, FocusMode = Control.FocusModeEnum.None, AutowrapMode = TextServer.AutowrapMode.WordSmart, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        c.Toggled += on => onToggle(on);
        return c;
    }

    /// <summary>A labelled field: "Name  value" on one line, the input under it (the web's <c>label.field</c> + <c>.val</c>).</summary>
    static VBoxContainer Field(string name, Control input, out HudLabel value, string note = "")
    {
        var v = VBox(2);
        var head = HBox(4);
        head.AddChild(Text(name));
        if (note.Length > 0) head.AddChild(Text(note, HudTheme.LabelMuted));
        value = Text("", HudTheme.LabelVal);
        value.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        value.HorizontalAlignment = HorizontalAlignment.Right;
        head.AddChild(value);
        v.AddChild(head);
        v.AddChild(input);
        return v;
    }
}
