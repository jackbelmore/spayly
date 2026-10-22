using System;
using Godot;
using Spayly.Sim;

namespace Spayly.Game.Render;

/// <summary>
/// Asteroid or fragment; port of src/render/rockView.ts: one of 16 shared fbm-displaced welded icospheres, the real map
/// where the catalogue has one (Vesta, Pallas…) else a procedural albedo in a composition tint, magma glow, and a glow
/// sprite while it is a dot.
/// </summary>
public partial class RockView : BodyView, IStreamedView
{
    ShaderMaterial mat = null!;
    MeshInstance3D glow = null!;
    ShaderMaterial glowMat = null!;
    Texture2D? map;
    int mapWidth;
    float lastMagma = -1;
    Color lastSunColor = new(-1, -1, -1);

    protected override double MinPixels(ViewContext ctx) => 2.5;
    /// <summary>Lumps and squash keep most of a rock inside its nominal radius, so its label sits lower than a planet's.</summary>
    protected override double DiscLabelRadii => 1.25;

    public Texture2D? AlbedoMap => map;
    public int AlbedoWidth => mapWidth;

    /// <summary>Colours the procedural surface only (linear); a real map is shown untinted.</summary>
    static Vector3 Tint(Spayly.Sim.Surface s) => s switch
    {
        Spayly.Sim.Surface.Ice => new Vector3(0.8f, 0.85f, 0.9f),
        Spayly.Sim.Surface.Lava => new Vector3(0.35f, 0.3f, 0.28f),
        Spayly.Sim.Surface.Gas => new Vector3(0.6f, 0.55f, 0.5f),
        Spayly.Sim.Surface.Star => new Vector3(1, 1, 1),
        _ => new Vector3(0.55f, 0.5f, 0.45f),
    };

    public RockView() { }

    public RockView(Body body)
    {
        mat = new ShaderMaterial { Shader = ShaderLib.Rock };
        // Where in the shader's noise field this rock's surface comes from, so no two look alike.
        var rand = Rng.Mulberry32(unchecked((uint)(body.Seed + 11)));
        mat.SetShaderParameter(ShaderLib.SeedOffset, new Vector3((float)(rand() * 50), (float)(rand() * 50), (float)(rand() * 50)));
        var key = TextureLibrary.KeyFor(body);
        map = body.Visual.Texture != null ? TextureLibrary.Load(key, MapSlot.Albedo) : null;
        if (map != null)
        {
            mapWidth = TextureLibrary.EntryFor(key)?.Width ?? map.GetWidth();
            mat.SetShaderParameter(ShaderLib.Map, map);
            mat.SetShaderParameter(ShaderLib.HasMap, 1f);
            mat.SetShaderParameter(ShaderLib.Tint, new Vector3(1, 1, 1));
        }
        else mat.SetShaderParameter(ShaderLib.Tint, Tint(body.Surface));
        Init(body, Meshes.RockShape(body.Seed), mat);

        glowMat = new ShaderMaterial { Shader = ShaderLib.Glow, RenderPriority = 3 };
        glowMat.SetShaderParameter(ShaderLib.Sprite, TextureLibrary.RadialSprite());
        glowMat.SetShaderParameter(ShaderLib.Color, body.Surface == Spayly.Sim.Surface.Lava ? new Vector3(1, 0.45f, 0.15f) : new Vector3(0.9f, 0.85f, 0.8f));
        glow = new MeshInstance3D { Mesh = Meshes.Quad, MaterialOverride = glowMat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, Name = "Glow" };
        AddChild(glow);
    }

    public override void Update(ViewContext ctx)
    {
        base.Update(ctx);
        var body = Body;
        var d = body.Damage;
        var k = SqueezeFactor;
        var s = VisualRadiusKm * RenderUnits.Scale * k;
        var magma = (float)ViewContext.Decay(d.Magma, d.MagmaTime, d.MagmaTau, ctx.SimTime);

        var opacity = Math.Clamp((12 - PixelRadius) / 9, 0, 1) * (0.5 + 0.5 * magma);
        glow.Visible = opacity > 0.01;
        if (glow.Visible)
        {
            glow.Basis = ShaderLib.Billboard(ctx, (float)(5 * TrueDistance / ctx.PixelsPerRadian * k));
            glowMat.SetShaderParameter(ShaderLib.Opacity, (float)opacity);
        }

        if (!ctx.OnScreen(Position, s * 1.3)) return;
        var invQ = Orient.Conjugate();
        mat.SetShaderParameter(ShaderLib.SunDir, ShaderLib.SunDirBody(ctx, body, invQ, out _));
        mat.SetShaderParameter(ShaderLib.ViewPos, ShaderLib.ViewPosBody(ctx, body, invQ, VisualRadiusKm));
        mat.SetShaderParameter(ShaderLib.SunIntensity, (float)ctx.Irradiance(body));
        if (ctx.SunColor != lastSunColor)
        {
            mat.SetShaderParameter(ShaderLib.SunColor, ShaderLib.Rgb(ctx.SunColor));
            lastSunColor = ctx.SunColor;
        }
        if (magma != lastMagma) mat.SetShaderParameter(ShaderLib.Magma, lastMagma = magma);
        if (magma > 0.001f) mat.SetShaderParameter(ShaderLib.Time, (float)ctx.RealTime);
    }
}
