namespace Spayly.Sim.Sph;

/// <summary>
/// The M4 cubic-spline kernel in 3D (Monaghan &amp; Lattanzio 1985; Monaghan 1992 eq. 2.6), support 2h:
///   W(r, h) = 1/(πh³) × { 1 − 1.5q² + 0.75q³ for q &lt; 1;  0.25(2 − q)³ for 1 ≤ q &lt; 2;  0 },  q = r/h.
/// r and h in the same length unit; W in 1/length³. The GPU (sph_common.glslinc) evaluates the same polynomial.
/// </summary>
public static class Kernel
{
    /// <summary>Support radius in units of h.</summary>
    public const double Support = 2;

    private const double Sigma = 1 / Math.PI;

    /// <summary>W(r, h), 1/length³.</summary>
    public static double W(double r, double h)
    {
        var q = r / h;
        var s = Sigma / (h * h * h);
        if (q < 1) return s * (1 - 1.5 * q * q + 0.75 * q * q * q);
        if (q < 2)
        {
            var t = 2 - q;
            return s * 0.25 * t * t * t;
        }
        return 0;
    }

    /// <summary>∂W/∂r at (r, h), 1/length⁴; ≤ 0 everywhere and 0 at r = 0 and r ≥ 2h.</summary>
    public static double DwDr(double r, double h)
    {
        var q = r / h;
        var s = Sigma / (h * h * h * h);
        if (q < 1) return s * (-3 * q + 2.25 * q * q);
        if (q < 2)
        {
            var t = 2 - q;
            return -s * 0.75 * t * t;
        }
        return 0;
    }

    /// <summary>
    /// ∇ᵢW(|rᵢⱼ|, h) = ∂W/∂r · rᵢⱼ/|rᵢⱼ| for rᵢⱼ = xᵢ − xⱼ, 1/length⁴; zero at rᵢⱼ = 0. Antisymmetric: swapping i and j
    /// negates it exactly, since only rᵢⱼ's sign changes.
    /// </summary>
    public static Vec3 Gradient(Vec3 rij, double h)
    {
        var r = Vec3.Length(rij);
        if (r <= 0) return Vec3.Zero;
        return rij * (DwDr(r, h) / r);
    }
}
