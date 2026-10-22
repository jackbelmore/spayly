using System;
using Godot;
using Spayly.Sim;

namespace Spayly.Game.Render;

/// <summary>
/// Planet or moon; port of src/render/planetView.ts. Surface (planet.gdshader: day/night/specular/normal/clouds/damage/ring
/// shadow/eclipses/heat/magma/climate/gas limb darkening), optional cloud shell, atmosphere shell, ring and a glow sprite for
/// when the body is a dot. Maps are engine-streamed (<see cref="TextureLibrary"/>); the crater overlay is painted on the CPU
/// only when <c>Damage.Version</c> moves. Reads the Body, never writes it.
/// </summary>
public partial class PlanetView : BodyView, IStreamedView
{
    ShaderMaterial mat = null!;
    ShaderMaterial? cloudMat, atmoMat, ringMat;
    MeshInstance3D? clouds, atmosphere, ring;
    MeshInstance3D glow = null!;
    ShaderMaterial glowMat = null!;
    Texture2D? dayMap;
    int dayWidth;
    Color glowBase;
    double atmThickness, ringOuter = 1;

    Image? damageImage;
    ImageTexture? damageTex;
    byte[]? damagePx;
    int paintedVersion = -1;

    readonly Vector4[] occ = new Vector4[Shadows.MaxOccluders];
    int occCount = -1, cloudOccCount = -1, atmoOccCount = -1;
    /// <summary>12 × 2 RGBAF texels: row 0 direction + angular radius, row 1 glow (see planet.gdshader heat_tex).</summary>
    readonly float[] heatTexels = new float[12 * 2 * 4];
    byte[]? heatBytes;
    Image? heatImage;
    ImageTexture? heatTex;
    int heatCount;
    float lastMagma = -1, lastDust = -1, lastClimate = -2;
    Color lastSunColor = new(-1, -1, -1);
    double lastRadius = -1;
    double? baselineTeq;

    /// <summary>Clouds drift eastward relative to the ground by about 0.065 turns a day.</summary>
    const double CloudDrift = 2 * Math.PI * 0.065 / 86400;

    public Texture2D? AlbedoMap => dayMap;
    public int AlbedoWidth => dayWidth;

    DamageState? previewDamage;
    /// <summary>What the damage uniforms and crater overlay come from: the body's own, unless a harness preview replaced it.</summary>
    DamageState DamageSource => previewDamage ?? Body.Damage;

    /// <summary>
    /// Harness only (App.Textures <c>--damage-preview</c>): show <paramref name="damage"/> — a copy the caller filled, never
    /// the body's own — so crater, heat and magma rendering can be checked without an impact changing the sim.
    /// </summary>
    /// <summary>The restored clone's damage is a different object whose version may repeat an older one: paint it afresh.</summary>
    public override void Rebind(Body body)
    {
        base.Rebind(body);
        paintedVersion = -1;
        lastRadius = -1;
    }

    public void PreviewDamage(DamageState? damage)
    {
        previewDamage = damage;
        paintedVersion = -1;
    }

    /// <summary>The crater overlay as last painted (RGB8, w × h), for the harness to save; null before any crater.</summary>
    public Image? DamageImage => damageImage;

    public PlanetView() { }

