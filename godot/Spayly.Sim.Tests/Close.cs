namespace Spayly.Sim.Tests;

/// <summary>
/// vitest's toBeCloseTo(expected, digits): |expected − actual| &lt; 0.5 × 10^−digits. xUnit's precision overload rounds
/// instead, which is a different (weaker at boundaries) test, so every ported tolerance goes through here.
/// </summary>
public static class Close
{
    public static void To(double expected, double actual, int digits = 2)
    {
        var tol = 0.5 * Math.Pow(10, -digits);
        var diff = Math.Abs(expected - actual);
        Assert.True(diff < tol, $"expected {actual} to be within {tol} of {expected} (diff {diff})");
    }
}
