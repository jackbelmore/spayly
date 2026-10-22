using System;
using System.Collections.Generic;
using Godot;
using Spayly.Sim;

namespace Spayly.Game.Render;

/// <summary>A camera goal; null members keep the current value. Port of ViewSpec in src/render/camera.ts.</summary>
public readonly record struct ViewSpec
{
    /// <summary>Body to follow. Leave <see cref="SetFocus"/> false to keep the current one.</summary>
    public Body? Focus { get; init; }
    public bool SetFocus { get; init; }
    /// <summary>Fixed target point (km) when not following a body; detaches the focus.</summary>
    public Vec3? Target { get; init; }
    /// <summary>km.</summary>
    public double? Distance { get; init; }
    /// <summary>rad.</summary>
    public double? Yaw { get; init; }
    /// <summary>rad.</summary>
    public double? Pitch { get; init; }
}

/// <summary>
/// Orbit camera in sim coordinates (km, double); port of src/render/camera.ts. The Godot camera stays at the render origin
/// and only rotates; everything else is placed relative to <see cref="Position"/> (floating origin). Zoom is multiplicative,
/// so one wheel notch feels the same at 10 km and 10 AU. Near/far are set per frame from what is on screen, with the far
/// plane never more than 1e6 × near (spike M1c: a larger ratio blacks out the frame).
/// </summary>
public sealed class CameraRig
{
    /// <summary>Largest far/near ratio; 1.3e7 renders and 3.9e7 fails outright on this GPU (M1c), so an order of margin.</summary>
    public const double MaxDepthRatio = 1e6;
    /// <summary>Squeeze starts this far inside the far plane (ratio to near), which leaves 10× headroom for everything squeezed.</summary>
    public const double SqueezeRatio = 1e5;

    readonly struct Flight
    {
        public Vec3 FromTarget { get; init; }
        public Vec3? ToTarget { get; init; }
        public double FromDist { get; init; }
        public double ToDist { get; init; }
        public double FromYaw { get; init; }
        public double ToYaw { get; init; }
        public double FromPitch { get; init; }
        public double ToPitch { get; init; }
        public double Dur { get; init; }
    }

    public readonly Camera3D Camera;
    public Body? Focus;
    /// <summary>Look-at point, km.</summary>
    public Vec3 Target;
    /// <summary>km.</summary>
    public double Distance = 3 * Constants.AU;
    public double Yaw = 0.7;
    public double Pitch = 0.45;
    public double GoalDistance;
    public double GoalYaw;
    public double GoalPitch;
    /// <summary>Camera position, km, sim frame.</summary>
    public Vec3 Position;
    /// <summary>Offset of the look-at point from the followed body, so panning does not break the follow; km.</summary>
    public Vec3 TargetOffset;
    /// <summary>Floor on the orbit distance when nothing is focused, km; a focused body's surface overrides it.</summary>
    public double MinDistanceKm = 0.01;
    public double MaxDistanceKm = 600 * Constants.AU;
    /// <summary>Radius the camera stops at around the focus, km: the app sets it from SolidRadius, never VisualRadius.</summary>
    public double FocusRadiusKm;
    /// <summary>Degrees. Past the surface stop, zooming narrows the lens instead of closing the distance.</summary>
    public const double WideFov = 50;
    public const double NarrowFov = 6;
    public double Fov = WideFov;
    public double GoalFov = WideFov;
    /// <summary>This frame's near plane, render units.</summary>
    public double Near { get; private set; } = 0.05;
    public double Far { get; private set; } = 4000;

    Flight? flight;
    double flightT;

    public CameraRig(Camera3D camera)
    {
        Camera = camera;
        GoalDistance = Distance;
        GoalYaw = Yaw;
        GoalPitch = Pitch;
        camera.Fov = (float)Fov;
    }

    public bool Flying => flight.HasValue;

    static double Clamp(double x, double lo, double hi) => Math.Min(hi, Math.Max(lo, x));
    static double Smooth(double x)
    {
        var t = Clamp(x, 0, 1);
        return t * t * (3 - 2 * t);
    }
    static double WrapAngle(double a) => Math.Atan2(Math.Sin(a), Math.Cos(a));

