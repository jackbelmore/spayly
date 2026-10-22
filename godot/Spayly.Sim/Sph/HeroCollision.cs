using static Spayly.Sim.Constants;

namespace Spayly.Sim.Sph;

/// <summary>Knobs of one hero collision. SPH time in seconds; everything else as documented.</summary>
public sealed class HeroOptions
{
    /// <summary>Total particle budget, split between the bodies in proportion to mass (equal-mass particles).</summary>
    public int Particles { get; set; } = 2000;
    /// <summary>Fewest particles either body gets.</summary>
    public int MinParticlesPerBody { get; set; } = 100;
    /// <summary>SPH time after which the collision is finished whatever its state, s.</summary>
    public double EndTime { get; set; } = 8 * Hour;
    /// <summary>The energy criterion is only consulted after this much SPH time, s.</summary>
    public double MinTime { get; set; } = 2 * Hour;
    /// <summary>Finished once the centre-of-mass-frame kinetic energy has fallen below this fraction of its value at contact.</summary>
    public double EnergyFraction { get; set; } = 0.05;
    /// <summary>Damped relaxation steps per body before contact, run on the CPU solver (0 = start from the hydrostatic profile).</summary>
    public int RelaxSteps { get; set; }
    /// <summary>Self-gravity; null = direct summation up to <see cref="DirectMax"/> particles (exact momentum), a tree above.</summary>
    public GravityMode? Gravity { get; set; }
    public int DirectMax { get; set; } = 4096;
    /// <summary>Seed of the shell rotations.</summary>
    public uint Seed { get; set; } = 1;
    /// <summary>Extra separation at contact, in units of the median h, so the first step sees no overlapping particles.</summary>
    public double ContactGap { get; set; } = 0.5;
    /// <summary>Solver constants; CellSize and Softening are filled from the built state by <see cref="HeroCollision.ResolvedOptions"/>.</summary>
    public SphOptions Solver { get; set; } = new();
    public DemoteOptions Demote { get; set; } = new();
}

/// <summary>When a collision is worth a hero SPH run: the planet-scale class.</summary>
public sealed class HeroTrigger
{
    /// <summary>Both bodies at least this big, km.</summary>
    public double MinRadiusKm { get; set; } = 1000;
    /// <summary>γ = M_p/M_t at least this.</summary>
    public double MinMassRatio { get; set; } = 0.005;
    /// <summary>
    /// Q_R at least this fraction of the mass-ratio-corrected Q*_RD (LS12). Small because giant impacts sit far below
    /// catastrophic disruption: the canonical Moon-forming impact is Q_R ≈ 0.035 Q*_RD, a lunar-mass body hitting Earth
    /// at the mutual escape speed ≈ 0.0011 Q*_RD.
    /// </summary>
    public double MinQFraction { get; set; } = 1e-3;
    public double CStar { get; set; } = CollisionRegimes.CStarStrengthless;
}

/// <summary>A demoted clump in the world frame: kg, km, km/s, kg km²/s, rad/s, kg/m³.</summary>
public sealed record DemotedBody
{
    public required double Mass { get; init; }
    public required Vec3 Position { get; init; }
    public required Vec3 Velocity { get; init; }
    /// <summary>Spin angular momentum about its own centre of mass, kg km²/s.</summary>
    public required Vec3 Spin { get; init; }
    /// <summary>Spin rate vector, rad/s, ecliptic axes.</summary>
    public required Vec3 Omega { get; init; }
    /// <summary>km, from the mass and the clump's mean SPH density.</summary>
    public required double Radius { get; init; }
    public required double MeanDensity { get; init; }
    /// <summary>kg from the target (0) and the impactor (1).</summary>
    public required double[] MassBySource { get; init; }
    /// <summary>kg of each material, indexed like the hero state's material table.</summary>
    public required double[] MassByMaterial { get; init; }
    public required int Particles { get; init; }

    public Ledger Ledger => Ledger.Point(Mass, Position, Velocity, Spin);
}

