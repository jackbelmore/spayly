namespace Spayly.Sim.Sph;

/// <summary>How <see cref="Clumps.Demote"/> finds and keeps clumps.</summary>
public sealed class DemoteOptions
{
    /// <summary>b in the linking length l = b × mean spacing, mean spacing = median (m/ρ)^{1/3}.</summary>
    public double LinkingFactor { get; set; } = 1.5;
    /// <summary>When &gt; 0, the linking length itself (state units), overriding <see cref="LinkingFactor"/>.</summary>
    public double LinkingLength { get; set; }
    /// <summary>Fewest particles a clump needs to become a body.</summary>
    public int MinParticles { get; set; } = 20;
    /// <summary>Least mass a clump needs to become a body, kg.</summary>
    public double MinMass { get; set; }
    /// <summary>Most clumps kept, largest first; the rest go to the remainder.</summary>
    public int MaxClumps { get; set; } = 16;
    /// <summary>Merge kept clumps whose spheres (radius from mass and mean density) overlap, so the bodies they become do not start in contact.</summary>
    public bool MergeOverlapping { get; set; } = true;
}

/// <summary>
/// One bound clump reduced to a body. State units (SI in the SPH frame): kg, m, m/s, kg m²/s, rad/s, kg/m³.
/// Its ledger is <see cref="Ledger.Point"/>(Mass, Position, Velocity, Spin).
/// </summary>
public sealed record SphClump
{
    public required double Mass { get; init; }
    /// <summary>Centre of mass.</summary>
    public required Vec3 Position { get; init; }
    /// <summary>Centre-of-mass velocity.</summary>
    public required Vec3 Velocity { get; init; }
    /// <summary>Angular momentum about the clump's own centre of mass, Σ m (r − R) × (v − V).</summary>
    public required Vec3 Spin { get; init; }
    /// <summary>ω = I⁻¹L with I the particles' inertia tensor about the centre of mass.</summary>
    public required Vec3 Omega { get; init; }
    /// <summary>Radius of a sphere of <see cref="Mass"/> at <see cref="MeanDensity"/>.</summary>
    public required double Radius { get; init; }
    /// <summary>Mass over the particles' SPH volume Σ m/ρ.</summary>
    public required double MeanDensity { get; init; }
    /// <summary>Mass contributed by each source body (index = <c>SphState.Body</c>).</summary>
    public required double[] MassBySource { get; init; }
    /// <summary>Mass of each material (index = <c>SphState.Mat</c>).</summary>
    public required double[] MassByMaterial { get; init; }
    /// <summary>Particle indices.</summary>
    public required int[] Members { get; init; }

    public Ledger Ledger => Ledger.Point(Mass, Position, Velocity, Spin);
}

/// <summary>Result of <see cref="Clumps.Demote"/>: kept clumps, and the remainder built as the exact difference.</summary>
public sealed class Demotion
{
    public required List<SphClump> Clumps { get; init; }
    /// <summary>Reference minus every clump's ledger: what the debris tier inherits.</summary>
    public required Ledger Remainder { get; init; }
    /// <summary>Particles in no kept clump.</summary>
    public required int[] RemainderMembers { get; init; }
    /// <summary>The totals the demotion conserves (the particles' own, unless the caller supplied truer ones).</summary>
    public required Ledger Reference { get; init; }
    public required double LinkingLength { get; init; }

    /// <summary>Σ clumps + remainder − reference; zero up to the rounding of one subtraction.</summary>
    public Ledger Closure
    {
        get
        {
            var sum = Remainder;
            foreach (var c in Clumps) sum += c.Ledger;
            return sum - Reference;
        }
    }
}

/// <summary>
/// Friends-of-friends grouping (Davis et al. 1985) and the conservative demotion of SPH particles to bodies (research
/// §2.4): each kept clump becomes a point with m = Σm, the centre-of-mass position and velocity, and spin L about its
/// centre of mass (ω = I⁻¹L); everything else is one remainder whose mass, first moment, momentum and angular momentum
/// are the reference totals minus the clumps', so Σm, Σmv and Σ(r × mv + L_spin) close exactly by construction.
/// </summary>
public static class Clumps
{
    /// <summary>Median (m/ρ)^{1/3} over particles with ρ &gt; 0, state length units.</summary>
    public static double MeanSpacing(SphState s)
    {
        var a = new List<double>(s.Count);
        for (var i = 0; i < s.Count; i++) if (s.Rho[i] > 0) a.Add(Math.Cbrt(s.M[i] / s.Rho[i]));
        if (a.Count == 0) return 0;
        a.Sort();
        return a[a.Count / 2];
    }

