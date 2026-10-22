namespace Spayly.Sim;

/// <summary>
/// What a black hole's disc radiates. Mass handed to a hole (a swallowed body, an absorbed debris particle) does not light
/// the disc at once: it is spread over the disc's viscous time, and the accretion rate Ṁ that comes out of it, plus any
/// tidal-disruption fallback, radiates L = ε Ṁ c² with the Schwarzschild efficiency ε = 1 − √(8/9) ≈ 0.057, capped at the
/// Eddington luminosity. An isolated hole with nothing falling in is dark. SI in, W out; kg, s.
///
/// [GAME] The viscous time is the α-disc estimate t = (1/α)(r/H)² / Ω at 10 r_s with α = 0.1 and H/r = 0.1, using the
/// Paczyński–Wiita Ω: 4.0 s for a 10 M☉ hole, growing linearly with mass (4.6 days at 10⁶ M☉). One exponential
/// reservoir stands in for the disc's real spreading. The hole's mass already includes the deposit: the reservoir only
/// schedules the light.
/// </summary>
public static class Accretion
{
    /// <summary>Radiative efficiency of a thin disc ending at the Schwarzschild ISCO: the binding energy there, 1 − √(8/9).</summary>
    public static readonly double Efficiency = 1 - Math.Sqrt(8.0 / 9);
    /// <summary>Shakura–Sunyaev viscosity parameter [GAME].</summary>
    public const double Alpha = 0.1;
    /// <summary>Disc aspect ratio H/r [GAME].</summary>
    public const double AspectRatio = 0.1;
    /// <summary>Radius the viscous time is taken at, r_s [GAME].</summary>
    public const double ViscousRadiusRs = 10;

    const double CSi = BlackHole.SpeedOfLight * 1000;

    /// <summary>Viscous time of a hole's disc, s: (1/α)(r/H)²/Ω at <see cref="ViscousRadiusRs"/> r_s, Ω = v_PW/r.</summary>
    public static double ViscousTime(double massKg)
    {
        var rs = BlackHole.SchwarzschildRadiusKm(massKg);
        var r = ViscousRadiusRs * rs;
        var omega = PseudoNewtonian.CircularSpeed(Constants.G * massKg, r, rs) / r;
        return 1 / (Alpha * AspectRatio * AspectRatio * omega);
    }

    /// <summary>L = ε Ṁ c², W, capped at the hole's Eddington luminosity. <paramref name="mdot"/> kg/s.</summary>
    public static double Luminosity(double mdot, double massKg) => Math.Min(Efficiency * Math.Max(mdot, 0) * CSi * CSi, BlackHole.EddingtonLuminosity(massKg));

    /// <summary>The Ṁ that radiates the Eddington luminosity at <see cref="Efficiency"/>, kg/s.</summary>
    public static double EddingtonRate(double massKg) => BlackHole.EddingtonLuminosity(massKg) / (Efficiency * CSi * CSi);
}

/// <summary>
/// One hole's disc reservoir: deposits in, Ṁ = reservoir / τ out, integrated exactly (the reservoir decays as e^(−t/τ)), so
/// deposited = accreted + reservoir holds to rounding. Sim time only runs forward here: rewinding does not un-radiate.
/// </summary>
public sealed class AccretionFeed(double viscousTime)
{
    /// <summary>s.</summary>
    public double ViscousTime { get; set; } = viscousTime;
    /// <summary>Mass waiting in the disc, kg.</summary>
    public double Reservoir { get; private set; }
    /// <summary>Everything ever deposited, kg.</summary>
    public double Deposited { get; private set; }
    /// <summary>Everything that has left the reservoir through the inner edge, kg.</summary>
    public double Accreted { get; private set; }

    /// <summary>Accretion rate out of the reservoir now, kg/s.</summary>
    public double Mdot => Reservoir / ViscousTime;

    /// <summary>An independent copy, for undo.</summary>
    public AccretionFeed Clone() => new(ViscousTime) { Reservoir = Reservoir, Deposited = Deposited, Accreted = Accreted };

    public void Deposit(double kg)
    {
        if (!(kg > 0)) return;
        Reservoir += kg;
        Deposited += kg;
    }

    /// <summary>Advance by <paramref name="dt"/> s of sim time (≤ 0 does nothing).</summary>
    public void Advance(double dt)
    {
        if (!(dt > 0) || Reservoir == 0) return;
        var left = Reservoir * Math.Exp(-dt / ViscousTime);
        Accreted += Reservoir - left;
        Reservoir = left;
    }
}
