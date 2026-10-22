using static Spayly.Sim.Constants;

namespace Spayly.Sim;

/// <summary>The part of a body the predictor reads. A <see cref="Body"/> satisfies it; so does a plain snapshot.</summary>
public interface IWorldBody
{
    string Id { get; }
    /// <summary>km.</summary>
    Vec3 Pos { get; }
    /// <summary>km/s.</summary>
    Vec3 Vel { get; }
    /// <summary>kg.</summary>
    double Mass { get; }
    /// <summary>km.</summary>
    double Radius { get; }
    /// <summary>The Paczyński–Wiita r_s this body adds to its pairs, km: its Schwarzschild radius if a black hole, else 0.</summary>
    double HoleRs => 0;
}

/// <summary>
/// What the predictor integrates: a <see cref="Universe"/> satisfies it, and so does a snapshot, which is how aiming
/// runs in a worker instead of stalling the page.
/// </summary>
public interface IWorld
{
    IReadOnlyList<IWorldBody> Bodies { get; }
}

/// <summary>A copied body: what <see cref="Aim.SnapshotWorld"/> holds. Pos km, Vel km/s, Mass kg, Radius km, HoleRs km.</summary>
public sealed record WorldBody(string Id, Vec3 Pos, Vec3 Vel, double Mass, double Radius, double HoleRs = 0) : IWorldBody;

/// <summary>A copied world: what <see cref="Aim.SnapshotWorld"/> returns.</summary>
public sealed class World(IReadOnlyList<IWorldBody> bodies) : IWorld
{
    public IReadOnlyList<IWorldBody> Bodies { get; } = bodies;
}

/// <param name="Pos">km.</param>
/// <param name="Vel">km/s.</param>
/// <param name="Radius">km.</param>
public readonly record struct Probe(Vec3 Pos, Vec3 Vel, double Radius);

/// <param name="BodyId">The body hit.</param>
/// <param name="Time">s after the prediction starts.</param>
/// <param name="Pos">Probe position at the hit, km.</param>
/// <param name="BodyPos">Body position at the hit, km.</param>
/// <param name="Speed">km/s relative to the body.</param>
public readonly record struct PredictHit(string BodyId, double Time, Vec3 Pos, Vec3 BodyPos, double Speed);

/// <param name="BodyId">The tracked body.</param>
/// <param name="Dist">km, centre to centre.</param>
/// <param name="Time">s after the prediction starts.</param>
/// <param name="ProbePos">km.</param>
/// <param name="BodyPos">km.</param>
public readonly record struct Closest(string BodyId, double Dist, double Time, Vec3 ProbePos, Vec3 BodyPos);

public sealed class PredictResult
{
    /// <summary>km.</summary>
    public required List<Vec3> Points { get; init; }
    public PredictHit? Hit { get; init; }
    public Closest? Closest { get; init; }
    /// <summary>s integrated.</summary>
    public required double Duration { get; init; }
    public required int Steps { get; init; }
    /// <summary>Gravity pair evaluations spent: the unit the budget is set in, and the real cost.</summary>
    public required double PairEvals { get; init; }
    /// <summary>The pair budget ran out before the horizon, so the path stops short.</summary>
    public required bool Truncated { get; init; }
}

public readonly record struct PredictOptions
{
    public int? MaxSteps { get; init; }
    public int? MaxPoints { get; init; }
    public string? TrackId { get; init; }
    public double? DynFraction { get; init; }
    /// <summary>s.</summary>
    public double? MaxSubstep { get; init; }
    /// <summary>
    /// Bodies lighter than this (kg) do not attract; they still move under everything that does and can still be hit.
    /// Defaults to 1e-10 of the heaviest body: about 2e20 kg beside the Sun, so roughly Enceladus and anything
    /// lighter. The tracked target always attracts.
    /// </summary>
    public double? MassFloor { get; init; }
    /// <summary>Stop once this many gravity pair evaluations have been spent (see <see cref="PredictResult.PairEvals"/>).</summary>
    public double? PairBudget { get; init; }
}

