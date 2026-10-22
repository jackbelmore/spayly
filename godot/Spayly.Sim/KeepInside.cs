namespace Spayly.Sim;

/// <summary>
/// Which bodies a "keep inside" sphere leaves standing. Everything outside is deleted so the CPU only simulates what the
/// player wants to watch, but a body is only on a correct orbit while the thing that holds it is still there, so the sphere
/// also keeps the central star and, for every kept body, the body that holds it (its catalogue primary: a moon's planet, a planet's
/// star; for a rock or fragment whichever body pulls hardest, e.g. a black hole it circles) even when that body lies outside. Those survive as gravity sources and are
/// reported separately so the player is told about them before they confirm.
/// </summary>
public sealed class KeepPlan
{
    /// <summary>Inside the sphere (centre within the radius), the star excluded when it lies outside.</summary>
    public readonly List<Body> Inside = [];
    /// <summary>Kept although outside, because an inside body depends on its gravity, the star first.</summary>
    public readonly List<Body> GravitySources = [];
    public readonly List<Body> Remove = [];

    public int Kept => Inside.Count + GravitySources.Count;

    public void Clear()
    {
        Inside.Clear();
        GravitySources.Clear();
        Remove.Clear();
    }
}

public static class KeepInside
{
    /// <summary>The central star: the body named "sun", else the first star, else the most massive body.</summary>
    public static Body? Star(Universe u)
    {
        if (u.ById("sun") is { } sun) return sun;
        Body? heaviest = null;
        foreach (var b in u.Bodies)
        {
            if (b.Kind == BodyKind.Star) return b;
            if (heaviest == null || b.Mass > heaviest.Mass) heaviest = b;
        }
        return heaviest;
    }

    public static void Plan(Universe u, Vec3 centre, double radiusKm, KeepPlan plan)
    {
        plan.Clear();
        var star = Star(u);
        var r2 = radiusKm * radiusKm;
        foreach (var b in u.Bodies)
        {
            var d = b.Pos - centre;
            if (d.X * d.X + d.Y * d.Y + d.Z * d.Z <= r2) plan.Inside.Add(b);
        }
        if (star != null && !plan.Inside.Contains(star)) plan.GravitySources.Add(star);
        // Whoever holds a kept body must stay: moon -> planet -> star settles in two rounds, and the loop runs until nothing new joins.
        for (var i = 0; i < plan.Inside.Count + plan.GravitySources.Count; i++)
        {
            var b = i < plan.Inside.Count ? plan.Inside[i] : plan.GravitySources[i - plan.Inside.Count];
            if (ReferenceEquals(b, star)) continue;
            // The catalogue's own hierarchy where it has one (the Sun out-pulls Earth on the Moon, so the strongest pull is
            // not what holds it), the strongest pull for what has none (a rock, a fragment, a body that circles a hole).
            var holder = (b.PrimaryId != null ? u.ById(b.PrimaryId) : null) ?? u.DominantBody(b.Pos, b);
            if (holder == null) continue;
            if (plan.Inside.Contains(holder) || plan.GravitySources.Contains(holder)) continue;
            plan.GravitySources.Add(holder);
        }
        foreach (var b in u.Bodies)
            if (!plan.Inside.Contains(b) && !plan.GravitySources.Contains(b)) plan.Remove.Add(b);
    }

    /// <summary>
    /// Mark the test particles (the belts) outside the sphere dead: they are massless and only drawn, but they cost a force
    /// evaluation each every step. Returns how many this killed; <see cref="TestParticles.Alive"/> is the caller's to save.
    /// </summary>
    public static int CullParticles(TestParticles p, Vec3 centre, double radiusKm)
    {
        var r2 = radiusKm * radiusKm;
        var killed = 0;
        for (var i = 0; i < p.Count; i++)
        {
            if (p.Alive[i] == 0) continue;
            double dx = p.Pos[i * 3] - centre.X, dy = p.Pos[i * 3 + 1] - centre.Y, dz = p.Pos[i * 3 + 2] - centre.Z;
            if (dx * dx + dy * dy + dz * dz <= r2) continue;
            p.Alive[i] = 0;
            killed++;
        }
        return killed;
    }
}
