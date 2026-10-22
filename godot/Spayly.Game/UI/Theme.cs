using Godot;

namespace Spayly.Game.UI;

/// <summary>
/// The web's src/style.css as a Godot <see cref="Theme"/>: dark translucent panels, 13 px sans, mono readouts, accent blue
/// for values and the active state, amber for presets and the report, red for erase. Type variations stand in for CSS
/// classes (<c>.muted</c>, <c>.val</c>, <c>button.active</c> …); a control switches class by its ThemeTypeVariation.
/// </summary>
public static class HudTheme
{
    public static readonly Color Text = new("#dfe6f0");
    public static readonly Color Muted = new("#8b97a8");
    public static readonly Color Accent = new("#7fb4ff");
    public static readonly Color Accent2 = new("#ffb86b");
    public static readonly Color Danger = new("#ff6b6b");
    public static readonly Color Panel = new(9 / 255f, 13 / 255f, 22 / 255f, 0.66f);
    public static readonly Color PanelBorder = new(1, 1, 1, 0.09f);

    /// <summary>Glyphs the text faces lack (❙ U+2759 on the pause button): the system fallback alone rendered a hex box.</summary>
    static readonly SystemFont Symbols = new() { FontNames = ["Noto Sans Symbols 2", "Symbola", "OpenSymbol", "DejaVu Sans"] };

    public static readonly SystemFont Sans = new() { FontNames = ["Inter", "Segoe UI", "Noto Sans", "DejaVu Sans", "sans-serif"] };
    public static readonly SystemFont SansBold = new() { FontNames = ["Inter", "Segoe UI", "Noto Sans", "DejaVu Sans", "sans-serif"], FontWeight = 700 };
    public static readonly SystemFont SansSemi = new() { FontNames = ["Inter", "Segoe UI", "Noto Sans", "DejaVu Sans", "sans-serif"], FontWeight = 600 };
    public static readonly SystemFont Mono = new() { FontNames = ["JetBrains Mono", "Cascadia Code", "Noto Sans Mono", "DejaVu Sans Mono", "monospace"] };

    // Button / label class names (ThemeTypeVariation values).
    public const string ButtonActive = "ButtonActive";
    public const string ButtonSmall = "ButtonSmall";
    public const string ButtonTight = "ButtonTight";
    public const string PresetButton = "PresetButton";
    public const string PresetActive = "PresetActive";
    public const string EraseActive = "EraseActive";
    public const string ListRow = "ListRow";
    public const string ListRowActive = "ListRowActive";
    public const string ListRowSat = "ListRowSat";
    public const string ListRowSatActive = "ListRowSatActive";
    public const string LabelMuted = "LabelMuted";
    public const string LabelMono = "LabelMono";
    public const string LabelMonoMuted = "LabelMonoMuted";
    public const string LabelVal = "LabelVal";
    public const string LabelH3 = "LabelH3";
    public const string LabelTitle = "LabelTitle";
    public const string LabelBrand = "LabelBrand";
    public const string LabelClass = "LabelClass";
    public const string LabelKey = "LabelKey";
    public const string LabelDt = "LabelDt";
    public const string LabelDd = "LabelDd";
    public const string LabelHelpDt = "LabelHelpDt";
    public const string LabelLimited = "LabelLimited";
    public const string LabelSmallMuted = "LabelSmallMuted";
    public const string ReportPanel = "ReportPanel";
    public const string HintPanel = "HintPanel";
    /// <summary>A feed toast (Hud.Feed.cs): the panel with the severity in its border.</summary>
    public const string ToastMinor = "ToastMinor", ToastNotable = "ToastNotable", ToastMajor = "ToastMajor";
    public const string ButtonSmallActive = "ButtonSmallActive";

    /// <summary>
    /// Sans with the symbol face as a fallback, for the transport buttons only: a fallback's ascent and descent join every
    /// line's height, so on the default font it made each list row and stat line ~8 px taller.
    /// </summary>
    public static readonly SystemFont SansSymbols = new() { FontNames = ["Inter", "Segoe UI", "Noto Sans", "DejaVu Sans", "sans-serif"], Fallbacks = [Symbols] };

    public static StyleBoxFlat Box(Color bg, Color border, int radius, float padX, float padY, int borderWidth = 1)
    {
        var s = new StyleBoxFlat { BgColor = bg, BorderColor = border, AntiAliasing = true };
        s.SetBorderWidthAll(borderWidth);
        s.SetCornerRadiusAll(radius);
        s.ContentMarginLeft = s.ContentMarginRight = padX;
        s.ContentMarginTop = s.ContentMarginBottom = padY;
        return s;
    }

