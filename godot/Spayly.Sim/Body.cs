namespace Spayly.Sim;

/// <summary>BlackHole is appended last so the existing members keep their values.</summary>
public enum BodyKind { Star, Planet, Moon, Asteroid, Fragment, BlackHole }

public enum Surface { Rock, Ice, Gas, Star, Lava }

/// <summary>IAU rotation model.</summary>
/// <param name="PoleRA">Pole direction, ICRF right ascension in degrees (IAU).</param>
/// <param name="PoleDec">Pole direction, ICRF declination in degrees (IAU).</param>
/// <param name="W0">Prime meridian angle at J2000, deg.</param>
/// <param name="Rate">Prime meridian rate, deg/day. Negative rate = retrograde.</param>
public readonly record struct Rotation(double PoleRA, double PoleDec, double W0, double Rate);

/// <summary>Exponential atmosphere used by the entry model.</summary>
/// <param name="Rho0">Surface density, kg/m^3.</param>
/// <param name="H">Scale height, m.</param>
public readonly record struct AtmosphereModel(double Rho0, double H);

public readonly record struct AtmosphereVisual
{
    /// <summary>Rayleigh scattering coefficients per metre (r,g,b).</summary>
    public required (double R, double G, double B) Rayleigh { get; init; }
    /// <summary>Mie scattering coefficient per metre.</summary>
    public required double Mie { get; init; }
    /// <summary>m.</summary>
    public required double RayleighH { get; init; }
    /// <summary>m.</summary>
    public required double MieH { get; init; }
    /// <summary>Shell thickness as a fraction of the radius.</summary>
    public required double Thickness { get; init; }
    /// <summary>Mie phase asymmetry.</summary>
    public required double G { get; init; }
    /// <summary>Overall multiplier for artistic control.</summary>
    public required double Strength { get; init; }
}

/// <param name="Inner">km.</param>
/// <param name="Outer">km.</param>
/// <param name="Texture">Optional texture file name.</param>
public readonly record struct RingVisual(double Inner, double Outer, string? Texture = null);

public readonly record struct BodyVisual
{
    /// <summary>CSS colour, e.g. "#9a8f82".</summary>
    public required string Color { get; init; }
    public string? Texture { get; init; }
    public string? Night { get; init; }
    public string? Clouds { get; init; }
    public string? Normal { get; init; }
    public string? Specular { get; init; }
    public RingVisual? Ring { get; init; }
    /// <summary>The map has blank, never-imaged regions; grow the imaged parts over them at load.</summary>
    public bool? FillGaps { get; init; }
    /// <summary>Multiply the map by this colour at load; for bodies that borrow another body's map.</summary>
    public string? Tint { get; init; }
    /// <summary>Rotate the map in longitude by this fraction of a turn at load, for the same reason.</summary>
    public double? Roll { get; init; }
    public AtmosphereVisual? Atmosphere { get; init; }
    /// <summary>Bond albedo for equilibrium temperature.</summary>
    public required double Albedo { get; init; }
    /// <summary>Baseline mean surface temperature (K) used for the climate cue.</summary>
    public double? BaselineTemp { get; init; }
}

/// <param name="Dir">Unit direction in the body frame.</param>
/// <param name="DiameterKm">km.</param>
/// <param name="Time">Simulation time of the impact, s.</param>
/// <param name="Energy">Impact energy, J.</param>
public readonly record struct Crater(Vec3 Dir, double DiameterKm, double Time, double Energy);

/// <param name="Dir">Unit direction in the body frame.</param>
/// <param name="AngularRadius">rad.</param>
/// <param name="Time">Simulation time the spot formed, s.</param>
/// <param name="Tau">Cooling timescale, seconds.</param>
public readonly record struct HeatSpot(Vec3 Dir, double AngularRadius, double Time, double Tau);

