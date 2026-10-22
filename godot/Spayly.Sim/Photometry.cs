namespace Spayly.Sim;

/// <summary>
/// Quadratic limb-darkening law I(μ)/I(1) = 1 − U1 (1 − μ) − U2 (1 − μ)², μ = cos of the angle from disc centre.
/// </summary>
public readonly record struct LimbLaw(double U1, double U2)
{
    /// <summary>Intensity at <paramref name="mu"/> relative to disc centre (dimensionless, μ in [0, 1]).</summary>
    public double Intensity(double mu)
    {
        var x = 1 - mu;
        return 1 - U1 * x - U2 * x * x;
    }
}

/// <summary>
/// Star colour and limb darkening from first principles, for the renderer's star LUT (Godot has no web counterpart:
/// the web build approximates colour with a curve fit, RenderUnits.Blackbody). No engine types, so it is testable here.
/// </summary>
public static class Photometry
{
    // CODATA 2018 exact SI values.
    const double H = 6.62607015e-34, C = 2.99792458e8, K = 1.380649e-23;

    /// <summary>First wavelength of <see cref="Cmf"/>, nm.</summary>
    public const double CmfStartNm = 380;
    /// <summary>Spacing of <see cref="Cmf"/>, nm.</summary>
    public const double CmfStepNm = 10;

    /// <summary>
    /// CIE 1931 2° standard observer x̄, ȳ, z̄ at 10 nm from 380 to 780 nm: the 10 nm abridgement of CIE 015:2004
    /// (Colorimetry, 3rd ed.) Table T.4, identical to Wyszecki &amp; Stiles, Color Science (2nd ed., 1982) Table I(3.3.1).
    /// Checked by PhotometryTests: the three columns sum to within 0.1 % of each other (equal-energy white at x = y = 1/3)
    /// and a 2856 K Planckian gives CIE Illuminant A's (0.44757, 0.40745).
    /// </summary>
    public static readonly double[,] Cmf =
    {
        { 0.001368, 0.000039, 0.006450 }, { 0.004243, 0.000120, 0.020050 }, { 0.014310, 0.000396, 0.067850 },
        { 0.043510, 0.001210, 0.207400 }, { 0.134380, 0.004000, 0.645600 }, { 0.283900, 0.011600, 1.385600 },
        { 0.348280, 0.023000, 1.747060 }, { 0.336200, 0.038000, 1.772110 }, { 0.290800, 0.060000, 1.669200 },
        { 0.195360, 0.090980, 1.287640 }, { 0.095640, 0.139020, 0.812950 }, { 0.032010, 0.208020, 0.465180 },
        { 0.004900, 0.323000, 0.272000 }, { 0.009300, 0.503000, 0.158200 }, { 0.063270, 0.710000, 0.078250 },
        { 0.165500, 0.862000, 0.042160 }, { 0.290400, 0.954000, 0.020300 }, { 0.433450, 0.994950, 0.008750 },
        { 0.594500, 0.995000, 0.003900 }, { 0.762100, 0.952000, 0.002100 }, { 0.916300, 0.870000, 0.001650 },
        { 1.026300, 0.757000, 0.001100 }, { 1.062200, 0.631000, 0.000800 }, { 1.002600, 0.503000, 0.000340 },
        { 0.854450, 0.381000, 0.000190 }, { 0.642400, 0.265000, 0.000050 }, { 0.447900, 0.175000, 0.000020 },
        { 0.283500, 0.107000, 0.000000 }, { 0.164900, 0.061000, 0.000000 }, { 0.087400, 0.032000, 0.000000 },
        { 0.046770, 0.017000, 0.000000 }, { 0.022700, 0.008210, 0.000000 }, { 0.011359, 0.004102, 0.000000 },
        { 0.005790, 0.002091, 0.000000 }, { 0.002899, 0.001047, 0.000000 }, { 0.001440, 0.000520, 0.000000 },
        { 0.000690, 0.000249, 0.000000 }, { 0.000332, 0.000120, 0.000000 }, { 0.000166, 0.000060, 0.000000 },
        { 0.000083, 0.000030, 0.000000 }, { 0.000042, 0.000015, 0.000000 },
    };

    /// <summary>
    /// Wavelengths the limb law is evaluated at for the R, G, B channels, nm: the dominant wavelengths of the sRGB primaries
    /// (chromaticities (0.64, 0.33), (0.30, 0.60), (0.15, 0.06) seen from D65).
    /// </summary>
    public static readonly double[] ChannelWavelengthNm = [611.3, 549.1, 464.3];

    /// <summary>Planck spectral radiance B_λ(T), W·sr⁻¹·m⁻³, for a wavelength in m and a temperature in K.</summary>
    public static double PlanckRadiance(double lambdaM, double tempK)
    {
        var x = H * C / (lambdaM * K * tempK);
        return 2 * H * C * C / (Math.Pow(lambdaM, 5) * (Math.Exp(x) - 1));
    }

    /// <summary>
    /// CIE XYZ of a blackbody at <paramref name="tempK"/> (K), scaled so Y = 1. Rectangle rule over the 10 nm table, the
    /// CIE's own practice for abridged data.
    /// </summary>
    public static (double X, double Y, double Z) PlanckToXyz(double tempK)
    {
        double x = 0, y = 0, z = 0;
        for (var i = 0; i < Cmf.GetLength(0); i++)
        {
            var b = PlanckRadiance((CmfStartNm + i * CmfStepNm) * 1e-9, tempK);
            x += b * Cmf[i, 0];
            y += b * Cmf[i, 1];
            z += b * Cmf[i, 2];
        }
        return (x / y, 1, z / y);
    }

