using System;
using System.Collections.Generic;
using System.Globalization;
using Godot;
using Spayly.Game.Render;
using Spayly.Sim;
using Spayly.Sim.Debris;

namespace Spayly.Game.App;

/// <summary>
/// Names for what a tidal disruption leaves. A body torn apart is removed and its stream lives on the GPU as unnamed
/// particles, so without this the planet's label simply vanishes. Each full disruption of a named body gets a label,
/// "Earth (debris)", on the stream's centre of mass, which follows the debris as a massless point.
///
/// Rendering only: the point is integrated here with its own leapfrog under the bodies' gravity, reads the bodies and never
/// writes one, and touches neither the universe's particles nor the debris ledger. It is an estimate of where the middle of
/// the cloud is (a stretched stream's centre is not a particle's orbit), and it ends where the cloud does: when the tier's
/// particle lifetime is over, when the point reaches a body's surface, or when an undo takes the disruption back.
/// </summary>
public partial class App
{
    /// <summary>[GAME] Remnant labels at once; the oldest goes first.</summary>
    const int MaxRemnants = 8;
    /// <summary>[GAME] Substeps one frame may spend moving one remnant point, so a high warp near a hole cannot stall the frame; the point then simply lags.</summary>
    const int RemnantMaxSubsteps = 400;
    /// <summary>[GAME] Substep as a fraction of the shortest local free-fall time sqrt(r³/GM) of any body.</summary>
    const double RemnantStepFraction = 0.05;

    sealed class Remnant
    {
        public required string Id;
        public required string Name;
        public required string ParentName;
        public required Body Ghost;
        /// <summary>km, world frame.</summary>
        public Vec3 Pos;
        /// <summary>km/s.</summary>
        public Vec3 Vel;
        /// <summary>Universe time of the disruption, s.</summary>
        public double Born;
    }

    readonly List<Remnant> remnants = [];
    int remnantSeq;

    /// <summary>Whether <paramref name="b"/> is the stand-in a remnant label carries: not in the universe, so a click must not select it.</summary>
    bool IsRemnantGhost(Body b) => b.Id.StartsWith("remnant:", StringComparison.Ordinal);

    /// <summary>
    /// Label the debris of <paramref name="s"/>, torn apart by <paramref name="p"/>, at the stream's centre of mass.
    /// <paramref name="d"/>'s positions are relative to the primary.
    /// </summary>
    void AddRemnant(Body p, Body s, TidalDisruption d, Vec3 pos0, Vec3 vel0)
    {
        // A partial disruption leaves the core, which keeps the body's own label; the stripped layers are too small a share to name.
        if (!IsNamedBody(s) || !d.Full) return;
        var pos = pos0;
        var vel = vel0;
        if (d.Count > 0)
        {
            double m = 0;
            Vec3 sp = default, sv = default;
            for (var i = 0; i < d.Count; i++)
            {
                m += d.Mass[i];
                sp += d.Position[i] * d.Mass[i];
                sv += d.Velocity[i] * d.Mass[i];
            }
            if (m > 0)
            {
                pos = p.Pos + sp * (1 / m);
                vel = p.Vel + sv * (1 / m);
            }
        }
        var name = $"{s.Name} (debris)";
        var id = $"remnant:{s.Id}:{++remnantSeq}";
        var ghost = Body.MakeBody(new BodySpec
        {
            Id = id, Name = name, Kind = BodyKind.Fragment, Surface = Surface.Rock, Mass = 1, Radius = 1, Visual = s.Visual,
        });
        var label = labels.Add(ghost);
        label.TooltipText = $"{name}: the centre of what is left of {s.Name}. It is a marker, not a body";
        remnants.Add(new Remnant { Id = id, Name = name, ParentName = s.Name, Ghost = ghost, Pos = pos, Vel = vel, Born = Universe.Time });
        while (remnants.Count > MaxRemnants) DropRemnant(0, "too many labels");
        GD.Print(string.Create(CultureInfo.InvariantCulture, $"[remnant] '{name}' labelled at sim_t={Universe.Time:F1} s, {remnants.Count} remnant label(s)"));
    }

