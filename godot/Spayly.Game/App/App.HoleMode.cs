using System;
using System.Collections.Generic;
using System.Globalization;
using Godot;
using Spayly.Game.Render;
using Spayly.Game.UI;
using Spayly.Sim;

namespace Spayly.Game.App;

/// <summary>
/// Black-hole mode (key B): the HUD panel picks a mass and a starting velocity, a click on the view places a hole on the
/// aim plane (the horizontal plane through the camera target, as a hand aim uses), and every placement is an undo point.
/// Velocity is relative to the body dominating where the hole lands: a circular orbit about it (prograde about the
/// ecliptic pole), at rest relative to it, or dragged out like a hand aim, where a 150 px drag is the local circular speed.
///
/// Capture verbs (App.Capture.cs routes them here): <c>bhMass:M☉</c>, <c>bhVelocity:orbit|rest|drag</c>, <c>placeBH:x,y</c>
/// (a left click at viewport px through the same input path), <c>removeBH:id</c>, <c>cameraAt:bodyId,distanceKm[,pitch]</c>,
/// <c>logHolePanel</c> and <c>logState[:tag]</c>.
/// </summary>
public partial class App
{
    /// <summary>A drag this long, px, launches at the local circular speed.</summary>
    const double HoleDragPx = 150;

    /// <summary>The readout's reference Earth (IAU 2015 nominal), kg and km: the catalogued Earth may have been edited or eaten.</summary>
    const double EarthMassKg = 5.9722e24, EarthRadiusKm = 6371.0;

    public readonly BlackHoleSettings HoleSettings = new();

    sealed class HoleDrag
    {
        public Vec3 Spawn;
        public Vec3 Current;
        public Vector2 Start;
        public Vector2 At;
        public Body? Primary;
        public Vec3? Vel;
        public PredictResult? Path;
        public bool Dirty = true;
    }

    /// <summary>
    /// A placed hole's orbit about its primary, measured frame by frame in the orbit's own plane: the swept angle, unwrapped,
    /// and the sim time at which it first reached 2π, for comparison with Kepler's period at placement.
    /// </summary>
    sealed class HoleOrbit
    {
        public required Body Hole;
        public required Body Primary;
        public Vec3 E1, E2;
        public double T0;
        public double LastAngle;
        public double Swept;
        public double LastTime;
        public double KeplerPeriod;
        public double FirstPeriod = double.NaN;
    }

    HoleDrag? holeDrag;
    Vector2? holePointer;
    readonly Dictionary<string, HoleOrbit> holeOrbits = [];

    /// <summary>Push a position out of every body to 1.5 × contact for a hole of horizon <paramref name="rs"/> km.</summary>
    Vec3 ClearOfBodies(Vec3 pos, double rs)
    {
        foreach (var o in Universe.Bodies)
        {
            var off = pos - o.Pos;
            var min = 1.5 * (o.Radius + rs);
            var d = Vec3.Length(off);
            if (d < min) pos = o.Pos + (d > 0 ? off * (1 / d) : DefaultSpawnDirection(o)) * min;
        }
        return pos;
    }

    /// <summary>
    /// Circular speed of a hole of <paramref name="massKg"/> at <paramref name="d"/> km from <paramref name="primary"/>, km/s: the
    /// Paczyński–Wiita speed the pair's gravity actually has, √(μ d)/(d − r_s) with r_s of every hole in the pair.
    /// </summary>
    static double HoleCircularSpeed(Body primary, double massKg, double d) =>
        PseudoNewtonian.CircularSpeed(Constants.G * (primary.Mass + massKg), d, BlackHole.SchwarzschildRadiusKm(massKg) + PseudoNewtonian.HoleRs(primary));

    /// <summary>Period of that circular orbit, s.</summary>
    static double HoleCircularPeriod(Body primary, double massKg, double d) =>
        PseudoNewtonian.CircularPeriod(Constants.G * (primary.Mass + massKg), d, BlackHole.SchwarzschildRadiusKm(massKg) + PseudoNewtonian.HoleRs(primary));

