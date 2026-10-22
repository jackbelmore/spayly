using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Godot;
using Spayly.Sim;

namespace Spayly.Game.Render;

/// <summary>
/// A camera-relative polyline drawn additively, with an optional head-to-tail fade; port of PolyLine in src/render/lines.ts.
/// The vertex buffer is allocated once at <see cref="Capacity"/> and rewritten each frame with one
/// <c>MeshSurfaceUpdateVertexRegion</c> call, so a frame costs one interop call per line instead of one per vertex, and no
/// managed allocation. Unused tail vertices repeat the last point, which draws nothing. The fade the web writes as vertex
/// colours is computed from the vertex index in <c>Shaders/line.gdshader</c>, so only positions are ever uploaded.
/// </summary>
public sealed class PolyLine
{
    static readonly StringName ColorParam = "color", OpacityParam = "opacity", BrightnessParam = "brightness", FadeParam = "fade", CountParam = "count";
    static Shader? shader;

    public readonly int Capacity;
    public readonly MeshInstance3D Node;
    readonly ArrayMesh mesh = new();
    readonly ShaderMaterial material;
    readonly byte[] bytes;
    float lastBrightness = -1, lastFade = -1;
    int lastCount = -1;
    readonly bool ok;

    /// <param name="color">sRGB, as the catalogue's hex colours are.</param>
    public PolyLine(int capacity, Color color, float opacity)
    {
        Capacity = capacity;
        shader ??= GD.Load<Shader>("res://Shaders/line.gdshader");
        material = new ShaderMaterial { Shader = shader };
        var lin = color.SrgbToLinear();
        material.SetShaderParameter(ColorParam, new Vector3(lin.R, lin.G, lin.B));
        material.SetShaderParameter(OpacityParam, opacity);
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = new Vector3[capacity];
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.LineStrip, arrays);
        // Vertices move every frame, so the mesh's own bounds mean nothing; everything drawn is inside the far plane.
        mesh.CustomAabb = new Aabb(new Vector3(-1e12f, -1e12f, -1e12f), new Vector3(2e12f, 2e12f, 2e12f));
        mesh.SurfaceSetMaterial(0, material);
        var stride = (int)RenderingServer.MeshSurfaceGetFormatVertexStride((RenderingServer.ArrayFormat)(long)mesh.SurfaceGetFormat(0), capacity);
        ok = stride == 12;
        if (!ok) GD.PushError($"PolyLine: vertex stride {stride}, expected 12 (float3 positions only); lines disabled");
        bytes = new byte[capacity * 12];
        Node = new MeshInstance3D { Mesh = mesh, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, Visible = false };
    }

    public bool Visible
    {
        get => Node.Visible;
        set => Node.Visible = value;
    }

    /// <summary>
    /// Write points given relative to <paramref name="frame"/> (km, sim frame; e.g. a conic relative to its primary) as
    /// camera-relative, squeezed render positions. <paramref name="brightness"/> scales the additive colour;
    /// <paramref name="fade"/> &gt; 0 darkens toward the first point.
    /// </summary>
    public void Set(List<Vec3> points, Vec3 frame, FloatingOrigin origin, float brightness = 1, float fade = 0)
    {
        var n = Math.Min(points.Count, Capacity);
        for (var i = 0; i < n; i++) Write(i, origin.PlaceRelative(points[i], frame));
        Commit(n, brightness, fade);
    }

    /// <summary>Vertex <paramref name="i"/> as a camera-relative render position; follow with <see cref="Commit"/>.</summary>
    public void Write(int i, Vector3 v)
    {
        var f = MemoryMarshal.Cast<byte, float>(bytes.AsSpan());
        f[i * 3] = v.X;
        f[i * 3 + 1] = v.Y;
        f[i * 3 + 2] = v.Z;
    }

    /// <summary>Upload the first <paramref name="n"/> written vertices (the rest repeat the last one) and show the line.</summary>
    public void Commit(int n, float brightness, float fade)
    {
        n = Math.Min(n, Capacity);
        if (!ok || n < 2)
        {
            Node.Visible = false;
            return;
        }
        var f = MemoryMarshal.Cast<byte, float>(bytes.AsSpan());
        float x = f[(n - 1) * 3], y = f[(n - 1) * 3 + 1], z = f[(n - 1) * 3 + 2];
        for (var i = n; i < Capacity; i++)
        {
            f[i * 3] = x;
            f[i * 3 + 1] = y;
            f[i * 3 + 2] = z;
        }
        RenderingServer.MeshSurfaceUpdateVertexRegion(mesh.GetRid(), 0, 0, bytes);
        if (Math.Abs(brightness - lastBrightness) > 0.004f)
        {
            lastBrightness = brightness;
            material.SetShaderParameter(BrightnessParam, brightness);
        }
        if (fade != lastFade)
        {
            lastFade = fade;
            material.SetShaderParameter(FadeParam, fade);
        }
        if (n != lastCount)
        {
            lastCount = n;
            material.SetShaderParameter(CountParam, n);
        }
        Node.Visible = true;
    }

    public void Clear() => Node.Visible = false;

    public void Dispose()
    {
        Node.QueueFree();
    }
}