    /// <summary>
    /// Group label of every particle (0 = most massive group), linking any two closer than <paramref name="link"/>.
    /// <paramref name="groups"/> is the number of groups (singletons count).
    /// </summary>
    public static int[] FriendsOfFriends(SphState s, double link, out int groups)
    {
        var n = s.Count;
        var parent = new int[n];
        for (var i = 0; i < n; i++) parent[i] = i;
        int Find(int i)
        {
            while (parent[i] != i)
            {
                parent[i] = parent[parent[i]];
                i = parent[i];
            }
            return i;
        }
        if (link > 0 && n > 1)
        {
            var g = new UniformGrid();
            g.Build(n, s.X, s.Y, s.Z, null, link);
            var l2 = link * link;
            for (var i = 0; i < n; i++)
            {
                double xi = s.X[i], yi = s.Y[i], zi = s.Z[i];
                int ci = g.Cx[i], cj = g.Cy[i], ck = g.Cz[i];
                for (var dz = -1; dz <= 1; dz++)
                for (var dy = -1; dy <= 1; dy++)
                for (var dx = -1; dx <= 1; dx++)
                {
                    int a = ci + dx, b = cj + dy, c = ck + dz;
                    var key = UniformGrid.Key(a, b, c, g.Mask);
                    var e = g.Start[key] + g.Count[key];
                    for (var q = g.Start[key]; q < e; q++)
                    {
                        var j = g.Sorted[q];
                        if (j <= i || g.Cx[j] != a || g.Cy[j] != b || g.Cz[j] != c) continue;
                        double rx = xi - s.X[j], ry = yi - s.Y[j], rz = zi - s.Z[j];
                        if (rx * rx + ry * ry + rz * rz >= l2) continue;
                        int ri = Find(i), rj = Find(j);
                        if (ri != rj) parent[Math.Max(ri, rj)] = Math.Min(ri, rj);
                    }
                }
            }
        }
        var rootMass = new Dictionary<int, double>();
        for (var i = 0; i < n; i++)
        {
            var r = Find(i);
            rootMass[r] = rootMass.GetValueOrDefault(r) + s.M[i];
        }
        var order = rootMass.Keys.ToList();
        order.Sort((a, b) => rootMass[b] != rootMass[a] ? rootMass[b].CompareTo(rootMass[a]) : a.CompareTo(b));
        var label = new Dictionary<int, int>(order.Count);
        for (var k = 0; k < order.Count; k++) label[order[k]] = k;
        var result = new int[n];
        for (var i = 0; i < n; i++) result[i] = label[Find(i)];
        groups = order.Count;
        return result;
    }

    /// <summary>A clump's mass, centre of mass, spin, inertia-derived ω, SPH volume and composition from its members.</summary>
    public static SphClump Measure(SphState s, int[] members)
    {
        double m = 0, dx = 0, dy = 0, dz = 0, px = 0, py = 0, pz = 0, vol = 0;
        var sources = 1;
        foreach (var i in members) sources = Math.Max(sources, s.Body[i] + 1);
        var bySource = new double[sources];
        var byMat = new double[s.Materials.Length];
        foreach (var i in members)
        {
            var mi = s.M[i];
            m += mi;
            dx += mi * s.X[i]; dy += mi * s.Y[i]; dz += mi * s.Z[i];
            px += mi * s.Vx[i]; py += mi * s.Vy[i]; pz += mi * s.Vz[i];
            if (s.Rho[i] > 0) vol += mi / s.Rho[i];
            bySource[s.Body[i]] += mi;
            byMat[s.Mat[i]] += mi;
        }
        var r = new Vec3(dx / m, dy / m, dz / m);
        var v = new Vec3(px / m, py / m, pz / m);
        double lx = 0, ly = 0, lz = 0, ixx = 0, iyy = 0, izz = 0, ixy = 0, ixz = 0, iyz = 0;
        foreach (var i in members)
        {
            var mi = s.M[i];
            double x = s.X[i] - r.X, y = s.Y[i] - r.Y, z = s.Z[i] - r.Z;
            double u = s.Vx[i] - v.X, w = s.Vy[i] - v.Y, t = s.Vz[i] - v.Z;
            lx += mi * (y * t - z * w); ly += mi * (z * u - x * t); lz += mi * (x * w - y * u);
            ixx += mi * (y * y + z * z); iyy += mi * (x * x + z * z); izz += mi * (x * x + y * y);
            ixy -= mi * x * y; ixz -= mi * x * z; iyz -= mi * y * z;
        }
        var spin = new Vec3(lx, ly, lz);
        var omega = Solve3(ixx, iyy, izz, ixy, ixz, iyz, spin);
        var rho = vol > 0 ? m / vol : 0;
        var radius = rho > 0 ? Math.Cbrt(3 * m / (4 * Math.PI * rho)) : 0;
        return new SphClump
        {
            Mass = m, Position = r, Velocity = v, Spin = spin, Omega = omega, Radius = radius, MeanDensity = rho,
            MassBySource = bySource, MassByMaterial = byMat, Members = members,
        };
    }

