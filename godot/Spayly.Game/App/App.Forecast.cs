using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Spayly.Game.Render;
using Spayly.Game.UI;
using Spayly.Sim;

namespace Spayly.Game.App;

/// <summary>
/// "What will this do?" before the player commits. While the pointer hovers or drags in black-hole mode, or a rock is aimed in
/// asteroid mode (a hand aim, or the pointer resting on a body that a click would auto-aim at), a copy of the universe is
/// stepped forward with the hypothetical body on a worker (<see cref="Spayly.Sim.Forecast"/>) and the outcome is drawn: the
/// ghost path, the deflected paths of what it disturbs, a marker per event, and a one-line list in the mode panel.
///
/// The live universe is only read, on this thread, to take the snapshot; the worker owns every body it touches. At most one job
/// runs: a request waits until the pointer has rested <see cref="ForecastSettleSeconds"/>, a request that changes while its job
/// runs cancels it, and a result that no longer matches what the pointer wants is dropped. Everything is cleared on leaving
/// the mode and on placing.
///
/// Capture verbs: <c>forecastAt:x,y</c> (compute synchronously for the pointer at viewport px x,y in the current mode, draw it and
/// log the list), <c>hover:x,y</c> (a pointer move with no button, through the input path), <c>logForecast</c>.
/// </summary>
public partial class App
{
    /// <summary>[GAME] The pointer must rest this long, s, before a forecast starts: a drag is not a stream of requests.</summary>
    const double ForecastSettleSeconds = 0.1;
    /// <summary>[GAME] A forecast of an unchanged request is redone no sooner than this, s, when the sim has moved on under it.</summary>
    const double ForecastRefreshSeconds = 1;
    /// <summary>[GAME] Sim time, as a fraction of the horizon, after which a shown forecast has drifted from the live system.</summary>
    const double ForecastDriftFraction = 0.005;
    const string ForecastBodyId = "forecast-hypothetical";
    const int ForecastTextEvents = 4;

    readonly record struct ForecastKey(bool Hole, Vec3 Pos, Vec3 Vel, double Mass, string? Target, double Speed, Vec3 From);

    sealed class ForecastRequest
    {
        public required ForecastKey Key;
        public required Body Hyp;
        public bool Hole;
        /// <summary>Set for a click-to-fire rock: the worker solves the aim first, from <see cref="Key"/>'s From.</summary>
        public string? AutoTarget;
        public double AutoSpeed;
    }

    sealed class ForecastJob
    {
        public required ForecastRequest Req;
        public required Task<ForecastResult?> Work;
        public required CancellationTokenSource Cts;
        public double StartSim;
        public int Version;
        public long Started;
    }

    sealed class ForecastShown
    {
        public required ForecastResult Result;
        public required ForecastKey Key;
        public required bool Hole;
        public required double StartSim;
        public required int Version;
        public required double Ms;
        public required string Text;
        public required List<ForecastEvent> Marked;
    }

    ForecastOverlay? forecastOverlay;
    Label? forecastLabel;
    ForecastJob? forecastJob;
    /// <summary>A cancelled job's task: the worker stops at its next chunk, and no second one starts before it has.</summary>
    Task? forecastZombie;
    ForecastShown? forecastShown;
    ForecastRequest? forecastWanted;
    double forecastWantedAt, forecastLastStart = double.NegativeInfinity;
    bool forecastSuppressed;
    string forecastText = "";
    int forecastStarted, forecastDone, forecastDropped, forecastCancelled;
    double forecastMainSumMs, forecastMainMaxMs;
    long forecastMainCalls;
    /// <summary>Worst single call of each main-thread step, ms: taking the snapshot, accepting a result, drawing.</summary>
    double forecastStartMaxMs, forecastAcceptMaxMs, forecastDrawMaxMs;

    /// <summary>Step 13, after the hole mode: request, collect and draw the forecast. Everything the frame pays for is timed.</summary>
    void UpdateForecast()
    {
        var t0 = Stopwatch.GetTimestamp();
        UpdateForecastInner();
        var ms = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        forecastMainSumMs += ms;
        forecastMainMaxMs = Math.Max(forecastMainMaxMs, ms);
        forecastMainCalls++;
    }