    void DropRemnant(int i, string why)
    {
        var r = remnants[i];
        remnants.RemoveAt(i);
        labels.Remove(r.Id);
        GD.Print(string.Create(CultureInfo.InvariantCulture, $"[remnant] '{r.Name}' label removed: {why} (sim_t={Universe.Time:F1} s)"));
    }

    /// <summary>Step 11, before the labels: carry each remnant point through this frame's sim time and drop the ones that are over.</summary>
    void AdvanceRemnants()
    {
        if (remnants.Count == 0) return;
        var dt = Sim.LastAdvanced;
        var life = debris?.Lifetime ?? 30 * Constants.Day;
        for (var i = remnants.Count - 1; i >= 0; i--)
        {
            var r = remnants[i];
            if (Universe.Time < r.Born - 1e-6)
            {
                DropRemnant(i, "its disruption was undone");
                continue;
            }
            if (Universe.Time - r.Born > life)
            {
                DropRemnant(i, "the debris has lived out its particle lifetime");
                continue;
            }
            if (dt != 0 && !StepRemnant(r, dt)) DropRemnant(i, "its centre fell into a body");
        }
    }

    /// <summary>
    /// Leapfrog the point by <paramref name="dt"/> s (negative runs it back) in the field of the bodies as they are now, a
    /// black hole's by Paczyński–Wiita as the sim's own test particles feel it. False when it reaches a body's surface.
    /// </summary>
    bool StepRemnant(Remnant r, double dt)
    {
        var bodies = Universe.Bodies;
        var remaining = dt;
        var pos = r.Pos;
        var vel = r.Vel;
        var sign = dt < 0 ? -1.0 : 1.0;
        Vec3 Acc(Vec3 x, out double fall, out bool inside)
        {
            Vec3 a = default;
            fall = double.PositiveInfinity;
            inside = false;
            for (var k = 0; k < bodies.Count; k++)
            {
                var b = bodies[k];
                if (!(b.Mass > 0) || b.Kind == BodyKind.Fragment) continue;
                var rel = b.Pos - x;
                var d2 = Vec3.Dot(rel, rel);
                var hole = b.Kind == BodyKind.BlackHole;
                var contact = hole ? PseudoNewtonian.ContactRadius(b) : b.Radius;
                if (d2 < contact * contact)
                {
                    inside = true;
                    return a;
                }
                var dist = Math.Sqrt(d2);
                var rs = hole ? PseudoNewtonian.HoleRs(b) : 0;
                var g = Constants.G * b.Mass / (dist * (dist - rs) * (dist - rs));
                a += rel * g;
                fall = Math.Min(fall, Math.Sqrt(dist * dist * dist / (Constants.G * b.Mass)));
            }
            return a;
        }
        var acc = Acc(pos, out var tff, out var gone);
        for (var n = 0; n < RemnantMaxSubsteps && Math.Abs(remaining) > 1e-6 && !gone; n++)
        {
            var h = Math.Min(Math.Abs(remaining), Math.Max(1e-3, RemnantStepFraction * tff)) * sign;
            vel += acc * (h / 2);
            pos += vel * h;
            acc = Acc(pos, out tff, out gone);
            vel += acc * (h / 2);
            remaining -= h;
        }
        if (gone) return false;
        r.Pos = pos;
        r.Vel = vel;
        return true;
    }

    /// <summary>logRemnants: each remnant label's state and whether it survived declutter this frame.</summary>
    void LogRemnants()
    {
        GD.Print($"[remnant] {remnants.Count} label(s)");
        foreach (var r in remnants)
        {
            var l = labels.For(r.Id);
            var at = l != null ? l.Position + l.Size / 2 : default;
            var nearest = "";
            var best = double.PositiveInfinity;
            foreach (var b in Universe.Bodies)
            {
                var dd = Vec3.Length(b.Pos - r.Pos);
                if (dd < best) { best = dd; nearest = b.Id; }
            }
            GD.Print(string.Create(CultureInfo.InvariantCulture,
                $"[remnant]   '{r.Name}' visible={l?.Visible == true} at=({at.X:F0},{at.Y:F0}) age={Universe.Time - r.Born:G5} s nearest={nearest} at {best:G4} km speed={Vec3.Length(r.Vel):G4} km/s"));
        }
    }
}
