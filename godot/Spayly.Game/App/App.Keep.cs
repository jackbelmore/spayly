using System;
using System.Globalization;
using Godot;
using Spayly.Game.Render;
using Spayly.Sim;

namespace Spayly.Game.App;

/// <summary>
/// Keep inside: the player centres a sphere on the orbital plane, resizes it (drag the handle on its rim, or the wheel) and
/// applies it. Everything outside is deleted so the CPU only simulates what they want to watch; the plan (Spayly.Sim
/// KeepInside) says which bodies that is, and keeps the Sun and whatever holds an inside body (its planet) as gravity sources.
///
/// The centre is a double km <see cref="Vec3"/> on the plane z = 0 and the radius a double km, so the tool works from the
/// Moon's surface to Neptune's orbit; the overlay turns them into floats camera-relative through the floating origin each
/// frame. Apply is one undo point (taken before anything goes) behind a confirm prompt.
/// </summary>
public partial class App
{
    const double KeepMinRadiusKm = 1e3, KeepMaxRadiusKm = 1e12;
    /// <summary>Px: how close to the handle a press counts as grabbing it.</summary>
    const float KeepHandleGrabPx = 20;

    enum KeepGrab { None, Move, Resize }

    KeepSphereOverlay? keepOverlay;
    Vec3 keepCentre;
    double keepRadius = 1e8;
    /// <summary>False until the first click: the sphere only follows the pointer, dimmed.</summary>
    bool keepPlaced, keepHover;
    KeepGrab keepGrab;
    Vec3 keepGrabOffset;
    bool keepConfirming;
    readonly KeepPlan keepPlan = new();
    int keepPlanVersion = -1;
    Vec3 keepPlanCentre;
    double keepPlanRadius;
    /// <summary>The physics capacity (warp) before an apply, and when to measure it again; 0 when no gain is pending.</summary>
    double keepCapacityBefore, keepGainAt;
    int keepBodiesBefore;

    void EnterKeep()
    {
        keepPlaced = false;
        keepHover = false;
        keepGrab = KeepGrab.None;
        keepConfirming = false;
        keepPlanVersion = -1;
        // A sphere a third of the way to what the camera looks at is on screen whatever the zoom.
        keepRadius = Math.Clamp(Rig.Distance * 0.35, 1e4, KeepMaxRadiusKm);
        keepCentre = new Vec3(Rig.Target.X, Rig.Target.Y, 0);
    }

    void LeaveKeep()
    {
        if (!keepPlaced && !keepHover && !keepConfirming && keepOverlay == null) return;
        keepPlaced = keepHover = keepConfirming = false;
        keepGrab = KeepGrab.None;
        keepOverlay?.HideAll();
        hud?.HideKeepConfirm();
    }

    /// <summary>Where a pointer ray meets the orbital plane (z = 0), km; null edge-on, behind the camera, or absurdly far.</summary>
    Vec3? KeepPlanePoint(Vector2 at)
    {
        var dir = Rig.RayDir(at);
        var pos = Rig.Position;
        if (Math.Abs(dir.Z) < 1e-6) return null;
        var t = -pos.Z / dir.Z;
        if (!(t > 0) || t > 200 * Math.Max(Rig.Distance, 1e3)) return null;
        var p = pos + dir * t;
        return new Vec3(p.X, p.Y, 0);
    }

    /// <summary>The resize handle: on the equator, on the side facing the camera so it is always reachable.</summary>
    Vec3 KeepHandlePoint()
    {
        var toCam = new Vec3(Rig.Position.X - keepCentre.X, Rig.Position.Y - keepCentre.Y, 0);
        var len = Vec3.Length(toCam);
        var dir = len > 1e-9 ? toCam / len : new Vec3(1, 0, 0);
        return keepCentre + dir * keepRadius;
    }

    bool KeepHandleHit(Vector2 at)
    {
        var rel = ctx.Origin.Place(KeepHandlePoint(), out _, out _);
        return labels.Project(rel, out var s) && s.DistanceTo(at) <= KeepHandleGrabPx;
    }

