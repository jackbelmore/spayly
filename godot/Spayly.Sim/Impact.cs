using System.Globalization;

namespace Spayly.Sim;

// Impact consequences after Collins, Melosh & Marcus (2005), "Earth Impact Effects Program",
// Meteoritics & Planetary Science 40, 817-840. SI units throughout (m, kg, s, J) — unlike the rest of Spayly.Sim,
// which is km. Collide converts at the boundary; every distance in an ImpactReport is metres.

/// <summary>The impactor, SI.</summary>
public readonly record struct ImpactorSpec
{
    /// <summary>m.</summary>
    public required double Diameter { get; init; }
    /// <summary>kg/m^3.</summary>
    public required double Density { get; init; }
    /// <summary>m/s at the top of the atmosphere.</summary>
    public required double Speed { get; init; }
    /// <summary>Radians from horizontal.</summary>
    public required double Angle { get; init; }
}

/// <summary>What the impactor lands in (TS <c>TargetSpec['surface']</c>). Not Body's <c>Surface</c>: this one has <c>Water</c> and no <c>Lava</c>.</summary>
public enum TargetSurface { Rock, Water, Ice, Gas, Star }

/// <summary>The target body at the impact point, SI.</summary>
public readonly record struct TargetSpec
{
    /// <summary>m.</summary>
    public required double Radius { get; init; }
    /// <summary>kg.</summary>
    public required double Mass { get; init; }
    /// <summary>m/s^2.</summary>
    public required double Gravity { get; init; }
    public required TargetSurface Surface { get; init; }
    /// <summary>kg/m^3.</summary>
    public required double SurfaceDensity { get; init; }
    /// <summary>m, for water targets.</summary>
    public double? WaterDepth { get; init; }
    public AtmosphereModel? Atmosphere { get; init; }
}

public readonly record struct EntryResult
{
    public required bool Breakup { get; init; }
    /// <summary>m.</summary>
    public required double BreakupAltitude { get; init; }
    public required bool Airburst { get; init; }
    /// <summary>m.</summary>
    public required double BurstAltitude { get; init; }
    /// <summary>m/s (0 if airburst).</summary>
    public required double SurfaceSpeed { get; init; }
    /// <summary>m/s at the airburst point.</summary>
    public required double BurstSpeed { get; init; }
}

public readonly record struct CraterResult
{
    /// <summary>m.</summary>
    public required double TransientDiameter { get; init; }
    /// <summary>m.</summary>
    public required double FinalDiameter { get; init; }
    /// <summary>m.</summary>
    public required double Depth { get; init; }
    public required bool Complex { get; init; }
}

/// <summary>TS <c>ImpactReport['thermal']</c>.</summary>
public readonly record struct ThermalResult
{
    /// <summary>m.</summary>
    public required double BurnRadius { get; init; }
    /// <summary>m.</summary>
    public required double IgnitionRadius { get; init; }
    public required bool HorizonLimited { get; init; }
}

/// <summary>TS <c>ImpactReport['airblast']</c>.</summary>
public readonly record struct AirblastResult
{
    /// <summary>m, 1 kPa peak overpressure.</summary>
    public required double GlassRadius { get; init; }
    /// <summary>m, 28 kPa peak overpressure.</summary>
    public required double CollapseRadius { get; init; }
}

/// <summary>TS <c>ImpactReport['tsunami']</c>.</summary>
public readonly record struct TsunamiResult
{
    /// <summary>m, wave amplitude 100 km out.</summary>
    public required double Amplitude100km { get; init; }
    /// <summary>m, wave amplitude 1000 km out.</summary>
    public required double Amplitude1000km { get; init; }
}

