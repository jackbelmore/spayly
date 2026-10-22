using System;
using System.Collections.Generic;
using System.Globalization;
using Godot;
using Spayly.Game.App;
using Spayly.Game.Sim;
using Spayly.Sim;

namespace Spayly.Game.UI;

/// <summary>Top bar, left column (body list, View panel) and the bottom mode panel (hud.ts constructor, update, setMode).</summary>
public partial class Hud
{
    // ---------- top bar ----------
    HudLabel date = null!, warpLabel = null!, fps = null!, physics = null!;
    Button pause = null!, rewind = null!, skip = null!, limited = null!;
    ScrollContainer viewScroll = null!;
    /// <summary>The Moons dropdown pulses until this time (<see cref="now"/>) after the limited chip is pressed.</summary>
    double moonsFlashUntil;
    string shownSkip = "";
    HSlider warp = null!;
    readonly Held warpHeld = new();
    bool shownPaused, shownRewind, shownLimited;
    string shownWarp = "";
    int shownFps = -1, shownBodies = -1, shownSteps = -1;

    // ---------- left column ----------
    VBoxContainer bodyList = null!;
    /// <summary>The web's <c>if (n++ > 80) break</c> lets 81 rows through.</summary>
    const int MaxRows = 81;
    readonly Button[] rows = new Button[MaxRows];
    readonly HudLabel[] rowKinds = new HudLabel[MaxRows];
    readonly Body?[] rowBodies = new Body?[MaxRows];
    readonly string[] rowColors = new string[MaxRows];
    int listVersion = -1;
    string listSelected = "";
    static readonly ImageTexture DotBig = HudTheme.Disc(8, Colors.White);
    static readonly ImageTexture DotSmall = HudTheme.Disc(6, Colors.White);
    OptionButton textures = null!, moons = null!, falloff = null!, sphSelect = null!, qualitySelect = null!;
    CheckBox diorama = null!, orbits = null!, trails = null!, labelsBox = null!, belt = null!, bloom = null!;
    OptionButton directorSelect = null!;
    CheckBox lensingBox = null!, discBox = null!, dopplerBox = null!, tidalBox = null!;
    static readonly int[] TextureSizes = [16384, 8192, 4096, 2048];
    static readonly double[] Falloffs = [0.5, 2, 0];

    // ---------- bottom ----------
    Button modeExplore = null!, modeAsteroid = null!, modeErase = null!, modeHole = null!;
    Control erasePanel = null!, asteroidPanel = null!;
    Button barHide = null!, barShow = null!;
    HSlider astDiam = null!, astSpeed = null!;
    /// <summary>Top of the Speed slider, log₁₀ km/s.</summary>
    const double SpeedLogMax = 5;
    readonly ColorRect circularMark = new() { Color = new Color(0.45f, 0.75f, 1f) }, escapeMark = new() { Color = new Color(1f, 0.6f, 0.25f) };
    readonly Held astDiamHeld = new(), astSpeedHeld = new();
    HudLabel astDiamVal = null!, astSpeedVal = null!, aim = null!, astForecast = null!, showerVal = null!;
    CheckBox showerBox = null!;
    HSlider showerSlider = null!;
    readonly Held showerHeld = new();
    OptionButton astComp = null!;
    readonly List<Button> presetButtons = [];
    string activePreset = "";
    readonly Dictionary<Control, string> variations = [];

    void Variation(Control c, string v)
    {
        if (variations.TryGetValue(c, out var cur) && cur == v) return;
        variations[c] = v;
        c.ThemeTypeVariation = v;
    }

