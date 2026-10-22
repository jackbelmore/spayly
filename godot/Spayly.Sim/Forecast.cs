namespace Spayly.Sim;

/// <summary>What a forecast event is. The order is the tie-break when two share a time.</summary>
public enum ForecastKind
{
    /// <summary>The hypothetical body touches another (swept, as <see cref="Universe"/> detects it), or two other bodies do.</summary>
    Contact,
    /// <summary>A body's two-body periapsis is inside its Roche limit (a tidal radius about a hole) and it is inside that limit: <see cref="Tidal.TidalCheck"/> would fire.</summary>
    RocheEntry,
    /// <summary>A body becomes bound to the hypothetical hole, which pulls it harder than the star does.</summary>
    Capture,
    /// <summary>A body that orbited the star becomes unbound from it (energy about the star turns positive).</summary>
    Ejection,
    /// <summary>The nearest the hypothetical body comes to a body that matters, when no contact makes it moot.</summary>
    ClosestApproach,
}

/// <summary>Why a forecast stopped integrating.</summary>
public enum ForecastEnd
{
    /// <summary>The horizon was reached.</summary>
    Horizon,
    /// <summary>The hypothetical body touched something; nothing after that is meaningful without resolving the collision.</summary>
    Contact,
    /// <summary>The substep budget ran out first: the forecast is truncated and says so.</summary>
    Budget,
}

/// <summary>One thing the forecast expects to happen.</summary>
/// <param name="Kind">What.</param>
/// <param name="Time">s after the forecast's start.</param>
/// <param name="SubjectId">The body it happens to (the Moon whose Roche limit is crossed, the planet approached).</param>
/// <param name="SubjectName">Its name.</param>
/// <param name="CauseId">The other party (the hypothetical body, or the primary that tears the subject), or null (Ejection).</param>
/// <param name="CauseName">Its name.</param>
/// <param name="DistanceKm">ClosestApproach: centre-to-centre distance at closest approach. RocheEntry: the limit. Otherwise 0. km.</param>
/// <param name="Pos">Where the subject is at that time, absolute km, for a marker.</param>
public readonly record struct ForecastEvent(
    ForecastKind Kind, double Time, string SubjectId, string SubjectName, string? CauseId, string? CauseName, double DistanceKm, Vec3 Pos);

/// <summary>A sampled path. <see cref="Times"/> are s after the forecast's start; <see cref="Points"/> absolute km.</summary>
public sealed class ForecastPath(string id, string name, bool hypothetical)
{
    public string Id { get; } = id;
    public string Name { get; } = name;
    /// <summary>The body the player is placing, as opposed to one it deflects.</summary>
    public bool Hypothetical { get; } = hypothetical;
    public List<Vec3> Points { get; } = [];
    public List<double> Times { get; } = [];
}

public sealed class ForecastResult
{
    /// <summary>In time order.</summary>
    public required IReadOnlyList<ForecastEvent> Events { get; init; }
    /// <summary>The hypothetical body's first (when there is one), then every body it measurably deflects.</summary>
    public required IReadOnlyList<ForecastPath> Paths { get; init; }
    public required ForecastEnd End { get; init; }
    /// <summary>s the forecast covers: the horizon, or the time of the contact or budget stop.</summary>
    public required double Duration { get; init; }
    /// <summary>s asked for.</summary>
    public required double Horizon { get; init; }
    /// <summary>Substeps spent, all chunks.</summary>
    public required int Substeps { get; init; }
    /// <summary>Moons left out because they orbit faster than <see cref="ForecastOptions.FastSatellitePeriod"/>.</summary>
    public int Omitted { get; init; }
    /// <summary>Every body's state when the run stopped, in the snapshot's order with the hypothetical body last: what a check against <see cref="Universe.Step"/> compares.</summary>
    public required IReadOnlyList<WorldBody> Final { get; init; }
    /// <summary>The budget ran out before the horizon: the forecast is incomplete.</summary>
    public bool Truncated => End == ForecastEnd.Budget;
}

