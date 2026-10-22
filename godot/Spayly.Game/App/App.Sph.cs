using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading.Tasks;
using Godot;
using Spayly.Game.Render;
using Spayly.Sim;
using Spayly.Sim.Sph;

namespace Spayly.Game.App;

/// <summary>
/// Where a hero collision's debris remainder goes: the debris tier implements it and sets <see cref="App.DebrisSink"/>.
/// Units are Sim's: kg, kg·km/s, kg·km²/s about the world origin (ecliptic J2000), km for positions.
/// </summary>
public interface IDebrisSink
{
    /// <summary>
    /// Take the remainder: exactly <paramref name="mass"/>, <paramref name="momentum"/> and <paramref name="angularMomentum"/>,
    /// placed as the given particles (world km, km/s, kg) as nearly as those totals allow. False when it cannot (no room), so
    /// the caller keeps it.
    /// </summary>
    bool Inject(double mass, Vec3 momentum, Vec3 angularMomentum, IReadOnlyList<Vec3> positions, IReadOnlyList<Vec3> velocities, IReadOnlyList<double> masses);
}

/// <summary>Hero-collision knobs the View panel does not show; whether SPH runs at all is <see cref="Settings.Sph"/>.</summary>
public static class SphSettings
{
    /// <summary>Particles per hero collision by mode: M4 measured 3e4 at ~4.5 ms a frame and 1e5 at 13.0 ms.</summary>
    public static int ParticlesFor(SphMode m) => m == SphMode.Fast ? 30_000 : 100_000;
    /// <summary>Capture override of the particle count (<c>sphN</c>); 0 follows the mode.</summary>
    public static int ParticlesOverride;
    /// <summary>
    /// [GAME] Soft safety cap, wall-clock s: a run still going after this is demoted where it stands, with a notice. The
    /// physics ends a run (<see cref="HeroCollision.Finished"/>); this only keeps a very slow GPU from running for ever.
    /// </summary>
    public static double WallSeconds = 120;
    /// <summary>SPH time after which it is demoted, s.</summary>
    public static double EndTime = 8 * Constants.Hour;
    /// <summary>Substeps per frame; 0 = adaptive, as many as fit in the frame's SPH budget (<see cref="SubstepController.Budget"/>).</summary>
    public static int Substeps;
    /// <summary>
    /// Substeps between two finish checks. A step count, not an SPH time: Δt is chosen on the GPU and the CPU sees the clock
    /// only through a readback 20 frames apart, so no frame could be made to end on a given SPH time, while a frame can end
    /// on a given substep. Δt depends only on the state, so the same substep is the same SPH time on every machine. At the
    /// ~2.4 s steps M6.7 measured, 256 substeps is about 10 minutes of SPH time.
    /// </summary>
    public static int CheckEvery = 256;
    /// <summary>Headroom the SPH budget leaves under the 33 ms frame for what the non-SPH p95 misses, ms.</summary>
    public static double BudgetMarginMs = 3;
    public static readonly HeroTrigger Trigger = new();
}

/// <summary>
/// Hero collisions (opt-in): a planet-scale collision the camera is watching runs as a GPU SPH patch instead of the
/// analytic outcome. The collision gate wraps the handlers SimRunner installed, so every other collision still goes to
/// <see cref="Collide.ResolveCollision"/> and the configured OutcomeModel. While a patch runs, the pair is replaced in the
/// N-body by one hidden proxy body carrying their mass, centre of mass and momentum, so the rest of the system keeps
/// feeling them and the patch rides the proxy's orbit. [ART] The patch's own clock runs as fast as the GPU allows, far
/// ahead of the Universe's; its internal state is what gets demoted. On finish the clumps become bodies (the target's
/// heir keeps its id, name and maps) and the exact-difference remainder goes to <see cref="DebrisSink"/>, or to a logged
/// reservoir when there is no debris tier, so the ledger stays exact either way.
/// </summary>
public partial class App
{
    /// <summary>The debris tier's entry point for hero-collision remainders; null keeps them in <see cref="SphReservoir"/>.</summary>
    public IDebrisSink? DebrisSink { get; set; }

    /// <summary>Remainders nobody took yet: kg, kg·km/s, kg·km²/s, number of events.</summary>
    public (double Mass, Vec3 Momentum, Vec3 AngularMomentum, int Events) SphReservoir { get; private set; }

    /// <summary>Whether a hero collision is running now.</summary>
    public bool SphActive => sph != null;

    sealed class SphRun
    {
        public required HeroCollision Hero;
        public required Body Proxy;
        public required string? FocusId;
        /// <summary>The bodies' particles, the gravity box and the packed upload, built on a worker (<see cref="PrepareSph"/>).</summary>
        public required Task<SphPrep> Prep;
        public SphGpu? Gpu;
        public double Wall;
        public int Frames;
        public double LastLog;
        public double BoxFloor;
        public readonly SubstepController Pace = new();
        /// <summary>Substeps recorded so far; every <see cref="SphSettings.CheckEvery"/>-th ends a frame and is a finish check.</summary>
        public long Substeps;
        public double BudgetMs;
    }