public sealed class AimResult
{
    /// <summary>Launch velocity, km/s.</summary>
    public required Vec3 Vel { get; init; }
    public required PredictResult Prediction { get; init; }
    public required bool Converged { get; init; }
    /// <summary>Gravity pair evaluations spent across every iteration.</summary>
    public double PairEvals { get; set; }
}

/// <summary>Trajectory prediction and auto-aim; mirrors src/sim/aim.ts.</summary>
public static class Aim
{
    /// <summary>A copy of just what the predictor needs, cheap enough to post to a worker every aim.</summary>
    public static World SnapshotWorld(IWorld w) =>
        new(w.Bodies.Select(IWorldBody (b) => new WorldBody(b.Id, b.Pos, b.Vel, b.Mass, b.Radius, b.HoleRs)).ToList());

    /// <summary>
    /// Integrate a massless probe through a copy of the universe (bodies keep attracting each other) and return its
    /// path, the first collision, and the closest approach to a tracked body. <paramref name="horizon"/> is in s.
    /// Gravity matches <see cref="Universe"/>: Paczyński–Wiita for any pair or probe pull involving a black hole (r_s from
    /// <see cref="IWorldBody.HoleRs"/>), whose contact radius is the 2 r_s capture radius; Newton, unchanged, for the rest.
    /// </summary>
    public static PredictResult PredictPath(IWorld u, Probe probe, double horizon, PredictOptions opts = default)
    {
        var maxSteps = opts.MaxSteps ?? 3000;
        var maxPoints = opts.MaxPoints ?? 500;
        var dynFraction = opts.DynFraction ?? 0.04;
        var maxSubstep = opts.MaxSubstep ?? 12 * 3600;
        var bodies = u.Bodies;
        var n = bodies.Count;
        double[] px = new double[n], py = new double[n], pz = new double[n];
        double[] vx = new double[n], vy = new double[n], vz = new double[n];
        double[] ax = new double[n], ay = new double[n], az = new double[n];
        double[] m = new double[n], rad = new double[n], hrs = new double[n];
        var anyHole = false;
        for (var i = 0; i < n; i++)
        {
            var b = bodies[i];
            px[i] = b.Pos.X; py[i] = b.Pos.Y; pz[i] = b.Pos.Z;
            vx[i] = b.Vel.X; vy[i] = b.Vel.Y; vz[i] = b.Vel.Z;
            m[i] = b.Mass;
            hrs[i] = b.HoleRs;
            rad[i] = hrs[i] > 0 ? PseudoNewtonian.CaptureRs * hrs[i] : b.Radius;
            if (hrs[i] > 0) anyHole = true;
        }
        var trackIdx = -1;
        if (!string.IsNullOrEmpty(opts.TrackId))
        {
            for (var i = 0; i < n; i++) if (bodies[i].Id == opts.TrackId) { trackIdx = i; break; }
        }

        // Only bodies above the mass floor attract. A fragment's pull on a trajectory is far below this
        // integration's own error, but keeping it costs twice over: every step becomes all-pairs, and
        // fragments passing close to each other or to their parent set tiny timescales that shrink the
        // step for the whole prediction, so the probe never reaches its target within maxSteps. With 200
        // fragments near the Moon that made one auto-aim take 33 s and still miss.
        //
        // Light bodies are obstacles here: they keep their radius so the probe can still hit them, and the
        // probe-crossing term below is what stops the probe stepping through one. Each follows its host,
        // the attractor pulling it hardest at the start: it takes the host's own acceleration plus the
        // host's pull on it, which keeps its orbit about the host right and drops only third bodies'
        // tides, negligible over a prediction's span. That makes each one O(1) a step instead of O(k).
        // Their own orbital timescales no longer set the step either, so a fragment skimming its parent
        // cannot slow the probe down. None of this touches a probe aimed at a light body: the tracked
        // target always counts as an attractor.
        double heaviest = 0;
        for (var i = 0; i < n; i++) if (m[i] > heaviest) heaviest = m[i];
        var floor = opts.MassFloor ?? heaviest * 1e-10;
        var att = new List<int>();
        var attracts = new bool[n];
        for (var i = 0; i < n; i++)
        {
            if (m[i] >= floor || i == trackIdx) { att.Add(i); attracts[i] = true; }
        }
        var k = att.Count;
        var host = new int[n];
        Array.Fill(host, -1);
        for (var i = 0; i < n; i++)
        {
            if (attracts[i]) continue;
            double best = -1;
            for (var c = 0; c < k; c++)
            {
                var j = att[c];
                double dx = px[j] - px[i], dy = py[j] - py[i], dz = pz[j] - pz[i];
                var pull = m[j] / (dx * dx + dy * dy + dz * dz);
                if (pull > best) { best = pull; host[i] = j; }
            }
        }
        var pairsPerStep = (k * (k - 1.0)) / 2 + (n - k) + n;
        var budget = opts.PairBudget ?? double.PositiveInfinity;
        double pairEvals = 0;
        var truncated = false;

        double qx = probe.Pos.X, qy = probe.Pos.Y, qz = probe.Pos.Z;
        double wx = probe.Vel.X, wy = probe.Vel.Y, wz = probe.Vel.Z;
        double qax = 0, qay = 0, qaz = 0;
        var dyn = double.PositiveInfinity;
        var hitIdx = -1;

        // Timescales are compared squared (r^3/mu, and r^2/v^2 for crossings) so each pair costs one
        // square root instead of two. Gravity is softened to the contact distance, as in the universe's
        // own force loop, so coincident bodies give a finite pull instead of NaN.
        void Accel()
        {
            for (var i = 0; i < n; i++) { ax[i] = 0; ay[i] = 0; az[i] = 0; }
            var dyn2 = double.PositiveInfinity;
            for (var a = 0; a < k; a++)
            {
                var i = att[a];
                for (var c = a + 1; c < k; c++)
                {
                    var j = att[c];
                    double dx = px[j] - px[i], dy = py[j] - py[i], dz = pz[j] - pz[i];
                    var r2 = dx * dx + dy * dy + dz * dz;
                    var r = Math.Sqrt(r2);
                    var sum = rad[i] + rad[j];
                    var rs = r > sum ? r : sum;
                    var inv3 = 1 / (rs * rs * rs);
                    var hole = anyHole ? hrs[i] + hrs[j] : 0;
                    if (hole > 0) inv3 = 1 / ((rs - hole) * (rs - hole) * rs);
                    double gi = G * m[j] * inv3, gj = G * m[i] * inv3;
                    ax[i] += dx * gi; ay[i] += dy * gi; az[i] += dz * gi;
                    ax[j] -= dx * gj; ay[j] -= dy * gj; az[j] -= dz * gj;
                    var mu = G * (m[i] + m[j]);
                    if (mu > 0)
                    {
                        var t2 = (r2 * r) / mu;
                        if (t2 < dyn2) dyn2 = t2;
                    }
                }
            }
            for (var i = 0; i < n; i++)
            {
                var j = host[i];
                if (j < 0) continue;
                double dx = px[j] - px[i], dy = py[j] - py[i], dz = pz[j] - pz[i];
                var r = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                var sum = rad[i] + rad[j];
                var rs = r > sum ? r : sum;
                var hole = anyHole ? hrs[i] + hrs[j] : 0;
                var g = hole > 0 ? (G * m[j]) / ((rs - hole) * (rs - hole) * rs) : (G * m[j]) / (rs * rs * rs);
                ax[i] = ax[j] + dx * g; ay[i] = ay[j] + dy * g; az[i] = az[j] + dz * g;
            }
            qax = 0; qay = 0; qaz = 0;
            for (var i = 0; i < n; i++)
            {
                double dx = px[i] - qx, dy = py[i] - qy, dz = pz[i] - qz;
                var r2 = dx * dx + dy * dy + dz * dz;
                var r = Math.Sqrt(r2);
                if (r < rad[i] + probe.Radius) hitIdx = i;
                if (attracts[i])
                {
                    var rs = r > rad[i] ? r : rad[i];
                    var g = hrs[i] > 0 ? (G * m[i]) / ((rs - hrs[i]) * (rs - hrs[i]) * rs) : (G * m[i]) / (rs * rs * rs);
                    qax += dx * g; qay += dy * g; qaz += dz * g;
                    var mu = G * m[i];
                    if (mu > 0)
                    {
                        var t2 = (r2 * r) / mu;
                        if (t2 < dyn2) dyn2 = t2;
                    }
                }
                double dvx = vx[i] - wx, dvy = vy[i] - wy, dvz = vz[i] - wz;
                var v2 = dvx * dvx + dvy * dvy + dvz * dvz;
                if (v2 > 0)
                {
                    var c2 = r2 / v2;
                    if (c2 < dyn2) dyn2 = c2;
                }
            }
            dyn = Math.Sqrt(dyn2);
            pairEvals += pairsPerStep;
        }

        Accel();
        var points = new List<Vec3> { new(qx, qy, qz) };
        var recordEvery = Math.Max(1, maxSteps / maxPoints);
        double t = 0;
        var steps = 0;
        Closest? closest = null;
        PredictHit? hit = null;
        while (t < horizon && steps < maxSteps)
        {
            if (pairEvals + pairsPerStep > budget) { truncated = true; break; }
            var h = Math.Min(maxSubstep, Math.Max(1, dynFraction * dyn));
            if (t + h > horizon) h = horizon - t;
            var hh = h / 2;
            for (var i = 0; i < n; i++) { vx[i] += ax[i] * hh; vy[i] += ay[i] * hh; vz[i] += az[i] * hh; }
            wx += qax * hh; wy += qay * hh; wz += qaz * hh;
            for (var i = 0; i < n; i++) { px[i] += vx[i] * h; py[i] += vy[i] * h; pz[i] += vz[i] * h; }
            qx += wx * h; qy += wy * h; qz += wz * h;
            Accel();
            for (var i = 0; i < n; i++) { vx[i] += ax[i] * hh; vy[i] += ay[i] * hh; vz[i] += az[i] * hh; }
            wx += qax * hh; wy += qay * hh; wz += qaz * hh;
            t += h;
            steps++;
            if (trackIdx >= 0)
            {
                double dx = qx - px[trackIdx], dy = qy - py[trackIdx], dz = qz - pz[trackIdx];
                var d = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                if (closest is not { } cl || d < cl.Dist)
                {
                    closest = new Closest(bodies[trackIdx].Id, d, t, new Vec3(qx, qy, qz), new Vec3(px[trackIdx], py[trackIdx], pz[trackIdx]));
                }
            }
            if (steps % recordEvery == 0) points.Add(new Vec3(qx, qy, qz));
            if (hitIdx >= 0)
            {
                var i = hitIdx;
                double dvx = wx - vx[i], dvy = wy - vy[i], dvz = wz - vz[i];
                hit = new PredictHit(bodies[i].Id, t, new Vec3(qx, qy, qz), new Vec3(px[i], py[i], pz[i]), Math.Sqrt(dvx * dvx + dvy * dvy + dvz * dvz));
                points.Add(new Vec3(qx, qy, qz));
                break;
            }
        }
        return new PredictResult { Points = points, Hit = hit, Closest = closest, Duration = t, Steps = steps, PairEvals = pairEvals, Truncated = truncated };
    }