    /// <summary>Orbit by a mouse drag, px.</summary>
    public void Orbit(double dx, double dy) => RotateBy(-dx * 0.0055, dy * 0.0055);

    /// <summary>Swing the camera around the target, rad.</summary>
    public void RotateBy(double dYaw, double dPitch)
    {
        if (flight.HasValue) return;
        GoalYaw += dYaw;
        GoalPitch = Clamp(GoalPitch + dPitch, -1.53, 1.53);
    }

    /// <summary>Distance the camera may not close past, km: a focused body's surface, otherwise the global floor.</summary>
    double Floor() => Focus != null ? Math.Max(FocusRadiusKm, 1e-6) : MinDistanceKm;

    /// <summary>Multiplicative zoom; factor &lt; 1 closes in. At the surface stop it narrows the lens instead.</summary>
    public void Zoom(double factor)
    {
        if (flight.HasValue) return;
        var grounded = GoalDistance <= Floor() * 1.002;
        if (factor < 1 && grounded)
        {
            GoalFov = Clamp(GoalFov * factor, NarrowFov, WideFov);
            return;
        }
        if (factor > 1 && GoalFov < WideFov)
        {
            GoalFov = Clamp(GoalFov * factor, NarrowFov, WideFov);
            if (GoalFov < WideFov) return;
        }
        GoalDistance = Clamp(GoalDistance * factor, MinDistanceKm, MaxDistanceKm);
    }

    public bool ZoomedIn => GoalFov < WideFov - 0.01;

    /// <summary>Slide the look-at point by a mouse drag, px: the world follows the pointer.</summary>
    public void Pan(double dx, double dy)
    {
        var k = Distance * 0.0016;
        PanBy(-dx * k, dy * k);
    }

    /// <summary>Slide the look-at point across the view plane, km. While following, the slide is an offset so the follow survives.</summary>
    public void PanBy(double right, double up)
    {
        if (flight.HasValue) return;
        var b = Camera.Basis;
        Vector3 r = b.X, u = b.Y;
        var shift = new Vec3(r.X * right + u.X * up, -(r.Z * right + u.Z * up), r.Y * right + u.Y * up);
        if (Focus != null) TargetOffset += shift;
        else Target += shift;
    }

    public void FlyTo(ViewSpec spec, double duration = 1.5)
    {
        var fromTarget = Target;
        TargetOffset = Vec3.Zero;
        GoalFov = WideFov;
        if (spec.SetFocus) Focus = spec.Focus;
        if (spec.Target.HasValue) Focus = null;
        var toYaw = spec.Yaw ?? GoalYaw;
        flight = new Flight
        {
            FromTarget = fromTarget,
            ToTarget = spec.Target,
            FromDist = Distance,
            ToDist = Clamp(spec.Distance ?? GoalDistance, MinDistanceKm, MaxDistanceKm),
            FromYaw = Yaw,
            ToYaw = Yaw + WrapAngle(toYaw - Yaw),
            FromPitch = Pitch,
            ToPitch = Clamp(spec.Pitch ?? GoalPitch, -1.53, 1.53),
            Dur = duration,
        };
        flightT = 0;
    }

    /// <summary>Jump instantly.</summary>
    public void Set(ViewSpec spec)
    {
        TargetOffset = Vec3.Zero;
        GoalFov = WideFov;
        if (spec.SetFocus) Focus = spec.Focus;
        if (spec.Target is Vec3 t) { Target = t; Focus = null; }
        if (spec.Distance is double d) Distance = GoalDistance = Clamp(d, MinDistanceKm, MaxDistanceKm);
        if (spec.Yaw is double y) Yaw = GoalYaw = y;
        if (spec.Pitch is double p) Pitch = GoalPitch = p;
        flight = null;
    }