    /// <summary>The <c>.panel</c> box with its own padding (the top bar's 8 px × 14 px).</summary>
    public static StyleBoxFlat PanelBox(float padX, float padY, Color? border = null)
    {
        var s = Box(Panel, border ?? PanelBorder, 12, padX, padY);
        s.ShadowColor = new Color(0, 0, 0, 0.35f);
        s.ShadowSize = 20;
        s.ShadowOffset = new Vector2(0, 10);
        return s;
    }

    /// <summary>A filled circle, white (tinted by the icon colour) or in <paramref name="fill"/> with a <paramref name="ring"/>.</summary>
    public static ImageTexture Disc(int size, Color fill, Color? ring = null, float ringWidth = 0)
    {
        var img = Image.CreateEmpty(size, size, false, Image.Format.Rgba8);
        var c = (size - 1) / 2f;
        var r = size / 2f;
        for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
            {
                var d = new Vector2(x - c, y - c).Length();
                var a = Mathf.Clamp(r - d, 0, 1);
                var col = ring is Color rc && d > r - ringWidth ? rc : fill;
                img.SetPixel(x, y, new Color(col, col.A * a));
            }
        return ImageTexture.CreateFromImage(img);
    }

    /// <summary>
    /// Chromium's 13 px checkbox, measured off the web parity shot: a 2 px-rounded square with a 1 px border and, when
    /// <paramref name="tick"/> is given, the check mark (a 1.6 px stroke through (2.5, 6.5), (5.5, 9.5), (10, 2.5)).
    /// </summary>
    static ImageTexture CheckIcon(int size, Color fill, Color border, Color? tick = null)
    {
        var img = Image.CreateEmpty(size, size, false, Image.Format.Rgba8);
        var h = size / 2f;
        Vector2 a = new(2.5f, 6.5f), b = new(5.5f, 9.5f), c = new(10f, 2.5f);
        for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
            {
                var p = new Vector2(x + 0.5f, y + 0.5f);
                // Signed distance to a rounded box of radius 2.
                var q = new Vector2(Mathf.Abs(p.X - h), Mathf.Abs(p.Y - h)) - new Vector2(h - 2, h - 2);
                var sd = new Vector2(Mathf.Max(q.X, 0), Mathf.Max(q.Y, 0)).Length() + Mathf.Min(Mathf.Max(q.X, q.Y), 0) - 2;
                var cover = Mathf.Clamp(0.5f - sd, 0, 1);
                if (cover <= 0) continue;
                var col = sd > -1 ? border : fill;
                if (tick is Color t)
                {
                    var d = Mathf.Min(Segment(p, a, b), Segment(p, b, c));
                    col = col.Lerp(t, Mathf.Clamp(0.8f + 0.5f - d, 0, 1));
                }
                img.SetPixel(x, y, new Color(col, col.A * cover));
            }
        return ImageTexture.CreateFromImage(img);

        static float Segment(Vector2 p, Vector2 a, Vector2 b)
        {
            var ab = b - a;
            var t = Mathf.Clamp((p - a).Dot(ab) / ab.LengthSquared(), 0, 1);
            return (p - (a + ab * t)).Length();
        }
    }

