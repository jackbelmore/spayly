namespace Spayly.Sim.Debris;

/// <summary>
/// Double-double number: <see cref="Hi"/> + <see cref="Lo"/> with |Lo| ≤ ½ ulp(Hi), about 106 bits. The ledger keeps its
/// totals in it so that a planet's 1e24 kg and a crater's 1e15 kg can be added and taken away again without the small one
/// disappearing into the big one's rounding.
/// </summary>
public readonly record struct DD(double Hi, double Lo)
{
    public static readonly DD Zero = new(0, 0);

    public static implicit operator DD(double x) => new(x, 0);

    /// <summary>The value rounded to one double.</summary>
    public double Value => Hi + Lo;

    /// <summary>Knuth's TwoSum: s + e == a + b exactly.</summary>
    public static DD Sum(double a, double b)
    {
        var s = a + b;
        var bb = s - a;
        var e = (a - (s - bb)) + (b - bb);
        return new DD(s, e);
    }

    static DD QuickSum(double a, double b)
    {
        var s = a + b;
        return new DD(s, b - (s - a));
    }

    /// <summary>a·b exactly, through one fused multiply-add.</summary>
    public static DD Product(double a, double b)
    {
        var p = a * b;
        return new DD(p, Math.FusedMultiplyAdd(a, b, -p));
    }

    // The accurate (IEEE-style) addition: the sloppy one loses the low word under cancellation, which is exactly the
    // ledger's case (a body giving back what it lent).
    public static DD operator +(DD a, DD b)
    {
        var s = Sum(a.Hi, b.Hi);
        var t = Sum(a.Lo, b.Lo);
        var r = QuickSum(s.Hi, s.Lo + t.Hi);
        return QuickSum(r.Hi, r.Lo + t.Lo);
    }

    public static DD operator -(DD a) => new(-a.Hi, -a.Lo);
    public static DD operator -(DD a, DD b) => a + -b;

    public static DD operator *(DD a, double b)
    {
        var p = Product(a.Hi, b);
        return QuickSum(p.Hi, p.Lo + a.Lo * b);
    }

    public static DD Abs(DD a) => a.Hi < 0 || (a.Hi == 0 && a.Lo < 0) ? -a : a;
}