    void BuildTop()
    {
        var top = NewPanel();
        top.AddThemeStyleboxOverride("panel", HudTheme.PanelBox(14, 8));
        AnchorTopCentre(top, 12);
        var row = HBox(16);
        row.AddChild(Text("spayly", HudTheme.LabelBrand));

        var clock = VBox(0);
        // Fixed: wide enough for "◀ 12 d/s asked · 0.62 s/s actual", so the bar does not resize when the warp is limited.
        clock.CustomMinimumSize = new Vector2(240, 0);
        date = Text("—", HudTheme.LabelMono);
        warpLabel = Text("real time", HudTheme.LabelMono);
        warpLabel.AddThemeColorOverride("font_color", HudTheme.Muted);
        warpLabel.ClipText = true;
        clock.AddChild(date);
        clock.AddChild(warpLabel);
        clock.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        row.AddChild(clock);

        var time = HBox(6);
        time.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        rewind = Btn("◀◀", () => app?.ToggleRewind(), "Rewind (R)");
        pause = Btn("❙❙", () => app?.TogglePause(), "Pause (Space)");
        pause.CustomMinimumSize = new Vector2(36, 0);
        pause.AddThemeFontOverride("font", HudTheme.SansSymbols);
        time.AddChild(rewind);
        time.AddChild(pause);
        time.AddChild(Btn("−", () => { if (app != null) app.SetWarp(app.Warp / 4); }, "Slower ([)"));
        warp = Slider(0, 8, 0.01, 0, v => app?.SetWarp(Math.Pow(10, v)), warpHeld, 140);
        warp.SizeFlagsHorizontal = Control.SizeFlags.Fill;
        warp.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        warp.TooltipText = "Time warp";
        time.AddChild(warp);
        time.AddChild(Btn("+", () => { if (app != null) app.SetWarp(app.Warp * 4); }, "Faster (])"));
        // Fixed width, wide enough for "Skipping… 100 y": the label changes while the bar is centred.
        skip = Btn("⏭ Skip", () => { if (app != null) { if (app.Skipping) app.CancelSkip(); else app.StartSkip(); } }, "Skip to the next notable event: time runs as fast as the physics allows, then drops to real time (N)");
        skip.CustomMinimumSize = new Vector2(132, 0);
        skip.ClipText = true;
        skip.AddThemeFontOverride("font", HudTheme.SansSymbols);
        time.AddChild(skip);
        row.AddChild(time);

        // Fixed widths: the bar is centred, so text that changes width would slide the controls under the pointer.
        var status = HBox(8);
        status.CustomMinimumSize = new Vector2(290, 0);
        status.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        fps = Text("", HudTheme.LabelMonoMuted, 46);
        fps.HorizontalAlignment = HorizontalAlignment.Right;
        physics = Text("", HudTheme.LabelMonoMuted);
        physics.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        physics.HorizontalAlignment = HorizontalAlignment.Right;
        physics.ClipText = true;
        // A filled amber button, not a quiet label: the dial and the real speed disagree, and a click says what to do about it.
        limited = Btn("limited ▸", () => ShowMoonsHint(), "The time warp is asking for more integration steps than one frame can afford, so time is running slower than the dial says (the actual rate is shown beside the dial). Nothing is being skipped. Click to see the Moons setting: fewer moons, or a lower warp, clears it.", HudTheme.PresetActive);
        limited.CustomMinimumSize = new Vector2(78, 0);
        limited.Modulate = new Color(1, 1, 1, 0);
        limited.MouseFilter = Control.MouseFilterEnum.Ignore;
        status.AddChild(fps);
        status.AddChild(physics);
        status.AddChild(limited);
        row.AddChild(status);
        row.AddChild(historyButton =Btn("History", () => ToggleHistory(), "Every undo point, newest first: click one to jump there (U)"));
        row.AddChild(realismButton = Btn("Realism", () => ToggleRealism(), "What is established science, what is a simplification for play, what is art"));
        row.AddChild(Btn("?", () => ToggleHelp(), "Help (H)"));
        top.AddChild(row);
        root.AddChild(top);
        topPanel = top;
    }

    void UpdateTop(in HudInfo info)
    {
        date.Set(Format.Date(info.Jd));
        // Limited: say how fast time really runs, since the dial's number is a request the budget is not meeting.
        var behind = info.Limited && !info.Paused;
        warpLabel.Set(info.Paused ? "paused"
            : behind ? $"{Format.WarpShort(info.Rewind ? -info.Asked : info.Asked)} asked · {Format.WarpShort(info.Achieved)} actual"
            : Format.Warp(info.Rewind ? -info.Warp : info.Warp));
        if (behind != (shownWarp == "limited"))
        {
            shownWarp = behind ? "limited" : "";
            warpLabel.AddThemeColorOverride("font_color", behind ? HudTheme.Accent2 : HudTheme.Muted);
        }
        if (!warpHeld.Dragging) warp.SetValueNoSignal(Math.Log10(info.Warp));
        var f = (int)Math.Round(info.Fps);
        if (f != shownFps)
        {
            shownFps = f;
            fps.Set(f.ToString(CultureInfo.InvariantCulture) + " fps");
        }
        if (info.Bodies != shownBodies || info.Substeps != shownSteps)
        {
            shownBodies = info.Bodies;
            shownSteps = info.Substeps;
            physics.Set(string.Create(CultureInfo.InvariantCulture, $"{info.Bodies} bodies · {info.Substeps} steps"));
        }
        if (info.Limited != shownLimited)
        {
            shownLimited = info.Limited;
            limited.Modulate = new Color(1, 1, 1, info.Limited ? 1 : 0);
            limited.MouseFilter = info.Limited ? Control.MouseFilterEnum.Stop : Control.MouseFilterEnum.Ignore;
        }
        // ❙❙ while running, ▶ while paused: the button shows what pressing it does next.
        if (info.Paused != shownPaused)
        {
            shownPaused = info.Paused;
            pause.Text = info.Paused ? "▶" : "❙❙";
            pause.TooltipText = info.Paused ? "Play (Space)" : "Pause (Space)";
            Variation(pause, info.Paused ? HudTheme.ButtonActive : "");
        }
        var skipText = info.Skipping ? "Skipping… " + Format.ElapsedShort(info.SkipElapsed) : "⏭ Skip";
        if (skipText != shownSkip)
        {
            shownSkip = skipText;
            skip.Text = skipText;
            skip.TooltipText = info.Skipping ? "Cancel the skip (N or any key)" : "Skip to the next notable event: time runs as fast as the physics allows, then drops to real time (N)";
            Variation(skip, info.Skipping ? HudTheme.ButtonActive : "");
        }
        if (info.Rewind != shownRewind)
        {
            shownRewind = info.Rewind;
            Variation(rewind, info.Rewind ? HudTheme.ButtonActive : "");
        }
    }