    /// <summary>What <see cref="PrepareSph"/> builds off the main thread for the GPU start.</summary>
    sealed record SphPrep(SphOptions Options, double Box, SphGpu.Packed Packed, double Ms);

    // Frame times, ms, for the SPH budget: every frame, and the part of each frame that was not SPH (all of it when no
    // substep ran, else the frame minus k × the median substep).
    readonly RollingPercentile sphFrameMs = new(120), sphNonSphMs = new(120);
    int sphLastK;

    SphRun? sph;
    bool sphInstalled;
    bool sphSunk;
    double sphCooldownUntil;
    CollisionHandler[] sphOriginalHandlers = [];

    /// <summary>What the player picked in the SPH select, or Fast when High impact quality asks for SPH on its own.</summary>
    SphMode SphModeNow => Settings.Sph != SphMode.Off ? Settings.Sph : Settings.ImpactQuality == ImpactQuality.High ? SphMode.Fast : SphMode.Off;

    int SphParticles => SphSettings.ParticlesOverride > 0 ? SphSettings.ParticlesOverride : SphSettings.ParticlesFor(SphModeNow);

    /// <summary>Real seconds a High quality stretch is watched before judging it, and the frame time and share of slow frames that fail it.</summary>
    const double QualityWindow = 3, SlowFrameMs = 40, SlowFraction = 0.3;
    double qualityWindowT;
    int qualityFrames, qualitySlow;
    /// <summary>A capture or the player chose the quality: the automatic fallback leaves it alone.</summary>
    bool qualityPinned;

    /// <summary>The View panel's impact-quality select (and the <c>impactQuality:low|medium|high</c> verb). Choosing it pins it against the fallback.</summary>
    public void SetImpactQuality(ImpactQuality q, bool pin = true)
    {
        Settings.ImpactQuality = q;
        qualityPinned = pin;
        SphSettings.Trigger.Wide = q == ImpactQuality.High;
        ImpactEffects.Flat = q == ImpactQuality.Low;
        if (q == ImpactQuality.High) EnsureSph();
        qualityWindowT = 0;
        qualityFrames = qualitySlow = 0;
        GD.Print($"[sph] impact quality {q}: sph={SphModeNow} wide={SphSettings.Trigger.Wide} pinned={pin}");
    }

    /// <summary>
    /// Automatic fallback: while quality is High and not pinned, judge the real frame times over each <see cref="QualityWindow"/>
    /// and, when more than <see cref="SlowFraction"/> of them ran over <see cref="SlowFrameMs"/>, drop to Medium. Frames are not
    /// counted while a hero run is on (its cost is the SPH patch, which has its own wall cap) or under a Director slow-down.
    /// </summary>
    void WatchImpactFrames(double dtReal)
    {
        if (Settings.ImpactQuality != ImpactQuality.High || qualityPinned || sph != null) return;
        qualityFrames++;
        if (dtReal * 1000 > SlowFrameMs) qualitySlow++;
        qualityWindowT += dtReal;
        if (qualityWindowT < QualityWindow) return;
        var share = (double)qualitySlow / Math.Max(qualityFrames, 1);
        if (share > SlowFraction)
        {
            GD.Print(string.Create(CultureInfo.InvariantCulture, $"[sph] {share:P0} of {qualityFrames} frames over {SlowFrameMs} ms: impact quality High -> Medium"));
            SetImpactQuality(ImpactQuality.Medium, pin: false);
            return;
        }
        qualityWindowT = 0;
        qualityFrames = qualitySlow = 0;
    }

    /// <summary>The View panel's SPH select (and the <c>setSph:off|fast|hero</c> / <c>sph:1|0</c> verbs).</summary>
    public void SetSph(SphMode mode)
    {
        Settings.Sph = mode;
        if (mode != SphMode.Off) EnsureSph();
        GD.Print($"[sph] hero collisions {mode}: particles={SphParticles} cap={SphSettings.WallSeconds}s end={SphSettings.EndTime}s check_every={SphSettings.CheckEvery} substeps");
    }

    void EnsureSph()
    {
        if (sphInstalled) return;
        sphInstalled = true;
        // Wrap, do not replace: the handlers SimRunner (and anything after it) installed still see every other collision.
        sphOriginalHandlers = [.. Universe.CollisionHandlers];
        Universe.CollisionHandlers.Clear();
        Universe.CollisionHandlers.Add((ev, u) =>
        {
            if (SphGate(ev, u)) return;
            foreach (var h in sphOriginalHandlers) h(ev, u);
        });
        AddChild(new SphTicker { Name = "SphTicker", Tick = SphUpdate });
    }

