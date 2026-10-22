using static Spayly.Sim.Constants;
using static Spayly.Sim.Vec3;

namespace Spayly.Sim.Tests;

// No vitest file: black holes exist only in the Godot port. The slow-down near a hole (M6.2) acts on Approach's two-body
// times, so they are checked against an integrated fall; flybys and receding bodies must not be reported.
public class ApproachTests
{
    private const double HoleMass = 1e6 * SunMass;

    private static (Universe U, Body Hole, Body Star) StarFalling(double d, Vec3 vel)
    {
        var hole = HandBuilt.Make("bh", BodyKind.BlackHole, Surface.Star, HoleMass, BlackHole.SchwarzschildRadiusKm(HoleMass), seed: 9);
        var star = HandBuilt.Make("star", BodyKind.Star, Surface.Star, SunMass, SunRadius, new Vec3(d, 0, 0), vel, 3);
        var u = HandBuilt.AtRest([hole, star]);
        return (u, u.ById("bh")!, u.ById("star")!);
    }

    // -1 km/s: nearly from rest, where a distance-over-final-speed bound had fired ~20× early in the game.
    [Theory]
    [InlineData(-2000.0)]
    [InlineData(-1.0)]
    public void RadialFallTimesMatchAnIntegratedFall(double vx)
    {
        var rt = BlackHole.TidalRadiusKm(HoleMass, SunMass, SunRadius);
        var (u, hole, star) = StarFalling(10 * rt, new Vec3(vx, 0, 0));
        var a = Approach.Test(hole, star, tidal: true);
        Assert.NotNull(a);
        Assert.Equal(ApproachEvent.TidalRadius, a.Value.Event);
        Assert.Equal(rt, a.Value.Radius, 1e-9 * rt);
        double tIn = double.NaN, tContact = double.NaN;
        const double dt = 1;
        for (var k = 1; k < 2_000_000 && double.IsNaN(tContact); k++)
        {
            u.Step(dt);
            var d = Length(star.Pos - hole.Pos);
            if (double.IsNaN(tIn) && d <= a.Value.Radius) tIn = k * dt;
            if (d <= a.Value.Contact) tContact = k * dt;
        }
        Assert.False(double.IsNaN(tContact));
        Assert.True(Math.Abs(tIn - a.Value.Arrive) < 0.01 * tIn, $"arrived after {tIn} s, predicted {a.Value.Arrive} s");
        Assert.True(Math.Abs(tContact - tIn - a.Value.Zone) < 0.03 * a.Value.Zone + 2 * dt, $"zone took {tContact - tIn} s, predicted {a.Value.Zone} s");
    }

    [Fact]
    public void FlybyAndRecedingBodiesAreNotReported()
    {
        var rt = BlackHole.TidalRadiusKm(HoleMass, SunMass, SunRadius);
        var vEsc = Math.Sqrt(2 * G * HoleMass / (10 * rt));
        // Mostly tangential: periapsis far outside r_t.
        var (_, hole, star) = StarFalling(10 * rt, new Vec3(-0.1 * vEsc, 0.9 * vEsc, 0));
        Assert.Null(Approach.Test(hole, star, tidal: true));
        var (_, hole2, star2) = StarFalling(10 * rt, new Vec3(2000, 0, 0));
        Assert.Null(Approach.Test(hole2, star2, tidal: true));
    }

    [Fact]
    public void WithoutTidalAStarOnlyReachesTheCaptureZone()
    {
        var rt = BlackHole.TidalRadiusKm(HoleMass, SunMass, SunRadius);
        var (u, hole, star) = StarFalling(10 * rt, new Vec3(-2000, 0, 0));
        var a = Approach.Test(hole, star, tidal: false);
        Assert.NotNull(a);
        Assert.Equal(ApproachEvent.Capture, a.Value.Event);
        Assert.Equal(Approach.CaptureZone * a.Value.Contact, a.Value.Radius, 1e-9 * a.Value.Radius);
        Assert.Single(Approach.Find(u, false, []));
        Assert.Equal(1, Approach.WarpFor(a.Value with { Zone = 0.01 }, 1 / 60.0, 6));
    }
}