    void BuildLeft()
    {
        var left = VBox(10);
        left.Position = new Vector2(12, 74);
        // max-height: calc(100% - 160px) at 900 px; the View panel scrolls inside what the body list leaves.
        left.Size = new Vector2(240, 740);
        left.CustomMinimumSize = new Vector2(240, 740);

        var bodies = NewPanel();
        var bv = VBox(4);
        bv.AddChild(H3("Bodies"));
        var scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, CustomMinimumSize = new Vector2(0, 262) };
        bodyList = VBox(2);
        bodyList.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        for (var i = 0; i < MaxRows; i++)
        {
            var idx = i;
            var b = Btn("", () => RowPressed(idx), "", HudTheme.ListRow);
            b.Alignment = HorizontalAlignment.Left;
            b.Icon = DotBig;
            b.ClipText = true;
            b.Visible = false;
            var kind = Text("", HudTheme.LabelMonoMuted);
            kind.AddThemeFontSizeOverride("font_size", 10);
            kind.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            kind.OffsetRight = -8;
            kind.HorizontalAlignment = HorizontalAlignment.Right;
            kind.VerticalAlignment = VerticalAlignment.Center;
            b.AddChild(kind);
            rows[i] = b;
            rowKinds[i] = kind;
            rowColors[i] = "";
            bodyList.AddChild(b);
        }
        scroll.AddChild(bodyList);
        bv.AddChild(scroll);
        bodies.AddChild(bv);
        left.AddChild(bodies);

        var view = NewPanel();
        view.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        var vscroll = viewScroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        var v = VBox(4);
        v.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        v.AddChild(H3("View"));
        var presets = HBox(6);
        foreach (var (text, tip, act) in new (string, string, Action)[]
                 {
                     ("Tabletop", "Whole system on a table (T)", () => app?.ViewTabletop()),
                     ("Inner system", "", () => app?.ViewInner()),
                     ("Outer system", "", () => app?.ViewOuter()),
                 })
        {
            var b = Btn(text, act, tip, HudTheme.ButtonTight);
            b.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            // Wrap between words only, as a browser button does: "Tabletop" keeps its width and the others give way.
            if (text.Contains(' ')) b.AutowrapMode = TextServer.AutowrapMode.Word;
            b.AddThemeConstantOverride("line_spacing", -2);
            viewButtons[presets.GetChildCount()] = b;
            presets.AddChild(b);
        }
        v.AddChild(presets);

        textures = Select(("Ultra — up to 16K", 16384), ("High — up to 8K", 8192), ("Medium — up to 4K", 4096), ("Low — up to 2K", 2048));
        textures.ItemSelected += i => SetTextureCeiling(textures.GetItemId((int)i));
        v.AddChild(Labelled("Texture detail", "(maps sharpen as you fly closer)", textures));
        moons = Select(("All", (int)MoonDetail.All), ("Major only", (int)MoonDetail.Major), ("None", (int)MoonDetail.None));
        moons.ItemSelected += i => app?.SetMoonDetail((MoonDetail)moons.GetItemId((int)i));
        v.AddChild(Labelled("Moons", "(fewer = higher warp)", moons));

