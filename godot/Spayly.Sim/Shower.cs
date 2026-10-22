namespace Spayly.Sim;

/// <param name="Pos">Where the rock starts, km.</param>
/// <param name="Vel">Its velocity, km/s, in the same frame as the aimed velocity.</param>
/// <param name="Delay">How much later than the aimed shot it passes the launch point, s: its place in the launch window.</param>
public readonly record struct ShowerRock(Vec3 Pos, Vec3 Vel, double Delay);

/// <summary>
/// A meteor shower: one aimed shot spread into N rocks that arrive over a short window instead of all at once, as the
/// fragments of comet Shoemaker–Levy 9 fell on Jupiter one after another in 1994. Everything here happens in the target's
/// frame (the aimed velocity minus the target's), which is what keeps every rock on the target while the target moves.
///
/// Each rock gets its own aim point, drawn uniformly over a disc of <see cref="AimFill"/> of the target's radius across the
/// line of flight, and its own speed within ±<see cref="SpeedSpread"/>. The launch is staggered by starting rock k further
/// back along its own line by the distance the aimed shot covers in its delay: it passes the launch point that much later,
/// so the whole volley is laid out at one instant (one undo point, and it works while paused) and still arrives as a
/// shower; a caller that adds the rocks over a few frames carries each along its line by the time since. Delays are stratified over the window, so consecutive rocks start at least
/// <see cref="MinSeparationRadii"/> rock radii apart and never coincide; the fastest rocks lead, so none catches up with
/// the one ahead of it on the way in.
/// </summary>
public static class Shower
{
    public const int MinRocks = 8, MaxRocks = 24;
    /// <summary>Half-width of the speed spread, a fraction of the aimed speed relative to the target.</summary>
    public const double SpeedSpread = 0.05;
    /// <summary>Radius of the disc the aim points fill across the line of flight, a fraction of the target's radius.</summary>
    public const double AimFill = 0.6;
    /// <summary>The launch window, a fraction of the flight time.</summary>
    public const double WindowFraction = 0.1;
    /// <summary>Closest two rocks may start, in rock radii.</summary>
    public const double MinSeparationRadii = 8;
    /// <summary>How far a rock's delay may stray inside its slot of the window, a fraction of the slot; the rest is the gap.</summary>
    const double Jitter = 0.8;

    public static int Clamp(int n) => Math.Clamp(n, MinRocks, MaxRocks);

    /// <summary>Diameter of each of <paramref name="n"/> equal pieces of a rock <paramref name="diameter"/> across at the same density (mass conserved). Any length unit in, the same out.</summary>
    public static double PieceDiameter(double diameter, int n) => diameter / Math.Cbrt(Math.Max(n, 1));

    /// <summary>The launch window for a flight of <paramref name="flightTime"/> s, s: long enough that the stratified delays keep <paramref name="n"/> rocks of <paramref name="rockRadius"/> km apart at <paramref name="speed"/> km/s.</summary>
    public static double Window(int n, double flightTime, double speed, double rockRadius)
    {
        var t = double.IsFinite(flightTime) && flightTime > 0 ? flightTime : 0;
        var spacing = MinSeparationRadii * rockRadius * n / ((1 - Jitter) * speed);
        return Math.Max(WindowFraction * t, spacing);
    }

    /// <summary>
    /// Spread one aimed shot into <paramref name="n"/> rocks. <paramref name="from"/> is the launch point (km) and
    /// <paramref name="vel"/> the aimed velocity (km/s) that hits a target moving at <paramref name="targetVel"/> (km/s)
    /// after <paramref name="flightTime"/> s; <paramref name="targetRadius"/> and <paramref name="rockRadius"/> are km.
    /// Deterministic in <paramref name="seed"/> (<see cref="Rng.Mulberry32"/>). Rock 0 leads.
    /// </summary>
    public static ShowerRock[] Spread(int n, Vec3 from, Vec3 vel, Vec3 targetVel, double targetRadius, double flightTime, double rockRadius, uint seed)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(n, 1);
        var rel = vel - targetVel;
        var speed = Vec3.Length(rel);
        if (!(speed > 0) || !double.IsFinite(speed)) throw new ArgumentException($"the aimed speed relative to the target must be finite and positive, got {speed}", nameof(vel));
        var u = rel * (1 / speed);
        var e1 = Vec3.AnyPerpendicular(u);
        var e2 = Vec3.Cross(u, e1);
        var window = Window(n, flightTime, speed, rockRadius);
        // The distance the aimed shot covers to the target, km: turns an aim offset at the target into a direction.
        var reach = Math.Max(speed * (double.IsFinite(flightTime) && flightTime > 0 ? flightTime : 0), targetRadius);
        var rand = Rng.Mulberry32(seed);

        var delays = new double[n];
        var speeds = new double[n];
        var dirs = new Vec3[n];
        for (var k = 0; k < n; k++)
        {
            delays[k] = window * (k + 0.5 + Jitter * (rand() - 0.5)) / n;
            speeds[k] = speed * (1 + SpeedSpread * (2 * rand() - 1));
            // Uniform over the disc: radius from the square root.
            var r = AimFill * targetRadius * Math.Sqrt(rand());
            var phi = 2 * Math.PI * rand();
            dirs[k] = Vec3.Normalize(u * reach + e1 * (r * Math.Cos(phi)) + e2 * (r * Math.Sin(phi)));
        }
        // Fastest first: a rock never gains on the one ahead of it, so the stagger holds all the way in.
        Array.Sort(speeds);
        Array.Reverse(speeds);

        var rocks = new ShowerRock[n];
        for (var k = 0; k < n; k++)
        {
            // Back along the rock's own line, so it passes the launch point `Delay` after the aimed shot would.
            var pos = from - dirs[k] * (speed * delays[k]);
            rocks[k] = new ShowerRock(pos, targetVel + dirs[k] * speeds[k], delays[k]);
        }
        return rocks;
    }
}