public readonly record struct ImpactReport
{
    /// <summary>kg.</summary>
    public required double Mass { get; init; }
    /// <summary>Kinetic energy at entry, J.</summary>
    public required double Energy { get; init; }
    /// <summary>Kinetic energy at entry, megatons of TNT.</summary>
    public required double EnergyMT { get; init; }
    /// <summary>Energy at the ground or burst point, J.</summary>
    public required double EnergyDeposited { get; init; }
    public required EntryResult Entry { get; init; }
    public CraterResult? Crater { get; init; }
    /// <summary>m.</summary>
    public required double FireballRadius { get; init; }
    public required ThermalResult Thermal { get; init; }
    /// <summary>Richter-equivalent; −∞ when there is no crater.</summary>
    public required double SeismicMagnitude { get; init; }
    /// <summary>m, where effective magnitude >= 6.</summary>
    public required double SeismicDamageRadius { get; init; }
    /// <summary>m.</summary>
    public required double EjectaOneMetreRadius { get; init; }
    public required AirblastResult Airblast { get; init; }
    public TsunamiResult? Tsunami { get; init; }
    /// <summary>Deposited energy / gravitational binding energy of the target.</summary>
    public required double BindingRatio { get; init; }
    /// <summary>The crater scaling no longer applies: the event reshapes the whole body.</summary>
    public required bool PlanetScale { get; init; }
    public required string Classification { get; init; }
    public required string[] Comparisons { get; init; }
}

public static class Impact
{
    private const double Cd = 2;
    private const double Fp = 7;
    private const double LuminousEfficiency = 3e-3;

    /// <summary>Impactor yield strength, Pa, from its density rho (kg/m^3).</summary>
    public static double YieldStrength(double rho) => Math.Pow(10, 2.107 + 0.0624 * Math.Sqrt(rho));

    /// <summary>Pancake-model atmospheric entry.</summary>
    public static EntryResult AtmosphericEntry(ImpactorSpec imp, TargetSpec tgt)
    {
        var none = new EntryResult { Breakup = false, BreakupAltitude = 0, Airburst = false, BurstAltitude = 0, SurfaceSpeed = imp.Speed, BurstSpeed = 0 };
        if (tgt.Atmosphere is not { } atm) return none;
        var rho0 = atm.Rho0;
        var H = atm.H;
        var L0 = imp.Diameter;
        var rhoI = imp.Density;
        var v0 = imp.Speed;
        var sinT = Math.Max(Math.Sin(imp.Angle), 0.05);
        var Yi = YieldStrength(rhoI);
        var If = (4.07 * Cd * H * Yi) / (rhoI * L0 * v0 * v0 * sinT);
        var intactSurfaceSpeed = v0 * Math.Exp((-3 * rho0 * Cd * H) / (4 * rhoI * L0 * sinT));
        if (If >= 1) return none with { SurfaceSpeed = intactSurfaceSpeed };
        var zStar = -H * (Math.Log(Yi / (rho0 * v0 * v0)) + 1.308 - 0.314 * If - 1.303 * Math.Sqrt(1 - If));
        if (!(zStar > 0)) return none with { SurfaceSpeed = intactSurfaceSpeed };
        var rhoZ = rho0 * Math.Exp(-zStar / H);
        var vZ = v0 * Math.Exp((-3 * rhoZ * Cd * H) / (4 * rhoI * L0 * sinT));
        var l = L0 * sinT * Math.Sqrt(rhoI / (Cd * rhoZ));
        var zb = zStar - 2 * H * Math.Log(1 + (l / (2 * H)) * Math.Sqrt(Fp * Fp - 1));
        var zEnd = Math.Max(zb, 0);
        // Integrate dv/dz for the spreading fragment cloud from z* down to the burst altitude or the ground.
        const int N = 240;
        var dz = (zStar - zEnd) / N;
        double integral = 0;
        for (var k = 0; k < N; k++)
        {
            var z = zStar - (k + 0.5) * dz;
            var spread = (2 * H) / l;
            var L = L0 * Math.Sqrt(1 + spread * spread * Math.Pow(Math.Exp((zStar - z) / (2 * H)) - 1, 2));
            var rho = rho0 * Math.Exp(-z / H);
            integral += ((3 * Cd * rho * L * L) / (4 * rhoI * L0 * L0 * L0 * sinT)) * dz;
        }
        var vEnd = vZ * Math.Exp(-integral);
        if (zb > 0) return new EntryResult { Breakup = true, BreakupAltitude = zStar, Airburst = true, BurstAltitude = zb, SurfaceSpeed = 0, BurstSpeed = vEnd };
        return new EntryResult { Breakup = true, BreakupAltitude = zStar, Airburst = false, BurstAltitude = 0, SurfaceSpeed = vEnd, BurstSpeed = 0 };
    }

