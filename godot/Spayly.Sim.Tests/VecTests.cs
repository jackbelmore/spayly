namespace Spayly.Sim.Tests;

// No vitest file covers vec.ts directly; these pin the port. The Mulberry32 goldens come from running the exact
// makeRandom body of src/sim/vec.ts under node, so any drift from the web build's streams fails here.
public class VecTests
{
    [Fact]
    public void OperatorsMatchComponentwiseArithmetic()
    {
        var a = new Vec3(1, 2, 3);
        var b = new Vec3(-4, 0.5, 8);
        Assert.Equal(new Vec3(-3, 2.5, 11), a + b);
        Assert.Equal(new Vec3(5, 1.5, -5), a - b);
        Assert.Equal(new Vec3(-1, -2, -3), -a);
        Assert.Equal(new Vec3(2, 4, 6), a * 2);
        Assert.Equal(new Vec3(2, 4, 6), 2 * a);
        Assert.Equal(new Vec3(0.5, 1, 1.5), a / 2);
        Assert.Equal(a + b, Vec3.Add(a, b));
        Assert.Equal(a - b, Vec3.Sub(a, b));
        Assert.Equal(a * 3, Vec3.Scale(a, 3));
    }

    [Fact]
    public void DotCrossLengthDistance()
    {
        var x = new Vec3(1, 0, 0);
        var y = new Vec3(0, 1, 0);
        Assert.Equal(new Vec3(0, 0, 1), Vec3.Cross(x, y));
        Assert.Equal(new Vec3(0, 0, -1), Vec3.Cross(y, x));
        Assert.Equal(0, Vec3.Dot(x, y));
        Assert.Equal(1 * -4 + 2 * 0.5 + 3 * 8, Vec3.Dot(new Vec3(1, 2, 3), new Vec3(-4, 0.5, 8)));
        Assert.Equal(5, Vec3.Length(new Vec3(3, 4, 0)));
        Assert.Equal(13, Vec3.Distance(new Vec3(1, 1, 1), new Vec3(4, 5, 13)));
        var a = new Vec3(0.3, -1.7, 2.2);
        var b = new Vec3(4.1, 0.2, -0.9);
        var c = Vec3.Cross(a, b);
        Close.To(0, Vec3.Dot(c, a), 12);
        Close.To(0, Vec3.Dot(c, b), 12);
    }

    [Fact]
    public void NormalizeLerpClamp()
    {
        Close.To(1, Vec3.Length(Vec3.Normalize(new Vec3(3, -7, 11))), 14);
        Assert.Equal(Vec3.Zero, Vec3.Normalize(Vec3.Zero));
        Assert.Equal(new Vec3(2, 3, 4), Vec3.Lerp(new Vec3(0, 2, 4), new Vec3(4, 4, 4), 0.5));
        Assert.Equal(1, Vec3.Clamp(5, -1, 1));
        Assert.Equal(-1, Vec3.Clamp(-5, -1, 1));
        Assert.Equal(0.25, Vec3.Clamp(0.25, -1, 1));
        // lo > hi returns hi, as Math.min(hi, Math.max(lo, x)) does; Math.Clamp would throw.
        Assert.Equal(0, Vec3.Clamp(0.5, 1, 0));
    }

    [Fact]
    public void AnyPerpendicularIsAUnitNormal()
    {
        foreach (var v in new[] { new Vec3(1, 0, 0), new Vec3(0, 0, 5), new Vec3(0.95, 0.1, 0), new Vec3(-2, 3, 7) })
        {
            var p = Vec3.AnyPerpendicular(v);
            Close.To(1, Vec3.Length(p), 14);
            Close.To(0, Vec3.Dot(p, Vec3.Normalize(v)), 14);
        }
    }

    [Theory]
    [InlineData(7u, new[] { 0.011704753153026104, 0.061958257574588060, 0.97690763277933002, 0.69902870571240783, 0.52144526853226125 })]
    [InlineData(0u, new[] { 0.26642920868471265, 0.00032974570058286190, 0.22327202744781971, 0.14620214793831110, 0.46732782293111086 })]
    [InlineData(123456789u, new[] { 0.25779074383899570, 0.97077211155556142, 0.78532801428809762, 0.20616457983851433, 0.30307188746519387 })]
    public void Mulberry32IsBitExactWithMakeRandom(uint seed, double[] expected)
    {
        var rand = Rng.Mulberry32(seed);
        foreach (var e in expected) Assert.Equal(e, rand());
    }

    [Fact]
    public void Mulberry32GoldensAreExactMultiplesOfTwoToTheMinus32()
    {
        // The numerators node printed alongside the decimals; guards against a literal that merely rounds close.
        var rand = Rng.Mulberry32(7);
        foreach (var n in new uint[] { 50271532, 266108690, 4195786334, 3002305430, 2239590375 })
            Assert.Equal(n / 4294967296.0, rand());
    }

    [Fact]
    public void RandomUnitWithASeededRngIsAUnitVector()
    {
        var rand = Rng.Mulberry32(42);
        for (var i = 0; i < 1000; i++) Close.To(1, Vec3.Length(Vec3.RandomUnit(rand)), 14);
        Close.To(1, Vec3.Length(Vec3.RandomUnit()), 14);
    }

    [Fact]
    public void RandomUnitIsDeterministicForASeed()
    {
        Assert.Equal(Vec3.RandomUnit(Rng.Mulberry32(9)), Vec3.RandomUnit(Rng.Mulberry32(9)));
    }
}