    void KeepPointerDown(Vector2 at)
    {
        if (keepConfirming) return;
        var p = KeepPlanePoint(at);
        if (!keepPlaced)
        {
            if (p is not Vec3 q) return;
            keepCentre = q;
            keepPlaced = true;
            Hint("Sphere placed. Drag it to move, drag the handle on its rim or use the wheel to resize, Enter to apply, Esc to cancel.", 4.5);
            return;
        }
        if (KeepHandleHit(at))
        {
            keepGrab = KeepGrab.Resize;
            return;
        }
        if (p is not Vec3 hit) return;
        // Inside the sphere a drag carries it; a click outside re-centres it there (and carries it from then on).
        keepGrab = KeepGrab.Move;
        keepGrabOffset = Vec3.Distance(hit, keepCentre) <= keepRadius ? keepCentre - hit : Vec3.Zero;
        if (keepGrabOffset == Vec3.Zero) keepCentre = hit;
    }

    void KeepPointerMove(Vector2 at, bool leftDown)
    {
        if (keepConfirming) return;
        var p = KeepPlanePoint(at);
        if (!keepPlaced)
        {
            keepHover = p != null;
            if (p is Vec3 q) keepCentre = q;
            return;
        }
        if (!leftDown || p is not Vec3 hit) return;
        if (keepGrab == KeepGrab.Move) keepCentre = hit + keepGrabOffset;
        else if (keepGrab == KeepGrab.Resize) keepRadius = Math.Clamp(Vec3.Distance(hit, keepCentre), KeepMinRadiusKm, KeepMaxRadiusKm);
    }

    void KeepPointerUp() => keepGrab = KeepGrab.None;

    void KeepWheel(double deltaY) =>
        keepRadius = Math.Clamp(keepRadius * Math.Exp(Math.Clamp(-deltaY, -200, 200) * 0.0015), KeepMinRadiusKm, KeepMaxRadiusKm);

    /// <summary>The plan for the sphere as it is now; recomputed only when the sphere or the body list changed.</summary>
    KeepPlan KeepCurrentPlan()
    {
        if (keepPlanVersion != BodiesVersion || keepPlanCentre != keepCentre || keepPlanRadius != keepRadius)
        {
            KeepInside.Plan(Universe, keepCentre, keepRadius, keepPlan);
            keepPlanVersion = BodiesVersion;
            keepPlanCentre = keepCentre;
            keepPlanRadius = keepRadius;
        }
        return keepPlan;
    }

    void UpdateKeep(double dtReal)
    {
        if (keepGainAt > 0 && realTime >= keepGainAt) ReportKeepGain();
        if (Mode != Mode.KeepInside)
        {
            keepOverlay?.HideAll();
            return;
        }
        if (keepOverlay == null)
        {
            keepOverlay = new KeepSphereOverlay();
            AddChild(keepOverlay);
        }
        if (!keepPlaced && !keepHover)
        {
            keepOverlay.HideAll();
            hud.SetKeepReadout("Click the orbital plane to centre the sphere. The wheel resizes it, shift+wheel zooms, right-drag orbits.");
            return;
        }
        var plan = KeepCurrentPlan();
        keepOverlay.Show(keepCentre, keepRadius, keepPlaced ? KeepHandlePoint() : null, keepPlaced ? 1f : 0.5f, ctx);
        var src = plan.GravitySources.Count == 0 ? "" : $" + {plan.GravitySources.Count} kept as gravity {(plan.GravitySources.Count == 1 ? "source" : "sources")} ({NameList(plan.GravitySources)})";
        hud.SetKeepReadout(string.Create(CultureInfo.InvariantCulture,
            $"Radius {UI.Format.Distance(keepRadius)} · {plan.Inside.Count} inside{src} · {plan.Remove.Count} would be removed"));
    }

    static string NameList(System.Collections.Generic.List<Body> bodies)
    {
        var names = new string[Math.Min(bodies.Count, 3)];
        for (var i = 0; i < names.Length; i++) names[i] = bodies[i].Name;
        return string.Join(", ", names) + (bodies.Count > names.Length ? ", …" : "");
    }