/// <summary>The outcome of a hero collision in world units, with its exact ledger.</summary>
public sealed class HeroDemotion
{
    public required List<DemotedBody> Bodies { get; init; }
    /// <summary>What the debris tier inherits: truth minus every body, world frame, kg · km · km/s · kg km²/s about the world origin.</summary>
    public required Ledger Remainder { get; init; }
    /// <summary>World positions (km), velocities (km/s) and masses (kg) of the particles in no body.</summary>
    public required Vec3[] RemainderPositions { get; init; }
    public required Vec3[] RemainderVelocities { get; init; }
    public required double[] RemainderMasses { get; init; }
    /// <summary>The totals the event must conserve: the pair (or its proxy) at demotion time plus the pair's internal angular momentum, orbital and spin.</summary>
    public required Ledger Truth { get; init; }
    /// <summary>The raw demotion in the SPH frame (SI, relative to the centre of mass).</summary>
    public required Demotion Sph { get; init; }
    /// <summary>The SPH run's own drift since contact, relative: |ΔΣm|/M, |ΔΣmv|/Σm|v|, |ΔL|/Σ|r × mv|. The remainder absorbs it.</summary>
    public required (double Mass, double Momentum, double AngularMomentum) Drift { get; init; }
    public required string[] MaterialNames { get; init; }

    /// <summary>Σ bodies + remainder − truth: zero to the rounding of the subtraction that built the remainder.</summary>
    public Ledger Closure
    {
        get
        {
            var sum = Remainder;
            foreach (var b in Bodies) sum += b.Ledger;
            return sum - Truth;
        }
    }
}

/// <summary>
/// A planet-scale collision run as a transient SPH patch, then demoted back to bodies plus a debris remainder.
///
/// The two <see cref="Body"/>s come in at contact (km, km/s). <see cref="Build"/> turns each into a hydrostatic Tillotson
/// body (<see cref="PlanetProfile"/>, materials from Kind and Surface), places them in the pair's centre-of-mass frame at
/// SPH contact distance, moved along their two-body conic so the pair's orbital energy and angular momentum are the
/// N-body values, and builds an SI <see cref="SphState"/>. <see cref="Step"/> advances it on the CPU solver (tests, and the
/// reference); the game advances the same state on the GPU and hands it back. <see cref="Demote"/> finds the clumps and
/// returns them in world units with a remainder built as the exact difference from the truth ledger, so mass, momentum
/// and angular momentum close exactly however much the SPH itself drifted.
/// </summary>
public sealed class HeroCollision
{
    public Body Target { get; }
    public Body Impactor { get; }
    public HeroOptions Options { get; }
    /// <summary>kg, at construction.</summary>
    public double TargetMass { get; }
    public double ImpactorMass { get; }
    /// <summary>The pair's centre of mass at contact, km and km/s.</summary>
    public Vec3 CmPosition { get; }
    public Vec3 CmVelocity { get; }
    /// <summary>Impactor minus target at contact, km and km/s.</summary>
    public Vec3 RelativePosition { get; }
    public Vec3 RelativeVelocity { get; }
    /// <summary>The pair's angular momentum about its centre of mass, μ r × v, kg km²/s.</summary>
    public Vec3 AngularMomentum { get; }
    /// <summary>
    /// The bodies' own spin angular momentum at contact, kg km²/s, from the rigid rotation <see cref="Build"/> gave their
    /// particles (ω from each body's IAU rotation, <see cref="SpinOf"/>). Part of the truth: the pair's orbital
    /// <see cref="AngularMomentum"/> is not all the L the event must conserve.
    /// </summary>
    public Vec3 SpinAngularMomentum { get; private set; }
    public PlanetProfile? TargetProfile { get; private set; }
    public PlanetProfile? ImpactorProfile { get; private set; }
    public SphState State { get; private set; } = null!;
    /// <summary>Particles of the target: indices [0, TargetParticles); the impactor's follow.</summary>
    public int TargetParticles { get; private set; }
    /// <summary>SPH time since contact, s. The CPU <see cref="Step"/> advances it; a GPU backend sets it.</summary>
    public double Time { get; set; }
    /// <summary>Centre-of-mass-frame kinetic energy at contact, J.</summary>
    public double InitialKinetic { get; private set; }
    /// <summary>Particle totals at contact (SI, centre-of-mass frame).</summary>
    public Ledger Initial { get; private set; }
    public (double Momentum, double AngularMomentum) InitialScales { get; private set; }
    public bool Built { get; private set; }

    private SphSolver? solver;

