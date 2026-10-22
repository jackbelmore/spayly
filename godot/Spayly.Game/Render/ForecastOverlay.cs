using System;
using System.Collections.Generic;
using Godot;
using Spayly.Sim;

namespace Spayly.Game.Render;

/// <summary>
/// What a placement would do, drawn: the ghost path of the hole or rock the player is about to place, the deflected paths of the
/// bodies it disturbs in a warning colour, and a small marker at each event. Paths are absolute sim positions and are rewritten
/// camera-relative every frame, so they hold at any zoom (the same route as <see cref="AimOverlay"/>); nothing here reads or
/// writes a body.
/// </summary>
public partial class ForecastOverlay : Node3D
{
    public const int MaxAffected = 6;
    public const int MaxMarkers = 8;
    static readonly Vector3 GhostColor = new(0.5f, 1.4f, 3f), WarnColor = new(3f, 1.5f, 0.35f);

    readonly PolyLine ghost = new(Forecast.DefaultMaxPathPoints, new Color("#7fd8ff"), 0.9f);
    readonly PolyLine[] affected = new PolyLine[MaxAffected];
    readonly MeshInstance3D[] markers = new MeshInstance3D[MaxMarkers];

    public ForecastOverlay()
    {
        Name = "ForecastOverlay";
        AddChild(ghost.Node);
        for (var i = 0; i < MaxAffected; i++)
        {
            affected[i] = new PolyLine(Forecast.DefaultMaxPathPoints, new Color("#ffa347"), 0.85f);
            AddChild(affected[i].Node);
        }
        for (var i = 0; i < MaxMarkers; i++)
        {
            markers[i] = AimOverlay.Marker(i == 0 ? WarnColor : GhostColor);
            AddChild(markers[i]);
        }
    }

    /// <summary>
    /// Draw <paramref name="r"/>. <paramref name="dim"/> fades everything while a newer forecast is still being computed, so a
    /// stale path is not mistaken for the current one. Returns where the first event's marker sits in render space, or null.
    /// </summary>
    public Vector3? Show(ForecastResult r, IReadOnlyList<ForecastEvent> marked, ViewContext ctx, float dim)
    {
        var ai = 0;
        foreach (var p in r.Paths)
        {
            if (p.Hypothetical) ghost.Set(p.Points, Vec3.Zero, ctx.Origin, 0.9f * dim);
            else if (ai < MaxAffected) affected[ai++].Set(p.Points, Vec3.Zero, ctx.Origin, 1.1f * dim);
        }
        for (; ai < MaxAffected; ai++) affected[ai].Clear();
        Vector3? first = null;
        for (var i = 0; i < MaxMarkers; i++)
        {
            if (i >= marked.Count)
            {
                markers[i].Visible = false;
                continue;
            }
            AimOverlay.Place(markers[i], marked[i].Pos, i == 0 ? 16 : 11, ctx);
            if (i == 0) first = markers[i].Position;
        }
        return first;
    }

    public void Clear()
    {
        ghost.Clear();
        foreach (var a in affected) a.Clear();
        foreach (var m in markers) m.Visible = false;
    }
}
