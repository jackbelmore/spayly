using System;
using Godot;
using Spayly.Sim;

namespace Spayly.Game.Render;

/// <summary>Switches and looks shared by every black hole's view and the lensing pass; the app owns one.</summary>
public sealed class BlackHoleOptions
{
    /// <summary>Bend light (<see cref="LensingPass"/>). Off: a black horizon ball of radius r_s and the flat disc.</summary>
    public bool Lensing = true;
    public bool Disc = true;
    /// <summary>[SCI] Doppler and gravitational shift of the disc (colour by g, brightness by g⁴); off paints every radius at rest.</summary>
    public bool Doppler = true;
    /// <summary>Disc inner edge, r_s: the Schwarzschild ISCO.</summary>
    public double DiscInnerRs = 3;
    /// <summary>Disc outer edge, r_s [ART].</summary>
    public double DiscOuterRs = 20;
    /// <summary>Display temperature at the profile peak for a disc at the Eddington luminosity, K [GAME]: the real ~10⁷ K would be X-ray white.</summary>
    public double DiscPeakDisplayK = 9000;
    /// <summary>The disc's prograde pole, sim frame: ecliptic north, so a fly-above view sees it nearly edge-on.</summary>
    public Vec3 DiscPoleSim = new(0, 0, 1);
}

/// <summary>
/// A black hole. It draws no sphere: with lensing on, each frame it publishes where it is and how big — the camera
/// direction and true distance, the rendered r_s (diorama-scaled like every other body, but with no minimum-pixel floor:
/// a floor on r_s would inflate the Einstein ring, which grows as √r_s) and the disc axes — to <see cref="LensingPass"/>,
/// which draws the shadow, the bent sky and the disc. With lensing off it shows a black horizon ball of radius r_s and the
/// flat <see cref="AccretionDisc"/>, so the two can be compared. Never writes the Body.
/// </summary>
public partial class BlackHoleView : BodyView
{
    readonly LensingPass? pass;
    readonly BlackHoleOptions? options;
    readonly AccretionDisc? disc;
    int slot = -1;
    static StandardMaterial3D? horizon;

    protected override double MinPixels(ViewContext ctx) => 0;
    protected override double DotLabelPixels => 8;
    /// <summary>Radii are r_s: the label clears the shadow, whose far-field radius is b_c ≈ 2.6 r_s.</summary>
    protected override double DiscLabelRadii => 3.2;

    public BlackHoleView() { }

    public BlackHoleView(Body body, LensingPass pass, BlackHoleOptions options)
    {
        this.pass = pass;
        this.options = options;
        horizon ??= new StandardMaterial3D { ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, AlbedoColor = Colors.Black };
        Init(body, Meshes.Sphere, horizon);
        disc = new AccretionDisc(options);
        AddChild(disc);
        slot = pass.Register();
        if (slot < 0) GD.PushWarning($"[bh] {body.Id}: more than {LensingPass.MaxHoles} black holes; this one is not lensed");
    }

    /// <summary>This hole's disc brightness at its temperature peak, before exposure compensation; the app sets it from its accretion (App.HoleDisc.cs). 0: no disc.</summary>
    public double DiscBrightness;
    /// <summary>This hole's display temperature at the profile peak, K.</summary>
    public double DiscPeakK = 2000;

    /// <summary>Whether the lensing pass gave this hole a slot (it has <see cref="LensingPass.MaxHoles"/>).</summary>
    public bool Lensed => slot >= 0;

    /// <summary>Rendered Schwarzschild radius this frame, render units (true or diorama-scaled).</summary>
    public double RsUnits => VisualRadiusKm * RenderUnits.Scale;

    public override void Update(ViewContext ctx)
    {
        base.Update(ctx);
        if (pass == null || options == null || disc == null) return;
        var lens = options.Lensing && slot >= 0;
        Surface.Visible = !lens;
        var rs = RsUnits;
        var k = SqueezeFactor;
        var dir = Position.LengthSquared() > 0 ? Position.Normalized() : new Vector3(0, 0, -1);
        var (n, x) = AccretionDisc.Axes(options.DiscPoleSim);
        disc.Update(ctx, dir, TrueDistance, rs, k, n, x, options, DiscBrightness, DiscPeakK, options.Disc && !lens && DiscBrightness > 0);
        if (!lens) return;
        pass.Publish(slot, new LensingPass.Hole
        {
            Dir = dir, TrueDistance = TrueDistance, RsUnits = rs, Squeeze = k,
            DiscInner = options.DiscInnerRs, DiscOuter = options.DiscOuterRs, DiscNormal = n, DiscX = x,
            DiscBrightness = options.Disc ? DiscBrightness : 0, DiscPeakK = DiscPeakK,
        }, ctx.ExposureComp, ctx.RealTime, ctx.Origin.SqueezeStart, options.Disc, options.Doppler);
    }

    /// <summary>Expected angular radius of the shadow from where the camera is, rad (a static observer's; <see cref="BlackHole.ShadowAngularRadius"/>).</summary>
    public double ShadowAngularRadius => BlackHole.ShadowAngularRadius(TrueDistance / Math.Max(RsUnits, 1e-30));

    public override void DisposeView()
    {
        pass?.Unregister(slot);
        slot = -1;
    }
}
