using System;
using System.Threading.Tasks;
using Godot;
using Spayly.Sim;

namespace Spayly.Game.Render;

/// <summary>
/// The Milky Way panorama plus 9000 blackbody-coloured point stars, both centred on the camera; port of src/render/sky.ts.
///
/// The camera sits at the render origin (floating origin), so a node there is camera-anchored for free. Neither mesh has a
/// real radius: the shaders place every vertex at a fixed fraction of the far plane they read back from the projection
/// (Milky Way 0.95, stars 0.9), behind everything the depth squeeze keeps inside 0.79 × far, so however the near/far
/// range swings the sky is never clipped and never in front of a body. The panorama is opaque and writes no depth; the
/// stars are additive and depth-tested, so bodies hide them.
///
/// The map is <c>res://textures/milky_way.jpg</c> (manifest <c>_sky</c>, a plain imported texture) when present, else the
/// web's procedural band (textures.ts proceduralMilkyWay), generated off the main thread.
/// </summary>
public sealed class Sky
{
    const string MapPath = "res://textures/milky_way.jpg";
    const int StarCount = 9000;
    static readonly StringName MapParam = "map", IntensityParam = "intensity", AlphaParam = "alpha", ScaleParam = "point_scale";

    public readonly Godot.Environment Environment;
    /// <summary>Parent of the panorama and the stars; the app adds it at the render origin.</summary>
    public readonly Node3D Node = new() { Name = "Sky" };
    readonly ShaderMaterial milkyMat;
    readonly ShaderMaterial starMat;
    Task<byte[]>? procedural;
    float lastExposureComp = -1;

