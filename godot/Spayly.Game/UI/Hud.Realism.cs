using System;
using System.Collections.Generic;
using System.Globalization;
using Godot;

namespace Spayly.Game.UI;

/// <summary>
/// The Realism panel: every entry of <see cref="Realism"/>, grouped by area, each row a tag chip (SCI teal, GAME the HUD's
/// amber, ART magenta), the feature and its why. Tag filters with counts; entries pre-written for unmerged branches show
/// only under the Pending filter. Opened by the top bar's Realism button or the <c>realism:1</c> capture verb; Esc, a click
/// on the dimmed backdrop or × closes it. The View panel's diorama, falloff, bloom, belt and Director controls carry their
/// entries as tooltips.
///
/// Built once, and rebuilt only when <see cref="Realism.Version"/> moves (some file registered), which the 8 Hz panel update
/// polls with one int compare; the filters only flip Visible on rows that exist, so nothing here allocates per frame.
/// </summary>
public partial class Hud
{
    /// <summary>Chip colours by <see cref="RealismTag"/>: SCI teal, GAME amber (the presets' accent), ART magenta.</summary>
    static readonly Color[] TagColors = [new("#5fd4c4"), HudTheme.Accent2, new("#f27ad6")];
    static readonly StyleBoxFlat[] ChipBoxes = [ChipBox(TagColors[0]), ChipBox(TagColors[1]), ChipBox(TagColors[2])];

    Control realism = null!;
    PanelContainer realismPanel = null!;
    Button realismButton = null!;
    VBoxContainer realismList = null!;
    HudLabel realismCounts = null!;
    /// <summary>SCI, GAME, ART, then Pending.</summary>
    readonly Button[] realismFilter = new Button[4];
    readonly bool[] realismShow = [true, true, true, false];
    readonly List<RealismRow> realismRows = [];
    readonly List<RealismHead> realismHeads = [];
    readonly int[] realismTagCount = new int[3];
    int realismBuilt = int.MinValue, realismLive, realismPending;

    readonly record struct RealismRow(Control Node, RealismTag Tag, RealismArea Area, bool Pending);
    readonly record struct RealismHead(Control Node, HudLabel Label, RealismArea Area);

    static StyleBoxFlat ChipBox(Color c) => HudTheme.Box(new Color(c, 0.14f), new Color(c, 0.5f), 4, 0, 1);