    /// <param name="a">One body at contact.</param>
    /// <param name="b">The other; the heavier of the two is the target.</param>
    /// <param name="posA">Contact position of <paramref name="a"/> (km) when known (a swept contact); else its Pos.</param>
    /// <param name="posB">Likewise for <paramref name="b"/>.</param>
    public HeroCollision(Body a, Body b, HeroOptions? options = null, Vec3? posA = null, Vec3? posB = null)
    {
        Options = options ?? new HeroOptions();
        var aIsTarget = a.Mass >= b.Mass;
        (Target, Impactor) = aIsTarget ? (a, b) : (b, a);
        var pt = (aIsTarget ? posA : posB) ?? Target.Pos;
        var pp = (aIsTarget ? posB : posA) ?? Impactor.Pos;
        TargetMass = Target.Mass;
        ImpactorMass = Impactor.Mass;
        var m = TargetMass + ImpactorMass;
        CmPosition = (pt * TargetMass + pp * ImpactorMass) * (1 / m);
        CmVelocity = (Target.Vel * TargetMass + Impactor.Vel * ImpactorMass) * (1 / m);
        RelativePosition = pp - pt;
        RelativeVelocity = Impactor.Vel - Target.Vel;
        AngularMomentum = Vec3.Cross(RelativePosition, RelativeVelocity) * (TargetMass * ImpactorMass / m);
    }

    /// <summary>Layers for a body from its Kind and Surface, innermost first; null for stars and gas giants (no Tillotson set fits).</summary>
    public static IReadOnlyList<Layer>? LayersFor(Body b)
    {
        if (b.Kind == BodyKind.Star || b.Surface is Surface.Gas or Surface.Star) return null;
        if (b.Surface == Surface.Ice) return [new(Materials.Basalt, 0.5), new(Materials.Ice, 0.5)];
        if (b.Kind == BodyKind.Planet) return [new(Materials.Iron, 0.3), new(Materials.Granite, 0.7)];
        if (b.Mass >= 1e22) return [new(Materials.Iron, 0.1), new(Materials.Granite, 0.9)];
        return [new(Materials.Basalt, 1.0)];
    }

    /// <summary>Specific energy on each layer's isentrope at ρ₀, J/kg: molten bodies start hot.</summary>
    public static double SurfaceEnergyFor(Body b) => b.Surface == Surface.Lava ? 2e6 : 0;

    /// <summary>
    /// Whether an impact between <paramref name="a"/> and <paramref name="b"/> is in the planet-scale class: rocky or icy,
    /// both at least <see cref="HeroTrigger.MinRadiusKm"/>, γ ≥ MinMassRatio and Q_R ≥ MinQFraction × Q*_RD (Leinhardt &amp;
    /// Stewart 2012 via <see cref="CollisionRegimes.Evaluate"/>). <paramref name="regime"/> is the LS12 evaluation.
    /// </summary>
    public static bool Qualifies(Body a, Body b, HeroTrigger trigger, out CollisionRegimeResult regime, Vec3? posA = null, Vec3? posB = null)
    {
        regime = default;
        if (LayersFor(a) == null || LayersFor(b) == null) return false;
        if (Math.Min(a.Radius, b.Radius) < trigger.MinRadiusKm) return false;
        var (t, p) = a.Mass >= b.Mass ? (a, b) : (b, a);
        if (p.Mass / t.Mass < trigger.MinMassRatio) return false;
        var rel = b.Vel - a.Vel;
        var speed = Vec3.Length(rel);
        if (!(speed > 0)) return false;
        var normal = Vec3.Normalize((posB ?? b.Pos) - (posA ?? a.Pos));
        var cos = Math.Abs(Vec3.Dot(rel, normal)) / speed;
        var bImpact = Math.Sqrt(Math.Max(0, 1 - cos * cos));
        regime = CollisionRegimes.Evaluate(t.Mass, p.Mass, t.Radius, p.Radius, speed, bImpact, trigger.CStar);
        return regime.QR >= trigger.MinQFraction * regime.QStarRD;
    }

    /// <summary>
    /// Where to put an impactor of <paramref name="mass"/> kg and <paramref name="radius"/> km so it touches
    /// <paramref name="target"/> along <paramref name="normal"/> (unit, from the target out), moving at
    /// <paramref name="speedOverVesc"/> × the mutual escape speed at <paramref name="angleDeg"/> from the line of centres
    /// (b = sin θ; 0 = head-on), tilted towards <paramref name="tangent"/>. The canonical Moon-forming impact (Canup 2004,
    /// Icarus 168:433) is a Mars-mass body at 45° and ≈ v_esc. Returns world position (km) and velocity (km/s).
    /// </summary>
    public static (Vec3 Pos, Vec3 Vel) ContactState(Body target, double mass, double radius, double angleDeg, double speedOverVesc, Vec3 normal, Vec3 tangent)
    {
        var n = Vec3.Normalize(normal);
        var t = Vec3.Normalize(tangent - n * Vec3.Dot(tangent, n));
        var sum = target.Radius + radius;
        var vEsc = Math.Sqrt(2 * G * (target.Mass + mass) / sum);
        var th = angleDeg * Deg;
        var v = (n * -Math.Cos(th) + t * Math.Sin(th)) * (speedOverVesc * vEsc);
        return (target.Pos + n * sum, target.Vel + v);
    }

