using System.Text.RegularExpressions;

namespace Spayly.Sim.Tests;

// tests/impact.test.ts — describe('impact model (Collins, Melosh & Marcus 2005)'), in order:
//   it('Chelyabinsk-class stone airbursts at tens of km')
//   it('a 50 m iron reaches the ground and digs a Meteor-Crater-sized hole')
//   it('a 10 km asteroid makes a Chicxulub-class complex crater')
//   it('has no atmospheric breakup on the Moon')
//   it('reports oceans boiling for a Ceres-class impact')
public class ImpactTests
{
    private static readonly TargetSpec Earth = new()
    {
        Radius = 6.371e6, Mass = 5.9722e24, Gravity = 9.81, Surface = TargetSurface.Rock, SurfaceDensity = 2500,
        Atmosphere = new AtmosphereModel { Rho0 = 1.225, H = 8000 },
    };
    private static readonly TargetSpec Moon = new()
    {
        Radius = 1.7374e6, Mass = 7.3458e22, Gravity = 1.62, Surface = TargetSurface.Rock, SurfaceDensity = 2500,
    };

    [Fact]
    public void ChelyabinskClassStoneAirburstsAtTensOfKm()
    {
        var r = Impact.ImpactReport(new ImpactorSpec { Diameter = 19, Density = 3300, Speed = 19000, Angle = 18 * Math.PI / 180 }, Earth);
        Assert.True(r.Entry.Airburst, "expected an airburst");
        Assert.True(r.Entry.BurstAltitude / 1000 > 15, $"burst altitude {r.Entry.BurstAltitude / 1000} km should be > 15");
        Assert.True(r.Entry.BurstAltitude / 1000 < 45, $"burst altitude {r.Entry.BurstAltitude / 1000} km should be < 45");
        Assert.Null(r.Crater);
        Assert.True(r.EnergyMT > 0.2, $"energy {r.EnergyMT} Mt should be > 0.2");
        Assert.True(r.EnergyMT < 1.5, $"energy {r.EnergyMT} Mt should be < 1.5");
        Assert.True(r.Airblast.GlassRadius / 1000 > 10, $"glass radius {r.Airblast.GlassRadius / 1000} km should be > 10");
    }

    [Fact]
    public void A50MIronReachesTheGroundAndDigsAMeteorCraterSizedHole()
    {
        var r = Impact.ImpactReport(new ImpactorSpec { Diameter = 50, Density = 7800, Speed = 15000, Angle = 45 * Math.PI / 180 }, Earth);
        Assert.False(r.Entry.Airburst, $"expected no airburst, burst altitude {r.Entry.BurstAltitude} m");
        Assert.NotNull(r.Crater);
        var crater = r.Crater.Value;
        Assert.True(crater.FinalDiameter / 1000 > 0.8, $"final diameter {crater.FinalDiameter / 1000} km should be > 0.8");
        Assert.True(crater.FinalDiameter / 1000 < 2.0, $"final diameter {crater.FinalDiameter / 1000} km should be < 2.0");
        Assert.False(crater.Complex, "expected a simple crater");
    }

    [Fact]
    public void A10KmAsteroidMakesAChicxulubClassComplexCrater()
    {
        var r = Impact.ImpactReport(new ImpactorSpec { Diameter = 10000, Density = 3000, Speed = 20000, Angle = 45 * Math.PI / 180 }, Earth);
        Assert.NotNull(r.Crater);
        var crater = r.Crater.Value;
        Assert.True(crater.Complex, "expected a complex crater");
        Assert.True(crater.FinalDiameter / 1000 > 90, $"final diameter {crater.FinalDiameter / 1000} km should be > 90");
        Assert.True(crater.FinalDiameter / 1000 < 200, $"final diameter {crater.FinalDiameter / 1000} km should be < 200");
        Assert.True(r.SeismicMagnitude > 9, $"seismic magnitude {r.SeismicMagnitude} should be > 9");
        Assert.True(r.SeismicMagnitude < 11, $"seismic magnitude {r.SeismicMagnitude} should be < 11");
        Assert.True(r.FireballRadius / 1000 > 80, $"fireball radius {r.FireballRadius / 1000} km should be > 80");
        Assert.Contains("extinction", r.Classification.ToLowerInvariant());
        Assert.True(r.BindingRatio < 1e-6, $"binding ratio {r.BindingRatio} should be < 1e-6");
    }

    [Fact]
    public void HasNoAtmosphericBreakupOnTheMoon()
    {
        var imp = new ImpactorSpec { Diameter = 5, Density = 3000, Speed = 20000, Angle = 0.8 };
        var e = Impact.AtmosphericEntry(imp, Moon);
        Assert.False(e.Breakup, "expected no breakup without an atmosphere");
        Assert.Equal(20000.0, e.SurfaceSpeed);
        var r = Impact.ImpactReport(imp, Moon);
        Assert.NotNull(r.Crater);
        Assert.True(r.Crater.Value.FinalDiameter > 50, $"final diameter {r.Crater.Value.FinalDiameter} m should be > 50");
    }

    [Fact]
    public void ReportsOceansBoilingForACeresClassImpact()
    {
        var r = Impact.ImpactReport(new ImpactorSpec { Diameter = 940000, Density = 2100, Speed = 30000, Angle = 1.2 }, Earth);
        Assert.True(r.Energy > 4e27, $"energy {r.Energy} J should be > 4e27");
        Assert.Matches(new Regex("steril|melt|shatter|disrupt"), r.Classification.ToLowerInvariant());
    }
}
