using System.Runtime.InteropServices;

namespace Spayly.Sim;

/// <summary>
/// Hierarchical time steps: a planet's moon system is integrated on its own, small steps while the rest of the solar system
/// takes one large one. A single global step is 0.02 of the shortest dynamical time anywhere, and that is Phobos (87 s) when
/// everything else could take four hours, so 40 bodies pay 780 pair evaluations every 87 s for the sake of three.
///
/// The scheme is the symplectic multiple-time-step ("r-RESPA", Verlet-I) splitting. Gravity is divided exactly in two:
/// the <i>fast</i> part is every pair inside one group (a planet, its moons, and any other body close enough to have joined
/// it), the <i>slow</i> part is every other pair. One outer step of H is
///   kick(slow, H/2) · [ each group: leapfrog on the fast forces alone, over H, in its own substeps ] ·
///   drift(non-members, H) · recompute slow · kick(slow, H/2)
/// which is time-symmetric and keeps each group's own integrator (including its contact sweeps) exactly the flat one's:
/// a group is a child <see cref="Universe"/> over the same Body objects. H is limited by the slow pairs' own dynamical
/// time as before, and by <see cref="HierarchyFraction"/> of the fastest group's, so the Sun's tide on a moon is kicked
/// well inside the moon's period rather than aliased against it.
///
/// Anything this cannot describe falls back to the flat step for that moment, so the flat integrator remains the
/// definition of correct: a black hole in the system, groups that overlap, a moon that has left its primary, and any
/// contact. A contact inside a group aborts that outer step and restores its start state, and the flat step then finds the
/// same contact with its usual sweep and resolves it.
/// </summary>
public sealed partial class Universe
{
    /// <summary>Integrate moon systems on their own steps. Off by default: the goldens and every predictor use the flat step.</summary>
    public bool Hierarchical { get; set; }
    /// <summary>
    /// The outer step may be at most this multiple of the fastest group's dynamical time (orbital period / 2π). The slow
    /// force on a moon (the Sun's tide) is sampled once per outer step, so H well under a period keeps that sampling from
    /// beating against the orbit; 0.5 puts about thirteen samples on the fastest orbit; measured over 100 y in HierarchyTests.
    /// </summary>
    public double HierarchyFraction { get; set; } = 0.5;
    /// <summary>
    /// DynFraction inside a group. A group is a handful of bodies, so a finer step there costs almost nothing next to the
    /// slow pass over every pair, and it keeps a moon's own orbit as well resolved as the outer step allows.
    /// </summary>
    public double HierarchyInnerFraction { get; set; } = 0.005;
    /// <summary>Outer steps the last Step() took hierarchically, and how many were abandoned for a contact.</summary>
    public int HierarchicalStepsLastFrame { get; private set; }
    public int HierarchyAbortsLastFrame { get; private set; }

    // Set on a child universe: stop at the first contact instead of resolving it, and say so.
    private bool abortOnContact;
    private bool contactAborted;

    private sealed class FastGroup
    {
        public readonly Universe Sim = new(0) { abortOnContact = true };
        public readonly List<int> Base = [];
        public readonly List<int> Members = [];
        public int Primary;
        public double DynFast;
    }

    private bool hierarchyDirty = true;
    private bool slowValid;
    private int hold;
    private readonly List<FastGroup> groups = [];
    private int groupCount;
    private readonly Dictionary<string, int> indexOfId = [];
    // Per body: its own group from the catalogue (primary or moon), and the group it is in this outer step (that plus intruders).
    private int[] baseOf = [], groupOf = [], prevGroupOf = [];
    private double[] sax = [], say = [], saz = [];
    private double[] bpx = [], bpy = [], bpz = [], bvx = [], bvy = [], bvz = [], bax = [], bay = [], baz = [];
    private double dynSlow = double.PositiveInfinity;

    private readonly struct SlowPairs(int[] group) : IPairFilter
    {
        public bool Skip(int i, int j) => group[i] >= 0 && group[i] == group[j];
        // A body in a group moves along a curve the straight-line sweep cannot follow, so its contacts are the group's to
        // find, and BuildPartition guarantees nothing outside a group can reach one within the step.
        public bool Sweep(int i, int j) => group[i] < 0 && group[j] < 0;
    }

    private void EnsureHierarchyArrays()
    {
        if (baseOf.Length >= capacity) return;
        baseOf = new int[capacity]; groupOf = new int[capacity]; prevGroupOf = new int[capacity];
        sax = new double[capacity]; say = new double[capacity]; saz = new double[capacity];
        bpx = new double[capacity]; bpy = new double[capacity]; bpz = new double[capacity];
        bvx = new double[capacity]; bvy = new double[capacity]; bvz = new double[capacity];
        bax = new double[capacity]; bay = new double[capacity]; baz = new double[capacity];
    }

