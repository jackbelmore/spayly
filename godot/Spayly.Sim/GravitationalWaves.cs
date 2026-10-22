namespace Spayly.Sim;

/// <summary>
/// What a merger of two non-spinning black holes radiates in gravitational waves, from numerical-relativity fits. η is the
/// symmetric mass ratio m₁m₂/(m₁+m₂)², δm = (m₁ − m₂)/(m₁ + m₂) = √(1 − 4η).
///
/// Remnant: Healy, Lousto &amp; Zlochower (2014), Phys. Rev. D 90, 104004 (arXiv:1406.7295), Eqs. (14) and (16) with every
/// initial-spin term zero, coefficients from their Table VI (checked against the paper's text, M6.5):
/// M_rem/m = (4η)² (M0 + K2d δm² + K4f δm⁴) + [1 + η(Ẽ_ISCO + 11)] δm⁶ and
/// α_rem = (4η)² (L0 + L2d δm² + L4f δm⁴) + η J̃_ISCO δm⁶, both ISCO quantities those of a Kerr hole of spin α_rem
/// (Ori &amp; Thorne 2000 Eqs. 2.7–2.8, the Bardeen–Press–Teukolsky prograde ISCO), so α_rem is solved by iteration.
///
/// Inspiral (M6.4): two holes lose orbital energy and angular momentum to the waves continuously, through the 2.5PN
/// radiation-reaction acceleration of the relative motion (Iyer &amp; Will 1993; Blanchet 2014 Living Rev. Relativ. 17, 2,
/// Eq. 219 in harmonic gauge; Iyer–Will α = −1, β = 0), applied equal and opposite so momentum stays exact. Burke–Thorne
/// (α = 4, β = 5) differs only by terms that average out over an orbit, with the same circular and radial forms. Chosen over an orbit-averaged drag fitted
/// to Peters' da/dt and de/dt because it is local: it needs no osculating a and e, which are ill defined in a three-body
/// encounter or a plunge, and its orbit average is Peters (1964) exactly, eccentric orbits included. Its conservative
/// partner is PW gravity, which moves the inspiral time by ≈ −3.3 r_s/a against Peters (−0.8 % from 400 r_s). On a head-on
/// plunge the force adds energy (every gauge of the family agrees on radial motion), 1.2 % of c at contact from 41 r_s.
///
/// The inspiral and the merger do not double count. The reaction force takes orbital energy (kinetic plus PW potential),
/// never mass. <see cref="RemnantMassFraction"/> is the rest mass the whole coalescence radiates, from infinity through
/// ringdown, and it is booked once, at contact, as the pair minus the remnant. The orbital energy the inspiral removed was
/// binding energy, not mass, and so is the relative kinetic energy the inelastic merge drops at contact. HLZ14's E_rad is
/// the physical total that both stand in for.
///
/// Recoil: González et al. (2007), Phys. Rev. Lett. 98, 091101: v = A η² √(1 − 4η) (1 + B η), A = 1.2 × 10⁴ km/s,
/// B = −0.93, in the orbital plane; zero for equal masses, largest (≈ 175 km/s) at η ≈ 0.195 (q ≈ 0.36).
/// </summary>
public static class GravitationalWaves
{
    public const double M0 = 0.951507, K2d = 1.995246, K4f = 2.980990;
    /// <summary>HLZ14 Table VI remnant-spin coefficients of Eq. (16) that survive with zero initial spins.</summary>
    public const double L0 = 0.686710, L2d = 0.801838, L4f = 0.953458;
    /// <summary>km/s.</summary>
    public const double KickA = 1.2e4;
    public const double KickB = -0.93;
    /// <summary>Specific energy at the Schwarzschild ISCO, √(8/9): <see cref="KerrIscoEnergy"/> at spin 0.</summary>
    public static readonly double IscoEnergy = Math.Sqrt(8.0 / 9);

    /// <summary>Prograde ISCO radius of a Kerr hole of spin <paramref name="a"/> (0 ≤ a &lt; 1), in units of GM/c² (BPT 1972): 6 at a = 0.</summary>
    public static double KerrIscoRadius(double a)
    {
        a = Math.Clamp(a, 0, 1);
        var z1 = 1 + Math.Cbrt(1 - a * a) * (Math.Cbrt(1 + a) + Math.Cbrt(1 - a));
        var z2 = Math.Sqrt(3 * a * a + z1 * z1);
        return 3 + z2 - Math.Sqrt((3 - z1) * (3 + z1 + 2 * z2));
    }

