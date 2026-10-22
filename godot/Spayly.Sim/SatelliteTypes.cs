namespace Spayly.Sim;

/// <summary>What a satellite's angles are referred to (see <see cref="SatelliteElements.Frame"/>).</summary>
public enum SatelliteFrame { Ecliptic, Pole }

/// <summary>
/// Mean orbital elements of a natural satellite, as JPL publishes them; mirrors src/sim/satelliteTypes.ts.
/// Angles in degrees, <c>A</c> in km, epoch as a Julian day.
/// </summary>
public readonly record struct SatelliteElements
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string PrimaryId { get; init; }
    /// <summary>
    /// Ecliptic — angles referred to the ecliptic J2000.
    /// Pole — referred to the plane normal to (PoleRA, PoleDec); when those are absent the primary's IAU pole is used.
    /// The node is measured from that plane's ascending node on the ICRF equator.
    /// </summary>
    public required SatelliteFrame Frame { get; init; }
    /// <summary>ICRF right ascension of the reference pole, deg.</summary>
    public double? PoleRA { get; init; }
    /// <summary>ICRF declination of the reference pole, deg.</summary>
    public double? PoleDec { get; init; }
    /// <summary>Julian day.</summary>
    public required double EpochJd { get; init; }
    /// <summary>Semi-major axis, km.</summary>
    public required double A { get; init; }
    public required double E { get; init; }
    /// <summary>Inclination, deg.</summary>
    public required double Incl { get; init; }
    /// <summary>Longitude of the ascending node, deg.</summary>
    public required double Node { get; init; }
    /// <summary>Argument of periapsis, deg.</summary>
    public required double Peri { get; init; }
    /// <summary>Mean anomaly at epoch, deg.</summary>
    public required double M { get; init; }
    /// <summary>Days.</summary>
    public required double PeriodDays { get; init; }
    /// <summary>Nodal regression period, years (absent when the node does not precess measurably).</summary>
    public double? NodeYr { get; init; }
    /// <summary>Apsidal precession period, years.</summary>
    public double? ApsisYr { get; init; }
    /// <summary>kg.</summary>
    public required double Mass { get; init; }
    /// <summary>km.</summary>
    public required double Radius { get; init; }
    public required Surface Surface { get; init; }
    /// <summary>Tidally locked: one face stays turned towards the primary.</summary>
    public required bool Locked { get; init; }
    public required BodyVisual Visual { get; init; }
}

/// <summary>Heliocentric osculating elements of a dwarf planet or asteroid (JPL Small-Body Database).</summary>
public readonly record struct MinorBodyElements
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    /// <summary>Planet or Asteroid only.</summary>
    public required BodyKind Kind { get; init; }
    public required Surface Surface { get; init; }
    /// <summary>Julian day.</summary>
    public required double EpochJd { get; init; }
    /// <summary>Semi-major axis, km.</summary>
    public required double A { get; init; }
    public required double E { get; init; }
    /// <summary>Inclination, deg.</summary>
    public required double Incl { get; init; }
    /// <summary>Longitude of the ascending node, deg.</summary>
    public required double Node { get; init; }
    /// <summary>Argument of perihelion, deg.</summary>
    public required double Peri { get; init; }
    /// <summary>Mean anomaly at epoch, deg.</summary>
    public required double M { get; init; }
    /// <summary>Mean motion, deg/day.</summary>
    public required double NDegPerDay { get; init; }
    /// <summary>kg.</summary>
    public required double Mass { get; init; }
    /// <summary>km.</summary>
    public required double Radius { get; init; }
    /// <summary>IAU rotation elements when they are published for this body.</summary>
    public Rotation? Rotation { get; init; }
    /// <summary>Otherwise the measured rotation period, hours, used with an arbitrary upright pole.</summary>
    public double? RotationPeriodHours { get; init; }
    public required BodyVisual Visual { get; init; }
}