public sealed record ForecastOptions
{
    /// <summary>s to look ahead; <see cref="double.NaN"/> picks <see cref="Forecast.HoleHorizon"/> for a black hole and <see cref="Forecast.RockHorizon"/> otherwise.</summary>
    public double Horizon { get; init; } = double.NaN;
    /// <summary>Integrator substeps allowed in all, a bound on the worker's wall time.</summary>
    public long SubstepBudget { get; init; } = Forecast.DefaultSubstepBudget;
    /// <summary>A body's path and closest approach are kept when the hypothetical body comes within this many of its Hill radii.</summary>
    public double HillRadii { get; init; } = Forecast.DefaultHillRadii;
    /// <summary>Longest stretch between looks at the state, s.</summary>
    public double MaxChunk { get; init; } = Forecast.DefaultMaxChunk;
    /// <summary>Most points one path keeps.</summary>
    public int MaxPathPoints { get; init; } = Forecast.DefaultMaxPathPoints;
    /// <summary>
    /// Moons that orbit faster than this, s, are left out of the forecast (0 keeps them all); see <see cref="Forecast.DefaultFastSatellitePeriod"/>.
    /// </summary>
    public double FastSatellitePeriod { get; init; } = Forecast.DefaultFastSatellitePeriod;
    /// <summary>The live universe's epoch (Julian date); only labels the clock.</summary>
    public double EpochJd { get; init; }
    /// <summary>The live universe's integrator settings, so the forecast steps as the game does (see <see cref="Universe"/>).</summary>
    public double MaxSubstep { get; init; } = 4 * 3600;
    public double MinSubstep { get; init; } = 0.25;
    public double HoleMinSubstep { get; init; } = 1e-6;
    public double DynFraction { get; init; } = 0.02;

    /// <summary>Settings copied from a live universe; call on the thread that owns it.</summary>
    public static ForecastOptions From(Universe u) => new()
    {
        EpochJd = u.EpochJd, MaxSubstep = u.MaxSubstep, MinSubstep = u.MinSubstep, HoleMinSubstep = u.HoleMinSubstep, DynFraction = u.DynFraction,
    };
}

/// <summary>
/// What a body the player is about to place (a black hole, a rock) would do: a copy of the universe is stepped forward with
/// the same integrator, gravity only, and the events that matter are noted on the way. The live universe is never touched:
/// it is not even passed in, only a <see cref="UniverseSnapshot"/>, so the run can sit on a worker thread.
///
/// Collisions are detected, not resolved. The first contact of the hypothetical body ends the run (what follows depends on an
/// impact model that a forecast has no business running); contacts between other bodies are noted and the bodies go on
/// through each other, softened as <see cref="Universe"/> softens a touching pair. Test particles (belts, debris) are not
/// simulated. The Roche test is Sim's own <see cref="Tidal.TidalCheck"/> on the forecast universe, so it fires on the state
/// the game's check would see; the contact time is the end of the substep that found it, as the game's is.
/// </summary>
public static class Forecast
{
    /// <summary>
    /// [GAME] Horizon for a hole, s. A hole's consequences are orbital: the inner planets turn through ten orbits and Jupiter
    /// through nearly one, so a perturbation that takes years to bite still shows. Longer costs more than a hover can pay.
    /// </summary>
    public const double HoleHorizon = 10 * Constants.Year;
    /// <summary>
    /// [GAME] Horizon for a rock, s. A rock's fate is decided within a transfer orbit or two (the hand-aim preview looks as
    /// far, and its masses leave the planets undisturbed), so more years only add noise from chaos.
    /// </summary>
    public const double RockHorizon = 2 * Constants.Year;
    /// <summary>
    /// [GAME] Substeps in all: at about 5 µs a substep (measured: 39 bodies less the fast moons) this is some ten seconds of one core, the
    /// most a player will wait for a hover to settle. A plunge into a hole shrinks the substep and eats it, and says so.
    /// </summary>
    public const long DefaultSubstepBudget = 2_000_000;
    /// <summary>[GAME] Hill radii of an affected body within which the hypothetical body counts as having come near.</summary>
    public const double DefaultHillRadii = 3;
    /// <summary>[GAME] One day: six substeps at the largest, so a look costs little, and fine enough to draw a Moon's orbit.</summary>
    public const double DefaultMaxChunk = Constants.Day;
    public const int DefaultMaxPathPoints = 1500;
    /// <summary>
    /// [GAME] Moons with a shorter orbit than this, s, are not simulated. The integrator's substep is a fiftieth of the
    /// shortest pair timescale, so Phobos (7.7 h) or Io (1.8 d) alone cost ten times the substeps of everything else together:
    /// ten years of the full system is minutes of CPU, without them seconds. Their mass is 1e-4 of their planet's or less, so
    /// the planets' paths do not notice; what is lost is a forecast event about those moons themselves. Five days keeps the
    /// Moon, Ganymede, Callisto, Titan and Triton.
    /// </summary>
    public const double DefaultFastSatellitePeriod = 5 * Constants.Day;

