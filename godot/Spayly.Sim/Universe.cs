using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Diagnostics;
namespace Spayly.Sim;

/// <param name="A">First body of the contact pair.</param>
/// <param name="B">Second body of the contact pair.</param>
/// <param name="Time">Universe time at the end of the substep that found the contact, s.</param>
/// <param name="PosA">Position of A at closest approach during the substep (km), when the swept test found the contact.</param>
/// <param name="PosB">Position of B at closest approach during the substep (km), when the swept test found the contact.</param>
public readonly record struct CollisionEvent(Body A, Body B, double Time, Vec3? PosA = null, Vec3? PosB = null);

public delegate void CollisionHandler(CollisionEvent ev, Universe u);

/// <summary>A deep copy of the bodies at a moment, for undo, and whatever state the app keeps alongside them.</summary>
public sealed class UniverseSnapshot
{
    /// <summary>s since EpochJd.</summary>
    public required double Time { get; init; }
    public required List<Body> Bodies { get; init; }
    /// <summary>What the point was taken before, for the player: "placing Black hole 2", "Earth's mass change".</summary>
    public required string Label { get; init; }
    public UndoKind Kind { get; init; } = UndoKind.Other;
    /// <summary>Id of the body the act was about (an impact's impactor), or null.</summary>
    public string? Subject { get; init; }
    /// <summary>
    /// The app's own state at the same moment (debris particles, discs, selection). Opaque to Sim, so Sim stays free of the
    /// engine: <see cref="Universe.AttachState"/> fills it, and the app reads it back after a restore.
    /// </summary>
    public object? Attachment { get; set; }
    /// <summary>Estimated RAM of <see cref="Bodies"/>, bytes.</summary>
    public long BodyBytes { get; init; }
    /// <summary>RAM the attachment holds, bytes, as the app measured it.</summary>
    public long AttachmentBytes { get; set; }
    /// <summary>GPU memory the attachment holds, bytes, as the app measured it.</summary>
    public long VramBytes { get; set; }
    /// <summary>RAM of the whole point, bytes.</summary>
    public long Bytes => BodyBytes + AttachmentBytes;
}

/// <summary>
/// Full mutual N-body gravity, integrated with kick-drift-kick leapfrog (symplectic, time reversible); mirrors
/// src/sim/universe.ts. Substeps adapt to the shortest dynamical timescale so close encounters stay accurate; a budget
/// on pair evaluations per frame throttles the time warp instead of losing accuracy.
///
/// The integrator works on flat double[]s packed from <see cref="Bodies"/> at the start of each step and written back
/// at the end, which keeps the inner loop free of object dereferences. km, kg, s throughout.
/// </summary>
public sealed partial class Universe : IWorld
{
    /// <summary>
    /// Smallest fraction of a pair's gravitational timescale that its crossing time is allowed to demand.
    /// At this ratio the flyby changes the relative velocity by about 2e-4 of itself.
    /// </summary>
    public const double EncounterFloor = 0.01;
    /// <summary>Step() calls without a collision that end a cascade; about half a second of frames.</summary>
    public const int HistoryQuietSteps = 30;

    private readonly record struct PendingPair(Body A, Body B, Vec3 PosA, Vec3 PosB);