    /// <summary>True when the collision became a hero collision (and so must not be resolved analytically).</summary>
    bool SphGate(CollisionEvent ev, Universe u)
    {
        if (SphModeNow == SphMode.Off || sph != null || realTime < sphCooldownUntil) return false;
        // Watched: the camera follows one of the pair, or the Director will cut to it (a giant impact is always Major).
        var focus = Rig.Focus;
        if (!ReferenceEquals(focus, ev.A) && !ReferenceEquals(focus, ev.B) && Settings.Director == DirectorMode.Off) return false;
        if (!HeroCollision.Qualifies(ev.A, ev.B, SphSettings.Trigger, out var regime, ev.PosA, ev.PosB)) return false;
        GD.Print(string.Create(CultureInfo.InvariantCulture,
            $"[sph] trigger: {ev.A.Id} + {ev.B.Id} Q_R/Q*_RD={regime.QR / regime.QStarRD:E3} γ={regime.Gamma:F4} b={regime.B:F3} v={regime.ImpactSpeed:F2} km/s ({regime.ImpactSpeed / regime.VEsc:F2} v_esc) regime={regime.Regime}"));
        return StartSph(ev.A, ev.B, ev.PosA, ev.PosB);
    }

    /// <summary>
    /// Swap the pair for their proxy now and build the SPH bodies on a worker; the GPU starts on the first frame after the
    /// build lands. Building 10⁵ particles took 144 ms and the gravity box's sort 18 ms, both in the collision's frame
    /// (capture m7-perf-sph-before); the build is a pure function of the two bodies, which leave the universe here and are
    /// not touched again, so it gives the same particles on any thread.
    /// </summary>
    bool StartSph(Body a, Body b, Vec3? posA, Vec3? posB)
    {
        var u = Universe;
        // What Build would throw on; checked here so a pair it cannot build still resolves analytically in this frame.
        if (HeroCollision.LayersFor(a) == null || HeroCollision.LayersFor(b) == null) return false;
        var hero = new HeroCollision(a, b, new HeroOptions { Particles = SphParticles, EndTime = SphSettings.EndTime }, posA, posB);
        u.PushHistory($"{hero.Impactor.Name} hitting {hero.Target.Name} (SPH)", kind: UndoKind.SphCollision, subject: hero.Impactor.Id);
        var t = hero.Target;
        var p = hero.Impactor;
        var proxy = Body.MakeBody(new BodySpec
        {
            Id = $"sph-{t.Id}-{((long)Math.Floor(u.Time)).ToString(CultureInfo.InvariantCulture)}",
            Name = $"{t.Name} + {p.Name}",
            // A fragment's view is a cheap procedural rock with no streamed maps; it is hidden anyway.
            Kind = BodyKind.Fragment,
            Surface = Surface.Lava,
            Mass = hero.TargetMass + hero.ImpactorMass,
            Radius = t.Radius,
            Pos = hero.CmPosition,
            Vel = hero.CmVelocity,
            PrimaryId = t.PrimaryId,
            Visual = new BodyVisual { Color = t.Visual.Color, Albedo = 0.1 },
            Seed = t.Seed,
            Born = u.Time,
        });
        ParkViews(a, b);
        u.Remove(a);
        u.Remove(b);
        u.Add(proxy);
        var wasFocus = Rig.Focus;
        sph = new SphRun { Hero = hero, Proxy = proxy, FocusId = Rig.Focus?.Id, Prep = Task.Run(() => PrepareSph(hero)) };
        var ev = PostEvent(new GameEvent
        {
            Kind = EventKind.GiantImpact, Severity = EventSeverity.Major, Title = $"Giant impact: {p.Name} hits {t.Name}",
            Detail = $"{SphParticles:N0}-particle SPH collision", SimTime = u.Time, BodyIds = [proxy.Id, t.Id, p.Id], Position = hero.CmPosition,
            FrameKm = 4.5 * (t.Radius + p.Radius),
        });
        // The pair is gone: a camera or selection that was on one of them moves to the proxy, but nobody else's view is touched.
        if (!ev.Cut && (ReferenceEquals(wasFocus, a) || ReferenceEquals(wasFocus, b))) Rig.Focus = proxy;
        if (ev.Cut || ReferenceEquals(Selected, a) || ReferenceEquals(Selected, b)) Select(proxy);
        return true;
    }

    /// <summary>Worker: build the bodies, then what the GPU start needs from them (pure CPU, reads only the hero).</summary>
    static SphPrep PrepareSph(HeroCollision hero)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        hero.Build();
        var o = hero.ResolvedOptions();
        var box = GravityBox(hero.State);
        var packed = SphGpu.Pack(hero.State, o);
        return new SphPrep(o, box, packed, System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
    }

