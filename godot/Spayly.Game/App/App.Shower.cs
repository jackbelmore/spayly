using System;
using System.Diagnostics;
using System.Globalization;
using Godot;
using Spayly.Sim;

namespace Spayly.Game.App;

/// <summary>
/// Meteor showers: with the asteroid panel's Meteor shower ticked, a launch fires the panel's rock broken into 8–24 equal
/// pieces (mass kept) that fall on the target one after another, as comet Shoemaker–Levy 9's fragments fell on Jupiter.
/// The spread (aim, speed, the staggered start) comes from Spayly.Sim's <see cref="Shower"/>, seeded per shower so the same
/// shower is the same every time. The rocks enter over a few frames, lead rock first (<see cref="PumpShower"/>), because a
/// rock's view takes milliseconds to build. The whole volley is one undo point, and while it is the newest point its rocks' impacts
/// record none of their own (Universe.PushHistory), so one Ctrl+Z takes the shower back, craters, debris and all.
/// Capture verbs: <c>shower:N[,target]</c>, <c>showerMode:1|0[,N]</c>, <c>logShower</c>.
/// </summary>
public partial class App
{
    int showerCount;
    /// <summary>Id prefix of the latest shower's rocks ("shower3-"), or null before the first.</summary>
    string? showerPrefix;
    int showerSize, showerImpacts;
    string showerTarget = "";
    /// <summary>The cinematic camera has already flown to one of this shower's impacts.</summary>
    bool showerLooked;

    /// <summary>[GAME] The shower's shape; the HUD calls this when it builds the Realism panel.</summary>
    public static void RegisterShowerRealism()
    {
        Realism.Register("Meteor shower", RealismTag.Game,
            "A shower is the panel's rock broken into N equal pieces of its density (mass kept, each d/∛N across), falling one after another as comet Shoemaker–Levy 9's 21 fragments hit Jupiter in 1994; the spread of aim over 0.6 of the target's radius, of speed (±5 %) and the launch window (a tenth of the flight time) are chosen for play, and the shower's own impacts share its one undo point.",
            RealismArea.Collisions);
    }

    /// <summary>Pieces the panel asks for: 0 when the shower is off or the launch is a star, which is fired whole.</summary>
    int ShowerRocksWanted() => Asteroid.Shower && Asteroid.Composition != Composition.Star ? Shower.Clamp(Asteroid.ShowerRocks) : 0;

    bool IsShowerRock(Body b) => showerPrefix != null && b.Id.StartsWith(showerPrefix, StringComparison.Ordinal);

    /// <summary>
    /// Fire <paramref name="n"/> pieces of the panel's rock from <paramref name="from"/> (km), spread about the aimed velocity
    /// <paramref name="vel"/> (km/s) that hits <paramref name="target"/> after <paramref name="flightTime"/> s. One undo point.
    /// </summary>
    void FireShower(Vec3 from, Vec3 vel, Body target, double flightTime, int n)
    {
        var a = Asteroid;
        n = Shower.Clamp(n);
        // The body cap is what keeps a shatter cascade bounded; a shower must not use it all up.
        var room = Universe.MaxBodies - Universe.Bodies.Count;
        if (room < Shower.MinRocks)
        {
            Hint($"No room for a shower: {Universe.Bodies.Count} bodies of {Universe.MaxBodies}. Erase some rocks first (Z).", 4);
            return;
        }
        n = Math.Min(n, room);
        var pieceD = Shower.PieceDiameter(a.DiameterM, n);
        var r = pieceD / 2000;
        showerCount++;
        var prefix = $"shower{showerCount}-";
        var seed = unchecked((uint)showerCount * 2654435761u ^ (uint)n);
        ShowerRock[] rocks;
        try
        {
            rocks = Shower.Spread(n, from, vel, target.Vel, target.Radius, flightTime, r, seed);
        }
        catch (ArgumentException e)
        {
            GD.PushError($"[shower] not fired: {e.Message}");
            Hint($"Could not aim a shower at {target.Name}", 3);
            return;
        }
        RecordUndo(UndoKind.Shower, $"a meteor shower of {n} rocks", prefix);
        showerPrefix = prefix;
        showerSize = n;
        showerImpacts = 0;
        showerTarget = target.Id;
        showerLooked = false;
        var name = a.Name ?? "Meteor";
        launching = new LaunchingShower(rocks, Universe.Time, prefix, name, pieceD, a.Composition, target.Id, Stopwatch.GetTimestamp());

        var window = rocks[^1].Delay;
        var minSep = double.PositiveInfinity;
        for (var i = 0; i < rocks.Length; i++)
        for (var j = i + 1; j < rocks.Length; j++)
            minSep = Math.Min(minSep, Vec3.Distance(rocks[i].Pos, rocks[j].Pos));
        var mass = Presets.Density(a.Composition) * (Math.PI / 6) * Math.Pow(pieceD, 3);
        GD.Print(string.Create(CultureInfo.InvariantCulture,
            $"[shower] {prefix} n={n} at {target.Id}: piece d={pieceD:G4} m mass={mass:G4} kg total={mass * n:G4} kg {a.Composition} v={a.SpeedKms:G4} km/s flight={flightTime:G5} s window={window:G5} s min_sep={minSep:G5} km ({minSep / r:G4} radii) seed={seed} bodies={Universe.Bodies.Count} {MomentumLedger()}"));
        Hint(string.Create(CultureInfo.InvariantCulture,
            $"A shower of {n} {name} pieces, {UI.Format.LengthM(pieceD)} each, falls on {target.Name} from {FormatDuration(flightTime)} on, over {FormatDuration(window)}. Warp time to watch it land."), 5);
        // The rocks start entering next frame (PumpShower): this one has already paid for the aim and the undo point.
    }

