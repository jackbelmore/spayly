namespace Spayly.Sim.Sph;

/// <summary>
/// Barnes–Hut octree (Barnes &amp; Hut 1986) for self-gravity, with monopole plus optional quadrupole moments about each
/// node's centre of mass and Plummer softening. A node is accepted when d &gt; s/θ + δ, s its side and δ the offset of its
/// centre of mass from its geometric centre (Barnes 1994's guard against the "exploding galaxy" cases of the plain s/d
/// test). Units are the caller's; accelerations come out in [G]·mass/length². Built on flat arrays with an explicit walk
/// stack, so a walk allocates nothing.
///
/// Quadrupole Q_ab = Σ m (3 x_a x_b − |x|² δ_ab) about the node CM; for d = x − x_cm the field is
///   a = −GM d/r³ + G Q·d/r⁵ − (5G/2)(dᵀQd) d/r⁷,
/// with r² → r² + ε² in every denominator (the usual softened approximation; exact for the monopole).
/// </summary>
public sealed class Octree
{
    /// <summary>Most particles in a leaf.</summary>
    public int LeafSize { get; set; } = 8;
    /// <summary>Deepest level; coincident particles stop splitting here.</summary>
    public int MaxDepth { get; set; } = 40;

    private int nodes;
    private double[] cx = [], cy = [], cz = [], half = [];
    private double[] nm = [], mx = [], my = [], mz = [], delta = [];
    private double[] qxx = [], qyy = [], qzz = [], qxy = [], qxz = [], qyz = [];
    private int[] child = [], start = [], count = [], depth = [];
    private int[] perm = [], tmp = [];
    private double[] lx = [], ly = [], lz = [], lm = [];
    private int[] stack = [];
    private readonly int[] octCount = new int[8];
    private readonly int[] octStart = new int[8];
    private readonly List<int> work = [];

    /// <summary>Nodes in the current tree.</summary>
    public int NodeCount => nodes;

    /// <summary>Build over the first <paramref name="n"/> entries of the arrays (positions and masses).</summary>
    public void Build(double[] x, double[] y, double[] z, double[] m, int n)
    {
        if (perm.Length < n)
        {
            perm = new int[n]; tmp = new int[n];
            lx = new double[n]; ly = new double[n]; lz = new double[n]; lm = new double[n];
        }
        nodes = 0;
        if (n == 0) return;
        double x0 = double.PositiveInfinity, y0 = x0, z0 = x0, x1 = double.NegativeInfinity, y1 = x1, z1 = x1;
        for (var i = 0; i < n; i++)
        {
            perm[i] = i;
            x0 = Math.Min(x0, x[i]); x1 = Math.Max(x1, x[i]);
            y0 = Math.Min(y0, y[i]); y1 = Math.Max(y1, y[i]);
            z0 = Math.Min(z0, z[i]); z1 = Math.Max(z1, z[i]);
        }
        var h = 0.5 * Math.Max(x1 - x0, Math.Max(y1 - y0, z1 - z0));
        // A hair larger so the maximum lands strictly inside; a zero-extent set still gets a finite box.
        h = h > 0 ? h * (1 + 1e-9) : 1;
        NewNode(0.5 * (x0 + x1), 0.5 * (y0 + y1), 0.5 * (z0 + z1), h, 0, n, 0);

        work.Clear();
        work.Add(0);
        while (work.Count > 0)
        {
            var k = work[^1];
            work.RemoveAt(work.Count - 1);
            if (count[k] <= LeafSize || depth[k] >= MaxDepth) continue;
            Split(k, x, y, z);
            for (var c = 0; c < 8; c++) work.Add(child[k] + c);
        }
        for (var p = 0; p < n; p++)
        {
            var i = perm[p];
            lx[p] = x[i]; ly[p] = y[i]; lz[p] = z[i]; lm[p] = m[i];
        }
        // Children are always created after their parent, so a reverse sweep sees every child before its parent.
        for (var k = nodes - 1; k >= 0; k--) Moments(k);
        var need = 8 * (MaxDepth + 2);
        if (stack.Length < need) stack = new int[need];
    }

