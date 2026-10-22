using System;
using Godot;
using Spayly.Sim;

namespace Spayly.Game.Render;

/// <summary>
/// Ring buffer of past positions drawn as a fading line; port of Trail in src/render/lines.ts. Positions can be kept
/// relative to a moving frame — a moon's trail relative to its planet — so the trail draws the orbit rather than a
/// corkscrew smeared along the planet's own path. The line is made on first draw (<see cref="EnsureLine"/>), because the
/// view map constructs trails before anything knows their colour.
/// </summary>
public sealed class Trail
{
    /// <summary>Samples kept (the web's 360).</summary>
    public const int DefaultCapacity = 360;

    /// <summary>Id of the body stored positions are relative to; "" for the solar-system frame.</summary>
    public string Frame = "";
    /// <summary>Whether the app wants it drawn this frame (setting on, not aliased).</summary>
    public bool Visible;
    /// <summary>Id of the body this trail follows; set with the line, used to sweep trails of vanished bodies.</summary>
    public string? Owner { get; private set; }
    public PolyLine? Line { get; private set; }
    public readonly int Capacity;

    readonly double[] buf;
    int head, count;
    double lastTime = double.NegativeInfinity;
    int lastSign = 1;

    public Trail() : this(DefaultCapacity) { }

    public Trail(int capacity)
    {
        Capacity = capacity;
        buf = new double[capacity * 3];
    }

    /// <summary>Samples held.</summary>
    public int Count => count;

    /// <summary>
    /// Make the line (opacity 0.6, as the web) on first use. It holds one more vertex than the ring so the body's current
    /// position always ends the trail; the web's line is one short and drops it once the ring is full.
    /// Returns true when the line was just made and still needs a parent.
    /// </summary>
    public bool EnsureLine(string owner, Color srgb)
    {
        if (Line != null) return false;
        Owner = owner;
        Line = new PolyLine(Capacity + 1, srgb, 0.6f);
        return true;
    }

    /// <summary>
    /// Record <paramref name="pos"/> (km, in the trail's frame) at <paramref name="simTime"/> (s) unless the last sample is
    /// less than <paramref name="minInterval"/> (s) away. Time running the other way (rewind) starts the trail afresh.
    /// </summary>
    public void Push(Vec3 pos, double simTime, double minInterval)
    {
        var dt = simTime - lastTime;
        var sign = dt < 0 ? -1 : 1;
        if (sign != lastSign && double.IsFinite(lastTime)) Reset();
        lastSign = sign;
        if (Math.Abs(simTime - lastTime) < minInterval) return;
        lastTime = simTime;
        var ix = head * 3;
        buf[ix] = pos.X;
        buf[ix + 1] = pos.Y;
        buf[ix + 2] = pos.Z;
        head = (head + 1) % Capacity;
        count = Math.Min(count + 1, Capacity);
    }

    /// <summary>Switch the frame positions are pushed in; the old history means nothing in the new one.</summary>
    public void UseFrame(string id)
    {
        if (id == Frame) return;
        Frame = id;
        Reset();
    }

    public void Reset()
    {
        head = 0;
        count = 0;
        lastTime = double.NegativeInfinity;
        Line?.Clear();
    }

    /// <summary>
    /// Redraw: the stored samples oldest first, then <paramref name="current"/> (absolute km). For a relative trail
    /// <paramref name="frameAt"/> is where its frame body is now (km); for an absolute one, <see cref="Vec3"/> zero. Sums
    /// stay in double until the camera-relative offset is taken.
    /// </summary>
    public void Update(FloatingOrigin origin, Vec3 current, float brightness, Vec3 frameAt)
    {
        var line = Line;
        if (line == null) return;
        var n = count;
        var start = (head - n + Capacity) % Capacity;
        for (var i = 0; i < n; i++)
        {
            var ix = (start + i) % Capacity * 3;
            line.Write(i, origin.PlaceRelative(new Vec3(buf[ix], buf[ix + 1], buf[ix + 2]), frameAt));
        }
        line.Write(n, origin.Place(current, out _, out _));
        line.Commit(n + 1, brightness, 1.6f);
    }

    public void Hide() => Line?.Clear();

    public void Dispose()
    {
        Line?.Dispose();
        Line = null;
    }
}