    public PlanetView(Body body)
    {
        var vis = body.Visual;
        var key = TextureLibrary.KeyFor(body);
        glowBase = new Color(vis.Color).SrgbToLinear();
        mat = new ShaderMaterial { Shader = ShaderLib.Planet };
        dayMap = TextureLibrary.Load(key, MapSlot.Albedo);
        dayWidth = dayMap != null ? TextureLibrary.EntryFor(key)?.Width ?? dayMap.GetWidth() : 0;
        mat.SetShaderParameter(ShaderLib.DayMap, dayMap ?? TextureLibrary.ProceduralPlanet(body.Seed, new Color(vis.Color), body.Surface));
        if (TextureLibrary.Load(key, MapSlot.Night) is { } night)
        {
            mat.SetShaderParameter(ShaderLib.NightMap, night);
            mat.SetShaderParameter(ShaderLib.HasNight, 1f);
        }
        if (TextureLibrary.Load(key, MapSlot.Specular) is { } spec)
        {
            mat.SetShaderParameter(ShaderLib.SpecularMap, spec);
            mat.SetShaderParameter(ShaderLib.HasSpecular, 1f);
        }
        if (TextureLibrary.Load(key, MapSlot.Normal) is { } normal)
        {
            mat.SetShaderParameter(ShaderLib.NormalMap, normal);
            mat.SetShaderParameter(ShaderLib.HasNormal, 1f);
        }
        mat.SetShaderParameter(ShaderLib.Gas, body.Surface == Spayly.Sim.Surface.Gas ? 1f : 0f);
        mat.SetShaderParameter(ShaderLib.DamageMap, TextureLibrary.Grey());
        Init(body, Meshes.Sphere, mat);

        // The web builds the shell whenever the catalogue names clouds but shows it only once the map has loaded.
        if (vis.Clouds != null && TextureLibrary.Load(key, MapSlot.Clouds) is { } cloudTex)
        {
            cloudMat = new ShaderMaterial { Shader = ShaderLib.Clouds, RenderPriority = 1 };
            cloudMat.SetShaderParameter(ShaderLib.CloudsMap, cloudTex);
            clouds = new MeshInstance3D { Mesh = Meshes.Sphere, MaterialOverride = cloudMat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, Name = "Clouds" };
            AddChild(clouds);
            mat.SetShaderParameter(ShaderLib.CloudsMap, cloudTex);
            mat.SetShaderParameter(ShaderLib.HasClouds, 1f);
        }

        if (vis.Atmosphere is { } a)
        {
            atmThickness = a.Thickness;
            atmoMat = new ShaderMaterial { Shader = ShaderLib.Atmosphere, RenderPriority = 2 };
            atmoMat.SetShaderParameter(ShaderLib.AtmR, (float)(1 + a.Thickness));
            atmoMat.SetShaderParameter(ShaderLib.G, (float)a.G);
            atmoMat.SetShaderParameter(ShaderLib.Strength, (float)a.Strength);
            atmosphere = new MeshInstance3D { Mesh = Meshes.Sphere, MaterialOverride = atmoMat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, Name = "Atmosphere" };
            AddChild(atmosphere);
            mat.SetShaderParameter(ShaderLib.AtmoOn, 1f);
        }

        if (vis.Ring is { } r)
        {
            var inner = r.Inner / body.Radius;
            var outer = r.Outer / body.Radius;
            ringOuter = outer;
            var strip = (r.Texture != null ? TextureLibrary.Load(key, MapSlot.Ring) : null) ?? TextureLibrary.ProceduralRingStrip(body.Seed);
            ringMat = new ShaderMaterial { Shader = ShaderLib.Ring, RenderPriority = 1 };
            ringMat.SetShaderParameter(ShaderLib.RingMap, strip);
            ringMat.SetShaderParameter(ShaderLib.Inner, (float)inner);
            ringMat.SetShaderParameter(ShaderLib.Outer, (float)outer);
            ring = new MeshInstance3D { Mesh = Meshes.Ring(inner, outer), MaterialOverride = ringMat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, Name = "Ring" };
            AddChild(ring);
            mat.SetShaderParameter(ShaderLib.RingMap, strip);
            mat.SetShaderParameter(ShaderLib.HasRing, 1f);
            mat.SetShaderParameter(ShaderLib.RingInner, (float)inner);
            mat.SetShaderParameter(ShaderLib.RingOuter, (float)outer);
        }

        glowMat = new ShaderMaterial { Shader = ShaderLib.Glow, RenderPriority = 3 };
        glowMat.SetShaderParameter(ShaderLib.Sprite, TextureLibrary.RadialSprite());
        glow = new MeshInstance3D { Mesh = Meshes.Quad, MaterialOverride = glowMat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, Name = "Glow" };
        AddChild(glow);
    }

