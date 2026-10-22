namespace Spayly.Sim;

/// <summary>
/// The body catalogue and the solar-system builder; mirrors src/sim/catalog.ts. The TS <c>CATALOG</c> record is
/// <see cref="Specs"/> here, since a member cannot share its class's name.
/// </summary>
public static class Catalog
{
    private static readonly AtmosphereVisual EarthAtmo = new()
    {
        Rayleigh = (5.8e-6, 13.5e-6, 33.1e-6), Mie = 8e-6, RayleighH = 8000, MieH = 1200, Thickness = 0.028, G = 0.76, Strength = 1.0,
    };
    private static readonly AtmosphereVisual MarsAtmo = new()
    {
        Rayleigh = (4e-8, 7e-8, 1.5e-7), Mie = 1.6e-6, RayleighH = 11100, MieH = 9000, Thickness = 0.025, G = 0.65, Strength = 1.0,
    };
    private static readonly AtmosphereVisual VenusAtmo = new()
    {
        Rayleigh = (2e-6, 2.5e-6, 3.5e-6), Mie = 2.5e-5, RayleighH = 15900, MieH = 9000, Thickness = 0.035, G = 0.7, Strength = 0.55,
    };
    private static readonly AtmosphereVisual JupiterAtmo = new()
    {
        Rayleigh = (1.2e-7, 1.8e-7, 3.2e-7), Mie = 4e-7, RayleighH = 27000, MieH = 20000, Thickness = 0.012, G = 0.7, Strength = 1.0,
    };
    private static readonly AtmosphereVisual SaturnAtmo = new()
    {
        Rayleigh = (1.1e-7, 1.5e-7, 2.4e-7), Mie = 4e-7, RayleighH = 59500, MieH = 40000, Thickness = 0.014, G = 0.7, Strength = 0.9,
    };
    private static readonly AtmosphereVisual IceGiantAtmo = new()
    {
        Rayleigh = (1.0e-7, 2.2e-7, 4.5e-7), Mie = 3e-7, RayleighH = 27700, MieH = 20000, Thickness = 0.016, G = 0.7, Strength = 1.0,
    };
    /// <summary>
    /// Titan's tholin haze. The shader's Rayleigh term is the only colour knob for in-scattered light, so these
    /// coefficients run red-heavy rather than blue-heavy: real tholins scatter and absorb their way to orange, which a
    /// pure Rayleigh model cannot reach. Extinction is kept mild so the dune fields still show through near the
    /// sub-solar point, the way Cassini's infrared maps see them.
    /// </summary>
    private static readonly AtmosphereVisual TitanAtmo = new()
    {
        Rayleigh = (2.6e-6, 1.45e-6, 0.55e-6), Mie = 5e-6, RayleighH = 30000, MieH = 40000, Thickness = 0.085, G = 0.35, Strength = 2.4,
    };

