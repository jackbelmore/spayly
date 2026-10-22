using System;
using Godot;
using Spayly.Sim;

namespace Spayly.Game.Render;

/// <summary>
/// Base of every body's scene node; the BodyView interface of src/render/viewContext.ts. The node itself only ever holds
/// the camera-relative (squeezed) position; rotation and radius live on the <see cref="Surface"/> child's basis, because
/// assigning <c>Node3D.Basis</c> overwrites <c>Scale</c> (spike M1c).
/// </summary>
public abstract partial class BodyView : Node3D
{
    public Body Body { get; private set; } = null!;
    /// <summary>The unit-radius mesh; its basis is orientation × radius.</summary>
    protected MeshInstance3D Surface = null!;
    /// <summary>Legibility floor for this kind of view, px.</summary>
    protected virtual double MinPixels(ViewContext ctx) => ctx.MinPixelRadius;
    /// <summary>Label height above the centre when the body is a dot, in pixels (the web's 6 px planets, 8 px Sun).</summary>
    protected virtual double DotLabelPixels => 6;
    /// <summary>Label height above the centre when the body is a disc, in radii.</summary>
    protected virtual double DiscLabelRadii => 1.3;

    /// <summary>Rendered radius after diorama and minimum-pixel scaling, km (unsqueezed).</summary>
    public double VisualRadiusKm { get; private set; }
    /// <summary>On-screen radius, px.</summary>
    public double PixelRadius { get; private set; }
    /// <summary>Unsqueezed camera distance to the centre, render units.</summary>
    public double TrueDistance { get; private set; }
    /// <summary>Squeeze factor applied this frame (1 when near).</summary>
    public double SqueezeFactor { get; private set; } = 1;
    /// <summary>Where the label's bottom edge sits above the centre, in on-screen radii (the web's placeLabel height).</summary>
    public double LabelHeight { get; private set; } = 1.3;
    /// <summary>False once the body fills the view, when its name would sit in the middle of the screen.</summary>
    public bool LabelAllowed { get; private set; } = true;
    /// <summary>Estimated mean surface temperature, K (baseline plus the change in equilibrium temperature); null without a baseline, and for every view but PlanetView.</summary>
    public double? SurfaceTemp { get; protected set; }
    /// <summary>Equilibrium temperature at the current distance and luminosity, K; only PlanetView sets it, as in the web.</summary>
    public double? EquilibriumTemp { get; protected set; }
    /// <summary>This frame's orientation, geometry frame → render frame.</summary>
    public Orientation.DQuat Orient { get; private set; } = Orientation.DQuat.Identity;

    protected void Init(Body body, Mesh mesh, Material material)
    {
        Body = body;
        Name = body.Id;
        Surface = new MeshInstance3D { Mesh = mesh, MaterialOverride = material, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        AddChild(Surface);
    }

    /// <summary>Place and size the body for this frame. Subclasses extend it for materials; it never writes the Body.</summary>
    public virtual void Update(ViewContext ctx)
    {
        Position = ctx.Origin.Place(Body.Pos, out var k, out var dist);
        SqueezeFactor = k;
        TrueDistance = dist;
        var r = ctx.VisualRadius(Body, dist, MinPixels(ctx));
        VisualRadiusKm = r / RenderUnits.Scale;
        PixelRadius = r / Math.Max(dist, 1e-9) * ctx.PixelsPerRadian;
        Orient = Orientation.BodyOrientation(Body, ctx.Jd);
        var s = (float)(r * k);
        Surface.Basis = new Basis(Orient.ToGodot()) * Basis.FromScale(new Vector3(s, s, s));
        LabelHeight = PixelRadius > 4 ? DiscLabelRadii : DotLabelPixels / Math.Max(PixelRadius, 1e-9);
        LabelAllowed = PixelRadius <= 0.45 * ctx.PixelsPerRadian;
    }

    /// <summary>
    /// Point the view at a restored clone of its body (undo, fault rollback). Only what the constructor took from the body
    /// (kind, seed, look) must match; everything else is read each frame. Subclasses drop caches keyed on the old object.
    /// </summary>
    public virtual void Rebind(Body body) => Body = body;

    /// <summary>True when <paramref name="body"/> is the same body this view was built for, so <see cref="Rebind"/> is enough.</summary>
    public bool CanRebind(Body body) =>
        body.Id == Body.Id && body.Kind == Body.Kind && body.Seed == Body.Seed && body.Name == Body.Name && Equals(body.Visual, Body.Visual) && body.Kind != BodyKind.BlackHole;

    /// <summary>Called before the node is freed; views holding GPU resources release them here.</summary>
    public virtual void DisposeView() { }
}
