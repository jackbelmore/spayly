namespace Spayly.Sim;

/// <summary>
/// Double-precision 3-vector; mirrors src/sim/vec.ts. A value type: where the TS mutates <c>b.pos.x</c>, C# assigns a
/// new Vec3. Units are whatever the caller's are (km, km/s in the simulation).
/// </summary>
public readonly record struct Vec3(double X, double Y, double Z)
{
    /// <summary>The TS <c>v3()</c>.</summary>
    public static readonly Vec3 Zero = new(0, 0, 0);

    public static Vec3 operator +(Vec3 a, Vec3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static Vec3 operator -(Vec3 a, Vec3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static Vec3 operator -(Vec3 a) => new(-a.X, -a.Y, -a.Z);
    /// <summary>The TS <c>scale(a, s)</c>.</summary>
    public static Vec3 operator *(Vec3 a, double s) => new(a.X * s, a.Y * s, a.Z * s);
    public static Vec3 operator *(double s, Vec3 a) => new(a.X * s, a.Y * s, a.Z * s);
    // A true division, not scale(a, 1 / s): the two differ in the last bit, so a port of `scale(a, 1 / l)` must
    // write `a * (1 / l)`, never `a / l`.
    public static Vec3 operator /(Vec3 a, double s) => new(a.X / s, a.Y / s, a.Z / s);

    public static Vec3 Add(Vec3 a, Vec3 b) => a + b;
    public static Vec3 Sub(Vec3 a, Vec3 b) => a - b;
    public static Vec3 Scale(Vec3 a, double s) => a * s;
    public static double Dot(Vec3 a, Vec3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

    public static Vec3 Cross(Vec3 a, Vec3 b) => new(
        a.Y * b.Z - a.Z * b.Y,
        a.Z * b.X - a.X * b.Z,
        a.X * b.Y - a.Y * b.X);

    public static double Length(Vec3 a) => Math.Sqrt(a.X * a.X + a.Y * a.Y + a.Z * a.Z);
    public static double Distance(Vec3 a, Vec3 b) => Length(a - b);

    /// <summary>Unit vector along a, or zero for the zero vector.</summary>
    public static Vec3 Normalize(Vec3 a)
    {
        var l = Length(a);
        return l > 0 ? a * (1 / l) : Zero;
    }

    public static Vec3 Lerp(Vec3 a, Vec3 b, double t) => new(
        a.X + (b.X - a.X) * t,
        a.Y + (b.Y - a.Y) * t,
        a.Z + (b.Z - a.Z) * t);

    /// <summary>
    /// The TS <c>clamp</c>. Not Math.Clamp: that throws when lo &gt; hi, where this returns hi as the TS does.
    /// </summary>
    public static double Clamp(double x, double lo, double hi) => Math.Min(hi, Math.Max(lo, x));

    /// <summary>Any unit vector perpendicular to a.</summary>
    public static Vec3 AnyPerpendicular(Vec3 a)
    {
        var n = Normalize(a);
        var helper = Math.Abs(n.X) < 0.9 ? new Vec3(1, 0, 0) : new Vec3(0, 1, 0);
        return Normalize(Cross(n, helper));
    }

    /// <summary>Uniformly distributed random unit vector.</summary>
    public static Vec3 RandomUnit(Func<double>? rand = null)
    {
        rand ??= Random.Shared.NextDouble;
        var z = rand() * 2 - 1;
        var t = rand() * Math.PI * 2;
        var r = Math.Sqrt(1 - z * z);
        return new Vec3(r * Math.Cos(t), r * Math.Sin(t), z);
    }
}

/// <summary>Deterministic randomness shared bit for bit with the web build.</summary>
public static class Rng
{
    /// <summary>
    /// Deterministic PRNG (mulberry32), bit-exact with makeRandom in src/sim/vec.ts. Returns values in [0, 1).
    /// JS works in int32 with Math.imul (the low 32 bits of the product) and a final <c>&gt;&gt;&gt; 0</c>; unchecked
    /// uint arithmetic produces the same bits, including the unwrapped <c>t + imul(...)</c> sum, which ^= truncates.
    /// </summary>
    public static Func<double> Mulberry32(uint seed)
    {
        var a = seed;
        return () =>
        {
            unchecked
            {
                a += 0x6d2b79f5;
                var t = a;
                t = (t ^ (t >> 15)) * (t | 1);
                t ^= t + (t ^ (t >> 7)) * (t | 61);
                return (t ^ (t >> 14)) / 4294967296.0;
            }
        };
    }
}