    /// <summary>CIE 1931 (x, y) chromaticity of a blackbody at <paramref name="tempK"/> (K).</summary>
    public static (double X, double Y) PlanckChromaticity(double tempK)
    {
        var (x, y, z) = PlanckToXyz(tempK);
        var s = x + y + z;
        return (x / s, y / s);
    }

    /// <summary>
    /// CIE XYZ to linear sRGB (IEC 61966-2-1 primaries, D65 white; Lindbloom's full-precision matrix). XYZ of D65
    /// (0.95047, 1, 1.08883) maps to (1, 1, 1).
    /// </summary>
    public static (double R, double G, double B) XyzToLinearSrgb(double x, double y, double z) => (
        3.2404542 * x - 1.5371385 * y - 0.4985314 * z,
        -0.9692660 * x + 1.8760108 * y + 0.0415560 * z,
        0.0556434 * x - 0.2040259 * y + 1.0572252 * z);

    /// <summary>Relative luminance Y of a linear sRGB colour (the middle row of the sRGB → XYZ matrix).</summary>
    public static double Luminance(double r, double g, double b) => 0.2126729 * r + 0.7151522 * g + 0.0721750 * b;

    /// <summary>
    /// Linear sRGB colour of a blackbody at <paramref name="tempK"/> (K), with luminance Y = 1, so it carries hue only and
    /// the caller sets brightness. Below about 1900 K blue falls outside the sRGB gamut; a negative channel is clipped to 0
    /// and the result rescaled to Y = 1 again.
    /// </summary>
    public static (double R, double G, double B) PlanckToLinearSrgb(double tempK)
    {
        var (x, y, z) = PlanckToXyz(tempK);
        var (r, g, b) = XyzToLinearSrgb(x, y, z);
        r = Math.Max(r, 0);
        g = Math.Max(g, 0);
        b = Math.Max(b, 0);
        var lum = Luminance(r, g, b);
        return (r / lum, g / lum, b / lum);
    }

    /// <summary>sRGB transfer function (IEC 61966-2-1), linear [0, 1] to encoded [0, 1].</summary>
    public static double LinearToSrgb(double c) =>
        c <= 0.0031308 ? 12.92 * c : 1.055 * Math.Pow(c, 1 / 2.4) - 0.055;

    /// <summary>
    /// Per-channel quadratic limb-darkening coefficients for a star of effective temperature <paramref name="tempK"/> (K).
    ///
    /// Method: a grey atmosphere in the Eddington approximation, T⁴(τ) = ¾ T_eff⁴ (τ + ⅔), with LTE source function
    /// S = B_λ(T(τ)); the emergent intensity I_λ(μ) = ∫ S e^{−τ/μ} dτ/μ at each channel's <see cref="ChannelWavelengthNm"/>,
    /// fitted by least squares on 21 values of μ with I(0) held exact. Darkening is stronger at short wavelengths and in
    /// cooler stars because B_λ is steeper in T on the Wien side (Mihalas, Stellar Atmospheres, 2nd ed., §3-4).
    ///
    /// Check against the Sun (5772 K): the G channel's law equals a linear u ≈ 0.65 and I(0) ≈ 0.34; Pierce &amp; Slaughter
    /// (1977, Solar Phys. 51, 25), tabulated in Allen's Astrophysical Quantities (4th ed., §14.7), give u ≈ 0.6–0.65 and
    /// I(0) ≈ 0.30 near 550 nm, and I(0) ≈ 0.35 at 600 nm (ours 0.38) and ≈ 0.2 at 450 nm (ours 0.27): the grey model
    /// under-darkens the blue limb a little, the right sign everywhere. U1 is clamped to [0, 2(1 − I(0))], which keeps the
    /// law monotonic and so positive on all of μ ∈ [0, 1] even for the coolest stars.
    /// </summary>
    public static (LimbLaw R, LimbLaw G, LimbLaw B) LimbDarkeningCoefficients(double tempK)
    {
        var t = Math.Max(tempK, 500);
        return (FitLimb(t, ChannelWavelengthNm[0]), FitLimb(t, ChannelWavelengthNm[1]), FitLimb(t, ChannelWavelengthNm[2]));
    }

    const int LimbMu = 21, LimbSteps = 128;

    static LimbLaw FitLimb(double teff, double lambdaNm)
    {
        var lambda = lambdaNm * 1e-9;
        var centre = EmergentIntensity(teff, lambda, 1);
        var a = 1 - EmergentIntensity(teff, lambda, 0) / centre;
        // I = 1 − a x² − u1 (x − x²) with x = 1 − μ: one free parameter once I(0) is pinned.
        double num = 0, den = 0;
        for (var k = 0; k < LimbMu; k++)
        {
            var mu = k / (double)(LimbMu - 1);
            var x = 1 - mu;
            var f = x - x * x;
            var r = 1 - a * x * x - EmergentIntensity(teff, lambda, mu) / centre;
            num += f * r;
            den += f * f;
        }
        var u1 = Math.Clamp(den > 0 ? num / den : a, 0, 2 * a);
        return new LimbLaw(u1, a - u1);
    }

    // ∫₀^∞ B(T(τ)) e^{−τ/μ} dτ/μ with s = 1 − e^{−τ/μ}, so the integral becomes ∫₀¹ B(T(−μ ln(1 − s))) ds (midpoint rule).
    static double EmergentIntensity(double teff, double lambda, double mu)
    {
        double sum = 0;
        for (var i = 0; i < LimbSteps; i++)
        {
            var s = (i + 0.5) / LimbSteps;
            var tau = -mu * Math.Log(1 - s);
            sum += PlanckRadiance(lambda, teff * Math.Pow(0.75 * (tau + 2.0 / 3), 0.25));
        }
        return sum / LimbSteps;
    }
}
