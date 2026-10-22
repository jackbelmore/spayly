using System;
using System.Globalization;
using System.Text;
using Godot;
using Spayly.Sim;
using AppNode = Spayly.Game.App.App;

namespace Spayly.Game.UI;

/// <summary>
/// The undo history panel (key U, or the top bar's History button): every undo point by its label, newest first, and the
/// redo points above them, greyed, with a "now" marker between. A click on a point jumps there through
/// <see cref="AppNode.JumpHistory"/>: an undo point undoes down to just before its action, a redo point redoes up to just
/// after it. A point whose debris copy was shed for memory says so; it still restores the bodies.
///
/// Rows are pooled, one per point the history can hold, and rewritten only when <see cref="UndoHistory.Version"/> moves,
/// which the 8 Hz panel update polls with one int compare while the panel is open. Capture verbs: <c>history[:1|0]</c>,
/// <c>logHistory</c>, <c>historyJump:N</c> (the row N points back; negative for a redo row), in App.Capture.cs.
/// </summary>
public partial class Hud
{
    const int HistoryRows = UndoHistory.DefaultMaxPoints;
    /// <summary>px per row, for sizing the list to what it shows.</summary>
    const float HistoryRowHeight = 27;
    const float HistoryListMax = 380;

    PanelContainer historyPanel = null!;
    ScrollContainer historyScroll = null!;
    VBoxContainer historyList = null!;
    Control historyNow = null!;
    HudLabel historyEmpty = null!, historyFooter = null!;
    Button historyButton = null!;
    readonly Button[] historyRows = new Button[HistoryRows];
    readonly HudLabel[] historyTags = new HudLabel[HistoryRows];
    /// <summary>What a row does when clicked: undo this many points, or redo −this many.</summary>
    readonly int[] historySteps = new int[HistoryRows];
    readonly UniverseSnapshot?[] historyPoints = new UniverseSnapshot?[HistoryRows];
    readonly bool[] historyLost = new bool[HistoryRows];
    int historyShown = int.MinValue;
    int historyRowCount;
    readonly StringBuilder historySb = new();

    void BuildHistory()
    {
        var panel = NewPanel();
        // Beside the left column, under the top bar: over the stage, not over another panel.
        panel.Position = new Vector2(264, 74);
        panel.CustomMinimumSize = new Vector2(270, 0);
        panel.Visible = false;
        var v = VBox(6);
        var head = HBox(6);
        var title = H3("History");
        title.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        title.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        head.AddChild(title);
        var close = Btn("×", () => ToggleHistory(false), "Close (U)", HudTheme.ButtonSmall);
        head.AddChild(close);
        v.AddChild(head);
        v.AddChild(Wrapped("Click a point to go back to just before it, or forward to a greyed one. Ctrl+Z steps back one, Ctrl+Shift+Z forward.", HudTheme.LabelSmallMuted));

        historyScroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        historyList = VBox(1);
        historyList.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        for (var i = 0; i < HistoryRows; i++)
        {
            var idx = i;
            var b = Btn("", () => HistoryRowPressed(idx), "", HudTheme.ListRow);
            b.Alignment = HorizontalAlignment.Left;
            b.ClipText = true;
            b.Visible = false;
            b.CustomMinimumSize = new Vector2(0, HistoryRowHeight - 1);
            var tag = Text("", HudTheme.LabelMonoMuted);
            tag.AddThemeFontSizeOverride("font_size", 10);
            tag.AddThemeColorOverride("font_color", HudTheme.Accent2);
            tag.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            tag.OffsetRight = -8;
            tag.HorizontalAlignment = HorizontalAlignment.Right;
            tag.VerticalAlignment = VerticalAlignment.Center;
            tag.MouseFilter = Control.MouseFilterEnum.Pass;
            b.AddChild(tag);
            historyRows[i] = b;
            historyTags[i] = tag;
            historyList.AddChild(b);
        }
        var now = Text("●  now", HudTheme.LabelMono);
        now.AddThemeColorOverride("font_color", HudTheme.Accent);
        now.AddThemeFontSizeOverride("font_size", 11);
        var nowPad = new MarginContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        nowPad.AddThemeConstantOverride("margin_left", 8);
        nowPad.AddThemeConstantOverride("margin_top", 2);
        nowPad.AddThemeConstantOverride("margin_bottom", 2);
        nowPad.AddChild(now);
        historyNow = nowPad;
        historyList.AddChild(nowPad);
        historyScroll.AddChild(historyList);
        v.AddChild(historyScroll);
        historyEmpty = Text("Nothing to undo yet: every launch, hole, erase stroke, slider drag and impact adds a point here.", HudTheme.LabelMuted);
        historyEmpty.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        historyEmpty.CustomMinimumSize = new Vector2(250, 0);
        v.AddChild(historyEmpty);
        historyFooter = Text("", HudTheme.LabelMonoMuted);
        historyFooter.AddThemeFontSizeOverride("font_size", 10);
        v.AddChild(historyFooter);
        panel.AddChild(v);
        root.AddChild(panel);
        historyPanel = panel;
    }

    /// <summary>Show or hide the history panel; no argument flips it. Opening it lists the history as it is now.</summary>
    public void ToggleHistory(bool? force = null)
    {
        var on = force ?? !historyPanel.Visible;
        historyPanel.Visible = on;
        if (historyButton != null) Variation(historyButton, on ? HudTheme.ButtonActive : "");
        if (on) RefreshHistory(true);
    }

    public bool HistoryVisible => historyPanel.Visible;