    /// <summary>[GAME] The nearest approach to the star worth naming, km: inside Mercury's orbit.</summary>
    const double StarAttentionKm = 0.25 * Constants.AU;
    /// <summary>
    /// [GAME] A body counts as deflected when the hypothetical body's pull has changed its velocity by this fraction of its
    /// speed about its primary; below it the shift is smaller than the width of the line that would show it.
    /// </summary>
    const double DeflectionFraction = 1e-4;
    /// <summary>[GAME] Tidal pull of the hypothetical body, as a fraction of a body's pull towards its primary, at which it counts as perturbed (2 %).</summary>
    const double PerturbationTidalRatio = 0.02;
    /// <summary>[GAME] A chunk is this fraction of the time the nearest body takes to close at the current speed.</summary>
    const double ApproachFraction = 0.1;
    /// <summary>[GAME] Path samples a path keeps over the horizon; near the hypothetical body it samples six times as often.</summary>
    const int PathSamples = 600;
    /// <summary>[GAME] Heading change, rad, at which thinning keeps a point of the hypothetical path whatever its spacing: a flyby's turn is drawn, not cut across.</summary>
    const double TurnRad = 0.05;
    /// <summary>The hypothetical path's raw points before thinning: a look per chunk, so a decade is a few thousand.</summary>
    const int RawPathPoints = 200_000;
    const string HypotheticalId = "forecast-hypothetical";

    sealed class Track(int index)
    {
        public readonly int Index = index;
        public double Hill = double.PositiveInfinity;
        public double Attention;
        public double Reach;
        public int PrimaryIdx = -1;
        public bool OrbitsStar;
        public double PrimaryDist;
        public double PrimaryMass;
        public double PrimarySpeed;
        public Vec3 PrimaryVel;
        public double DeflectDv;
        public bool Near;
        public bool Contacted;
        public double MinDist = double.PositiveInfinity;
        public double MinTime;
        public Vec3 MinPos;
        public double PrevDot;
        public Vec3 PrevRel, PrevRelVel;
        public bool Bound;
        public bool Ejected;
        public bool Captured;
        public ForecastPath? Path;
        public double LastSample = double.NegativeInfinity;
        public Vec3 LastDir;
    }

