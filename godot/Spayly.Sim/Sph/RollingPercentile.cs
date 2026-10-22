namespace Spayly.Sim.Sph;

/// <summary>
/// The last <c>capacity</c> samples and their percentiles, with the same rank rule as SphGpu's substep stats: p50 is
/// sorted[n/2], p95 sorted[min(n − 1, ⌊0.95 n⌋)]. Preallocated: adding and querying allocate nothing, so it can run every frame.
/// </summary>
public sealed class RollingPercentile(int capacity)
{
    private readonly double[] ring = new double[capacity], scratch = new double[capacity];
    private long added;

    /// <summary>Samples held, 0..capacity.</summary>
    public int Count => (int)Math.Min(added, ring.Length);

    public void Add(double x) => ring[added++ % ring.Length] = x;

    public void Clear() => added = 0;

    /// <summary>The <paramref name="q"/>-quantile of the samples held (0 when empty), in their unit.</summary>
    public double Percentile(double q)
    {
        var n = Count;
        if (n == 0) return 0;
        Array.Copy(ring, scratch, n);
        Array.Sort(scratch, 0, n);
        return scratch[Math.Clamp((int)(q * n), 0, n - 1)];
    }
}