    /// <summary>The build landed: start the GPU on it, or give the pair back to the analytic model if it failed.</summary>
    bool StartSphGpu(SphRun run)
    {
        if (run.Prep.IsFaulted)
        {
            GD.PushError($"[sph] could not build the hero collision, resolving it analytically: {run.Prep.Exception?.GetBaseException().Message}");
            FallBackToAnalytic(run);
            return false;
        }
        var prep = run.Prep.Result;
        var hero = run.Hero;
        var gpu = new SphGpu { Name = "SphGpu" };
        AddChild(gpu);
        run.Gpu = gpu;
        try
        {
            run.BoxFloor = prep.Box;
            gpu.Init(hero.State, prep.Options, run.BoxFloor, packed: prep.Packed);
        }
        catch (Exception e)
        {
            GD.PushError($"[sph] GPU start failed, falling back to the analytic outcome: {e.Message}");
            FallBackToAnalytic(run);
            return false;
        }
        var t = hero.Target;
        var p = hero.Impactor;
        // The SPH surfaces sit a little inside the bodies' radii; frame the pair by them, as the flight from StartSph nearly did.
        if (ReferenceEquals(Rig.Focus, run.Proxy))
            Rig.FlyTo(new ViewSpec { Focus = run.Proxy, SetFocus = true, Distance = 4.5 * (hero.TargetProfile!.Radius + hero.ImpactorProfile!.Radius) * 1e-3 }, 1.0);
        GD.Print(string.Create(CultureInfo.InvariantCulture,
            $"[sph] start: {t.Id} ({hero.TargetParticles} particles, R_sph={hero.TargetProfile!.Radius / 1e3:F0} km) + {p.Id} ({hero.State.Count - hero.TargetParticles}, R_sph={hero.ImpactorProfile!.Radius / 1e3:F0} km) v_rel={Vec3.Length(hero.RelativeVelocity):F3} km/s L_int={Vec3.Length(hero.AngularMomentum):E4} kg km²/s KE0={hero.InitialKinetic:E4} J proxy={run.Proxy.Id} built off the main thread in {prep.Ms:F1} ms frame={Engine.GetProcessFrames()}"));
        return true;
    }

    /// <summary>Per frame, after the App's own frame (the ticker is its child): GPU start, stepping, placement, finish.</summary>
    void SphUpdate(double dtReal)
    {
        var run = sph;
        SampleSphFrame(dtReal, run);
        if (run == null) return;
        if (Universe.ById(run.Proxy.Id) != run.Proxy)
        {
            // Undo, a fault rollback, the erase brush or a merge into something bigger took the proxy: the patch is moot.
            AbortSph(run, "the proxy left the universe");
            return;
        }
        if (views.TryGetValue(run.Proxy.Id, out var pv)) pv.Visible = false;
        if (run.Gpu == null)
        {
            // The bodies are still being built: the proxy flies on in the N-body and the patch has not started its clock.
            if (!run.Prep.IsCompleted || !StartSphGpu(run)) return;
        }
        var g = run.Gpu!;
        var at = ctx.Origin.Place(run.Proxy.Pos, out var squeeze, out _);
        // Towards the Sun in the render frame (sim z-up → Godot y-up), for the cloud's shading.
        var toSun = ctx.SunPos - run.Proxy.Pos;
        var sunDir = new Vector3((float)toSun.X, (float)toSun.Z, (float)-toSun.Y).Normalized();
        g.Place(at, squeeze, ctx.PixelsPerRadian, ctx.ExposureComp, sunDir.IsFinite() ? sunDir : Vector3.Right);
        if (Paused || Rewind) return;
        // A checkpoint's state is on its way back: no substeps until it lands, so the finish is judged on exactly it.
        if (g.DownloadPending)
        {
            run.Wall += dtReal;
            if (run.Wall >= SphSettings.WallSeconds) FinishSph(run, cutShort: true);
            return;
        }
        if (g.TakeDownload(out var box))
        {
            run.Hero.Time = g.SimTime;
            LogSphState(run);
            if (run.Hero.Finished)
            {
                FinishSph(run, fresh: true);
                return;
            }
            // The new gravity box changes the forces from here on, so it too starts at the checkpoint on every machine.
            g.SetGravityBox(box);
        }
        var k = SphSettings.Substeps;
        var frameP95 = sphFrameMs.Percentile(0.95);
        run.BudgetMs = SubstepController.Budget(sphNonSphMs.Percentile(0.95), SphSettings.BudgetMarginMs);
        // The median of the last 120 timed substeps, not the latest: M6.7's Fast run flipped 1 ↔ 16 on single samples.
        if (k <= 0) k = run.Pace.Next(g.SubstepStats().P50, run.BudgetMs, frameP95);
        // Never past a checkpoint, so the finish is judged at the same substeps whatever k the machine affords.
        k = Math.Min(k, SubstepController.ToCheckpoint(run.Substeps, SphSettings.CheckEvery));
        g.Step(k);
        sphLastK = k;
        run.Substeps += k;
        run.Frames++;
        run.Wall += dtReal;
        run.Hero.Time = g.SimTime;
        if (run.Wall - run.LastLog >= 1)
        {
            run.LastLog = run.Wall;
            var (p50, p95, n) = g.SubstepStats();
            GD.Print(string.Create(CultureInfo.InvariantCulture,
                $"[sph] gpu n={g.Count} gravity={(g.Tree ? "tree" : "direct")} substeps={k} ms_per_substep p50={p50:F3} p95={p95:F3} (samples {n}, dropped {g.TimestampsDropped}) frame_sph_ms={p50 * k:F2} budget={run.BudgetMs:F1}ms frame_p95={frameP95:F1}ms non_sph_p95={sphNonSphMs.Percentile(0.95):F1}ms sim_t={g.SimTime:F1}s dt={g.NextDt:F3}s steps={g.StepsTaken} substeps_total={run.Substeps} wall={run.Wall:F1}s box={g.GravityBox / 1e3:F0} km"));
        }
        // At a checkpoint the frame ended exactly on it, so the download, the finish test and the new gravity box all happen
        // at the same substep on every machine. The download is async; the next frames wait for it (above).
        if (run.Substeps % SphSettings.CheckEvery == 0)
        {
            var floor = run.BoxFloor;
            g.DownloadAsync(run.Hero.State, st => GravityBox(st, floor));
            return;
        }
        if (run.Wall >= SphSettings.WallSeconds) FinishSph(run, cutShort: true);
    }