    /// <summary>Starting velocity (km/s, absolute) for the panel's Orbit or Rest choice at <paramref name="pos"/>.</summary>
    Vec3 HoleStartVelocity(Vec3 pos, Body? primary, double massKg)
    {
        if (primary == null) return Vec3.Zero;
        if (HoleSettings.Velocity != HoleVelocity.Orbit) return primary.Vel;
        var r = pos - primary.Pos;
        var d = Vec3.Length(r);
        if (!(d > 0)) return primary.Vel;
        // Prograde about the ecliptic pole, the planets' sense; straight above or below a body any horizontal direction will do.
        var t = Vec3.Cross(new Vec3(0, 0, 1), r * (1 / d));
        t = Vec3.Length(t) > 1e-9 ? Vec3.Normalize(t) : new Vec3(1, 0, 0);
        return primary.Vel + t * HoleCircularSpeed(primary, massKg, d);
    }

    /// <summary>Place a hole where the pointer ray meets the aim plane, with the panel's mass and velocity choice.</summary>
    void PlaceHoleAt(Vector2 at, Vec3? dragVel = null)
    {
        if (AimPlanePoint(at) is not Vec3 p)
        {
            Hint("Point below the horizon: a black hole is placed on the plane through what the camera looks at", 3);
            return;
        }
        var mass = HoleSettings.MassSolar * Constants.SunMass;
        p = ClearOfBodies(p, BlackHole.SchwarzschildRadiusKm(mass));
        var primary = Universe.DominantBody(p);
        var vel = dragVel ?? (HoleSettings.Velocity == HoleVelocity.Drag ? primary?.Vel ?? Vec3.Zero : HoleStartVelocity(p, primary, mass));
        var b = SpawnBlackHoleAt(HoleSettings.MassSolar, p, vel, primary, undoable: true);
        if (b == null || primary == null) return;
        Select(b);
        TrackHoleOrbit(b, primary);
    }

    void TrackHoleOrbit(Body hole, Body primary)
    {
        var r = hole.Pos - primary.Pos;
        var v = hole.Vel - primary.Vel;
        var h = Vec3.Cross(r, v);
        if (!(Vec3.Length(h) > 0) || !(Vec3.Length(r) > 0)) return;
        var e1 = Vec3.Normalize(r);
        var mu = Constants.G * (primary.Mass + hole.Mass);
        var el = Kepler.StateToElements(mu, r, v);
        holeOrbits[hole.Id] = new HoleOrbit
        {
            Hole = hole, Primary = primary, E1 = e1, E2 = Vec3.Cross(Vec3.Normalize(h), e1), T0 = Universe.Time, LastTime = Universe.Time,
            KeplerPeriod = el.Period,
        };
    }

    /// <summary>Step 13 (after the asteroid aim): the drag preview and the orbit trackers; the readout follows at 8 Hz (App.Hud.cs).</summary>
    void UpdateHoleMode()
    {
        foreach (var o in holeOrbits.Values)
        {
            // Undo or a merge replaced either body: the tracker would measure a clone that never moves.
            if (!ReferenceEquals(Universe.ById(o.Hole.Id), o.Hole) || !ReferenceEquals(Universe.ById(o.Primary.Id), o.Primary)) continue;
            var r = o.Hole.Pos - o.Primary.Pos;
            var ang = Math.Atan2(Vec3.Dot(r, o.E2), Vec3.Dot(r, o.E1));
            var d = ang - o.LastAngle;
            d -= 2 * Math.PI * Math.Round(d / (2 * Math.PI));
            var before = o.Swept;
            o.Swept += d;
            if (double.IsNaN(o.FirstPeriod) && before < 2 * Math.PI && o.Swept >= 2 * Math.PI)
                o.FirstPeriod = o.LastTime - o.T0 + (Universe.Time - o.LastTime) * (2 * Math.PI - before) / (o.Swept - before);
            o.LastAngle = ang;
            o.LastTime = Universe.Time;
        }
        if (Mode == Mode.BlackHole) UpdateHoleDrag();
    }

