namespace Spayly.Sim;

/// <summary>
/// Massless test particles (asteroid belt, Trojans, Kuiper belt); mirrors src/sim/particles.ts. They feel every
/// body's gravity but exert none, and use a coarser leapfrog than the main bodies since they are decoration.
/// Structure of arrays: particle i is at [3i, 3i+1, 3i+2] in Pos/Vel/Acc (km, km/s, km/s^2).
/// </summary>
public sealed class TestParticles
{
    /// <summary>Particles in use; PopulateBelts lowers it to the number actually placed.</summary>
    public int Count { get; set; }
    /// <summary>km, ecliptic J2000, xyz interleaved.</summary>
    public double[] Pos { get; }
    /// <summary>km/s, xyz interleaved.</summary>
    public double[] Vel { get; }
    /// <summary>km/s^2, xyz interleaved.</summary>
    public double[] Acc { get; }
    /// <summary>1 alive, 0 swallowed by a body.</summary>
    public byte[] Alive { get; }
    /// <summary>0 main belt, 1 Trojan, 2 Kuiper belt.</summary>
    public byte[] Family { get; }
    /// <summary>s.</summary>
    public double MaxSubstep { get; set; } = 6 * 3600;
    public int MaxSubsteps { get; set; } = 10;
    /// <summary>Particles swallowed so far.</summary>
    public int Absorbed { get; set; }
    /// <summary>Bodies lighter than this (kg) are ignored for particle gravity (small asteroids).</summary>
    public double MassFloor { get; set; } = 1e19;

    // The TS filters into a fresh array per call; reusing one keeps the same order without the per-frame garbage.
    private readonly List<Body> active = [];

    public TestParticles(int count)
    {
        Count = count;
        Pos = new double[count * 3];
        Vel = new double[count * 3];
        Acc = new double[count * 3];
        Alive = new byte[count];
        Family = new byte[count];
    }

    public void ComputeAccelerations(IReadOnlyList<Body> bodies)
    {
        var pos = Pos;
        var acc = Acc;
        var alive = Alive;
        var count = Count;
        Array.Clear(acc);
        active.Clear();
        foreach (var b in bodies) if (b.Mass >= MassFloor) active.Add(b);
        for (var i = 0; i < count; i++)
        {
            if (alive[i] == 0) continue;
            var ix = i * 3;
            double px = pos[ix], py = pos[ix + 1], pz = pos[ix + 2];
            double ax = 0, ay = 0, az = 0;
            for (var k = 0; k < active.Count; k++)
            {
                var b = active[k];
                var bp = b.Pos;
                double dx = bp.X - px, dy = bp.Y - py, dz = bp.Z - pz;
                var r2 = dx * dx + dy * dy + dz * dz;
                // A black hole captures at 2 r_s and pulls by Paczyński–Wiita, G M/(r − r_s)², as it does the bodies.
                var hole = b.Kind == BodyKind.BlackHole;
                var contact = hole ? PseudoNewtonian.ContactRadius(b) : b.Radius;
                if (r2 < contact * contact)
                {
                    alive[i] = 0;
                    b.Damage.Absorbed++;
                    Absorbed++;
                    ax = 0; ay = 0; az = 0;
                    break;
                }
                var r = Math.Sqrt(r2);
                var g = hole ? Constants.G * b.Mass / (r * (r - PseudoNewtonian.HoleRs(b)) * (r - PseudoNewtonian.HoleRs(b))) : (Constants.G * b.Mass) / (r2 * r);
                ax += dx * g; ay += dy * g; az += dz * g;
            }
            acc[ix] = ax; acc[ix + 1] = ay; acc[ix + 2] = az;
        }
    }

    /// <summary>Advance every live particle by dt seconds (negative rewinds) against the bodies as they are now.</summary>
    public void Step(double dt, IReadOnlyList<Body> bodies)
    {
        if (dt == 0) return;
        var nsub = (int)Math.Min(MaxSubsteps, Math.Max(1, Math.Ceiling(Math.Abs(dt) / MaxSubstep)));
        var h = dt / nsub;
        var hh = h / 2;
        var pos = Pos;
        var vel = Vel;
        var acc = Acc;
        var alive = Alive;
        var count = Count;
        for (var s = 0; s < nsub; s++)
        {
            for (var i = 0; i < count * 3; i++) vel[i] += acc[i] * hh;
            for (var i = 0; i < count; i++)
            {
                if (alive[i] == 0) continue;
                var ix = i * 3;
                pos[ix] += vel[ix] * h; pos[ix + 1] += vel[ix + 1] * h; pos[ix + 2] += vel[ix + 2] * h;
            }
            ComputeAccelerations(bodies);
            for (var i = 0; i < count * 3; i++) vel[i] += acc[i] * hh;
        }
    }
}