    /// <summary>
    /// Run the forecast. <paramref name="hypothetical"/> is a body not in the snapshot (its Pos km and Vel km/s are absolute); null
    /// forecasts the universe as it is, which is how the integration is checked against <see cref="Universe.Step"/>. Throws
    /// <see cref="OperationCanceledException"/> when <paramref name="cancel"/> fires; checked every chunk.
    /// </summary>
    public static ForecastResult Run(UniverseSnapshot snapshot, Body? hypothetical, ForecastOptions? options = null, CancellationToken cancel = default)
    {
        var opt = options ?? new ForecastOptions();
        var isHole = hypothetical?.Kind == BodyKind.BlackHole;
        var horizon = double.IsNaN(opt.Horizon) ? (isHole ? HoleHorizon : RockHorizon) : opt.Horizon;
        var u = new Universe(opt.EpochJd)
        {
            Time = snapshot.Time, MaxSubstep = opt.MaxSubstep, MinSubstep = opt.MinSubstep, HoleMinSubstep = opt.HoleMinSubstep,
            DynFraction = opt.DynFraction, MaxBodies = int.MaxValue,
        };
        foreach (var b in snapshot.Bodies) u.Add(b.Clone());
        var omitted = OmitFastMoons(u, opt.FastSatellitePeriod);
        var natural = u.Bodies.Count;
        var tracks = new List<Track>(natural + 1);
        for (var i = 0; i < natural; i++) tracks.Add(new Track(i));

        // What each body is bound to, from the system as it is before anything is added to it.
        var star = HeaviestIndex(u.Bodies);
        var hMass = hypothetical?.Mass ?? 0;
        for (var i = 0; i < natural; i++)
        {
            var b = u.Bodies[i];
            var t = tracks[i];
            if (u.PrimaryOf(b) is { } p)
            {
                var r = Vec3.Length(b.Pos - p.Pos);
                t.PrimaryIdx = u.Bodies.IndexOf(p);
                t.OrbitsStar = t.PrimaryIdx == star;
                t.PrimaryDist = r;
                t.PrimaryMass = p.Mass;
                t.PrimaryVel = p.Vel;
                t.PrimarySpeed = Math.Max(Vec3.Length(b.Vel - p.Vel), 1e-9);
                t.Hill = r * Math.Cbrt(b.Mass / (3 * p.Mass));
                t.Reach = r * Math.Cbrt(hMass / PerturbationTidalRatio * 2 / p.Mass);
            }
            // Only what orbits the star can be ejected from it: a moon's speed about the Sun swings through escape speed every orbit.
            if (i != star && t.OrbitsStar) t.Bound = HelioEnergy(b, u.Bodies[star]) < 0;
            // Pairs already inside a limit (nothing the hypothetical body did) are not news.
        }

        Body? hyp = null;
        int h = -1;
        if (hypothetical != null)
        {
            hyp = hypothetical.Clone();
            if (u.ById(hyp.Id) != null) throw new ArgumentException($"A body called {hyp.Id} is already in the snapshot", nameof(hypothetical));
            u.Add(hyp);
            h = u.Bodies.Count - 1;
        }
        var hypTrack = new Track(h);
        if (hyp != null)
        {
            hypTrack.Bound = HelioEnergy(hyp, u.Bodies[star]) < 0;
            hypTrack.Path = new ForecastPath(hyp.Id, hyp.Name, true);
            for (var i = 0; i < natural; i++)
            {
                var b = u.Bodies[i];
                var t = tracks[i];
                if (i == star) t.Attention = StarAttentionKm;
                else
                {
                    t.Attention = opt.HillRadii * t.Hill;
                    if (hyp.Mass > b.Mass && b.Radius >= Tidal.MinSecondaryRadiusKm) t.Attention = Math.Max(t.Attention, 2 * Tidal.LimitFor(hyp, b).Km);
                }
                if (double.IsInfinity(t.Attention) || double.IsNaN(t.Attention)) t.Attention = 0;
            }
        }
        for (var i = 0; i < natural; i++)
        {
            if (u.Bodies[i].Kind == BodyKind.Star) continue;
            tracks[i].Path = new ForecastPath(u.Bodies[i].Id, u.Bodies[i].Name, false);
        }

        var events = new List<ForecastEvent>();
        var t0 = u.Time;
        var end = ForecastEnd.Horizon;
        var contactSeen = new HashSet<string>();
        var stop = false;
        u.CollisionHandlers.Add((ev, uu) =>
        {
            var a = ev.A;
            var b = ev.B;
            var involvesHyp = hyp != null && (ReferenceEquals(a, hyp) || ReferenceEquals(b, hyp));
            if (involvesHyp) hypTrack.Contacted = true;
            if (!contactSeen.Add(a.Id + "|" + b.Id) || involvesHyp && stop) return;
            var other = involvesHyp && ReferenceEquals(a, hyp) ? b : a;
            var cause = involvesHyp ? hyp! : b;
            var at = ReferenceEquals(other, a) ? ev.PosA ?? a.Pos : ev.PosB ?? b.Pos;
            events.Add(new ForecastEvent(ForecastKind.Contact, ev.Time - t0, other.Id, other.Name, cause.Id, cause.Name, 0, at));
            var ix = u.Bodies.IndexOf(other);
            if (ix >= 0 && ix < natural) tracks[ix].Contacted = true;
            if (!involvesHyp) return;
            stop = true;
            // Leave the Step in progress at the end of this substep: the budget check at its top sees zero.
            uu.MaxSubstepsPerFrame = 0;
        });

        var tidalSeen = new HashSet<string>();
        var candidates = new List<TidalCandidate>();
        Tidal.TidalCheck(u, 0, candidates);
        foreach (var c in candidates)
            if (hyp == null || (!ReferenceEquals(c.Primary, hyp) && !ReferenceEquals(c.Secondary, hyp))) tidalSeen.Add(c.Primary.Id + "|" + c.Secondary.Id);
        var prevPos = new Vec3[u.Bodies.Count];

        var sampleDt = horizon / PathSamples;
        var substeps = 0L;
        var budget = opt.SubstepBudget;
        Sample(u, hyp, hypTrack, tracks, sampleDt, t0, opt.MaxPathPoints, true);
        Observe(u, hyp, h, star, tracks, hypTrack, natural, events, t0, -1, prevPos, tidalSeen, candidates, isHole);
        for (var i = 0; i < u.Bodies.Count; i++) prevPos[i] = u.Bodies[i].Pos;

        while (u.Time - t0 < horizon - 1e-6)
        {
            cancel.ThrowIfCancellationRequested();
            if (budget <= 0)
            {
                end = ForecastEnd.Budget;
                break;
            }
            var dt = Math.Min(ChooseChunk(u, h, opt, hyp), t0 + horizon - u.Time);
            u.MaxSubstepsPerFrame = (int)Math.Min(int.MaxValue, budget);
            var before = u.Time;
            u.Step(dt);
            substeps += u.SubstepsLastFrame;
            budget -= u.SubstepsLastFrame;
            Sample(u, hyp, hypTrack, tracks, sampleDt, t0, opt.MaxPathPoints, false);
            Observe(u, hyp, h, star, tracks, hypTrack, natural, events, t0, before - t0, prevPos, tidalSeen, candidates, isHole);
            for (var i = 0; i < u.Bodies.Count; i++) prevPos[i] = u.Bodies[i].Pos;
            if (stop)
            {
                end = ForecastEnd.Contact;
                break;
            }
            if (u.WarpLimited && u.Time < before + dt - 1e-6)
            {
                end = ForecastEnd.Budget;
                break;
            }
        }

        var duration = u.Time - t0;
        // Each other path ends where the run does.
        foreach (var t in tracks)
            if (t.Path is { } tp && tp.Points.Count > 0 && tp.Times[^1] < duration - 1e-6 && tp.Points.Count < opt.MaxPathPoints + 1)
            {
                tp.Points.Add(u.Bodies[t.Index].Pos);
                tp.Times.Add(duration);
            }
        if (hypTrack.Path != null) Thin(hypTrack.Path, opt.MaxPathPoints);
        if (hyp != null)
        {
            for (var i = 0; i < natural; i++)
            {
                var t = tracks[i];
                // After a contact the closest approach to that body is the contact itself.
                // A minimum at the first look is the start of a recession, not an approach.
                if (t.Contacted || !(t.MinDist < t.Attention) || t.Attention <= 0 || t.MinTime <= t0) continue;
                var b = u.Bodies[i];
                events.Add(new ForecastEvent(ForecastKind.ClosestApproach, t.MinTime - t0, b.Id, b.Name, hyp.Id, hyp.Name, t.MinDist, t.MinPos));
            }
        }
        events.Sort((x, y) => x.Time != y.Time ? x.Time.CompareTo(y.Time) : x.Kind.CompareTo(y.Kind));

        var paths = new List<ForecastPath>();
        if (hypTrack.Path != null) paths.Add(hypTrack.Path);
        for (var i = 0; i < natural; i++)
        {
            var t = tracks[i];
            if (t.Path == null || t.Path.Points.Count < 2) continue;
            var near = hyp != null && t.Near && t.DeflectDv > DeflectionFraction * t.PrimarySpeed;
            if (near) paths.Add(t.Path);
        }
        var final = new List<WorldBody>(u.Bodies.Count);
        foreach (var b in u.Bodies) final.Add(new WorldBody(b.Id, b.Pos, b.Vel, b.Mass, b.Radius, PseudoNewtonian.HoleRs(b)));
        return new ForecastResult { Omitted = omitted, Final = final, Events = events, Paths = paths, End = end, Duration = duration, Horizon = horizon, Substeps = (int)Math.Min(substeps, int.MaxValue) };
    }