    /// <summary>Pi-group crater scaling. Inputs in SI: diameter m, rhoI kg/m^3, speed m/s, angle rad from horizontal.</summary>
    public static CraterResult CraterDimensions(double diameter, double rhoI, double speed, double angle, TargetSpec tgt)
    {
        var k = tgt.Surface == TargetSurface.Water ? 1.365 : 1.161;
        var rhoT = tgt.SurfaceDensity;
        var g = tgt.Gravity;
        var Dtc = k * Math.Cbrt(rhoI / rhoT) * Math.Pow(diameter, 0.78) * Math.Pow(speed, 0.44) * Math.Pow(g, -0.22) * Math.Cbrt(Math.Max(Math.Sin(angle), 0.05));
        var Dc = 3200 * (9.81 / g); // simple-to-complex transition scales inversely with gravity
        if (Dtc < Dc)
        {
            var Dfr = 1.25 * Dtc;
            return new CraterResult { TransientDiameter = Dtc, FinalDiameter = Dfr, Depth = 0.2 * Dfr, Complex = false };
        }
        var DfrComplex = (1.17 * Math.Pow(Dtc, 1.13)) / Math.Pow(Dc, 0.13);
        return new CraterResult { TransientDiameter = Dtc, FinalDiameter = DfrComplex, Depth = 400 * Math.Pow(DfrComplex / 1000, 0.3), Complex = true };
    }

    /// <summary>
    /// Peak overpressure (Pa) at ground distance r (m) from a burst of energy E (J).
    /// Surface bursts use the scaled-distance fit; airbursts use the regular-reflection form
    /// with the burst altitude (m) folded into the scaled slant range.
    /// </summary>
    public static double Overpressure(double r, double E, double burstAltitude = 0)
    {
        var Ekt = Math.Max(E / Constants.KtTnt, 1e-9);
        var s = Math.Cbrt(Ekt);
        var r1 = r / s;
        if (burstAltitude > 0)
        {
            var zb1 = burstAltitude / s;
            var d2 = r1 * r1 + zb1 * zb1;
            return 3.14e11 * Math.Pow(d2, -1.3) + 1.8e7 * Math.Pow(d2, -0.565);
        }
        const double rx = 290;
        return 75000 * (rx / (4 * r1)) * (1 + 3 * Math.Pow(rx / r1, 1.3));
    }

    private static double RadiusForOverpressure(double target, double E, double burstAltitude)
    {
        double lo = 1, hi = 1e8;
        if (Overpressure(lo, E, burstAltitude) < target) return 0;
        for (var i = 0; i < 60; i++)
        {
            var mid = Math.Sqrt(lo * hi);
            if (Overpressure(mid, E, burstAltitude) > target) lo = mid;
            else hi = mid;
        }
        return lo;
    }

    /// <summary>Effective seismic magnitude at distance r (m) for source magnitude M.</summary>
    public static double EffectiveMagnitude(double M, double r)
    {
        var rkm = r / 1000;
        if (rkm < 60) return M - 0.0238 * rkm;
        if (rkm < 700) return M - 0.0048 * rkm - 1.1644;
        return M - 1.66 * Math.Log10(rkm) - 6.399;
    }

    private static double SeismicRadius(double M, double threshold)
    {
        if (M < threshold) return 0;
        double lo = 1, hi = 2e7;
        for (var i = 0; i < 60; i++)
        {
            var mid = Math.Sqrt(lo * hi);
            if (EffectiveMagnitude(M, mid) > threshold) lo = mid;
            else hi = mid;
        }
        return lo;
    }