    /// <summary>Advance flights and easing by dt seconds, then aim the Godot camera. Near/far are set separately by <see cref="SetDepthRange"/>.</summary>
    public void Update(double dt)
    {
        if (flight is Flight f)
        {
            flightT += dt;
            var s = Smooth(flightT / f.Dur);
            var dest = Focus != null ? Focus.Pos : (f.ToTarget ?? f.FromTarget);
            Target = Vec3.Lerp(f.FromTarget, dest, s);
            Distance = Math.Exp(Math.Log(f.FromDist) + (Math.Log(f.ToDist) - Math.Log(f.FromDist)) * s);
            Yaw = f.FromYaw + (f.ToYaw - f.FromYaw) * s;
            Pitch = f.FromPitch + (f.ToPitch - f.FromPitch) * s;
            if (flightT >= f.Dur)
            {
                flight = null;
                GoalDistance = Distance;
                GoalYaw = Yaw;
                GoalPitch = Pitch;
            }
        }
        else
        {
            if (Focus != null) Target = Focus.Pos + TargetOffset;
            var k = 1 - Math.Exp(-dt * 9);
            Distance = Math.Exp(Math.Log(Distance) + (Math.Log(GoalDistance) - Math.Log(Distance)) * k);
            Yaw += (GoalYaw - Yaw) * k;
            Pitch += (GoalPitch - Pitch) * k;
        }
        var kf = 1 - Math.Exp(-dt * 9);
        Fov += (GoalFov - Fov) * kf;
        if (Math.Abs(Camera.Fov - Fov) > 1e-4) Camera.Fov = (float)Fov;
        // A 19 m Chelyabinsk rock wants the camera 40 m away; only its own surface should stop us.
        var minD = Focus != null ? Math.Max(FocusRadiusKm, 1e-6) : MinDistanceKm;
        if (Distance < minD)
        {
            Distance = minD;
            if (GoalDistance < minD) GoalDistance = minD;
        }
        var cp = Math.Cos(Pitch);
        var ox = cp * Math.Sin(Yaw) * Distance;
        var oy = Math.Sin(Pitch) * Distance;
        var oz = cp * Math.Cos(Yaw) * Distance;
        // (ox, oy, oz) is the render-frame offset from target to camera; the sim frame is (x, −z, y).
        Position = new Vec3(Target.X + ox, Target.Y - oz, Target.Z + oy);
        Camera.Position = Vector3.Zero;
        var look = new Vector3((float)-ox, (float)-oy, (float)-oz).Normalized();
        Camera.Basis = Basis.LookingAt(look, Vector3.Up);
    }

    /// <summary>
    /// Near plane at half the nearest visible surface, far at <see cref="MaxDepthRatio"/> × near, and the squeeze start
    /// between them. <paramref name="nearestSurfaceUnits"/> is the smallest camera-to-surface distance of anything in
    /// front of the camera, render units (infinity when nothing is).
    /// </summary>
    public void SetDepthRange(double nearestSurfaceUnits, FloatingOrigin origin)
    {
        var near = double.IsFinite(nearestSurfaceUnits) ? Clamp(0.5 * nearestSurfaceUnits, 1e-7, 1e6) : 0.05;
        Near = near;
        Far = near * MaxDepthRatio;
        origin.SqueezeStart = origin.SqueezeEnabled ? near * SqueezeRatio : double.PositiveInfinity;
        Camera.Near = (float)Near;
        Camera.Far = (float)Far;
    }

    /// <summary>Viewport pixels per radian of vertical field.</summary>
    public double PixelsPerRadian(double viewportHeight) => viewportHeight / (2 * Math.Tan(Fov * Math.PI / 360));

    /// <summary>Unit ray direction in the sim frame through a viewport pixel.</summary>
    public Vec3 RayDir(Vector2 screen)
    {
        var v = Camera.ProjectRayNormal(screen);
        return Vec3.Normalize(new Vec3(v.X, -v.Z, v.Y));
    }

    /// <summary>Unit ray direction in the sim frame through normalised device coordinates (x right, y up, −1..1).</summary>
    public Vec3 RayDir(double ndcX, double ndcY)
    {
        var size = Camera.GetViewport().GetVisibleRect().Size;
        return RayDir(new Vector2((float)((ndcX + 1) / 2 * size.X), (float)((1 - ndcY) / 2 * size.Y)));
    }

    /// <summary>Yaw that places the camera on the side of the target facing <paramref name="dirSim"/> (e.g. towards the Sun).</summary>
    public static double YawTowards(Vec3 dirSim) => Math.Atan2(dirSim.X, -dirSim.Y);
}