public sealed class DamageState
{
    public List<Crater> Craters { get; set; } = [];
    public List<HeatSpot> Heat { get; set; } = [];
    /// <summary>0..1 magma glow (global), decaying over MagmaTau seconds from MagmaTime.</summary>
    public double Magma { get; set; }
    /// <summary>s.</summary>
    public double MagmaTime { get; set; }
    /// <summary>s.</summary>
    public double MagmaTau { get; set; } = 1;
    /// <summary>0..1 atmospheric dust load.</summary>
    public double Dust { get; set; }
    /// <summary>s.</summary>
    public double DustTime { get; set; }
    /// <summary>s.</summary>
    public double DustTau { get; set; } = 1;
    public int Hits { get; set; }
    /// <summary>Total impact energy received, J.</summary>
    public double Energy { get; set; }
    /// <summary>J.</summary>
    public double Absorbed { get; set; }
    /// <summary>Incremented whenever the crater list changes so views can repaint.</summary>
    public int Version { get; set; }

    /// <summary>Deep copy: the crater and heat lists are new lists (their elements are values).</summary>
    public DamageState Clone() => new()
    {
        Craters = [.. Craters], Heat = [.. Heat], Magma = Magma, MagmaTime = MagmaTime, MagmaTau = MagmaTau,
        Dust = Dust, DustTime = DustTime, DustTau = DustTau, Hits = Hits, Energy = Energy, Absorbed = Absorbed, Version = Version,
    };
}

public readonly record struct BodySpec
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required BodyKind Kind { get; init; }
    public required Surface Surface { get; init; }
    /// <summary>kg.</summary>
    public required double Mass { get; init; }
    /// <summary>km.</summary>
    public required double Radius { get; init; }
    /// <summary>km, ecliptic J2000.</summary>
    public Vec3? Pos { get; init; }
    /// <summary>km/s.</summary>
    public Vec3? Vel { get; init; }
    public string? PrimaryId { get; init; }
    public Rotation? Rotation { get; init; }
    public AtmosphereModel? Atmosphere { get; init; }
    public required BodyVisual Visual { get; init; }
    public int? Seed { get; init; }
    /// <summary>Simulation time of creation, s.</summary>
    public double? Born { get; init; }
}

/// <summary>
/// A simulated body; mirrors src/sim/body.ts. The constructor is private so that <see cref="MakeBody"/> (and
/// <see cref="Clone"/>) are the only places Mass0/Radius0 are written from outside the library. They are
/// <c>internal set</c> rather than <c>init</c> because collide.ts rewrites them when bodies merge or fragment (the
/// merged body's catalogue value is the new body); nothing outside Spayly.Sim may touch them. The module's free
/// functions live here as statics; <c>using static Spayly.Sim.Body;</c> gives them their TS spelling.
/// </summary>
public sealed class Body : IWorldBody
{
    /// <summary>The Paczyński–Wiita r_s this body adds to its pairs (<see cref="PseudoNewtonian.HoleRs"/>), km.</summary>
    double IWorldBody.HoleRs => PseudoNewtonian.HoleRs(this);

    private Body() { }

    /// <summary>Stable and unique; views key off it.</summary>
    public required string Id { get; init; }
    public required string Name { get; set; }
    public required BodyKind Kind { get; set; }
    public required Surface Surface { get; set; }
    /// <summary>kg.</summary>
    public required double Mass { get; set; }
    /// <summary>km.</summary>
    public required double Radius { get; set; }
    /// <summary>Catalogue mass, kg. Written once, in MakeBody.</summary>
    public double Mass0 { get; internal set; }
    /// <summary>Catalogue radius, km. Written once, in MakeBody.</summary>
    public double Radius0 { get; internal set; }
    /// <summary>km, ecliptic J2000.</summary>
    public Vec3 Pos { get; set; }
    /// <summary>km/s.</summary>
    public Vec3 Vel { get; set; }
    /// <summary>km/s^2.</summary>
    public Vec3 Acc { get; set; }
    public string? PrimaryId { get; set; }
    public Rotation? Rotation { get; set; }
    public AtmosphereModel? Atmosphere { get; set; }
    public required BodyVisual Visual { get; set; }
    public required DamageState Damage { get; set; }
    /// <summary>Random seed for procedural shapes.</summary>
    public int Seed { get; set; }
    /// <summary>Simulation time at which the body was created, s.</summary>
    public double Born { get; set; }
    /// <summary>
    /// Dimensionless spin a/M = cJ/(GM²) of a black hole, 0 to 1; 0 for everything else and for a hole placed by hand. A hole
    /// merger sets it from HLZ14 (<see cref="GravitationalWaves.RemnantSpin"/>); gravity, lensing and the disc ignore it.
    /// </summary>
    public double Spin { get; set; }

