namespace Spayly.Sim;

/// <summary>Right-handed basis, in ecliptic coordinates, of a reference plane.</summary>
public readonly record struct PlaneBasis(Vec3 X, Vec3 Y, Vec3 Z);

/// <summary>Reference-frame conversions; mirrors src/sim/frames.ts. Ecliptic J2000, +z ecliptic north.</summary>
public static class Frames
{
    private static readonly double CosEps = Math.Cos(Constants.Obliquity);
    private static readonly double SinEps = Math.Sin(Constants.Obliquity);

    /// <summary>ICRF equatorial J2000 to ecliptic J2000.</summary>
    public static Vec3 EquatorialToEcliptic(Vec3 v) => new(
        v.X,
        v.Y * CosEps + v.Z * SinEps,
        -v.Y * SinEps + v.Z * CosEps);

    /// <summary>Unit vector of a pole given as ICRF right ascension and declination (degrees), in ecliptic coordinates.</summary>
    public static Vec3 PoleToEcliptic(double raDeg, double decDeg)
    {
        var ra = raDeg * Constants.Deg;
        var dec = decDeg * Constants.Deg;
        var cd = Math.Cos(dec);
        return Vec3.Normalize(EquatorialToEcliptic(new Vec3(cd * Math.Cos(ra), cd * Math.Sin(ra), Math.Sin(dec))));
    }

    /// <summary>Ecliptic J2000 back to ICRF equatorial J2000.</summary>
    public static Vec3 EclipticToEquatorial(Vec3 v) => new(
        v.X,
        v.Y * CosEps - v.Z * SinEps,
        v.Y * SinEps + v.Z * CosEps);

    /// <summary>An ecliptic direction as ICRF right ascension (degrees, [0, 360)) and declination (degrees).</summary>
    public static (double PoleRA, double PoleDec) EclipticToPole(Vec3 v)
    {
        var e = EclipticToEquatorial(Vec3.Normalize(v));
        var ra = Math.Atan2(e.Y, e.X) / Constants.Deg;
        return ((ra + 360) % 360, Math.Asin(Math.Max(-1, Math.Min(1, e.Z))) / Constants.Deg);
    }

    // Declared after CosEps/SinEps on purpose: static initialisers run in textual order.
    private static readonly Vec3 IcrfNorth = EquatorialToEcliptic(new Vec3(0, 0, 1));

    /// <summary>
    /// Basis of the plane normal to <paramref name="pole"/>. x lies along that plane's ascending node on the ICRF
    /// equator, which is the direction JPL measures satellite nodes from; z is the pole.
    /// </summary>
    public static PlaneBasis PlaneBasis(Vec3 pole)
    {
        var x = Vec3.Cross(IcrfNorth, pole);
        if (Vec3.Length(x) < 1e-9) x = Vec3.AnyPerpendicular(pole);
        x = Vec3.Normalize(x);
        return new PlaneBasis(x, Vec3.Cross(pole, x), pole);
    }

    /// <summary>A vector expressed in <paramref name="b"/> back into ecliptic coordinates.</summary>
    public static Vec3 FromBasis(PlaneBasis b, Vec3 v) => new(
        b.X.X * v.X + b.Y.X * v.Y + b.Z.X * v.Z,
        b.X.Y * v.X + b.Y.Y * v.Y + b.Z.Y * v.Z,
        b.X.Z * v.X + b.Y.Z * v.Y + b.Z.Z * v.Z);

    /// <summary>An ecliptic vector expressed in <paramref name="b"/>.</summary>
    public static Vec3 ToBasis(PlaneBasis b, Vec3 v) => new(
        b.X.X * v.X + b.X.Y * v.Y + b.X.Z * v.Z,
        b.Y.X * v.X + b.Y.Y * v.Y + b.Y.Z * v.Z,
        b.Z.X * v.X + b.Z.Y * v.Y + b.Z.Z * v.Z);
}