    void UpdateForecastInner()
    {
        PollForecast();
        if (Mode is not (Mode.BlackHole or Mode.Asteroid))
        {
            if (forecastShown != null || forecastJob != null || forecastWanted != null) ClearForecast();
            forecastSuppressed = false;
            return;
        }
        var want = forecastSuppressed ? null : DesiredForecast(null);
        if (want == null)
        {
            if (forecastShown != null || forecastJob != null || forecastWanted != null) ClearForecast(keepSuppressed: true);
            return;
        }
        if (forecastWanted == null || forecastWanted.Key != want.Key)
        {
            forecastWanted = want;
            forecastWantedAt = realTime;
            if (forecastJob != null && forecastJob.Req.Key != want.Key) CancelForecastJob();
        }
        var shown = forecastShown;
        var same = shown != null && shown.Key == want.Key;
        var horizon = want.Hole ? Spayly.Sim.Forecast.HoleHorizon : Spayly.Sim.Forecast.RockHorizon;
        var drifted = shown != null && (shown.Version != BodiesVersion || Math.Abs(Universe.Time - shown.StartSim) > ForecastDriftFraction * horizon);
        var needs = !same || drifted;
        var settled = realTime - forecastWantedAt >= ForecastSettleSeconds;
        var due = !same || realTime - forecastLastStart >= ForecastRefreshSeconds;
        if (forecastJob == null && (forecastZombie == null || forecastZombie.IsCompleted) && needs && settled && due)
        {
            var t0 = Stopwatch.GetTimestamp();
            StartForecast(want);
            forecastStartMaxMs = Math.Max(forecastStartMaxMs, Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
        }
        var td = Stopwatch.GetTimestamp();
        ShowForecast(want, same && !drifted);
        forecastDrawMaxMs = Math.Max(forecastDrawMaxMs, Stopwatch.GetElapsedTime(td).TotalMilliseconds);
    }

    /// <summary>Draw what is held, dimmed when it is not for the pointer's present request, and keep the panel's line current.</summary>
    void ShowForecast(ForecastRequest want, bool current)
    {
        EnsureForecastOverlay();
        var shown = forecastShown;
        if (shown == null)
        {
            forecastOverlay!.Clear();
            forecastLabel!.Visible = false;
            SetForecastText(forecastJob != null ? "Forecasting…" : "");
            return;
        }
        var first = forecastOverlay!.Show(shown.Result, shown.Marked, ctx, current ? 1f : 0.35f);
        SetForecastText(current ? shown.Text : shown.Text + (forecastJob != null ? "  (updating…)" : ""));
        if (first is Vector3 at && shown.Marked.Count > 0 && labels.Project(at, out var screen))
        {
            forecastLabel!.Text = ForecastEventText(shown.Marked[0], shown.Hole);
            forecastLabel.Position = screen + new Vector2(14, -30);
            forecastLabel.Modulate = new Color(1, 1, 1, current ? 1f : 0.45f);
            forecastLabel.Visible = true;
        }
        else forecastLabel!.Visible = false;
    }

    void SetForecastText(string text)
    {
        if (text == forecastText) return;
        forecastText = text;
        hud.SetForecast(text);
    }

    void EnsureForecastOverlay()
    {
        if (forecastOverlay != null) return;
        forecastOverlay = new ForecastOverlay();
        AddChild(forecastOverlay);
        forecastLabel = new Label { MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
        forecastLabel.AddThemeColorOverride("font_color", new Color("#ffb454"));
        forecastLabel.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 0.9f));
        forecastLabel.AddThemeConstantOverride("outline_size", 4);
        forecastLabel.AddThemeFontSizeOverride("font_size", 14);
        labels.AddChild(forecastLabel);
    }

    /// <summary>Drop the forecast, in flight and shown; <paramref name="placed"/> also holds a new one back until the pointer moves.</summary>
    void ClearForecast(bool placed = false, bool keepSuppressed = false)
    {
        CancelForecastJob();
        forecastShown = null;
        forecastWanted = null;
        forecastOverlay?.Clear();
        if (forecastLabel != null) forecastLabel.Visible = false;
        SetForecastText("");
        if (placed) forecastSuppressed = true;
        else if (!keepSuppressed) forecastSuppressed = false;
    }

    void CancelForecastJob()
    {
        if (forecastJob is not { } j) return;
        j.Cts.Cancel();
        forecastZombie = j.Work;
        forecastJob = null;
        forecastCancelled++;
    }

    // ---------- the request: what the pointer would place ----------

