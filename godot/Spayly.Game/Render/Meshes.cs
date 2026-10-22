using System;
using System.Collections.Generic;
using Godot;
using Spayly.Sim;

namespace Spayly.Game.Render;

/// <summary>Meshes shared by every view of a kind; views scale them through their bases.</summary>
public static class Meshes
{
    /// <summary>Planets, moons and the Sun: fine enough that a terminator on a full-screen disc does not facet.</summary>
    public static readonly SphereMesh Sphere = new() { Radius = 1, Height = 2, RadialSegments = 128, Rings = 64 };
    /// <summary>Unused by the views since the displaced shapes (<see cref="RockShape"/>); kept for anything that wants a cheap ball.</summary>
    public static readonly SphereMesh Rock = new() { Radius = 1, Height = 2, RadialSegments = 32, Rings = 16 };
    /// <summary>Unit quad for glows and the corona; faces +z, which a camera basis turns toward the viewer.</summary>
    public static readonly QuadMesh Quad = new() { Size = new Vector2(1, 1) };

    static readonly Dictionary<(double, double), ArrayMesh> Rings = [];

    /// <summary>
    /// Flat annulus in the xz plane (the body's equator; pole +y) between <paramref name="inner"/> and <paramref name="outer"/>
    /// body radii: three's RingGeometry(inner, outer, 256, 2) rotated into the equator, as planetView.ts builds it.
    /// </summary>
    public static ArrayMesh Ring(double inner, double outer)
    {
        if (Rings.TryGetValue((inner, outer), out var m)) return m;
        const int seg = 256, rad = 2;
        var verts = new Vector3[(seg + 1) * (rad + 1)];
        for (var j = 0; j <= rad; j++)
        {
            var r = (float)(inner + (outer - inner) * j / rad);
            for (var i = 0; i <= seg; i++)
            {
                var a = i * Mathf.Tau / seg;
                verts[j * (seg + 1) + i] = new Vector3(r * Mathf.Cos(a), 0, -r * Mathf.Sin(a));
            }
        }
        var idx = new int[seg * rad * 6];
        var n = 0;
        for (var j = 0; j < rad; j++)
        {
            for (var i = 0; i < seg; i++)
            {
                int a = j * (seg + 1) + i, b = a + 1, c = a + seg + 1, d = c + 1;
                idx[n++] = a; idx[n++] = c; idx[n++] = b;
                idx[n++] = b; idx[n++] = c; idx[n++] = d;
            }
        }
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = verts;
        arrays[(int)Mesh.ArrayType.Index] = idx;
        m = new ArrayMesh();
        m.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        // The ring is flat; its AABB would be zero-height and cull at grazing views.
        m.CustomAabb = new Aabb(new Vector3((float)-outer, -0.01f, (float)-outer), new Vector3((float)(2 * outer), 0.02f, (float)(2 * outer)));
        Rings[(inner, outer)] = m;
        return m;
    }

    // ---------- rocks (rockView.ts) ----------

    /// <summary>
    /// Subdivision of the icosphere: (n + 1)^2 triangles on each of its 20 faces, so 30 gives about 19k triangles and facets
    /// under 2 degrees across; at 4 a rock filling the screen had a visibly polygonal outline.
    /// </summary>
    const int RockDetail = 30;
    static Vector3[]? weldedDirs;
    static int[]? weldedIndex;
    static readonly ArrayMesh?[] RockCache = new ArrayMesh?[16];

    sealed record RockArrays(Vector3[] Pos, Vector3[] Nrm, Vector2[] Uv, Vector2[] Uv2);
    static System.Threading.Tasks.Task<RockArrays[]>? rockWarm;

    /// <summary>A shatter can spawn a dozen rocks at once; sixteen shared shapes beats building one per body.</summary>
    public static ArrayMesh RockShape(int seed)
    {
        var key = ((seed % 16) + 16) % 16;
        if (RockCache[key] is { } m) return m;
        var arrays = rockWarm is { IsCompletedSuccessfully: true } w ? w.Result[key] : RockVertices(key * 7919 + 13);
        return RockCache[key] = MeshFrom(arrays);
    }

    /// <summary>
    /// Displace the sixteen shared shapes on a worker at load, so a new rock (a hero collision's proxy, a shatter's
    /// fragments) only uploads its mesh: displacing one on the main thread cost 14–19 ms of the SPH start frame (capture
    /// m7-perf-sph-prof). The same pure function either way, so the shapes do not depend on which ran.
    /// </summary>
    public static void WarmRocks()
    {
        WeldedSphere();
        rockWarm ??= System.Threading.Tasks.Task.Run(() =>
        {
            var all = new RockArrays[RockCache.Length];
            for (var k = 0; k < all.Length; k++) all[k] = RockVertices(k * 7919 + 13);
            return all;
        });
    }