    public static Theme Build()
    {
        var t = new Theme { DefaultFont = Sans, DefaultFontSize = 13 };
        var none = new StyleBoxEmpty();

        // .panel
        var panel = Box(Panel, PanelBorder, 12, 14, 12);
        panel.ShadowColor = new Color(0, 0, 0, 0.35f);
        panel.ShadowSize = 20;
        panel.ShadowOffset = new Vector2(0, 10);
        t.SetStylebox("panel", "PanelContainer", panel);
        t.SetTypeVariation(ReportPanel, "PanelContainer");
        var report = (StyleBoxFlat)panel.Duplicate();
        report.BorderColor = new Color(Accent2, 0.35f);
        t.SetStylebox("panel", ReportPanel, report);
        foreach (var (name, border) in new[] { (ToastMinor, PanelBorder), (ToastNotable, new Color(Accent, 0.55f)), (ToastMajor, new Color(Accent2, 0.75f)) })
        {
            t.SetTypeVariation(name, "PanelContainer");
            var toast = Box(new Color(9 / 255f, 13 / 255f, 22 / 255f, 0.78f), border, 10, 10, 7);
            toast.BorderWidthLeft = toast.BorderWidthRight = toast.BorderWidthTop = toast.BorderWidthBottom = 1;
            if (name == ToastMajor) toast.BorderWidthLeft = 3;
            toast.ShadowColor = new Color(0, 0, 0, 0.3f);
            toast.ShadowSize = 10;
            t.SetStylebox("panel", name, toast);
        }
        t.SetTypeVariation(HintPanel, "PanelContainer");
        t.SetStylebox("panel", HintPanel, Box(new Color(9 / 255f, 13 / 255f, 22 / 255f, 0.75f), PanelBorder, 18, 16, 8));

        // labels
        t.SetColor("font_color", "Label", Text);
        Variation(t, LabelMuted, "Label", Muted, null, 0);
        Variation(t, LabelSmallMuted, "Label", Muted, null, 12);
        Variation(t, LabelMono, "Label", Text, Mono, 12);
        Variation(t, LabelMonoMuted, "Label", Muted, Mono, 11);
        Variation(t, LabelVal, "Label", Accent, Mono, 11);
        Variation(t, LabelH3, "Label", Muted, new FontVariation { BaseFont = SansSemi, SpacingGlyph = 1 }, 12);
        Variation(t, LabelTitle, "Label", Colors.White, SansSemi, 15);
        Variation(t, LabelBrand, "Label", Colors.White, new FontVariation { BaseFont = SansBold, SpacingGlyph = 3 }, 14);
        Variation(t, LabelClass, "Label", Accent2, SansSemi, 13);
        Variation(t, LabelDt, "Label", Muted, null, 12);
        Variation(t, LabelDd, "Label", Text, Mono, 11);
        Variation(t, LabelHelpDt, "Label", Accent, Mono, 12);
        Variation(t, LabelLimited, "Label", Accent2, Mono, 11);
        Variation(t, LabelKey, "Label", Muted, null, 10);
        t.SetStylebox("normal", LabelKey, Box(new Color(0, 0, 0, 0), PanelBorder, 4, 5, 0));

        // button, button:hover, button.active
        var btn = Box(new Color(1, 1, 1, 0.06f), PanelBorder, 8, 10, 6);
        var btnHover = Box(new Color(1, 1, 1, 0.12f), new Color(1, 1, 1, 0.2f), 8, 10, 6);
        var btnActive = Box(new Color(Accent, 0.22f), new Color(Accent, 0.5f), 8, 10, 6);
        ButtonStyles(t, "Button", btn, btnHover, btnHover);
        foreach (var type in new[] { "Button", "OptionButton", "CheckBox" })
        {
            t.SetColor("font_color", type, Text);
            t.SetColor("font_hover_color", type, Colors.White);
            t.SetColor("font_pressed_color", type, Colors.White);
            t.SetColor("font_hover_pressed_color", type, Colors.White);
            t.SetColor("font_focus_color", type, Text);
            t.SetStylebox("focus", type, none);
        }
        t.SetTypeVariation(ButtonActive, "Button");
        ButtonStyles(t, ButtonActive, btnActive, btnActive, btnActive);
        t.SetColor("font_color", ButtonActive, Colors.White);
        t.SetTypeVariation(ButtonTight, "Button");
        ButtonStyles(t, ButtonTight, Box(new Color(1, 1, 1, 0.06f), PanelBorder, 8, 6, 6), Box(new Color(1, 1, 1, 0.12f), new Color(1, 1, 1, 0.2f), 8, 6, 6), null);
        t.SetTypeVariation(ButtonSmall, "Button");
        ButtonStyles(t, ButtonSmall, Box(new Color(1, 1, 1, 0.06f), PanelBorder, 8, 8, 2), Box(new Color(1, 1, 1, 0.12f), new Color(1, 1, 1, 0.2f), 8, 8, 2), null);
        t.SetTypeVariation(ButtonSmallActive, "Button");
        var smallActive = Box(new Color(Accent, 0.22f), new Color(Accent, 0.5f), 8, 8, 2);
        ButtonStyles(t, ButtonSmallActive, smallActive, smallActive, smallActive);
        t.SetColor("font_color", ButtonSmallActive, Colors.White);
        t.SetTypeVariation(PresetButton, "Button");
        ButtonStyles(t, PresetButton, Box(new Color(1, 1, 1, 0.06f), PanelBorder, 8, 9, 4), Box(new Color(1, 1, 1, 0.12f), new Color(1, 1, 1, 0.2f), 8, 9, 4), null);
        t.SetFontSize("font_size", PresetButton, 12);
        t.SetTypeVariation(PresetActive, "Button");
        var presetActive = Box(new Color(Accent2, 0.22f), new Color(Accent2, 0.5f), 8, 9, 4);
        ButtonStyles(t, PresetActive, presetActive, presetActive, presetActive);
        t.SetFontSize("font_size", PresetActive, 12);
        t.SetColor("font_color", PresetActive, Colors.White);
        t.SetTypeVariation(EraseActive, "Button");
        var eraseActive = Box(new Color(Danger, 0.22f), new Color(Danger, 0.55f), 8, 10, 6);
        ButtonStyles(t, EraseActive, eraseActive, eraseActive, eraseActive);
        t.SetColor("font_color", EraseActive, Colors.White);

        // #body-list button (+ .active, + .sat indent)
        var rowPad = 8f;
        StyleBoxFlat Row(Color bg, Color border, float left)
        {
            var s = Box(bg, border, 8, rowPad, 5);
            s.ContentMarginLeft = left;
            return s;
        }
        var clear = new Color(0, 0, 0, 0);
        var rowHover = new Color(1, 1, 1, 0.07f);
        var rowActiveBg = new Color(Accent, 0.18f);
        var rowActiveBorder = new Color(Accent, 0.4f);
        foreach (var (name, left, active) in new[] { (ListRow, 8f, false), (ListRowSat, 20f, false), (ListRowActive, 8f, true), (ListRowSatActive, 20f, true) })
        {
            t.SetTypeVariation(name, "Button");
            var normal = active ? Row(rowActiveBg, rowActiveBorder, left) : Row(clear, clear, left);
            ButtonStyles(t, name, normal, active ? normal : Row(rowHover, clear, left), normal);
            t.SetConstant("h_separation", name, 8);
            if (active) t.SetColor("font_color", name, Colors.White);
        }

        // select
        var sel = Box(new Color(1, 1, 1, 0.06f), PanelBorder, 8, 8, 4);
        ButtonStyles(t, "OptionButton", sel, Box(new Color(1, 1, 1, 0.12f), new Color(1, 1, 1, 0.2f), 8, 8, 4), sel);
        t.SetStylebox("panel", "PopupMenu", Box(new Color("#0e1420"), PanelBorder, 6, 4, 4));
        t.SetStylebox("hover", "PopupMenu", Box(new Color(Accent, 0.22f), clear, 4, 4, 2));
        t.SetColor("font_color", "PopupMenu", Text);
        t.SetColor("font_hover_color", "PopupMenu", Colors.White);

        // checkbox: Chromium's control with accent-color: var(--accent) — accent fill and a dark tick when checked, its
        // light-scheme white box with a #767676 border when not (the page sets no color-scheme).
        foreach (var s in new[] { "normal", "hover", "pressed", "hover_pressed", "disabled" }) t.SetStylebox(s, "CheckBox", Box(clear, clear, 0, 0, 2, 0));
        t.SetIcon("checked", "CheckBox", CheckIcon(13, Accent, Accent, new Color("#3b3b3b")));
        t.SetIcon("unchecked", "CheckBox", CheckIcon(13, Colors.White, new Color("#767676")));
        t.SetConstant("h_separation", "CheckBox", 6);

        // input[type=range]
        var track = Box(new Color(1, 1, 1, 0.14f), clear, 2, 0, 2, 0);
        t.SetStylebox("slider", "HSlider", track);
        t.SetStylebox("grabber_area", "HSlider", track);
        t.SetStylebox("grabber_area_highlight", "HSlider", track);
        var grab = Disc(14, Accent, new Color("#0b1220"), 2);
        t.SetIcon("grabber", "HSlider", grab);
        t.SetIcon("grabber_highlight", "HSlider", grab);
        t.SetConstant("center_grabber", "HSlider", 0);

        // containers
        t.SetConstant("separation", "HBoxContainer", 6);
        t.SetConstant("separation", "VBoxContainer", 4);
        t.SetConstant("h_separation", "HFlowContainer", 6);
        t.SetConstant("v_separation", "HFlowContainer", 6);
        t.SetConstant("h_separation", "GridContainer", 10);
        t.SetConstant("v_separation", "GridContainer", 3);

        // ::-webkit-scrollbar
        t.SetStylebox("scroll", "VScrollBar", Box(clear, clear, 4, 0, 0, 0));
        var thumb = Box(new Color(1, 1, 1, 0.14f), clear, 4, 4, 4, 0);
        t.SetStylebox("grabber", "VScrollBar", thumb);
        t.SetStylebox("grabber_highlight", "VScrollBar", thumb);
        t.SetStylebox("grabber_pressed", "VScrollBar", thumb);

        t.SetStylebox("panel", "TooltipPanel", Box(new Color("#0e1420"), PanelBorder, 6, 8, 5));
        t.SetColor("font_color", "TooltipLabel", Text);
        return t;
    }

    static void Variation(Theme t, string name, string baseType, Color color, Font? font, int size)
    {
        t.SetTypeVariation(name, baseType);
        t.SetColor("font_color", name, color);
        if (font != null) t.SetFont("font", name, font);
        if (size > 0) t.SetFontSize("font_size", name, size);
    }

    static void ButtonStyles(Theme t, string type, StyleBox normal, StyleBox hover, StyleBox? pressed)
    {
        t.SetStylebox("normal", type, normal);
        t.SetStylebox("hover", type, hover);
        t.SetStylebox("pressed", type, pressed ?? hover);
        t.SetStylebox("hover_pressed", type, pressed ?? hover);
        t.SetStylebox("disabled", type, normal);
    }
}