    /// <summary>
    /// What a click here would place, or null. Black-hole mode: the panel's mass and velocity at the aim-plane point under the
    /// pointer, or the drag's velocity while one runs. Asteroid mode: the hand aim being dragged, or the rock a click on the body
    /// under the pointer would fire (aimed on the worker).
    /// </summary>
    ForecastRequest? DesiredForecast(Vector2? pointer)
    {
        if (Mode == Mode.BlackHole)
        {
            var mass = HoleSettings.MassSolar * Constants.SunMass;
            Vec3 pos;
            Vec3 vel;
            if (holeDrag is { } a)
            {
                if (a.Primary == null) return null;
                pos = a.Spawn;
                vel = (a.Vel ?? HoleDragVelocity(a)) ?? a.Primary.Vel;
            }
            else
            {
                var at = pointer ?? holePointer;
                if (at is not Vector2 pt || AimPlanePoint(pt) is not Vec3 p) return null;
                pos = ClearOfBodies(p, BlackHole.SchwarzschildRadiusKm(mass));
                var primary = Universe.DominantBody(pos);
                vel = HoleSettings.Velocity == HoleVelocity.Drag ? primary?.Vel ?? Vec3.Zero : HoleStartVelocity(pos, primary, mass);
            }
            var hole = Body.MakeBody(new BodySpec
            {
                Id = ForecastBodyId, Name = "The black hole", Kind = BodyKind.BlackHole, Surface = Surface.Star, Mass = mass,
                Radius = BlackHole.SchwarzschildRadiusKm(mass), Pos = pos, Vel = vel, Visual = new BodyVisual { Color = "#8a7fb0", Albedo = 0 },
                Born = Universe.Time,
            });
            return new ForecastRequest { Key = new ForecastKey(true, pos, vel, mass, null, 0, Vec3.Zero), Hyp = hole, Hole = true };
        }
        if (Mode != Mode.Asteroid) return null;
        if (aim is { Vel: Vec3 v } hand)
        {
            var rock = NewRock(ForecastBodyId, "The rock", Asteroid.DiameterM, Asteroid.Composition, hand.Spawn, v, null);
            return new ForecastRequest { Key = new ForecastKey(false, hand.Spawn, v, rock.Mass, null, 0, Vec3.Zero), Hyp = rock };
        }
        if (aiming) return null;
        var over = pointer ?? holePointer;
        if (over is not Vector2 ptr || PickBody(ptr) is not Body target) return null;
        var from = LaunchPoint();
        var auto = NewRock(ForecastBodyId, "The rock", Asteroid.DiameterM, Asteroid.Composition, from, Vec3.Zero, target);
        return new ForecastRequest
        {
            Key = new ForecastKey(false, from, Vec3.Zero, auto.Mass, target.Id, Asteroid.SpeedKms, from), Hyp = auto, AutoTarget = target.Id,
            AutoSpeed = Asteroid.SpeedKms,
        };
    }

    void StartForecast(ForecastRequest req)
    {
        // Everything read from the live universe is read here, on this thread; the worker holds only copies.
        var snap = Universe.Snapshot("forecast");
        var opt = Spayly.Sim.ForecastOptions.From(Universe);
        var world = req.AutoTarget != null ? Spayly.Sim.Aim.SnapshotWorld(Universe) : null;
        var cts = new CancellationTokenSource();
        var token = cts.Token;
        var hyp = req.Hyp;
        var from = req.Key.From;
        forecastJob = new ForecastJob
        {
            Req = req, Cts = cts, StartSim = Universe.Time, Version = BodiesVersion, Started = Stopwatch.GetTimestamp(),
            Work = Task.Run(() => ComputeForecast(snap, opt, world, hyp, req, from, token), token),
        };
        forecastLastStart = realTime;
        forecastStarted++;
    }

    /// <summary>The worker's whole job: aim (for a click-to-fire rock), then step the copy forward.</summary>
    static ForecastResult? ComputeForecast(UniverseSnapshot snap, ForecastOptions opt, IWorld? world, Body hyp, ForecastRequest req, Vec3 from, CancellationToken token)
    {
        if (world != null)
        {
            var res = Spayly.Sim.Aim.AutoAim(world, from, req.AutoTarget!, req.AutoSpeed, hyp.Radius);
            if (res == null) return null;
            hyp.Vel = res.Vel;
        }
        return Spayly.Sim.Forecast.Run(snap, hyp, opt, token);
    }