        diorama = Toggle("Diorama scale (bodies enlarged, physics untouched)", on => { if (app != null) app.Settings.Diorama = on; });
        orbits = Toggle("Orbits", on => { if (app != null) app.Settings.Orbits = on; });
        trails = Toggle("Trails", on => { if (app != null) app.Settings.Trails = on; });
        labelsBox = Toggle("Labels", on => { if (app != null) app.Settings.Labels = on; });
        belt = Toggle("Asteroid and Kuiper belts", on => { if (app != null) app.Settings.Belt = on; });
        bloom = Toggle("Bloom", on => { if (app != null) app.Settings.Bloom = on; });
        foreach (var c in new[] { diorama, orbits, trails, labelsBox, belt, bloom }) v.AddChild(c);
        directorSelect = Select(("Off", (int)DirectorMode.Off), ("Notable and major events", (int)DirectorMode.Notable), ("Major events only", (int)DirectorMode.Major));
        directorSelect.ItemSelected += i => app?.SetDirector((DirectorMode)directorSelect.GetItemId((int)i));
        v.AddChild(Labelled("Director camera", "(cuts to events; key C)", directorSelect));
        lensingBox = Toggle("Black-hole lensing", on => app?.SetLensing(on));
        discBox = Toggle("Accretion discs", on => app?.SetDisc(on));
        dopplerBox = Toggle("Disc Doppler shift", on => app?.SetDoppler(on));
        tidalBox = Toggle("Tidal disruption", on => app?.SetTidal(on));
        foreach (var c in new[] { lensingBox, discBox, dopplerBox, tidalBox }) v.AddChild(c);

