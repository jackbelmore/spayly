namespace Spayly.Sim.Tests;

// No vitest file covers frames.ts directly; these are round trips, which survive any retuning of the obliquity.
public class FramesTests
{
    private static readonly Vec3[] Samples =
    [
        new(1, 0, 0), new(0, 1, 0), new(0, 0, 1), new(0.3, -0.8, 0.52), new(-1.2e8, 4.5e7, -3.3e6), new(-0.1, -0.2, -0.97),
    ];

    private static void CloseVec(Vec3 expected, Vec3 actual, double tol)
    {
        var d = Vec3.Distance(expected, actual);
        Assert.True(d <= tol, $"expected {actual} within {tol} of {expected} (distance {d})");
    }

    [Fact]
    public void EquatorialEclipticRoundTrip()
    {
        foreach (var v in Samples)
        {
            var tol = 1e-12 * Math.Max(1, Vec3.Length(v));
            CloseVec(v, Frames.EclipticToEquatorial(Frames.EquatorialToEcliptic(v)), tol);
            CloseVec(v, Frames.EquatorialToEcliptic(Frames.EclipticToEquatorial(v)), tol);
            Close.To(Vec3.Length(v) / Math.Max(1, Vec3.Length(v)), Vec3.Length(Frames.EquatorialToEcliptic(v)) / Math.Max(1, Vec3.Length(v)), 12);
        }
    }

    [Fact]
    public void IcrfNorthTiltsByTheObliquity()
    {
        var n = Frames.EquatorialToEcliptic(new Vec3(0, 0, 1));
        Close.To(Math.Cos(Constants.Obliquity), Vec3.Dot(n, new Vec3(0, 0, 1)), 12);
        Close.To(0, n.X, 12);
    }

    [Fact]
    public void PoleEclipticRoundTrip()
    {
        foreach (var (ra, dec) in new[] { (0.0, 0.0), (286.13, 63.87), (40.589, -89.9), (257.311, -15.175), (359.5, 10.0), (123.4, 45.6) })
        {
            var p = Frames.PoleToEcliptic(ra, dec);
            Close.To(1, Vec3.Length(p), 12);
            var (ra2, dec2) = Frames.EclipticToPole(p);
            // Near a pole RA is ill-conditioned; compare the directions instead of the angles.
            CloseVec(p, Frames.PoleToEcliptic(ra2, dec2), 1e-12);
            Close.To(dec, dec2, 9);
            if (Math.Abs(dec) < 89) Close.To(0, Math.IEEERemainder(ra - ra2, 360), 9);
            Assert.InRange(ra2, 0, 360);
        }
    }

    [Fact]
    public void PlaneBasisIsOrthonormalAndRoundTrips()
    {
        var poles = new[] { Frames.PoleToEcliptic(286.13, 63.87), new Vec3(0, 0, 1), Frames.EquatorialToEcliptic(new Vec3(0, 0, 1)), Vec3.Normalize(new Vec3(0.2, -0.9, 0.4)) };
        foreach (var pole in poles)
        {
            var b = Frames.PlaneBasis(pole);
            Close.To(1, Vec3.Length(b.X), 12);
            Close.To(1, Vec3.Length(b.Y), 12);
            Close.To(0, Vec3.Dot(b.X, b.Y), 12);
            Close.To(0, Vec3.Dot(b.X, b.Z), 12);
            CloseVec(b.Z, Vec3.Cross(b.X, b.Y), 1e-12);
            foreach (var v in Samples)
            {
                var tol = 1e-12 * Math.Max(1, Vec3.Length(v));
                CloseVec(v, Frames.FromBasis(b, Frames.ToBasis(b, v)), tol);
                CloseVec(v, Frames.ToBasis(b, Frames.FromBasis(b, v)), tol);
            }
        }
    }

    [Fact]
    public void PlaneBasisXLiesOnTheIcrfEquator()
    {
        var b = Frames.PlaneBasis(Frames.PoleToEcliptic(40.589, 83.537));
        Close.To(0, Frames.EclipticToEquatorial(b.X).Z, 12);
    }
}
