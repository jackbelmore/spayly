namespace Spayly.Sim;

/// <summary>
/// Classical elements: a (km), angles in radians. M = mean anomaly.
/// <c>Omega</c> is the TS <c>Omega</c> (longitude of the ascending node, Ω); <c>ArgPeri</c> is the TS <c>omega</c>
/// (argument of periapsis, ω) — PascalCase would give both the same name.
/// </summary>
public readonly record struct Elements(double A, double E, double I, double Omega, double ArgPeri, double M);

/// <summary>Osculating elements of a relative state. <c>ArgPeri</c> is the TS <c>omega</c> (see <see cref="Elements"/>).</summary>
public readonly record struct Osculating
{
    /// <summary>km (negative if hyperbolic).</summary>
    public double A { get; init; }
    public double E { get; init; }
    /// <summary>Inclination, rad.</summary>
    public double I { get; init; }
    /// <summary>Longitude of the ascending node, rad.</summary>
    public double Omega { get; init; }
    /// <summary>Argument of periapsis, rad (the TS <c>omega</c>).</summary>
    public double ArgPeri { get; init; }
    /// <summary>True anomaly, rad.</summary>
    public double Nu { get; init; }
    /// <summary>Semi-latus rectum, km.</summary>
    public double P { get; init; }
    /// <summary>s (PositiveInfinity if unbound).</summary>
    public double Period { get; init; }
    /// <summary>Specific orbital energy, km^2/s^2.</summary>
    public double Energy { get; init; }
    /// <summary>km.</summary>
    public double Periapsis { get; init; }
    /// <summary>km (PositiveInfinity if unbound).</summary>
    public double Apoapsis { get; init; }
    /// <summary>Specific angular momentum, km^2/s.</summary>
    public Vec3 H { get; init; }
    /// <summary>Eccentricity vector (dimensionless, towards periapsis).</summary>
    public Vec3 Evec { get; init; }
}

/// <summary>Two-body orbital mechanics; mirrors src/sim/kepler.ts. km, km/s, s, rad; mu in km^3/s^2.</summary>
public static class Kepler
{
    /// <summary>Solve Kepler's equation M = E - e sin E (elliptic). Radians.</summary>
    public static double SolveKepler(double M, double e)
    {
        M = ((M + Math.PI) % (2 * Math.PI) + 2 * Math.PI) % (2 * Math.PI) - Math.PI;
        // JS `Math.sign(M || 1)`: ±0 and NaN fall back to 1. Math.Sign would throw on NaN, so the guard comes first.
        var signM = M == 0 || double.IsNaN(M) ? 1 : Math.Sign(M);
        var E = e < 0.8 ? M : Math.PI * signM;
        for (var i = 0; i < 40; i++)
        {
            var f = E - e * Math.Sin(E) - M;
            var fp = 1 - e * Math.Cos(E);
            var dE = f / fp;
            E -= dE;
            if (Math.Abs(dE) < 1e-14) break;
        }
        return E;
    }

    /// <summary>Position (km) and velocity (km/s) relative to the primary for elliptic elements. mu in km^3/s^2.</summary>
    public static (Vec3 R, Vec3 V) ElementsToState(double mu, Elements el)
    {
        var (a, e, i, Omega, omega, _) = el;
        var E = SolveKepler(el.M, e);
        var cosE = Math.Cos(E);
        var sinE = Math.Sin(E);
        var sq = Math.Sqrt(1 - e * e);
        var xp = a * (cosE - e);
        var yp = a * sq * sinE;
        var n = Math.Sqrt(mu / (a * a * a));
        var denom = 1 - e * cosE;
        var vxp = (-a * n * sinE) / denom;
        var vyp = (a * n * sq * cosE) / denom;
        double cO = Math.Cos(Omega), sO = Math.Sin(Omega);
        double ci = Math.Cos(i), si = Math.Sin(i);
        double cw = Math.Cos(omega), sw = Math.Sin(omega);
        var R11 = cO * cw - sO * sw * ci;
        var R12 = -cO * sw - sO * cw * ci;
        var R21 = sO * cw + cO * sw * ci;
        var R22 = -sO * sw + cO * cw * ci;
        var R31 = sw * si;
        var R32 = cw * si;
        return (
            new Vec3(R11 * xp + R12 * yp, R21 * xp + R22 * yp, R31 * xp + R32 * yp),
            new Vec3(R11 * vxp + R12 * vyp, R21 * vxp + R22 * vyp, R31 * vxp + R32 * vyp));
    }