    private int NewNode(double x, double y, double z, double h, int s, int c, int d)
    {
        if (nodes == cx.Length) Grow(Math.Max(64, 2 * nodes));
        var k = nodes++;
        cx[k] = x; cy[k] = y; cz[k] = z; half[k] = h;
        start[k] = s; count[k] = c; depth[k] = d; child[k] = -1;
        return k;
    }

    private void Grow(int cap)
    {
        void G(ref double[] a) => Array.Resize(ref a, cap);
        void Gi(ref int[] a) => Array.Resize(ref a, cap);
        G(ref cx); G(ref cy); G(ref cz); G(ref half); G(ref nm); G(ref mx); G(ref my); G(ref mz); G(ref delta);
        G(ref qxx); G(ref qyy); G(ref qzz); G(ref qxy); G(ref qxz); G(ref qyz);
        Gi(ref child); Gi(ref start); Gi(ref count); Gi(ref depth);
    }

    private void Split(int k, double[] x, double[] y, double[] z)
    {
        int s = start[k], c = count[k];
        double ox = cx[k], oy = cy[k], oz = cz[k];
        Array.Clear(octCount);
        for (var p = s; p < s + c; p++)
        {
            var i = perm[p];
            octCount[Octant(x[i] >= ox, y[i] >= oy, z[i] >= oz)]++;
        }
        var run = s;
        for (var o = 0; o < 8; o++)
        {
            octStart[o] = run;
            run += octCount[o];
        }
        for (var p = s; p < s + c; p++)
        {
            var i = perm[p];
            tmp[octStart[Octant(x[i] >= ox, y[i] >= oy, z[i] >= oz)]++] = i;
        }
        Array.Copy(tmp, s, perm, s, c);
        var h = 0.5 * half[k];
        var first = -1;
        var at = s;
        for (var o = 0; o < 8; o++)
        {
            var nx = ox + ((o & 1) != 0 ? h : -h);
            var ny = oy + ((o & 2) != 0 ? h : -h);
            var nz = oz + ((o & 4) != 0 ? h : -h);
            var id = NewNode(nx, ny, nz, h, at, octCount[o], depth[k] + 1);
            if (o == 0) first = id;
            at += octCount[o];
        }
        child[k] = first;
    }

    private static int Octant(bool px, bool py, bool pz) => (px ? 1 : 0) | (py ? 2 : 0) | (pz ? 4 : 0);

    private void Moments(int k)
    {
        double m = 0, sx = 0, sy = 0, sz = 0;
        double xx = 0, yy = 0, zz = 0, xy = 0, xz = 0, yz = 0;
        if (child[k] < 0)
        {
            int s = start[k], e = s + count[k];
            for (var p = s; p < e; p++)
            {
                m += lm[p];
                sx += lm[p] * lx[p]; sy += lm[p] * ly[p]; sz += lm[p] * lz[p];
            }
            if (m > 0)
            {
                double gx = sx / m, gy = sy / m, gz = sz / m;
                for (var p = s; p < e; p++)
                {
                    double dx = lx[p] - gx, dy = ly[p] - gy, dz = lz[p] - gz, w = lm[p];
                    var r2 = dx * dx + dy * dy + dz * dz;
                    xx += w * (3 * dx * dx - r2); yy += w * (3 * dy * dy - r2); zz += w * (3 * dz * dz - r2);
                    xy += w * 3 * dx * dy; xz += w * 3 * dx * dz; yz += w * 3 * dy * dz;
                }
                Store(k, m, gx, gy, gz, xx, yy, zz, xy, xz, yz);
            }
            else Store(k, 0, cx[k], cy[k], cz[k], 0, 0, 0, 0, 0, 0);
            return;
        }
        var f = child[k];
        for (var c = f; c < f + 8; c++)
        {
            m += nm[c];
            sx += nm[c] * mx[c]; sy += nm[c] * my[c]; sz += nm[c] * mz[c];
        }
        if (!(m > 0))
        {
            Store(k, 0, cx[k], cy[k], cz[k], 0, 0, 0, 0, 0, 0);
            return;
        }
        double ux = sx / m, uy = sy / m, uz = sz / m;
        for (var c = f; c < f + 8; c++)
        {
            var w = nm[c];
            if (w == 0) continue;
            // Parallel-axis shift of each child's quadrupole to the parent's centre of mass.
            double dx = mx[c] - ux, dy = my[c] - uy, dz = mz[c] - uz;
            var r2 = dx * dx + dy * dy + dz * dz;
            xx += qxx[c] + w * (3 * dx * dx - r2); yy += qyy[c] + w * (3 * dy * dy - r2); zz += qzz[c] + w * (3 * dz * dz - r2);
            xy += qxy[c] + w * 3 * dx * dy; xz += qxz[c] + w * 3 * dx * dz; yz += qyz[c] + w * 3 * dy * dz;
        }
        Store(k, m, ux, uy, uz, xx, yy, zz, xy, xz, yz);
    }

