namespace Spayly.Sim;

/// <summary>
/// Keplerian mean elements and rates, valid 1800-2050 (E. M. Standish, JPL).
/// Each field is (value at J2000, rate per century); a in AU, angles in degrees.
/// L = mean longitude, Lp = longitude of perihelion, Node = longitude of ascending node.
/// </summary>
public readonly record struct MeanElements(
    (double Value, double Rate) A,
    (double Value, double Rate) E,
    (double Value, double Rate) I,
    (double Value, double Rate) L,
    (double Value, double Rate) Lp,
    (double Value, double Rate) Node);

/// <summary>Dates and mean orbital elements; mirrors src/sim/ephemeris.ts.</summary>
public static class Ephemeris
{
    /// <summary>
    /// Julian day of a date. A JS Date holds whole milliseconds, so sub-millisecond ticks are dropped (towards zero, as
    /// the Date constructor's TimeClip does) before the same arithmetic. Local times are converted to UTC; Unspecified
    /// is taken as UTC.
    /// </summary>
    public static double JulianDay(DateTime utc)
    {
        var t = utc.Kind == DateTimeKind.Local ? utc.ToUniversalTime() : utc;
        double ms = (t.Ticks - DateTime.UnixEpoch.Ticks) / TimeSpan.TicksPerMillisecond;
        return ms / 86400000 + 2440587.5;
    }

    /// <summary>Julian centuries past J2000.0.</summary>
    public static double CenturiesSinceJ2000(double jd) => (jd - Constants.J2000Jd) / 36525;

    /// <summary>The UTC date of a Julian day, truncated to whole milliseconds like <c>new Date(ms)</c>.</summary>
    public static DateTime DateFromJd(double jd)
    {
        var ms = Math.Truncate((jd - 2440587.5) * 86400000);
        return new DateTime(DateTime.UnixEpoch.Ticks + (long)ms * TimeSpan.TicksPerMillisecond, DateTimeKind.Utc);
    }

    /// <summary>Standish mean elements of the eight planets (Earth = Earth-Moon barycentre), in TS order.</summary>
    public static readonly IReadOnlyDictionary<string, MeanElements> PlanetElements = new OrderedDictionary<string, MeanElements>
    {
        ["mercury"] = new(A: (0.38709927, 0.00000037), E: (0.20563593, 0.00001906), I: (7.00497902, -0.00594749), L: (252.2503235, 149472.67411175), Lp: (77.45779628, 0.16047689), Node: (48.33076593, -0.12534081)),
        ["venus"] = new(A: (0.72333566, 0.0000039), E: (0.00677672, -0.00004107), I: (3.39467605, -0.0007889), L: (181.9790995, 58517.81538729), Lp: (131.60246718, 0.00268329), Node: (76.67984255, -0.27769418)),
        ["earth"] = new(A: (1.00000261, 0.00000562), E: (0.01671123, -0.00004392), I: (-0.00001531, -0.01294668), L: (100.46457166, 35999.37244981), Lp: (102.93768193, 0.32327364), Node: (0, 0)),
        ["mars"] = new(A: (1.52371034, 0.00001847), E: (0.0933941, 0.00007882), I: (1.84969142, -0.00813131), L: (-4.55343205, 19140.30268499), Lp: (-23.94362959, 0.44441088), Node: (49.55953891, -0.29257343)),
        ["jupiter"] = new(A: (5.202887, -0.00011607), E: (0.04838624, -0.00013253), I: (1.30439695, -0.00183714), L: (34.39644051, 3034.74612775), Lp: (14.72847983, 0.21252668), Node: (100.47390909, 0.20469106)),
        ["saturn"] = new(A: (9.53667594, -0.0012506), E: (0.05386179, -0.00050991), I: (2.48599187, 0.00193609), L: (49.95424423, 1222.49362201), Lp: (92.59887831, -0.41897216), Node: (113.66242448, -0.28867794)),
        ["uranus"] = new(A: (19.18916464, -0.00196176), E: (0.04725744, -0.00004397), I: (0.77263783, -0.00242939), L: (313.23810451, 428.48202785), Lp: (170.9542763, 0.40805281), Node: (74.01692503, 0.04240589)),
        ["neptune"] = new(A: (30.06992276, 0.00026291), E: (0.00859048, 0.00005105), I: (1.77004347, 0.00035372), L: (-55.12002969, 218.45945325), Lp: (44.96476227, -0.32241464), Node: (131.78422574, -0.00508664)),
    };

    private static double WrapDeg(double d) => ((d % 360) + 360) % 360;

    /// <summary>
    /// Heliocentric ecliptic elements of a planet (Earth = Earth-Moon barycentre) at T centuries past J2000.
    /// a in km, angles in radians.
    /// </summary>
    public static Elements PlanetElementsAt(string name, double T)
    {
        if (!PlanetElements.TryGetValue(name, out var m)) throw new ArgumentException($"no elements for {name}", nameof(name));
        double At((double Value, double Rate) p) => p.Value + p.Rate * T;
        var a = At(m.A) * Constants.AU;
        var e = At(m.E);
        var i = At(m.I) * Constants.Deg;
        var L = At(m.L);
        var lp = At(m.Lp);
        var node = At(m.Node);
        var omega = (lp - node) * Constants.Deg;
        var M = WrapDeg(L - lp) * Constants.Deg;
        return new Elements(A: a, E: e, I: i, Omega: node * Constants.Deg, ArgPeri: omega, M: M);
    }

    /// <summary>
    /// Mean geocentric lunar elements (Meeus, Astronomical Algorithms ch. 47) at T centuries past J2000. Accurate to a
    /// few degrees. a in km, angles in radians.
    /// </summary>
    public static Elements MoonElementsAt(double T)
    {
        var Lp = 218.3164477 + 481267.88123421 * T - 0.0015786 * T * T;
        var Mp = 134.9633964 + 477198.8675055 * T + 0.0087414 * T * T;
        var node = 125.0445479 - 1934.1362891 * T + 0.0020754 * T * T;
        var omega = WrapDeg(Lp - Mp - node);
        return new Elements(A: 384400, E: 0.0549, I: 5.145 * Constants.Deg, Omega: WrapDeg(node) * Constants.Deg, ArgPeri: omega * Constants.Deg, M: WrapDeg(Mp) * Constants.Deg);
    }
}