    /// <summary>Osculating elements from a relative state vector: r in km, v in km/s, mu in km^3/s^2.</summary>
    public static Osculating StateToElements(double mu, Vec3 r, Vec3 v)
    {
        var rm = Vec3.Length(r);
        var v2 = Vec3.Dot(v, v);
        var h = Vec3.Cross(r, v);
        var hm = Vec3.Length(h);
        var evec = Vec3.Cross(v, h) * (1 / mu) - r * (1 / rm);
        var e = Vec3.Length(evec);
        var energy = v2 / 2 - mu / rm;
        var a = -mu / (2 * energy);
        var p = (hm * hm) / mu;
        var i = Math.Acos(Vec3.Clamp(hm > 0 ? h.Z / hm : 1, -1, 1));
        var nvec = new Vec3(-h.Y, h.X, 0);
        var nm = Vec3.Length(nvec);
        var Omega = nm > 1e-12 ? Math.Atan2(nvec.Y, nvec.X) : 0;
        double omega = 0;
        if (nm > 1e-12 && e > 1e-12)
        {
            omega = Math.Acos(Vec3.Clamp(Vec3.Dot(nvec, evec) / (nm * e), -1, 1));
            if (evec.Z < 0) omega = 2 * Math.PI - omega;
        }
        else if (e > 1e-12)
        {
            omega = Math.Atan2(evec.Y, evec.X);
        }
        double nu = 0;
        if (e > 1e-12)
        {
            nu = Math.Acos(Vec3.Clamp(Vec3.Dot(evec, r) / (e * rm), -1, 1));
            if (Vec3.Dot(r, v) < 0) nu = 2 * Math.PI - nu;
        }
        var period = e < 1 && a > 0 ? 2 * Math.PI * Math.Sqrt((a * a * a) / mu) : double.PositiveInfinity;
        return new Osculating
        {
            A = a, E = e, I = i, Omega = Omega, ArgPeri = omega, Nu = nu, P = p, Period = period, Energy = energy,
            Periapsis = p / (1 + e),
            Apoapsis = e < 1 ? p / (1 - e) : double.PositiveInfinity,
            H = h, Evec = evec,
        };
    }

    /// <summary>
    /// Sample the osculating conic through (r, v) relative to the primary into a new list of <c>count + 1</c> points
    /// relative to the primary (km). Hyperbolic paths are clipped at rMax (km). startNu and gap are radians.
    /// </summary>
    public static List<Vec3> ConicPoints(double mu, Vec3 r, Vec3 v, int count, double rMax = 1e10, double startNu = 0, double gap = 0) =>
        ConicPoints(mu, r, v, count, rMax, startNu, gap, new List<Vec3>(count + 1));

    /// <summary>
    /// Sample the osculating conic through (r, v) relative to the primary into <paramref name="output"/>, which is
    /// cleared first and returned. Points are relative to the primary (km). Hyperbolic paths are clipped at rMax.
    ///
    /// The TS passes `out` so orbit lines, which redraw every body every frame, reuse their point objects; here the
    /// points are values, so reusing the list's storage is what saves the allocation.
    /// </summary>
    public static List<Vec3> ConicPoints(double mu, Vec3 r, Vec3 v, int count, double rMax, double startNu, double gap, List<Vec3> output)
    {
        output.Clear();
        var el = StateToElements(mu, r, v);
        var e = el.E;
        var p = el.P;
        if (!(p > 0) || !double.IsFinite(p)) return output;
        var hUnit = Vec3.Normalize(el.H);
        var P = e > 1e-9 ? Vec3.Normalize(el.Evec) : Vec3.AnyPerpendicular(hUnit);
        var Q = Vec3.Normalize(Vec3.Cross(hUnit, P));
        double nu0, span;
        if (e < 1)
        {
            nu0 = startNu + gap;
            span = 2 * Math.PI - 2 * gap;
        }
        else
        {
            var cosLimit = Vec3.Clamp((p / rMax - 1) / e, -1, 1);
            var nuMax = Math.Acos(cosLimit) * 0.999;
            nu0 = -nuMax;
            span = 2 * nuMax;
        }
        output.EnsureCapacity(count + 1);
        for (var k = 0; k <= count; k++)
        {
            // JS division: k / count is never integer division.
            var nu = nu0 + ((double)k / count) * span;
            var rad = p / (1 + e * Math.Cos(nu));
            double a = rad * Math.Cos(nu), b = rad * Math.Sin(nu);
            output.Add(new Vec3(P.X * a + Q.X * b, P.Y * a + Q.Y * b, P.Z * a + Q.Z * b));
        }
        return output;
    }
}
