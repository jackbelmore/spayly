namespace Spayly.Sim.Sph;

/// <summary>
/// Hashed uniform grid for neighbour search, rebuilt by counting sort (Green, NVIDIA "particles"; research §2.3): each
/// particle's cell is hashed into a table of next_pow2(2N) buckets, counted, prefix-summed and scattered. The key is the
/// spike M1a "block" key, bit for bit what the GPU uses (sph_common.glslinc): a 4×4×4 block of cells shares 64
/// consecutive buckets and only the block index is hashed (Teschner et al. 2003 primes). Different cells can share a
/// bucket, so a visitor must compare each candidate's own cell (<see cref="Cx"/>…) with the cell it is visiting; that
/// also makes visiting one bucket twice harmless. Units: whatever the positions are in.
/// </summary>
public sealed class UniformGrid
{
    /// <summary>Cell side.</summary>
    public double Cell { get; private set; }
    public double InvCell { get; private set; }
    public int Mask { get; private set; }
    public int TableSize { get; private set; }
    /// <summary>First slot in <see cref="Sorted"/> of each bucket.</summary>
    public int[] Start { get; private set; } = [];
    /// <summary>Particles in each bucket.</summary>
    public int[] Count { get; private set; } = [];
    /// <summary>Particle indices grouped by bucket.</summary>
    public int[] Sorted { get; private set; } = [];
    /// <summary>Each particle's integer cell.</summary>
    public int[] Cx { get; private set; } = [];
    public int[] Cy { get; private set; } = [];
    public int[] Cz { get; private set; } = [];
    /// <summary>Largest h in each bucket (0 when built without h).</summary>
    public double[] BucketHmax { get; private set; } = [];
    private int[] key = [], rank = [];

    /// <summary>Largest |cell index| kept; farther particles are clamped into the edge cells, which distance checks tolerate.</summary>
    public const int CellLimit = 1 << 28;

    /// <summary>Bucket of an integer cell: ((hash(c ≫ 2) ≪ 6) | (c &amp; 3 packed)) &amp; mask; mask + 1 ≥ 64.</summary>
    public static int Key(int cx, int cy, int cz, int mask)
    {
        unchecked
        {
            var h = ((uint)(cx >> 2) * 73856093u) ^ ((uint)(cy >> 2) * 19349663u) ^ ((uint)(cz >> 2) * 83492791u);
            var l = (uint)(cx & 3) | ((uint)(cy & 3) << 2) | ((uint)(cz & 3) << 4);
            return (int)(((h << 6) | l) & (uint)mask);
        }
    }

    /// <summary>Integer cell of a coordinate: floor(x / cell), clamped to ±<see cref="CellLimit"/>.</summary>
    public int CellOf(double x)
    {
        var c = Math.Floor(x * InvCell);
        if (c > CellLimit) return CellLimit;
        if (c < -CellLimit) return -CellLimit;
        return (int)c;
    }

    /// <summary>Rebuild for the first <paramref name="n"/> particles with cells of side <paramref name="cell"/>; <paramref name="h"/> fills <see cref="BucketHmax"/>.</summary>
    public void Build(int n, double[] x, double[] y, double[] z, double[]? h, double cell)
    {
        Cell = cell;
        InvCell = 1 / cell;
        var table = 64;
        while (table < 2 * n) table <<= 1;
        if (table != TableSize)
        {
            TableSize = table;
            Mask = table - 1;
            Start = new int[table];
            Count = new int[table];
            BucketHmax = new double[table];
        }
        else
        {
            Array.Clear(Count);
            Array.Clear(BucketHmax);
        }
        if (Sorted.Length < n)
        {
            Sorted = new int[n]; key = new int[n]; rank = new int[n];
            Cx = new int[n]; Cy = new int[n]; Cz = new int[n];
        }
        for (var i = 0; i < n; i++)
        {
            int a = CellOf(x[i]), b = CellOf(y[i]), c = CellOf(z[i]);
            Cx[i] = a; Cy[i] = b; Cz[i] = c;
            var k = Key(a, b, c, Mask);
            key[i] = k;
            rank[i] = Count[k]++;
            if (h != null && h[i] > BucketHmax[k]) BucketHmax[k] = h[i];
        }
        var s = 0;
        for (var k = 0; k < table; k++)
        {
            Start[k] = s;
            s += Count[k];
        }
        for (var i = 0; i < n; i++) Sorted[Start[key[i]] + rank[i]] = i;
    }

    /// <summary>Squared distance from a point to the cell (a, b, c)'s box; 0 inside it.</summary>
    public double BoxDistance2(double x, double y, double z, int a, int b, int c)
    {
        var d = Axis(x, a);
        var e = Axis(y, b);
        var f = Axis(z, c);
        return d * d + e * e + f * f;
    }

    private double Axis(double x, int a)
    {
        var lo = a * Cell;
        var hi = lo + Cell;
        return x < lo ? lo - x : x > hi ? x - hi : 0;
    }
}
