using System;
using System.Collections.Generic;
using System.Globalization;
using Godot;
using Spayly.Game.Render;
using Spayly.Sim;

namespace Spayly.Game.Sim;

/// <summary>How many satellites the universe carries (settings.moons in the web build).</summary>
public enum MoonDetail { All, Major, None }

/// <summary>
/// Owns the <see cref="Universe"/> and advances it; the physics half of src/app/app.ts (constructor, tick step 3,
/// stateIsFinite, recoverFromFault, setMoonDetail's universe edits, the collision handler). Never re-implements physics:
/// every number here comes from Spayly.Sim.
/// </summary>
public sealed class SimRunner
{
    /// <summary>Pair evaluations per frame, as the web sets it: adding moons throttles the reachable warp instead of the frame rate.</summary>
    public const double PairBudget = 3e5;
    public const int BeltParticles = 4200;

    public readonly Universe Universe;
    /// <summary>Outcomes resolved during the last steps, oldest first; App.HandleOutcomes drains it every frame.</summary>
    public readonly List<ImpactOutcome> Outcomes = [];
    /// <summary>Signed simulated seconds the last frame advanced (0 when paused); trails and lines use it to tell when they alias.</summary>
    public double LastDtSim { get; private set; }
    /// <summary>Seconds actually advanced last frame, which is less than asked when the substep budget cut it short.</summary>
    public double LastAdvanced { get; private set; }
    public bool WarpLimited => Universe.WarpLimited;
    public int Substeps => Universe.SubstepsLastFrame;
    /// <summary>Tells whether a body-frame direction on a body is water; null until the Earth ocean mask exists (texture task).</summary>
    public SurfaceAtFn? SurfaceAt { get; set; }
    /// <summary>Print a [sim] line every this many steps; 0 turns it off.</summary>
    public int LogEvery = 30;

    readonly CollideOptions collideOptions;
    int steps;
    bool wasLimited;

    public SimRunner(MoonDetail moons, DateTime date)
    {
        var (bodies, epochJd) = Catalog.CreateSolarSystem(date);
        Universe = new Universe(epochJd) { MaxPairEvalsPerFrame = PairBudget };
        Universe.SetBodies(bodies);
        var particles = new TestParticles(BeltParticles);
        Particles.PopulateBelts(particles, Universe.ById("sun")!, Universe.ById("jupiter"));
        Universe.Particles = particles;
        SetMoonDetail(moons, _ => { });
        collideOptions = new CollideOptions
        {
            BodyFrame = (b, d) => Render.Orientation.WorldToBodyFrame(b, d, Universe.Jd),
            SurfaceAt = (b, d) => SurfaceAt?.Invoke(b, d) ?? (false, 0),
        };
        Universe.CollisionHandlers.Add((ev, u) => Outcomes.Add(Collide.ResolveCollision(u, ev, collideOptions)));
    }

    /// <summary>
    /// Add or drop satellites to match <paramref name="level"/>. Every extra body costs force evaluations and the inner moons
    /// force short substeps, so dropping them is what buys back high warps. <paramref name="removing"/> is called before
    /// each removal so the app can move its focus and selection to the primary.
    /// </summary>
    public void SetMoonDetail(MoonDetail level, Action<Body> removing)
    {
        var u = Universe;
        foreach (var sat in SatelliteTable.Satellites)
        {
            var wanted = level == MoonDetail.All || (level == MoonDetail.Major && sat.Radius >= Catalog.MajorMoonRadiusKm);
            var present = u.ById(sat.Id);
            if (wanted && present == null)
            {
                var primary = u.ById(sat.PrimaryId);
                if (primary != null) u.Add(Catalog.MakeSatellite(sat, primary, u.Jd));
            }
            else if (!wanted && present != null)
            {
                removing(present);
                u.Remove(present);
            }
        }
    }

    /// <summary>
    /// Advance by dtReal (s) × warp, backwards when rewinding, unless paused. <paramref name="warpCap"/> is the cinematic
    /// camera's cap (1 for 9 s after an impact), or infinity.
    /// </summary>
    public void Step(double dtReal, double warp, bool rewind, bool paused, double warpCap = double.PositiveInfinity)
    {
        if (paused)
        {
            LastDtSim = 0;
            LastAdvanced = 0;
            return;
        }
        var w = Math.Min(warp, warpCap);
        var dtSim = (rewind ? -1 : 1) * w * dtReal;
        LastAdvanced = Universe.Step(dtSim);
        LastDtSim = dtSim;
        steps++;
        var flipped = Universe.WarpLimited != wasLimited;
        wasLimited = Universe.WarpLimited;
        if (LogEvery > 0 && (steps % LogEvery == 0 || flipped))
        {
            GD.Print(string.Create(CultureInfo.InvariantCulture,
                $"[sim] step={steps} warp={w:G4} dtSim={dtSim:G4}s advanced={LastAdvanced:G4}s substeps={Universe.SubstepsLastFrame} pairEvals={Universe.PairEvalsLastFrame:G4} limited={Universe.WarpLimited} bodies={Universe.Bodies.Count} jd={Universe.Jd:F4}"));
        }
    }

    /// <summary>False when any position, velocity or the clock has gone non-finite (app.ts stateIsFinite).</summary>
    public bool StateIsFinite()
    {
        foreach (var b in Universe.Bodies)
        {
            if (!double.IsFinite(b.Pos.X + b.Pos.Y + b.Pos.Z + b.Vel.X + b.Vel.Y + b.Vel.Z)) return false;
        }
        return double.IsFinite(Universe.Time);
    }

    /// <summary>
    /// Roll back to the last undo point after a numerical fault. NaN fails every comparison, so collision detection switches
    /// off silently and the NaN spreads through the force sum; Step may not validate, so the app checks after it. Returns
    /// whether there was a snapshot to restore; the caller pauses and rebinds its views either way.
    /// </summary>
    public bool RecoverFromFault()
    {
        var restored = Universe.PopHistory() != null;
        GD.PushError($"[spayly] non-finite body state after a step; {(restored ? "rolled back to the last undo point" : "no undo point to roll back to")}");
        Outcomes.Clear();
        return restored;
    }
}