    void BuildRealism()
    {
        // Help's backdrop, plus: a click on the dimmed stage closes the panel (the panel stops its own clicks); the wheel does not.
        var backdrop = new ColorRect { Color = new Color(0, 0, 0, 0.45f), MouseFilter = Control.MouseFilterEnum.Stop, Visible = false };
        backdrop.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        backdrop.GuiInput += e =>
        {
            if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left or MouseButton.Right }) ToggleRealism(false);
        };
        var centre = new CenterContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        centre.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        var panel = NewPanel();
        panel.CustomMinimumSize = new Vector2(720, 0);
        var v = VBox(8);

        var head = HBox(6);
        var title = Text("Realism", HudTheme.LabelTitle);
        title.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        head.AddChild(title);
        var close = Btn("×", () => ToggleRealism(false), "Close (Esc)", HudTheme.ButtonSmall);
        close.SizeFlagsVertical = Control.SizeFlags.ShrinkBegin;
        head.AddChild(close);
        v.AddChild(head);
        v.AddChild(Wrapped("What in spayly is established science, what is a simplification for play and what is art. The simulation " +
            "runs at true scale with mass and momentum conserved; every shortcut it takes is listed here.", HudTheme.LabelMuted));

        var legend = new HFlowContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        legend.AddThemeConstantOverride("h_separation", 16);
        foreach (var t in Enum.GetValues<RealismTag>())
        {
            var item = HBox(6);
            item.AddChild(Chip(t));
            item.AddChild(Text(Realism.TagMeaning(t), HudTheme.LabelSmallMuted));
            legend.AddChild(item);
        }
        v.AddChild(legend);

        var filters = HBox(6);
        for (var i = 0; i < realismFilter.Length; i++)
        {
            var idx = i;
            var tip = i < 3 ? $"Show {Realism.TagName((RealismTag)i)} entries" : "Show entries written ahead for work that is not in this build yet";
            var b = Btn("", () => ToggleRealismFilter(idx), tip);
            b.AddThemeColorOverride("font_color", i < 3 ? TagColors[i] : HudTheme.Muted);
            realismFilter[i] = b;
            filters.AddChild(b);
        }
        realismCounts = Text("", HudTheme.LabelMonoMuted);
        realismCounts.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        realismCounts.HorizontalAlignment = HorizontalAlignment.Right;
        realismCounts.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        filters.AddChild(realismCounts);
        v.AddChild(filters);

        // 520 px of list keeps the whole card inside the 900 px reference viewport.
        var scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, CustomMinimumSize = new Vector2(0, 520) };
        realismList = VBox(6);
        realismList.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        scroll.AddChild(realismList);
        v.AddChild(scroll);

        panel.AddChild(v);
        centre.AddChild(panel);
        backdrop.AddChild(centre);
        realism = backdrop;
        realismPanel = panel;
        root.AddChild(backdrop);
        RebuildRealism();
    }

    static HudLabel Chip(RealismTag t)
    {
        var l = Text(Realism.TagName(t), HudTheme.LabelMono, 46);
        l.HorizontalAlignment = HorizontalAlignment.Center;
        l.AddThemeStyleboxOverride("normal", ChipBoxes[(int)t]);
        l.AddThemeColorOverride("font_color", TagColors[(int)t]);
        l.AddThemeFontSizeOverride("font_size", 10);
        return l;
    }

    /// <summary>Throw the rows away and make them again from the registry; runs only when its version moved.</summary>
    void RebuildRealism()
    {
        // Version before the snapshot: a registration landing between the two reads is picked up by the next poll.
        realismBuilt = Realism.Version;
        var entries = Realism.Snapshot();
        foreach (var c in realismList.GetChildren())
        {
            realismList.RemoveChild(c);
            c.QueueFree();
        }
        realismRows.Clear();
        realismHeads.Clear();
        Array.Clear(realismTagCount);
        realismLive = realismPending = 0;
        foreach (var area in Enum.GetValues<RealismArea>())
        {
            var label = H3("");
            var pad = new MarginContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
            pad.AddThemeConstantOverride("margin_top", realismHeads.Count == 0 ? 0 : 8);
            pad.AddChild(label);
            realismList.AddChild(pad);
            realismHeads.Add(new RealismHead(pad, label, area));
            var inArea = 0;
            // This build's entries first, the pending ones after them.
            foreach (var pendingPass in (ReadOnlySpan<bool>)[false, true])
                foreach (var e in entries)
                {
                    if (e.Area != area || e.Pending != pendingPass) continue;
                    var row = RealismRowNode(e);
                    realismList.AddChild(row);
                    realismRows.Add(new RealismRow(row, e.Tag, area, e.Pending));
                    if (e.Pending)
                    {
                        realismPending++;
                        continue;
                    }
                    realismLive++;
                    realismTagCount[(int)e.Tag]++;
                    inArea++;
                }
            label.Set(string.Create(CultureInfo.InvariantCulture, $"{Realism.AreaName(area)} · {inArea}"));
        }
        for (var i = 0; i < 3; i++) realismFilter[i].Text = string.Create(CultureInfo.InvariantCulture, $"{Realism.TagName((RealismTag)i)} {realismTagCount[i]}");
        realismFilter[3].Text = string.Create(CultureInfo.InvariantCulture, $"Pending {realismPending}");
        realismCounts.Set(string.Create(CultureInfo.InvariantCulture, $"{realismLive} in this build"));
        ApplyRealismFilter();
        ApplyRealismTooltips();
    }

    Control RealismRowNode(RealismEntry e)
    {
        var row = HBox(10);
        var chip = Chip(e.Tag);
        chip.SizeFlagsVertical = Control.SizeFlags.ShrinkBegin;
        row.AddChild(chip);
        var text = VBox(1);
        text.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        var name = HBox(8);
        var feature = Text(e.Feature);
        feature.AddThemeFontOverride("font", HudTheme.SansSemi);
        feature.AddThemeColorOverride("font_color", Colors.White);
        name.AddChild(feature);
        if (e.Pending) name.AddChild(Text($"pending · {Realism.OwnerLabel(e.Owner)} branch", HudTheme.LabelSmallMuted));
        text.AddChild(name);
        var why = Wrapped(e.Why);
        why.AddThemeFontSizeOverride("font_size", 12);
        text.AddChild(why);
        row.AddChild(text);
        if (e.Pending) row.Modulate = new Color(1, 1, 1, 0.6f);
        return row;
    }

    bool RealismShown(in RealismRow r) => realismShow[(int)r.Tag] && (!r.Pending || realismShow[3]);

    void ToggleRealismFilter(int i)
    {
        realismShow[i] = !realismShow[i];
        ApplyRealismFilter();
    }

    void ApplyRealismFilter()
    {
        foreach (var r in realismRows) r.Node.Visible = RealismShown(r);
        foreach (var h in realismHeads)
        {
            var any = false;
            foreach (var r in realismRows)
            {
                if (r.Area != h.Area || !RealismShown(r)) continue;
                any = true;
                break;
            }
            h.Node.Visible = any;
        }
        for (var i = 0; i < realismFilter.Length; i++) Variation(realismFilter[i], realismShow[i] ? HudTheme.ButtonActive : "");
    }

    /// <summary>The View panel's controls carry their registry entries; set on every rebuild so a re-registration shows.</summary>
    void ApplyRealismTooltips()
    {
        diorama.TooltipText = Realism.Tooltip(Realism.Diorama, Realism.TrueScale);
        falloff.TooltipText = Realism.Tooltip(Realism.FalloffPhysical, Realism.FalloffBalanced);
        bloom.TooltipText = Realism.Tooltip(Realism.Bloom);
        belt.TooltipText = Realism.Tooltip(Realism.Belts);
        directorSelect.TooltipText = Realism.Tooltip(Realism.Director);
        lensingBox.TooltipText = Realism.Tooltip("Black-hole lensing", "Several black holes");
        discBox.TooltipText = Realism.Tooltip("Accretion-fed discs", "Viscous delay", "Disc display brightness", "Accretion disc temperature", "Disc size");
        dopplerBox.TooltipText = Realism.Tooltip("Disc Doppler beaming");
        tidalBox.TooltipText = Realism.Tooltip("Roche and tidal limits", "Tidal energy spread", "Partial disruption ramp");
        sphSelect.TooltipText = Realism.Tooltip("Hero SPH collisions", "Tillotson equation of state", "SPH self-gravity");
    }

    /// <summary>Rebuild if a registration moved <see cref="Realism.Version"/>; polled by the 8 Hz panel update.</summary>
    void RefreshRealism()
    {
        if (Realism.Version != realismBuilt) RebuildRealism();
    }

    /// <summary>Show or hide the Realism panel; no argument flips it. Opening it closes help.</summary>
    public void ToggleRealism(bool? force = null)
    {
        var on = force ?? !realism.Visible;
        if (on)
        {
            ToggleHelp(false);
            RefreshRealism();
        }
        realism.Visible = on;
    }

    public bool RealismVisible => realism.Visible;

    public override void _UnhandledKeyInput(InputEvent e)
    {
        // Not consumed, so App._UnhandledInput still gets this Esc and does the rest of what Esc does (mode, help, report).
        if (e is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Escape } && realism.Visible) realism.Visible = false;
    }

    /// <summary>
    /// Capture helper: put the pointer on a View control (diorama, falloff, bloom, belt, director) so that its Realism
    /// tooltip opens after gui/timers/tooltip_delay_sec (0.5 s). Logs where it went and whether the control lies inside the
    /// View panel's scrolled area (outside it the pointer lands on something else). False for an unknown name.
    /// </summary>
    public bool HoverRealismControl(string name)
    {
        Control? c = name switch
        {
            "diorama" => diorama,
            "falloff" => falloff,
            "bloom" => bloom,
            "belt" => belt,
            "director" => directorSelect,
            "lensing" => lensingBox,
            "disc" => discBox,
            "doppler" => dopplerBox,
            "tidal" => tidalBox,
            "sph" => sphSelect,
            _ => null,
        };
        if (c == null) return false;
        ToggleRealism(false);
        ToggleHelp(false);
        var p = c.GetParent();
        while (p != null && p is not ScrollContainer) p = p.GetParent();
        var scroll = p as ScrollContainer;
        if (scroll != null && !scroll.GetGlobalRect().Encloses(c.GetGlobalRect()))
        {
            // Below the fold at 900 px (falloff is): scroll it into view as a user would, and hover once the container
            // has re-sorted its children, which happens later in the frame.
            scroll.EnsureControlVisible(c);
            GetTree().CreateTimer(0.1).Timeout += () => HoverAt(c, name, scroll);
            return true;
        }
        HoverAt(c, name, scroll);
        return true;
    }

    void HoverAt(Control c, string name, ScrollContainer? scroll)
    {
        var rect = c.GetGlobalRect();
        var at = rect.GetCenter();
        var inside = scroll == null || scroll.GetGlobalRect().Encloses(rect);
        Input.WarpMouse(at);
        Input.ParseInputEvent(new InputEventMouseMotion { Position = at, GlobalPosition = at });
        GD.Print(string.Create(CultureInfo.InvariantCulture,
            $"[realism] hover {name} at {at.X:F0},{at.Y:F0} inside_scroll={inside} tooltip_chars={c.TooltipText.Length}"));
    }

    /// <summary>The panel's rect, counts and tooltip sizes, for scripted checks.</summary>
    public string RealismSummary()
    {
        static string R(Control c) => c.IsVisibleInTree()
            ? string.Create(CultureInfo.InvariantCulture, $"({c.GlobalPosition.X:F0},{c.GlobalPosition.Y:F0} {c.Size.X:F0}x{c.Size.Y:F0})")
            : "hidden";
        var shown = 0;
        foreach (var r in realismRows) if (r.Node.Visible) shown++;
        return string.Create(CultureInfo.InvariantCulture,
            $"panel={R(realismPanel)} button={R(realismButton)} version={realismBuilt} live={realismLive} pending={realismPending} " +
            $"SCI={realismTagCount[0]} GAME={realismTagCount[1]} ART={realismTagCount[2]} rows={realismRows.Count} shown={shown} " +
            $"tooltip_chars diorama={diorama.TooltipText.Length} falloff={falloff.TooltipText.Length} bloom={bloom.TooltipText.Length} lensing={lensingBox.TooltipText.Length} disc={discBox.TooltipText.Length} doppler={dopplerBox.TooltipText.Length} tidal={tidalBox.TooltipText.Length}");
    }
}