    /// <summary>
    /// The pair state at separation <paramref name="d"/> on the same two-body conic as (<paramref name="r0"/>,
    /// <paramref name="v0"/>) with gravitational parameter <paramref name="mu"/>: same specific energy ½v² − μ/r and same
    /// angular momentum |r × v|, along the same direction and in the same plane, still approaching if it was.
    /// </summary>
    public static (Vec3 R, Vec3 V) AlongConic(Vec3 r0, Vec3 v0, double mu, double d)
    {
        var r = Vec3.Length(r0);
        var n = r0 * (1 / r);
        var vr = Vec3.Dot(v0, n);
        var vtVec = v0 - n * vr;
        var vt = Vec3.Length(vtVec);
        var t = vt > 0 ? vtVec * (1 / vt) : Vec3.Zero;
        var energy = 0.5 * Vec3.Dot(v0, v0) - mu / r;
        var vt1 = r * vt / d;
        var v2 = 2 * (energy + mu / d);
        var vr1 = Math.Sqrt(Math.Max(0, v2 - vt1 * vt1));
        if (vr < 0) vr1 = -vr1;
        return (n * d, n * vr1 + t * vt1);
    }

    /// <summary>
    /// A body's spin vector, rad/s in ecliptic axes, from its IAU rotation: |Rate| (deg/day) about the pole (RA, Dec), a
    /// negative rate spinning backwards about it, as the prime meridian W = W0 + Rate d turns. Zero without a rotation model.
    /// </summary>
    public static Vec3 SpinOf(Body b) =>
        b.Rotation is Rotation r ? Frames.PoleToEcliptic(r.PoleRA, r.PoleDec) * (r.Rate * Deg / Day) : Vec3.Zero;

    /// <summary>Build both bodies and place them at contact in the centre-of-mass frame (SI).</summary>
    public void Build()
    {
        var o = Options;
        var lt = LayersFor(Target) ?? throw new InvalidOperationException($"{Target.Name}: no Tillotson material for {Target.Kind}/{Target.Surface}");
        var lp = LayersFor(Impactor) ?? throw new InvalidOperationException($"{Impactor.Name}: no Tillotson material for {Impactor.Kind}/{Impactor.Surface}");
        var g = o.Solver.G;
        TargetProfile = PlanetProfile.Solve(TargetMass, lt, SurfaceEnergyFor(Target), g: g);
        ImpactorProfile = PlanetProfile.Solve(ImpactorMass, lp, SurfaceEnergyFor(Impactor), g: g);
        var total = Math.Max(o.Particles, 2 * o.MinParticlesPerBody);
        var m = TargetMass + ImpactorMass;
        var nt = (int)Math.Round(total * TargetMass / m);
        nt = Math.Clamp(nt, o.MinParticlesPerBody, total - o.MinParticlesPerBody);
        var np = total - nt;
        TargetParticles = nt;

        var mats = new List<Material>();
        int[] Map(IReadOnlyList<Layer> ls)
        {
            var idx = new int[ls.Count];
            for (var k = 0; k < ls.Count; k++)
            {
                var at = mats.IndexOf(ls[k].Material);
                if (at < 0) { at = mats.Count; mats.Add(ls[k].Material); }
                idx[k] = at;
            }
            return idx;
        }
        var mt = Map(lt);
        var mp = Map(lp);
        var s = new SphState(total, [.. mats]);
        var eta = o.Solver.Eta;
        PlaceBody(s, 0, TargetProfile, nt, 0, mt, o.Seed, eta);
        PlaceBody(s, nt, ImpactorProfile, np, 1, mp, unchecked(o.Seed * 7919u + 17u), eta);
        // Each body turns rigidly at its real rate before it is moved into place; the L that puts in is exact, not ω·I_profile.
        SpinAngularMomentum = (Spin(s, 0, nt, SpinOf(Target)) + Spin(s, nt, np, SpinOf(Impactor))) * 1e-6;

        // Contact: the two SPH surfaces a small gap apart, on the pair's own conic (SI).
        var href = SphSolver.MedianH(s);
        var d = TargetProfile.Radius + ImpactorProfile.Radius + o.ContactGap * href;
        var (r1, v1) = AlongConic(RelativePosition * 1e3, RelativeVelocity * 1e3, g * m, d);
        var ft = -ImpactorMass / m;
        var fp = TargetMass / m;
        Offset(s, 0, nt, r1 * ft, v1 * ft);
        Offset(s, nt, np, r1 * fp, v1 * fp);
        State = s;
        Initial = s.Totals();
        InitialScales = s.Scales();
        InitialKinetic = s.KineticEnergy();
        Time = 0;
        solver = null;
        Built = true;
    }