    /// <summary>
    /// Pair evaluations one auto-aim may spend in total. It runs synchronously on a click, so this is
    /// what bounds how long the page can stall: a clear shot needs a few million, and a crowded scene
    /// aims as well as the budget allows instead of freezing the page.
    /// </summary>
    public const double AimPairBudget = 4e6;

    /// <summary>
    /// Reference launch speeds, km/s relative to <paramref name="target"/>, for a projectile of
    /// <paramref name="projectileMass"/> kg (and horizon <paramref name="projectileRs"/> km, 0 unless it is a hole) starting at
    /// <paramref name="from"/> (km): the circular-orbit speed there and the escape speed, both Paczyński–Wiita near a hole
    /// (√(μ r)/(r − r_s) and √(2μ/(r − r_s))), Newton's otherwise. Two-body: the rest of the system is ignored.
    /// </summary>
    public static (double DistanceKm, double Circular, double Escape) LaunchSpeeds(IWorldBody target, Vec3 from, double projectileMass, double projectileRs = 0)
    {
        var r = Vec3.Length(from - target.Pos);
        var rs = target.HoleRs + projectileRs;
        var mu = G * (target.Mass + projectileMass);
        if (!(r > rs)) return (r, double.NaN, double.NaN);
        return (r, PseudoNewtonian.CircularSpeed(mu, r, rs), Math.Sqrt(2 * mu / (r - rs)));
    }

