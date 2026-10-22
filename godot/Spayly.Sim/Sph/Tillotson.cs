namespace Spayly.Sim.Sph;

/// <summary>
/// One material's Tillotson (1962) equation-of-state parameters plus the display constants the game uses to turn specific
/// internal energy into a colour. SI throughout: kg/m³, Pa, J/kg, K. The SPH module is SI end to end, like
/// <see cref="Impact"/>; <see cref="HeroCollision"/> converts at the km boundary.
/// </summary>
public sealed record Material
{
    public required string Name { get; init; }
    /// <summary>Reference (zero-pressure) density ρ₀, kg/m³.</summary>
    public required double Rho0 { get; init; }
    /// <summary>Tillotson A (bulk modulus term), Pa.</summary>
    public required double A { get; init; }
    /// <summary>Tillotson B (non-linear compression term), Pa.</summary>
    public required double B { get; init; }
    /// <summary>Tillotson E₀ (often u₀), J/kg.</summary>
    public required double E0 { get; init; }
    /// <summary>Specific energy of incipient vaporisation E_iv, J/kg.</summary>
    public required double Eiv { get; init; }
    /// <summary>Specific energy of complete vaporisation E_cv, J/kg.</summary>
    public required double Ecv { get; init; }
    /// <summary>Tillotson a (dimensionless).</summary>
    public required double SmallA { get; init; }
    /// <summary>Tillotson b (dimensionless).</summary>
    public required double SmallB { get; init; }
    /// <summary>Tillotson α (dimensionless), the exponent on (ρ₀/ρ − 1)² in the expanded branch.</summary>
    public required double Alpha { get; init; }
    /// <summary>Tillotson β (dimensionless), the exponent on (ρ₀/ρ − 1) in the expanded branch.</summary>
    public required double Beta { get; init; }
    /// <summary>[GAME] specific heat for the display temperature, J/(kg K).</summary>
    public required double HeatCapacity { get; init; }
    /// <summary>[GAME] display temperature at zero thermal energy, K.</summary>
    public required double ReferenceTemperature { get; init; }
    /// <summary>[GAME] melting temperature of the display plateau, K.</summary>
    public required double MeltTemperature { get; init; }
    /// <summary>[GAME] latent heat of the melting plateau, J/kg.</summary>
    public required double MeltLatent { get; init; }
    /// <summary>[GAME] cold base colour, linear RGB 0..1.</summary>
    public required (double R, double G, double B) Color { get; init; }
}

/// <summary>
/// Parameter sets. Sources are the tables the planetary-impact literature quotes: Tillotson (1962) via Melosh (1989,
/// Impact Cratering, table AII.3) and Benz &amp; Asphaug (1999, Icarus 142:5, table II). Display constants are [GAME]
/// handbook-order values, not physics.
/// </summary>
public static class Materials
{
    /// <summary>Granite (Melosh 1989 table AII.3; used for rocky mantles, as in Benz &amp; Asphaug 1999 and SWIFT's planetary tests).</summary>
    public static readonly Material Granite = new()
    {
        Name = "granite", Rho0 = 2680, A = 18.0e9, B = 18.0e9, E0 = 16.0e6, Eiv = 3.5e6, Ecv = 18.0e6,
        SmallA = 0.5, SmallB = 1.3, Alpha = 5, Beta = 5,
        HeatCapacity = 790, ReferenceTemperature = 300, MeltTemperature = 1500, MeltLatent = 4.0e5, Color = (0.42, 0.37, 0.33),
    };

    /// <summary>Basalt (Benz &amp; Asphaug 1999 table II). Note E₀ = 487 MJ/kg, which is the published value, not a typo.</summary>
    public static readonly Material Basalt = new()
    {
        Name = "basalt", Rho0 = 2700, A = 26.7e9, B = 26.7e9, E0 = 487e6, Eiv = 4.72e6, Ecv = 18.2e6,
        SmallA = 0.5, SmallB = 1.5, Alpha = 5, Beta = 5,
        HeatCapacity = 840, ReferenceTemperature = 300, MeltTemperature = 1400, MeltLatent = 4.0e5, Color = (0.22, 0.21, 0.20),
    };

    /// <summary>Iron (Tillotson 1962 via Melosh 1989 table AII.3; ρ₀ = 7800 as in most giant-impact SPH work, Melosh lists 7860).</summary>
    public static readonly Material Iron = new()
    {
        Name = "iron", Rho0 = 7800, A = 128e9, B = 105e9, E0 = 9.5e6, Eiv = 2.4e6, Ecv = 8.67e6,
        SmallA = 0.5, SmallB = 1.5, Alpha = 5, Beta = 5,
        HeatCapacity = 450, ReferenceTemperature = 300, MeltTemperature = 1811, MeltLatent = 2.47e5, Color = (0.50, 0.48, 0.46),
    };