    public List<Body> Bodies { get; private set; } = [];
    // The predictor reads a Universe directly, as aim.ts does, rather than paying for a snapshot per aim.
    IReadOnlyList<IWorldBody> IWorld.Bodies => Bodies;
    /// <summary>Seconds since EpochJd.</summary>
    public double Time { get; set; }
    public double EpochJd { get; set; }
    public int SubstepsLastFrame { get; private set; }
    public bool WarpLimited { get; private set; }
    public int MaxSubstepsPerFrame { get; set; } = 12000;
    /// <summary>
    /// Force evaluations allowed per call to Step(), as a proxy for wall-clock time: the substep budget is this divided
    /// by the number of pairs. PositiveInfinity here means "only MaxSubstepsPerFrame applies"; the app sets a real
    /// number so that frame time stays bounded as bodies are added.
    /// </summary>
    public double MaxPairEvalsPerFrame { get; set; } = double.PositiveInfinity;
    /// <summary>
    /// Wall-clock milliseconds one Step() may spend before it stops and reports <see cref="WarpLimited"/>. Pair evaluations
    /// only proxy for time (a pair costs 12-25 ns depending on the machine and on what else the frame is doing), so the
    /// app budgets the thing it cares about directly and lets a faster machine reach a higher warp. PositiveInfinity keeps
    /// Step() a pure function of its input, which the tests and the predictors rely on; only the app turns this on.
    /// </summary>
    public double MaxStepMs { get; set; } = double.PositiveInfinity;
    /// <summary>Force pair evaluations the last Step() spent, collision re-packs included.</summary>
    public double PairEvalsLastFrame { get; private set; }
    /// <summary>
    /// Ceiling on how many bodies may exist. Every shatter can add eighteen and each fragment can be shattered again,
    /// so without a stop a few shots breed thousands; fragmentation folds the excess back into the remnant instead,
    /// which still conserves mass and momentum exactly.
    /// </summary>
    public int MaxBodies { get; set; } = 240;
    /// <summary>s.</summary>
    public double MaxSubstep { get; set; } = 4 * 3600;
    /// <summary>s.</summary>
    public double MinSubstep { get; set; } = 0.25;
    /// <summary>
    /// Floor on the substep, s, when the shortest timescale belongs to a pair with a black hole. Near a stellar-mass hole the
    /// orbital time is milliseconds; held at <see cref="MinSubstep"/> a plunge overshoots contact by several times c. The
    /// warp throttle pays for it while such a pair is close, and hole-free systems never see it.
    /// </summary>
    public double HoleMinSubstep { get; set; } = 1e-6;
    /// <summary>Fraction of the shortest dynamical time used per substep.</summary>
    public double DynFraction { get; set; } = 0.02;
    public TestParticles? Particles { get; set; }
    public List<CollisionHandler> CollisionHandlers { get; } = [];
    /// <summary>Undo points (enumerates oldest first) and redo points; see <see cref="UndoHistory"/>.</summary>
    public UndoHistory History { get; } = new();
    /// <summary>
    /// Called on every undo point this universe records (a collision's, a forced one, the state an undo or redo replaces) to
    /// set its <see cref="UniverseSnapshot.Attachment"/> and byte counts. The app injects it, as it injects Collide's
    /// <c>BodyFrame</c>: Sim never learns what the attachment is.
    /// </summary>
    public Action<UniverseSnapshot>? AttachState { get; set; }
    /// <summary>Calls to Step() so far: the clock history coalescing counts in, since sim has no wall clock.</summary>
    public int Steps { get; private set; }
    /// <summary>The Steps value at the last PushHistory; NegativeInfinity until the first (and after Restore).</summary>
    public double LastCollisionStep { get; private set; } = double.NegativeInfinity;

    private List<PendingPair> pending = [];
    // ResolveCollisions swaps this with `pending` (the TS assigns a fresh array) so a collision frame allocates nothing.
    private List<PendingPair> resolving = [];
    private double dynTime = double.PositiveInfinity;
    private bool dynFromHole;
    private readonly Dictionary<string, Body> byIdMap = [];

    private int n;
    private int capacity;
    private double[] px = [], py = [], pz = [];
    private double[] vx = [], vy = [], vz = [];
    private double[] ax = [], ay = [], az = [];
    private double[] mass = [], rad = [];
    // Paczyński–Wiita r_s each body adds to its pairs (0 unless a black hole), and whether any is non-zero this pack.
    private double[] hrs = [];
    private bool anyHole;

    public Universe(double epochJd)
    {
        EpochJd = epochJd;
    }

    /// <summary>Julian date of the current time.</summary>
    public double Jd => EpochJd + Time / 86400;

    public void Add(Body b)
    {
        Bodies.Add(b);
        byIdMap[b.Id] = b;
    }

    public void Remove(Body b)
    {
        var i = Bodies.IndexOf(b);
        if (i >= 0) Bodies.RemoveAt(i);
        byIdMap.Remove(b.Id);
    }

    public Body? ById(string id) => byIdMap.GetValueOrDefault(id);

