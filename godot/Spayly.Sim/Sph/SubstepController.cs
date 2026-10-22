namespace Spayly.Sim.Sph;

/// <summary>
/// How many SPH substeps a frame records (M7.4). k is sized from the median GPU cost of a substep over the last timed
/// substeps, not the latest one, so one slow or mis-attributed sample cannot swing it; and k moves by at most ×2 a frame,
/// so it cannot flip between 1 and 16 (M6.7 measured 7 × 1, 9 × 16 in one Fast run). M7.5 adds the frame guard: while the
/// rolling frame p95 is over <see cref="FrameTargetMs"/>, k never grows and is halved every <see cref="HalveHold"/> frames.
/// Costs in ms, k in substeps a frame.
/// </summary>
public sealed class SubstepController
{
    /// <summary>Most substeps one frame records.</summary>
    public const int Max = 16;
    /// <summary>Substeps a frame before any substep has been timed.</summary>
    public const int Initial = 2;

    /// <summary>The frame time the game never lets its p95 pass, ms: 30 fps.</summary>
    public const double FrameTargetMs = 33;
    /// <summary>
    /// Frames between two halvings while the frame p95 stays over target. A 120-frame p95 keeps a slow burst for up to
    /// 120 frames after it ends, so halving every frame would pin k at 1 long after the frames recovered.
    /// </summary>
    public const int HalveHold = 30;

    private int sinceHalve = HalveHold;

    /// <summary>The substep count chosen last, 1..<see cref="Max"/>.</summary>
    public int K { get; private set; } = Initial;

    /// <summary>
    /// Choose this frame's k from the median ms per substep, the frame's SPH budget and the rolling frame p95, all ms.
    /// Without a timing (<paramref name="medianMsPerSubstep"/> ≤ 0) k stays where it is, unless the frame guard halves it.
    /// </summary>
    public int Next(double medianMsPerSubstep, double budgetMs, double frameP95Ms = 0)
    {
        sinceHalve++;
        var fit = medianMsPerSubstep > 0 ? Limit(K, Fit(budgetMs, medianMsPerSubstep)) : K;
        if (frameP95Ms > FrameTargetMs)
        {
            if (sinceHalve >= HalveHold)
            {
                fit = Math.Min(fit, Math.Max(1, (K + 1) / 2));
                sinceHalve = 0;
            }
            fit = Math.Min(fit, K);
        }
        K = fit;
        return K;
    }

    /// <summary>
    /// The frame's SPH budget, ms: what the target frame leaves after the rest of the frame (<paramref name="nonSphP95Ms"/>,
    /// render + lensing + debris, p95) and a margin, never under <paramref name="minMs"/> (so SPH always moves) or over
    /// <paramref name="maxMs"/>.
    /// </summary>
    public static double Budget(double nonSphP95Ms, double marginMs = 3, double minMs = 8, double maxMs = 20) =>
        Math.Clamp(FrameTargetMs - nonSphP95Ms - marginMs, minMs, maxMs);

    /// <summary>
    /// Substeps left before the next checkpoint, 1..<paramref name="every"/>, after <paramref name="done"/> substeps: capping k
    /// with it makes a frame end on every multiple of <paramref name="every"/>, so the finish is judged at the same
    /// substeps, and so the same SPH times, on every machine.
    /// </summary>
    public static int ToCheckpoint(long done, int every) => every - (int)(done % every);

    /// <summary>Substeps of <paramref name="msPerSubstep"/> that fit in <paramref name="budgetMs"/>, clamped to 1..<see cref="Max"/>.</summary>
    public static int Fit(double budgetMs, double msPerSubstep)
    {
        if (!(msPerSubstep > 0)) return Initial;
        var n = budgetMs / msPerSubstep;
        return n >= Max ? Max : Math.Max(1, (int)n);
    }

    /// <summary><paramref name="target"/> held within ×2 of <paramref name="previous"/> and within 1..<see cref="Max"/>.</summary>
    public static int Limit(int previous, int target)
    {
        var p = Math.Clamp(previous, 1, Max);
        // Round the lower bound up: 3 → 1 would be a ×3 drop.
        return Math.Clamp(target, Math.Max(1, (p + 1) / 2), Math.Min(Max, 2 * p));
    }
}