    /// <summary>
    /// Static physical data (the TS <c>CATALOG</c>; Pos/Vel are never set). Masses from JPL GM values (kg), radii mean
    /// volumetric (km), rotation from IAU. In the TS order.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, BodySpec> Specs = new OrderedDictionary<string, BodySpec>
    {
        ["sun"] = new()
        {
            Id = "sun", Name = "Sun", Kind = BodyKind.Star, Surface = Surface.Star, Mass = Constants.SunMass, Radius = Constants.SunRadius,
            Rotation = new Rotation(PoleRA: 286.13, PoleDec: 63.87, W0: 84.176, Rate: 14.1844),
            Visual = new BodyVisual { Color = "#ffd27a", Texture = "sun.jpg", Albedo = 0 },
        },
        ["mercury"] = new()
        {
            Id = "mercury", Name = "Mercury", Kind = BodyKind.Planet, Surface = Surface.Rock, Mass = 3.3011e23, Radius = 2439.7, PrimaryId = "sun",
            Rotation = new Rotation(PoleRA: 281.0103, PoleDec: 61.4155, W0: 329.5988, Rate: 6.1385108),
            Visual = new BodyVisual { Color = "#b8b0a8", Texture = "mercury.jpg", Albedo = 0.088, BaselineTemp = 440 },
        },
        ["venus"] = new()
        {
            Id = "venus", Name = "Venus", Kind = BodyKind.Planet, Surface = Surface.Rock, Mass = 4.8673e24, Radius = 6051.8, PrimaryId = "sun",
            Rotation = new Rotation(PoleRA: 272.76, PoleDec: 67.16, W0: 160.2, Rate: -1.4813688),
            Atmosphere = new AtmosphereModel(Rho0: 65, H: 15900),
            Visual = new BodyVisual { Color = "#e8c98a", Texture = "venus_atmosphere.jpg", Albedo = 0.76, BaselineTemp = 737, Atmosphere = VenusAtmo },
        },
        ["earth"] = new()
        {
            Id = "earth", Name = "Earth", Kind = BodyKind.Planet, Surface = Surface.Rock, Mass = 5.9722e24, Radius = 6371.0, PrimaryId = "sun",
            Rotation = new Rotation(PoleRA: 0, PoleDec: 90, W0: 190.147, Rate: 360.9856235),
            Atmosphere = new AtmosphereModel(Rho0: 1.225, H: 8000),
            Visual = new BodyVisual
            {
                Color = "#6fa8ff", Texture = "earth_day.jpg", Night = "earth_night.jpg", Clouds = "earth_clouds.jpg",
                Normal = "earth_normal.jpg", Specular = "earth_specular.jpg", Albedo = 0.306, BaselineTemp = 288, Atmosphere = EarthAtmo,
            },
        },
        ["mars"] = new()
        {
            Id = "mars", Name = "Mars", Kind = BodyKind.Planet, Surface = Surface.Rock, Mass = 6.4169e23, Radius = 3389.5, PrimaryId = "sun",
            Rotation = new Rotation(PoleRA: 317.68143, PoleDec: 52.8865, W0: 176.63, Rate: 350.89198226),
            Atmosphere = new AtmosphereModel(Rho0: 0.02, H: 11100),
            Visual = new BodyVisual { Color = "#ff7a4d", Texture = "mars.jpg", Albedo = 0.25, BaselineTemp = 210, Atmosphere = MarsAtmo },
        },
        ["jupiter"] = new()
        {
            Id = "jupiter", Name = "Jupiter", Kind = BodyKind.Planet, Surface = Surface.Gas, Mass = 1.89819e27, Radius = 69911, PrimaryId = "sun",
            Rotation = new Rotation(PoleRA: 268.056595, PoleDec: 64.495303, W0: 284.95, Rate: 870.536),
            Atmosphere = new AtmosphereModel(Rho0: 0.16, H: 27000),
            Visual = new BodyVisual { Color = "#d9b48f", Texture = "jupiter.jpg", Albedo = 0.503, Atmosphere = JupiterAtmo },
        },
        ["saturn"] = new()
        {
            Id = "saturn", Name = "Saturn", Kind = BodyKind.Planet, Surface = Surface.Gas, Mass = 5.6832e26, Radius = 58232, PrimaryId = "sun",
            Rotation = new Rotation(PoleRA: 40.589, PoleDec: 83.537, W0: 38.9, Rate: 810.7939024),
            Atmosphere = new AtmosphereModel(Rho0: 0.19, H: 59500),
            Visual = new BodyVisual { Color = "#f0d9a0", Texture = "saturn.jpg", Albedo = 0.342, Atmosphere = SaturnAtmo, Ring = new RingVisual(Inner: 74500, Outer: 140220) },
        },
        ["uranus"] = new()
        {
            Id = "uranus", Name = "Uranus", Kind = BodyKind.Planet, Surface = Surface.Gas, Mass = 8.6811e25, Radius = 25362, PrimaryId = "sun",
            Rotation = new Rotation(PoleRA: 257.311, PoleDec: -15.175, W0: 203.81, Rate: -501.1600928),
            Atmosphere = new AtmosphereModel(Rho0: 0.42, H: 27700),
            Visual = new BodyVisual { Color = "#9be3e8", Texture = "uranus.jpg", Albedo = 0.3, Atmosphere = IceGiantAtmo, Ring = new RingVisual(Inner: 41837, Outer: 51149) },
        },
        ["neptune"] = new()
        {
            Id = "neptune", Name = "Neptune", Kind = BodyKind.Planet, Surface = Surface.Gas, Mass = 1.02409e26, Radius = 24622, PrimaryId = "sun",
            Rotation = new Rotation(PoleRA: 299.36, PoleDec: 43.46, W0: 249.978, Rate: 541.1397757),
            Atmosphere = new AtmosphereModel(Rho0: 0.45, H: 19700),
            Visual = new BodyVisual { Color = "#5c7cff", Texture = "neptune.jpg", Albedo = 0.29, Atmosphere = IceGiantAtmo },
        },
    };