    /// <summary>
    /// Find a launch velocity from <paramref name="from"/> (km) that hits the target body with the given
    /// <paramref name="speed"/> (km/s) relative to it. Iteratively corrects the aim direction by the predicted miss
    /// (a shooting method). <paramref name="probeRadius"/> is km. Null when the target does not exist.
    /// </summary>
    public static AimResult? AutoAim(IWorld u, Vec3 from, string targetId, double speed, double probeRadius = 0.001, int iterations = 8, double pairBudget = AimPairBudget)
    {
        IWorldBody? target = null;
        foreach (var b in u.Bodies) if (b.Id == targetId) { target = b; break; }
        if (target is null) return null;
        var dir = Vec3.Normalize(target.Pos - from);
        var dist = Vec3.Length(target.Pos - from);
        var horizon = Math.Min((2.5 * dist) / Math.Max(speed, 0.1) + 3600, 5 * 365.25 * 86400);
        AimResult? best = null;
        double spent = 0;
        var stalls = 0;
        for (var iter = 0; iter < iterations; iter++)
        {
            var vel = target.Vel + dir * speed;
            var prediction = PredictPath(u, new Probe(from, vel, probeRadius), horizon, new PredictOptions { TrackId = targetId, MaxSteps = 4000, PairBudget = pairBudget - spent });
            spent += prediction.PairEvals;
            var converged = prediction.Hit is { } hit && hit.BodyId == targetId;
            var prevMiss = best?.Prediction.Closest?.Dist ?? double.PositiveInfinity;
            if (best is null || converged || (prediction.Closest is { } pc && best.Prediction.Closest is { } bc && pc.Dist < bc.Dist))
            {
                best = new AimResult { Vel = vel, Prediction = prediction, Converged = converged, PairEvals = 0 };
            }
            // Out of budget: a path that stops short cannot steer the next shot.
            if (converged || prediction.Truncated) break;
            // Give up on a shot that has stopped closing in. A small moon whipping round its planet can
            // defeat this correction entirely, and chasing it just spends the budget on the page's time.
            var missNow = prediction.Closest?.Dist ?? double.PositiveInfinity;
            stalls = missNow < prevMiss * 0.8 ? 0 : stalls + 1;
            if (stalls >= 2) break;
            if (prediction.Closest is not { } c) break;
            var miss = c.ProbePos - c.BodyPos;
            var L = Vec3.Length(c.BodyPos - from);
            dir = Vec3.Normalize(dir * L - miss);
        }
        if (best is not null) best.PairEvals = spent;
        return best;
    }
}
