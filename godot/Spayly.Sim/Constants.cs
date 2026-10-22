namespace Spayly.Sim;

/// <summary>Physical constants. Simulation units are km, kg, s, rad; mirrors src/sim/constants.ts one for one.</summary>
public static class Constants
{
    /// <summary>Gravitational constant, km^3 kg^-1 s^-2.</summary>
    public const double G = 6.6743e-20;
    /// <summary>Astronomical unit, km.</summary>
    public const double AU = 149597870.7;
    /// <summary>Seconds.</summary>
    public const double Day = 86400;
    /// <summary>Seconds.</summary>
    public const double Hour = 3600;
    /// <summary>Julian year, seconds.</summary>
    public const double Year = 365.25 * Day;
    /// <summary>Julian date of J2000.0.</summary>
    public const double J2000Jd = 2451545.0;
    /// <summary>J2000 mean obliquity of the ecliptic, rad.</summary>
    public const double Obliquity = 23.4392911 * Math.PI / 180;
    /// <summary>kg.</summary>
    public const double SunMass = 1.98841e30;
    /// <summary>km.</summary>
    public const double SunRadius = 695700;
    /// <summary>W.</summary>
    public const double SunLuminosity = 3.828e26;
    /// <summary>K.</summary>
    public const double SunTemperature = 5772;
    /// <summary>J per megaton of TNT.</summary>
    public const double MtTnt = 4.184e15;
    /// <summary>J per kiloton of TNT.</summary>
    public const double KtTnt = 4.184e12;
    /// <summary>W m^-2 K^-4.</summary>
    public const double StefanBoltzmann = 5.670374419e-8;
    /// <summary>Radians per degree.</summary>
    public const double Deg = Math.PI / 180;
}
