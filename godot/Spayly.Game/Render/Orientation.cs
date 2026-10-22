using System;
using System.Collections.Generic;
using Godot;
using Spayly.Sim;

namespace Spayly.Game.Render;

/// <summary>
/// Body orientation; port of src/render/orientation.ts. Worked in double quaternions so the rotation angle of a body
/// spinning for centuries of Julian days keeps its precision, then narrowed once for Godot.
///
/// Frames: the body's geometry frame is three.js SphereGeometry's (pole +y, prime meridian +x), the frame craters, shaders
/// and <see cref="BodyDirToUv"/> agree on. Godot's SphereMesh lays its UVs out differently, so texture work must compute its
/// UV from the geometry-frame direction (as the web shaders do) rather than use the mesh's.
/// </summary>
public static class Orientation
{
    /// <summary>Unit quaternion in double. Hamilton product, same composition order as three.js and Godot (a * b applies b first).</summary>
    public readonly record struct DQuat(double X, double Y, double Z, double W)
    {
        public static readonly DQuat Identity = new(0, 0, 0, 1);

        /// <summary>Rotation by <paramref name="angle"/> radians about a unit axis.</summary>
        public static DQuat FromAxisAngle(double ax, double ay, double az, double angle)
        {
            var h = angle / 2;
            var s = Math.Sin(h);
            return new DQuat(ax * s, ay * s, az * s, Math.Cos(h));
        }

        public static DQuat operator *(DQuat a, DQuat b) => new(
            a.X * b.W + a.W * b.X + a.Y * b.Z - a.Z * b.Y,
            a.Y * b.W + a.W * b.Y + a.Z * b.X - a.X * b.Z,
            a.Z * b.W + a.W * b.Z + a.X * b.Y - a.Y * b.X,
            a.W * b.W - a.X * b.X - a.Y * b.Y - a.Z * b.Z);

        public DQuat Conjugate() => new(-X, -Y, -Z, W);

        /// <summary>Rotate a vector (any frame; the quaternion does not know about sim versus render axes).</summary>
        public Vec3 Rotate(Vec3 v)
        {
            // v' = v + 2w (q × v) + 2 q × (q × v)
            double tx = 2 * (Y * v.Z - Z * v.Y), ty = 2 * (Z * v.X - X * v.Z), tz = 2 * (X * v.Y - Y * v.X);
            return new Vec3(v.X + W * tx + (Y * tz - Z * ty), v.Y + W * ty + (Z * tx - X * tz), v.Z + W * tz + (X * ty - Y * tx));
        }

        public Quaternion ToGodot() => new((float)X, (float)Y, (float)Z, (float)W);
    }

    /// <summary>Simulation ecliptic frame to render frame.</summary>
    static readonly DQuat QToRender = DQuat.FromAxisAngle(1, 0, 0, -Math.PI / 2);
    /// <summary>ICRF equatorial to ecliptic.</summary>
    static readonly DQuat QEps = DQuat.FromAxisAngle(1, 0, 0, -Constants.Obliquity);
    /// <summary>Sphere geometry (pole +y, prime meridian +x) to the IAU body frame (pole +z, prime meridian +x).</summary>
    static readonly DQuat QGeom = DQuat.FromAxisAngle(1, 0, 0, Math.PI / 2);

    readonly record struct Tumble(Vec3 Axis, double Rate);

    static readonly Dictionary<string, Tumble> TumbleAxes = [];

    /// <summary>
    /// Orientation of a body's mesh in the render frame at Julian day jd (geometry frame → render frame), following the IAU
    /// convention: body-fixed to ICRF is Rz(α0 + 90°) Rx(90° − δ0) Rz(W). Bodies without a rotation model tumble about a
    /// seeded axis with a period of 2–20 hours.
    /// </summary>
    public static DQuat BodyOrientation(Body b, double jd)
    {
        var d = jd - Constants.J2000Jd;
        if (b.Rotation is Rotation rot)
        {
            // Reduce the prime-meridian angle in degrees first: at 360°/day for 1e5 days a raw radian angle loses digits.
            var wDeg = (rot.W0 + rot.Rate * d) % 360;
            return QToRender * QEps
                * DQuat.FromAxisAngle(0, 0, 1, (rot.PoleRA + 90) * Constants.Deg)
                * DQuat.FromAxisAngle(1, 0, 0, (90 - rot.PoleDec) * Constants.Deg)
                * DQuat.FromAxisAngle(0, 0, 1, wDeg * Constants.Deg)
                * QGeom;
        }
        if (!TumbleAxes.TryGetValue(b.Id, out var t))
        {
            var rand = Rng.Mulberry32(unchecked((uint)b.Seed));
            var zz = rand() * 2 - 1;
            var th = rand() * Math.PI * 2;
            var rr = Math.Sqrt(1 - zz * zz);
            t = new Tumble(new Vec3(rr * Math.Cos(th), rr * Math.Sin(th), zz), 2 * Math.PI / ((2 + rand() * 18) / 24));
            TumbleAxes[b.Id] = t;
        }
        var bornDays = b.Born / 86400;
        var angle = ((d - bornDays) * t.Rate) % (2 * Math.PI);
        return QToRender * DQuat.FromAxisAngle(t.Axis.X, t.Axis.Y, t.Axis.Z, angle);
    }

    /// <summary>Drop cached tumble axes for bodies that no longer exist (ids are reused by undo, so it is keyed by id).</summary>
    public static void Forget(string id) => TumbleAxes.Remove(id);

    /// <summary>A sim-frame direction in the body's geometry frame (what shaders and crater maps use). Injected into Collide as <c>BodyFrame</c>.</summary>
    public static Vec3 WorldToBodyFrame(Body b, Vec3 dirSim, double jd)
    {
        var q = BodyOrientation(b, jd).Conjugate();
        return q.Rotate(new Vec3(dirSim.X, dirSim.Z, -dirSim.Y));
    }

    /// <summary>A geometry-frame direction in the sim frame; the inverse of <see cref="WorldToBodyFrame"/>.</summary>
    public static Vec3 BodyFrameToWorld(Body b, Vec3 dirBody, double jd)
    {
        var r = BodyOrientation(b, jd).Rotate(dirBody);
        return new Vec3(r.X, -r.Z, r.Y);
    }

    /// <summary>Equirectangular UV of a geometry-frame direction, matching three.js SphereGeometry; u, v in [0, 1).</summary>
    public static (double U, double V) BodyDirToUv(Vec3 dir)
    {
        var len = Vec3.Length(dir);
        if (len == 0) len = 1;
        var y = dir.Y / len;
        var theta = Math.Acos(Math.Min(1, Math.Max(-1, y)));
        var phi = Math.Atan2(dir.Z, -dir.X);
        var u = ((phi / (2 * Math.PI)) % 1 + 1) % 1;
        return (u, 1 - theta / Math.PI);
    }
}
