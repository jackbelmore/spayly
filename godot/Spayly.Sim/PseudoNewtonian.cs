namespace Spayly.Sim;

/// <summary>
/// Paczyński–Wiita (1980) pseudo-Newtonian gravity for any pair that includes a black hole: a = G M / (r − r_s)² along r̂,
/// from the potential Φ = −G M / (r − r_s). It is still a central pair force, equal and opposite, so momentum is exact and
/// energy is conserved against that Φ; and it puts the innermost stable circular orbit at 3 r_s and the marginally bound
/// orbit at 2 r_s, where the circular speed reaches c, as Schwarzschild geodesics do. A pair without a hole has r_s = 0 and
/// is Newton's law exactly; every call site keeps its old arithmetic for that case, so hole-free runs stay bit-identical.
///
/// The pair's r_s is the sum of its holes' Schwarzschild radii: the hole's own next to an ordinary body, and for two holes
/// that of their total mass, the one-body problem PW describes. Anything reaching <see cref="CaptureRs"/> r_s is captured,
/// so the r ≤ r_s singularity is never evaluated: callers soften the separation to at least the capture radius.
/// km, kg, s.
/// </summary>
public static class PseudoNewtonian
{
    /// <summary>Capture radius in r_s: the marginally bound orbit. Inside it no orbit returns, whatever its angular momentum.</summary>
    public const double CaptureRs = 2;

    /// <summary>The r_s a body adds to any pair it is in, km: its Schwarzschild radius if it is a black hole, else 0.</summary>
    public static double HoleRs(Body b) => b.Kind == BodyKind.BlackHole ? BlackHole.SchwarzschildRadiusKm(b.Mass) : 0;

    /// <summary>
    /// The radius contact is tested at, km: <see cref="CaptureRs"/> r_s for a hole, the body's radius otherwise. Body.Radius
    /// stays r_s for a hole, the horizon the renderer and inspector show.
    /// </summary>
    public static double ContactRadius(Body b) => b.Kind == BodyKind.BlackHole ? CaptureRs * BlackHole.SchwarzschildRadiusKm(b.Mass) : b.Radius;

    /// <summary>|a| at separation <paramref name="r"/> km from a mass with μ = GM km³/s², pair r_s <paramref name="rs"/> km: μ/(r − r_s)², km/s².</summary>
    public static double Acceleration(double mu, double r, double rs) => mu / ((r - rs) * (r - rs));

    /// <summary>Φ = −μ/(r − r_s), km²/s².</summary>
    public static double Potential(double mu, double r, double rs) => -mu / (r - rs);

    /// <summary>Circular speed √(μ r)/(r − r_s), km/s: c at 2 r_s.</summary>
    public static double CircularSpeed(double mu, double r, double rs) => Math.Sqrt(mu * r) / (r - rs);

    /// <summary>Period of the circular orbit at <paramref name="r"/>, 2π (r − r_s) √(r/μ), s.</summary>
    public static double CircularPeriod(double mu, double r, double rs) => 2 * Math.PI * (r - rs) * Math.Sqrt(r / mu);

    /// <summary>Specific angular momentum of the circular orbit, √(μ r³)/(r − r_s), km²/s; its minimum is at 3 r_s (the ISCO).</summary>
    public static double CircularAngularMomentum(double mu, double r, double rs) => Math.Sqrt(mu * r * r * r) / (r - rs);

    /// <summary>
    /// Periapsis of the relative orbit through (r km, v km/s) in the PW potential, km: the largest turning point at or below
    /// |r| of v_r²/2 = E + μ/(x − r_s) − L²/(2x²). Returns <paramref name="rs"/> when there is none above
    /// <see cref="CaptureRs"/> r_s (a plunge). With r_s = 0 it is the Keplerian periapsis h²/(μ(1 + e)).
    /// </summary>
    public static double Periapsis(Vec3 r, Vec3 v, double mu, double rs)
    {
        if (rs <= 0) return Tidal.Periapsis(r, v, mu);
        var d = Vec3.Length(r);
        var h = Vec3.Cross(r, v);
        var l2 = Vec3.Dot(h, h);
        var energy = 0.5 * Vec3.Dot(v, v) - mu / (d - rs);
        double G(double x) => energy + mu / (x - rs) - l2 / (2 * x * x);
        var floor = CaptureRs * rs;
        if (d <= floor) return rs;
        // Walk inwards in 2 % steps to the first sign change, then bisect; the radial kinetic energy is ≥ 0 at |r| itself. A
        // negative dip narrower than a step (an orbit grazing the top of the potential barrier) can be missed and read as a plunge.
        var hi = d;
        for (var k = 0; k < 4000; k++)
        {
            var lo = Math.Max(floor, hi * 0.98);
            if (G(lo) < 0)
            {
                for (var it = 0; it < 80; it++)
                {
                    var mid = 0.5 * (lo + hi);
                    if (G(mid) < 0) lo = mid;
                    else hi = mid;
                }
                return 0.5 * (lo + hi);
            }
            if (lo <= floor) return rs;
            hi = lo;
        }
        return rs;
    }
}