    /// <summary>I⁻¹L for the symmetric tensor I by Cramer's rule; L/(tr I/3) when I is (near) singular, 0 when empty.</summary>
    private static Vec3 Solve3(double a, double b, double c, double d, double e, double f, Vec3 l)
    {
        // | a d e |
        // | d b f |
        // | e f c |
        var det = a * (b * c - f * f) - d * (d * c - f * e) + e * (d * f - b * e);
        var tr = (a + b + c) / 3;
        if (!(tr > 0)) return Vec3.Zero;
        if (Math.Abs(det) < 1e-9 * tr * tr * tr) return l * (1 / tr);
        var inv = 1 / det;
        var x = (l.X * (b * c - f * f) - d * (l.Y * c - f * l.Z) + e * (l.Y * f - b * l.Z)) * inv;
        var y = (a * (l.Y * c - f * l.Z) - l.X * (d * c - f * e) + e * (d * l.Z - l.Y * e)) * inv;
        var z = (a * (b * l.Z - l.Y * f) - d * (d * l.Z - l.Y * e) + l.X * (d * f - b * e)) * inv;
        return new Vec3(x, y, z);
    }

    /// <summary>
    /// Group by friends-of-friends, keep the clumps that pass <paramref name="options"/>, and put the rest in a remainder
    /// computed as <paramref name="reference"/> (default: the particles' own totals, <see cref="SphState.Totals"/>) minus
    /// every kept clump's ledger.
    /// </summary>
    public static Demotion Demote(SphState s, DemoteOptions? options = null, Ledger? reference = null)
    {
        var o = options ?? new DemoteOptions();
        var link = o.LinkingLength > 0 ? o.LinkingLength : o.LinkingFactor * MeanSpacing(s);
        var label = FriendsOfFriends(s, link, out var groups);
        var members = new List<int>[groups];
        for (var g = 0; g < groups; g++) members[g] = [];
        for (var i = 0; i < s.Count; i++) members[label[i]].Add(i);

        var kept = new List<SphClump>();
        for (var g = 0; g < groups && kept.Count < o.MaxClumps; g++)
        {
            var list = members[g];
            if (list.Count < o.MinParticles) continue;
            var c = Measure(s, [.. list]);
            if (c.Mass < o.MinMass) continue;
            kept.Add(c);
        }
        if (o.MergeOverlapping)
        {
            var merged = true;
            while (merged && kept.Count > 1)
            {
                merged = false;
                for (var a = 0; a < kept.Count && !merged; a++)
                for (var b = a + 1; b < kept.Count && !merged; b++)
                {
                    var ca = kept[a];
                    var cb = kept[b];
                    if (Vec3.Distance(ca.Position, cb.Position) >= ca.Radius + cb.Radius) continue;
                    kept[a] = Measure(s, [.. ca.Members, .. cb.Members]);
                    kept.RemoveAt(b);
                    merged = true;
                }
            }
            kept.Sort((x, y) => y.Mass.CompareTo(x.Mass));
        }

        var inClump = new bool[s.Count];
        foreach (var c in kept) foreach (var i in c.Members) inClump[i] = true;
        var rest = new List<int>();
        for (var i = 0; i < s.Count; i++) if (!inClump[i]) rest.Add(i);
        var refLedger = reference ?? s.Totals();
        var rem = refLedger;
        foreach (var c in kept) rem -= c.Ledger;
        return new Demotion { Clumps = kept, Remainder = rem, RemainderMembers = [.. rest], Reference = refLedger, LinkingLength = link };
    }
}
