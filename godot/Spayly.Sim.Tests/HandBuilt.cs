using static Spayly.Sim.Constants;

namespace Spayly.Sim.Tests;

/// <summary>
/// Small systems built without the catalogue, op for op the same as Golden/web-universe-handbuilt.source.ts.txt so the
/// web build's numbers apply to them bit for bit. Only + - * / and sqrt go into them: those are correctly rounded in
/// both runtimes, where sin/cos/log are not guaranteed to be.
/// </summary>
internal static class HandBuilt
{
    private static readonly BodyVisual Vis = new() { Color = "#fff", Albedo = 0.3 };

    public static Body Make(string id, BodyKind kind, Surface surface, double mass, double radius, Vec3? pos = null, Vec3? vel = null, int seed = 5) =>
        Body.MakeBody(new BodySpec
        {
            Id = id, Name = id, Kind = kind, Surface = surface, Mass = mass, Radius = radius, Pos = pos, Vel = vel, Visual = Vis, Seed = seed,
        });

    /// <summary>(x, y) turned q quarter-turns about +z; exact, unlike a rotation matrix.</summary>
    public static Vec3 Quad(int q, double x, double y) => q switch
    {
        0 => new Vec3(x, y, 0),
        1 => new Vec3(-y, x, 0),
        2 => new Vec3(-x, -y, 0),
        _ => new Vec3(y, -x, 0),
    };

    /// <summary>A planet on a circular heliocentric orbit of aAU astronomical units, q quarter-turns round from +x.</summary>
    public static Body Planet(string id, double aAU, double mass, double radius, int q)
    {
        var a = aAU * AU;
        var v = Math.Sqrt((G * SunMass) / a);
        return Make(id, BodyKind.Planet, Surface.Rock, mass, radius, Quad(q, a, 0), Quad(q, 0, v));
    }

    public static Body Sun() => Make("sun", BodyKind.Star, Surface.Star, SunMass, 695700, seed: 1);

    /// <summary>Sun, Earth and Moon (slightly inclined), plus seven circular planets when asked; barycentre at rest at the origin.</summary>
    public static Universe Build(bool withPlanets = false)
    {
        var vE = Math.Sqrt((G * SunMass) / AU);
        var vM = Math.Sqrt((G * 5.9722e24) / 384400);
        var bodies = new List<Body>
        {
            Sun(),
            Make("earth", BodyKind.Planet, Surface.Rock, 5.9722e24, 6371, new Vec3(AU, 0, 0), new Vec3(0, vE, 0.001), 2),
            Make("moon", BodyKind.Moon, Surface.Rock, 7.342e22, 1737.4, new Vec3(AU + 384400, 0, 0), new Vec3(0, vE + vM, 0.05), 3),
        };
        if (withPlanets)
        {
            bodies.AddRange([
                Planet("mercury", 0.387, 3.3011e23, 2439.7, 1),
                Planet("venus", 0.723, 4.8675e24, 6051.8, 2),
                Planet("mars", 1.524, 6.4171e23, 3389.5, 3),
                Planet("jupiter", 5.2028, 1.898e27, 69911, 1),
                Planet("saturn", 9.537, 5.683e26, 58232, 2),
                Planet("uranus", 19.19, 8.681e25, 25362, 3),
                Planet("neptune", 30.07, 1.024e26, 24622, 0),
            ]);
        }
        return AtRest(bodies);
    }

    public static Universe AtRest(List<Body> bodies)
    {
        var u = new Universe(2451545);
        u.SetBodies(bodies);
        var b = u.Barycentre();
        foreach (var x in u.Bodies)
        {
            x.Pos -= b.Pos;
            x.Vel -= b.Vel;
        }
        return u;
    }
}