    /// <summary>Which bodies are a planet and its moons. Only changes when the body list does, i.e. at Pack.</summary>
    private void PrepareGroups()
    {
        EnsureHierarchyArrays();
        hierarchyDirty = false;
        slowValid = false;
        groupCount = 0;
        indexOfId.Clear();
        for (var i = 0; i < n; i++) { indexOfId[Bodies[i].Id] = i; baseOf[i] = -1; prevGroupOf[i] = -2; }
        for (var i = 0; i < n; i++)
        {
            var b = Bodies[i];
            if (b.Kind != BodyKind.Moon || b.PrimaryId == null || !indexOfId.TryGetValue(b.PrimaryId, out var p)) continue;
            if (Bodies[p].Kind == BodyKind.Moon || Bodies[p].Kind == BodyKind.BlackHole) continue;
            var g = baseOf[p];
            if (g < 0)
            {
                g = groupCount++;
                if (g == groups.Count) groups.Add(new FastGroup());
                groups[g].Base.Clear();
                groups[g].Primary = p;
                baseOf[p] = g;
            }
            baseOf[i] = g;
        }
        for (var i = 0; i < n; i++) if (baseOf[i] >= 0) groups[baseOf[i]].Base.Add(i);
    }

    /// <summary>
    /// Fix this outer step's groups: each planet system plus every other body that could come within three times the
    /// system's present radius during <paramref name="h"/> at its present relative speed. False when two systems would
    /// overlap, which the flat step then handles.
    /// </summary>
    private bool BuildPartition(double h)
    {
        for (var i = 0; i < n; i++) groupOf[i] = baseOf[i];
        for (var g = 0; g < groupCount; g++)
        {
            var fg = groups[g];
            int p = fg.Primary;
            double rmax = 0;
            foreach (var m in fg.Base)
            {
                var d2 = Sq(px[m] - px[p]) + Sq(py[m] - py[p]) + Sq(pz[m] - pz[p]);
                if (d2 > rmax) rmax = d2;
            }
            rmax = Math.Sqrt(rmax);
            for (var k = 0; k < n; k++)
            {
                if (baseOf[k] == g) continue;
                var d = Math.Sqrt(Sq(px[k] - px[p]) + Sq(py[k] - py[p]) + Sq(pz[k] - pz[p]));
                var vrel = Math.Sqrt(Sq(vx[k] - vx[p]) + Sq(vy[k] - vy[p]) + Sq(vz[k] - vz[p]));
                if (d >= 3 * rmax + vrel * h + rad[k]) continue;
                if (baseOf[k] >= 0 || groupOf[k] >= 0) return false;
                groupOf[k] = g;
            }
        }
        for (var g = 0; g < groupCount; g++) groups[g].Members.Clear();
        for (var i = 0; i < n; i++) if (groupOf[i] >= 0) groups[groupOf[i]].Members.Add(i);
        return true;
    }

    private static double Sq(double x) => x * x;

