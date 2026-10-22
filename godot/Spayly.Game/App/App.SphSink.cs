using System;
using System.Collections.Generic;
using System.Globalization;
using Godot;
using Spayly.Sim;
using Spayly.Sim.Debris;

namespace Spayly.Game.App;

/// <summary>
/// The debris tier as the hero collision's <see cref="IDebrisSink"/>: the SPH remainder (the particles in no clump) becomes
/// T2 particles. The remainder's totals are the exact difference from the event's truth ledger, while the SPH particles
/// drifted slightly (float32 masses on the GPU), so the particles are fitted to the totals: masses scaled to the remainder
/// mass, a uniform velocity shift for its momentum and a rigid rotation about their centre of mass for its angular momentum.
/// What was queued is booked in the debris ledger as entering T2; the proxy and the new bodies are the audit's external
/// T0 changes, so the book balances the remainder that left T0 against the particles that took it.
/// </summary>
public partial class App
{
    sealed class TierSink(App app) : IDebrisSink
    {
        public bool Inject(double mass, Vec3 momentum, Vec3 angularMomentum, IReadOnlyList<Vec3> positions, IReadOnlyList<Vec3> velocities, IReadOnlyList<double> masses) =>
            app.InjectSphRemainder(mass, momentum, angularMomentum, positions, velocities, masses);
    }

    /// <summary>[GAME] Density a remainder particle's radius is sized at, kg/m³ (rock).</summary>
    const double SphRemainderDensity = 3000;

    void EnsureSphSink()
    {
        EnsureDebris();
        if (DebrisSink == null && debris != null) DebrisSink = new TierSink(this);
    }

    bool InjectSphRemainder(double mass, Vec3 momentum, Vec3 angularMomentum, IReadOnlyList<Vec3> positions, IReadOnlyList<Vec3> velocities, IReadOnlyList<double> masses)
    {
        var tier = debris is { IsReady: true } t ? t : null;
        var l = ledger;
        var n = Math.Min(positions.Count, tier?.CanQueue ?? 0);
        if (tier == null || l == null || n < 3 || !(mass > 0)) return false;

        // Masses: the first n particles' SPH masses, scaled to the remainder mass; the last one takes the exact difference.
        var m = new double[n];
        double sum = 0;
        for (var i = 0; i < n; i++) sum += masses[i];
        var scale = mass / sum;
        var acc = DD.Zero;
        for (var i = 0; i < n - 1; i++)
        {
            m[i] = masses[i] * scale;
            acc += m[i];
        }
        m[n - 1] = (new DD(mass, 0) - acc).Value;
        if (!(m[n - 1] > 0)) return false;

        var r = new Vec3[n];
        var v = new Vec3[n];
        var com = Vec3.Zero;
        for (var i = 0; i < n; i++)
        {
            r[i] = positions[i];
            v[i] = velocities[i];
            com += r[i] * m[i];
        }
        com *= 1 / mass;
        double ixx = 0, iyy = 0, izz = 0, ixy = 0, ixz = 0, iyz = 0;
        for (var i = 0; i < n; i++)
        {
            var d = r[i] - com;
            ixx += m[i] * (d.Y * d.Y + d.Z * d.Z);
            iyy += m[i] * (d.X * d.X + d.Z * d.Z);
            izz += m[i] * (d.X * d.X + d.Y * d.Y);
            ixy -= m[i] * d.X * d.Y;
            ixz -= m[i] * d.X * d.Z;
            iyz -= m[i] * d.Y * d.Z;
        }
        var det = ixx * (iyy * izz - iyz * iyz) - ixy * (ixy * izz - iyz * ixz) + ixz * (ixy * iyz - iyy * ixz);
        var rotate = Math.Abs(det) > 0 && double.IsFinite(det);
        double Det3(Vec3 a, Vec3 b, Vec3 c) => a.X * (b.Y * c.Z - b.Z * c.Y) - b.X * (a.Y * c.Z - a.Z * c.Y) + c.X * (a.Y * b.Z - a.Z * b.Y);
        var c0 = new Vec3(ixx, ixy, ixz);
        var c1 = new Vec3(ixy, iyy, iyz);
        var c2 = new Vec3(ixz, iyz, izz);
        var goal = new Tally(mass, DV.Product(1, momentum), DV.Product(1, angularMomentum - Vec3.Cross(l.Origin, momentum)));
        var dvTotal = Vec3.Zero;
        // Two passes: the second takes out what the first's double arithmetic left, measured in double-double.
        for (var pass = 0; pass < 2; pass++)
        {
            var now = Tally.Zero;
            for (var i = 0; i < n; i++) now += Tally.Of(m[i], r[i], v[i], l.Origin);
            var miss = goal - now;
            // Momentum: one shift for all.
            var dv = miss.Momentum.Value * (1 / mass);
            dvTotal += dv;
            for (var i = 0; i < n; i++) v[i] += dv;
            if (!rotate) continue;
            // Angular momentum: a rigid rotation about the centre of mass adds I ω and no momentum; the shift above added
            // (com − origin) × M dv, which the measured miss already accounts for once re-tallied.
            now = Tally.Zero;
            for (var i = 0; i < n; i++) now += Tally.Of(m[i], r[i], v[i], l.Origin);
            var dl = (goal - now).Angular.Value;
            var w = new Vec3(Det3(dl, c1, c2), Det3(c0, dl, c2), Det3(c0, c1, dl)) * (1 / det);
            for (var i = 0; i < n; i++) v[i] += Vec3.Cross(w, r[i] - com);
        }
        var dv0 = dvTotal;

        var booked = Tally.Zero;
        var queued = 0;
        for (var i = 0; i < n; i++)
        {
            if (!tier.Queue(r[i], v[i], m[i], Body.RadiusForDensity(m[i], SphRemainderDensity))) break;
            booked += Tally.Of(m[i], r[i], v[i], l.Origin);
            queued++;
        }
        // n ≤ CanQueue, so every Queue above succeeds; were one refused, the closure below would show the missing mass.
        l.AddExternalParticles(booked);
        var gap = booked - goal;
        var closure = string.Create(CultureInfo.InvariantCulture,
            $"|ΔM|/M={Math.Abs(gap.Mass.Value) / mass:E2} |ΔP|/|P|={gap.Momentum.Length / Math.Max(Vec3.Length(momentum), 1e-300):E2} |ΔL|/|L|={gap.Angular.Length / Math.Max(Vec3.Length(angularMomentum - Vec3.Cross(l.Origin, momentum)), 1e-300):E2}");
        GD.Print(string.Create(CultureInfo.InvariantCulture,
            $"[sph] remainder -> debris tier: {queued} particles of {positions.Count}, M={mass:E4} kg, scale {scale:F9}, dv={Vec3.Length(dv0):E3} km/s; sink closure {closure}"));
        return true;
    }
}