    /// <summary>
    /// Deep copy, the C# form of the structuredClone that Universe snapshots take. Every other member is a value or an
    /// immutable record, so only Damage needs copying.
    /// </summary>
    public Body Clone() => new()
    {
        Id = Id, Name = Name, Kind = Kind, Surface = Surface, Mass = Mass, Radius = Radius, Mass0 = Mass0, Radius0 = Radius0,
        Pos = Pos, Vel = Vel, Acc = Acc, PrimaryId = PrimaryId, Rotation = Rotation, Atmosphere = Atmosphere, Visual = Visual,
        Damage = Damage.Clone(), Seed = Seed, Born = Born, Spin = Spin,
    };

    public static DamageState EmptyDamage() => new()
    {
        Craters = [], Heat = [], Magma = 0, MagmaTime = 0, MagmaTau = 1, Dust = 0, DustTime = 0, DustTau = 1,
        Hits = 0, Energy = 0, Absorbed = 0, Version = 0,
    };

    public static Body MakeBody(BodySpec spec)
    {
        // A black hole's size is not a catalogue value: it is its Schwarzschild radius, fixed by the mass.
        var radius = spec.Kind == BodyKind.BlackHole ? BlackHole.SchwarzschildRadiusKm(spec.Mass) : spec.Radius;
        return MakeBody(spec, radius);
    }

    private static Body MakeBody(BodySpec spec, double radius) => new()
    {
        Id = spec.Id,
        Name = spec.Name,
        Kind = spec.Kind,
        Surface = spec.Surface,
        Mass = spec.Mass,
        Radius = radius,
        Mass0 = spec.Mass,
        Radius0 = radius,
        Pos = spec.Pos ?? Vec3.Zero,
        Vel = spec.Vel ?? Vec3.Zero,
        Acc = Vec3.Zero,
        PrimaryId = spec.PrimaryId,
        Rotation = spec.Rotation,
        Atmosphere = spec.Atmosphere,
        Visual = spec.Visual,
        Damage = EmptyDamage(),
        // Math.floor(Math.random() * 1e9): an integer in [0, 1e9), which Next's exclusive upper bound matches.
        Seed = spec.Seed ?? Random.Shared.Next(0, 1_000_000_000),
        Born = spec.Born ?? 0,
    };

    private const double GSi = 6.6743e-11;

    /// <summary>Mean density, kg/m^3.</summary>
    public static double Density(Body b) => b.Mass / ((4.0 / 3) * Math.PI * Math.Pow(b.Radius * 1000, 3));

    /// <summary>Surface gravity, m/s^2 (SI, not km/s^2).</summary>
    public static double SurfaceGravity(Body b) => (GSi * b.Mass) / Math.Pow(b.Radius * 1000, 2);

    /// <summary>Escape velocity, km/s.</summary>
    public static double EscapeVelocity(Body b) => Math.Sqrt((2 * GSi * b.Mass) / (b.Radius * 1000)) / 1000;

    /// <summary>Gravitational binding energy of a uniform sphere, J.</summary>
    public static double BindingEnergy(Body b) => (3 * GSi * b.Mass * b.Mass) / (5 * b.Radius * 1000);

    /// <summary>Radius (km) a body of this mass (kg) would have at the given density (kg/m^3).</summary>
    public static double RadiusForDensity(double mass, double rho) => Math.Cbrt((3 * mass) / (4 * Math.PI * rho)) / 1000;
}
