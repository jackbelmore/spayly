using System;
using Godot;
using Spayly.Sim;

namespace Spayly.Game.Render;

/// <summary>
/// Star; port of src/render/sunView.ts. Photosphere (sun.gdshader: the web's fbm mottling, Worley granules and supergranules,
/// umbra/penumbra spots, per-channel limb darkening, the map as structure) at 1.3 × exposureComp, so it reads the same
/// whatever the auto-exposure does, plus a camera-facing corona 6 radii wide that grows to stay ~10 px when the Sun is a dot
/// and fades as the disc fills the view. Both take their colour from <see cref="StarColor"/> at the star's effective
/// temperature, T = (L / 4πR²σ)^¼ from the luminosity App.Sun.cs derives from the live mass.
///
/// <c>--star-temp K</c> on the command line overrides that temperature for the star's own look only (capture tests of cool
/// and hot stars); nothing is written back to the Body, and the light on planets keeps the real one.
/// </summary>
public partial class SunView : BodyView, IStreamedView
{
    ShaderMaterial mat = null!;
    ShaderMaterial coronaMat = null!;
    MeshInstance3D corona = null!;
    Texture2D? map;
    int mapWidth;
    double lastTemp = -1, lastRadiusKm = -1;
    double granPhase, superPhase, lastSimTime = double.NaN, lastRealTime = double.NaN;
    double granulation = 1, spots = 1, peakNorm = 1;
    bool paramsDirty = true;

    /// <summary>Real granule lifetime, s of sim time.</summary>
    const double GranuleLifetime = 480;
    /// <summary>Supergranule lifetime, s of sim time (~1.5 days).</summary>
    const double SupergranuleLifetime = 1.3e5;
    /// <summary>How much faster than real the surface evolves ([ART]: at warp 1 a granule lives 24 s).</summary>
    const double SurfaceCompression = 20;
    /// <summary>Shortest on-screen granule lifetime, s of wall clock, whatever the warp: above it the surface would flicker.</summary>
    const double MinGranuleLifetimeReal = 2;
    /// <summary>The same for supergranules, s of wall clock.</summary>
    const double MinSupergranuleLifetimeReal = 20;

    static readonly StringName StarLut = "star_lut", StarTemp = "star_temp", GranPhase = "gran_phase", SuperPhase = "super_phase",
        LimbU1 = "limb_u1", LimbU2 = "limb_u2", GranulationParam = "granulation", SpotsParam = "spots", SpotCoverParam = "spot_cover",
        SpotLo = "spot_lo", SpotDt = "spot_dt", SpotContrast = "spot_contrast", GranFreq = "gran_freq", SuperFreq = "super_freq";

    /// <summary><c>--star-temp K</c>, or null.</summary>
    static readonly double? TempOverride = ParseTempOverride();

    /// <summary>Impact flare boost, decaying by 3 % a frame; the effects task sets it, nothing here raises it.</summary>
    public double Flare { get; set; }

    protected override double MinPixels(ViewContext ctx) => 2;
    protected override double DotLabelPixels => 8;
    protected override double DiscLabelRadii => 1.35;

    public Texture2D? AlbedoMap => map;
    public int AlbedoWidth => mapWidth;

    /// <summary>Effective temperature the star is drawn at, K (the <c>--star-temp</c> override if given).</summary>
    public double TempK { get; private set; } = 5772;

    /// <summary>Granulation contrast: 1 is the default look (the web's mottling plus the physical cells), 0 a smooth disc.</summary>
    public double Granulation
    {
        get => granulation;
        set { if (value != granulation) { granulation = value; paramsDirty = true; } }
    }

    /// <summary>Spot darkness: 1 is the default look (the web's spots), 0 none. Coverage still follows <see cref="SpotCover"/>.</summary>
    public double Spots
    {
        get => spots;
        set { if (value != spots) { spots = value; paramsDirty = true; } }
    }