    /// <summary>One-sentence verdict from entry energy (J) and binding ratio. Tests match substrings; keep the strings exact.</summary>
    public static string Classify(double energy, double bindingRatio, EntryResult entry, TargetSpec tgt)
    {
        if (tgt.Surface == TargetSurface.Star) return "Swallowed by the star. A brief brightening, nothing more.";
        if (bindingRatio >= 1) return "Catastrophic disruption. The body is torn apart and its pieces scatter.";
        if (bindingRatio >= 0.2) return "Shattering impact. The body survives as a molten, reassembling ball of debris.";
        if (bindingRatio >= 0.01) return "Global melting. The surface becomes a magma ocean.";
        if (tgt.Surface == TargetSurface.Gas)
        {
            if (energy > 1e24) return "Titanic plume. A dark scar the size of Earth spreads through the cloud bands.";
            return "Plume and fireball in the upper atmosphere, leaving a dark scar like Shoemaker-Levy 9.";
        }
        if (energy > 4e27) return "Sterilising impact. The oceans boil away and the crust glows.";
        if (energy > 1e23) return "Mass extinction event. Global firestorm, impact winter, ecosystems collapse.";
        if (energy > 1e21) return "Global catastrophe. Years of impact winter and crop failure worldwide.";
        if (energy > 1e18) return "Continental devastation. A crater visible from space and a regional dust cloud.";
        if (energy > 1e16) return entry.Airburst ? "City-killer airburst. Buildings flattened over a wide area." : "Regional devastation with a large crater.";
        if (energy > 1e14) return entry.Airburst ? "Powerful airburst. Windows shattered across a city." : "Local devastation with a crater.";
        if (energy > 1e12) return entry.Airburst ? "Bright fireball and sonic boom. Meteorites reach the ground." : "Small crater; local damage.";
        return "Fireball. A spectacular meteor with little damage.";
    }

    /// <summary>Human-scale comparisons for an energy in J.</summary>
    public static string[] Comparisons(double energy)
    {
        var output = new List<string>();
        const double hiroshima = 6.3e13;
        const double tsar = 2.1e17;
        if (energy < hiroshima * 0.5) output.Add($"{Fmt(energy / 4.184e9)} tons of TNT");
        else if (energy < tsar * 2) output.Add($"{Fmt(energy / hiroshima)} Hiroshima bombs");
        else output.Add($"{Fmt(energy / tsar)} Tsar Bombas");
        if (energy > 2e15) output.Add($"{Fmt(energy / 2.1e15)} Chelyabinsk events");
        if (energy > 1e21) output.Add($"{Fmt(energy / 4e23)} Chicxulub impacts");
        return output.ToArray();
    }

    // JS toExponential/toFixed round the exact binary value, as .NET Core 3+ formatting does. JS Math.round is half
    // toward +inf, hence Floor(x + 0.5) rather than Math.Round (banker's). toLocaleString() follows the viewer's locale;
    // Sim has none, so this pins the en-US grouping ("1,234") that Node prints by default.
    private static string Fmt(double x)
    {
        var inv = CultureInfo.InvariantCulture;
        if (x >= 1e6) return x.ToString("0.0e0", inv);
        if (x >= 100) return Math.Floor(x + 0.5).ToString("#,##0", inv);
        if (x >= 10) return x.ToString("F1", inv);
        return x.ToString("F2", inv);
    }