    /// <summary>Rewrite the rows when the history changed since they were written; polled at 8 Hz while the panel is open.</summary>
    void RefreshHistory(bool force = false)
    {
        var a = app;
        if (a == null || !historyPanel.Visible) return;
        var h = a.Universe.History;
        if (!force && h.Version == historyShown) return;
        historyShown = h.Version;

        var redo = h.Redo;
        var shownRedo = Math.Min(redo.Count, HistoryRows);
        var shownUndo = Math.Min(h.Count, HistoryRows - shownRedo);
        var row = 0;
        // Redo points furthest first, so the one Ctrl+Shift+Z would bring back sits just above "now".
        for (var j = redo.Count - shownRedo; j < redo.Count; j++) SetHistoryRow(row++, redo[j], -(redo.Count - j));
        historyList.MoveChild(historyNow, row);
        historyNow.Visible = shownRedo + shownUndo > 0;
        for (var m = 0; m < shownUndo; m++) SetHistoryRow(row++, h[h.Count - 1 - m], m + 1);
        for (var i = row; i < HistoryRows; i++)
        {
            historyPoints[i] = null;
            historyRows[i].Visible = false;
        }
        historyRowCount = row;
        historyEmpty.Visible = row == 0;
        var lines = row + (historyNow.Visible ? 1 : 0);
        historyScroll.CustomMinimumSize = new Vector2(0, Math.Min(lines * HistoryRowHeight, HistoryListMax));
        var hidden = h.Count + redo.Count - row;
        historyFooter.Set(string.Create(CultureInfo.InvariantCulture,
            $"{h.Count} undo · {redo.Count} redo{(hidden > 0 ? $" · {hidden} not shown" : "")} · {h.Bytes / 1048576.0:F1} MB · VRAM {h.VramBytes / 1048576.0:F0} MB"));
    }

    void SetHistoryRow(int i, UniverseSnapshot p, int steps)
    {
        var b = historyRows[i];
        var lost = AppNode.UndoPointLostDebris(p);
        if (!ReferenceEquals(historyPoints[i], p) || historySteps[i] != steps || historyLost[i] != lost)
        {
            historyPoints[i] = p;
            historySteps[i] = steps;
            historyLost[i] = lost;
            b.Text = Capitalised(p.Label);
            historyTags[i].Set(lost ? "debris lost" : "");
            var n = Math.Abs(steps);
            var what = steps > 0
                ? $"Undo {n} {(n == 1 ? "action" : "actions")}: back to just before {p.Label}."
                : $"Redo {n} {(n == 1 ? "action" : "actions")}: forward to just after {p.Label}.";
            b.TooltipText = lost ? what + " The debris particles of this point were dropped to stay within the undo memory budget: the bodies come back, the debris does not." : what;
            // Greyed: undone, waiting to be redone.
            b.Modulate = steps < 0 ? new Color(1, 1, 1, 0.45f) : Colors.White;
        }
        b.Visible = true;
    }

    static string Capitalised(string s) => s.Length > 0 && char.IsLower(s[0]) ? char.ToUpperInvariant(s[0]) + s[1..] : s;

    void HistoryRowPressed(int i)
    {
        // Mid-jump the rows' step counts are a frame or two stale; the jump lands within a few frames.
        if (app == null || app.HistoryJumping || i >= historyRowCount || historyPoints[i] == null) return;
        app.JumpHistory(historySteps[i]);
        RefreshHistory();
    }

    /// <summary>Capture helper: press the row that jumps <paramref name="steps"/> points (negative: a redo row), as a click does. False when no row does.</summary>
    public bool PressHistoryRow(int steps)
    {
        RefreshHistory(true);
        for (var i = 0; i < historyRowCount; i++)
        {
            if (historySteps[i] != steps) continue;
            GD.Print($"[history] press row {i} steps={steps} '{historyRows[i].Text}'");
            historyRows[i].EmitSignal(BaseButton.SignalName.Pressed);
            return true;
        }
        GD.PushError($"[history] no row jumps {steps} (panel {(historyPanel.Visible ? "open" : "closed")}, {historyRowCount} rows)");
        return false;
    }

    /// <summary>Capture helper: the panel as shown, row by row (steps, label, flags, centre px), then the footer.</summary>
    public string HistorySummary()
    {
        RefreshHistory();
        var sb = historySb.Clear();
        var inv = CultureInfo.InvariantCulture;
        var h = app?.Universe.History;
        sb.Append(inv, $"version={h?.Version} visible={historyPanel.Visible} rows={historyRowCount}");
        if (historyPanel.Visible) sb.Append(inv, $" panel=({historyPanel.GlobalPosition.X:F0},{historyPanel.GlobalPosition.Y:F0} {historyPanel.Size.X:F0}x{historyPanel.Size.Y:F0})");
        for (var i = 0; i < historyRowCount; i++)
        {
            if (i == historyNow.GetIndex()) sb.Append(" | now");
            var b = historyRows[i];
            var c = b.GetGlobalRect().GetCenter();
            sb.Append(inv, $" | {historySteps[i]:+#;-#} '{b.Text}'{(historySteps[i] < 0 ? " greyed" : "")}{(historyLost[i] ? " debris-lost" : "")} @{c.X:F0},{c.Y:F0}");
        }
        if (historyRowCount > 0 && historyNow.GetIndex() >= historyRowCount) sb.Append(" | now");
        sb.Append(" | footer: ").Append(historyFooter.Text);
        return sb.ToString();
    }
}