    public void SetBodies(List<Body> bodies)
    {
        Bodies = bodies;
        byIdMap.Clear();
        foreach (var b in bodies) byIdMap[b.Id] = b;
    }

    /// <summary>How many more bodies may be created before MaxBodies is reached.</summary>
    public int RoomForBodies => Math.Max(0, MaxBodies - Bodies.Count);

    /// <summary>Shortest dynamical timescale found by the last force evaluation, seconds.</summary>
    public double ShortestDynamicalTime => dynTime;

    private void Pack()
    {
        var bs = Bodies;
        var count = n = bs.Count;
        if (count > capacity)
        {
            var cap = Math.Max(16, count * 2);
            capacity = cap;
            px = new double[cap]; py = new double[cap]; pz = new double[cap];
            vx = new double[cap]; vy = new double[cap]; vz = new double[cap];
            ax = new double[cap]; ay = new double[cap]; az = new double[cap];
            mass = new double[cap]; rad = new double[cap];
            hrs = new double[cap];
        }
        anyHole = false;
        for (var i = 0; i < count; i++)
        {
            var b = bs[i];
            var p = b.Pos;
            var v = b.Vel;
            px[i] = p.X; py[i] = p.Y; pz[i] = p.Z;
            vx[i] = v.X; vy[i] = v.Y; vz[i] = v.Z;
            mass[i] = b.Mass;
            // A hole is contacted (captured) at 2 r_s, the marginally bound radius, not at the horizon its Radius holds.
            rad[i] = PseudoNewtonian.ContactRadius(b);
            hrs[i] = PseudoNewtonian.HoleRs(b);
            if (hrs[i] > 0) anyHole = true;
        }
        Accelerate(0);
        hierarchyDirty = true;
        slowValid = false;
    }

    private void Unpack()
    {
        var bs = Bodies;
        for (var i = 0; i < n; i++)
        {
            var b = bs[i];
            b.Pos = new Vec3(px[i], py[i], pz[i]);
            b.Vel = new Vec3(vx[i], vy[i], vz[i]);
            b.Acc = new Vec3(ax[i], ay[i], az[i]);
        }
    }

    /// <summary>
    /// Accelerations and the shortest dynamical time. <paramref name="driftH"/> non-zero also detects contacts swept out by that drift.
    /// Gravity is Newton's for every pair without a black hole and Paczyński–Wiita (<see cref="PseudoNewtonian"/>) for a pair
    /// with one: G M/(r − r_s)², the separation softened to contact, which for a hole is its 2 r_s capture radius, so r − r_s ≥ r_s.
    /// </summary>
    private void Accelerate(double driftH) => Accelerate(driftH, default(AllPairs));

    /// <summary>
    /// Which pairs a force pass visits. A struct type parameter, not a delegate, so the JIT compiles the flat pass with the
    /// filter folded away and the integrator every golden covers costs nothing for the hierarchical one's existence.
    /// </summary>
    private interface IPairFilter
    {
        /// <summary>Leave the pair out of the force sum and the shortest dynamical time altogether.</summary>
        bool Skip(int i, int j);
        /// <summary>Test the pair for a contact swept out by the drift.</summary>
        bool Sweep(int i, int j);
    }

    private readonly struct AllPairs : IPairFilter
    {
        public bool Skip(int i, int j) => false;
        public bool Sweep(int i, int j) => true;
    }

