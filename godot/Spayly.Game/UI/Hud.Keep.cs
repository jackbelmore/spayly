using System;
using Godot;

namespace Spayly.Game.UI;

/// <summary>
/// The "Keep inside" tool's HUD: its line in the mode bar (instructions, then a live count of what the sphere keeps and what
/// Apply would delete, with Apply and Cancel) and the confirm prompt, a modal like the help overlay: its backdrop swallows
/// pointer events so a click on it never reaches the stage and moves the sphere.
/// </summary>
public partial class Hud
{
    Button modeKeep = null!;
    Control keepPanel = null!;
    readonly HudLabel keepText = new();
    Control keepConfirm = null!;
    PanelContainer keepConfirmPanel = null!;
    readonly HudLabel keepConfirmText = new();
    Action? keepConfirmOk, keepConfirmCancel;

    /// <summary>The mode button and its panel, built into the bottom bar by <see cref="BuildBottom"/>.</summary>
    void BuildKeepPanel(VBoxContainer col)
    {
        var box = VBox(6);
        box.Visible = false;
        keepText.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        keepText.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        keepText.ThemeTypeVariation = HudTheme.LabelMuted;
        box.AddChild(keepText);
        var row = HBox(6);
        row.AddChild(Btn("Apply (Enter)", () => app?.RequestKeepApply(), "Ask to delete everything outside the sphere (you confirm first; Ctrl+Z undoes it)", HudTheme.ButtonActive));
        row.AddChild(Btn("Cancel (Esc)", () => app?.SetMode(Spayly.Game.App.Mode.Explore), "Leave the tool and change nothing"));
        box.AddChild(row);
        keepPanel = box;
        col.AddChild(box);
    }

    void BuildKeepConfirm()
    {
        var backdrop = new ColorRect { Color = new Color(0, 0, 0, 0.45f), MouseFilter = Control.MouseFilterEnum.Stop, Visible = false };
        backdrop.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        var centre = new CenterContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        centre.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        var panel = NewPanel();
        panel.CustomMinimumSize = new Vector2(460, 0);
        var v = VBox(10);
        keepConfirmText.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        keepConfirmText.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        v.AddChild(keepConfirmText);
        var row = HBox(8);
        row.AddChild(Btn("Remove (Enter)", () => keepConfirmOk?.Invoke(), "Delete them; Ctrl+Z brings every one back", HudTheme.ButtonActive));
        row.AddChild(Btn("Cancel (Esc)", () => keepConfirmCancel?.Invoke()));
        v.AddChild(row);
        panel.AddChild(v);
        centre.AddChild(panel);
        backdrop.AddChild(centre);
        keepConfirm = backdrop;
        keepConfirmPanel = panel;
        root.AddChild(backdrop);
    }

    /// <summary>The live line in the Keep-inside panel: what the sphere keeps and what Apply would delete.</summary>
    public void SetKeepReadout(string text) => keepText.Set(text);

    public void ShowKeepConfirm(string text, Action ok, Action cancel)
    {
        keepConfirmText.Set(text);
        keepConfirmOk = ok;
        keepConfirmCancel = cancel;
        keepConfirm.Visible = true;
    }

    public void HideKeepConfirm() => keepConfirm.Visible = false;

    public bool KeepConfirmVisible => keepConfirm.Visible;

    /// <summary>Capture helper: the prompt's text and rect, for the confirm-prompt screenshot's log line.</summary>
    public string KeepConfirmSummary => $"visible={keepConfirm.Visible} text='{keepConfirmText.Text}' rect={keepConfirmPanel.GetGlobalRect()}";
}
