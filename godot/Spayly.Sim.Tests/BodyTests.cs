namespace Spayly.Sim.Tests;

// No vitest file covers body.ts directly; these pin MakeBody, Clone and the unit of every derived quantity.
// body.ts mixes units: density kg/m^3, surfaceGravity m/s^2 (SI), escapeVelocity km/s, bindingEnergy J.
public class BodyTests
{
    private static BodySpec EarthSpec(int? seed = 1234) => new()
    {
        Id = "earth", Name = "Earth", Kind = BodyKind.Planet, Surface = Surface.Rock,
        Mass = 5.972e24, Radius = 6371,
        Pos = new Vec3(Constants.AU, 0, 0), Vel = new Vec3(0, 29.78, 0),
        Rotation = new Rotation(0, 90, 190.147, 360.9856235),
        Atmosphere = new AtmosphereModel(1.225, 8500),
        Visual = new BodyVisual { Color = "#3a6ea5", Albedo = 0.306 },
        Seed = seed,
    };

    [Fact]
    public void MakeBodySetsCatalogueValuesAndDefaults()
    {
        var b = Body.MakeBody(EarthSpec());
        Assert.Equal(5.972e24, b.Mass);
        Assert.Equal(5.972e24, b.Mass0);
        Assert.Equal(6371, b.Radius);
        Assert.Equal(6371, b.Radius0);
        Assert.Equal(new Vec3(Constants.AU, 0, 0), b.Pos);
        Assert.Equal(Vec3.Zero, b.Acc);
        Assert.Equal(1234, b.Seed);
        Assert.Equal(0, b.Born);
        Assert.Empty(b.Damage.Craters);
        Assert.Equal(1, b.Damage.MagmaTau);
        Assert.Equal(1, b.Damage.DustTau);
        Assert.Equal(0, b.Damage.Version);

        b.Mass *= 2;
        b.Radius *= 3;
        Assert.Equal(5.972e24, b.Mass0);
        Assert.Equal(6371, b.Radius0);
    }

    [Fact]
    public void MakeBodyDefaultsPosVelAndDrawsASeedInRange()
    {
        var spec = EarthSpec(seed: null) with { Pos = null, Vel = null, Born = 42 };
        var b = Body.MakeBody(spec);
        Assert.Equal(Vec3.Zero, b.Pos);
        Assert.Equal(Vec3.Zero, b.Vel);
        Assert.Equal(42, b.Born);
        Assert.InRange(b.Seed, 0, 999_999_999);
    }

    [Fact]
    public void EmptyDamageMatchesTheTsDefaults()
    {
        var d = Body.EmptyDamage();
        Assert.Empty(d.Craters);
        Assert.Empty(d.Heat);
        Assert.Equal((0.0, 0.0, 1.0, 0.0, 0.0, 1.0), (d.Magma, d.MagmaTime, d.MagmaTau, d.Dust, d.DustTime, d.DustTau));
        Assert.Equal((0, 0.0, 0.0, 0), (d.Hits, d.Energy, d.Absorbed, d.Version));
        Assert.NotSame(d.Craters, Body.EmptyDamage().Craters);
    }

    [Fact]
    public void EarthDensityGravityAndEscapeVelocity()
    {
        var b = Body.MakeBody(EarthSpec());
        Close.To(5513, Body.Density(b), -1);            // kg/m^3
        Close.To(9.82, Body.SurfaceGravity(b), 2);       // m/s^2 — SI, not the km/s^2 of the rest of the sim
        Close.To(11.19, Body.EscapeVelocity(b), 2);      // km/s
        Close.To(2.24, Body.BindingEnergy(b) / 1e32, 2); // J
    }

    [Fact]
    public void RadiusForDensityInvertsDensity()
    {
        var b = Body.MakeBody(EarthSpec());
        Close.To(1, Body.RadiusForDensity(b.Mass, Body.Density(b)) / b.Radius, 12);
        Close.To(1, Body.RadiusForDensity(1e12, 2000) / 0.49237, 4); // a 1e12 kg rubble-pile asteroid is ~0.49 km
    }

    [Fact]
    public void CloneIsDeepIncludingDamageLists()
    {
        var a = Body.MakeBody(EarthSpec());
        a.Damage.Craters.Add(new Crater(new Vec3(0, 0, 1), 12, 100, 1e20));
        a.Damage.Heat.Add(new HeatSpot(new Vec3(1, 0, 0), 0.01, 100, 3600));
        a.Damage.Version = 1;
        a.Mass = 6e24;

        var c = a.Clone();
        Assert.NotSame(a, c);
        Assert.NotSame(a.Damage, c.Damage);
        Assert.NotSame(a.Damage.Craters, c.Damage.Craters);
        Assert.NotSame(a.Damage.Heat, c.Damage.Heat);
        Assert.Equal(a.Damage.Craters, c.Damage.Craters);
        Assert.Equal((a.Id, a.Mass, a.Mass0, a.Radius0, a.Pos, a.Vel, a.Rotation, a.Atmosphere, a.Visual, a.Seed),
                     (c.Id, c.Mass, c.Mass0, c.Radius0, c.Pos, c.Vel, c.Rotation, c.Atmosphere, c.Visual, c.Seed));

        c.Damage.Craters.Add(new Crater(new Vec3(0, 1, 0), 3, 200, 1e18));
        c.Damage.Heat.Clear();
        c.Damage.Version++;
        c.Pos = Vec3.Zero;
        c.Mass = 1;
        Assert.Single(a.Damage.Craters);
        Assert.Single(a.Damage.Heat);
        Assert.Equal(1, a.Damage.Version);
        Assert.Equal(new Vec3(Constants.AU, 0, 0), a.Pos);
        Assert.Equal(6e24, a.Mass);
    }
}