    /// <summary>Remove moons whose orbit about their primary is shorter than <paramref name="period"/> s; returns how many.</summary>
    static int OmitFastMoons(Universe u, double period)
    {
        if (!(period > 0)) return 0;
        var drop = new List<Body>();
        foreach (var b in u.Bodies)
        {
            if (b.Kind != BodyKind.Moon || u.PrimaryOf(b) is not { } p) continue;
            var r = Vec3.Length(b.Pos - p.Pos);
            if (2 * Math.PI * Math.Sqrt(r * r * r / (Constants.G * (p.Mass + b.Mass))) < period) drop.Add(b);
        }
        foreach (var b in drop) u.Remove(b);
        return drop.Count;
    }

    /// <summary>
    /// Thin <paramref name="path"/> to about <paramref name="max"/> points: one wherever the path has run its share of the
    /// total arc length, or has turned by <see cref="TurnRad"/> since the last one kept, and always the first and last.
    /// </summary>
    static void Thin(ForecastPath path, int max)
    {
        var n = path.Points.Count;
        if (n <= max) return;
        double total = 0;
        for (var i = 1; i < n; i++) total += Vec3.Distance(path.Points[i], path.Points[i - 1]);
        var share = total / (max * 0.8);
        var pts = new List<Vec3> { path.Points[0] };
        var times = new List<double> { path.Times[0] };
        double run = 0;
        var lastDir = Vec3.Zero;
        for (var i = 1; i < n; i++)
        {
            var seg = path.Points[i] - path.Points[i - 1];
            run += Vec3.Length(seg);
            var dir = Vec3.Length(seg) > 0 ? Vec3.Normalize(seg) : lastDir;
            var turned = Vec3.Dot(dir, lastDir) < Math.Cos(TurnRad) && Vec3.Length(lastDir) > 0;
            if ((run >= share || turned || i == n - 1) && pts.Count < max)
            {
                pts.Add(path.Points[i]);
                times.Add(path.Times[i]);
                run = 0;
                lastDir = dir;
            }
            else if (run < share && !turned) lastDir = Vec3.Length(lastDir) > 0 ? lastDir : dir;
        }
        if (times[^1] != path.Times[^1])
        {
            pts[^1] = path.Points[n - 1];
            times[^1] = path.Times[n - 1];
        }
        path.Points.Clear();
        path.Points.AddRange(pts);
        path.Times.Clear();
        path.Times.AddRange(times);
    }