    private void Accelerate<TFilter>(double driftH, TFilter filter) where TFilter : struct, IPairFilter
    {
        double[] px = this.px, py = this.py, pz = this.pz, vx = this.vx, vy = this.vy, vz = this.vz;
        double[] ax = this.ax, ay = this.ay, az = this.az, mass = this.mass, rad = this.rad, hrs = this.hrs;
        var anyHole = this.anyHole;
        // The j loop reads twelve arrays by an index the JIT cannot prove in range; every one is at least n long (Pack), and
        // the checks and the reloads of rad[i] cost about a fifth of the loop (12.7 to 10.2 ns a pair). Same loads, same arithmetic, same bits.
        ref double rpx = ref MemoryMarshal.GetArrayDataReference(px);
        ref double rpy = ref MemoryMarshal.GetArrayDataReference(py);
        ref double rpz = ref MemoryMarshal.GetArrayDataReference(pz);
        ref double rvx = ref MemoryMarshal.GetArrayDataReference(vx);
        ref double rvy = ref MemoryMarshal.GetArrayDataReference(vy);
        ref double rvz = ref MemoryMarshal.GetArrayDataReference(vz);
        ref double rax = ref MemoryMarshal.GetArrayDataReference(ax);
        ref double ray = ref MemoryMarshal.GetArrayDataReference(ay);
        ref double raz = ref MemoryMarshal.GetArrayDataReference(az);
        ref double rmass = ref MemoryMarshal.GetArrayDataReference(mass);
        ref double rrad = ref MemoryMarshal.GetArrayDataReference(rad);
        ref double rhrs = ref MemoryMarshal.GetArrayDataReference(hrs);
        var n = this.n;
        for (var i = 0; i < n; i++) { ax[i] = 0; ay[i] = 0; az[i] = 0; }
        var minDyn = double.PositiveInfinity;
        var minDynMargin2 = double.PositiveInfinity;
        var minFromHole = false;
        var detect = driftH != 0;
        var absDrift = driftH < 0 ? -driftH : driftH;
        for (var i = 0; i < n; i++)
        {
            double pix = px[i], piy = py[i], piz = pz[i];
            double vix = vx[i], viy = vy[i], viz = vz[i];
            var mi = mass[i];
            var radI = rad[i];
            var hrsI = anyHole ? hrs[i] : 0;
            double aix = ax[i], aiy = ay[i], aiz = az[i];
            for (var j = i + 1; j < n; j++)
            {
                if (filter.Skip(i, j)) continue;
                double dx = Unsafe.Add(ref rpx, j) - pix, dy = Unsafe.Add(ref rpy, j) - piy, dz = Unsafe.Add(ref rpz, j) - piz;
                var r2 = dx * dx + dy * dy + dz * dz;
                var sum = radI + Unsafe.Add(ref rrad, j);
                // Point-mass gravity is only right outside the bodies. Softening the force at contact costs
                // nothing for a pair that is not touching and keeps a head-on hit, where the centres land on
                // top of each other mid-substep, from firing an infinite kick before the contact resolves.
                var sum2 = sum * sum;
                var rs2 = r2 > sum2 ? r2 : sum2;
                var rs = Math.Sqrt(rs2);
                var inv3 = 1 / (rs2 * rs);
                // Paczyński–Wiita: G M/(r (r − r_s)²) per unit separation vector, still equal and opposite.
                var hole = anyHole ? hrsI + Unsafe.Add(ref rhrs, j) : 0;
                if (hole > 0)
                {
                    var e = rs - hole;
                    inv3 = 1 / (e * e * rs);
                }
                var gi = Constants.G * Unsafe.Add(ref rmass, j) * inv3;
                var gj = Constants.G * mi * inv3;
                aix += dx * gi; aiy += dy * gi; aiz += dz * gi;
                Unsafe.Add(ref rax, j) -= dx * gj; Unsafe.Add(ref ray, j) -= dy * gj; Unsafe.Add(ref raz, j) -= dz * gj;
                double dvx = Unsafe.Add(ref rvx, j) - vix, dvy = Unsafe.Add(ref rvy, j) - viy, dvz = Unsafe.Add(ref rvz, j) - viz;
                // Two holes also radiate gravitational waves: the 2.5PN reaction force, equal and opposite (GravitationalWaves).
                if (hole > 0 && hrsI > 0 && Unsafe.Add(ref rhrs, j) > 0)
                {
                    var (rx, ry, rz) = GravitationalWaves.RadiationReaction(dx, dy, dz, dvx, dvy, dvz, rs, mi, Unsafe.Add(ref rmass, j));
                    var M = mi + Unsafe.Add(ref rmass, j);
                    var fi = Unsafe.Add(ref rmass, j) / M;
                    var fj = mi / M;
                    aix -= rx * fi; aiy -= ry * fi; aiz -= rz * fi;
                    Unsafe.Add(ref rax, j) += rx * fj; Unsafe.Add(ref ray, j) += ry * fj; Unsafe.Add(ref raz, j) += rz * fj;
                }
                var vrel2 = dvx * dvx + dvy * dvy + dvz * dvz;
                var mu = Constants.G * (mi + Unsafe.Add(ref rmass, j));
                // Two of this loop's three square roots only matter for the few pairs that can still lower minDyn or touch
                // within this drift, and almost every pair is neither. Each test below is a squared-quantity bound with a
                // 1e-9 margin, a million times the rounding of the bound itself, so a pair is skipped only when the exact
                // path would certainly not have changed minDyn or queued a contact: the results stay bit-identical.
                var needDyn = mu > 0;
                if (needDyn && hole == 0)
                {
                    var rs3 = rs2 * rs;
                    if (rs3 > minDynMargin2 * mu                                 // grav > minDyn
                        && (EncounterFloor * EncounterFloor * rs3 > minDynMargin2 * mu   // significant >= grav*floor > minDyn
                            || vrel2 == 0 || rs2 > minDynMargin2 * vrel2))                // or cross = rs/vrel > minDyn
                        needDyn = false;
                }
                var needReach = detect && filter.Sweep(i, j) && !(r2 > 2 * (sum2 + vrel2 * absDrift * absDrift) * 1.000000001);
                var vrel = needDyn || needReach ? Math.Sqrt(vrel2) : 0;
                if (needDyn)
                {
                    // Gravitational timescale of the pair: how long it takes to bend each other's path; √(r/a) under PW,
                    // which shrinks towards the hole faster than Newton's √(r³/μ).
                    var grav = hole > 0 ? Math.Sqrt(rs * (rs - hole) * (rs - hole) / mu) : Math.Sqrt((rs2 * rs) / mu);
                    var pair = grav;
                    if (vrel > 0)
                    {
                        // A pair also has to be sampled as it flies past, but only as finely as the deflection
                        // deserves: over one crossing time the relative velocity changes by ~2(cross/grav)^2 of
                        // itself, so an encounter far faster than the pair's own gravity bends almost nothing and
                        // does not need resolving. Without this floor a cloud of fragments drifting past each
                        // other pins the substep at its minimum and the clock all but stops.
                        var cross = rs / vrel;
                        var significant = cross > grav * EncounterFloor ? cross : grav * EncounterFloor;
                        if (significant < pair) pair = significant;
                    }
                    if (pair < minDyn)
                    {
                        minDyn = pair;
                        minDynMargin2 = pair * pair * 1.000000001;
                        minFromHole = hole > 0;
                    }
                }
                // Closest approach along the straight line each body travelled during the drift. Without
                // this a small fast body slips through a small target between two samples of its position.
                // The pair can only touch if it is within one drift's worth of closing distance, which
                // rejects almost every pair before the swept test.
                var reach = sum + vrel * absDrift;
                if (needReach && r2 < reach * reach)
                {
                    double ux = dvx * driftH, uy = dvy * driftH, uz = dvz * driftH;
                    double ex = dx - ux, ey = dy - uy, ez = dz - uz;
                    var uu = ux * ux + uy * uy + uz * uz;
                    double s = 1;
                    if (uu > 0)
                    {
                        s = -(ex * ux + ey * uy + ez * uz) / uu;
                        s = s < 0 ? 0 : s > 1 ? 1 : s;
                    }
                    double cx = ex + s * ux, cy = ey + s * uy, cz = ez + s * uz;
                    if (cx * cx + cy * cy + cz * cz < sum * sum)
                    {
                        var back = (1 - s) * driftH;
                        pending.Add(new PendingPair(
                            Bodies[i], Bodies[j],
                            new Vec3(pix - vix * back, piy - viy * back, piz - viz * back),
                            new Vec3(Unsafe.Add(ref rpx, j) - Unsafe.Add(ref rvx, j) * back, Unsafe.Add(ref rpy, j) - Unsafe.Add(ref rvy, j) * back, Unsafe.Add(ref rpz, j) - Unsafe.Add(ref rvz, j) * back)));
                    }
                }
            }
            ax[i] = aix; ay[i] = aiy; az[i] = aiz;
        }
        dynTime = minDyn;
        dynFromHole = minFromHole;
    }