    void UpdateHoleDrag()
    {
        var a = holeDrag;
        if (a == null) return;
        EnsureAimOverlay();
        var overlay = aimOverlay!;
        overlay.PlaceSpawn(a.Spawn, 10, ctx);
        var dragPx = a.At.DistanceTo(a.Start);
        var dir = a.Current - a.Spawn;
        var prim = a.Primary;
        if (prim == null || Vec3.Length(dir) < 1 || dragPx < 4)
        {
            a.Vel = null;
            overlay.HidePath();
            overlay.HitVisible = false;
            return;
        }
        var mass = HoleSettings.MassSolar * Constants.SunMass;
        var d = Math.Max(Vec3.Length(a.Spawn - prim.Pos), 1);
        a.Vel = HoleDragVelocity(a);
        if (a.Dirty && a.Vel is Vec3 vel)
        {
            // A massless probe: the preview leaves out the hole's own pull on its primary, which the real run includes.
            var horizon = Math.Min(2 * Constants.Year, 1.5 * 2 * Math.PI * Math.Sqrt(d * d * d / (Constants.G * (prim.Mass + mass))));
            a.Path = Spayly.Sim.Aim.PredictPath(Universe, new Probe(a.Spawn, vel, BlackHole.SchwarzschildRadiusKm(mass)), horizon,
                new PredictOptions { MaxSteps = 2500, MaxPoints = 600, PairBudget = 3e6 });
            a.Dirty = false;
        }
        if (a.Path is { } res) overlay.ShowPath(res.Points, ctx.Origin, 1);
    }

    /// <summary>A drag's launch velocity (absolute km/s): the local circular speed per <see cref="HoleDragPx"/> px, along the drag; null for a click.</summary>
    Vec3? HoleDragVelocity(HoleDrag a)
    {
        var dir = a.Current - a.Spawn;
        var dragPx = a.At.DistanceTo(a.Start);
        if (a.Primary is not Body prim || Vec3.Length(dir) < 1 || dragPx < 4) return null;
        var mass = HoleSettings.MassSolar * Constants.SunMass;
        var d = Math.Max(Vec3.Length(a.Spawn - prim.Pos), 1);
        return prim.Vel + Vec3.Normalize(dir) * (HoleCircularSpeed(prim, mass, d) * dragPx / HoleDragPx);
    }

    /// <summary>The panel's readout: r_s and tidal radii of the chosen mass, then what the pointer is over.</summary>
    internal string HoleReadout()
    {
        var m = HoleSettings.MassSolar * Constants.SunMass;
        var rs = BlackHole.SchwarzschildRadiusKm(m);
        var rtStar = BlackHole.TidalRadiusKm(m, Constants.SunMass, Constants.SunRadius);
        var rtEarth = BlackHole.TidalRadiusKm(m, EarthMassKg, EarthRadiusKm);
        string Tidal(double rt) => rt > rs ? Format.Distance(rt) : "inside the horizon (swallowed whole)";
        var line1 = $"r_s {Format.Distance(rs)} · tidal radius: Sun-like star {Tidal(rtStar)}, Earth {Tidal(rtEarth)}";
        if (holeDrag is { Vel: Vec3 v, Primary: Body dp })
            return line1 + $"\nLaunch {Format.Speed(Vec3.Length(v - dp.Vel))} relative to {dp.Name}; release to place";
        if (holePointer is not Vector2 at || AimPlanePoint(at) is not Vec3 p) return line1 + "\nPoint at the view to see the Hill sphere there";
        var prim = Universe.DominantBody(p);
        if (prim == null) return line1;
        var d = Vec3.Length(p - prim.Pos);
        var where = $"{Format.Distance(d)} from {prim.Name}";
        if (m >= prim.Mass)
            return line1 + (m < 3 * prim.Mass
                ? $"\nHere: {where}. Close to its mass: the two would orbit each other"
                : $"\nHere: {where}. At {Format.Num(m / prim.Mass)} × its mass, {prim.Name} would orbit the hole");
        var hill = d * Math.Cbrt(m / (3 * prim.Mass));
        var orbit = HoleSettings.Velocity == HoleVelocity.Orbit
            ? $" · circular orbit {Format.Speed(HoleCircularSpeed(prim, m, d))}, period {Format.Duration(HoleCircularPeriod(prim, m, d))}"
            : "";
        return line1 + $"\nHere: {where}, Hill sphere {Format.Distance(hill)}{orbit}";
    }