    void PollForecast()
    {
        if (forecastJob is not { } j || !j.Work.IsCompleted) return;
        var t0 = Stopwatch.GetTimestamp();
        PollForecastDone(j);
        forecastAcceptMaxMs = Math.Max(forecastAcceptMaxMs, Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
    }

    void PollForecastDone(ForecastJob j)
    {
        forecastJob = null;
        if (j.Work.IsCanceled) return;
        if (j.Work.IsFaulted)
        {
            GD.PushError($"[forecast] worker failed: {j.Work.Exception?.GetBaseException().Message}");
            return;
        }
        var ms = Stopwatch.GetElapsedTime(j.Started).TotalMilliseconds;
        if (j.Work.Result is not { } result || forecastWanted == null || forecastWanted.Key != j.Req.Key)
        {
            forecastDropped++;
            return;
        }
        Accept(j.Req, result, j.StartSim, j.Version, ms);
    }

    void Accept(ForecastRequest req, ForecastResult result, double startSim, int version, double ms)
    {
        forecastDone++;
        var marked = ForecastItems(result, req.Hole).Take(ForecastOverlay.MaxMarkers).Select(i => i.First).ToList();
        forecastShown = new ForecastShown
        {
            Result = result, Key = req.Key, Hole = req.Hole, StartSim = startSim, Version = version, Ms = ms, Marked = marked,
            Text = ForecastText(result, req.Hole),
        };
        GD.Print(string.Create(CultureInfo.InvariantCulture,
            $"[forecast] frame={Engine.GetProcessFrames()} {(req.Hole ? "hole" : "rock")} done in {ms:F0} ms: {result.Events.Count} events end={result.End} duration={FormatDuration(result.Duration)} substeps={result.Substeps} omitted_moons={result.Omitted} paths={result.Paths.Count}"));
    }

    // ---------- the words ----------

    static string ForecastWhen(double s) => s < 60 ? "at once" : "in " + FormatDuration(s);

    static string ForecastEventText(ForecastEvent e, bool hole)
    {
        var when = ForecastWhen(e.Time);
        var byIt = e.CauseId == ForecastBodyId;
        return e.Kind switch
        {
            ForecastKind.Contact when byIt => hole ? $"{e.SubjectName} — swallowed {when}" : $"{e.SubjectName} — impact {when}",
            ForecastKind.Contact => $"{e.SubjectName} — hits {e.CauseName} {when}",
            ForecastKind.RocheEntry => $"{e.SubjectName} — inside {(byIt ? "" : e.CauseName + "'s ")}Roche limit {when} (will be torn apart)",
            ForecastKind.Capture => $"{e.SubjectName} — captured by the hole {when}",
            ForecastKind.Ejection => $"{e.SubjectName} — unbound from the Sun {when}",
            _ => $"{e.SubjectName} — closest approach {Format.Distance(e.DistanceKm)} {when}",
        };
    }

    /// <summary>One line of the list: the event that stands for it (its marker goes there), its words and its rank.</summary>
    readonly record struct ForecastItem(ForecastEvent First, string Text, int Rank);

    /// <summary>
    /// The list's lines, most telling first: touches and tidal tears, then captures, then ejections, then closest approaches,
    /// each by time. Several captures (or ejections) fold into one line naming the first, because a heavy hole upsets half the
    /// system at once and a list of fourteen planets says less than "14 bodies flung out".
    /// </summary>
    static List<ForecastItem> ForecastItems(ForecastResult r, bool hole)
    {
        var items = new List<ForecastItem>();
        void Group(ForecastKind kind, int rank, string many)
        {
            var of = r.Events.Where(e => e.Kind == kind).ToList();
            if (of.Count == 1) items.Add(new ForecastItem(of[0], ForecastEventText(of[0], hole), rank));
            else if (of.Count > 1)
                items.Add(new ForecastItem(of[0], $"{of.Count} bodies {many} (first {of[0].SubjectName} {ForecastWhen(of[0].Time)})", rank));
        }
        foreach (var e in r.Events)
            if (e.Kind is ForecastKind.Contact or ForecastKind.RocheEntry) items.Add(new ForecastItem(e, ForecastEventText(e, hole), 0));
        Group(ForecastKind.Capture, 1, "captured by the hole");
        Group(ForecastKind.Ejection, 2, "unbound from the Sun");
        foreach (var e in r.Events)
            if (e.Kind == ForecastKind.ClosestApproach) items.Add(new ForecastItem(e, ForecastEventText(e, hole), 3));
        return items.OrderBy(i => i.Rank).ThenBy(i => i.First.Time).ToList();
    }

    /// <summary>"⚠ Earth — closest approach 0.02 AU in 3.1 years · Moon — inside Roche limit in 3.1 years (will be torn apart)".</summary>
    static string ForecastText(ForecastResult r, bool hole)
    {
        var thing = hole ? "hole" : "rock";
        var tail = r.Truncated ? $" · forecast truncated at {FormatDuration(r.Duration)} of {FormatDuration(r.Horizon)} (compute budget)" : "";
        var items = ForecastItems(r, hole);
        if (items.Count == 0) return $"The {thing}: nothing notable within {FormatDuration(r.Duration)}{tail}";
        var shown = items.Take(ForecastTextEvents).OrderBy(i => i.First.Time).Select(i => i.Text);
        var more = items.Count > ForecastTextEvents ? $" · +{items.Count - ForecastTextEvents} more" : "";
        return "⚠ " + string.Join(" · ", shown) + more + tail;
    }

    // ---------- capture verbs ----------

    bool ForecastAction(string verb, string arg)
    {
        var c = CultureInfo.InvariantCulture;
        switch (verb)
        {
            case "hover":
            {
                var p = arg.Split(',');
                var at = new Vector2(float.Parse(p[0], c), float.Parse(p[1], c));
                Input.WarpMouse(at);
                Input.ParseInputEvent(new InputEventMouseMotion { Position = at, GlobalPosition = at });
                GD.Print($"[capture] hover {at.X:F0},{at.Y:F0}");
                return true;
            }
            case "forecastAt":
            {
                var p = arg.Split(',');
                var at = new Vector2(float.Parse(p[0], c), float.Parse(p[1], c));
                holePointer = at;
                forecastSuppressed = false;
                var req = DesiredForecast(at);
                if (req == null)
                {
                    GD.Print($"[forecast] at {at.X:F0},{at.Y:F0}: nothing to forecast in {Mode} mode here");
                    return true;
                }
                var snap = Universe.Snapshot("forecast");
                var t0 = Stopwatch.GetTimestamp();
                var result = ComputeForecast(snap, Spayly.Sim.ForecastOptions.From(Universe),
                    req.AutoTarget != null ? Spayly.Sim.Aim.SnapshotWorld(Universe) : null, req.Hyp, req, req.Key.From, CancellationToken.None);
                var ms = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
                if (result == null)
                {
                    GD.Print("[forecast] the auto-aim found no solution");
                    return true;
                }
                CancelForecastJob();
                forecastWanted = req;
                Accept(req, result, Universe.Time, BodiesVersion, ms);
                LogForecast($"at {at.X:F0},{at.Y:F0}");
                return true;
            }
            case "logForecast":
                LogForecast(arg);
                return true;
            default:
                return false;
        }
    }

    void LogForecast(string tag)
    {
        var c = CultureInfo.InvariantCulture;
        var s = forecastShown;
        var avg = forecastMainCalls > 0 ? forecastMainSumMs / forecastMainCalls : 0;
        var stats = string.Create(c, $"jobs started={forecastStarted} done={forecastDone} dropped={forecastDropped} cancelled={forecastCancelled} in_flight={(forecastJob != null ? 1 : 0)} main_thread_ms avg={avg:F3} max={forecastMainMaxMs:F3} (snapshot+start max={forecastStartMaxMs:F3} accept max={forecastAcceptMaxMs:F3} draw max={forecastDrawMaxMs:F3}) calls={forecastMainCalls} gc={GC.CollectionCount(0)}/{GC.CollectionCount(1)}/{GC.CollectionCount(2)}");
        if (s == null)
        {
            GD.Print($"[forecast] {tag}: none shown | {stats}");
            return;
        }
        var r = s.Result;
        GD.Print(string.Create(c, $"[forecast] {tag}: start_sim_t={s.StartSim:F1} s mode={(s.Hole ? "hole" : "rock")} end={r.End} duration={r.Duration:F1} s horizon={r.Horizon:F0} s substeps={r.Substeps} omitted_moons={r.Omitted} compute_ms={s.Ms:F0} paths={r.Paths.Count} points={r.Paths.Sum(p => p.Points.Count)} | {stats}"));
        GD.Print("[forecast] text: " + s.Text);
        foreach (var e in r.Events)
            GD.Print(string.Create(c, $"[forecast] event {e.Kind} t={e.Time:F1} s (sim_t={s.StartSim + e.Time:F1}) subject={e.SubjectId} cause={e.CauseId ?? "-"} distance_km={e.DistanceKm:G6}"));
    }
}