    /// <summary>Advance by dt seconds (negative rewinds). Returns the time actually advanced, s.</summary>
    public double Step(double dt)
    {
        Steps++;
        SubstepsLastFrame = 0;
        WarpLimited = false;
        pending.Clear();
        Pack();
        var remaining = dt;
        var sign = dt < 0 ? -1 : 1;
        // The budget is counted in pair evaluations as they are spent, not fixed from n up front: a
        // shatter mid-frame adds up to 17 bodies, and every substep after it costs the new, larger n
        // squared. Sized once from the old n, the rest of that frame overran its budget about twofold.
        // At constant n this stops on exactly the substep the old fixed count did.
        PairEvalsLastFrame = 0;
        HierarchicalStepsLastFrame = 0;
        HierarchyAbortsLastFrame = 0;
        var clocked = !double.IsPositiveInfinity(MaxStepMs);
        var stepStart = clocked ? Stopwatch.GetTimestamp() : 0;
        while (Math.Abs(remaining) > 1e-6)
        {
            // Computed in double as JS does: n * (n - 1) overflows int past n = 46341.
            var pairs = Math.Max(1, ((double)n * (n - 1)) / 2);
            var overBudget = SubstepsLastFrame >= 8 && (PairEvalsLastFrame + pairs > MaxPairEvalsPerFrame
                || (clocked && Stopwatch.GetElapsedTime(stepStart).TotalMilliseconds > MaxStepMs));
            if (SubstepsLastFrame >= MaxSubstepsPerFrame || overBudget)
            {
                WarpLimited = true;
                break;
            }
            double h;
            if (Hierarchical && TryHierarchicalStep(remaining, sign, out h))
            {
                // PairEvalsLastFrame already holds what it spent.
            }
            else
            {
                h = Math.Min(MaxSubstep, Math.Max(dynFromHole ? HoleMinSubstep : MinSubstep, DynFraction * dynTime));
                if (h > Math.Abs(remaining)) h = Math.Abs(remaining);
                h *= sign;
                Substep(h);
                slowValid = false;
                PairEvalsLastFrame += pairs;
            }
            Time += h;
            remaining -= h;
            SubstepsLastFrame++;
            if (pending.Count > 0)
            {
                if (abortOnContact)
                {
                    // A group's inner integrator: the parent restores its start state and lets the flat step handle it.
                    contactAborted = true;
                    pending.Clear();
                    break;
                }
                ResolveCollisions();
                // ResolveCollisions re-packs, and Pack() ends with a full force evaluation at the new n.
                PairEvalsLastFrame += Math.Max(1, ((double)n * (n - 1)) / 2);
            }
        }
        Unpack();
        var advanced = dt - remaining;
        Particles?.Step(advanced, Bodies);
        return advanced;
    }