    /// <summary>App start: compile the SPH kernels now, so the first hero collision does not (SphGpu.Warm).</summary>
    void WarmSph() => SphGpu.Warm();

    void SampleSphFrame(double dtReal, SphRun? run)
    {
        var ms = dtReal * 1e3;
        sphFrameMs.Add(ms);
        var sphMs = run?.Gpu != null && sphLastK > 0 ? sphLastK * run.Gpu.SubstepStats().P50 : 0;
        sphNonSphMs.Add(Math.Max(0, ms - sphMs));
        sphLastK = 0;
    }

    /// <summary>The soft cap as the notice says it: "2 minutes", "90 s".</summary>
    static string CapText()
    {
        var s = SphSettings.WallSeconds;
        return s >= 60 && s % 60 == 0 ? $"{s / 60:F0} minute{(s == 60 ? "" : "s")}" : string.Create(CultureInfo.InvariantCulture, $"{s:F0} s");
    }

    /// <summary>The HUD's progress line while a hero collision runs: SPH time, particles, and the soft cap once past 60 s. Null otherwise.</summary>
    string? SphProgressText()
    {
        if (sph is not { } run) return null;
        var g = run.Gpu;
        var inv = CultureInfo.InvariantCulture;
        return g == null
            ? $"SPH giant impact: building {SphParticles:N0} particles…"
            : string.Create(inv, $"SPH giant impact · {g.SimTime / 3600:F2} of {SphSettings.EndTime / 3600:F0} h simulated · {g.Count:N0} particles{(run.Wall > 60 ? $" · {run.Wall:F0} of {SphSettings.WallSeconds:F0} s cap" : "")}");
    }

    /// <summary>
    /// Pyramid half-size, m: 1.25 × the radius holding 99 % of the particles, never below <paramref name="floor"/>. Following
    /// the bulk rather than the farthest particle keeps the leaves fine; stragglers outside land in the edge leaves.
    /// </summary>
    static double GravityBox(SphState s, double floor = 0)
    {
        var r = new double[s.Count];
        for (var i = 0; i < s.Count; i++) r[i] = Math.Sqrt(s.X[i] * s.X[i] + s.Y[i] * s.Y[i] + s.Z[i] * s.Z[i]);
        Array.Sort(r);
        var box = Math.Max(1.25 * r[(int)(0.99 * (s.Count - 1))], floor);
        return floor > 0 ? box : Math.Max(box, 1.1 * r[^1]);
    }

    void LogSphState(SphRun run)
    {
        var h = run.Hero;
        var s = h.State;
        var now = s.Totals();
        var (ps, ls) = h.InitialScales;
        GD.Print(string.Create(CultureInfo.InvariantCulture,
            $"[sph] check t={h.Time:F0}s KE/KE0={s.KineticEnergy() / h.InitialKinetic:F4} U={s.InternalEnergy():E4} J drift: |ΔP|/Σm|v|={Vec3.Length(now.Momentum - h.Initial.Momentum) / ps:E3} |ΔL|/Σ|r×mv|={Vec3.Length(now.AngularMomentum - h.Initial.AngularMomentum) / ls:E3} ΔM/M={(now.Mass - h.Initial.Mass) / h.Initial.Mass:E3} finished={h.Finished}"));
    }