    /// <summary>
    /// [GAME] Spot coverage, 1 for the Sun: spots need a deep convection zone, gone above ~7000 K, so coverage falls linearly
    /// from the Sun to 0 there and rises to 2 for cool stars (4544 K and below); it shrinks as R^−½ for large stars.
    /// </summary>
    public static double SpotCover(double tempK, double radiusKm) =>
        Math.Clamp((7000 - tempK) / (7000 - 5772.0), 0, 2) * Math.Pow(Math.Max(radiusKm, 1) / Constants.SunRadius, -0.5);

    /// <summary>
    /// [SCI] Umbra temperature deficit ΔT / T_eff: ΔT falls from ~2000 K on G stars to ~200 K on M dwarfs (Berdyugina 2005,
    /// Living Rev. Solar Phys. 2, 8, fig. 7), linear in T_eff between 3300 and 6300 K here; 0.29 (≈ 1680 K) for the Sun.
    /// </summary>
    public static double SpotDeficit(double tempK)
    {
        var t = Math.Max(tempK, 1000);
        return Math.Clamp(200 + 1800 * (t - 3300) / 3000, 200, 2000) / t;
    }

    public SunView() { }

    public SunView(Body body)
    {
        mat = new ShaderMaterial { Shader = ShaderLib.Sun };
        var key = TextureLibrary.KeyFor(body);
        map = TextureLibrary.Load(key, MapSlot.Albedo);
        mapWidth = map != null ? TextureLibrary.EntryFor(key)?.Width ?? map.GetWidth() : 0;
        mat.SetShaderParameter(ShaderLib.Map, map ?? TextureLibrary.ProceduralSun());
        mat.SetShaderParameter(ShaderLib.HasMap, 1f);
        mat.SetShaderParameter(StarLut, StarColor.Lut);
        Init(body, Meshes.Sphere, mat);

        coronaMat = new ShaderMaterial { Shader = ShaderLib.Corona, RenderPriority = 4 };
        coronaMat.SetShaderParameter(StarLut, StarColor.Lut);
        corona = new MeshInstance3D { Mesh = Meshes.Quad, MaterialOverride = coronaMat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, Name = "Corona" };
        AddChild(corona);
    }

