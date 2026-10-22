using System;
using System.Collections.Generic;
using Godot;
using Spayly.Sim;

namespace Spayly.Game.Render;

/// <summary>
/// One body's name tag: a dot in the body's colour and the name, bottom-centred on a screen point. A press selects the
/// body and a second press within 380 ms flies to it (viewContext.ts makeLabel, which times the clicks itself because the
/// label drifts under the pointer); the press never reaches the stage, as the web's <c>stopPropagation</c>.
/// </summary>
public partial class BodyLabel : Control
{
    /// <summary>Approximate half width and height of the box, px, as the web's declutter measures it (9 + 3.4 per character, 18 tall).</summary>
    public float HalfWidth;
    public const float BoxHeight = 18;
    const ulong DoubleClickMs = 380;
    readonly Label text = null!;
    readonly ColorRect dot = null!;
    Body body = null!;
    readonly Labels owner = null!;
    ulong lastClickMs;
    bool hovered;
    // .label:hover { background: rgba(255,255,255,0.1); border-radius: 6px }
    static readonly StyleBoxFlat HoverBox = new() { BgColor = new Color(1, 1, 1, 0.1f), CornerRadiusTopLeft = 6, CornerRadiusTopRight = 6, CornerRadiusBottomLeft = 6, CornerRadiusBottomRight = 6 };

    public BodyLabel() { }

    public void Rebind(Body b) => body = b;

    public BodyLabel(Body body, LabelSettings settings, Labels owner)
    {
        this.body = body;
        this.owner = owner;
        Name = body.Id;
        MouseFilter = MouseFilterEnum.Stop;
        MouseDefaultCursorShape = CursorShape.PointingHand;
        TooltipText = $"{body.Name} — click to select, double-click to fly there";
        MouseEntered += () => { hovered = true; QueueRedraw(); };
        MouseExited += () => { hovered = false; QueueRedraw(); };
        HalfWidth = 9 + body.Name.Length * 3.4f;
        dot = new ColorRect { Color = new Color(body.Visual.Color), Size = new Vector2(6, 6), MouseFilter = MouseFilterEnum.Ignore };
        text = new Label { Text = body.Name, LabelSettings = settings, MouseFilter = MouseFilterEnum.Ignore };
        AddChild(dot);
        AddChild(text);
        Size = new Vector2(HalfWidth * 2, BoxHeight);
        dot.Position = new Vector2(2, 6);
        text.Position = new Vector2(11, -1);
    }

    /// <summary>Put the label's bottom-centre at (x, bottom), viewport px.</summary>
    public void Place(float x, float bottom) => Position = new Vector2(x - HalfWidth, bottom - BoxHeight);

    public override void _GuiInput(InputEvent e)
    {
        // pointerdown fires for any button, so a right or middle press on a label selects too and starts no pan.
        if (e is not InputEventMouseButton { Pressed: true } mb || mb.ButtonIndex is not (MouseButton.Left or MouseButton.Right or MouseButton.Middle)) return;
        AcceptEvent();
        var now = Time.GetTicksMsec();
        if (lastClickMs != 0 && now - lastClickMs < DoubleClickMs)
        {
            lastClickMs = 0;
            owner.Activate?.Invoke(body);
        }
        else
        {
            lastClickMs = now;
            owner.Select?.Invoke(body);
        }
    }

    public override void _Draw()
    {
        if (hovered) DrawStyleBox(HoverBox, new Rect2(Vector2.Zero, Size));
    }
}

/// <summary>
/// The label layer (the web's CSS2D layer): a CanvasLayer holding one <see cref="BodyLabel"/> per body, and the projection
/// every screen-space test uses. Which labels show is the app's call (App.Labels: occlusion, then declutter).
/// </summary>
public partial class Labels : CanvasLayer
{
    readonly Dictionary<string, BodyLabel> byId = [];
    readonly LabelSettings settings = new() { FontSize = 13, FontColor = new Color(0.86f, 0.9f, 1f), OutlineSize = 4, OutlineColor = new Color(0, 0, 0, 0.85f) };
    Camera3D camera = null!;
    readonly Control root = new() { MouseFilter = Control.MouseFilterEnum.Ignore };

    public Labels() { }

    public Labels(Camera3D cam)
    {
        camera = cam;
        Layer = 1;
        AddChild(root);
    }

    /// <summary>Whether the whole layer shows (settings.labels).</summary>
    public bool Shown
    {
        get => root.Visible;
        set => root.Visible = value;
    }

    /// <summary>A label press (the web's <c>BodyActions.select</c>); set by the app.</summary>
    public Action<Body>? Select;
    /// <summary>A label double-press (the web's <c>BodyActions.activate</c>: select and fly above); set by the app.</summary>
    public Action<Body>? Activate;

    public BodyLabel Add(Body body)
    {
        var l = new BodyLabel(body, settings, this);
        root.AddChild(l);
        byId[body.Id] = l;
        return l;
    }

    /// <summary>A restored clone replaces the body behind a label (undo); clicks must select the clone, not the old object.</summary>
    public void Rebind(Body b)
    {
        if (byId.TryGetValue(b.Id, out var l)) l.Rebind(b);
    }

    public void Remove(string id)
    {
        if (!byId.Remove(id, out var l)) return;
        l.QueueFree();
    }

    public BodyLabel? For(string id) => byId.GetValueOrDefault(id);

    /// <summary>
    /// Viewport position of a camera-relative render point, and whether it is in front of the camera and inside the depth
    /// range (the web's projected z in [−1, 1]).
    /// </summary>
    public bool Project(Vector3 renderPos, out Vector2 screen)
    {
        if (camera.IsPositionBehind(renderPos))
        {
            screen = default;
            return false;
        }
        screen = camera.UnprojectPosition(renderPos);
        var depth = -camera.Basis.Z.Dot(renderPos);
        return depth >= camera.Near && depth <= camera.Far;
    }
}