    private void Store(int k, double m, double x, double y, double z, double xx, double yy, double zz, double xy, double xz, double yz)
    {
        nm[k] = m; mx[k] = x; my[k] = y; mz[k] = z;
        qxx[k] = xx; qyy[k] = yy; qzz[k] = zz; qxy[k] = xy; qxz[k] = xz; qyz[k] = yz;
        double dx = x - cx[k], dy = y - cy[k], dz = z - cz[k];
        delta[k] = Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    /// <summary>
    /// Acceleration at (x, y, z) from every particle in the tree: <paramref name="g"/> the gravitational constant in the
    /// caller's units, <paramref name="eps2"/> the squared Plummer softening, <paramref name="theta"/> the opening angle.
    /// A particle's own contribution vanishes by itself (zero separation) when ε &gt; 0.
    /// </summary>
    public Vec3 Accel(double x, double y, double z, double g, double eps2, double theta, bool quadrupole)
    {
        if (nodes == 0) return Vec3.Zero;
        double ax = 0, ay = 0, az = 0;
        var sp = 0;
        stack[sp++] = 0;
        var invTheta = 1 / theta;
        while (sp > 0)
        {
            var k = stack[--sp];
            var mk = nm[k];
            if (mk == 0) continue;
            double dx = x - mx[k], dy = y - my[k], dz = z - mz[k];
            var r2 = dx * dx + dy * dy + dz * dz;
            var lim = 2 * half[k] * invTheta + delta[k];
            if (r2 > lim * lim)
            {
                var rs2 = r2 + eps2;
                var inv = 1 / Math.Sqrt(rs2);
                var inv3 = inv / rs2;
                ax -= g * mk * dx * inv3; ay -= g * mk * dy * inv3; az -= g * mk * dz * inv3;
                if (quadrupole)
                {
                    var inv5 = inv3 / rs2;
                    var inv7 = inv5 / rs2;
                    var qdx = qxx[k] * dx + qxy[k] * dy + qxz[k] * dz;
                    var qdy = qxy[k] * dx + qyy[k] * dy + qyz[k] * dz;
                    var qdz = qxz[k] * dx + qyz[k] * dy + qzz[k] * dz;
                    var dqd = dx * qdx + dy * qdy + dz * qdz;
                    ax += g * (qdx * inv5 - 2.5 * dqd * dx * inv7);
                    ay += g * (qdy * inv5 - 2.5 * dqd * dy * inv7);
                    az += g * (qdz * inv5 - 2.5 * dqd * dz * inv7);
                }
            }
            else if (child[k] < 0)
            {
                int s = start[k], e = s + count[k];
                for (var p = s; p < e; p++)
                {
                    double ex = x - lx[p], ey = y - ly[p], ez = z - lz[p];
                    var rs2 = ex * ex + ey * ey + ez * ez + eps2;
                    if (rs2 <= 0) continue;
                    var inv3 = 1 / (rs2 * Math.Sqrt(rs2));
                    var gm = g * lm[p] * inv3;
                    ax -= gm * ex; ay -= gm * ey; az -= gm * ez;
                }
            }
            else
            {
                var f = child[k];
                for (var c = 0; c < 8; c++) stack[sp++] = f + c;
            }
        }
        return new Vec3(ax, ay, az);
    }
}
