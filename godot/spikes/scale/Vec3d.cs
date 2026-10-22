namespace Spayly.Spikes.Scale;

/// <summary>
/// Double-precision 3-vector for the scale spike's own orbit math and floating-origin arithmetic.
/// Local to this spike per the M1c task (not added to Spayly.Sim).
/// </summary>
public readonly struct Vec3d
{
    public readonly double X, Y, Z;
    public Vec3d(double x, double y, double z) { X = x; Y = y; Z = z; }
    public static readonly Vec3d Zero = new(0, 0, 0);

    public static Vec3d operator +(Vec3d a, Vec3d b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static Vec3d operator -(Vec3d a, Vec3d b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static Vec3d operator -(Vec3d a) => new(-a.X, -a.Y, -a.Z);
    public static Vec3d operator *(Vec3d a, double s) => new(a.X * s, a.Y * s, a.Z * s);
    public static Vec3d operator *(double s, Vec3d a) => a * s;
    public static Vec3d operator /(Vec3d a, double s) => new(a.X / s, a.Y / s, a.Z / s);

    public double Dot(Vec3d o) => X * o.X + Y * o.Y + Z * o.Z;
    public Vec3d Cross(Vec3d o) => new(Y * o.Z - Z * o.Y, Z * o.X - X * o.Z, X * o.Y - Y * o.X);
    public double Length() => System.Math.Sqrt(Dot(this));
    public Vec3d Normalized()
    {
        var len = Length();
        return len > 1e-300 ? this / len : new Vec3d(0, 0, 1);
    }

    /// <summary>Cast to a render-space float vector: caller has already subtracted the camera's double
    /// position (or not, for the --no-floating-origin control) and divided by RENDER_SCALE.</summary>
    public Godot.Vector3 ToVector3() => new((float)X, (float)Y, (float)Z);
}
