namespace Spayly.Sim.Tests;

/// <summary>
/// Photometry.cs has no web counterpart (the web approximates blackbody colour with a curve fit), so these are new: the
/// CIE table against known standards, the colour's normalisation and ordering, and the limb law's bounds.
/// </summary>
public class PhotometryTests
{
    static readonly double[] Temps = Enumerable.Range(0, 60).Select(i => 1000 * Math.Pow(40, i / 59.0)).ToArray();

    [Fact]
    public void CmfColumnsAreEqualEnergyBalanced()
    {
        double sx = 0, sy = 0, sz = 0;
        for (var i = 0; i < Photometry.Cmf.GetLength(0); i++)
        {
            sx += Photometry.Cmf[i, 0];
            sy += Photometry.Cmf[i, 1];
            sz += Photometry.Cmf[i, 2];
        }
        Assert.Equal(41, Photometry.Cmf.GetLength(0));
        Assert.True(Math.Abs(sx / sy - 1) < 1e-3 && Math.Abs(sz / sy - 1) < 1e-3, $"sums {sx} {sy} {sz}");
    }

    [Fact]
    public void PlanckAt2856KIsIlluminantA()
    {
        var (x, y) = Photometry.PlanckChromaticity(2856);
        Close.To(0.44757, x, 3);
        Close.To(0.40745, y, 3);
    }

    [Fact]
    public void SolarChromaticityIsNearThePlanckianLocusValue()
    {
        var (x, y) = Photometry.PlanckChromaticity(5772);
        Assert.True(Math.Abs(x - 0.3267) < 0.01 && Math.Abs(y - 0.3355) < 0.01, $"(x, y) = ({x}, {y})");
    }

    [Fact]
    public void SrgbMatrixMapsD65ToWhite()
    {
        var (r, g, b) = Photometry.XyzToLinearSrgb(0.95047, 1, 1.08883);
        Close.To(1, r, 4);
        Close.To(1, g, 4);
        Close.To(1, b, 4);
    }

    [Fact]
    public void LinearSrgbColourHasUnitLuminanceAndRoundTripsToTheSameChromaticity()
    {
        foreach (var t in Temps)
        {
            var (r, g, b) = Photometry.PlanckToLinearSrgb(t);
            Close.To(1, Photometry.Luminance(r, g, b), 6);
            Assert.True(r >= 0 && g >= 0 && b >= 0, $"{t} K: ({r}, {g}, {b})");
        }
        // Inverse sRGB matrix (Lindbloom): back to XYZ and compare with the direct chromaticity for an in-gamut 5772 K.
        var (sr, sg, sb) = Photometry.PlanckToLinearSrgb(5772);
        var X = 0.4124564 * sr + 0.3575761 * sg + 0.1804375 * sb;
        var Y = 0.2126729 * sr + 0.7151522 * sg + 0.0721750 * sb;
        var Z = 0.0193339 * sr + 0.1191920 * sg + 0.9503041 * sb;
        var (x, y) = Photometry.PlanckChromaticity(5772);
        Close.To(x, X / (X + Y + Z), 4);
        Close.To(y, Y / (X + Y + Z), 4);
    }

    [Fact]
    public void CoolStarIsRedOrangeAndHotStarIsBlue()
    {
        var (r3, g3, b3) = Photometry.PlanckToLinearSrgb(3000);
        Assert.True(r3 > g3 && g3 > b3, $"3000 K: ({r3}, {g3}, {b3})");
        var (r30, _, b30) = Photometry.PlanckToLinearSrgb(30000);
        Assert.True(b30 > r30, $"30000 K: R {r30} B {b30}");
    }

    [Fact]
    public void HueIsMonotonicInTemperature()
    {
        double lastBr = -1, lastGr = -1, lastX = 1;
        foreach (var t in Temps)
        {
            var (r, g, b) = Photometry.PlanckToLinearSrgb(t);
            var (x, _) = Photometry.PlanckChromaticity(t);
            Assert.True(b / r >= lastBr && g / r > lastGr && x < lastX, $"{t} K: B/R {b / r} G/R {g / r} x {x}");
            lastBr = b / r;
            lastGr = g / r;
            lastX = x;
        }
    }

    [Fact]
    public void LinearToSrgbIsTheIecCurve()
    {
        Close.To(0, Photometry.LinearToSrgb(0), 9);
        Close.To(1, Photometry.LinearToSrgb(1), 9);
        Close.To(0.7353570, Photometry.LinearToSrgb(0.5), 6);
        // The two branches meet at the breakpoint.
        Close.To(12.92 * 0.0031308, 1.055 * Math.Pow(0.0031308, 1 / 2.4) - 0.055, 6);
    }

    [Fact]
    public void LimbLawIsOneAtCentreAndPositiveAtTheLimb()
    {
        foreach (var t in Temps)
        {
            var (r, g, b) = Photometry.LimbDarkeningCoefficients(t);
            foreach (var law in new[] { r, g, b })
            {
                Close.To(1, law.Intensity(1), 12);
                for (var k = 0; k <= 20; k++)
                    Assert.True(law.Intensity(k / 20.0) > 0, $"{t} K {law}: I({k / 20.0}) = {law.Intensity(k / 20.0)}");
            }
        }
    }

    [Fact]
    public void SunDarkensMoreInBlueAndMatchesTheVisibleLinearLaw()
    {
        var (r, g, b) = Photometry.LimbDarkeningCoefficients(5772);
        Assert.True(b.Intensity(0.3) < g.Intensity(0.3) && g.Intensity(0.3) < r.Intensity(0.3), $"R {r} G {g} B {b}");
        // Linear u ≈ 0.6 in the visible puts I(0.5) near 0.7.
        Assert.True(Math.Abs(g.Intensity(0.5) - 0.7) < 0.05, $"G I(0.5) = {g.Intensity(0.5)}");
        Assert.True(Math.Abs(g.U1 - 0.6) < 0.1, $"G u1 = {g.U1}");
    }

    [Fact]
    public void HotterStarsDarkenLess()
    {
        var last = 0.0;
        foreach (var t in Temps)
        {
            var limb = Photometry.LimbDarkeningCoefficients(t).G.Intensity(0);
            Assert.True(limb > last, $"{t} K: I(0) {limb} after {last}");
            last = limb;
        }
    }
}