    /// <summary>
    /// three's IcosahedronGeometry(1, 30) welded into one indexed mesh (mergeVertices at 1e-4), so shared vertices move
    /// together and normals average across faces. The weld drops UVs, which differ across the seam; the shader maps the
    /// texture from the undisplaced direction instead.
    /// </summary>
    static void WeldedSphere()
    {
        if (weldedDirs != null) return;
        var t = (1 + Math.Sqrt(5)) / 2;
        double[] v =
        [
            -1, t, 0, 1, t, 0, -1, -t, 0, 1, -t, 0, 0, -1, t, 0, 1, t, 0, -1, -t, 0, 1, -t, t, 0, -1, t, 0, 1, -t, 0, -1, -t, 0, 1,
        ];
        int[] faces =
        [
            0, 11, 5, 0, 5, 1, 0, 1, 7, 0, 7, 10, 0, 10, 11, 1, 5, 9, 5, 11, 4, 11, 10, 2, 10, 7, 6, 7, 1, 8,
            3, 9, 4, 3, 4, 2, 3, 2, 6, 3, 6, 8, 3, 8, 9, 4, 9, 5, 2, 4, 11, 6, 2, 10, 8, 6, 7, 9, 8, 1,
        ];
        Vec3 P(int i) => new(v[i * 3], v[i * 3 + 1], v[i * 3 + 2]);
        var dirs = new List<Vector3>();
        var index = new List<int>();
        var weld = new Dictionary<(long, long, long), int>();
        const int cols = RockDetail + 1;
        var grid = new Vec3[cols + 1][];
        void Emit(Vec3 p)
        {
            var d = Vec3.Normalize(p);
            var k = ((long)Math.Round(d.X * 1e4), (long)Math.Round(d.Y * 1e4), (long)Math.Round(d.Z * 1e4));
            if (!weld.TryGetValue(k, out var id))
            {
                id = dirs.Count;
                weld[k] = id;
                dirs.Add(new Vector3((float)d.X, (float)d.Y, (float)d.Z));
            }
            index.Add(id);
        }
        for (var f = 0; f < 20; f++)
        {
            Vec3 a = P(faces[f * 3]), b = P(faces[f * 3 + 1]), c = P(faces[f * 3 + 2]);
            // PolyhedronGeometry.subdivideFace: rows lerped from a→c and b→c, then across.
            for (var i = 0; i <= cols; i++)
            {
                var aj = Vec3.Lerp(a, c, (double)i / cols);
                var bj = Vec3.Lerp(b, c, (double)i / cols);
                var rows = cols - i;
                grid[i] = new Vec3[rows + 1];
                for (var j = 0; j <= rows; j++) grid[i][j] = j == 0 && i == cols ? aj : Vec3.Lerp(aj, bj, (double)j / rows);
            }
            for (var i = 0; i < cols; i++)
            {
                for (var j = 0; j < 2 * (cols - i) - 1; j++)
                {
                    var k = j / 2;
                    // three's counter-clockwise triangle, emitted clockwise because Godot's front faces are clockwise.
                    if (j % 2 == 0) { Emit(grid[i][k + 1]); Emit(grid[i][k]); Emit(grid[i + 1][k]); }
                    else { Emit(grid[i][k + 1]); Emit(grid[i + 1][k]); Emit(grid[i + 1][k + 1]); }
                }
            }
        }
        weldedDirs = [.. dirs];
        weldedIndex = [.. index];
    }

    /// <summary>A lumpy potato: the welded icosphere displaced by low-frequency noise (rockView.ts rockGeometry).</summary>
    public static ArrayMesh RockGeometry(int seed) => MeshFrom(RockVertices(seed));

    static RockArrays RockVertices(int seed)
    {
        WeldedSphere();
        var dirs = weldedDirs!;
        var index = weldedIndex!;
        var rand = Rng.Mulberry32(unchecked((uint)seed));
        var ex = 0.85 + rand() * 0.5;
        var ez = 0.75 + rand() * 0.4;
        var pos = new Vector3[dirs.Length];
        var uv = new Vector2[dirs.Length];
        var uv2 = new Vector2[dirs.Length];
        for (var i = 0; i < dirs.Length; i++)
        {
            var d = dirs[i].Normalized();
            // Low frequencies only: the lumps should be many facets wide, or neighbouring vertices swing in and out and crease.
            var n = Noise.Fbm3(d.X * 1.8 + 3.1, d.Y * 1.8, d.Z * 1.8, seed) - 0.5;
            var m = Noise.Fbm3(d.X * 4 + 0.7, d.Y * 4 + 1.3, d.Z * 4, seed + 7, 3) - 0.5;
            var r = 1 + 0.42 * n + 0.1 * m;
            pos[i] = new Vector3((float)(d.X * r * ex), (float)(d.Y * r), (float)(d.Z * r * ez));
            uv[i] = new Vector2(d.X, d.Y);
            uv2[i] = new Vector2(d.Z, 0);
        }
        // computeVertexNormals: area-weighted face normals summed per vertex. The index is clockwise here, so the cross
        // product is taken the other way round to stay outward.
        var nrm = new Vector3[dirs.Length];
        for (var f = 0; f < index.Length; f += 3)
        {
            int a = index[f], c = index[f + 1], b = index[f + 2];
            var fn = (pos[c] - pos[b]).Cross(pos[a] - pos[b]);
            nrm[a] += fn;
            nrm[b] += fn;
            nrm[c] += fn;
        }
        for (var i = 0; i < nrm.Length; i++) nrm[i] = nrm[i].Normalized();
        return new RockArrays(pos, nrm, uv, uv2);
    }

    static ArrayMesh MeshFrom(RockArrays r)
    {
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = r.Pos;
        arrays[(int)Mesh.ArrayType.Normal] = r.Nrm;
        arrays[(int)Mesh.ArrayType.TexUV] = r.Uv;
        arrays[(int)Mesh.ArrayType.TexUV2] = r.Uv2;
        arrays[(int)Mesh.ArrayType.Index] = weldedIndex!;
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        return mesh;
    }
}