    /// <summary>Everything the impact does, from an impactor and a target in SI. Every distance in the report is metres.</summary>
    public static ImpactReport ImpactReport(ImpactorSpec imp, TargetSpec tgt)
    {
        var mass = imp.Density * (Math.PI / 6) * Math.Pow(imp.Diameter, 3);
        var energy = 0.5 * mass * imp.Speed * imp.Speed;
        var entry = tgt.Surface == TargetSurface.Gas || tgt.Surface == TargetSurface.Star
            ? new EntryResult { Breakup = true, BreakupAltitude = 0, Airburst = true, BurstAltitude = 0, SurfaceSpeed = 0, BurstSpeed = imp.Speed }
            : AtmosphericEntry(imp, tgt);
        var speedAtEnd = entry.Airburst ? entry.BurstSpeed : entry.SurfaceSpeed;
        var energyDeposited = 0.5 * mass * speedAtEnd * speedAtEnd;
        // SI G as a literal, exactly as the TS writes it: Constants.G is km^3 kg^-1 s^-2, and G * 1e9 is not bit-identical.
        var bindingEnergy = (3 * 6.6743e-11 * tgt.Mass * tgt.Mass) / (5 * tgt.Radius);
        var bindingRatio = energy / bindingEnergy;

        CraterResult? crater = null;
        if (!entry.Airburst && tgt.Surface != TargetSurface.Gas && tgt.Surface != TargetSurface.Star)
        {
            crater = CraterDimensions(imp.Diameter, imp.Density, entry.SurfaceSpeed, imp.Angle, tgt);
        }

        var fireballRadius = 0.002 * Math.Cbrt(energyDeposited);
        var Emt = energyDeposited / Constants.MtTnt;
        var scaling = Math.Pow(Math.Max(Emt, 1e-12), 1.0 / 6);
        var burnFlux = 0.42e6 * scaling;
        var igniteFlux = 1.0e6 * scaling;
        double ThermalRadius(double flux) => Math.Sqrt((LuminousEfficiency * energyDeposited) / (2 * Math.PI * flux));
        var horizon = Math.Sqrt(Math.Max(0, Math.Pow(tgt.Radius + fireballRadius + entry.BurstAltitude, 2) - Math.Pow(tgt.Radius, 2)));
        var burnRaw = ThermalRadius(burnFlux);
        var thermal = new ThermalResult
        {
            BurnRadius = Math.Min(burnRaw, horizon),
            IgnitionRadius = Math.Min(ThermalRadius(igniteFlux), horizon),
            HorizonLimited = burnRaw > horizon,
        };

        var half = Math.PI * tgt.Radius; // antipode distance: nothing on the surface is farther away
        var seismicMagnitude = crater is not null ? 0.67 * Math.Log10(Math.Max(energyDeposited, 1)) - 5.87 : double.NegativeInfinity;
        var seismicDamageRadius = crater is not null ? Math.Min(SeismicRadius(seismicMagnitude, 6), half) : 0;
        var ejectaOneMetreRadius = crater is { } c ? Math.Min(Math.Cbrt(Math.Pow(c.TransientDiameter, 4) / 112), half) : 0;
        var airblast = tgt.Atmosphere is not null
            ? new AirblastResult { GlassRadius = Math.Min(RadiusForOverpressure(1000, energyDeposited, entry.BurstAltitude), half), CollapseRadius = Math.Min(RadiusForOverpressure(28000, energyDeposited, entry.BurstAltitude), half) }
            : new AirblastResult { GlassRadius = 0, CollapseRadius = 0 };

        TsunamiResult? tsunami = null;
        if (crater is { } cw && tgt.Surface == TargetSurface.Water)
        {
            var depth = tgt.WaterDepth ?? 3700;
            var dtc = cw.TransientDiameter / 2.828;
            double Amp(double r) => Math.Min(dtc, depth) * (cw.TransientDiameter / (2 * r));
            tsunami = new TsunamiResult { Amplitude100km = Amp(1e5), Amplitude1000km = Amp(1e6) };
        }

        var planetScale = (crater is { } cp ? cp.FinalDiameter > 0.6 * tgt.Radius : false) || fireballRadius > tgt.Radius;
        return new ImpactReport
        {
            Mass = mass, Energy = energy, EnergyMT = energy / Constants.MtTnt, EnergyDeposited = energyDeposited, Entry = entry, Crater = crater,
            FireballRadius = fireballRadius, Thermal = thermal, SeismicMagnitude = seismicMagnitude, SeismicDamageRadius = seismicDamageRadius,
            EjectaOneMetreRadius = ejectaOneMetreRadius, Airblast = airblast, Tsunami = tsunami, BindingRatio = bindingRatio, PlanetScale = planetScale,
            Classification = Classify(energy, bindingRatio, entry, tgt),
            Comparisons = Comparisons(energy),
        };
    }
}