    /// <summary>
    /// Demote the patch: clumps → bodies, remainder → debris tier or reservoir, with the ledger logged.
    /// <paramref name="cutShort"/>: the soft wall cap ended it before the physics did, which the log and the hint say.
    /// <paramref name="fresh"/>: the state was just downloaded at a checkpoint.
    /// </summary>
    void FinishSph(SphRun run, bool cutShort = false, bool fresh = false)
    {
        var h = run.Hero;
        var g = run.Gpu!;
        // A finish found at a checkpoint already holds that checkpoint's state; downloading again would stall a frame for nothing.
        if (!fresh) g.Download(h.State);
        h.Time = g.SimTime;
        var u = Universe;
        var proxy = run.Proxy;
        var d = h.Demote(proxy.Pos, proxy.Vel, proxy.Mass, Math.Max(1, u.RoomForBodies + 1));
        var bodies = HeroCollision.CreateBodies(d, h.Target, h.Impactor, u.Time, h.State.Materials);
        u.Remove(proxy);
        foreach (var b in bodies) u.Add(b);

        var rem = d.Remainder;
        EnsureSphSink();
        sphSunk = DebrisSink is IDebrisSink sink && rem.Mass > 0 &&
                   sink.Inject(rem.Mass, rem.Momentum, rem.AngularMomentum, d.RemainderPositions, d.RemainderVelocities, d.RemainderMasses);
        if (!sphSunk)
        {
            var r = SphReservoir;
            SphReservoir = (r.Mass + rem.Mass, r.Momentum + rem.Momentum, r.AngularMomentum + rem.AngularMomentum, r.Events + 1);
        }
        if (cutShort)
            GD.Print(string.Create(CultureInfo.InvariantCulture,
                $"[sph] SPH collision cut short after {CapText()}: soft cap {SphSettings.WallSeconds:F0} s wall reached at t={h.Time:F0}s, substep {run.Substeps}, before the physics finished"));
        var (r50, r95, rMax, rN) = g.RunStats();
        GD.Print(string.Create(CultureInfo.InvariantCulture,
            $"[sph] run cost: ms_per_substep p50={r50:F3} p95={r95:F3} max={rMax:F3} over {rN} substeps, {run.Frames} frames, n={g.Count} box={g.GravityBox / 1e3:F0} km (floor {run.BoxFloor / 1e3:F0} km) frame={Engine.GetProcessFrames()}"));
        LogDemotion(run, d, bodies, cutShort);

        var heir = run.FocusId != null ? bodies.Find(b => b.Id == run.FocusId) : null;
        heir ??= bodies.Count > 0 ? bodies[0] : null;
        // Only a camera or selection that was on the collision follows it to its remains.
        if (heir != null && (Rig.Focus == null || ReferenceEquals(Rig.Focus, run.Proxy) || Universe.ById(Rig.Focus.Id) == null)) Rig.Focus = heir;
        if (heir != null && (Selected == null || ReferenceEquals(Selected, run.Proxy) || Universe.ById(Selected.Id) == null)) Select(heir);
        EndSph(run);
        BodiesVersion++;
        var dayText = bodies.Count > 0 && bodies[0].Rotation is Rotation r0 && r0.Rate != 0 ? $", day {UI.Format.Duration(Math.Abs(360 / r0.Rate) * 86400)}" : "";
        var head = cutShort ? $"SPH collision cut short after {CapText()}" : "SPH collision done";
        var ids = new string[bodies.Count];
        for (var i = 0; i < ids.Length; i++) ids[i] = bodies[i].Id;
        PostEvent(new GameEvent
        {
            Kind = EventKind.GiantImpactDone, Severity = EventSeverity.Minor, Title = head,
            Detail = bodies.Count > 0 ? $"{bodies.Count} bodies, largest {bodies[0].Name}{dayText}" : "everything became debris",
            SimTime = Universe.Time, BodyIds = ids, Position = bodies.Count > 0 ? bodies[0].Pos : run.Hero.CmPosition,
        });
    }

    /// <summary>Realism entry for the spin bodies bring into an SPH collision; the HUD calls this when it builds the Realism panel.</summary>
    public static void RegisterSphSpinRealism()
    {
        Realism.Register("Planets spin into giant impacts", RealismTag.Sci,
            "Each body enters an SPH collision turning rigidly at its real rotation rate about its real pole, so the day and the tilt that come out mix the spin it had with the spin the impact gave; that spin's angular momentum is part of what the collision conserves.",
            RealismArea.Collisions);
    }

    static double DayHours(Body b) => b.Rotation is Rotation r && r.Rate != 0 ? Math.Abs(360 / r.Rate) * 24 : double.PositiveInfinity;

