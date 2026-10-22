using System;
using System.Runtime.InteropServices;
using Godot;
using Spayly.Sim;

namespace Spayly.Game.Render;

/// <summary>
/// Point cloud for the main belt, Trojan and Kuiper belt test particles; port of BeltView in src/render/belt.ts. Colours
/// and sizes are static attributes; each frame rewrites only the float3 positions — camera-relative and through the depth
/// squeeze, like every other drawn offset — with one <c>MeshSurfaceUpdateVertexRegion</c> from a reused buffer, so the
/// frame allocates nothing. A swallowed particle is parked at the eye, which the point shader draws at size 0.
/// </summary>
public sealed class Belt
{
    // Linear RGB, as three.js reads new THREE.Color(r, g, b): main belt, Trojans, Kuiper belt.
    static readonly Color[] FamilyColors = [new(0.75f, 0.68f, 0.6f), new(0.65f, 0.68f, 0.78f), new(0.6f, 0.72f, 0.9f)];
    static readonly StringName AlphaParam = "alpha", ScaleParam = "point_scale";

    public readonly TestParticles? Particles;
    /// <summary>The cloud's node; null when there are no particles. The app parents it at the render origin.</summary>
    public readonly MeshInstance3D? Node;
    readonly ArrayMesh? mesh;
    readonly ShaderMaterial? material;
    readonly byte[] bytes = [];
    readonly int n;
    float lastAlpha = -1, lastScale = -1;
    bool visible = true;

    public Belt(TestParticles? particles)
    {
        Particles = particles;
        n = particles?.Count ?? 0;
        if (particles == null || n == 0) return;
        var verts = new Vector3[n];
        var colors = new Color[n];
        var sizes = new Vector2[n];
        for (var i = 0; i < n; i++)
        {
            var family = particles.Family[i];
            var c = family < FamilyColors.Length ? FamilyColors[family] : FamilyColors[0];
            var v = 0.7f + 0.3f * (i * 7919 % 97) / 97f;
            colors[i] = new Color(c.R * v, c.G * v, c.B * v);
            sizes[i] = new Vector2(family == 2 ? 1.6f : 1.4f + 0.6f * (i * 104729 % 89) / 89f, 0);
        }
        material = new ShaderMaterial { Shader = GD.Load<Shader>("res://Shaders/points.gdshader"), RenderPriority = -1 };
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = verts;
        arrays[(int)Mesh.ArrayType.Color] = colors;
        arrays[(int)Mesh.ArrayType.TexUV] = sizes;
        mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Points, arrays);
        mesh.CustomAabb = new Aabb(new Vector3(-1e12f, -1e12f, -1e12f), new Vector3(2e12f, 2e12f, 2e12f));
        mesh.SurfaceSetMaterial(0, material);
        // Colours and UVs live in the attribute stream; the vertex stream must be bare float3 for the raw rewrite below.
        var stride = (int)RenderingServer.MeshSurfaceGetFormatVertexStride((RenderingServer.ArrayFormat)(long)mesh.SurfaceGetFormat(0), n);
        if (stride != 12)
        {
            GD.PushError($"Belt: vertex stride {stride}, expected 12 (float3 positions only); belt disabled");
            return;
        }
        bytes = new byte[n * 12];
        Node = new MeshInstance3D { Mesh = mesh, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
    }

    public bool Visible
    {
        get => visible;
        set
        {
            visible = value;
            if (Node != null) Node.Visible = value;
        }
    }

    /// <summary>Rewrite the positions camera-relative (km → squeezed render units) and follow the exposure.</summary>
    public void Update(ViewContext ctx)
    {
        var p = Particles;
        if (p == null || Node == null || mesh == null || material == null) return;
        var pos = p.Pos;
        var alive = p.Alive;
        var origin = ctx.Origin;
        var f = MemoryMarshal.Cast<byte, float>(bytes.AsSpan());
        var count = Math.Min(n, p.Count);
        for (var i = 0; i < n; i++)
        {
            var ix = i * 3;
            if (i >= count || alive[i] == 0)
            {
                f[ix] = 0;
                f[ix + 1] = 0;
                f[ix + 2] = 0;
                continue;
            }
            var v = origin.Place(new Vec3(pos[ix], pos[ix + 1], pos[ix + 2]), out _, out _);
            f[ix] = v.X;
            f[ix + 1] = v.Y;
            f[ix + 2] = v.Z;
        }
        RenderingServer.MeshSurfaceUpdateVertexRegion(mesh.GetRid(), 0, 0, bytes);
        var a = (float)(0.75 * ctx.ExposureComp);
        if (Math.Abs(a - lastAlpha) > 1e-4f * Math.Max(a, 1e-6f))
        {
            lastAlpha = a;
            material.SetShaderParameter(AlphaParam, a);
        }
        var s = (float)ctx.PixelRatio;
        if (s != lastScale)
        {
            lastScale = s;
            material.SetShaderParameter(ScaleParam, s);
        }
    }
}