    /// <summary>Specific energy at the prograde Kerr ISCO, √(1 − 2/(3 r̃)) (Ori &amp; Thorne 2000 Eq. 2.7).</summary>
    public static double KerrIscoEnergy(double a) => Math.Sqrt(1 - 2 / (3 * KerrIscoRadius(a)));

    /// <summary>Specific angular momentum at the prograde Kerr ISCO, units of GM/c, (2/3√3)(1 + 2√(3 r̃ − 2)) (Eq. 2.8): 2√3 at a = 0.</summary>
    public static double KerrIscoAngularMomentum(double a) => 2 / (3 * Math.Sqrt(3)) * (1 + 2 * Math.Sqrt(3 * KerrIscoRadius(a) - 2));

    /// <summary>
    /// Dimensionless spin a/M of the remnant of two non-spinning holes (HLZ14 Eq. 16). Implicit through J̃_ISCO(α_rem), which
    /// only enters at δm⁶ with a factor η, so a fixed-point iteration converges in a few rounds.
    /// </summary>
    public static double RemnantSpin(double m1, double m2)
    {
        var eta = SymmetricMassRatio(m1, m2);
        var dm = (m1 - m2) / (m1 + m2);
        var dm2 = dm * dm;
        var f = 4 * eta;
        var fixedPart = f * f * (L0 + L2d * dm2 + L4f * dm2 * dm2);
        var tail = eta * dm2 * dm2 * dm2;
        var a = fixedPart + tail * KerrIscoAngularMomentum(0);
        for (var i = 0; i < 50; i++)
        {
            var next = Math.Clamp(fixedPart + tail * KerrIscoAngularMomentum(a), 0, 0.998);
            if (Math.Abs(next - a) < 1e-15) return next;
            a = next;
        }
        return a;
    }

    public static double SymmetricMassRatio(double m1, double m2) => m1 * m2 / ((m1 + m2) * (m1 + m2));

    /// <summary>M_rem / (m₁ + m₂) for non-spinning holes (HLZ14 Eq. 14), Ẽ_ISCO at the remnant's spin.</summary>
    public static double RemnantMassFraction(double m1, double m2) => RemnantMassFraction(m1, m2, KerrIscoEnergy(RemnantSpin(m1, m2)));

    /// <summary>Eq. (14) with a given ISCO energy: <see cref="IscoEnergy"/> gives the M5 (Schwarzschild) value.</summary>
    public static double RemnantMassFraction(double m1, double m2, double iscoEnergy)
    {
        var eta = SymmetricMassRatio(m1, m2);
        var dm = (m1 - m2) / (m1 + m2);
        var dm2 = dm * dm;
        var f = 4 * eta;
        return f * f * (M0 + K2d * dm2 + K4f * dm2 * dm2) + (1 + eta * (iscoEnergy + 11)) * dm2 * dm2 * dm2;
    }

    /// <summary>Recoil speed of the remnant, km/s (González et al. 2007).</summary>
    public static double RecoilSpeed(double eta)
    {
        eta = Math.Clamp(eta, 0, 0.25);
        return KickA * eta * eta * Math.Sqrt(1 - 4 * eta) * (1 + KickB * eta);
    }

    /// <summary>
    /// 2.5PN radiation-reaction acceleration of the relative motion of two holes, km/s²: a₂ − a₁ for separation
    /// d = x₂ − x₁ (km), relative velocity dv = v₂ − v₁ (km/s), <paramref name="r"/> = |d| (km, softened to contact by the
    /// caller) and masses kg. With n = d/r and ṙ = n·dv: (8/5) η (GM)²/(c⁵ r³) [ṙ (3v² + 17GM/3r) n − (v² + 3GM/r) dv], the
    /// sign such that it drains energy, (32/5) G⁴ μ² M³/(c⁵ r⁵) on a circular orbit. Body 1 gets −(m₂/M) of it, body 2 +(m₁/M).
    /// </summary>
    public static (double X, double Y, double Z) RadiationReaction(double dx, double dy, double dz, double dvx, double dvy, double dvz, double r, double m1, double m2)
    {
        var m = m1 + m2;
        var gm = Constants.G * m;
        var c = BlackHole.SpeedOfLight;
        var c2 = c * c;
        var k = 1.6 * (m1 * m2 / (m * m)) * gm * gm / (c2 * c2 * c * r * r * r);
        var v2 = dvx * dvx + dvy * dvy + dvz * dvz;
        var rdot = (dx * dvx + dy * dvy + dz * dvz) / r;
        var cn = k * rdot * (3 * v2 + 17.0 / 3 * gm / r) / r;
        var cv = -k * (v2 + 3 * gm / r);
        return (cn * dx + cv * dvx, cn * dy + cv * dvy, cn * dz + cv * dvz);
    }
}
