using System;
using Godot;
using Spayly.Sim;

namespace Spayly.Game.Render;

/// <summary>
/// A thin accretion disc between the ISCO and <see cref="BlackHoleOptions.DiscOuterRs"/>, drawn flat (disc.gdshader) when
/// lensing is off. With lensing on the mesh hides and <see cref="LensingPass"/> ray-traces the same disc through the
/// deflection tables, with the same temperature profile, Doppler factor, colour table and turbulence.
///
/// Colour: <see cref="RenderUnits.Blackbody"/> through a log-temperature table (Spayly.Sim.Photometry does not exist in this
/// tree). [SCI] Shakura–Sunyaev T(r) and bolometric (T/T_peak)⁴; with Doppler on, the circular-emitter g shifts colour by g
/// and brightness by g⁴. [GAME] The profile is shown scaled to the hole's display peak temperature (App.HoleDisc.cs, at most
/// <see cref="BlackHoleOptions.DiscPeakDisplayK"/> at Eddington): a stellar-mass disc is ~10⁷ K, X-ray white. Brightness
/// comes from the hole's own accretion luminosity. [ART] Keplerian-advected turbulence.
/// </summary>
public partial class AccretionDisc : Node3D
{
    /// <summary>Entries in the colour table, log-spaced over <see cref="LutMinK"/>..<see cref="LutMaxK"/>.</summary>
    public const int LutSize = 256;
    public const double LutMinK = 1000, LutMaxK = 40000;

    static readonly StringName ColorLut = "color_lut", ViewPos = "view_pos", RIn = "r_in", ROut = "r_out", PeakK = "peak_k",
        Intensity = "intensity", Doppler = "doppler", Time = "time";
    static ImageTexture? lutTexture;

    readonly ShaderMaterial? mat;
    readonly MeshInstance3D? mesh;

    public AccretionDisc() { }

    public AccretionDisc(BlackHoleOptions options)
    {
        Name = "Disc";
        mat = new ShaderMaterial { Shader = GD.Load<Shader>("res://Shaders/disc.gdshader") };
        mat.SetShaderParameter(ColorLut, LutTexture);
        mat.SetShaderParameter(RIn, (float)options.DiscInnerRs);
        mat.SetShaderParameter(ROut, (float)options.DiscOuterRs);
        mesh = new MeshInstance3D
        {
            Mesh = Meshes.Ring(options.DiscInnerRs, options.DiscOuterRs), MaterialOverride = mat,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, Name = "DiscMesh",
        };
        AddChild(mesh);
        Visible = false;
    }

    /// <summary>
    /// Linear RGB (brightest channel 1) at temperatures log-spaced from 1000 K to 40000 K, RGBA float32 in a row: the shaders'
    /// blackbody() looks it up by ln(T/1000)/ln(40).
    /// </summary>
    public static float[] BuildColorLut()
    {
        var lut = new float[LutSize * 4];
        for (var i = 0; i < LutSize; i++)
        {
            var t = LutMinK * Math.Pow(LutMaxK / LutMinK, (double)i / (LutSize - 1));
            var c = RenderUnits.Blackbody(t);
            lut[i * 4] = c.R;
            lut[i * 4 + 1] = c.G;
            lut[i * 4 + 2] = c.B;
            lut[i * 4 + 3] = 1;
        }
        return lut;
    }

    /// <summary>The colour table as a texture for disc.gdshader.</summary>
    public static ImageTexture LutTexture
    {
        get
        {
            if (lutTexture != null) return lutTexture;
            var lut = BuildColorLut();
            var bytes = new byte[lut.Length * 4];
            Buffer.BlockCopy(lut, 0, bytes, 0, bytes.Length);
            lutTexture = ImageTexture.CreateFromImage(Image.CreateFromData(LutSize, 1, false, Image.Format.Rgbaf, bytes));
            return lutTexture;
        }
    }

    /// <summary>The disc's pole and first in-plane axis in the render frame, from its pole in the sim frame.</summary>
    public static (Vector3 Normal, Vector3 X) Axes(Vec3 poleSim)
    {
        var n = RenderUnits.DirToRender(Vec3.Normalize(poleSim)).Normalized();
        var x = RenderUnits.DirToRender(Vec3.AnyPerpendicular(Vec3.Normalize(poleSim))).Normalized();
        return (n, x);
    }

    /// <summary>
    /// Orient and size the flat disc on the hole's node (which sits at the squeezed position), rs in render units, k the
    /// squeeze factor; <paramref name="trueDistance"/> is the unsqueezed camera distance, render units.
    /// </summary>
    public void Update(ViewContext ctx, Vector3 dirToHole, double trueDistance, double rsUnits, double k, Vector3 n, Vector3 x,
        BlackHoleOptions options, double brightness, double peakK, bool visible)
    {
        if (mat == null) return;
        Visible = visible;
        if (!visible) return;
        var z = x.Cross(n);
        var s = (float)(rsUnits * k);
        Basis = new Basis(x, n, z) * Basis.FromScale(new Vector3(s, s, s));
        // The camera in the disc frame (pole +y, the mesh's), in r_s, for the Doppler factor.
        var toCam = -dirToHole * (float)(trueDistance / Math.Max(rsUnits, 1e-30));
        mat.SetShaderParameter(ViewPos, new Vector3(toCam.Dot(x), toCam.Dot(n), toCam.Dot(z)));
        mat.SetShaderParameter(Intensity, (float)(brightness * ctx.ExposureComp));
        mat.SetShaderParameter(PeakK, (float)peakK);
        mat.SetShaderParameter(Doppler, options.Doppler ? 1f : 0f);
        mat.SetShaderParameter(Time, (float)(ctx.RealTime % 3600));
    }
}
