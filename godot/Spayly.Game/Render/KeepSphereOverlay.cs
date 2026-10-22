using Godot;
using Spayly.Sim;

namespace Spayly.Game.Render;

/// <summary>
/// The "Keep inside" tool's sphere: a unit sphere mesh scaled to the radius, placed through <see cref="FloatingOrigin"/> like
/// every sim position, so its centre is a double km point and only the camera-relative offset is a float. Plus a handle on
/// the equator, on the side facing the camera, that the player drags to resize it, and a marker at the centre.
/// </summary>
public partial class KeepSphereOverlay : Node3D
{
    static readonly StringName StrengthParam = "strength";
    readonly MeshInstance3D sphere;
    readonly ShaderMaterial material;
    readonly MeshInstance3D handle = AimOverlay.Marker(new Vector3(0.5f, 2.2f, 3f));
    readonly MeshInstance3D centre = AimOverlay.Marker(new Vector3(0.6f, 1.6f, 3f));
    float lastStrength = -1;

    public KeepSphereOverlay()
    {
        Name = "KeepSphereOverlay";
        material = new ShaderMaterial { Shader = GD.Load<Shader>("res://Shaders/keep_sphere.gdshader"), RenderPriority = 5 };
        sphere = new MeshInstance3D
        {
            Mesh = new SphereMesh { Radius = 1, Height = 2, RadialSegments = 64, Rings = 32 },
            MaterialOverride = material,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Visible = false,
            // The camera may be inside it, and the scaled bounds are in the mesh's own unit.
            ExtraCullMargin = 16384,
        };
        AddChild(sphere);
        AddChild(handle);
        AddChild(centre);
    }

    /// <summary>
    /// Draw the sphere. <paramref name="handleKm"/> is the resize handle's sim position (null hides it); <paramref name="strength"/>
    /// dims it while it only follows the pointer and before it has been placed.
    /// </summary>
    public void Show(Vec3 centreKm, double radiusKm, Vec3? handleKm, float strength, ViewContext ctx)
    {
        var p = ctx.Origin.Place(centreKm, out var k, out _);
        var s = (float)(radiusKm * RenderUnits.Scale * k);
        sphere.Position = p;
        sphere.Scale = new Vector3(s, s, s);
        sphere.Visible = s > 0 && float.IsFinite(s);
        if (strength != lastStrength)
        {
            lastStrength = strength;
            material.SetShaderParameter(StrengthParam, strength);
        }
        AimOverlay.Place(centre, centreKm, 8, ctx);
        if (handleKm is Vec3 h) AimOverlay.Place(handle, h, 14, ctx);
        else handle.Visible = false;
    }

    public void HideAll()
    {
        sphere.Visible = false;
        handle.Visible = false;
        centre.Visible = false;
    }
}
