using System;
using Godot;
using Spayly.Sim;

namespace Spayly.Game.Render;

/// <summary>
/// Star colour lookup: 256 linear-sRGB texels, log-spaced over 1000–40000 K, computed once from
/// <see cref="Photometry.PlanckToLinearSrgb"/> (Planck × CIE 1931 → sRGB D65, luminance 1), so every texel carries hue
/// only. Shaders sample it through Shaders/star.gdshaderinc; CPU code reads the same table through <see cref="Linear"/>.
///
/// Still on the web's curve fit (<c>RenderUnits.Blackbody</c>), outside this file's reach: the Sun's light on planets
/// (App.Sun.cs → ctx.SunColor) and the sky's point stars (Sky.cs). Both can switch to <see cref="MaxNormalised"/>, a drop-in
/// with the same brightest-channel-1 convention.
/// </summary>
public static class StarColor
{
    static StarColor() => Realism.RegisterAll(RealismNotes, RealismArea.Rendering);

    public const int Size = 256;
    /// <summary>K.</summary>
    public const double MinK = 1000, MaxK = 40000;

    static float[]? table;
    static ImageTexture? lut;

    /// <summary>Realism tag of every choice in the star shaders, for the Realism tooltip: SCI physics, GAME a rule chosen for play, ART a look.</summary>
    public static readonly (string Feature, string Tag, string Why)[] RealismNotes =
    [
        ("Star colour", "SCI", "Planck spectrum integrated against the CIE 1931 2° observer, converted to sRGB (D65); the Sun is 5772 K."),
        ("Sun looks white", "GAME", "Brightness is exposure-compensated so the disc saturates to near-white after tone mapping, as the eye sees it, instead of following the auto-exposure."),
        ("Star brightness", "GAME", "Every star's disc is drawn with its brightest colour channel at the Sun's level, whatever its luminosity, so cool stars stay orange and hot ones blue rather than clipping to white."),
        ("Close-up dimming", "GAME", "Like a solar filter, the disc dims as it fills the view so granulation is not lost to overexposure."),
        ("Limb darkening", "SCI", "Quadratic law per colour channel from a grey (Eddington) atmosphere: the edge is dimmer and redder, more so for cool stars."),
        ("Granules", "SCI", "Cells about 1000 km across with bright hot centres and dark lanes, ~15 % contrast; only resolved when you fly close."),
        ("Granule lifetime", "ART", "Real granules live about 8 minutes; compressed 20× so they visibly boil at normal speed, and capped at high warp so they never flicker."),
        ("Supergranules", "SCI", "A network about 30000 km across that brightens toward the limb (faculae)."),
        ("Supergranule contrast", "GAME", "Raised well above the real sub-percent white-light contrast so the network is visible at all."),
        ("Surface mottling", "ART", "The web build's fbm noise, kept under the physical cells so both builds look alike from afar."),
        ("Sunspots", "GAME", "A threshold on low-frequency noise, not magnetic flux, with a penumbra ring and a darker umbra."),
        ("Spot temperature", "SCI", "Umbrae are ~1700 K cooler than the Sun's surface and only ~200 K cooler on red dwarfs (Berdyugina 2005), so spots redden and cool stars' spots are shallow."),
        ("Spot count", "GAME", "More spots on cool stars, none above ~7000 K (no deep convection zone, no dynamo), fewer on large stars."),
        ("Surface map", "ART", "The texture map adds structure only; its false colour is discarded in favour of the Planck colour."),
        ("Corona colour", "SCI", "The white-light corona is sunlight scattered by electrons, which is grey, so it takes the star's colour."),
        ("Corona shape", "ART", "Rays and streaks are noise, not magnetic field lines, and the corona is far brighter than the real one, which only shows in an eclipse."),
    ];

    /// <summary>The LUT texture (RGB float, <see cref="Size"/> × 1); bind to <c>star_lut</c>.</summary>
    public static ImageTexture Lut => lut ??= BuildTexture();

    /// <summary>Texture coordinate of a temperature (K) at texel centres, matching star_rgb() in star.gdshaderinc.</summary>
    public static float LutU(double tempK) => (float)((0.5 + (Size - 1) * T01(tempK)) / Size);

    /// <summary>Linear sRGB of a temperature (K) with luminance 1, interpolated from the LUT exactly as the GPU does.</summary>
    public static Color Linear(double tempK)
    {
        var t = Table();
        var f = T01(tempK) * (Size - 1);
        var i = Math.Min((int)f, Size - 2);
        var w = (float)(f - i);
        return new Color(
            t[3 * i] + (t[3 * i + 3] - t[3 * i]) * w,
            t[3 * i + 1] + (t[3 * i + 4] - t[3 * i + 1]) * w,
            t[3 * i + 2] + (t[3 * i + 5] - t[3 * i + 2]) * w);
    }

    /// <summary>As <see cref="Linear"/>, scaled so the brightest channel is 1 (the convention of RenderUnits.Blackbody).</summary>
    public static Color MaxNormalised(double tempK)
    {
        var c = Linear(tempK);
        var m = Math.Max(c.R, Math.Max(c.G, c.B));
        return m > 0 ? new Color(c.R / m, c.G / m, c.B / m) : c;
    }

    static double T01(double tempK) => Math.Clamp(Math.Log(Math.Max(tempK, 1) / MinK) / Math.Log(MaxK / MinK), 0, 1);

    static float[] Table()
    {
        if (table != null) return table;
        var t = new float[3 * Size];
        for (var i = 0; i < Size; i++)
        {
            var (r, g, b) = Photometry.PlanckToLinearSrgb(MinK * Math.Pow(MaxK / MinK, i / (double)(Size - 1)));
            t[3 * i] = (float)r;
            t[3 * i + 1] = (float)g;
            t[3 * i + 2] = (float)b;
        }
        return table = t;
    }

    static ImageTexture BuildTexture()
    {
        var t = Table();
        var bytes = new byte[t.Length * sizeof(float)];
        Buffer.BlockCopy(t, 0, bytes, 0, bytes.Length);
        return ImageTexture.CreateFromImage(Image.CreateFromData(Size, 1, false, Image.Format.Rgbf, bytes));
    }
}