    // ---------- pointer (App.Input.cs calls these in black-hole mode) ----------

    void HolePointerDown(Vector2 at)
    {
        if (HoleSettings.Velocity != HoleVelocity.Drag || AimPlanePoint(at) is not Vec3 p) return;
        var mass = HoleSettings.MassSolar * Constants.SunMass;
        p = ClearOfBodies(p, BlackHole.SchwarzschildRadiusKm(mass));
        holeDrag = new HoleDrag { Spawn = p, Current = p, Start = at, At = at, Primary = Universe.DominantBody(p) };
    }

    /// <summary>True when a hole drag took the motion.</summary>
    bool HolePointerMove(Vector2 at)
    {
        holePointer = at;
        if (holeDrag is not { } a) return false;
        if (AimPlanePoint(at) is Vec3 p)
        {
            a.Current = p;
            a.At = at;
            a.Dirty = true;
        }
        return true;
    }

    /// <summary>A left release in black-hole mode: place (a drag launches with its speed, a click uses the panel's choice).</summary>
    void HolePointerUp(Vector2 at, bool moved)
    {
        if (holeDrag is { } a)
        {
            holeDrag = null;
            aimOverlay?.Clear();
            if (a.Primary == null) return;
            var mass = HoleSettings.MassSolar * Constants.SunMass;
            // Computed here too: a drag released within the frame it moved has not been through UpdateHoleDrag yet.
            var vel = moved && HoleDragVelocity(a) is Vec3 v ? v : a.Primary.Vel;
            var b = SpawnBlackHoleAt(HoleSettings.MassSolar, a.Spawn, vel, a.Primary, undoable: true);
            if (b == null) return;
            Select(b);
            TrackHoleOrbit(b, a.Primary);
            return;
        }
        if (!moved) PlaceHoleAt(at);
    }

    void CancelHoleDrag()
    {
        if (holeDrag == null) return;
        holeDrag = null;
        aimOverlay?.Clear();
    }

    /// <summary>Remove a hole from the universe (the inspector's Remove); an undo point first, so Undo brings it back.</summary>
    public void RemoveBlackHole(Body b)
    {
        if (b.Kind != BodyKind.BlackHole || !ReferenceEquals(Universe.ById(b.Id), b)) return;
        RecordUndo(UndoKind.RemoveHole, $"removing {b.Name}", b.Id);
        var fallback = Universe.DominantBody(b.Pos, b);
        if (ReferenceEquals(Rig.Focus, b)) Rig.Focus = fallback;
        if (ReferenceEquals(Selected, b)) Select(fallback);
        Universe.Remove(b);
        holeOrbits.Remove(b.Id);
        SyncViews();
        BodiesVersion++;
        GD.Print($"[bh] removed {b.Id} bodies={Universe.Bodies.Count}");
        Hint($"{b.Name} removed. Undo brings it back.", 3);
    }

    /// <summary>
    /// <c>logState[:tag]</c>: an FNV-1a hash over every body's id, mass, radius, position and velocity bits, in list order, and
    /// the clock; two equal hashes mean an exactly equal state.
    /// </summary>
    void LogState(string tag)
    {
        ulong h = 14695981039346656037;
        void Mix(ulong x)
        {
            for (var i = 0; i < 8; i++)
            {
                h ^= (x >> (8 * i)) & 0xff;
                h *= 1099511628211;
            }
        }
        void D(double x) => Mix((ulong)BitConverter.DoubleToInt64Bits(x));
        foreach (var b in Universe.Bodies)
        {
            foreach (var c in b.Id) Mix(c);
            D(b.Mass); D(b.Radius);
            D(b.Pos.X); D(b.Pos.Y); D(b.Pos.Z);
            D(b.Vel.X); D(b.Vel.Y); D(b.Vel.Z);
        }
        D(Universe.Time);
        GD.Print(string.Create(CultureInfo.InvariantCulture, $"[state] {tag} bodies={Universe.Bodies.Count} time={Universe.Time:R} hash={h:x16}"));
    }