    /// <summary>Entry atmospheres for bodies outside Specs (surface density kg/m^3, scale height m).</summary>
    private static readonly Dictionary<string, AtmosphereModel> SatelliteAtmosphere = new()
    {
        ["titan"] = new AtmosphereModel(Rho0: 5.4, H: 20600),
    };

    private static readonly string[] PlanetsFromElements = ["mercury", "venus", "earth", "mars", "jupiter", "saturn", "uranus", "neptune"];

    /// <summary>Number-key targets, in order: 1..9 then 0.</summary>
    public static readonly IReadOnlyList<string> PlanetOrder = ["sun", "mercury", "venus", "earth", "mars", "jupiter", "saturn", "uranus", "neptune", "pluto"];

    private static readonly string[] DisplayOrderArray = BuildDisplayOrder();

    /// <summary>Order bodies appear in the HUD list: each planet followed by its moons.</summary>
    public static readonly IReadOnlyList<string> DisplayOrder = DisplayOrderArray;

    private static string[] BuildDisplayOrder()
    {
        var order = new List<string> { "sun" };
        IEnumerable<string> MoonsOf(string id) => SatelliteTable.Satellites.Where(s => s.PrimaryId == id).Select(s => s.Id);
        var minorsBetween = new Dictionary<string, string[]>
        {
            ["mars"] = ["ceres", "pallas", "vesta"],
            ["neptune"] = ["pluto", "haumea", "makemake", "eris"],
        };
        foreach (var id in PlanetsFromElements)
        {
            order.Add(id);
            order.AddRange(MoonsOf(id));
            foreach (var m in minorsBetween.GetValueOrDefault(id) ?? [])
            {
                order.Add(m);
                order.AddRange(MoonsOf(m));
            }
        }
        return [.. order];
    }

    /// <summary>The satellites orbiting a given body, as the generated table lists them.</summary>
    public static List<SatelliteElements> SatellitesOf(string id) => SatelliteTable.Satellites.Where(s => s.PrimaryId == id).ToList();

    /// <summary>JS truthiness of an optional number: absent, 0 and NaN are false.</summary>
    private static bool Truthy(double? x) => x is double v && v != 0 && !double.IsNaN(v);

    /// <summary>
    /// Rotation elements of a tidally locked satellite. Its spin axis is the orbit normal and it turns once per orbit,
    /// with the prime meridian on the sub-primary point — the IAU convention for the regular satellites, and what makes
    /// the published maps line up. The axis is taken in whichever hemisphere <paramref name="reference"/> (the Laplace or
    /// planet pole) lies in, so that north on the map is north; a retrograde moon like Triton then gets a negative
    /// rotation rate, again as the IAU writes it. periodDays in days, jd a Julian day.
    /// </summary>
    private static Rotation LockedRotation(Vec3 reference, double periodDays, (Vec3 R, Vec3 V) rel, double jd)
    {
        var normal = Vec3.Normalize(Vec3.Cross(rel.R, rel.V));
        var prograde = Vec3.Dot(normal, reference) >= 0;
        var spin = prograde ? normal : normal * -1;
        var basis = Frames.PlaneBasis(spin);
        var rate = (prograde ? 360 : -360) / periodDays;
        // The primary sits opposite the satellite's position vector.
        var toPrimary = Vec3.Normalize(rel.R * -1);
        var W = Math.Atan2(Vec3.Dot(toPrimary, basis.Y), Vec3.Dot(toPrimary, basis.X)) / Constants.Deg;
        var (poleRA, poleDec) = Frames.EclipticToPole(spin);
        return new Rotation(PoleRA: poleRA, PoleDec: poleDec, W0: W - rate * (jd - Constants.J2000Jd), Rate: rate);
    }