    void LogDemotion(SphRun run, HeroDemotion d, List<Body> bodies, bool cutShort)
    {
        var h = run.Hero;
        var t = d.Truth;
        var c = d.Closure;
        GD.Print(string.Create(CultureInfo.InvariantCulture,
            $"[sph] demote at t={h.Time:F0}s after {run.Wall:F1}s wall, substep {run.Substeps}, ended by {(cutShort ? "cap" : "physics")}: {bodies.Count} bodies, linking {d.Sph.LinkingLength / 1e3:F1} km, remainder {d.RemainderMasses.Length} particles; spin brought in |L|={Vec3.Length(h.SpinAngularMomentum):E4} kg km²/s (target day {DayHours(h.Target):F2} h, impactor {DayHours(h.Impactor):F2} h), pair orbital |L|={Vec3.Length(h.AngularMomentum):E4}"));
        for (var k = 0; k < bodies.Count; k++)
        {
            var b = bodies[k];
            var db = d.Bodies[k];
            var w = Vec3.Length(db.Omega);
            GD.Print(string.Create(CultureInfo.InvariantCulture,
                $"[sph]   {b.Id}: M={b.Mass:E4} kg ({b.Mass / t.Mass:P2}) R={b.Radius:F0} km ρ̄={db.MeanDensity:F0} kg/m³ spin period={(w > 0 ? 2 * Math.PI / w / 3600 : double.PositiveInfinity):F2} h from target {db.MassBySource[0] / h.TargetMass:P1} impactor {(db.MassBySource.Length > 1 ? db.MassBySource[1] / h.ImpactorMass : 0):P1}"));
        }
        GD.Print(string.Create(CultureInfo.InvariantCulture,
            $"[sph] ledger truth M={t.Mass:E6} kg P={Vec3.Length(t.Momentum):E6} kg km/s L={Vec3.Length(t.AngularMomentum):E6} kg km²/s; remainder M={d.Remainder.Mass:E4} kg ({d.Remainder.Mass / t.Mass:P3}) P={Vec3.Length(d.Remainder.Momentum):E4} L={Vec3.Length(d.Remainder.AngularMomentum):E4} -> {(sphSunk ? "debris tier" : "reservoir")}"));
        GD.Print(string.Create(CultureInfo.InvariantCulture,
            $"[sph] ledger closure |ΔM|/M={Math.Abs(c.Mass) / t.Mass:E2} |ΔP|/|P|={Vec3.Length(c.Momentum) / Math.Max(Vec3.Length(t.Momentum), 1e-300):E2} |ΔL|/|L|={Vec3.Length(c.AngularMomentum) / Math.Max(Vec3.Length(t.AngularMomentum), 1e-300):E2}; sph drift M={d.Drift.Mass:E2} P={d.Drift.Momentum:E2} L={d.Drift.AngularMomentum:E2}"));
        var r = SphReservoir;
        if (r.Events > 0)
            GD.Print(string.Create(CultureInfo.InvariantCulture,
                $"[sph] reservoir now M={r.Mass:E4} kg P=({r.Momentum.X:E3}, {r.Momentum.Y:E3}, {r.Momentum.Z:E3}) L=({r.AngularMomentum.X:E3}, {r.AngularMomentum.Y:E3}, {r.AngularMomentum.Z:E3}) over {r.Events} events"));
    }

    /// <summary>The GPU could not run it: put the pair back where the proxy took them and let the normal model resolve it.</summary>
    void FallBackToAnalytic(SphRun run)
    {
        var h = run.Hero;
        var u = Universe;
        var dr = run.Proxy.Pos - h.CmPosition;
        var dv = run.Proxy.Vel - h.CmVelocity;
        u.Remove(run.Proxy);
        EndSph(run);
        var a = h.Target;
        var b = h.Impactor;
        a.Pos += dr; a.Vel += dv;
        b.Pos += dr; b.Vel += dv;
        u.Add(a);
        u.Add(b);
        var ev = new CollisionEvent(a, b, u.Time);
        foreach (var hd in sphOriginalHandlers) hd(ev, u);
    }

    void AbortSph(SphRun run, string why)
    {
        GD.Print($"[sph] abort: {why}; the SPH patch is dropped");
        EndSph(run);
    }

    void EndSph(SphRun run)
    {
        if (run.Gpu != null)
        {
            run.Gpu.Release();
            run.Gpu.QueueFree();
            run.Gpu = null;
        }
        if (ReferenceEquals(sph, run)) sph = null;
        sphCooldownUntil = realTime + 10;
    }

    /// <summary>
    /// Theia at contact with <paramref name="targetId"/> in the canonical Canup (2004) geometry: a Mars-mass body at 45°
    /// and the mutual escape speed, in the plane facing the camera, run straight away as a hero collision.
    /// </summary>
    void SphDemo(string targetId, string impactorName)
    {
        if (Universe.ById(targetId) is not Body target)
        {
            GD.PushError($"[sph] sphDemo: no body '{targetId}'");
            return;
        }
        if (sph != null)
        {
            GD.PushError("[sph] sphDemo: a hero collision is already running");
            return;
        }
        if (Settings.Sph == SphMode.Off) SetSph(SphMode.Hero);
        sphCooldownUntil = 0;
        var toCam = Vec3.Normalize(Rig.Position - target.Pos);
        if (Vec3.Length(toCam) == 0) toCam = new Vec3(0, -1, 0);
        var normal = Vec3.Normalize(Vec3.Cross(new Vec3(0, 0, 1), toCam));
        if (Vec3.Length(normal) < 1e-6) normal = Vec3.AnyPerpendicular(toCam);
        var tangent = Vec3.Normalize(Vec3.Cross(toCam, normal));
        const double marsMass = 6.417e23, marsRadius = 3389.5;
        var (pos, vel) = HeroCollision.ContactState(target, marsMass, marsRadius, 45, 1.0, normal, tangent);
        var name = string.IsNullOrWhiteSpace(impactorName) ? "theia" : impactorName.Trim();
        var id = name.ToLowerInvariant();
        for (var k = 2; Universe.ById(id) != null; k++) id = $"{name.ToLowerInvariant()}-{k}";
        var theia = Body.MakeBody(new BodySpec
        {
            Id = id, Name = char.ToUpperInvariant(name[0]) + name[1..], Kind = BodyKind.Planet, Surface = Surface.Rock,
            Mass = marsMass, Radius = marsRadius, Pos = pos, Vel = vel, PrimaryId = target.PrimaryId,
            Visual = new BodyVisual { Color = "#b5654a", Albedo = 0.25 }, Born = Universe.Time,
        });
        RecordUndo(UndoKind.Demo, $"the {theia.Name} impact demo");
        Universe.Add(theia);
        Rig.Focus = target;
        HeroCollision.Qualifies(target, theia, SphSettings.Trigger, out var r);
        GD.Print(string.Create(CultureInfo.InvariantCulture,
            $"[sph] demo: {theia.Id} at contact with {target.Id}, 45°, v={Vec3.Length(vel - target.Vel):F3} km/s (v_esc), Q_R/Q*_RD={r.QR / r.QStarRD:E3} regime={r.Regime}"));
        if (!StartSph(target, theia, null, null)) Universe.Remove(theia);
    }

