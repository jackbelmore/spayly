namespace Spayly.Sim.Tests;

public class KeepInsideTests
{
    static Universe Build()
    {
        var (bodies, epochJd) = Catalog.CreateSolarSystem(new DateTime(2000, 1, 1, 12, 0, 0, DateTimeKind.Utc));
        var u = new Universe(epochJd);
        u.SetBodies(bodies);
        return u;
    }

    static string[] Ids(IEnumerable<Body> bs) => [.. bs.Select(b => b.Id).Order()];

    [Fact]
    public void ASphereAroundEarthKeepsEarthItsMoonAndTheSun()
    {
        var u = Build();
        var plan = new KeepPlan();
        KeepInside.Plan(u, u.ById("earth")!.Pos, 2e6, plan);
        Assert.Equal(["earth", "moon"], Ids(plan.Inside));
        Assert.Equal(["sun"], Ids(plan.GravitySources));
        Assert.Equal(u.Bodies.Count - 3, plan.Remove.Count);
        Assert.DoesNotContain(plan.Remove, b => b.Id is "sun" or "earth" or "moon");
    }

    [Fact]
    public void AMoonInsideKeepsItsPlanetEvenWhenThePlanetIsOutside()
    {
        var u = Build();
        var plan = new KeepPlan();
        var phobos = u.ById("phobos")!;
        // Centred on Phobos and just big enough for it alone, which leaves Mars outside.
        KeepInside.Plan(u, phobos.Pos, 100, plan);
        Assert.Equal(["phobos"], Ids(plan.Inside));
        Assert.Equal(["mars", "sun"], Ids(plan.GravitySources));
    }

    [Fact]
    public void ASphereOverTheWholeSystemRemovesNothing()
    {
        var u = Build();
        var plan = new KeepPlan();
        KeepInside.Plan(u, Vec3.Zero, 1e12, plan);
        Assert.Empty(plan.Remove);
        Assert.Empty(plan.GravitySources);
        Assert.Equal(u.Bodies.Count, plan.Inside.Count);
    }

    [Fact]
    public void ASphereAboveThePlaneStillCountsBodiesByDistanceInThreeDimensions()
    {
        var u = Build();
        var plan = new KeepPlan();
        var earth = u.ById("earth")!;
        // Centre a radius above Earth: the sphere reaches Earth but not out to the Moon's side, and says nothing of z.
        KeepInside.Plan(u, earth.Pos + new Vec3(0, 0, 1e6), 1.01e6, plan);
        Assert.Contains(earth, plan.Inside);
        Assert.DoesNotContain(u.ById("moon")!, plan.Inside);
        KeepInside.Plan(u, earth.Pos + new Vec3(0, 0, 2e6), 1.01e6, plan);
        Assert.DoesNotContain(earth, plan.Inside);
    }

    [Fact]
    public void CullingParticlesKillsOnlyThoseOutside()
    {
        var p = new TestParticles(3);
        p.Pos[0] = 0; p.Pos[3] = 10; p.Pos[6] = 100;
        p.Alive[0] = p.Alive[1] = p.Alive[2] = 1;
        Assert.Equal(2, KeepInside.CullParticles(p, Vec3.Zero, 5));
        Assert.Equal([1, 0, 0], p.Alive);
    }
}
