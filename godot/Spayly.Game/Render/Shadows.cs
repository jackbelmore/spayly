using System;
using Godot;
using Spayly.Sim;

namespace Spayly.Game.Render;

/// <summary>Eclipse casters for the body shaders; port of occludersFor() in src/render/viewContext.ts.</summary>
public static class Shadows
{
    public const int MaxOccluders = 4;

    /// <summary>
    /// The up to four nearest bodies that could shadow <paramref name="body"/> (not stars, not under 50 km, within
    /// 300 of their radii plus 50 of this body's), written to <paramref name="output"/> as (centre, radius) in units of the
    /// body's true radius in its geometry frame. <paramref name="invQ"/> takes the render frame to that frame. Returns the
    /// count. Allocation-free: a four-slot insertion sort instead of the web's sorted candidate list.
    /// </summary>
    public static int OccludersFor(ViewContext ctx, Body body, Orientation.DQuat invQ, Vector4[] output)
    {
        Span<double> bestD = stackalloc double[MaxOccluders];
        Span<int> bestI = stackalloc int[MaxOccluders];
        var n = 0;
        var bodies = ctx.Bodies;
        for (var i = 0; i < bodies.Count; i++)
        {
            var o = bodies[i];
            if (ReferenceEquals(o, body) || o.Kind == BodyKind.Star || o.Radius < 50) continue;
            var d = Vec3.Length(o.Pos - body.Pos);
            if (d > 300 * o.Radius + 50 * body.Radius) continue;
            // Insert keeping ascending distance; ties keep list order, as the web's stable sort does.
            var at = n;
            while (at > 0 && bestD[at - 1] > d) at--;
            if (at >= MaxOccluders) continue;
            for (var k = System.Math.Min(n, MaxOccluders - 1); k > at; k--)
            {
                bestD[k] = bestD[k - 1];
                bestI[k] = bestI[k - 1];
            }
            bestD[at] = d;
            bestI[at] = i;
            if (n < MaxOccluders) n++;
        }
        var inv = 1 / body.Radius;
        for (var k = 0; k < n; k++)
        {
            var o = bodies[bestI[k]];
            var rel = o.Pos - body.Pos;
            // Sim → render axes, then into the body's geometry frame.
            var p = invQ.Rotate(new Vec3(rel.X, rel.Z, -rel.Y));
            output[k] = new Vector4((float)(p.X * inv), (float)(p.Y * inv), (float)(p.Z * inv), (float)(o.Radius * inv));
        }
        return n;
    }
}