    public override void Update(ViewContext ctx)
    {
        base.Update(ctx);
        var body = Body;
        var d = DamageSource;
        var t = ctx.SimTime;
        var k = SqueezeFactor;
        var rKm = VisualRadiusKm;
        var s = (float)(rKm * RenderUnits.Scale * k);
        var invQ = Orient.Conjugate();
        var sunDir = ShaderLib.SunDirBody(ctx, body, invQ, out var dSun);

        // Climate and temperatures feed the inspector whether or not the body is on screen.
        var teq = EquilibriumTemperature(ctx.SunWatts, dSun, body.Visual.Albedo);
        baselineTeq ??= teq;
        EquilibriumTemp = teq;
        var climate = 0.0;
        if (body.Visual.BaselineTemp is { } baseT)
        {
            climate = Math.Clamp((teq - baselineTeq.Value) / 45, -1, 1);
            SurfaceTemp = baseT + (teq - baselineTeq.Value);
        }

        // Glow: a soft point while the disc is under ~16 px.
        var pixR = PixelRadius;
        var opacity = Math.Clamp((16 - pixR) / 12, 0, 1) * 0.9;
        glow.Visible = opacity > 0.01;
        if (glow.Visible)
        {
            glow.Basis = ShaderLib.Billboard(ctx, (float)(7 * TrueDistance / ctx.PixelsPerRadian * k));
            glowMat.SetShaderParameter(ShaderLib.Opacity, (float)opacity);
            var gk = (float)(1.2 * ctx.ExposureComp);
            glowMat.SetShaderParameter(ShaderLib.Color, new Vector3(glowBase.R * gk, glowBase.G * gk, glowBase.B * gk));
        }

        // Shell and ring transforms every frame: a stale one from a close pass (a 6.5-unit Earth atmosphere) would stay in
        // the scene at the body's new squeezed position, huge, while the culling test below uses the current size.
        var orientBasis = new Basis(Orient.ToGodot());
        var cloudAng = t * CloudDrift % (2 * Math.PI);
        if (clouds != null)
        {
            var cs = s * 1.0045f;
            clouds.Basis = orientBasis * new Basis(Vector3.Up, (float)cloudAng) * Basis.FromScale(new Vector3(cs, cs, cs));
        }
        if (atmosphere != null)
        {
            var sa = s * (float)(1 + atmThickness);
            atmosphere.Basis = orientBasis * Basis.FromScale(new Vector3(sa, sa, sa));
        }
        if (ring != null) ring.Basis = orientBasis * Basis.FromScale(new Vector3(s, s, s));

        // Nothing below changes what an off-screen body looks like later, so skip the uniform traffic.
        var extent = Math.Max(ringOuter, 1 + atmThickness) * 1.3;
        if (!ctx.OnScreen(Position, s * extent)) return;

        var viewPos = ShaderLib.ViewPosBody(ctx, body, invQ, rKm);
        var intensity = (float)ctx.Irradiance(body);
        var sunAng = (float)(ctx.SunRadiusKm / Math.Max(dSun, 1));
        var sunColorChanged = ctx.SunColor != lastSunColor;
        lastSunColor = ctx.SunColor;
        var sunColor = ShaderLib.Rgb(ctx.SunColor);
        var n = Shadows.OccludersFor(ctx, body, invQ, occ);

        mat.SetShaderParameter(ShaderLib.SunDir, sunDir);
        mat.SetShaderParameter(ShaderLib.SunIntensity, intensity);
        mat.SetShaderParameter(ShaderLib.SunAngular, sunAng);
        mat.SetShaderParameter(ShaderLib.ViewPos, viewPos);
        if (sunColorChanged) mat.SetShaderParameter(ShaderLib.SunColor, sunColor);
        ShaderLib.SetOccluders(mat, occ, n, ref occCount);

        UpdateHeat(t);
        var magma = (float)ViewContext.Decay(d.Magma, d.MagmaTime, d.MagmaTau, t);
        var dust = (float)ViewContext.Decay(d.Dust, d.DustTime, d.DustTau, t, 12 * 3600);
        if (magma != lastMagma) mat.SetShaderParameter(ShaderLib.Magma, lastMagma = magma);
        if (dust != lastDust) mat.SetShaderParameter(ShaderLib.Dust, dust);
        if ((float)climate != lastClimate) mat.SetShaderParameter(ShaderLib.Climate, lastClimate = (float)climate);
        if (heatCount > 0 || magma > 0.001f) mat.SetShaderParameter(ShaderLib.Time, (float)ctx.RealTime);
        if (d.Version != paintedVersion) PaintDamage();

        if (cloudMat != null)
        {
            mat.SetShaderParameter(ShaderLib.CloudShift, (float)(cloudAng / (2 * Math.PI)));
            cloudMat.SetShaderParameter(ShaderLib.SunDir, sunDir);
            cloudMat.SetShaderParameter(ShaderLib.SunIntensity, intensity);
            cloudMat.SetShaderParameter(ShaderLib.SunAngular, sunAng);
            if (sunColorChanged) cloudMat.SetShaderParameter(ShaderLib.SunColor, sunColor);
            if (dust != lastDust) cloudMat.SetShaderParameter(ShaderLib.Dust, dust);
            ShaderLib.SetOccluders(cloudMat, occ, n, ref cloudOccCount);
        }

        if (atmoMat != null)
        {
            var a = body.Visual.Atmosphere!.Value;
            if (body.Radius != lastRadius)
            {
                // Coefficients per metre times the radius in metres: optical depths in body radii, as the shaders integrate.
                var rm = body.Radius * 1000;
                var betaR = new Vector3((float)(a.Rayleigh.R * rm), (float)(a.Rayleigh.G * rm), (float)(a.Rayleigh.B * rm));
                foreach (var m in (ReadOnlySpan<ShaderMaterial>)[mat, atmoMat])
                {
                    m.SetShaderParameter(ShaderLib.BetaR, betaR);
                    m.SetShaderParameter(ShaderLib.BetaM, (float)(a.Mie * rm));
                    m.SetShaderParameter(ShaderLib.HR, (float)(a.RayleighH / rm));
                    m.SetShaderParameter(ShaderLib.HM, (float)(a.MieH / rm));
                }
            }
            atmoMat.SetShaderParameter(ShaderLib.SunDir, sunDir);
            atmoMat.SetShaderParameter(ShaderLib.ViewPos, viewPos);
            atmoMat.SetShaderParameter(ShaderLib.SunIntensity, intensity);
            atmoMat.SetShaderParameter(ShaderLib.SunAngular, sunAng);
            if (sunColorChanged) atmoMat.SetShaderParameter(ShaderLib.SunColor, sunColor);
            if (dust != lastDust) atmoMat.SetShaderParameter(ShaderLib.Dust, dust);
            ShaderLib.SetOccluders(atmoMat, occ, n, ref atmoOccCount);
        }
        lastRadius = body.Radius;
        lastDust = dust;

        if (ringMat != null)
        {
            ringMat.SetShaderParameter(ShaderLib.SunDir, sunDir);
            ringMat.SetShaderParameter(ShaderLib.ViewPos, viewPos);
            ringMat.SetShaderParameter(ShaderLib.SunIntensity, intensity);
            ringMat.SetShaderParameter(ShaderLib.SunAngular, sunAng);
            if (sunColorChanged) ringMat.SetShaderParameter(ShaderLib.SunColor, sunColor);
        }
    }