        falloff = Select(("Balanced", 0), ("Physical (1/r²)", 1), ("Flat", 2));
        falloff.ItemSelected += i => { if (app != null) app.Settings.Falloff = Falloffs[i]; };
        v.AddChild(Labelled("Sunlight falloff", "", falloff));
        sphSelect = Select(("Off", (int)SphMode.Off), ("Fast — 30k particles", (int)SphMode.Fast), ("Hero — 100k particles", (int)SphMode.Hero));
        sphSelect.ItemSelected += i => app?.SetSph((SphMode)sphSelect.GetItemId((int)i));
        v.AddChild(Labelled("Giant impacts as fluid (SPH)", "(the collision you watch)", sphSelect));
        qualitySelect = Select(("Low — simple flash", (int)ImpactQuality.Low), ("Medium — scaled effects", (int)ImpactQuality.Medium), ("High — fluid breakup when watched", (int)ImpactQuality.High));
        qualitySelect.ItemSelected += i => app?.SetImpactQuality((ImpactQuality)qualitySelect.GetItemId((int)i));
        v.AddChild(Labelled("Impact quality", "(High falls back to Medium on slow frames)", qualitySelect));
        vscroll.AddChild(v);
        view.AddChild(vscroll);
        left.AddChild(view);
        root.AddChild(left);
        leftColumn = left;
    }

    /// <summary>
    /// The limited chip's click: say what costs steps and pulse the Moons dropdown. It changes nothing itself: fewer moons
    /// drop bodies from the physics, which is the player's call.
    /// </summary>
    void ShowMoonsHint()
    {
        var a = app;
        if (a == null) return;
        viewScroll.EnsureControlVisible(moons);
        moonsFlashUntil = now + 3;
        var m = a.Settings.Moons;
        a.Hint(m == Spayly.Game.Sim.MoonDetail.None
            ? "Time is running slower than the dial: even with no moons this warp needs more steps than a frame can afford. Lower the warp"
            : $"Time is running slower than the dial: moons cost steps. Set Moons to {(m == Spayly.Game.Sim.MoonDetail.All ? "Major only or None" : "None")} in the View panel (left); the warp then holds", 6);
    }

    static HudLabel H3(string text)
    {
        var l = Text(text, HudTheme.LabelH3);
        l.Uppercase = true;
        return l;
    }

    static VBoxContainer Labelled(string name, string note, Control input)
    {
        var box = VBox(2);
        box.AddThemeConstantOverride("separation", 2);
        var head = Wrapped(name);
        box.AddChild(head);
        if (note.Length > 0) box.AddChild(Wrapped(note, HudTheme.LabelSmallMuted));
        box.AddChild(input);
        var pad = new MarginContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        pad.AddThemeConstantOverride("margin_top", 6);
        pad.AddChild(box);
        var wrap = VBox(0);
        wrap.AddChild(pad);
        return wrap;
    }

    /// <summary>
    /// The App has no SetTextureQuality yet: write the ceiling as the web's setTextureQuality does (settings.textures) and
    /// give its hint. Streaming reads Settings.Textures through ViewContext.TextureCeiling every frame.
    /// </summary>
    void SetTextureCeiling(int width)
    {
        if (app == null) return;
        app.Settings.Textures = width;
        // Word for word the web's toast (app.ts setTextureQuality), which has no 16K branch: Ultra reads "up to 8K" there too.
        app.Hint($"Texture detail: up to {(width >= 8192 ? "8K" : width >= 4096 ? "4K" : "2K")} when close", 2.6);
    }

    void BindPanels()
    {
        var a = app!;
        // Offer only the sizes this GPU can actually hold.
        var max = RenderingServer.GetRenderingDevice()?.LimitGet(RenderingDevice.Limit.MaxTextureSize2D) ?? 16384;
        for (var i = textures.ItemCount - 1; i >= 0; i--)
            if ((ulong)textures.GetItemId(i) > max) textures.RemoveItem(i);
        var idx = textures.GetItemIndex(a.Settings.Textures);
        textures.Select(idx >= 0 ? idx : 0);
        var moonCount = 0;
        foreach (var b in a.Universe.Bodies) if (b.Kind == BodyKind.Moon) moonCount++;
        if (a.Settings.Moons == MoonDetail.All) moons.SetItemText(0, $"All {moonCount}");
        moons.Select(moons.GetItemIndex((int)a.Settings.Moons));
        falloff.Select(Math.Max(0, Array.IndexOf(Falloffs, a.Settings.Falloff)));
        SyncToggles();
    }

    /// <summary>
    /// Checkboxes and selects follow the settings (keys, view presets and capture verbs change them too); no signal, so
    /// nothing loops. A select whose list is open is left alone, as the web leaves the focused input.
    /// </summary>
    void SyncToggles()
    {
        var s = app!.Settings;
        Sync(diorama, s.Diorama);
        Sync(orbits, s.Orbits);
        Sync(trails, s.Trails);
        Sync(labelsBox, s.Labels);
        Sync(belt, s.Belt);
        Sync(bloom, s.Bloom);
        Sync(lensingBox, app.BlackHoles.Lensing);
        Sync(discBox, app.BlackHoles.Disc);
        Sync(dopplerBox, app.BlackHoles.Doppler);
        Sync(tidalBox, s.Tidal);
        Pick(textures, textures.GetItemIndex(s.Textures));
        Pick(moons, moons.GetItemIndex((int)s.Moons));
        Pick(falloff, Array.IndexOf(Falloffs, s.Falloff));
        Pick(sphSelect, sphSelect.GetItemIndex((int)s.Sph));
        Pick(qualitySelect, qualitySelect.GetItemIndex((int)s.ImpactQuality));
        Pick(directorSelect, directorSelect.GetItemIndex((int)s.Director));
        static void Sync(CheckBox c, bool on)
        {
            if (c.ButtonPressed != on) c.SetPressedNoSignal(on);
        }
        // OptionButton.Select emits no item_selected.
        static void Pick(OptionButton o, int idx)
        {
            if (idx >= 0 && o.Selected != idx && !o.GetPopup().Visible) o.Select(idx);
        }
    }

    partial void UpdatePanels(in HudInfo info)
    {
        RefreshRealism();
        if (app == null) return;
        SyncToggles();
        RefreshBodyList();
        RefreshHistory();
    }

    /// <summary>Rebuild the list when bodies appear or vanish or the selection moves (hud.ts rebuildBodyList); rows are pooled.</summary>
    public void RefreshBodyList()
    {
        var a = app;
        if (a == null) return;
        var selId = a.Selected?.Id ?? "";
        if (a.BodiesVersion == listVersion && selId == listSelected) return;
        var bodies = a.Universe.Bodies;
        var n = Math.Min(bodies.Count, MaxRows);
        for (var i = 0; i < MaxRows; i++)
        {
            var row = rows[i];
            if (i >= n)
            {
                rowBodies[i] = null;
                row.Visible = false;
                continue;
            }
            var b = bodies[i];
            var sat = b.Kind == BodyKind.Moon;
            var active = ReferenceEquals(b, a.Selected);
            if (!ReferenceEquals(rowBodies[i], b))
            {
                rowBodies[i] = b;
                row.Text = b.Name;
                rowKinds[i].Set(KindLabel(b.Kind));
                row.Icon = sat ? DotSmall : DotBig;
            }
            if (rowColors[i] != b.Visual.Color)
            {
                rowColors[i] = b.Visual.Color;
                var c = Color.FromString(b.Visual.Color, HudTheme.Muted);
                foreach (var slot in IconSlots) row.AddThemeColorOverride(slot, c);
            }
            Variation(row, sat ? active ? HudTheme.ListRowSatActive : HudTheme.ListRowSat : active ? HudTheme.ListRowActive : HudTheme.ListRow);
            row.Visible = true;
        }
        listVersion = a.BodiesVersion;
        listSelected = selId;
    }

    static readonly string[] IconSlots = ["icon_normal_color", "icon_hover_color", "icon_pressed_color", "icon_hover_pressed_color", "icon_focus_color"];

    /// <summary>One click selects and flies above, as the web's row handler does (only the stage and labels need a double-click).</summary>
    void RowPressed(int i)
    {
        var a = app;
        var b = rowBodies[i];
        if (a == null || b == null) return;
        a.Select(b);
        a.FlyAbove(b);
    }

    static string KindLabel(BodyKind k) => k switch
    {
        BodyKind.Star => "star",
        BodyKind.Planet => "planet",
        BodyKind.Moon => "moon",
        BodyKind.Asteroid => "asteroid",
        BodyKind.BlackHole => "black hole",
        _ => "fragment",
    };

    void BuildBottom()
    {
        var panel = NewPanel();
        panel.AnchorLeft = panel.AnchorRight = 0.5f;
        panel.AnchorTop = panel.AnchorBottom = 1;
        // width: min(880px, 100% - 560px) at 1600 px
        panel.OffsetLeft = -440;
        panel.OffsetRight = 440;
        panel.OffsetTop = panel.OffsetBottom = -14;
        panel.GrowVertical = Control.GrowDirection.Begin;
        var col = VBox(10);
        var modes = HBox(6);
        modeExplore = Btn("Explore", () => app?.SetMode(Mode.Explore));
        modeExplore.CustomMinimumSize = new Vector2(0, 30);
        modeAsteroid = KeyBtn("Asteroid mode", "X", () => app?.SetMode(Mode.Asteroid));
        modeErase = KeyBtn("Erase rocks", "Z", () =>
        {
            if (app != null) app.SetMode(app.Mode == Mode.Erase ? Mode.Explore : Mode.Erase);
        }, "Drag over the view to delete asteroids and fragments");
        modeHole = KeyBtn("Black hole", "B", () =>
        {
            if (app != null) app.SetMode(app.Mode == Mode.BlackHole ? Mode.Explore : Mode.BlackHole);
        }, "Place a black hole of any mass, from a stellar remnant to M87*");
        modeKeep = KeyBtn("Keep inside", "K", () =>
        {
            if (app != null) app.SetMode(app.Mode == Mode.KeepInside ? Mode.Explore : Mode.KeepInside);
        }, "Drag out a sphere and delete everything outside it, so only the bodies you watch are simulated");
        foreach (var b in new[] { modeExplore, modeAsteroid, modeErase, modeHole, modeKeep })
        {
            b.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            modes.AddChild(b);
        }
        // Not ExpandFill like the modes: a small square at the end of the row that tucks the whole bar away.
        barHide = Btn("▾", () => app?.ToggleBottomBar(), "Hide this bar (M). The mode stays as it is; the tab at the bottom brings the bar back", HudTheme.ButtonTight);
        barHide.CustomMinimumSize = new Vector2(30, 30);
        barHide.AddThemeFontOverride("font", HudTheme.SansSymbols);
        modes.AddChild(barHide);
        col.AddChild(modes);

        erasePanel = Wrapped("Drag over the view to delete asteroids and fragments. The wheel resizes the brush; planets, moons and the Sun are never touched.", HudTheme.LabelMuted);
        erasePanel.Visible = false;
        col.AddChild(erasePanel);
        BuildKeepPanel(col);

        var ast = VBox(4);
        ast.Visible = false;
        asteroidPanel = ast;
        var presets = new HFlowContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        foreach (var p in Presets.All)
        {
            var preset = p;
            var b = Btn(p.Name, () => { app?.ApplyPreset(preset); activePreset = preset.Name; SyncAsteroid(); },
                string.Create(CultureInfo.InvariantCulture, $"{p.Blurb} · {Format.LengthM(p.DiameterM)} · {p.SpeedKms} km/s"), HudTheme.PresetButton);
            presetButtons.Add(b);
            presets.AddChild(b);
        }
        ast.AddChild(presets);
        var fields = HBox(10);
        // Up to 10^9.15 m: the Sun-like star preset is 1.39 × 10⁹ m across.
        astDiam = Slider(0, 9.15, 0.01, 1.3, v =>
        {
            if (app == null) return;
            app.Asteroid.DiameterM = Math.Pow(10, v);
            activePreset = "";
            SyncAsteroid();
        }, astDiamHeld);
        // 1 to 10⁵ km/s: a star fired at a massive hole from far out needs tens of thousands of km/s to reach escape speed.
        astSpeed = Slider(0, SpeedLogMax, 0.01, 1.3, v =>
        {
            if (app == null) return;
            app.Asteroid.SpeedKms = Math.Pow(10, v);
            SyncAsteroid();
        }, astSpeedHeld);
        astComp = Select(("Rocky · 3000 kg/m³", (int)Composition.Rock), ("Iron · 7800 kg/m³", (int)Composition.Iron),
            ("Carbonaceous · 1500 kg/m³", (int)Composition.Carbon), ("Icy comet · 900 kg/m³", (int)Composition.Ice),
            ("Star · 1410 kg/m³", (int)Composition.Star));
        astComp.ItemSelected += i =>
        {
            if (app == null) return;
            app.Asteroid.Composition = (Composition)astComp.GetItemId((int)i);
            activePreset = "";
            SyncAsteroid();
        };
        var fd = Field("Diameter", astDiam, out astDiamVal);
        var fs = Field("Speed", astSpeed, out astSpeedVal);
        foreach (var m in new[] { circularMark, escapeMark })
        {
            m.Visible = false;
            m.MouseFilter = Control.MouseFilterEnum.Pass;
            m.Size = new Vector2(3, 12);
            astSpeed.AddChild(m);
        }
        var fc = Field("Composition", astComp, out _);
        foreach (var f in new Control[] { fd, fs, fc })
        {
            f.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            f.SizeFlagsStretchRatio = 1;
            fields.AddChild(f);
        }
        ast.AddChild(fields);
        var shower = HBox(10);
        showerBox = Toggle("Meteor shower", on =>
        {
            if (app == null) return;
            app.Asteroid.Shower = on;
            SyncAsteroid();
        });
        showerBox.AutowrapMode = TextServer.AutowrapMode.Off;
        showerBox.SizeFlagsHorizontal = Control.SizeFlags.Fill;
        showerBox.TooltipText = "Fire the rock broken into equal pieces (its mass kept) that fall one after another, as comet Shoemaker–Levy 9 fell on Jupiter. One Ctrl+Z takes the whole shower back.";
        showerSlider = Slider(Shower.MinRocks, Shower.MaxRocks, 1, 16, v =>
        {
            if (app == null) return;
            app.Asteroid.ShowerRocks = (int)Math.Round(v);
            SyncAsteroid();
        }, showerHeld, 180);
        showerSlider.SizeFlagsHorizontal = Control.SizeFlags.Fill;
        showerSlider.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        showerSlider.TooltipText = "Pieces in the shower";
        showerVal = Text("", HudTheme.LabelVal);
        showerVal.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        showerVal.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        shower.AddChild(showerBox);
        shower.AddChild(showerSlider);
        shower.AddChild(showerVal);
        ast.AddChild(shower);
        aim = Wrapped("", HudTheme.LabelMonoMuted);
        aim.CustomMinimumSize = new Vector2(0, 16);
        ast.AddChild(aim);
        astForecast = ForecastLabel();
        ast.AddChild(astForecast);
        SetAimText("");
        col.AddChild(ast);
        BuildHolePanel(col);
        panel.AddChild(col);
        root.AddChild(panel);
        bottomPanel = panel;

        // What stands in for the bar while it is hidden: a small tab at the same spot that names the mode still in force.
        barShow = Btn("▴ Explore", () => app?.ToggleBottomBar(), "Show the mode bar (M)", HudTheme.ButtonTight);
        barShow.AddThemeFontOverride("font", HudTheme.SansSymbols);
        barShow.AnchorLeft = barShow.AnchorRight = 0.5f;
        barShow.AnchorTop = barShow.AnchorBottom = 1;
        barShow.OffsetLeft = -70;
        barShow.OffsetRight = 70;
        barShow.OffsetTop = barShow.OffsetBottom = -14;
        barShow.GrowVertical = Control.GrowDirection.Begin;
        barShow.Visible = false;
        root.AddChild(barShow);
    }

    /// <summary>Show or hide the bottom bar (Settings.BottomBar); the tab that brings it back shows while it is hidden.</summary>
    public void SetBottomBar(bool on)
    {
        bottomPanel.Visible = on;
        barShow.Visible = !on;
    }

    /// <summary>The tab's label names the active mode, since the bar that shows it is hidden.</summary>
    void SetBarTab(Mode m) => barShow.Text = "▴ " + m switch { Mode.Asteroid => "Asteroid", Mode.Erase => "Erase", Mode.BlackHole => "Black hole", Mode.KeepInside => "Keep inside", _ => "Explore" };

    /// <summary>The asteroid panel follows <c>app.Asteroid</c> (hud.ts syncAsteroid); sliders the user holds keep their value.</summary>
    public void SyncAsteroid()
    {
        var a = app?.Asteroid;
        if (a == null) return;
        if (!astDiamHeld.Dragging) astDiam.SetValueNoSignal(Math.Log10(a.DiameterM));
        if (!astSpeedHeld.Dragging) astSpeed.SetValueNoSignal(Math.Log10(a.SpeedKms));
        var ci = astComp.GetItemIndex((int)a.Composition);
        if (astComp.Selected != ci) astComp.Select(ci);
        var mass = Presets.Density(a.Composition) * (Math.PI / 6) * Math.Pow(a.DiameterM, 3);
        astDiamVal.Set($"{Format.LengthM(a.DiameterM)} · {Format.Sci(mass, 2)} kg");
        astSpeedVal.Set(Format.Speed(a.SpeedKms));
        for (var i = 0; i < presetButtons.Count; i++) Variation(presetButtons[i], Presets.All[i].Name == activePreset ? HudTheme.PresetActive : HudTheme.PresetButton);
        if (showerBox.ButtonPressed != a.Shower) showerBox.SetPressedNoSignal(a.Shower);
        var n = Shower.Clamp(a.ShowerRocks);
        if (!showerHeld.Dragging) showerSlider.SetValueNoSignal(n);
        var d = Shower.PieceDiameter(a.DiameterM, n);
        showerVal.Set(a.Composition == Composition.Star ? "a star is fired whole"
            : $"{n} rocks · {Format.LengthM(d)} each · {Format.Sci(mass / n, 2)} kg");
        var dim = a.Shower && a.Composition != Composition.Star ? 1f : 0.5f;
        showerSlider.Modulate = showerVal.Modulate = new Color(1, 1, 1, dim);
    }

    /// <summary>
    /// Place the Speed slider's two markers (M6.3) at the circular-orbit and escape speeds, each with its one-sentence tooltip;
    /// null, or a speed off the slider, hides them.
    /// </summary>
    public void SetSpeedMarkers(SpeedMarkers? m)
    {
        Place(circularMark, m?.Circular ?? double.NaN, m?.CircularTip);
        Place(escapeMark, m?.Escape ?? double.NaN, m?.EscapeTip);

        void Place(ColorRect mark, double kms, string? tip)
        {
            var f = Math.Log10(kms) / SpeedLogMax;
            mark.Visible = double.IsFinite(f) && f is >= 0 and <= 1;
            if (!mark.Visible) return;
            // The grabber's centre travels the track inset by half its width; 8 px matches the theme's grabber.
            var w = astSpeed.Size.X - 16;
            mark.Position = new Vector2(8 + (float)f * w - 1.5f, astSpeed.Size.Y - 6);
            if (mark.TooltipText != tip) mark.TooltipText = tip;
        }
    }

    /// <summary>Capture helper: where the markers sit, px along the slider, or "hidden".</summary>
    public string SpeedMarkerSummary =>
        $"circular={(circularMark.Visible ? $"{circularMark.Position.X:F1}" : "hidden")} escape={(escapeMark.Visible ? $"{escapeMark.Position.X:F1}" : "hidden")} slider_w={astSpeed.Size.X:F0}";

    /// <summary>The aim readout under the asteroid panel (the asteroid task's live prediction); empty restores the instructions.</summary>
    public void SetAimText(string t) => aim.Set(t.Length > 0 ? t : "Click a body to fire from the camera. Drag on empty space to aim by hand.");

    /// <summary>Mode buttons and panels follow the mode (hud.ts setMode); the brush shows only in erase mode.</summary>
    public void SetMode(Mode m)
    {
        Variation(modeExplore, m == Mode.Explore ? HudTheme.ButtonActive : "");
        Variation(modeAsteroid, m == Mode.Asteroid ? HudTheme.ButtonActive : "");
        Variation(modeErase, m == Mode.Erase ? HudTheme.EraseActive : "");
        Variation(modeHole, m == Mode.BlackHole ? HudTheme.ButtonActive : "");
        Variation(modeKeep, m == Mode.KeepInside ? HudTheme.ButtonActive : "");
        keepPanel.Visible = m == Mode.KeepInside;
        if (m != Mode.KeepInside) HideKeepConfirm();
        SetBarTab(m);
        asteroidPanel.Visible = m == Mode.Asteroid;
        holePanel.Visible = m == Mode.BlackHole;
        if (m == Mode.BlackHole) SyncHolePanel();
        erasePanel.Visible = m == Mode.Erase;
        var entering = m == Mode.Erase && !erasing;
        erasing = m == Mode.Erase;
        if (!erasing) ShowBrush(null);
        if (entering && IsInsideTree()) eraseEnteredAt = GetViewport().GetMousePosition();
        Input.SetDefaultCursorShape(erasing ? Input.CursorShape.Cross : Input.CursorShape.Arrow);
    }
}