    private void PlaceBody(SphState s, int offset, PlanetProfile p, int n, int body, int[] mats, uint seed, double eta)
    {
        if (Options.RelaxSteps <= 0)
        {
            PlanetBuilder.Place(s, offset, p, n, body, mats, Rng.Mulberry32(seed), eta);
            return;
        }
        var one = PlanetBuilder.Build(p, n, seed, eta);
        var so = CloneSolverOptions(Options.Solver);
        so.Gravity = n <= Options.DirectMax ? GravityMode.Direct : GravityMode.Tree;
        PlanetBuilder.Relax(one, p, so, Options.RelaxSteps);
        for (var i = 0; i < n; i++)
        {
            var j = offset + i;
            s.X[j] = one.X[i]; s.Y[j] = one.Y[i]; s.Z[j] = one.Z[i];
            s.Vx[j] = 0; s.Vy[j] = 0; s.Vz[j] = 0;
            s.M[j] = one.M[i]; s.H[j] = one.H[i]; s.Rho[j] = one.Rho[i]; s.U[j] = one.U[i];
            s.Mat[j] = mats[one.Mat[i]]; s.Body[j] = body;
        }
    }

    /// <summary>Give particles [from, from + count) the rigid rotation ω (rad/s) about their centre of mass; returns their L, kg m²/s.</summary>
    private static Vec3 Spin(SphState s, int from, int count, Vec3 omega)
    {
        if (omega == Vec3.Zero) return Vec3.Zero;
        double m = 0, cx = 0, cy = 0, cz = 0;
        for (var i = from; i < from + count; i++)
        {
            m += s.M[i];
            cx += s.M[i] * s.X[i]; cy += s.M[i] * s.Y[i]; cz += s.M[i] * s.Z[i];
        }
        var c = new Vec3(cx / m, cy / m, cz / m);
        var l = Vec3.Zero;
        for (var i = from; i < from + count; i++)
        {
            var r = s.Position(i) - c;
            var v = Vec3.Cross(omega, r);
            s.Vx[i] += v.X; s.Vy[i] += v.Y; s.Vz[i] += v.Z;
            l += Vec3.Cross(r, v) * s.M[i];
        }
        return l;
    }

    private static void Offset(SphState s, int from, int count, Vec3 dr, Vec3 v)
    {
        for (var i = from; i < from + count; i++)
        {
            s.X[i] += dr.X; s.Y[i] += dr.Y; s.Z[i] += dr.Z;
            s.Vx[i] += v.X; s.Vy[i] += v.Y; s.Vz[i] += v.Z;
        }
    }

    private static SphOptions CloneSolverOptions(SphOptions o) => new()
    {
        Alpha = o.Alpha, Beta = o.Beta, Balsara = o.Balsara, Courant = o.Courant, AccelerationFactor = o.AccelerationFactor,
        GravityFactor = o.GravityFactor, Eta = o.Eta, PressureFloor = o.PressureFloor, Gravity = o.Gravity, G = o.G,
        Softening = o.Softening, Theta = o.Theta, Quadrupole = o.Quadrupole, CellSize = o.CellSize, HMinFraction = o.HMinFraction,
        DtMax = o.DtMax, DtGrowth = o.DtGrowth, FixedDt = o.FixedDt,
    };