    public Sky(Godot.Environment env)
    {
        Environment = env;
        env.BackgroundMode = Godot.Environment.BGMode.Color;
        env.BackgroundColor = Colors.Black;
        var huge = new Aabb(new Vector3(-1e15f, -1e15f, -1e15f), new Vector3(2e15f, 2e15f, 2e15f));

        milkyMat = new ShaderMaterial { Shader = GD.Load<Shader>("res://Shaders/sky.gdshader"), RenderPriority = -10 };
        // Galactic pole and centre in the render frame (from their ecliptic coordinates), as sky.ts.
        var pole = new Vector3(-0.8676f, 0.4971f, 0.0003f).Normalized();
        var gc = new Vector3(-0.0556f, -0.0958f, 0.9937f).Normalized();
        var x = (gc - pole * gc.Dot(pole)).Normalized();
        var z = x.Cross(pole);
        milkyMat.SetShaderParameter("axis_x", x);
        milkyMat.SetShaderParameter("axis_y", pole);
        milkyMat.SetShaderParameter("axis_z", z);
        milkyMat.SetShaderParameter(IntensityParam, 0.5f);
        if (ResourceLoader.Exists(MapPath) && ResourceLoader.Load<Texture2D>(MapPath) is Texture2D map)
        {
            milkyMat.SetShaderParameter(MapParam, map);
            GD.Print($"[sky] milky way map {map.GetWidth()}x{map.GetHeight()}");
        }
        else
        {
            procedural = Task.Run(ProceduralMilkyWay);
            GD.Print("[sky] no milky_way.jpg; procedural milky way");
        }
        var sphere = new SphereMesh { Radius = 1, Height = 2, RadialSegments = 64, Rings = 32, Material = milkyMat };
        Node.AddChild(new MeshInstance3D { Mesh = sphere, CustomAabb = huge, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });

        var dirs = new Vector3[StarCount];
        var sizes = new Vector2[StarCount];
        var colors = new Color[StarCount];
        var rand = Rng.Mulberry32(42);
        for (var i = 0; i < StarCount; i++)
        {
            var zz = rand() * 2 - 1;
            var t = rand() * Math.PI * 2;
            var r = Math.Sqrt(1 - zz * zz);
            dirs[i] = new Vector3((float)(r * Math.Cos(t)), (float)zz, (float)(r * Math.Sin(t)));
            var size = 0.9 + 2.4 * Math.Pow(rand(), 9);
            size += rand() < 0.003 ? 1.6 : 0;
            sizes[i] = new Vector2((float)size, 0);
            var temp = 3200 + 9000 * Math.Pow(rand(), 2.2);
            var c = RenderUnits.Blackbody(temp) * (float)(0.35 + 0.65 * Math.Pow(rand(), 1.5));
            colors[i] = new Color(c.R, c.G, c.B);
        }
        starMat = new ShaderMaterial { Shader = GD.Load<Shader>("res://Shaders/points.gdshader"), RenderPriority = -9 };
        starMat.SetShaderParameter("far_fraction", 0.9f);
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = dirs;
        arrays[(int)Mesh.ArrayType.Color] = colors;
        arrays[(int)Mesh.ArrayType.TexUV] = sizes;
        var stars = new ArrayMesh();
        stars.AddSurfaceFromArrays(Mesh.PrimitiveType.Points, arrays);
        stars.SurfaceSetMaterial(0, starMat);
        Node.AddChild(new MeshInstance3D { Mesh = stars, CustomAabb = huge, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
    }

    /// <summary>Per-frame: exposure compensation (the web's 0.55 × comp on the panorama, comp on the stars).</summary>
    public void Update(double exposureComp)
    {
        if (procedural is { IsCompleted: true } job)
        {
            procedural = null;
            if (job.IsCompletedSuccessfully)
            {
                var img = Image.CreateFromData(ProcW, ProcH, false, Image.Format.Rgb8, job.Result);
                img.GenerateMipmaps();
                milkyMat.SetShaderParameter(MapParam, ImageTexture.CreateFromImage(img));
            }
            else GD.PushError($"Sky: procedural milky way failed: {job.Exception?.GetBaseException().Message}");
        }
        var ec = (float)exposureComp;
        if (Math.Abs(ec - lastExposureComp) <= 1e-4f * Math.Max(ec, 1e-6f)) return;
        lastExposureComp = ec;
        milkyMat.SetShaderParameter(IntensityParam, 0.55f * ec);
        starMat.SetShaderParameter(AlphaParam, ec);
    }

    const int ProcW = 2048, ProcH = 1024;

    /// <summary>
    /// textures.ts proceduralMilkyWay, bit for bit: a Gaussian band of fbm cloud with fbm dust lanes and sparse hashed stars,
    /// sRGB bytes rounded as a canvas ImageData would (round half to even after the clamp).
    /// </summary>
    static byte[] ProceduralMilkyWay()
    {
        var data = new byte[ProcW * ProcH * 3];
        Parallel.For(0, ProcH, y =>
        {
            var v = (double)y / ProcH;
            var band = Math.Exp(-Math.Pow((v - 0.5) / 0.09, 2));
            for (var x = 0; x < ProcW; x++)
            {
                var u = (double)x / ProcW;
                var i = (y * ProcW + x) * 3;
                double glow = 0;
                // exp(-(0.5/0.09)^2) ≈ 4e-14 at the edges: skip the noise where the band is exactly or nearly nothing.
                if (band > 1e-9)
                {
                    var cloud = Fbm(u * 12, v * 6, 11, 5);
                    var dust = Fbm(u * 30, v * 15, 12, 4);
                    glow = band * (0.15 + 0.85 * cloud) * (1 - 0.6 * Math.Pow(dust, 2)) * 90;
                }
                var star = Hash(x, y, 5) > 0.997 ? 140 : 0;
                data[i] = Byte(Math.Min(255, glow * 0.95 + star));
                data[i + 1] = Byte(Math.Min(255, glow * 0.9 + star));
                data[i + 2] = Byte(Math.Min(255, glow * 1.05 + star));
            }
        });
        return data;
    }

    static byte Byte(double x) => (byte)Math.Clamp(Math.Round(x, MidpointRounding.ToEven), 0, 255);

    // textures.ts hash/vnoise/fbm: JS int32 Math.imul arithmetic, which unchecked uint reproduces bit for bit.
    static double Hash(int ix, int iy, int seed)
    {
        unchecked
        {
            var h = (uint)ix * 374761393u + (uint)iy * 668265263u + (uint)seed * 1442695041u;
            h = (h ^ (h >> 13)) * 1274126177u;
            return (h ^ (h >> 16)) / 4294967296.0;
        }
    }

    static double VNoise(double x, double y, int seed)
    {
        var fx0 = Math.Floor(x);
        var fy0 = Math.Floor(y);
        int ix = (int)fx0, iy = (int)fy0;
        double fx = x - fx0, fy = y - fy0;
        double sx = fx * fx * (3 - 2 * fx), sy = fy * fy * (3 - 2 * fy);
        double a = Hash(ix, iy, seed), b = Hash(ix + 1, iy, seed), c = Hash(ix, iy + 1, seed), d = Hash(ix + 1, iy + 1, seed);
        return a + (b - a) * sx + (c - a) * sy + (a - b - c + d) * sx * sy;
    }

    static double Fbm(double x, double y, int seed, int octaves)
    {
        double v = 0, amp = 0.5, f = 1;
        for (var i = 0; i < octaves; i++)
        {
            v += amp * VNoise(x * f, y * f, seed + i * 17);
            amp *= 0.5;
            f *= 2.03;
        }
        return v;
    }
}
