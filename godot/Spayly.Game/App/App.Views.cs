using System.Collections.Generic;
using Godot;
using Spayly.Game.Render;
using Spayly.Sim;

namespace Spayly.Game.App;

/// <summary>The view map kept in step with <c>Universe.Bodies</c> (app.ts createView/removeView/syncViews/rebindAfterRestore).</summary>
public partial class App
{
    readonly List<string> staleIds = [];
    /// <summary>
    /// Views of the pair an SPH run took out of the universe, hidden and kept by id until the run ends. Undo and the analytic
    /// fallback put the same bodies back, and the target's heir keeps its id and look; building Earth's PlanetView afresh
    /// (materials, maps) cost a 68 ms frame on undo during a run (M7 capture m7-3-sph-undo), rebinding it costs nothing.
    /// </summary>
    readonly Dictionary<string, BodyView> parkedViews = [];
    readonly HashSet<string> parkIds = [];

    /// <summary>The next removal of these bodies' views hides them instead of freeing them (a hero collision's pair).</summary>
    void ParkViews(Body a, Body b)
    {
        parkIds.Add(a.Id);
        parkIds.Add(b.Id);
    }

    /// <summary>Sun, planet/moon or rock view by kind, its label, and for non-stars an orbit line and a trail.</summary>
    void CreateView(Body b)
    {
        BodyView? v = null;
        if (parkedViews.Remove(b.Id, out var parked))
        {
            if (parked.CanRebind(b))
            {
                parked.Rebind(b);
                parked.Visible = true;
                v = parked;
            }
            else FreeView(parked);
        }
        if (v == null)
        {
            v = b.Kind switch
            {
                BodyKind.Star => new SunView(b),
                BodyKind.Planet or BodyKind.Moon => new PlanetView(b),
                BodyKind.BlackHole => NewBlackHoleView(b),
                _ => new RockView(b),
            };
            AddChild(v);
        }
        views[b.Id] = v;
        labels.Add(b);
        if (b.Kind != BodyKind.Star)
        {
            var line = new PolyLine(OrbitPoints + 1, new Color(b.Visual.Color), 0.45f);
            AddChild(line.Node);
            orbitLines[b.Id] = line;
            trails[b.Id] = new Trail();
        }
        OnViewCreated(b, v);
    }

    void RemoveView(string id)
    {
        if (views.Remove(id, out var v))
        {
            // A parked view keeps its maps and their streaming override: clearing the override on Earth's 16K map cost
            // 18 ms (capture m7-perf-sph-prof), and the view comes back when the run ends.
            if (parkIds.Contains(id) && parkedViews.TryAdd(id, v)) v.Visible = false;
            else
            {
                OnViewRemoved(id, v);
                FreeView(v);
            }
        }
        labels.Remove(id);
        if (orbitLines.Remove(id, out var line)) line.Dispose();
        trails.Remove(id);
        periods.Remove(id);
        lineState.Remove(id);
        Render.Orientation.Forget(id);
    }

    /// <summary>Create views for new bodies and drop views of vanished ones; bumps <see cref="BodiesVersion"/> on any change.</summary>
    void SyncViews()
    {
        var changed = false;
        foreach (var b in Universe.Bodies)
        {
            if (!views.ContainsKey(b.Id))
            {
                CreateView(b);
                changed = true;
            }
        }
        staleIds.Clear();
        foreach (var id in views.Keys)
            if (Universe.ById(id) == null) staleIds.Add(id);
        foreach (var id in staleIds)
        {
            RemoveView(id);
            changed = true;
        }
        // The run is over and its pair did not come back (or came back and took its views): the rest are freed.
        if (sph == null && (parkIds.Count > 0 || parkedViews.Count > 0))
        {
            parkIds.Clear();
            foreach (var (id, pv) in parkedViews)
            {
                OnViewRemoved(id, pv);
                FreeView(pv);
            }
            parkedViews.Clear();
        }
        if (changed) BodiesVersion++;
    }

    /// <summary>The body's view is hidden and kept for its return (a hero collision's pair), not freed.</summary>
    bool ViewParked(string? id) => id != null && parkedViews.ContainsKey(id);

    static void FreeView(BodyView v)
    {
        v.DisposeView();
        v.QueueFree();
    }

    /// <summary>
    /// After an undo or a fault rollback every Body object is a fresh clone. A view whose body is still there is pointed at
    /// the clone; rebuilding all of them cost 11 ms a restore and, through the garbage it left, frames of 30–60 ms (M7.3
    /// capture m7-3-sixty). Only views whose body changed identity, and black holes, are rebuilt. Then focus and selection
    /// are re-bound by id.
    /// </summary>
    void RebindAfterRestore()
    {
        staleIds.Clear();
        foreach (var (id, v) in views)
        {
            if (Universe.ById(id) is not Body b || !v.CanRebind(b))
            {
                staleIds.Add(id);
                continue;
            }
            v.Rebind(b);
            labels.Rebind(b);
            // Where the body went after this point means nothing now; nor does an orbit fitted to its old state.
            if (trails.TryGetValue(id, out var trail)) trail.Reset();
            periods.Remove(id);
            lineState.Remove(id);
            Render.Orientation.Forget(id);
        }
        foreach (var id in staleIds) RemoveView(id);
        SyncViews();
        Rig.Focus = Rig.Focus != null ? Universe.ById(Rig.Focus.Id) : null;
        Selected = Selected != null ? Universe.ById(Selected.Id) : null;
        OnSelectionChanged();
        BodiesVersion++;
    }

    /// <summary>A view was made; later work attaches per-body extras (atmosphere shells, rings, effects anchors).</summary>
    partial void OnViewCreated(Body b, BodyView v);
    /// <summary>A view is about to be freed; later work drops its per-body caches.</summary>
    partial void OnViewRemoved(string id, BodyView v);
}