    static int HeaviestIndex(List<Body> bodies)
    {
        var best = 0;
        for (var i = 1; i < bodies.Count; i++) if (bodies[i].Mass > bodies[best].Mass) best = i;
        return best;
    }

    /// <summary>Specific energy of <paramref name="b"/> about <paramref name="star"/>, km²/s²: negative when bound.</summary>
    static double HelioEnergy(Body b, Body star)
    {
        var v = b.Vel - star.Vel;
        return 0.5 * Vec3.Dot(v, v) - Constants.G * (star.Mass + b.Mass) / Math.Max(Vec3.Length(b.Pos - star.Pos), 1e-9);
    }

    /// <summary>
    /// The next stretch to integrate, s: a tenth of the time the nearest body takes to close at its present speed, so an
    /// encounter gets about ten looks per approach however fast it is, but never less than two substeps (a look costs a force
    /// evaluation) nor more than <see cref="ForecastOptions.MaxChunk"/>.
    /// </summary>
    static double ChooseChunk(Universe u, int h, ForecastOptions opt, Body? hyp)
    {
        if (hyp == null) return opt.MaxChunk;
        var closing = double.PositiveInfinity;
        var bodies = u.Bodies;
        for (var i = 0; i < bodies.Count; i++)
        {
            if (i == h) continue;
            var d = Vec3.Length(bodies[i].Pos - hyp.Pos);
            var v = Vec3.Length(bodies[i].Vel - hyp.Vel);
            var t = d / Math.Max(v, 1e-3);
            if (t < closing) closing = t;
        }
        var floor = 2 * u.DynFraction * u.ShortestDynamicalTime;
        if (!double.IsFinite(floor)) floor = 0;
        return Math.Clamp(ApproachFraction * closing, Math.Min(floor, opt.MaxChunk), opt.MaxChunk);
    }