    /// <summary>logBH's orbit line for a placed hole: swept angle, the measured first period and Kepler's at placement.</summary>
    string HoleOrbitText(string id)
    {
        if (!holeOrbits.TryGetValue(id, out var o)) return "";
        var c = CultureInfo.InvariantCulture;
        var t = Universe.Time - o.T0;
        var err = (o.FirstPeriod - o.KeplerPeriod) / o.KeplerPeriod;
        return string.Create(c,
            $" orbit_about={o.Primary.Id} swept={o.Swept:F4} rad over {t:G6} s period_kepler={o.KeplerPeriod:G8} s period_measured={o.FirstPeriod:G8} s rel_err={err:G4}");
    }

    bool HoleAction(string verb, string arg)
    {
        var c = CultureInfo.InvariantCulture;
        switch (verb)
        {
            case "bhMass":
                HoleSettings.MassSolar = Math.Clamp(double.Parse(arg, NumberStyles.Float, c), BlackHoleSettings.MinSolar, BlackHoleSettings.MaxSolar);
                hud.SyncHolePanel();
                return true;
            case "bhVelocity":
                HoleSettings.Velocity = Enum.Parse<HoleVelocity>(arg, true);
                hud.SyncHolePanel();
                return true;
            case "placeBH":
                ClickAt(arg);
                return true;
            // A left drag "x0,y0,x1,y1" through the input path: press, move in 8 steps, release (drag-to-set velocity).
            case "bhDrag":
            {
                var p = Array.ConvertAll(arg.Split(','), x => float.Parse(x, CultureInfo.InvariantCulture));
                var a0 = new Vector2(p[0], p[1]);
                var a1 = new Vector2(p[2], p[3]);
                Input.WarpMouse(a0);
                Input.ParseInputEvent(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true, Position = a0, GlobalPosition = a0, ButtonMask = MouseButtonMask.Left });
                for (var k = 1; k <= 8; k++)
                {
                    var at = a0.Lerp(a1, k / 8f);
                    Input.ParseInputEvent(new InputEventMouseMotion { Position = at, GlobalPosition = at, ButtonMask = MouseButtonMask.Left });
                }
                Input.ParseInputEvent(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false, Position = a1, GlobalPosition = a1 });
                GD.Print(string.Create(CultureInfo.InvariantCulture, $"[capture] bhDrag {a0.X:F0},{a0.Y:F0} -> {a1.X:F0},{a1.Y:F0} ({a0.DistanceTo(a1):F0} px)"));
                return true;
            }
            case "removeBH":
                if (Universe.ById(arg) is Body b) RemoveBlackHole(b);
                return true;
            case "cameraAt":
            {
                // Instant framing so a placeBH pixel maps to a known distance: a point s km along the camera's right from the
                // target lands f·s/D px right of centre, f = PixelsPerRadian, at any pitch.
                var p = arg.Split(',', StringSplitOptions.TrimEntries);
                if (Universe.ById(p[0]) is not Body f) return true;
                Settings.Diorama = false;
                Rig.Set(new ViewSpec
                {
                    Focus = f, SetFocus = true, Distance = double.Parse(p[1], NumberStyles.Float, c),
                    Pitch = p.Length > 2 ? double.Parse(p[2], NumberStyles.Float, c) : 1.2,
                });
                GD.Print(string.Create(c, $"[capture] cameraAt {f.Id} distance={Rig.Distance:G6} km ppr={Rig.PixelsPerRadian(GetViewport().GetVisibleRect().Size.Y):F3}"));
                return true;
            }
            case "logHolePanel":
                hud.SetHoleReadout(HoleReadout());
                GD.Print($"[holepanel] {hud.HolePanelSummary()}");
                return true;
            case "logState":
                LogState(arg.Length > 0 ? arg : "now");
                return true;
            default:
                return false;
        }
    }
}
