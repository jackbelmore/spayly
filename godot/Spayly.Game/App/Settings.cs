using System;
using System.Collections.Generic;
using Spayly.Game.Sim;
using Spayly.Sim;

namespace Spayly.Game.App;

/// <summary>What the pointer does (src/app/types.ts Mode).</summary>
public enum Mode { Explore, Asteroid, Erase, BlackHole }

public enum Composition { Rock, Iron, Carbon, Ice, Star }

/// <summary>Hero collisions (App.Sph.cs): off, or a GPU SPH patch of 3 × 10⁴ or 10⁵ particles.</summary>
public enum SphMode { Off, Fast, Hero }

/// <summary>View settings; src/app/types.ts Settings with the web defaults (app.ts:50).</summary>
public sealed class Settings
{
    public MoonDetail Moons = MoonDetail.All;
    /// <summary>Widest a body map streams to, px.</summary>
    public int Textures = 8192;
    public bool Orbits = true;
    public bool Trails = true;
    public bool Labels = true;
    public bool Belt = true;
    public bool Bloom = true;
    public bool Cinematic = true;
    public bool Diorama;
    /// <summary>Irradiance falloff exponent: 0 flat, 0.5 default, 2 physical.</summary>
    public double Falloff = 0.5;
    /// <summary>Tear bodies apart inside their Roche limit or a black hole's tidal radius (App.Tidal.cs).</summary>
    public bool Tidal = true;
    /// <summary>Run a giant impact the player watches as SPH (App.Sph.cs); off by default, as in M4.</summary>
    public SphMode Sph = SphMode.Off;
}

/// <summary>The next asteroid to fire; default a 20 m rock at 20 km/s.</summary>
public sealed class AsteroidSettings
{
    /// <summary>m.</summary>
    public double DiameterM = 20;
    public Composition Composition = Composition.Rock;
    /// <summary>km/s.</summary>
    public double SpeedKms = 20;
    public string? Name;
    /// <summary>Fire the rock as a meteor shower (App.Shower.cs): <see cref="ShowerRocks"/> equal pieces that arrive one after another.</summary>
    public bool Shower;
    /// <summary>Pieces in a shower, 8–24 (Spayly.Sim.Shower.Clamp).</summary>
    public int ShowerRocks = 16;
}

/// <summary>How a placed black hole starts moving, relative to the body whose gravity dominates where it is placed.</summary>
public enum HoleVelocity { Orbit, Rest, Drag }

/// <summary>The next black hole to place (black-hole mode, key B); default a 10 M☉ stellar hole on a circular orbit.</summary>
public sealed class BlackHoleSettings
{
    /// <summary>Slider range, M☉: the lightest observed stellar holes to the heaviest known (log scale).</summary>
    public const double MinSolar = 3, MaxSolar = 1e10;
    /// <summary>M☉.</summary>
    public double MassSolar = 10;
    public HoleVelocity Velocity = HoleVelocity.Orbit;
}

/// <param name="MassSolar">M☉.</param>
public readonly record struct HolePreset(string Name, double MassSolar, string Blurb);

public static class HolePresets
{
    public static readonly IReadOnlyList<HolePreset> All =
    [
        new("Stellar", 10, "a typical stellar-mass hole"),
        new("Cygnus X-1", 21, "the first black hole identified, 21 M☉ (Miller-Jones et al. 2021)"),
        new("IMBH", 1e4, "an intermediate-mass hole, as in a dense star cluster"),
        new("Sgr A*", 4.3e6, "the Milky Way's central hole (GRAVITY 2019)"),
        new("M87*", 6.5e9, "the first hole imaged (Event Horizon Telescope 2019)"),
    ];
}

/// <param name="DiameterM">m.</param>
/// <param name="SpeedKms">km/s.</param>
public readonly record struct Preset(string Name, double DiameterM, Composition Composition, double SpeedKms, string Blurb);

public static class Presets
{
    public static readonly IReadOnlyList<Preset> All =
    [
        new("Chelyabinsk", 19, Composition.Rock, 19, "2013 airburst over Russia"),
        new("Tunguska", 60, Composition.Rock, 15, "1908 forest-flattening airburst"),
        new("Apophis", 370, Composition.Rock, 12.6, "the 2029 near miss"),
        new("Bennu", 490, Composition.Carbon, 12, "rubble pile visited by OSIRIS-REx"),
        new("Chicxulub", 10000, Composition.Rock, 20, "goodbye dinosaurs"),
        new("Vesta", 525000, Composition.Rock, 20, "a whole protoplanet"),
        new("Ceres", 940000, Composition.Carbon, 18, "dwarf planet"),
        new("Theia", 6000000, Composition.Rock, 10, "the Moon-forming giant impact"),
        new("Sun-like star", 2 * Constants.SunRadius * 1000, Composition.Star, 1000, "1 M☉, 1 R☉: aim it at a black hole for a tidal disruption"),
    ];

    /// <summary>kg/m^3.</summary>
    /// <summary>kg/m^3. A star is the Sun's mean density, so the Sun-like preset is 1 M☉ to within rounding.</summary>
    public static double Density(Composition c) => c switch
    {
        Composition.Iron => 7800, Composition.Carbon => 1500, Composition.Ice => 900,
        Composition.Star => Constants.SunMass / (4.0 / 3 * Math.PI * Math.Pow(Constants.SunRadius * 1000, 3)),
        _ => 3000,
    };

    public static string Color(Composition c) => c switch { Composition.Iron => "#c9c2b8", Composition.Carbon => "#6f6560", Composition.Ice => "#bfe0ff", Composition.Star => "#fff4e0", _ => "#b8a48c" };
}