    static void Sample(Universe u, Body? hyp, Track hypTrack, List<Track> tracks, double sampleDt, double t0, int maxPoints, bool first)
    {
        var now = u.Time - t0;
        if (hyp != null) SampleOne(hyp, hypTrack, now, sampleDt, maxPoints, first, true);
        foreach (var t in tracks)
            if (t.Path != null) SampleOne(u.Bodies[t.Index], t, now, hyp != null && t.Near ? sampleDt / 6 : sampleDt, maxPoints, first, false);
    }

    static void SampleOne(Body b, Track t, double now, double dt, int maxPoints, bool first, bool hypothetical)
    {
        var path = t.Path!;
        // The hypothetical body is kept at every look (the looks are dense where it matters) and thinned at the end, so a
        // short flight still has a line; the others are sampled by the clock and capped as they go.
        if (hypothetical)
        {
            if (path.Points.Count < RawPathPoints)
            {
                path.Points.Add(b.Pos);
                path.Times.Add(now);
            }
            return;
        }
        if (path.Points.Count >= maxPoints) return;
        var dir = Vec3.Length(b.Vel) > 0 ? Vec3.Normalize(b.Vel) : Vec3.Zero;
        if (!first && now - t.LastSample < dt) return;
        t.LastSample = now;
        t.LastDir = dir;
        path.Points.Add(b.Pos);
        path.Times.Add(now);
    }