    /// <summary>The solver options with gravity chosen and the grid cell (2 × median h) and softening (median h) fixed from the built state: what every backend must use.</summary>
    public SphOptions ResolvedOptions()
    {
        if (!Built) throw new InvalidOperationException("Build() first");
        var so = CloneSolverOptions(Options.Solver);
        so.Gravity = Options.Gravity ?? (State.Count <= Options.DirectMax ? GravityMode.Direct : GravityMode.Tree);
        var href = SphSolver.MedianH(State);
        if (so.CellSize <= 0) so.CellSize = 2 * href;
        if (so.Softening <= 0) so.Softening = href;
        return so;
    }

    /// <summary>The CPU solver on <see cref="State"/>, created on first use.</summary>
    public SphSolver Solver
    {
        get
        {
            if (solver == null)
            {
                solver = new SphSolver(State, ResolvedOptions()) { Time = Time };
                solver.Initialize();
            }
            return solver;
        }
    }

    /// <summary>Advance on the CPU by at least <paramref name="dtBudget"/> SPH seconds; returns the time advanced.</summary>
    public double Step(double dtBudget, int maxSteps = int.MaxValue)
    {
        var sv = Solver;
        var t = sv.Advance(dtBudget, maxSteps);
        Time = sv.Time;
        return t;
    }

    /// <summary>Past <see cref="HeroOptions.EndTime"/>, or past MinTime with the kinetic energy down to EnergyFraction of its contact value.</summary>
    public bool Finished =>
        Time >= Options.EndTime || (Time >= Options.MinTime && State.KineticEnergy() <= Options.EnergyFraction * InitialKinetic);

    /// <summary>
    /// <see cref="Demote(Vec3, Vec3, double?, int)"/> with the pair's centre of mass carried ballistically from contact for
    /// <see cref="Time"/> (no external field): for a patch run on its own, as the tests do.
    /// </summary>
    public HeroDemotion Demote() => Demote(CmPosition + CmVelocity * Time, CmVelocity);

    /// <summary>
    /// Clumps → bodies and the remainder, in world units. <paramref name="cmPosition"/>/<paramref name="cmVelocity"/> (km,
    /// km/s) are where the pair's centre of mass is now (a proxy body carried it through the N-body while the patch ran);
    /// <paramref name="mass"/> (kg, default the pair's) is the mass the Universe holds for it. Truth = that point mass plus
    /// the pair's internal angular momentum, orbital and spin; the remainder is truth minus the bodies. At most <paramref name="maxBodies"/>.
    /// </summary>
    public HeroDemotion Demote(Vec3 cmPosition, Vec3 cmVelocity, double? mass = null, int maxBodies = int.MaxValue)
    {
        var s = State;
        var o = Options.Demote;
        var opts = new DemoteOptions
        {
            LinkingFactor = o.LinkingFactor, LinkingLength = o.LinkingLength, MinParticles = o.MinParticles, MinMass = o.MinMass,
            MaxClumps = Math.Max(0, Math.Min(o.MaxClumps, maxBodies)), MergeOverlapping = o.MergeOverlapping,
        };
        var m = mass ?? TargetMass + ImpactorMass;
        var sphTruth = new Ledger(m, Vec3.Zero, Vec3.Zero, (AngularMomentum + SpinAngularMomentum) * 1e6);
        var raw = Clumps.Demote(s, opts, sphTruth);
        var bodies = new List<DemotedBody>(raw.Clumps.Count);
        foreach (var c in raw.Clumps)
        {
            bodies.Add(new DemotedBody
            {
                Mass = c.Mass, Position = cmPosition + c.Position * 1e-3, Velocity = cmVelocity + c.Velocity * 1e-3,
                Spin = c.Spin * 1e-6, Omega = c.Omega, Radius = c.Radius * 1e-3, MeanDensity = c.MeanDensity,
                MassBySource = c.MassBySource, MassByMaterial = c.MassByMaterial, Particles = c.Members.Length,
            });
        }
        var truth = Ledger.Point(m, cmPosition, cmVelocity, AngularMomentum + SpinAngularMomentum);
        var rem = truth;
        foreach (var b in bodies) rem -= b.Ledger;
        var rn = raw.RemainderMembers.Length;
        var rp = new Vec3[rn];
        var rv = new Vec3[rn];
        var rm = new double[rn];
        for (var k = 0; k < rn; k++)
        {
            var i = raw.RemainderMembers[k];
            rp[k] = cmPosition + s.Position(i) * 1e-3;
            rv[k] = cmVelocity + s.Velocity(i) * 1e-3;
            rm[k] = s.M[i];
        }
        var now = s.Totals();
        var (ps, ls) = InitialScales;
        var names = new string[s.Materials.Length];
        for (var k = 0; k < names.Length; k++) names[k] = s.Materials[k].Name;
        return new HeroDemotion
        {
            Bodies = bodies, Remainder = rem, RemainderPositions = rp, RemainderVelocities = rv, RemainderMasses = rm, Truth = truth, Sph = raw,
            Drift = (Math.Abs(now.Mass - Initial.Mass) / Initial.Mass,
                Vec3.Length(now.Momentum - Initial.Momentum) / Math.Max(ps, 1e-300),
                Vec3.Length(now.AngularMomentum - Initial.AngularMomentum) / Math.Max(ls, 1e-300)),
            MaterialNames = names,
        };
    }

