using System;
using Godot;
using Spayly.Sim;

namespace Spayly.Game.Render;

/// <summary>
/// The body shaders (godot/Spayly.Game/Shaders), their uniform names as cached <see cref="StringName"/>s — a string argument
/// would allocate a StringName on every per-frame set — and the per-frame body-frame vectors every body shader takes.
/// </summary>
public static class ShaderLib
{
    static Shader? planet, clouds, atmosphere, ring, sun, corona, rock, glow;
    public static Shader Planet => planet ??= GD.Load<Shader>("res://Shaders/planet.gdshader");
    public static Shader Clouds => clouds ??= GD.Load<Shader>("res://Shaders/clouds.gdshader");
    public static Shader Atmosphere => atmosphere ??= GD.Load<Shader>("res://Shaders/atmosphere.gdshader");
    public static Shader Ring => ring ??= GD.Load<Shader>("res://Shaders/ring.gdshader");
    public static Shader Sun => sun ??= GD.Load<Shader>("res://Shaders/sun.gdshader");
    public static Shader Corona => corona ??= GD.Load<Shader>("res://Shaders/corona.gdshader");
    public static Shader Rock => rock ??= GD.Load<Shader>("res://Shaders/rock.gdshader");
    public static Shader Glow => glow ??= GD.Load<Shader>("res://Shaders/glow.gdshader");

    public static readonly StringName DayMap = "day_map", NightMap = "night_map", SpecularMap = "specular_map", NormalMap = "normal_map",
        CloudsMap = "clouds_map", DamageMap = "damage_map", RingMap = "ring_map", Map = "map", Sprite = "sprite";
    public static readonly StringName HasNight = "has_night", HasSpecular = "has_specular", HasNormal = "has_normal", HasClouds = "has_clouds",
        HasRing = "has_ring", HasDamage = "has_damage", HasMap = "has_map";
    public static readonly StringName SunDir = "sun_dir", SunColor = "sun_color", SunIntensity = "sun_intensity", ViewPos = "view_pos",
        SunAngular = "sun_angular", Occ0 = "occ0", Occ1 = "occ1", Occ2 = "occ2", Occ3 = "occ3", OccCount = "occ_count";
    public static readonly StringName HeatTex = "heat_tex", HeatCount = "heat_count", Magma = "magma", Climate = "climate",
        Time = "time", Gas = "gas", Dust = "dust", CloudShift = "cloud_shift";
    public static readonly StringName RingInner = "ring_inner", RingOuter = "ring_outer", Inner = "inner", Outer = "outer";
    public static readonly StringName BetaR = "beta_r", BetaM = "beta_m", HR = "h_r", HM = "h_m", AtmoOn = "atmo_on", AtmR = "atm_r",
        G = "g", Strength = "strength";
    public static readonly StringName Color = "color", Intensity = "intensity", Flare = "flare", Opacity = "opacity", SeedOffset = "seed_offset",
        Tint = "tint";

    static readonly StringName[] OccNames = [Occ0, Occ1, Occ2, Occ3];

    /// <summary>Unit vector from the body to the Sun in the body's geometry frame (sim positions, so the depth squeeze never bends it).</summary>
    public static Vector3 SunDirBody(ViewContext ctx, Body b, Orientation.DQuat invQ, out double distKm)
    {
        var rel = ctx.SunPos - b.Pos;
        distKm = Vec3.Length(rel);
        var inv = distKm > 0 ? 1 / distKm : 0;
        var d = invQ.Rotate(new Vec3(rel.X * inv, rel.Z * inv, -rel.Y * inv));
        return new Vector3((float)d.X, (float)d.Y, (float)d.Z);
    }

    /// <summary>Camera position in the body's geometry frame, in units of the rendered radius (km).</summary>
    public static Vector3 ViewPosBody(ViewContext ctx, Body b, Orientation.DQuat invQ, double radiusKm)
    {
        var rel = ctx.Origin.Origin - b.Pos;
        var inv = 1 / Math.Max(radiusKm, 1e-12);
        var d = invQ.Rotate(new Vec3(rel.X * inv, rel.Z * inv, -rel.Y * inv));
        return new Vector3((float)d.X, (float)d.Y, (float)d.Z);
    }

    public static Vector3 Rgb(Color c) => new(c.R, c.G, c.B);

    /// <summary>Write the occluder uniforms; the four vectors only when there are casters, the count only when it changed.</summary>
    public static void SetOccluders(ShaderMaterial m, Vector4[] occ, int n, ref int lastCount)
    {
        for (var i = 0; i < n; i++) m.SetShaderParameter(OccNames[i], occ[i]);
        if (n != lastCount)
        {
            m.SetShaderParameter(OccCount, n);
            lastCount = n;
        }
    }

    /// <summary>A camera-facing quad of <paramref name="size"/> render units: the Sprite / billboard plane of the web views.</summary>
    public static Basis Billboard(ViewContext ctx, float size) => new Basis(ctx.CameraQuaternion) * Basis.FromScale(new Vector3(size, size, size));
}