    /// <summary>
    /// One outer step. False, with nothing changed, when the flat step has to take this one: <paramref name="h"/> is the
    /// signed step taken otherwise. Counts every pair evaluation it spends, wasted ones included, in PairEvalsLastFrame.
    /// </summary>
    private bool TryHierarchicalStep(double remaining, int sign, out double h)
    {
        h = 0;
        if (hold > 0) { hold--; return false; }
        if (anyHole) return false;
        if (hierarchyDirty) PrepareGroups();
        if (groupCount == 0) return false;
        var hMax = Math.Min(MaxSubstep, Math.Abs(remaining));
        if (!BuildPartition(hMax)) return false;

        // Which pairs are slow depends on the partition; the slow forces carried over from the last outer step are only
        // good if it is the same one.
        for (var i = 0; i < n; i++) if (groupOf[i] != prevGroupOf[i]) { slowValid = false; break; }
        Array.Copy(groupOf, prevGroupOf, n);

        // Rollback point, taken before the slow pass overwrites ax.
        Array.Copy(px, bpx, n); Array.Copy(py, bpy, n); Array.Copy(pz, bpz, n);
        Array.Copy(vx, bvx, n); Array.Copy(vy, bvy, n); Array.Copy(vz, bvz, n);
        Array.Copy(ax, bax, n); Array.Copy(ay, bay, n); Array.Copy(az, baz, n);

        // What the flat step would take from here: the bar the outer step has to clear to be worth its bookkeeping.
        var flatH = Math.Max(MinSubstep, DynFraction * dynTime);
        var slowPairs = Math.Max(1, (double)n * (n - 1) / 2);
        for (var g = 0; g < groupCount; g++)
        {
            var m = groups[g].Members.Count;
            slowPairs -= (double)m * (m - 1) / 2;
        }
        if (!slowValid) SlowPass(0, slowPairs);

        // Each group's fastest pair sets how big a step it can be asked for.
        var dynFast = double.PositiveInfinity;
        for (var g = 0; g < groupCount; g++)
        {
            var fg = groups[g];
            LoadGroup(fg);
            fg.Sim.PrimeFast();
            fg.DynFast = fg.Sim.dynTime;
            dynFast = Math.Min(dynFast, fg.DynFast);
        }
        var hOuter = Math.Min(MaxSubstep, Math.Max(MinSubstep, DynFraction * dynSlow));
        hOuter = Math.Min(hOuter, Math.Max(MinSubstep, HierarchyFraction * dynFast));
        hOuter = Math.Min(hOuter, Math.Abs(remaining));
        // Not worth the bookkeeping unless the outer step is several inner ones and several flat ones: a lone Earth-Moon
        // pair steps at 7500 s flat and gains nothing, and the groups cost a few microseconds an outer step to run.
        if (hOuter < 4 * Math.Max(MinSubstep, Math.Min(DynFraction, HierarchyInnerFraction) * dynFast) || hOuter < 6 * flatH)
        {
            hold = 32;
            Array.Copy(bax, ax, n); Array.Copy(bay, ay, n); Array.Copy(baz, az, n); // the slow pass overwrote the total
            dynTime = Math.Min(dynSlow, dynFast); // and the shortest time, which the flat step sizes itself by
            dynFromHole = false;
            return false;
        }
        h = hOuter * sign;

        var hh = h / 2;
        for (var i = 0; i < n; i++) { vx[i] += sax[i] * hh; vy[i] += say[i] * hh; vz[i] += saz[i] * hh; }
        // Bodies outside every group take the step as the flat leapfrog does; groups integrate theirs.
        for (var i = 0; i < n; i++)
        {
            if (groupOf[i] >= 0) continue;
            px[i] += vx[i] * h; py[i] += vy[i] * h; pz[i] += vz[i] * h;
        }
        var innerDyn = double.PositiveInfinity;
        for (var g = 0; g < groupCount; g++)
        {
            var fg = groups[g];
            LoadGroup(fg);
            fg.Sim.CopyLimits(this);
            fg.Sim.contactAborted = false;
            var advanced = fg.Sim.Step(h);
            PairEvalsLastFrame += fg.Sim.PairEvalsLastFrame;
            if (fg.Sim.contactAborted || fg.Sim.WarpLimited || Math.Abs(advanced - h) > 1e-3 * Math.Abs(h) + 1e-6)
            {
                Rollback();
                return false;
            }
            foreach (var i in fg.Members)
            {
                var b = Bodies[i];
                px[i] = b.Pos.X; py[i] = b.Pos.Y; pz[i] = b.Pos.Z;
                vx[i] = b.Vel.X; vy[i] = b.Vel.Y; vz[i] = b.Vel.Z;
            }
            innerDyn = Math.Min(innerDyn, fg.Sim.dynTime);
        }
        SlowPass(h, slowPairs);
        for (var i = 0; i < n; i++) { vx[i] += sax[i] * hh; vy[i] += say[i] * hh; vz[i] += saz[i] * hh; }
        // The total acceleration at the new positions, which a flat step or Unpack may use next: slow here, fast from each group.
        for (var i = 0; i < n; i++)
        {
            var fast = groupOf[i] >= 0 ? Bodies[i].Acc : Vec3.Zero;
            ax[i] = sax[i] + fast.X; ay[i] = say[i] + fast.Y; az[i] = saz[i] + fast.Z;
        }
        dynTime = Math.Min(dynSlow, innerDyn);
        dynFromHole = false;
        HierarchicalStepsLastFrame++;
        return true;
    }

    /// <summary>The slow forces at the current positions, kept in sax (the pass computes them in ax).</summary>
    private void SlowPass(double driftH, double pairs)
    {
        Accelerate(driftH, new SlowPairs(groupOf));
        Array.Copy(ax, sax, n); Array.Copy(ay, say, n); Array.Copy(az, saz, n);
        dynSlow = dynTime;
        slowValid = true;
        PairEvalsLastFrame += pairs;
    }

    private void LoadGroup(FastGroup fg)
    {
        var list = fg.Sim.Bodies;
        list.Clear();
        foreach (var i in fg.Members)
        {
            var b = Bodies[i];
            b.Pos = new Vec3(px[i], py[i], pz[i]);
            b.Vel = new Vec3(vx[i], vy[i], vz[i]);
            list.Add(b);
        }
    }

    private void Rollback()
    {
        Array.Copy(bpx, px, n); Array.Copy(bpy, py, n); Array.Copy(bpz, pz, n);
        Array.Copy(bvx, vx, n); Array.Copy(bvy, vy, n); Array.Copy(bvz, vz, n);
        // One full pass rather than the saved ax: it also restores the shortest dynamical time over every pair, which the
        // flat step sizes itself by and which the half-finished outer step has overwritten with a part of it.
        Accelerate(0);
        slowValid = false;
        hold = 8;
        HierarchyAbortsLastFrame++;
    }

    private void PrimeFast() => Pack();

    private void CopyLimits(Universe from)
    {
        MaxSubstep = from.MaxSubstep; MinSubstep = from.MinSubstep; DynFraction = Math.Min(from.DynFraction, from.HierarchyInnerFraction);
        MaxSubstepsPerFrame = from.MaxSubstepsPerFrame;
    }
}