    private void Substep(double h)
    {
        double[] px = this.px, py = this.py, pz = this.pz, vx = this.vx, vy = this.vy, vz = this.vz;
        double[] ax = this.ax, ay = this.ay, az = this.az;
        var n = this.n;
        var hh = h / 2;
        for (var i = 0; i < n; i++) { vx[i] += ax[i] * hh; vy[i] += ay[i] * hh; vz[i] += az[i] * hh; }
        for (var i = 0; i < n; i++) { px[i] += vx[i] * h; py[i] += vy[i] * h; pz[i] += vz[i] * h; }
        Accelerate(h);
        for (var i = 0; i < n; i++) { vx[i] += ax[i] * hh; vy[i] += ay[i] * hh; vz[i] += az[i] * hh; }
    }

    private void ResolveCollisions()
    {
        var pairs = pending;
        pending = resolving;
        resolving = pairs;
        Unpack();
        var seen = new HashSet<string>();
        foreach (var (a, b, posA, posB) in pairs)
        {
            if (!byIdMap.ContainsKey(a.Id) || !byIdMap.ContainsKey(b.Id)) continue;
            var key = a.Id + "|" + b.Id;
            if (!seen.Add(key)) continue;
            // By index, not foreach: JS for..of over a live array visits handlers appended mid-loop, where a C#
            // List enumerator would throw.
            for (var k = 0; k < CollisionHandlers.Count; k++) CollisionHandlers[k](new CollisionEvent(a, b, Time, posA, posB), this);
        }
        pairs.Clear();
        Pack();
    }