/// <summary>A 3-vector of <see cref="DD"/>: momentum (kg·km/s) and angular momentum (kg·km²/s) totals.</summary>
public readonly record struct DV(DD X, DD Y, DD Z)
{
    public static readonly DV Zero = new(DD.Zero, DD.Zero, DD.Zero);

    public static DV operator +(DV a, DV b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static DV operator -(DV a, DV b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static DV operator -(DV a) => new(-a.X, -a.Y, -a.Z);
    public static DV operator *(DV a, double s) => new(a.X * s, a.Y * s, a.Z * s);

    /// <summary>m·v, each component exact.</summary>
    public static DV Product(double m, Vec3 v) => new(DD.Product(m, v.X), DD.Product(m, v.Y), DD.Product(m, v.Z));

    /// <summary>r × p with r in doubles; exact up to the low words of p times r.</summary>
    public static DV Cross(Vec3 r, DV p) => new(
        p.Z * r.Y - p.Y * r.Z,
        p.X * r.Z - p.Z * r.X,
        p.Y * r.X - p.X * r.Y);

    public Vec3 Value => new(X.Value, Y.Value, Z.Value);

    public double Length
    {
        get
        {
            var v = Value;
            return Vec3.Length(v);
        }
    }
}

/// <summary>Mass (kg), momentum (kg·km/s) and angular momentum about the ledger's origin (kg·km²/s) of one tier or flow.</summary>
public readonly record struct Tally(DD Mass, DV Momentum, DV Angular)
{
    public static readonly Tally Zero = new(DD.Zero, DV.Zero, DV.Zero);

    public static Tally operator +(Tally a, Tally b) => new(a.Mass + b.Mass, a.Momentum + b.Momentum, a.Angular + b.Angular);
    public static Tally operator -(Tally a, Tally b) => new(a.Mass - b.Mass, a.Momentum - b.Momentum, a.Angular - b.Angular);
    public static Tally operator -(Tally a) => new(-a.Mass, -a.Momentum, -a.Angular);
    public static Tally operator *(Tally a, double s) => new(a.Mass * s, a.Momentum * s, a.Angular * s);

    /// <summary>
    /// What a point mass carries: <paramref name="mass"/> kg at <paramref name="pos"/> km moving at <paramref name="vel"/>
    /// km/s, angular momentum taken about <paramref name="origin"/> km.
    /// </summary>
    public static Tally Of(double mass, Vec3 pos, Vec3 vel, Vec3 origin)
    {
        var p = DV.Product(mass, vel);
        return new Tally(mass, p, DV.Cross(pos - origin, p));
    }

    public bool IsZero => Mass.Hi == 0 && Mass.Lo == 0 && Momentum == DV.Zero && Angular == DV.Zero;
}

/// <summary>Relative errors of <see cref="DebrisLedger.Total"/> against <see cref="DebrisLedger.Expected"/>.</summary>
/// <param name="Mass">|Δm| / expected mass.</param>
/// <param name="Momentum">|Δp| / the gross momentum the ledger has seen (initial Σ m|v| plus every flow's |p|).</param>
/// <param name="Angular">|ΔL| / the gross angular momentum the ledger has seen.</param>
public readonly record struct LedgerError(double Mass, double Momentum, double Angular);

/// <summary>
/// Exact book of mass, momentum and angular momentum across the debris tiers (docs/research/physics-and-visuals.md §2.4):
/// T0 = the bodies, T2 = test particles, T3 = a dust reservoir per body. Every operation moves a <see cref="Tally"/> from
/// one tier to another in double-double, so <c>Total == Initial + External</c> holds to ~1e-30 relative whatever the
/// magnitudes. Units: kg, km, km/s, like the rest of Sim; angular momentum about the fixed <see cref="Origin"/> (km), which
/// the caller sets to the system barycentre (the moving barycentre gives the same L, since its velocity is parallel to P).
///
/// Operations that touch a <see cref="Body"/> apply the change in doubles and book what the doubles actually took: the
/// sub-ulp remainder a body's mass or momentum cannot represent stays in that body's reservoir, so the book's T0 equals
/// the exact sum over the bodies, not merely an approximation of it.
///
/// What the book is not: T2's momentum is the momentum the particles carried at each hand-off. Between hand-offs they fall
/// under T0 gravity with no reaction on T0 (the test-particle approximation), so their true momentum drifts from the book;
/// the hand-offs themselves are exact.
/// </summary>
public sealed class DebrisLedger
{
    readonly Dictionary<Body, Tally> reservoirs = [];
    double momentumScale;
    double angularScale;

    public DebrisLedger(Vec3 origin) => Origin = origin;

    /// <summary>Point angular momentum is taken about, km.</summary>
    public Vec3 Origin { get; private set; }
    /// <summary>T0 as it was at <see cref="Baseline"/>.</summary>
    public Tally Initial { get; private set; }
    /// <summary>Everything added from outside the tiers since the baseline (a spawned asteroid, an erased body, a stress cloud).</summary>
    public Tally External { get; private set; }
    /// <summary>T0: the bodies.</summary>
    public Tally Bodies { get; private set; }
    /// <summary>T2: test particles, as handed over.</summary>
    public Tally Particles { get; private set; }
    /// <summary>T3: the sum of every reservoir.</summary>
    public Tally Dust { get; private set; }
    public IReadOnlyDictionary<Body, Tally> Reservoirs => reservoirs;

    public Tally Total => Bodies + Particles + Dust;
    public Tally Expected => Initial + External;

    /// <summary>Hand-off counts since the baseline.</summary>
    public long Injections { get; private set; }
    public long Absorptions { get; private set; }
    public long Dustings { get; private set; }
    public long Accretions { get; private set; }
    /// <summary>Gross mass that has left T0 for T2, kg.</summary>
    public double MassInjected { get; private set; }

    /// <summary>Exact tally of a set of bodies: Σ m, Σ m v, Σ (r − origin) × m v.</summary>
    public static Tally Measure(IReadOnlyList<Body> bodies, Vec3 origin)
    {
        var t = Tally.Zero;
        for (var i = 0; i < bodies.Count; i++)
        {
            var b = bodies[i];
            t += Tally.Of(b.Mass, b.Pos, b.Vel, origin);
        }
        return t;
    }

    /// <summary>Start a new book: T0 is the bodies as they are now, every other tier and every reservoir empty.</summary>
    public void Baseline(IReadOnlyList<Body> bodies, Vec3 origin)
    {
        Origin = origin;
        reservoirs.Clear();
        Initial = Bodies = Measure(bodies, origin);
        External = Particles = Dust = Tally.Zero;
        momentumScale = angularScale = 0;
        for (var i = 0; i < bodies.Count; i++)
        {
            var b = bodies[i];
            momentumScale += b.Mass * Vec3.Length(b.Vel);
            angularScale += b.Mass * Vec3.Length(Vec3.Cross(b.Pos - origin, b.Vel));
        }
        Injections = Absorptions = Dustings = Accretions = 0;
        MassInjected = 0;
    }

    void Scale(Tally t)
    {
        momentumScale += t.Momentum.Length;
        angularScale += t.Angular.Length;
    }

    /// <summary>Something entered (or, negative, left) T0 from outside the ledger: a spawned or erased body.</summary>
    public void AddExternal(Tally t)
    {
        External += t;
        Bodies += t;
        Scale(t);
    }

    /// <summary>Something entered T2 from outside the ledger (a stress-test cloud).</summary>
    public void AddExternalParticles(Tally t)
    {
        External += t;
        Particles += t;
        Scale(t);
    }

    /// <summary>
    /// Change <paramref name="b"/>'s mass by <paramref name="dm"/> and its momentum by <paramref name="dp"/> as closely as
    /// doubles allow, and return exactly what was applied. The new velocity is (M v + dp)/(M + dm). A change that would
    /// leave the body without mass is refused (nothing applied).
    /// </summary>
    static (DD Dm, DV Dp) Apply(Body b, DD dm, DV dp)
    {
        var m0 = b.Mass;
        var m1 = m0 + dm.Value;
        if (!(m1 > 0) || !double.IsFinite(m1)) return (DD.Zero, DV.Zero);
        var v0 = b.Vel;
        var p0 = DV.Product(m0, v0);
        var goal = (p0 + dp).Value;
        var v1 = new Vec3(goal.X / m1, goal.Y / m1, goal.Z / m1);
        b.Mass = m1;
        BlackHole.SyncHorizon(b);
        b.Vel = v1;
        return (DD.Sum(m1, -m0), DV.Product(m1, v1) - p0);
    }

    void AddToReservoir(Body home, Tally t)
    {
        reservoirs[home] = reservoirs.TryGetValue(home, out var r) ? r + t : t;
        Dust += t;
    }

    /// <summary>
    /// T0 → T2: particles carrying <paramref name="particles"/> leave <paramref name="source"/>, which loses that mass and
    /// momentum (the ejecta recoil). Returns what the body actually gave; the sub-ulp difference is booked against its
    /// reservoir.
    /// </summary>
    public (DD Dm, DV Dp) InjectFromBody(Body source, Tally particles)
    {
        Particles += particles;
        var (dm, dp) = Apply(source, -particles.Mass, -particles.Momentum);
        Bodies += new Tally(dm, dp, -particles.Angular);
        AddToReservoir(source, new Tally(-particles.Mass - dm, -particles.Momentum - dp, DV.Zero));
        Injections++;
        MassInjected += particles.Mass.Value;
        Scale(particles);
        return (dm, dp);
    }

    /// <summary>T0 → T2 where the bodies already lost the mass (Collide's LS12 particle tier): book only.</summary>
    public void InjectDetached(Tally particles)
    {
        Particles += particles;
        Bodies -= particles;
        Injections++;
        MassInjected += particles.Mass.Value;
        Scale(particles);
    }

    /// <summary>
    /// T0 → T3 where the bodies already lost the mass and no particle can carry it (the GPU tier is full or absent): it
    /// waits in <paramref name="home"/>'s reservoir and re-accretes. Book only.
    /// </summary>
    public void DustDetached(Body home, Tally dust)
    {
        Bodies -= dust;
        AddToReservoir(home, dust);
        Dustings++;
        Scale(dust);
    }

    /// <summary>
    /// T2 → T0: a particle of <paramref name="mass"/> kg at <paramref name="pos"/> km, moving at <paramref name="vel"/> km/s,
    /// is absorbed by <paramref name="target"/>: M += m, v = (M v + m v_p)/(M + m). Returns the flow that left T2.
    /// </summary>
    public Tally Absorb(Body target, double mass, Vec3 pos, Vec3 vel)
    {
        var flow = Tally.Of(mass, pos, vel, Origin);
        Particles -= flow;
        var (dm, dp) = Apply(target, flow.Mass, flow.Momentum);
        Bodies += new Tally(dm, dp, flow.Angular);
        AddToReservoir(target, new Tally(flow.Mass - dm, flow.Momentum - dp, DV.Zero));
        Absorptions++;
        Scale(flow);
        return flow;
    }

    /// <summary>T2 → T3: a particle outlived its lifetime or left the bounding radius; its tally joins <paramref name="home"/>'s reservoir.</summary>
    public Tally ToDust(Body home, double mass, Vec3 pos, Vec3 vel)
    {
        var flow = Tally.Of(mass, pos, vel, Origin);
        Particles -= flow;
        AddToReservoir(home, flow);
        Dustings++;
        Scale(flow);
        return flow;
    }

    /// <summary>
    /// T3 → T0: <paramref name="fraction"/> (clamped to 0..1) of <paramref name="home"/>'s reservoir falls onto it. The share
    /// the body's doubles cannot take stays behind. Returns what the body took.
    /// </summary>
    public (DD Dm, DV Dp) Accrete(Body home, double fraction)
    {
        if (!reservoirs.TryGetValue(home, out var r) || r.IsZero) return (DD.Zero, DV.Zero);
        var take = fraction >= 1 ? r : r * Math.Max(0, fraction);
        var (dm, dp) = Apply(home, take.Mass, take.Momentum);
        var applied = new Tally(dm, dp, take.Angular);
        reservoirs[home] = r - applied;
        Dust -= applied;
        Bodies += applied;
        Accretions++;
        return (dm, dp);
    }

    /// <summary>Fill <paramref name="into"/> with every body that has a reservoir (no enumerator boxing, so a frame can call it).</summary>
    public void ReservoirHomes(List<Body> into)
    {
        into.Clear();
        foreach (var kv in reservoirs) into.Add(kv.Key);
    }

    /// <summary>Hand <paramref name="from"/>'s reservoir to <paramref name="to"/> (the home body merged or was erased).</summary>
    public void MoveReservoir(Body from, Body to)
    {
        if (ReferenceEquals(from, to) || !reservoirs.Remove(from, out var r)) return;
        reservoirs[to] = reservoirs.TryGetValue(to, out var t) ? t + r : r;
    }

    /// <summary>
    /// A copy of the whole book, for undo. Reservoirs are keyed by Body reference, and an undo replaces every Body with a
    /// clone, so <paramref name="remap"/> (default: the same body) gives each reservoir's home in the copy; two homes that map
    /// to one body pool their reservoirs, as <see cref="MoveReservoir"/> would.
    /// </summary>
    public DebrisLedger Clone(Func<Body, Body>? remap = null)
    {
        var c = new DebrisLedger(Origin)
        {
            Initial = Initial, External = External, Bodies = Bodies, Particles = Particles, Dust = Dust,
            Injections = Injections, Absorptions = Absorptions, Dustings = Dustings, Accretions = Accretions, MassInjected = MassInjected,
            momentumScale = momentumScale, angularScale = angularScale,
        };
        foreach (var (home, t) in reservoirs)
        {
            var to = remap?.Invoke(home) ?? home;
            c.reservoirs[to] = c.reservoirs.TryGetValue(to, out var r) ? r + t : t;
        }
        return c;
    }

    public LedgerError Error()
    {
        var d = Total - Expected;
        var m = Expected.Mass.Value;
        return new LedgerError(
            m != 0 ? Math.Abs(d.Mass.Value) / Math.Abs(m) : Math.Abs(d.Mass.Value),
            d.Momentum.Length / Math.Max(momentumScale, double.Epsilon),
            d.Angular.Length / Math.Max(angularScale, double.Epsilon));
    }

    /// <summary>Gross momentum (kg·km/s) and angular momentum (kg·km²/s) the ledger has seen: the scales of <see cref="Error"/>.</summary>
    public (double Momentum, double Angular) Scales => (momentumScale, angularScale);
}
