using System;
using System.Collections.Generic;
using Godot;
using Spayly.Game.Render;
using Spayly.Sim;

namespace Spayly.Game.App;

/// <summary>
/// Orbit conics and trails (app.ts updateLines). Each non-star body draws the osculating conic about its dynamic primary,
/// camera-relative. What a body orbits changes over many frames, so the primary is re-derived every tenth frame, staggered
/// by index so the O(n) scans spread out.
///
/// Warp visibility, as the web does it: only the trail hides when one frame of sim time spans more than 1/16 of the
/// period (its one-sample-a-frame history would zig-zag across the orbit), and it is reset so it regrows cleanly. The conic
/// is recomputed from the current state every frame, so it stays correct at any warp and is never hidden for it.
/// </summary>
public partial class App
{
    const int OrbitPoints = 180;

    sealed class LineState
    {
        public string? PrimaryId;
        public int PrimaryAt = int.MinValue / 2;
        public readonly List<Vec3> Points = new(OrbitPoints + 1);
        public int ConicAt = int.MinValue / 2;
    }

    readonly Dictionary<string, PolyLine> orbitLines = [];
    readonly Dictionary<string, Trail> trails = [];
    readonly Dictionary<string, double> periods = [];
    readonly Dictionary<string, LineState> lineState = [];
    int lineFrame;

    /// <summary>Trails whose line is in the scene; the view map drops trails without disposing them, so this sweeps.</summary>
    readonly List<Trail> drawnTrails = [];
    int trailSweepVersion = -1;
    bool backdropAttached;

    void UpdateLines()
    {
        AttachBackdrop();
        SweepTrails();
        var focusView = Rig.Focus != null ? views.GetValueOrDefault(Rig.Focus.Id) : null;
        var focusPix = focusView?.PixelRadius ?? 0;
        var closeFade = Math.Clamp(1.3 - focusPix / 220, 0.15, 1);
        var lf = ++lineFrame;
        var bodies = Universe.Bodies;
        var t = Universe.Time;
        var aliasDt = Math.Abs(Sim.LastDtSim);
        for (var i = 0; i < bodies.Count; i++)
        {
            var b = bodies[i];
            if (b.Kind == BodyKind.Star) continue;
            if (!orbitLines.TryGetValue(b.Id, out var line) || !trails.TryGetValue(b.Id, out var trail)) continue;
            if (!lineState.TryGetValue(b.Id, out var st)) lineState[b.Id] = st = new LineState();
            var p = st.PrimaryId != null ? Universe.ById(st.PrimaryId) : null;
            if (p == null || (lf + i) % 10 == 0 || lf - st.PrimaryAt > 10)
            {
                p = PrimaryOf(b);
                st.PrimaryId = p?.Id;
                st.PrimaryAt = lf;
            }
            var period = periods.GetValueOrDefault(b.Id, 30 * Constants.Day);
            if (p != null)
            {
                var mu = Constants.G * (p.Mass + b.Mass);
                var r = b.Pos - p.Pos;
                var v = b.Vel - p.Vel;
                var el = Kepler.StateToElements(mu, r, v);
                period = double.IsFinite(el.Period) ? el.Period : 2 * Math.PI * Vec3.Length(r) / Math.Max(Vec3.Length(v), 1e-6);
                periods[b.Id] = period;
                var small = b.Kind == BodyKind.Asteroid || b.Kind == BodyKind.Fragment;
                var bound = el.E < 1;
                if (Settings.Orbits && (bound || b.Kind == BodyKind.Asteroid))
                {
                    var visualKm = views.TryGetValue(b.Id, out var view) ? view.VisualRadiusKm : b.Radius;
                    var gap = Math.Max(0.02, 3 * visualKm / Math.Max(Vec3.Length(r), 1));
                    var rMax = bound ? 120 * Constants.AU : 20 * Vec3.Length(r);
                    // Fragments are faint and many: their conics refresh every fourth frame.
                    if (b.Kind != BodyKind.Fragment || lf - st.ConicAt >= 4)
                    {
                        Kepler.ConicPoints(mu, r, v, OrbitPoints, rMax, el.Nu, gap, st.Points);
                        st.ConicAt = lf;
                    }
                    // Points are relative to the primary; the line shifts by the primary's offset from the camera.
                    line.Set(st.Points, p.Pos, ctx.Origin, (float)((b.Kind == BodyKind.Fragment ? 0.35 : small ? 0.7 : 1) * closeFade));
                }
                else line.Clear();
            }
            else line.Clear();
            // A moon trails in its planet's frame, where its path is the orbit, not a corkscrew along the planet's path.
            var frame = b.Kind == BodyKind.Moon && p != null && p.Id == b.PrimaryId ? p : null;
            trail.UseFrame(frame?.Id ?? "");
            // One sample a frame at most: once a frame spans a good fraction of the orbit the samples land at random
            // points on it and the line between them zig-zags, so drop the trail until warp eases.
            var aliased = aliasDt > period / 16;
            if (aliased) trail.Reset();
            trail.Visible = Settings.Trails && !aliased;
            UpdateTrail(b, trail, frame, closeFade, t, period);
        }
    }

    /// <summary>Push this frame's position into the trail (in its frame, at most every period/400) and redraw it.</summary>
    void UpdateTrail(Body b, Trail trail, Body? frame, double brightness, double simTime, double period)
    {
        if (!trail.Visible)
        {
            trail.Hide();
            return;
        }
        if (trail.EnsureLine(b.Id, new Color(b.Visual.Color)))
        {
            AddChild(trail.Line!.Node);
            drawnTrails.Add(trail);
        }
        trail.Push(frame != null ? b.Pos - frame.Pos : b.Pos, simTime, Math.Clamp(period / 400, 1, 30 * Constants.Day));
        trail.Update(ctx.Origin, b.Pos, (float)brightness, frame?.Pos ?? Vec3.Zero);
    }

    /// <summary>Free the lines of trails the view map has dropped (a body removed, or every view rebuilt after an undo).</summary>
    void SweepTrails()
    {
        if (trailSweepVersion == BodiesVersion && drawnTrails.Count <= trails.Count) return;
        trailSweepVersion = BodiesVersion;
        for (var i = drawnTrails.Count - 1; i >= 0; i--)
        {
            var tr = drawnTrails[i];
            if (tr.Owner != null && trails.TryGetValue(tr.Owner, out var live) && ReferenceEquals(live, tr)) continue;
            tr.Dispose();
            drawnTrails.RemoveAt(i);
        }
    }

    /// <summary>
    /// The belt cloud and the sky are built with the scene but are not nodes of it until here (App._Ready predates them);
    /// both are camera-anchored, so parenting them to the app root at the render origin is all they need.
    /// </summary>
    void AttachBackdrop()
    {
        if (backdropAttached) return;
        backdropAttached = true;
        AddChild(sky.Node);
        if (belt.Node != null) AddChild(belt.Node);
    }
}