    /// <summary>Position (km) and velocity (km/s) of a satellite relative to its primary, at Julian day jd. Mass in kg.</summary>
    private static (Vec3 R, Vec3 V) SatelliteState(SatelliteElements sat, double primaryMass, Vec3? pole, double jd)
    {
        var days = jd - sat.EpochJd;
        // Secular precession of the orbit itself: the node regresses for prograde orbits and advances for retrograde
        // ones, while the apsides always advance along the motion.
        var retrograde = sat.Incl > 90;
        var nodeRate = Truthy(sat.NodeYr) ? ((retrograde ? 360 : -360) * 86400) / (sat.NodeYr!.Value * Constants.Year) : 0;
        var periRate = Truthy(sat.ApsisYr) ? (360 * 86400) / (sat.ApsisYr!.Value * Constants.Year) : 0;
        var el = new Elements(
            A: sat.A,
            E: sat.E,
            I: sat.Incl * Constants.Deg,
            Omega: (sat.Node + nodeRate * days) * Constants.Deg,
            ArgPeri: (sat.Peri + periRate * days) * Constants.Deg,
            M: (sat.M + (360 * days) / sat.PeriodDays) * Constants.Deg);
        var mu = Constants.G * (primaryMass + sat.Mass);
        // Meeus' mean lunar elements track the Moon better than the single-epoch table row.
        if (sat.Id == "moon") return Kepler.ElementsToState(mu, Ephemeris.MoonElementsAt(Ephemeris.CenturiesSinceJ2000(jd)));
        var state = Kepler.ElementsToState(mu, el);
        if (sat.Frame == SatelliteFrame.Ecliptic || pole is not Vec3 p) return state;
        var basis = Frames.PlaneBasis(p);
        return (Frames.FromBasis(basis, state.R), Frames.FromBasis(basis, state.V));
    }

    /// <summary>Radius (km) at which a satellite counts as "major" for the level-of-detail setting.</summary>
    public const double MajorMoonRadiusKm = 600;

    /// <summary>Build a satellite in orbit around <paramref name="primary"/> as its elements place it at Julian day jd.</summary>
    public static Body MakeSatellite(SatelliteElements sat, Body primary, double jd) => SatelliteBody(sat, primary, primary.Rotation, jd);

    private static Body SatelliteBody(SatelliteElements sat, Body primary, Rotation? primaryRotation, double jd)
    {
        var poleRA = sat.PoleRA ?? primaryRotation?.PoleRA;
        var poleDec = sat.PoleDec ?? primaryRotation?.PoleDec;
        Vec3? pole = poleRA is double ra && poleDec is double dec ? Frames.PoleToEcliptic(ra, dec) : null;
        var rel = SatelliteState(sat, primary.Mass, pole, jd);
        Rotation? rotation = sat.Locked ? LockedRotation(pole ?? EclipticNorth, sat.PeriodDays, rel, jd) : null;
        return Body.MakeBody(new BodySpec
        {
            Id = sat.Id, Name = sat.Name, Kind = BodyKind.Moon, Surface = sat.Surface,
            Mass = sat.Mass, Radius = sat.Radius, PrimaryId = sat.PrimaryId,
            Pos = primary.Pos + rel.R, Vel = primary.Vel + rel.V,
            Rotation = rotation, Atmosphere = SatelliteAtmosphere.TryGetValue(sat.Id, out var atmo) ? atmo : null,
            Visual = sat.Id == "titan" ? sat.Visual with { Atmosphere = TitanAtmo } : sat.Visual,
        });
    }

    /// <summary>Heliocentric state (km, km/s) of a dwarf planet or asteroid at Julian day jd.</summary>
    private static (Vec3 R, Vec3 V) MinorState(MinorBodyElements m, double jd)
    {
        var days = jd - m.EpochJd;
        var el = new Elements(
            A: m.A, E: m.E, I: m.Incl * Constants.Deg, Omega: m.Node * Constants.Deg, ArgPeri: m.Peri * Constants.Deg,
            M: (m.M + m.NDegPerDay * days) * Constants.Deg);
        return Kepler.ElementsToState(Constants.G * (Constants.SunMass + m.Mass), el);
    }

    /// <summary>Ecliptic north in ICRF: the fallback spin axis for bodies with no published pole.</summary>
    private const double EclipticPoleRA = 270;
    private const double EclipticPoleDec = 90 - 23.4392911;
    private static readonly Vec3 EclipticNorth = new(0, 0, 1);

