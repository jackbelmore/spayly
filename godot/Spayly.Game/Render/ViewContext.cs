using System;
using System.Collections.Generic;
using Godot;
using Spayly.Sim;

namespace Spayly.Game.Render;

/// <summary>
/// Per-frame data every body view needs; port of the ViewContext in src/render/viewContext.ts, filled by the app at step 9
/// of the tick. Radii and distances are render units unless the name says km.
/// </summary>
public sealed class ViewContext
{
    /// <summary>Camera position (km) and the squeeze; every sim position becomes a render offset through it.</summary>
    public readonly FloatingOrigin Origin = new();
    public double Jd;
    /// <summary>s since the universe epoch.</summary>
    public double SimTime;
    /// <summary>Wall-clock seconds since start.</summary>
    public double RealTime;
    /// <summary>km.</summary>
    public Vec3 SunPos;
    public double SunRadiusKm = Constants.SunRadius;
    /// <summary>Linear RGB, brightest channel 1.</summary>
    public Color SunColor = new(1, 0.96f, 0.9f);
    /// <summary>Luminosity relative to the present Sun.</summary>
    public double SunLuminosity = 1;
    /// <summary>W, for equilibrium temperatures.</summary>
    public double SunWatts = Constants.SunLuminosity;
    /// <summary>Exponent of the irradiance falloff with distance (2 physical, 0 flat).</summary>
    public double Falloff = 0.5;
    /// <summary>Multiplier that keeps self-luminous things sane under auto-exposure (1 / exposure).</summary>
    public double ExposureComp = 1;
    public double DioramaAmount;
    public double DioramaPlanet = 800;
    public double DioramaSun = 30;
    /// <summary>px.</summary>
    public double MinPixelRadius = 3.5;
    /// <summary>Viewport pixels per radian of vertical field: height / (2 tan(fov/2)).</summary>
    public double PixelsPerRadian = 1000;
    public IReadOnlyList<Body> Bodies = [];
    /// <summary>Camera rotation, render frame (the camera sits at the render origin).</summary>
    public Quaternion CameraQuaternion = Quaternion.Identity;
    /// <summary>Widest map a body may stream to, px. Kept for the texture task; engine streaming does the rest.</summary>
    public int TextureCeiling = 8192;
    public double PixelRatio = 1;
    /// <summary>Frustum as the camera basis plus half-angle tangents, which is all <see cref="OnScreen"/> needs; no per-frame plane array.</summary>
    public Basis CameraBasis = Basis.Identity;
    public double TanHalfFovY = Math.Tan(25 * Math.PI / 180);
    public double Aspect = 16.0 / 9;
    /// <summary>Id of the body the camera follows, which gets its best map straight away.</summary>
    public string? FocusId;

    /// <summary>Whether any of a sphere (camera-relative render position and radius, units) is inside the view volume.</summary>
    public bool OnScreen(Vector3 center, double radius)
    {
        var b = CameraBasis;
        // Camera looks down −Z of its basis.
        double x = b.X.Dot(center), y = b.Y.Dot(center), z = -b.Z.Dot(center);
        if (z + radius <= 0) return false;
        var ty = TanHalfFovY;
        var tx = ty * Aspect;
        // Distance from the sphere centre to each side plane, whose inward normals are (∓1, 0, t)/√(1+t²).
        var ny = Math.Sqrt(1 + ty * ty);
        var nx = Math.Sqrt(1 + tx * tx);
        if ((tx * z - x) / nx < -radius || (tx * z + x) / nx < -radius) return false;
        if ((ty * z - y) / ny < -radius || (ty * z + y) / ny < -radius) return false;
        return true;
    }

    /// <summary>Irradiance at a body relative to Earth's present sunlight.</summary>
    public double Irradiance(Body body)
    {
        var d = Vec3.Length(body.Pos - SunPos);
        if (d <= 0) return SunLuminosity;
        return SunLuminosity * Math.Pow(Constants.AU / d, Falloff);
    }

    /// <summary>
    /// Radius the body's surface really occupies, render units: its true radius, enlarged for the diorama view (capped at a
    /// quarter of the distance to the nearest heavy neighbour so moons never swallow their planets). No legibility floor,
    /// so this is what the camera stops at.
    /// </summary>
    public double SolidRadius(Body body)
    {
        var r = body.Radius * RenderUnits.Scale;
        if (DioramaAmount <= 0) return r;
        var factor = body.Kind == BodyKind.Star ? DioramaSun : DioramaPlanet;
        var target = r * Math.Pow(factor, DioramaAmount);
        if (body.Kind != BodyKind.Star)
        {
            var nearest = double.PositiveInfinity;
            var bodies = Bodies;
            for (var i = 0; i < bodies.Count; i++)
            {
                var o = bodies[i];
                if (ReferenceEquals(o, body) || o.Mass < 1e20) continue;
                var d = Vec3.Length(o.Pos - body.Pos);
                if (d < nearest) nearest = d;
            }
            if (double.IsFinite(nearest)) target = Math.Min(target, 0.25 * nearest * RenderUnits.Scale);
        }
        return Math.Max(r, target);
    }

    /// <summary>As <see cref="SolidRadius"/>, but never smaller than <paramref name="minPixels"/> on screen at <paramref name="distUnits"/>. Cosmetic only.</summary>
    public double VisualRadius(Body body, double distUnits, double minPixels)
    {
        var r = SolidRadius(body);
        var pixels = r / Math.Max(distUnits, 1e-9) * PixelsPerRadian;
        return pixels < minPixels ? minPixels * distUnits / PixelsPerRadian : r;
    }

    /// <summary>Exponential fade of an effect that started at <paramref name="since"/> (s), with an optional rise time; port of decay().</summary>
    public static double Decay(double value, double since, double tau, double now, double rise = 0)
    {
        if (value <= 0) return 0;
        var age = Math.Max(0, now - since);
        var up = rise > 0 ? 1 - Math.Exp(-age / rise) : 1;
        return value * up * Math.Exp(-age / Math.Max(tau, 1));
    }
}
