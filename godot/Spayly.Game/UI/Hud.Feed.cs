using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Godot;
using Spayly.Game.App;
using AppNode = Spayly.Game.App.App;

namespace Spayly.Game.UI;

/// <summary>
/// The event feed: compact toasts, newest on top, in a column beside the inspector (left of it, so it never covers the
/// inspector or the report card, which grows up from the bottom-right), with a small "Events" button that swaps them for a list
/// of the last <see cref="AppNode.MaxEvents"/>. Nothing here takes time or the camera: a click on a toast or a row asks the
/// app to fly there and open the report (<see cref="AppNode.OpenEvent"/>).
///
/// A toast lives <see cref="ToastSeconds"/> s and fades over the last <see cref="ToastFade"/>; hovering keeps it, as does a
/// coalesced event arriving. At most <see cref="FeedToasts"/> are visible: a sixth recycles the oldest. Toasts and rows are
/// pooled. Pointer rule as everywhere in the HUD: the panels Stop, every container Ignores.
/// </summary>
public partial class Hud
{
    const int FeedToasts = 5;
    const float FeedWidth = 262;
    /// <summary>Right edge of the feed column from the viewport's right edge: the inspector's 280 px, its 12 px margin, and a gap.</summary>
    const float FeedRightInset = 304;
    const float FeedTop = 108;
    const double ToastSeconds = 6, ToastFade = 0.5;
    const double ToastHoverGrace = 1.5;
    const float EventRowHeight = 27;
    const float EventListMax = 300;

    sealed class Toast
    {
        public required PanelContainer Panel;
        public required HudLabel Title, Detail;
        public GameEvent? Event;
        public double Until;
        public double Born;
    }

    VBoxContainer feedColumn = null!;
    Button eventsButton = null!;
    PanelContainer eventsPanel = null!;
    ScrollContainer eventsScroll = null!;
    HudLabel eventsEmpty = null!;
    readonly Toast[] toasts = new Toast[FeedToasts];
    readonly Button[] eventRows = new Button[AppNode.MaxEvents];
    readonly HudLabel[] eventTags = new HudLabel[AppNode.MaxEvents];
    readonly GameEvent?[] eventRowEvents = new GameEvent?[AppNode.MaxEvents];
    int eventRowCount;
    readonly StringBuilder feedSb = new();

    /// <summary>Everything the feed adds that a label hidden beneath it should not show through.</summary>
    Control[] FeedCovers() => [eventsButton, eventsPanel, .. Array.ConvertAll(toasts, t => (Control)t.Panel)];