    /// <summary>
    /// Bodies for a demotion, through <see cref="Body.MakeBody"/>. The body holding most of the target's mass inherits the
    /// target's id, name, kind, visuals, atmosphere and primary (so views, labels and moons stay attached); likewise the
    /// impactor's heir. The rest are new moons or fragments orbiting the target's heir. All start molten; spin becomes an
    /// IAU-style rotation (pole from ω, rate |ω|). <paramref name="time"/> is the Universe time, s.
    /// </summary>
    public static List<Body> CreateBodies(HeroDemotion d, Body target, Body impactor, double time, IReadOnlyList<Material>? materials = null)
    {
        var result = new List<Body>(d.Bodies.Count);
        int Heir(int source, double sourceMass, int exclude)
        {
            var best = -1;
            double bestMass = 0;
            for (var k = 0; k < d.Bodies.Count; k++)
            {
                if (k == exclude) continue;
                var ms = d.Bodies[k].MassBySource;
                var v = source < ms.Length ? ms[source] : 0;
                if (v > bestMass) { bestMass = v; best = k; }
            }
            return bestMass > 0.5 * sourceMass ? best : -1;
        }
        var th = Heir(0, target.Mass, -1);
        var ih = Heir(1, impactor.Mass, th);
        var floorTime = ((long)Math.Floor(time)).ToString(System.Globalization.CultureInfo.InvariantCulture);
        for (var k = 0; k < d.Bodies.Count; k++)
        {
            var b = d.Bodies[k];
            var source = k == th ? target : k == ih ? impactor : null;
            Rotation? rot = null;
            var w = Vec3.Length(b.Omega);
            if (w > 0)
            {
                var (ra, dec) = Frames.EclipticToPole(b.Omega);
                rot = new Rotation(ra, dec, 0, w * Day / Deg);
            }
            var color = source?.Visual.Color ?? ColorOf(b, materials);
            var body = Body.MakeBody(new BodySpec
            {
                Id = source?.Id ?? $"{target.Id}-sph-{floorTime}-{k}",
                Name = source?.Name ?? $"{target.Name} clump {k + 1}",
                Kind = source?.Kind ?? (b.Mass >= 1e21 ? BodyKind.Moon : BodyKind.Fragment),
                Surface = Surface.Lava,
                Mass = b.Mass,
                Radius = b.Radius,
                Pos = b.Position,
                Vel = b.Velocity,
                PrimaryId = source != null ? source.PrimaryId : th >= 0 ? target.Id : target.PrimaryId,
                Rotation = rot,
                Atmosphere = source?.Atmosphere,
                Visual = source?.Visual ?? new BodyVisual { Color = color, Albedo = 0.1 },
                Seed = source?.Seed,
                Born = time,
            });
            body.Damage.Magma = 1;
            body.Damage.MagmaTime = time;
            body.Damage.MagmaTau = Math.Clamp(Year * Math.Sqrt(b.Radius / 1000), Day, 100 * Year);
            result.Add(body);
        }
        return result;
    }

    private static string ColorOf(DemotedBody b, IReadOnlyList<Material>? materials)
    {
        if (materials == null || b.MassByMaterial.Length == 0) return "#8a6a55";
        var best = 0;
        for (var k = 1; k < b.MassByMaterial.Length; k++) if (b.MassByMaterial[k] > b.MassByMaterial[best]) best = k;
        if (best >= materials.Count) return "#8a6a55";
        var (r, g, bl) = materials[best].Color;
        static int C(double x) => (int)Math.Round(255 * Math.Clamp(Math.Pow(Math.Clamp(x, 0, 1), 1 / 2.2), 0, 1));
        return $"#{C(r):x2}{C(g):x2}{C(bl):x2}";
    }
}