    public override void Update(ViewContext ctx)
    {
        base.Update(ctx);
        var k = SqueezeFactor;
        var rUnits = VisualRadiusKm * RenderUnits.Scale;
        var pixR = PixelRadius;
        var time = (float)ctx.RealTime;
        AdvanceSurface(ctx);
        var r = Math.Max(Body.Radius, 1);
        TempK = TempOverride ?? (ctx.SunWatts > 0 ? Math.Pow(ctx.SunWatts / (4 * Math.PI * r * r * 1e6 * Constants.StefanBoltzmann), 0.25) : StarColor.MinK);
        // The limb fit costs ~0.3 ms, so it waits for a 0.1 % change (a mass drag), not every frame's rounding.
        var tempChanged = Math.Abs(TempK - lastTemp) > 1e-3 * TempK || r != lastRadiusKm;
        if (tempChanged)
        {
            lastTemp = TempK;
            lastRadiusKm = r;
            var (lr, lg, lb) = Photometry.LimbDarkeningCoefficients(TempK);
            mat.SetShaderParameter(StarTemp, (float)TempK);
            mat.SetShaderParameter(LimbU1, new Vector3((float)lr.U1, (float)lg.U1, (float)lb.U1));
            mat.SetShaderParameter(LimbU2, new Vector3((float)lr.U2, (float)lg.U2, (float)lb.U2));
            var cover = SpotCover(TempK, r);
            mat.SetShaderParameter(SpotCoverParam, (float)Math.Min(cover, 1));
            // Beyond full cover, spots spread by lowering the threshold (0.62 at cover 2) rather than darkening further.
            mat.SetShaderParameter(SpotLo, (float)(0.67 - 0.05 * Math.Max(cover - 1, 0)));
            var dt = SpotDeficit(TempK);
            mat.SetShaderParameter(SpotDt, (float)dt);
            // [GAME] √ so cool stars' many shallow spots still show; the Sun keeps the web's contrast.
            mat.SetShaderParameter(SpotContrast, (float)Math.Sqrt(Math.Min(dt / SpotDeficit(5772), 1)));
            mat.SetShaderParameter(GranFreq, (float)(r / 1000));
            mat.SetShaderParameter(SuperFreq, (float)(r / 30000));
            coronaMat.SetShaderParameter(StarTemp, (float)TempK);
            // [GAME] the LUT is luminance-1; scaling by the Sun's peak channel over this star's keeps the brightest channel
            // where the Sun's is, so a cool star stays orange and a hot one blue instead of both clipping to pale white.
            peakNorm = Peak(StarColor.Linear(5772)) / Peak(StarColor.Linear(TempK));
        }
        if (paramsDirty)
        {
            paramsDirty = false;
            mat.SetShaderParameter(GranulationParam, (float)granulation);
            mat.SetShaderParameter(SpotsParam, (float)spots);
        }

        var coronaScale = Math.Max(1, 10 * TrueDistance / ctx.PixelsPerRadian / (3 * rUnits));
        corona.Basis = ShaderLib.Billboard(ctx, (float)(6 * rUnits * coronaScale * k));
        var big = Math.Clamp((pixR - 40) / 360, 0, 1);
        coronaMat.SetShaderParameter(ShaderLib.Intensity, (float)(ctx.ExposureComp * Math.Min(1, 0.6 + 0.4 * Math.Sqrt(ctx.SunLuminosity)) * (1 - 0.7 * big)));
        coronaMat.SetShaderParameter(ShaderLib.Time, time);
        coronaMat.SetShaderParameter(ShaderLib.Flare, (float)Flare);
        Flare *= 0.97;

        if (!ctx.OnScreen(Position, rUnits * k * 2)) return;
        var invQ = Orient.Conjugate();
        // [GAME] a close-up "solar filter": as a 1000 km granule grows from 1 to 10 px below the camera the disc dims to
        // 55 %, so granules read as cells instead of clipping to white under the tone mapper. Views that show the whole
        // disc (granules under a pixel) are untouched.
        var heightKm = Math.Max(TrueDistance / RenderUnits.Scale - VisualRadiusKm, 1);
        var filter = 1 - 0.45 * Math.Clamp(Math.Log10(1000 / heightKm * ctx.PixelsPerRadian), 0, 1);
        mat.SetShaderParameter(ShaderLib.Intensity, (float)(1.3 * ctx.ExposureComp * filter * peakNorm));
        mat.SetShaderParameter(ShaderLib.ViewPos, ShaderLib.ViewPosBody(ctx, Body, invQ, VisualRadiusKm));
        mat.SetShaderParameter(ShaderLib.Time, time);
        mat.SetShaderParameter(GranPhase, (float)granPhase);
        mat.SetShaderParameter(SuperPhase, (float)superPhase);
    }

    /// <summary>
    /// Advance the granule and supergranule clocks from sim time (so a paused sim has a frozen surface and rewind runs it
    /// backwards), compressed by <see cref="SurfaceCompression"/> and rate-capped against the wall clock.
    /// </summary>
    void AdvanceSurface(ViewContext ctx)
    {
        if (!double.IsNaN(lastSimTime))
        {
            var dSim = ctx.SimTime - lastSimTime;
            var dReal = Math.Max(ctx.RealTime - lastRealTime, 0);
            var capG = dReal / MinGranuleLifetimeReal;
            var capS = dReal / MinSupergranuleLifetimeReal;
            granPhase = Wrap(granPhase + Math.Clamp(dSim * SurfaceCompression / GranuleLifetime, -capG, capG));
            superPhase = Wrap(superPhase + Math.Clamp(dSim * SurfaceCompression / SupergranuleLifetime, -capS, capS));
        }
        lastSimTime = ctx.SimTime;
        lastRealTime = ctx.RealTime;
    }

    static double Wrap(double p) => p - Math.Floor(p);

    static double Peak(Color c) => Math.Max(c.R, Math.Max(c.G, c.B));

    static double? ParseTempOverride()
    {
        var args = OS.GetCmdlineUserArgs();
        for (var i = 0; i + 1 < args.Length; i++)
            if (args[i] == "--star-temp" && double.TryParse(args[i + 1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var t) && t > 0)
                return t;
        return null;
    }
}