    void BuildFeed()
    {
        feedColumn = VBox(6);
        feedColumn.AnchorLeft = feedColumn.AnchorRight = 1;
        feedColumn.AnchorTop = feedColumn.AnchorBottom = 0;
        feedColumn.OffsetRight = -FeedRightInset;
        feedColumn.OffsetLeft = -FeedRightInset - FeedWidth;
        feedColumn.OffsetTop = feedColumn.OffsetBottom = FeedTop;
        feedColumn.GrowHorizontal = Control.GrowDirection.Begin;
        feedColumn.GrowVertical = Control.GrowDirection.End;

        var head = HBox(0);
        head.Alignment = BoxContainer.AlignmentMode.End;
        eventsButton = Btn("Events", () => ToggleEventsList(), "Everything that happened lately: click one to fly there and open its report", HudTheme.ButtonSmall);
        head.AddChild(eventsButton);
        feedColumn.AddChild(head);

        eventsPanel = NewPanel();
        eventsPanel.Visible = false;
        var v = VBox(6);
        v.AddChild(H3("Events"));
        eventsScroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        var list = VBox(1);
        list.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        for (var i = 0; i < eventRows.Length; i++)
        {
            var idx = i;
            var b = Btn("", () => EventRowPressed(idx), "", HudTheme.ListRow);
            b.Alignment = HorizontalAlignment.Left;
            b.ClipText = true;
            b.Visible = false;
            b.CustomMinimumSize = new Vector2(0, EventRowHeight - 1);
            var tag = Text("", HudTheme.LabelMonoMuted);
            tag.AddThemeFontSizeOverride("font_size", 10);
            tag.AddThemeColorOverride("font_color", HudTheme.Accent2);
            tag.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            tag.OffsetRight = -8;
            tag.HorizontalAlignment = HorizontalAlignment.Right;
            tag.VerticalAlignment = VerticalAlignment.Center;
            tag.MouseFilter = Control.MouseFilterEnum.Pass;
            b.AddChild(tag);
            eventRows[i] = b;
            eventTags[i] = tag;
            list.AddChild(b);
        }
        eventsScroll.AddChild(list);
        v.AddChild(eventsScroll);
        eventsEmpty = Wrapped("Nothing has happened yet. Impacts, disruptions and mergers land here instead of taking over the camera.", HudTheme.LabelMuted);
        v.AddChild(eventsEmpty);
        eventsPanel.AddChild(v);
        feedColumn.AddChild(eventsPanel);

        for (var i = 0; i < FeedToasts; i++)
        {
            var panel = NewPanel(HudTheme.ToastMinor);
            panel.Visible = false;
            panel.CustomMinimumSize = new Vector2(FeedWidth, 0);
            var col = VBox(1);
            var title = Wrapped("", HudTheme.LabelTitle);
            title.AddThemeFontSizeOverride("font_size", 13);
            var detail = Wrapped("", HudTheme.LabelSmallMuted);
            detail.AddThemeFontSizeOverride("font_size", 11);
            col.AddChild(title);
            col.AddChild(detail);
            panel.AddChild(col);
            var t = new Toast { Panel = panel, Title = title, Detail = detail };
            toasts[i] = t;
            panel.GuiInput += e =>
            {
                if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } && t.Event is { } ev) app?.OpenEvent(ev);
            };
            feedColumn.AddChild(panel);
        }
        root.AddChild(feedColumn);
    }

    /// <summary>The app raised an event, or grew one that coalesced another: show or refresh its toast.</summary>
    public void OnEventRaised(GameEvent e)
    {
        // This event's own toast, else a free one, else the oldest is recycled.
        Toast? t = null, free = null, oldest = null;
        foreach (var c in toasts)
        {
            if (ReferenceEquals(c.Event, e)) t = c;
            if (c.Event == null) free ??= c;
            else if (oldest == null || c.Born < oldest.Born) oldest = c;
        }
        t ??= free ?? oldest!;
        t.Event = e;
        t.Born = now;
        t.Until = now + ToastSeconds;
        t.Title.Set(e.Title);
        t.Detail.Set(e.Detail);
        t.Detail.Visible = e.Detail.Length > 0;
        SetVariation(t.Panel, e.Severity switch { EventSeverity.Major => HudTheme.ToastMajor, EventSeverity.Notable => HudTheme.ToastNotable, _ => HudTheme.ToastMinor });
        t.Panel.Modulate = Colors.White;
        t.Panel.Visible = !eventsPanel.Visible;
        // Newest on top, below the Events button and the (hidden) list.
        feedColumn.MoveChild(t.Panel, 2);
        RefreshEventsButton();
        if (eventsPanel.Visible) RefreshEventsList();
    }

    /// <summary>An undo took events out of the feed: their toasts go, and the list is rewritten.</summary>
    public void OnEventsPruned()
    {
        var live = app?.Events;
        foreach (var t in toasts)
        {
            if (t.Event == null || live != null && Has(live, t.Event)) continue;
            t.Event = null;
            t.Panel.Visible = false;
        }
        RefreshEventsButton();
        if (eventsPanel.Visible) RefreshEventsList();
    }

    static bool Has(IReadOnlyList<GameEvent> list, GameEvent e)
    {
        for (var i = 0; i < list.Count; i++)
            if (ReferenceEquals(list[i], e)) return true;
        return false;
    }

    void RefreshEventsButton()
    {
        var n = app?.Events.Count ?? 0;
        eventsButton.Text = n > 0 ? $"Events ({n})" : "Events";
    }

    /// <summary>Show or hide the list of the last events; the toasts step aside while it is open. No argument flips it.</summary>
    public void ToggleEventsList(bool? force = null)
    {
        var on = force ?? !eventsPanel.Visible;
        eventsPanel.Visible = on;
        SetVariation(eventsButton, on ? HudTheme.ButtonSmallActive : HudTheme.ButtonSmall);
        foreach (var t in toasts) t.Panel.Visible = !on && t.Event != null && now < t.Until;
        if (on) RefreshEventsList();
    }

    void RefreshEventsList()
    {
        var evs = app?.Events;
        var n = evs?.Count ?? 0;
        // Newest first.
        for (var i = 0; i < eventRows.Length; i++)
        {
            if (i >= n)
            {
                eventRowEvents[i] = null;
                eventRows[i].Visible = false;
                continue;
            }
            var e = evs![n - 1 - i];
            eventRowEvents[i] = e;
            var b = eventRows[i];
            b.Text = e.Title;
            b.TooltipText = e.Detail;
            eventTags[i].Set(e.Severity == EventSeverity.Major ? "major" : e.Severity == EventSeverity.Notable ? "notable" : "");
            b.Visible = true;
        }
        eventRowCount = n;
        eventsEmpty.Visible = n == 0;
        eventsScroll.Visible = n > 0;
        eventsScroll.CustomMinimumSize = new Vector2(0, Math.Min(n * EventRowHeight, EventListMax));
        RefreshEventsButton();
    }

    void EventRowPressed(int i)
    {
        if (i < eventRowCount && eventRowEvents[i] is { } e) app?.OpenEvent(e);
    }

    /// <summary>Every frame: age the toasts, hold the hovered ones, fade and retire the rest. No allocation.</summary>
    void UpdateFeed()
    {
        if (feedColumn == null) return;
        var mouse = GetViewport().GetMousePosition();
        foreach (var t in toasts)
        {
            if (t.Event == null || !t.Panel.Visible) continue;
            if (t.Panel.GetGlobalRect().HasPoint(mouse)) t.Until = Math.Max(t.Until, now + ToastHoverGrace);
            var left = t.Until - now;
            if (left <= 0)
            {
                t.Event = null;
                t.Panel.Visible = false;
                continue;
            }
            var a = (float)Math.Min(1, left / ToastFade);
            if (t.Panel.Modulate.A != a) t.Panel.Modulate = new Color(1, 1, 1, a);
        }
    }

    /// <summary>Capture helper: the toasts as shown (centre of each, size, alpha, title) and whether any overlaps another panel.</summary>
    public string FeedSummary()
    {
        var sb = feedSb.Clear();
        var inv = CultureInfo.InvariantCulture;
        var shown = 0;
        var overlaps = new List<string>();
        var others = new (string Name, Control C)[] { ("inspector", inspectorPanel), ("report", report), ("top", topPanel), ("bottom", bottomPanel), ("left", leftColumn), ("hint", hintPanel), ("history", historyPanel) };
        void Check(string what, Control c)
        {
            var r = c.GetGlobalRect();
            foreach (var (name, o) in others)
                if (o.IsVisibleInTree() && o.GetGlobalRect().Intersects(r)) overlaps.Add($"{what}~{name}");
        }
        if (eventsButton.IsVisibleInTree()) Check("button", eventsButton);
        if (eventsPanel.IsVisibleInTree()) Check("list", eventsPanel);
        sb.Append(inv, $"button={eventsButton.Text} list={(eventsPanel.Visible ? $"open rows={eventRowCount} size={eventsPanel.Size.X:F0}x{eventsPanel.Size.Y:F0}" : "closed")}");
        for (var i = 0; i < toasts.Length; i++)
        {
            var t = toasts[i];
            if (t.Event == null || !t.Panel.IsVisibleInTree()) continue;
            shown++;
            var r = t.Panel.GetGlobalRect();
            Check($"toast{shown}", t.Panel);
            sb.Append(inv, $" | ({r.Position.X:F0},{r.Position.Y:F0} {r.Size.X:F0}x{r.Size.Y:F0} a={t.Panel.Modulate.A:F2}) '{t.Title.Text}'");
        }
        sb.Append(inv, $" | visible={shown} overlaps={(overlaps.Count == 0 ? "none" : string.Join(',', overlaps))}");
        return sb.ToString();
    }
}