    private static Body MinorBody(MinorBodyElements m, (Vec3 R, Vec3 V) state)
    {
        var rotation = m.Rotation
            ?? (Truthy(m.RotationPeriodHours)
                ? new Rotation(PoleRA: EclipticPoleRA, PoleDec: EclipticPoleDec, W0: 0, Rate: (360 * 24) / m.RotationPeriodHours!.Value)
                : null);
        return Body.MakeBody(new BodySpec
        {
            Id = m.Id, Name = m.Name, Kind = m.Kind, Surface = m.Surface, Mass = m.Mass, Radius = m.Radius,
            PrimaryId = "sun", Pos = state.R, Vel = state.V, Rotation = rotation, Visual = m.Visual,
        });
    }

    /// <summary>Shift a planet and its moons so their common barycentre lands on the position the elements describe.</summary>
    private static void CentreSystem(List<Body> members, Vec3 pos, Vec3 vel)
    {
        double m = 0;
        var r = Vec3.Zero;
        var v = Vec3.Zero;
        foreach (var b in members)
        {
            m += b.Mass;
            r += b.Pos * b.Mass;
            v += b.Vel * b.Mass;
        }
        var dr = pos - r * (1 / m);
        var dv = vel - v * (1 / m);
        foreach (var b in members)
        {
            b.Pos += dr;
            b.Vel += dv;
        }
    }

    /// <summary>
    /// Build the Sun, the planets with their major moons, the dwarf planets and the two largest asteroids at the given
    /// date, with the system barycentre at rest at the origin. Bodies come back in <see cref="DisplayOrder"/>.
    /// </summary>
    public static (List<Body> Bodies, double EpochJd) CreateSolarSystem(DateTime date)
    {
        var jd = Ephemeris.JulianDay(date);
        var T = Ephemeris.CenturiesSinceJ2000(jd);
        var bodies = new List<Body> { Body.MakeBody(Specs["sun"]) };

        // Place a primary at (r, v), hang its satellites off it, then recentre the group on (r, v).
        void WithMoons(Body primary, List<SatelliteElements> moons, Vec3 r, Vec3 v, Rotation? rotation)
        {
            primary.Pos = r;
            primary.Vel = v;
            var group = new List<Body> { primary };
            foreach (var sat in moons) group.Add(SatelliteBody(sat, primary, rotation, jd));
            CentreSystem(group, r, v);
            bodies.AddRange(group);
        }

        foreach (var id in PlanetsFromElements)
        {
            var spec = Specs[id];
            var moons = SatellitesOf(id);
            // The Standish elements describe the planet-system barycentre, so the mass in mu is the system's.
            var systemMass = moons.Aggregate(spec.Mass, (s, m) => s + m.Mass);
            var el = Ephemeris.PlanetElementsAt(id, T);
            var (r, v) = Kepler.ElementsToState(Constants.G * (Constants.SunMass + systemMass), el);
            WithMoons(Body.MakeBody(spec), moons, r, v, spec.Rotation);
        }

        foreach (var m in SatelliteTable.MinorBodies)
        {
            var state = MinorState(m, jd);
            var body = MinorBody(m, state);
            WithMoons(body, SatellitesOf(m.Id), state.R, state.V, body.Rotation);
        }

        // Put the barycentre at rest at the origin.
        double mTot = 0;
        var pSum = Vec3.Zero;
        var rSum = Vec3.Zero;
        foreach (var b in bodies)
        {
            mTot += b.Mass;
            pSum += b.Vel * b.Mass;
            rSum += b.Pos * b.Mass;
        }
        var vBary = pSum * (1 / mTot);
        var rBary = rSum * (1 / mTot);
        foreach (var b in bodies)
        {
            b.Vel -= vBary;
            b.Pos -= rBary;
        }
        int Rank(string id)
        {
            var i = Array.IndexOf(DisplayOrderArray, id);
            return i < 0 ? DisplayOrderArray.Length : i;
        }
        // OrderBy is stable, as Array.prototype.sort is; List.Sort is not.
        return (bodies.OrderBy(b => Rank(b.Id)).ToList(), jd);
    }
}