    /// <summary>A shower still entering the universe: its rocks as <see cref="Shower.Spread"/> placed them at <see cref="T0"/> (s), lead rock first.</summary>
    sealed class LaunchingShower(ShowerRock[] rocks, double t0, string prefix, string name, double pieceD, Composition composition, string targetId, long started)
    {
        public readonly ShowerRock[] Rocks = rocks;
        public readonly double T0 = t0;
        public readonly string Prefix = prefix, Name = name, TargetId = targetId;
        public readonly double PieceD = pieceD;
        public readonly Composition Composition = composition;
        public readonly long Started = started;
        public int Next, Frames;
    }

    LaunchingShower? launching;

    /// <summary>
    /// Wall time a frame may spend adding shower rocks, ms. A rock's view (mesh, materials, label, orbit line) costs 2.5–5.6 ms
    /// to build (m7-feat-shower-probe: 24 at once took 62–135 ms), so the volley enters over a few frames, lead rock first.
    /// </summary>
    const double ShowerBudgetMs = 6;

    /// <summary>
    /// Once a frame (App.Tick.cs, before physics): the next rocks of a shower still launching, each carried along its straight
    /// line from where the spread put it by the time since, so a rock entering a few frames late is where it would be.
    /// </summary>
    void PumpShower(bool all = false)
    {
        var p = launching;
        if (p == null) return;
        var t0 = Stopwatch.GetTimestamp();
        p.Frames++;
        var n = p.Rocks.Length;
        var target = Universe.ById(p.TargetId);
        do
        {
            var k = p.Next++;
            var rock = p.Rocks[k];
            var b = NewRock($"{p.Prefix}{k + 1}", $"{p.Name} {k + 1}/{n}", p.PieceD, p.Composition, rock.Pos + rock.Vel * (Universe.Time - p.T0), rock.Vel, target);
            Universe.Add(b);
            SyncViews();
            if (k == 0) RecordLaunch(b, target, null);
        } while (p.Next < n && (all || Stopwatch.GetElapsedTime(t0).TotalMilliseconds < ShowerBudgetMs));
        if (p.Next < n) return;
        launching = null;
        GD.Print(string.Create(CultureInfo.InvariantCulture,
            $"[shower] {p.Prefix} all {n} launched over {p.Frames} frames, {Stopwatch.GetElapsedTime(p.Started).TotalMilliseconds:F1} ms{(all ? " (flushed)" : "")} bodies={Universe.Bodies.Count}"));
    }

    /// <summary>Put every rock of a shower still launching into the universe now: before any new undo point, undo or redo, so a point never holds half a shower.</summary>
    void FlushShower() => PumpShower(true);

    /// <summary>HandleOutcomes: one impact of a shower rock. True when the cinematic camera should fly to it (the shower's first only).</summary>
    bool OnShowerImpact(ImpactOutcome o)
    {
        if (!IsShowerRock(o.Impactor)) return true;
        showerImpacts++;
        GD.Print(string.Create(CultureInfo.InvariantCulture,
            $"[shower] impact {showerImpacts}/{showerSize} {o.Impactor.Id} -> {o.Target.Id} kind={o.Kind} rel={o.RelSpeed:F3} km/s frame={Engine.GetProcessFrames()}"));
        if (showerLooked) return false;
        showerLooked = true;
        return true;
    }

    bool ShowerAction(string verb, string arg)
    {
        switch (verb)
        {
            // shower:N[,target]: fire a shower of N (clamped to 8–24) at the body, else the selected one, from the camera.
            case "shower":
            {
                var p = arg.Split(',');
                var n = p[0].Length > 0 ? int.Parse(p[0], CultureInfo.InvariantCulture) : Asteroid.ShowerRocks;
                var target = p.Length > 1 ? Universe.ById(p[1]) : Selected;
                if (target == null)
                {
                    GD.PushError($"[shower] no target '{arg}'");
                    return true;
                }
                FireAtBody(target, Math.Max(n, 1));
                return true;
            }
            case "showerMode":
            {
                var p = arg.Split(',');
                Asteroid.Shower = p[0] is "1" or "true" or "on";
                if (p.Length > 1) Asteroid.ShowerRocks = Shower.Clamp(int.Parse(p[1], CultureInfo.InvariantCulture));
                hud.SyncAsteroid();
                return true;
            }
            case "logShower":
            {
                var alive = 0;
                if (showerPrefix != null)
                    foreach (var b in Universe.Bodies)
                        if (IsShowerRock(b)) alive++;
                GD.Print(string.Create(CultureInfo.InvariantCulture,
                    $"[shower] state {showerPrefix ?? "none"} size={showerSize} launching={(launching != null ? $"{launching.Next}/{launching.Rocks.Length}" : "no")} alive={alive} impacts={showerImpacts} target={showerTarget} bodies={Universe.Bodies.Count} history={Universe.History.Count}/{Universe.History.Redo.Count} next_undo='{Universe.History.NextUndo?.Label}'"));
                return true;
            }
            default:
                return false;
        }
    }
}