    public UniverseSnapshot Snapshot(string label = "", UndoKind kind = UndoKind.Other, string? subject = null)
    {
        var bodies = CloneBodies(Bodies);
        long bytes = 0;
        foreach (var b in bodies) bytes += EstimateBytes(b);
        return new UniverseSnapshot { Time = Time, Label = label, Kind = kind, Subject = subject, Bodies = bodies, BodyBytes = bytes };
    }

    /// <summary>
    /// RAM one cloned body takes, bytes: the Body and DamageState objects with their strings and list headers (about 480 B
    /// measured by field count on x64) plus 48 B per crater or heat spot. An estimate: the budget only needs the scale.
    /// </summary>
    public static long EstimateBytes(Body b) => 480 + 48L * (b.Damage.Craters.Count + b.Damage.Heat.Count);

    /// <summary>A snapshot with the app's state attached, for the history.</summary>
    private UniverseSnapshot Capture(string label, UndoKind kind, string? subject)
    {
        var s = Snapshot(label, kind, subject);
        AttachState?.Invoke(s);
        return s;
    }

    public void Restore(UniverseSnapshot s)
    {
        Time = s.Time;
        SetBodies(CloneBodies(s.Bodies));
        pending.Clear();
        LastCollisionStep = double.NegativeInfinity;
    }

    private static List<Body> CloneBodies(List<Body> bodies)
    {
        var copy = new List<Body>(bodies.Count);
        foreach (var b in bodies) copy.Add(b.Clone());
        return copy;
    }

    /// <summary>
    /// Record an undo point before an impact, unless this collision continues a cascade that already
    /// has one. A shatter's fragments go on hitting each other for many frames, and a snapshot per
    /// collision meant a deep clone of every body per hit - a GC storm mid-cascade - while one
    /// cascade flushed the undo list with near-identical states. A cascade is collisions fewer
    /// than HistoryQuietSteps Step() calls apart; undo takes it back to before the first of them.
    /// <paramref name="force"/> records a deliberate act (placing a black hole) whatever the cascade clock says, and leaves
    /// that clock alone, so a collision right after it still gets its own undo point. Either way a new point clears redo.
    /// <paramref name="label"/> names the act for the player's "Undone: …" hint; <paramref name="subject"/> is the id of the
    /// body it was about (an impact's impactor).
    ///
    /// A collision whose impactor belongs to the meteor shower on top of the history (its id starts with that point's
    /// <see cref="UniverseSnapshot.Subject"/>) records nothing either: the shower's one point already holds the state before
    /// all of its impacts, so one undo takes the whole volley back. Once any other point lands on top, its rocks' impacts
    /// record their own again.
    /// </summary>
    public void PushHistory(string label, bool force = false, UndoKind kind = UndoKind.Collision, string? subject = null)
    {
        if (!force)
        {
            var quiet = Steps - LastCollisionStep > HistoryQuietSteps;
            LastCollisionStep = Steps;
            if (!quiet) return;
            if (subject != null && History.NextUndo is { Kind: UndoKind.Shower, Subject: { Length: > 0 } volley } &&
                subject.StartsWith(volley, StringComparison.Ordinal)) return;
        }
        History.Push(Capture(label, kind, subject));
    }

    /// <summary>Restore the newest undo point without keeping a redo entry (fault rollback). Null when there is none.</summary>
    public UniverseSnapshot? PopHistory()
    {
        var s = History.Pop();
        if (s != null) Restore(s);
        return s;
    }

    /// <summary>
    /// Restore the newest undo point; the state it replaces goes on the redo list under the same label, with the app's
    /// attachment. Returns the point, which has left the history: the caller restores its attachment and then frees it.
    ///
    /// Undo opens a quiet window like a collision's: an undone impact is usually still about to happen (the pair is at
    /// contact), and when time runs on it happens again without recording a point of its own, so the next undo goes on back
    /// past it (to the launch that caused it) instead of returning to the same moment for ever.
    /// </summary>
    public UniverseSnapshot? Undo()
    {
        if (History.NextUndo is not { } top) return null;
        var s = History.TakeUndo(Capture(top.Label, top.Kind, top.Subject));
        if (s == null) return null;
        Restore(s);
        LastCollisionStep = Steps;
        return s;
    }