    /// <summary>Water ice (Benz &amp; Asphaug 1999 table II).</summary>
    public static readonly Material Ice = new()
    {
        Name = "ice", Rho0 = 917, A = 9.47e9, B = 9.47e9, E0 = 10e6, Eiv = 0.773e6, Ecv = 3.04e6,
        SmallA = 0.3, SmallB = 0.1, Alpha = 10, Beta = 5,
        HeatCapacity = 2100, ReferenceTemperature = 200, MeltTemperature = 273, MeltLatent = 3.34e5, Color = (0.75, 0.84, 0.95),
    };

    /// <summary>
    /// Aluminium (Tillotson 1962; the set Schäfer et al. 2016 use for miluphcuda's tests). ρ₀ = 2700 kg/m³, which the
    /// research report's quotation of this set omits.
    /// </summary>
    public static readonly Material Aluminium = new()
    {
        Name = "aluminium", Rho0 = 2700, A = 75.2e9, B = 65e9, E0 = 5e6, Eiv = 3e6, Ecv = 13.9e6,
        SmallA = 0.5, SmallB = 1.63, Alpha = 5, Beta = 5,
        HeatCapacity = 900, ReferenceTemperature = 300, MeltTemperature = 933, MeltLatent = 3.97e5, Color = (0.78, 0.78, 0.80),
    };

    public static readonly IReadOnlyList<Material> All = [Granite, Basalt, Iron, Ice, Aluminium];
}

/// <summary>
/// The Tillotson (1962) equation of state as written in Melosh (1989) appendix II and Benz &amp; Asphaug (1999):
/// η = ρ/ρ₀, μ = η − 1, ω = u/(E₀η²) + 1.
///   compressed or cold (ρ ≥ ρ₀, or u ≤ E_iv):  P_c = (a + b/ω) ρu + Aμ + Bμ²
///   expanded and hot   (ρ &lt; ρ₀ and u ≥ E_cv): P_e = aρu + [bρu/ω + Aμ e^{−β(ρ₀/ρ−1)}] e^{−α(ρ₀/ρ−1)²}
///   in between: linear in u, P = [(u − E_iv)P_e + (E_cv − u)P_c]/(E_cv − E_iv).
/// Pressures Pa, energies J/kg, densities kg/m³. The analytic partial derivatives give the sound speed
/// c² = ∂P/∂ρ|_u + (P/ρ²) ∂P/∂u|_ρ, the adiabatic derivative for an EOS in (ρ, u).
/// </summary>
public static class Tillotson
{
    /// <summary>Pressure, Pa, unclamped (can be negative in tension). ρ kg/m³ (&gt; 0), u J/kg.</summary>
    public static double Pressure(Material m, double rho, double u)
    {
        Evaluate(m, rho, u, out var p, out _, out _);
        return p;
    }

    /// <summary>Pressure (Pa) and its partials ∂P/∂ρ at fixed u (m²/s²) and ∂P/∂u at fixed ρ (kg/m³). Unclamped.</summary>
    public static void Evaluate(Material m, double rho, double u, out double p, out double dPdRho, out double dPdU)
    {
        if (rho >= m.Rho0 || u <= m.Eiv)
        {
            Compressed(m, rho, u, out p, out dPdRho, out dPdU);
            return;
        }
        if (u >= m.Ecv)
        {
            Expanded(m, rho, u, out p, out dPdRho, out dPdU);
            return;
        }
        Compressed(m, rho, u, out var pc, out var dcr, out var dcu);
        Expanded(m, rho, u, out var pe, out var der, out var deu);
        var span = m.Ecv - m.Eiv;
        var we = (u - m.Eiv) / span;
        var wc = (m.Ecv - u) / span;
        p = we * pe + wc * pc;
        dPdRho = we * der + wc * dcr;
        dPdU = (pe - pc) / span + we * deu + wc * dcu;
    }

    private static void Compressed(Material m, double rho, double u, out double p, out double dPdRho, out double dPdU)
    {
        // The floor only matters below 10⁻⁶ρ₀, where it keeps E₀η² from underflowing in the GPU's float32 copy.
        var eta = Math.Max(rho / m.Rho0, 1e-6);
        var mu = eta - 1;
        var e0eta2 = m.E0 * eta * eta;
        var omega = u / e0eta2 + 1;
        var bw = m.SmallB / omega;
        // ρu × ∂(b/ω)/∂ρ = 2bu²/(ω²E₀η²); ∂(b/ω)/∂u = −b/(ω²E₀η²)
        var bw2 = m.SmallB / (omega * omega * e0eta2);
        p = (m.SmallA + bw) * rho * u + m.A * mu + m.B * mu * mu;
        dPdRho = (m.SmallA + bw) * u + 2 * bw2 * u * u + (m.A + 2 * m.B * mu) / m.Rho0;
        dPdU = (m.SmallA + bw) * rho - bw2 * rho * u;
    }