    /// <summary>Equilibrium temperature (K) at <paramref name="dKm"/> from a star of luminosity <paramref name="watts"/>.</summary>
    public static double EquilibriumTemperature(double watts, double dKm, double albedo)
    {
        var dm = Math.Max(dKm, 1) * 1000;
        return Math.Pow(watts * (1 - albedo) / (16 * Math.PI * Constants.StefanBoltzmann * dm * dm), 0.25);
    }

    /// <summary>The newest up-to-12 heat spots still glowing above 2 %; arrays go to the GPU only while any glow.</summary>
    void UpdateHeat(double t)
    {
        var list = DamageSource.Heat;
        var n = 0;
        for (var i = list.Count - 1; i >= 0 && n < 12; i--)
        {
            var h = list[i];
            var g = Math.Exp(-Math.Max(0, t - h.Time) / h.Tau);
            if (g < 0.02) continue;
            var o = n * 4;
            heatTexels[o] = (float)h.Dir.X;
            heatTexels[o + 1] = (float)h.Dir.Y;
            heatTexels[o + 2] = (float)h.Dir.Z;
            heatTexels[o + 3] = (float)h.AngularRadius;
            heatTexels[48 + o] = (float)g;
            n++;
        }
        if (n > 0)
        {
            heatBytes ??= new byte[heatTexels.Length * 4];
            System.Runtime.InteropServices.MemoryMarshal.AsBytes(heatTexels.AsSpan()).CopyTo(heatBytes);
            if (heatImage == null)
            {
                heatImage = Image.CreateFromData(12, 2, false, Image.Format.Rgbaf, heatBytes);
                heatTex = ImageTexture.CreateFromImage(heatImage);
                mat.SetShaderParameter(ShaderLib.HeatTex, heatTex);
            }
            else
            {
                heatImage.SetData(12, 2, false, Image.Format.Rgbaf, heatBytes);
                heatTex!.Update(heatImage);
            }
        }
        if (n != heatCount) mat.SetShaderParameter(ShaderLib.HeatCount, n);
        heatCount = n;
    }

    void PaintDamage()
    {
        var body = Body;
        var dmg = DamageSource;
        paintedVersion = dmg.Version;
        if (dmg.Craters.Count == 0)
        {
            mat.SetShaderParameter(ShaderLib.HasDamage, 0f);
            return;
        }
        var w = Damage.WidthFor(body);
        var h = w / 2;
        if (damagePx == null || damagePx.Length != w * h * 3) damagePx = new byte[w * h * 3];
        Damage.Paint(body, dmg, damagePx, w, h);
        if (damageImage == null || damageImage.GetWidth() != w)
            damageImage = Image.CreateFromData(w, h, false, Image.Format.Rgb8, damagePx);
        else
            damageImage.SetData(w, h, false, Image.Format.Rgb8, damagePx);
        damageImage.GenerateMipmaps();
        if (damageTex == null || damageTex.GetWidth() != w)
        {
            damageTex = ImageTexture.CreateFromImage(damageImage);
            mat.SetShaderParameter(ShaderLib.DamageMap, damageTex);
        }
        else damageTex.Update(damageImage);
        mat.SetShaderParameter(ShaderLib.HasDamage, 1f);
    }
}