    /// <summary>Restore the next redo point; the state it replaces goes back on the undo list. The mirror of <see cref="Undo"/>.</summary>
    public UniverseSnapshot? Redo()
    {
        if (History.NextRedo is not { } next) return null;
        var s = History.TakeRedo(Capture(next.Label, next.Kind, next.Subject));
        if (s == null) return null;
        Restore(s);
        LastCollisionStep = Steps;
        return s;
    }

    /// <summary>
    /// Total mechanical energy, J (the 1e6 converts km^2 to m^2). A pair with a black hole counts the Paczyński–Wiita
    /// potential −G m₁ m₂/(r − r_s), the one its force conserves.
    /// </summary>
    public (double Kinetic, double Potential, double Total) Energy()
    {
        double k = 0, p = 0;
        var bs = Bodies;
        for (var i = 0; i < bs.Count; i++)
        {
            var b = bs[i];
            var v2 = b.Vel.X * b.Vel.X + b.Vel.Y * b.Vel.Y + b.Vel.Z * b.Vel.Z;
            k += 0.5 * b.Mass * v2 * 1e6;
            var hi = PseudoNewtonian.HoleRs(b);
            for (var j = i + 1; j < bs.Count; j++)
            {
                var hole = hi + PseudoNewtonian.HoleRs(bs[j]);
                if (hole > 0) p -= (Constants.G * 1e6 * b.Mass * bs[j].Mass) / (Vec3.Length(bs[j].Pos - b.Pos) - hole);
                else p -= (Constants.G * 1e6 * b.Mass * bs[j].Mass) / Vec3.Length(bs[j].Pos - b.Pos);
            }
        }
        return (k, p, k + p);
    }

    /// <summary>Centre of mass (km), its velocity (km/s) and the total mass (kg).</summary>
    public (Vec3 Pos, Vec3 Vel, double Mass) Barycentre()
    {
        double m = 0;
        Vec3 r = Vec3.Zero, v = Vec3.Zero;
        foreach (var b in Bodies)
        {
            m += b.Mass;
            r += b.Pos * b.Mass;
            v += b.Vel * b.Mass;
        }
        return (r * (1 / m), v * (1 / m), m);
    }

    /// <summary>The body whose gravity dominates at a point (Hill-sphere style), defaulting to the most massive body.</summary>
    public Body? DominantBody(Vec3 pos, Body? exclude = null)
    {
        Body? best = null;
        double bestAcc = -1;
        foreach (var b in Bodies)
        {
            if (ReferenceEquals(b, exclude)) continue;
            var d = pos - b.Pos;
            var r2 = d.X * d.X + d.Y * d.Y + d.Z * d.Z;
            if (r2 == 0) continue;
            var a = b.Mass / r2;
            if (a > bestAcc)
            {
                bestAcc = a;
                best = b;
            }
        }
        return best;
    }

    /// <summary>
    /// The body <paramref name="b"/> is most tightly bound to: among everything heavier than it that it is
    /// gravitationally bound to, the nearest. That makes the Moon orbit Earth rather than the Sun, and a captured rock
    /// orbit whatever actually captured it.
    /// </summary>
    public Body? PrimaryOf(Body b)
    {
        Body? best = null;
        var bestDist = double.PositiveInfinity;
        Body? fallback = null;
        double fallbackAcc = -1;
        foreach (var o in Bodies)
        {
            if (ReferenceEquals(o, b) || o.Mass <= b.Mass) continue;
            var r = b.Pos - o.Pos;
            var d = Vec3.Length(r);
            if (d <= 0) continue;
            var v = b.Vel - o.Vel;
            var specificEnergy = (v.X * v.X + v.Y * v.Y + v.Z * v.Z) / 2 - (Constants.G * (o.Mass + b.Mass)) / d;
            if (specificEnergy < 0 && d < bestDist)
            {
                bestDist = d;
                best = o;
            }
            var acc = o.Mass / (d * d);
            if (acc > fallbackAcc)
            {
                fallbackAcc = acc;
                fallback = o;
            }
        }
        return best ?? fallback;
    }
}
