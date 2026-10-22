using System;
using System.Collections.Generic;
using Godot;
using Spayly.Sim;

namespace Spayly.Game.Render;

/// <summary>
/// The asteroid-mode overlay (app.ts pathLine, hitMarker, spawnMarker): the predicted path as a camera-relative polyline and
/// two additive markers held at a constant pixel size. Drawn over everything, as the web's depthTest:false sprites are, so
/// a hit on the far side of a planet still shows where it lands.
/// </summary>
public partial class AimOverlay : Node3D
{
    public const int PathPoints = 700;
    static readonly StringName ColorParam = "color";
    static readonly StringName PowerParam = "power";
    static readonly StringName ModeParam = "mode";

    readonly PolyLine path = new(PathPoints, new Color("#9fd0ff"), 0.95f);
    readonly MeshInstance3D hit;
    readonly MeshInstance3D spawn;

    public AimOverlay()
    {
        Name = "AimOverlay";
        AddChild(path.Node);
        hit = Marker(new Vector3(3, 1.2f, 0.6f));
        spawn = Marker(new Vector3(0.6f, 1.6f, 3));
        AddChild(hit);
        AddChild(spawn);
    }

    /// <summary>A billboard in the flash shader's sprite mode; colours are the web's linear HDR sprite colours.</summary>
    static MeshInstance3D Marker(Vector3 color)
    {
        var mat = new ShaderMaterial { Shader = ImpactEffects.FlashShader, RenderPriority = 10 };
        mat.SetShaderParameter(ModeParam, 0);
        mat.SetShaderParameter(PowerParam, 1.2f);
        mat.SetShaderParameter(ColorParam, color);
        return new MeshInstance3D
        {
            Mesh = ImpactEffects.Quad,
            MaterialOverride = mat,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Visible = false,
            // The quad's own bounds are one unit; the billboard never leaves its centre's neighbourhood by more than its scale.
            ExtraCullMargin = 1,
        };
    }

    public bool HitVisible
    {
        get => hit.Visible;
        set => hit.Visible = value;
    }

    public bool SpawnVisible
    {
        get => spawn.Visible;
        set => spawn.Visible = value;
    }

    /// <summary>Draw a path of absolute sim positions (km); <paramref name="brightness"/> scales the additive colour.</summary>
    public void ShowPath(List<Vec3> points, FloatingOrigin origin, float brightness) => path.Set(points, Vec3.Zero, origin, brightness);

    public void HidePath()
    {
        if (path.Visible) path.Clear();
    }

    public void PlaceHit(Vec3 posKm, double px, ViewContext ctx) => Place(hit, posKm, px, ctx);

    public void PlaceSpawn(Vec3 posKm, double px, ViewContext ctx) => Place(spawn, posKm, px, ctx);

    /// <summary>Put a marker at a sim point, <paramref name="px"/> pixels wide whatever its distance (app.ts placeMarker).</summary>
    static void Place(MeshInstance3D m, Vec3 posKm, double px, ViewContext ctx)
    {
        var p = ctx.Origin.Place(posKm, out _, out _);
        var s = (float)(px * p.Length() / ctx.PixelsPerRadian);
        m.Position = p;
        m.Scale = new Vector3(s, s, s);
        m.Visible = s > 0 && float.IsFinite(s);
    }

    public void Clear()
    {
        HidePath();
        if (hit.Visible) hit.Visible = false;
        if (spawn.Visible) spawn.Visible = false;
    }
}