    /// <summary>The GPU/CPU parity check at N = 2000 in the demo geometry (<c>sphParity:K</c>); prints its report.</summary>
    void SphParity(int steps)
    {
        var earth = Body.MakeBody(new BodySpec
        {
            Id = "parity-earth", Name = "Earth", Kind = BodyKind.Planet, Surface = Surface.Rock, Mass = 5.972e24, Radius = 6371,
            Visual = new BodyVisual { Color = "#6b93d6", Albedo = 0.3 },
        });
        var theia = Body.MakeBody(new BodySpec
        {
            Id = "parity-theia", Name = "Theia", Kind = BodyKind.Planet, Surface = Surface.Rock, Mass = 6.417e23, Radius = 3389.5,
            Visual = new BodyVisual { Color = "#b5654a", Albedo = 0.25 },
        });
        (theia.Pos, theia.Vel) = HeroCollision.ContactState(earth, theia.Mass, theia.Radius, 45, 1.0, new Vec3(1, 0, 0), new Vec3(0, 1, 0));
        var hero = new HeroCollision(earth, theia, new HeroOptions { Particles = 2000 });
        hero.Build();
        try
        {
            GD.Print(SphGpu.Parity(hero.State, hero.ResolvedOptions(), steps, GravityBox(hero.State)));
        }
        catch (Exception e)
        {
            GD.PushError($"[sph-parity] failed: {e}");
        }
    }

    /// <summary>Capture verbs for hero collisions; App.Capture routes them here.</summary>
    bool SphAction(string verb, string arg)
    {
        int Int(int fallback) => int.TryParse(arg.Replace("_", ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;
        switch (verb)
        {
            case "sph":
                SetSph(arg is "1" or "true" or "on" or "" ? SphMode.Hero : SphMode.Off);
                return true;
            case "impactQuality":
                SetImpactQuality(Enum.Parse<ImpactQuality>(arg, true));
                return true;
            case "setSph":
                SetSph(Enum.Parse<SphMode>(arg, true));
                return true;
            case "sphDemo":
            {
                var p = arg.Split(',');
                SphDemo(p.Length > 0 && p[0].Length > 0 ? p[0].Trim() : "earth", p.Length > 1 ? p[1] : "theia");
                return true;
            }
            case "sphN":
                SphSettings.ParticlesOverride = Math.Max(200, Int(SphParticles));
                GD.Print($"[sph] particles={SphParticles}");
                return true;
            case "sphSubsteps":
                SphSettings.Substeps = Math.Max(0, Int(0));
                GD.Print($"[sph] substeps={(SphSettings.Substeps == 0 ? "adaptive" : SphSettings.Substeps.ToString(CultureInfo.InvariantCulture))}");
                return true;
            case "sphWall":
                SphSettings.WallSeconds = double.Parse(arg, NumberStyles.Float, CultureInfo.InvariantCulture);
                return true;
            case "sphParity":
                SphParity(Math.Max(0, Int(10)));
                return true;
            case "logSph":
            {
                GD.Print($"[sph] hud {hud.SphProgressSummary}");
                var run = sph;
                var r = SphReservoir;
                GD.Print(string.Create(CultureInfo.InvariantCulture,
                    $"[sph] state: mode={Settings.Sph} active={run != null} proxy={run?.Proxy.Id ?? "-"} sim_t={run?.Gpu?.SimTime ?? 0:F1}s wall={run?.Wall ?? 0:F1}s reservoir M={r.Mass:E4} kg events={r.Events} bodies={Universe.Bodies.Count}"));
                return true;
            }
            default:
                return false;
        }
    }
}

/// <summary>Calls back once per frame after its parent's _Process: the hero collision's slot in the frame.</summary>
public partial class SphTicker : Node
{
    public Action<double>? Tick;

    public override void _Process(double delta) => Tick?.Invoke(Math.Clamp(delta, 0, 0.1));
}
