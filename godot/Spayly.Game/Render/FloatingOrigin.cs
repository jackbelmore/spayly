using System;
using Godot;
using Spayly.Sim;

namespace Spayly.Game.Render;

/// <summary>
/// The camera's position in double km and the one place a sim position becomes a float render offset from it (spike
/// M1c: a 10 m cube on the moving Moon holds to 0.0004 px this way, 460 px without).
///
/// It also owns the depth squeeze. The depth range may span at most near × 1e6 (a larger ratio blacks out the frame, M1c),
/// yet from 50 km above the Moon the Sun is 1.5e8 km away. Offsets farther than <see cref="SqueezeStart"/> are pulled in
/// along their own ray and their radius shrinks by the same factor, so direction and angular size — everything the
/// projection shows — are exactly preserved and only the depth ordering is compressed (monotonically). Render-only: the
/// sim never sees it.
/// </summary>
public sealed class FloatingOrigin
{
    /// <summary>Camera position, km, sim frame.</summary>
    public Vec3 Origin;

    /// <summary>Render distance (units) past which offsets are squeezed. Infinity turns the squeeze off.</summary>
    public double SqueezeStart = double.PositiveInfinity;

    /// <summary>False for the control run that shows what the squeeze fixes (far bodies clipped by the far plane).</summary>
    public bool SqueezeEnabled = true;

    /// <summary>
    /// Squeezed distance for a true render distance, units. Logarithmic past the start: 1e30 times the start lands at
    /// 7.9 times it, so everything fits under a far plane ten times the start.
    /// </summary>
    public double Squeeze(double d)
    {
        var s = SqueezeStart;
        return d <= s ? d : s * (1 + 0.1 * Math.Log(d / s));
    }

    /// <summary>
    /// Camera-relative render position of a sim point (km) after the squeeze, the factor applied (1 when unsqueezed; scale
    /// radii by it) and the true, unsqueezed render distance in units.
    /// </summary>
    public Vector3 Place(Vec3 pos, out double k, out double trueDist)
    {
        var x = (pos.X - Origin.X) * RenderUnits.Scale;
        var y = (pos.Z - Origin.Z) * RenderUnits.Scale;
        var z = -(pos.Y - Origin.Y) * RenderUnits.Scale;
        var d = Math.Sqrt(x * x + y * y + z * z);
        trueDist = d;
        k = d > SqueezeStart ? Squeeze(d) / d : 1;
        return new Vector3((float)(x * k), (float)(y * k), (float)(z * k));
    }

    /// <summary>As <see cref="Place(Vec3, out double, out double)"/> for a point given relative to <paramref name="frame"/> (km), e.g. a conic point relative to its primary.</summary>
    public Vector3 PlaceRelative(Vec3 rel, Vec3 frame)
    {
        var x = ((frame.X - Origin.X) + rel.X) * RenderUnits.Scale;
        var y = ((frame.Z - Origin.Z) + rel.Z) * RenderUnits.Scale;
        var z = -((frame.Y - Origin.Y) + rel.Y) * RenderUnits.Scale;
        var d = Math.Sqrt(x * x + y * y + z * z);
        var k = d > SqueezeStart ? Squeeze(d) / d : 1;
        return new Vector3((float)(x * k), (float)(y * k), (float)(z * k));
    }

    /// <summary>Unsqueezed camera-relative render position; for lights, whose direction to every surface must stay true.</summary>
    public Vector3 PlaceTrue(Vec3 pos) => RenderUnits.ToRender(pos, Origin);
}
