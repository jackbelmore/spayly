using static Spayly.Sim.Vec3;

namespace Spayly.Sim;

/// <summary>What kind of radius a body is about to reach round a black hole.</summary>
public enum ApproachEvent { TidalRadius, Capture }

/// <summary>
/// A body falling toward a black hole. <see cref="Radius"/> (km) is where the event starts: the tidal radius, or
/// <see cref="Approach.CaptureZone"/> × the contact distance when the body cannot be torn apart there. <see cref="Arrive"/>
/// (s) is the two-body time until the body reaches it, <see cref="Zone"/> (s) the time from there to contact (or periapsis).
/// </summary>
public readonly record struct ApproachInfo(Body Body, Body Hole, ApproachEvent Event, double Radius, double Contact, double Arrive, double Zone);

/// <summary>
/// Looks ahead for bodies that are about to reach a black hole's tidal radius or its 2 r_s capture radius, so the app can
/// slow the warp before a single frame steps over the whole event (M6.2). Times are two-body Paczyński–Wiita integrals of
/// dr/ṙ: a lower bound from the fastest the body could move (distance over the speed at the far end) fired days early for a
/// body falling from rest, the whole solar system toward a hole placed 50 AU away.
/// </summary>
public static class Approach
{
    /// <summary>[GAME] With no tidal radius outside contact, the event zone is this many contact distances out.</summary>
    public const double CaptureZone = 3;

    /// <summary>
    /// Every body falling toward a hole, inbound, outside the event radius and with a two-body periapsis inside it. Fragments
    /// are skipped: a debris cloud falling in would cap the warp over and over. <paramref name="tidal"/> false (the Tidal
    /// setting off) leaves only capture events. <paramref name="into"/> is cleared and reused.
    /// </summary>
    public static List<ApproachInfo> Find(Universe u, bool tidal, List<ApproachInfo> into)
    {
        into.Clear();
        var bodies = u.Bodies;
        for (var i = 0; i < bodies.Count; i++)
        {
            var h = bodies[i];
            if (h.Kind != BodyKind.BlackHole) continue;
            for (var j = 0; j < bodies.Count; j++)
            {
                var b = bodies[j];
                if (j == i || b.Kind == BodyKind.Fragment || !(b.Mass > 0)) continue;
                // Two holes: the lighter one does the approaching, so the pair is reported once.
                if (b.Kind == BodyKind.BlackHole && (b.Mass > h.Mass || (b.Mass == h.Mass && j < i))) continue;
                if (Test(h, b, tidal) is ApproachInfo a) into.Add(a);
            }
        }
        return into;
    }

    /// <summary>The approach of <paramref name="b"/> to hole <paramref name="h"/>, or null when it is not falling toward an event.</summary>
    public static ApproachInfo? Test(Body h, Body b, bool tidal)
    {
        var rs = PseudoNewtonian.HoleRs(h) + PseudoNewtonian.HoleRs(b);
        var contact = PseudoNewtonian.ContactRadius(h) + (b.Kind == BodyKind.BlackHole ? PseudoNewtonian.ContactRadius(b) : b.Radius);
        var ev = ApproachEvent.Capture;
        var radius = CaptureZone * contact;
        if (tidal && b.Kind != BodyKind.BlackHole)
        {
            var (_, limit) = Tidal.LimitFor(h, b);
            if (limit > contact)
            {
                ev = ApproachEvent.TidalRadius;
                radius = Math.Max(limit, radius);
            }
        }
        var r = b.Pos - h.Pos;
        var v = b.Vel - h.Vel;
        var d = Length(r);
        if (!(d > radius) || Dot(r, v) >= 0) return null;
        var mu = Constants.G * (h.Mass + b.Mass);
        var rp = PseudoNewtonian.Periapsis(r, v, mu, rs);
        if (!(rp < radius)) return null;
        var hh = Dot(Cross(r, v), Cross(r, v));
        var energy = 0.5 * Dot(v, v) + PseudoNewtonian.Potential(mu, d, rs);
        return new ApproachInfo(b, h, ev, radius, contact,
            RadialTime(Math.Max(rp, radius), d, energy, hh, mu, rs, anchorHigh: true),
            RadialTime(Math.Max(rp, contact), radius, energy, hh, mu, rs, anchorHigh: false));
    }

    /// <summary>
    /// Two-body time, s, to fall from <paramref name="hi"/> to <paramref name="lo"/> (km) on the PW orbit of specific energy
    /// <paramref name="energy"/> (km²/s²) and squared specific angular momentum <paramref name="hh"/>: ∫ dr / |ṙ|. |ṙ| can
    /// vanish at the end a body starts at rest from (<paramref name="anchorHigh"/>) or at periapsis (the low end); the
    /// substitution r = end ∓ (hi − lo) s² makes that 1/√ singularity finite, and a midpoint sum does the rest.
    /// </summary>
    static double RadialTime(double lo, double hi, double energy, double hh, double mu, double rs, bool anchorHigh)
    {
        if (!(hi > lo)) return 0;
        const int n = 64;
        var span = hi - lo;
        double t = 0;
        for (var k = 0; k < n; k++)
        {
            var s = (k + 0.5) / n;
            var r = anchorHigh ? hi - span * s * s : lo + span * s * s;
            var rdot2 = 2 * (energy - PseudoNewtonian.Potential(mu, r, rs)) - hh / (r * r);
            if (!(rdot2 > 0)) continue;
            t += 2 * span * s / Math.Sqrt(rdot2) / n;
        }
        return t;
    }

    /// <summary>
    /// The warp at which the event zone of <paramref name="a"/> takes <paramref name="frames"/> frames of
    /// <paramref name="dtReal"/> s each, never below 1.
    /// </summary>
    public static double WarpFor(ApproachInfo a, double dtReal, double frames) => Math.Max(1, a.Zone / (frames * dtReal));
}