    /// <summary>
    /// Everything noted after a chunk, from the state the integrator left. <paramref name="prevTime"/> is when the last look was,
    /// s after the start, or negative for the first; <paramref name="prevPos"/> holds the bodies then.
    /// </summary>
    static void Observe(Universe u, Body? hyp, int h, int star, List<Track> tracks, Track hypTrack, int natural, List<ForecastEvent> events,
        double t0, double prevTime, Vec3[] prevPos, HashSet<string> tidalSeen, List<TidalCandidate> candidates, bool isHole)
    {
        var bodies = u.Bodies;
        var rel = u.Time - t0;
        var chunk = prevTime < 0 ? 0 : rel - prevTime;
        if (hyp != null)
        {
            var starBody = bodies[star];
            for (var i = 0; i < natural; i++)
            {
                var b = bodies[i];
                var t = tracks[i];
                var r = b.Pos - hyp.Pos;
                var v = b.Vel - hyp.Vel;
                var d = Vec3.Length(r);
                var dot = Vec3.Dot(r, v);
                if (d < Math.Max(t.Attention, t.Reach)) t.Near = true;
                // Velocity the hypothetical body's pull has given it, over the chunk just integrated, at the distance it ended at.
                if (chunk > 0 && d > 0) t.DeflectDv += Constants.G * hyp.Mass / (d * d) * chunk;
                if (d < t.MinDist)
                {
                    t.MinDist = d;
                    t.MinTime = u.Time;
                    t.MinPos = b.Pos;
                }
                // A pass through periapsis between two looks: the cubic through both ends has the minimum the looks missed.
                if (chunk > 0 && t.PrevDot < 0 && dot >= 0)
                {
                    var (dm, sm) = HermiteMin(t.PrevRel, t.PrevRelVel, r, v, chunk);
                    if (dm < t.MinDist)
                    {
                        t.MinDist = dm;
                        t.MinTime = t0 + prevTime + sm * chunk;
                        t.MinPos = sm < 0.5 ? prevPos[i] : b.Pos;
                    }
                }
                t.PrevDot = dot;
                t.PrevRel = r;
                t.PrevRelVel = v;

                // A moon goes with its planet: naming the planet says it.
                var withPrimary = t.PrimaryIdx >= 0 && t.PrimaryIdx < natural && tracks[t.PrimaryIdx].Captured;
                if (isHole && i != star && !t.Captured && IsDominatedBy(b, hyp, starBody))
                {
                    t.Captured = true;
                    if (!withPrimary) events.Add(new ForecastEvent(ForecastKind.Capture, rel, b.Id, b.Name, hyp.Id, hyp.Name, 0, b.Pos));
                }
            }
        }

        // The star is the reference for ejection; a body bound to it that reaches escape speed leaves.
        for (var i = 0; i < bodies.Count; i++)
        {
            if (i == star) continue;
            var t = i == h ? hypTrack : tracks[i];
            // A body that has touched something goes no further in a run that does not resolve the touch.
            if (!t.Bound || t.Ejected || t.Contacted) continue;
            if (HelioEnergy(bodies[i], bodies[star]) >= 0)
            {
                t.Ejected = true;
                var b = bodies[i];
                events.Add(new ForecastEvent(ForecastKind.Ejection, rel, b.Id, b.Name, null, null, 0, b.Pos));
            }
        }

        Tidal.TidalCheck(u, 0, candidates);
        foreach (var c in candidates)
        {
            var key = c.Primary.Id + "|" + c.Secondary.Id;
            if (!tidalSeen.Add(key)) continue;
            var time = rel;
            // Entered since the last look: the crossing, interpolated linearly in distance to the limit.
            var ip = IndexOf(bodies, c.Primary);
            var isx = IndexOf(bodies, c.Secondary);
            if (chunk > 0 && ip >= 0 && isx >= 0)
            {
                var dPrev = Vec3.Length(prevPos[isx] - prevPos[ip]);
                var dNow = Vec3.Length(c.Secondary.Pos - c.Primary.Pos);
                if (dPrev > c.LimitKm && dPrev > dNow) time = prevTime + chunk * Math.Clamp((dPrev - c.LimitKm) / (dPrev - dNow), 0, 1);
            }
            events.Add(new ForecastEvent(ForecastKind.RocheEntry, time, c.Secondary.Id, c.Secondary.Name, c.Primary.Id, c.Primary.Name, c.LimitKm, c.Secondary.Pos));
        }
    }

    /// <summary>
    /// True when the hole pulls <paramref name="b"/> harder than the star does, and it is bound to the hole: a body the hole has
    /// taken from the star's care, not merely one on a slow enough path past a heavy hole far away.
    /// </summary>
    static bool IsDominatedBy(Body b, Body hole, Body star)
    {
        var d = Vec3.Distance(b.Pos, hole.Pos);
        var rStar = Vec3.Distance(b.Pos, star.Pos);
        if (!(hole.Mass / (d * d) > star.Mass / (rStar * rStar))) return false;
        var v = b.Vel - hole.Vel;
        return 0.5 * Vec3.Dot(v, v) - Constants.G * (hole.Mass + b.Mass) / d < 0;
    }

    static int IndexOf(List<Body> bodies, Body b)
    {
        for (var i = 0; i < bodies.Count; i++) if (ReferenceEquals(bodies[i], b)) return i;
        return -1;
    }

    /// <summary>Minimum |r| of the cubic Hermite through (r0, v0) and (r1, v1) a time <paramref name="dt"/> apart: (km, fraction of dt).</summary>
    static (double Dist, double S) HermiteMin(Vec3 r0, Vec3 v0, Vec3 r1, Vec3 v1, double dt)
    {
        var best = double.PositiveInfinity;
        var bestS = 0.0;
        const int n = 64;
        for (var k = 0; k <= n; k++)
        {
            var s = (double)k / n;
            var s2 = s * s;
            var s3 = s2 * s;
            var p = r0 * (2 * s3 - 3 * s2 + 1) + v0 * (dt * (s3 - 2 * s2 + s)) + r1 * (-2 * s3 + 3 * s2) + v1 * (dt * (s3 - s2));
            var d = Vec3.Length(p);
            if (d < best)
            {
                best = d;
                bestS = s;
            }
        }
        return (best, bestS);
    }
}