/// <summary>How many particles of each family PopulateBelts places.</summary>
public readonly record struct BeltCounts(int Belt, int Trojan, int Kuiper)
{
    /// <summary>The TS default <c>{ belt: 2600, trojan: 700, kuiper: 900 }</c>.</summary>
    public static readonly BeltCounts Default = new(2600, 700, 900);
}

public static class Particles
{
    /// <summary>Rayleigh-distributed random value with the given scale (same unit as sigma).</summary>
    public static double Rayleigh(Func<double> rand, double sigma) => sigma * Math.Sqrt(-2 * Math.Log(1 - rand() + 1e-12));

    private static readonly (double Lo, double Hi)[] Gaps = [(2.48, 2.52), (2.80, 2.84), (2.93, 2.97), (3.25, 3.29)];

    /// <summary>
    /// Fill the particle set with the main belt (with Kirkwood gaps), Jupiter Trojans and a cold Kuiper belt. Draws
    /// from <see cref="Rng.Mulberry32"/> in the TS order, so the random sequence is the web build's bit for bit.
    /// </summary>
    public static void PopulateBelts(TestParticles p, Body sun, Body? jupiter, BeltCounts? counts = null, uint seed = 7)
    {
        var c = counts ?? BeltCounts.Default;
        var rand = Rng.Mulberry32(seed);
        var mu = Constants.G * sun.Mass;
        var i = 0;
        void Place(double a, double e, double inc, double Omega, double omega, double M, byte family)
        {
            if (i >= p.Count) return;
            var (r, v) = Kepler.ElementsToState(mu, new Elements(a, e, inc, Omega, omega, M));
            var ix = i * 3;
            p.Pos[ix] = r.X + sun.Pos.X; p.Pos[ix + 1] = r.Y + sun.Pos.Y; p.Pos[ix + 2] = r.Z + sun.Pos.Z;
            p.Vel[ix] = v.X + sun.Vel.X; p.Vel[ix + 1] = v.Y + sun.Vel.Y; p.Vel[ix + 2] = v.Z + sun.Vel.Z;
            p.Alive[i] = 1;
            p.Family[i] = family;
            i++;
        }
        var placed = 0;
        while (placed < c.Belt && i < p.Count)
        {
            var a = 2.1 + rand() * 1.2;
            if (Array.Exists(Gaps, g => a > g.Lo && a < g.Hi)) continue;
            // Argument order is evaluation order in C# as in JS, so rand() is drawn in the same sequence.
            Place(a * Constants.AU, Math.Min(0.32, Rayleigh(rand, 0.075)), Math.Min(25 * Constants.Deg, Rayleigh(rand, 6 * Constants.Deg)), rand() * 2 * Math.PI, rand() * 2 * Math.PI, rand() * 2 * Math.PI, 0);
            placed++;
        }
        if (jupiter != null)
        {
            // Trojans: same semi-major axis as Jupiter, mean longitude 60 degrees ahead/behind.
            var rel = new Vec3(jupiter.Pos.X - sun.Pos.X, jupiter.Pos.Y - sun.Pos.Y, jupiter.Pos.Z - sun.Pos.Z);
            var lonJ = Math.Atan2(rel.Y, rel.X);
            var aJ = 5.2028 * Constants.AU;
            for (var k = 0; k < c.Trojan && i < p.Count; k++)
            {
                var side = k % 2 == 0 ? 1 : -1;
                var lon = lonJ + side * 60 * Constants.Deg + (rand() - 0.5) * 24 * Constants.Deg;
                var e = Math.Min(0.2, Rayleigh(rand, 0.05));
                var inc = Math.Min(30 * Constants.Deg, Rayleigh(rand, 9 * Constants.Deg));
                var Omega = rand() * 2 * Math.PI;
                var omega = rand() * 2 * Math.PI;
                // choose M so that the mean longitude Omega+omega+M lands at lon
                var M = ((lon - Omega - omega) % (2 * Math.PI) + 2 * Math.PI) % (2 * Math.PI);
                Place(aJ * (1 + (rand() - 0.5) * 0.03), e, inc, Omega, omega, M, 1);
            }
        }
        for (var k = 0; k < c.Kuiper && i < p.Count; k++)
        {
            var a = 39 + rand() * 9;
            Place(a * Constants.AU, Math.Min(0.2, Rayleigh(rand, 0.04)), Math.Min(20 * Constants.Deg, Rayleigh(rand, 3 * Constants.Deg)), rand() * 2 * Math.PI, rand() * 2 * Math.PI, rand() * 2 * Math.PI, 2);
        }
        p.Count = i;
    }
}