    private static void Expanded(Material m, double rho, double u, out double p, out double dPdRho, out double dPdU)
    {
        var z = m.Rho0 / rho - 1;
        // Past ρ₀/9, e^{−α(ρ₀/ρ−1)²} ≤ e^{−320}: only the aρu term survives, and skipping the rest keeps ρ₀/ρ² from
        // overflowing float32 on the GPU. Exact to double precision.
        if (z > 8)
        {
            p = m.SmallA * rho * u;
            dPdRho = m.SmallA * u;
            dPdU = m.SmallA * rho;
            return;
        }
        var eta = rho / m.Rho0;
        var mu = eta - 1;
        var e0eta2 = m.E0 * eta * eta;
        var omega = u / e0eta2 + 1;
        var bw = m.SmallB / omega;
        var bw2 = m.SmallB / (omega * omega * e0eta2);
        var dzdrho = -m.Rho0 / (rho * rho);
        var e1 = Math.Exp(-m.Beta * z);
        var e2 = Math.Exp(-m.Alpha * z * z);
        var t = bw * rho * u + m.A * mu * e1;
        var dtdrho = bw * u + 2 * bw2 * u * u + m.A * e1 / m.Rho0 - m.A * mu * e1 * m.Beta * dzdrho;
        var dtdu = bw * rho - bw2 * rho * u;
        p = m.SmallA * rho * u + t * e2;
        dPdRho = m.SmallA * u + e2 * (dtdrho - t * 2 * m.Alpha * z * dzdrho);
        dPdU = m.SmallA * rho + e2 * dtdu;
    }

    /// <summary>
    /// Squared sound speed, m²/s²: ∂P/∂ρ + (P/ρ²)∂P/∂u with <paramref name="pClamped"/> the pressure actually used by the
    /// solver (after its floor), and never below 1e-4 A/ρ₀ (1 % of the bulk sound speed), because the expanded branch's
    /// derivative can go negative and a timestep needs a positive signal speed.
    /// </summary>
    public static double SoundSpeed2(Material m, double rho, double u, double pClamped, double dPdRho, double dPdU)
    {
        var c2 = dPdRho + pClamped / (rho * rho) * dPdU;
        var floor = 1e-4 * m.A / m.Rho0;
        return c2 > floor ? c2 : floor;
    }

    /// <summary>Pressure clamped to <paramref name="pMin"/> (Pa) and sound speed (m/s) in one call; what the solver uses.</summary>
    public static (double P, double C) PressureAndSoundSpeed(Material m, double rho, double u, double pMin)
    {
        Evaluate(m, rho, u, out var p, out var dr, out var du);
        if (p < pMin) p = pMin;
        return (p, Math.Sqrt(SoundSpeed2(m, rho, u, p, dr, du)));
    }

    /// <summary>
    /// Specific energy stored by cold compression, J/kg: ∫ P_cold/ρ² dρ from ρ₀ with P_cold = Aμ + Bμ², i.e.
    /// [A(ln(1+μ) + 1/(1+μ) − 1) + B(μ − 2ln(1+μ) − 1/(1+μ) + 1)]/ρ₀ for μ &gt; 0, else 0. The display subtracts it so a
    /// compressed planetary core does not read as hot.
    /// </summary>
    public static double ColdEnergy(Material m, double rho)
    {
        var mu = rho / m.Rho0 - 1;
        if (mu <= 0) return 0;
        var l = Math.Log(1 + mu);
        var inv = 1 / (1 + mu);
        return (m.A * (l + inv - 1) + m.B * (mu - 2 * l - inv + 1)) / m.Rho0;
    }

    /// <summary>
    /// [GAME] display temperature (K) from thermal specific energy (J/kg, u minus <see cref="ColdEnergy"/>): linear in
    /// HeatCapacity from ReferenceTemperature, a melting plateau of MeltLatent, linear again up to incipient vaporisation at
    /// u = E_iv, a vaporisation plateau from E_iv to E_cv (the Tillotson two-phase range), then linear. Not a physical
    /// temperature: Tillotson has none.
    /// </summary>
    public static double DisplayTemperature(Material m, double uThermal)
    {
        var cv = m.HeatCapacity;
        var u = uThermal > 0 ? uThermal : 0;
        var meltStart = cv * (m.MeltTemperature - m.ReferenceTemperature);
        if (u < meltStart) return m.ReferenceTemperature + u / cv;
        var meltEnd = meltStart + m.MeltLatent;
        if (u < meltEnd) return m.MeltTemperature;
        var vapStart = Math.Max(m.Eiv, meltEnd);
        var vapT = m.MeltTemperature + (vapStart - meltEnd) / cv;
        if (u < vapStart) return m.MeltTemperature + (u - meltEnd) / cv;
        var vapEnd = Math.Max(m.Ecv, vapStart);
        if (u < vapEnd) return vapT;
        return vapT + (u - vapEnd) / cv;
    }
}
