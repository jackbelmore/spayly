using System;
using Godot;
using Spayly.Sim;

namespace Spayly.Game.Render;

/// <summary>
/// Units and axes shared by everything that draws; mirrors src/render/units.ts. 1 render unit = 1000 km, and the sim's
/// ecliptic z-up frame becomes Godot's y-up frame as (x, y, z) → (x, z, −y). The mapping is a proper rotation (−90° about
/// x), so handedness and cross products survive it.
/// </summary>
public static class RenderUnits
{
    /// <summary>Render units per km.</summary>
    public const double Scale = 1e-3;

    /// <summary>Camera-relative render position of a sim point (km). The subtraction happens in double; only the offset is narrowed.</summary>
    public static Vector3 ToRender(Vec3 v, Vec3 origin) =>
        new((float)((v.X - origin.X) * Scale), (float)((v.Z - origin.Z) * Scale), (float)(-(v.Y - origin.Y) * Scale));

    /// <summary>A sim-frame direction in the render frame (no scale, no origin).</summary>
    public static Vector3 DirToRender(Vec3 v) => new((float)v.X, (float)v.Z, (float)-v.Y);

    /// <summary>A camera-relative render position back to a sim point, km.</summary>
    public static Vec3 RenderToSim(Vector3 v, Vec3 origin) => new(origin.X + v.X / Scale, origin.Y - v.Z / Scale, origin.Z + v.Y / Scale);

    /// <summary>A render-frame direction in the sim frame.</summary>
    public static Vec3 RenderDirToSim(Vector3 v) => new(v.X, -v.Z, v.Y);

    /// <summary>
    /// Approximate blackbody colour of a temperature in K, as linear RGB normalised so the brightest channel is 1 (the web
    /// build's numbers). Godot's material and light colours are sRGB, so callers hand it over through <c>LinearToSrgb()</c>.
    /// </summary>
    public static Color Blackbody(double tempK)
    {
        var t = Math.Min(Math.Max(tempK, 1000), 40000) / 100;
        double r, g, b;
        if (t <= 66)
        {
            r = 255;
            g = 99.4708025861 * Math.Log(t) - 161.1195681661;
            b = t <= 19 ? 0 : 138.5177312231 * Math.Log(t - 10) - 305.0447927307;
        }
        else
        {
            r = 329.698727446 * Math.Pow(t - 60, -0.1332047592);
            g = 288.1221695283 * Math.Pow(t - 60, -0.0755148492);
            b = 255;
        }
        var lr = SrgbToLinear(Clamp01(r / 255));
        var lg = SrgbToLinear(Clamp01(g / 255));
        var lb = SrgbToLinear(Clamp01(b / 255));
        var m = Math.Max(lr, Math.Max(lg, lb));
        if (m <= 0) m = 1;
        return new Color((float)(lr / m), (float)(lg / m), (float)(lb / m));
    }

    // three.js Color.convertSRGBToLinear, which the web applies before normalising.
    static double SrgbToLinear(double c) => c < 0.04045 ? c * 0.0773993808 : Math.Pow(c * 0.9478672986 + 0.0521327014, 2.4);

    static double Clamp01(double x) => Math.Min(1, Math.Max(0, x));
}