    /// <summary>Enter or the Apply button: ask before anything is deleted.</summary>
    public void RequestKeepApply()
    {
        if (Mode != Mode.KeepInside || keepConfirming) return;
        if (!keepPlaced)
        {
            Hint("Click the orbital plane to place the sphere first", 2.5);
            return;
        }
        var plan = KeepCurrentPlan();
        if (plan.Remove.Count == 0)
        {
            Hint("Nothing lies outside the sphere", 2.5);
            return;
        }
        keepConfirming = true;
        keepGrab = KeepGrab.None;
        var needs = plan.GravitySources.Count == 0 ? "" : $" {NameList(plan.GravitySources)} stay{(plan.GravitySources.Count == 1 ? "s" : "")} as gravity {(plan.GravitySources.Count == 1 ? "source" : "sources")} for the bodies inside.";
        hud.ShowKeepConfirm($"Remove {plan.Remove.Count} bodies outside the sphere?\n{plan.Kept} stay.{needs} Ctrl+Z brings every one back.", ConfirmKeep, CancelKeepConfirm);
    }

    void CancelKeepConfirm()
    {
        if (!keepConfirming) return;
        keepConfirming = false;
        hud.HideKeepConfirm();
    }

    void ConfirmKeep()
    {
        if (!keepConfirming) return;
        keepConfirming = false;
        hud.HideKeepConfirm();
        ApplyKeepInside();
    }

    /// <summary>What an undo, a reset or a mode change must do about a prompt that is open.</summary>
    void KeepAbandon() => CancelKeepConfirm();

    /// <summary>
    /// Delete what the plan removes. One undo point first (the Erase kind: bodies, belt particles and the app's half go back
    /// together), then the same clean-up as erase and Reset: focus, selection, last impact, per-body books, views (labels,
    /// trails, orbit lines), pending tidal and SPH jobs, the approach slow-down and a running skip.
    /// </summary>
    void ApplyKeepInside()
    {
        var plan = KeepCurrentPlan();
        if (plan.Remove.Count == 0) return;
        var before = Universe.Bodies.Count;
        keepCapacityBefore = PhysicsCapacity();
        keepBodiesBefore = before;
        var stepBefore = stepMsEma;
        CancelSkip();
        CancelAim();
        aiming = false;
        CancelHoleDrag();
        CancelSphForUndo();
        CancelTidal();
        RecordUndo(UndoKind.Erase, $"keeping {plan.Kept} bodies inside a sphere");
        var star = Star();
        var focusGone = Rig.Focus != null && plan.Remove.Contains(Rig.Focus);
        foreach (var b in plan.Remove)
        {
            if (ReferenceEquals(Selected, b)) Select((Body?)null);
            if (LastImpact != null && (ReferenceEquals(LastImpact.Impactor, b) || ReferenceEquals(LastImpact.Target, b))) LastImpact = null;
            Universe.Remove(b);
            periods.Remove(b.Id);
            holeOrbits.Remove(b.Id);
            blackHoleNear.Remove(b.Id);
        }
        // The camera must not be left pointing at nothing: it falls back to the Sun, which is always kept.
        if (focusGone) Rig.Focus = star;
        var culled = Universe.Particles is { } belt ? KeepInside.CullParticles(belt, keepCentre, keepRadius) : 0;
        SyncViews();
        BodiesVersion++;
        ForgetApproach("keep inside");
        EndSlowMo();
        keepGainAt = realTime + 1.5;
        var after = Universe.Bodies.Count;
        GD.Print(string.Create(CultureInfo.InvariantCulture,
            $"[keep] applied centre=({keepCentre.X:G6},{keepCentre.Y:G6},{keepCentre.Z:G6}) radius={keepRadius:G6} km bodies {before}->{after} removed={plan.Remove.Count} inside={plan.Inside.Count} gravity_sources=[{string.Join(",", plan.GravitySources.ConvertAll(b => b.Id))}] belt_particles_culled={culled} step_ms_before={stepBefore:F3} version={BodiesVersion}"));
        SetMode(Mode.Explore);
        Hint($"{after} bodies remain ({plan.Remove.Count} removed). Ctrl+Z brings them back.", 4);
    }

    /// <summary>
    /// What the physics could reach at this moment, sim s per real s: what it advanced, scaled by how much of its 6 ms budget
    /// it used. An estimate (belt particles are in the time and outside the budget), good for a before-and-after ratio.
    /// </summary>
    double PhysicsCapacity() => stepMsEma > 0.01 && achievedRate > 0 ? achievedRate * Spayly.Game.Sim.SimRunner.StepBudgetMs / stepMsEma : 0;

    void ReportKeepGain()
    {
        if (Paused || realTime < keepGainAt) return;
        keepGainAt = 0;
        var now = PhysicsCapacity();
        if (!(keepCapacityBefore > 0) || !(now > 0)) return;
        var gain = now / keepCapacityBefore;
        GD.Print(string.Create(CultureInfo.InvariantCulture,
            $"[keep] gain bodies {keepBodiesBefore}->{Universe.Bodies.Count} capacity {keepCapacityBefore:G4}->{now:G4} sim-s/real-s ({gain:F2}x) step_ms={stepMsEma:F3}"));
        Hint(string.Create(CultureInfo.InvariantCulture,
            $"{Universe.Bodies.Count} bodies now: the physics can reach about {UI.Format.Warp(now)} (was {UI.Format.Warp(keepCapacityBefore)}, {gain:F1}×)"), 5);
    }

    // ---------- capture verbs ----------

    /// <summary><c>keepInside:cx,cy,radius[,confirm|preview]</c> (world km, or a body id for cx,cy; applies at once, <c>confirm</c> opens the prompt, <c>preview</c> only shows the sphere), <c>keepConfirm</c>, <c>keepCancel</c>, <c>logKeep</c>, <c>logBodies[:tag]</c>.</summary>
    bool KeepAction(string verb, string arg)
    {
        switch (verb)
        {
            case "keepInside":
            {
                var n = arg.Split(',');
                double D(int i) => double.Parse(n[i], NumberStyles.Float, CultureInfo.InvariantCulture);
                SetMode(Mode.KeepInside);
                // A body id in place of cx,cy centres the sphere on its position's projection onto the plane: keepInside:earth,2e6.
                var byId = Universe.ById(n[0]);
                var r = byId != null ? 1 : 2;
                keepCentre = byId != null ? new Vec3(byId.Pos.X, byId.Pos.Y, 0) : new Vec3(D(0), D(1), 0);
                keepRadius = Math.Clamp(D(r), KeepMinRadiusKm, KeepMaxRadiusKm);
                keepPlaced = true;
                keepPlanVersion = -1;
                var mode = n.Length > r + 1 ? n[r + 1] : "";
                if (mode == "confirm") RequestKeepApply();
                else if (mode == "preview") { }
                else
                {
                    keepConfirming = true;
                    ConfirmKeep();
                }
                return true;
            }
            case "keepConfirm": ConfirmKeep(); return true;
            case "keepCancel": CancelKeepConfirm(); return true;
            case "logKeep":
                GD.Print(string.Create(CultureInfo.InvariantCulture,
                    $"[keep] state mode={Mode} placed={keepPlaced} confirming={keepConfirming} centre=({keepCentre.X:G6},{keepCentre.Y:G6}) radius={keepRadius:G6} inside={KeepCurrentPlan().Inside.Count} sources={KeepCurrentPlan().GravitySources.Count} remove={KeepCurrentPlan().Remove.Count} prompt: {hud.KeepConfirmSummary}"));
                return true;
            case "logBodies":
            {
                var counts = new int[Enum.GetValues<BodyKind>().Length];
                foreach (var b in Universe.Bodies) counts[(int)b.Kind]++;
                var parts = new string[counts.Length];
                for (var i = 0; i < counts.Length; i++) parts[i] = $"{(BodyKind)i}={counts[i]}";
                var belt = Universe.Particles;
                var alive = 0;
                if (belt != null)
                    for (var i = 0; i < belt.Count; i++) alive += belt.Alive[i];
                GD.Print($"[bodies] {arg} total={Universe.Bodies.Count} {string.Join(" ", parts)} sun={(Universe.ById("sun") != null)} belt_alive={alive} views={views.Count} step_ms={stepMsEma:F3}");
                return true;
            }
        }
        return false;
    }
}
