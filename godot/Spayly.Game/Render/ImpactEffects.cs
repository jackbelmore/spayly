using System;
using System.Collections.Generic;
using Godot;
using Spayly.Sim;

namespace Spayly.Game.Render;

/// <summary>
/// Flash, fireball, blast ring, ballistic ejecta, shatter debris and meteor streaks; port of src/render/effects.ts.
///
/// Each effect hangs off an anchor node that copies the struck body's view transform every frame (its position and
/// orientation × on-screen radius, exactly what the view gives its surface mesh), so everything under it is laid out in
/// unit radii in the body's geometry frame and rotates with the surface. The anchor is not a child of the view because
/// undo and fault recovery free every view; an effect whose view is gone retires, as the web's does when its parent group
/// leaves the scene.
///
/// Spawning allocates (meshes, materials); a frame does not: uniforms go through cached StringNames and Variants of
/// value types, and the effect list is a fixed array.
/// </summary>
public partial class ImpactEffects : Node3D
{
    /// <summary>
    /// Most effects alive at once. A shatter's fragments go on hitting each other and every hit spawns one; uncapped, a cascade
    /// stacks dozens of point clouds and materials. The oldest makes way.
    /// </summary>
    public const int MaxEffects = 8;
    /// <summary>Real seconds an effect lives.</summary>
    const double Lifetime = 13;

    public static readonly Shader FlashShader = GD.Load<Shader>("res://Shaders/flash.gdshader");
    static readonly Shader FireballShader = GD.Load<Shader>("res://Shaders/fireball.gdshader");
    static readonly Shader EjectaShader = GD.Load<Shader>("res://Shaders/ejecta.gdshader");
    /// <summary>Unit quad for billboards; the node's scale is the full width.</summary>
    public static readonly QuadMesh Quad = new() { Size = Vector2.One };
    static readonly SphereMesh FireMesh = new() { Radius = 1, Height = 2, RadialSegments = 48, Rings = 24 };
    static readonly ArrayMesh RingMesh = BuildRing(0.86f, 1, 64);

    static readonly StringName PMode = "mode", PColor = "color", PColorB = "color_b", POpacity = "opacity", PPower = "power", POcclude = "occlude";
    static readonly StringName PTNorm = "t_norm", PTime = "u_time", PSeed = "seed";
    static readonly StringName PT = "u_t", PG = "u_g", POrigin = "u_origin", PPointScale = "u_point_scale", PEjMode = "u_mode", PLife = "u_life";

    sealed class Effect
    {
        public required Node3D Anchor;
        public required BodyView View;
        public required string BodyId;
        public double T;
        public Vector3 Dir;
        /// <summary>Fireball radius, body radii.</summary>
        public double Rf;
        /// <summary>Burst altitude, body radii.</summary>
        public double Altitude;
        public MeshInstance3D Flash = null!;
        public required ShaderMaterial FlashMat;
        public MeshInstance3D? Fire;
        public ShaderMaterial? FireMat;
        public double FireDur;
        public MeshInstance3D? Ring;
        public ShaderMaterial? RingMat;
        public Basis RingRot;
        public double RingStrength;
        public MeshInstance3D? Ejecta;
        public ShaderMaterial? EjectaMat;
        public double EjectaTimeScale;
        public MeshInstance3D? Debris;
        public ShaderMaterial? DebrisMat;
        public MeshInstance3D? Streak;
        public ShaderMaterial? StreakMat;
        public readonly List<IDisposable> Owned = [];
    }

    readonly Effect?[] active = new Effect?[MaxEffects];
    int count;

    public int Count => count;

    public ImpactEffects() => Name = "ImpactEffects";

    static double Smooth(double x)
    {
        var t = Math.Clamp(x, 0, 1);
        return t * t * (3 - 2 * t);
    }

    /// <summary>
    /// Start the effects for one resolved collision on <paramref name="view"/>'s body. <paramref name="velDirBody"/> is the
    /// impactor's direction of travel in the body's geometry frame (for the meteor streak).
    /// </summary>
    public void Spawn(ImpactOutcome o, BodyView view, Vec3? velDirBody, ViewContext ctx)
    {
        while (count >= MaxEffects) Retire(0);
        // Thin the particle clouds while others still play, so a cascade costs about a couple of lone impacts.
        var density = 1.0 / (1 + count / 2.0);
        var body = o.Target;
        var rm = body.Radius * 1000;
        var report = o.Report;
        var e = report?.EnergyDeposited ?? o.Energy;
        var rf = Math.Min(report is ImpactReport rep0 ? rep0.FireballRadius / rm : 0.002 * Math.Cbrt(Math.Max(e, 1)) / rm, 0.35);
        var altitude = o.Kind == OutcomeKind.Airburst && report is ImpactReport rep1 ? rep1.Entry.BurstAltitude / rm : 0;
        var rand = Rng.Mulberry32(unchecked((uint)(body.Seed ^ (int)(long)Math.Floor(o.Time))));
        var dir = SiteDir(o.DirBody, velDirBody, view);

        var anchor = new Node3D { Name = "impact-" + body.Id };
        AddChild(anchor);
        var fx = new Effect
        {
            Anchor = anchor, View = view, BodyId = body.Id, Dir = dir, Rf = rf, Altitude = altitude,
            FlashMat = new ShaderMaterial { Shader = FlashShader },
        };

        // Flash: the web's colour is set once at spawn against that frame's exposure.
        fx.FlashMat.SetShaderParameter(PMode, 0);
        fx.FlashMat.SetShaderParameter(PPower, 2.5f);
        fx.FlashMat.SetShaderParameter(PColor, new Vector3(1, 0.92f, 0.78f) * (float)(6 * ctx.ExposureComp));
        fx.Flash = Instance(fx.FlashMat, Quad, 0);
        fx.Flash.Position = dir * (float)(1.003 + altitude);
        anchor.AddChild(fx.Flash);
        fx.Owned.Add(fx.FlashMat);

        if (o.Kind != OutcomeKind.Absorb)
        {
            fx.FireDur = Math.Clamp(3 + Math.Log10(Math.Max(e, 1) / 1e15), 3, 9);
            fx.FireMat = new ShaderMaterial { Shader = FireballShader };
            fx.FireMat.SetShaderParameter(PSeed, (float)(rand() * 10));
            fx.Fire = Instance(fx.FireMat, FireMesh, 5);
            anchor.AddChild(fx.Fire);
            fx.Owned.Add(fx.FireMat);
        }

        if (o.Kind is OutcomeKind.Crater or OutcomeKind.Merge or OutcomeKind.Airburst or OutcomeKind.Gas)
        {
            fx.RingMat = new ShaderMaterial { Shader = FlashShader };
            fx.RingMat.SetShaderParameter(PMode, 1);
            fx.RingMat.SetShaderParameter(PColor, new Vector3(2.5f, 1.8f, 1.2f));
            fx.RingMat.SetShaderParameter(POcclude, true);
            fx.Ring = Instance(fx.RingMat, RingMesh, 5);
            fx.RingRot = new Basis(new Quaternion(Vector3.Back, dir));
            fx.RingStrength = body.Atmosphere != null ? 0.9 : 0.35;
            anchor.AddChild(fx.Ring);
            fx.Owned.Add(fx.RingMat);
        }

        if (o.Kind is OutcomeKind.Crater or OutcomeKind.Merge && report?.Crater is { } crater)
        {
            var n = Math.Max(400, (int)Math.Round(3500 * density));
            var g = Body.SurfaceGravity(body);
            var rc = crater.FinalDiameter / 2;
            var vmax = Math.Sqrt(10 * rc * g);
            var vLocal = vmax / rm;
            var t1 = Math.Abs(dir.X) > 0.9f ? new Vector3(0, 1, 0) : new Vector3(1, 0, 0);
            t1 = t1.Cross(dir).Normalized();
            var t2 = dir.Cross(t1).Normalized();
            var dirs = new Vector3[n];
            var attrs = new Vector2[n];
            for (var i = 0; i < n; i++)
            {
                var theta = (28 + 34 * rand()) * (Math.PI / 180);
                var az = rand() * Math.PI * 2;
                dirs[i] = dir * (float)Math.Cos(theta) + t1 * (float)(Math.Sin(theta) * Math.Cos(az)) + t2 * (float)(Math.Sin(theta) * Math.Sin(az));
                attrs[i] = new Vector2((float)(vLocal * (0.25 + 0.75 * Math.Pow(rand(), 0.7))), (float)rand());
            }
            var mesh = PointMesh(dirs, attrs);
            var tFlight = 2 * vmax * Math.Sin(1.05) / g;
            fx.EjectaTimeScale = tFlight / 6;
            fx.EjectaMat = new ShaderMaterial { Shader = EjectaShader };
            fx.EjectaMat.SetShaderParameter(PEjMode, 0);
            fx.EjectaMat.SetShaderParameter(PG, (float)(g / rm));
            fx.EjectaMat.SetShaderParameter(POrigin, dir);
            fx.EjectaMat.SetShaderParameter(PLife, 1f);
            fx.Ejecta = Instance(fx.EjectaMat, mesh, 6);
            anchor.AddChild(fx.Ejecta);
            fx.Owned.Add(fx.EjectaMat);
            fx.Owned.Add(mesh);
        }

        if (o.Kind is OutcomeKind.Shatter or OutcomeKind.Disperse)
        {
            var n = Math.Max(600, (int)Math.Round(6000 * density));
            var dirs = new Vector3[n];
            var attrs = new Vector2[n];
            for (var i = 0; i < n; i++)
            {
                var z = rand() * 2 - 1;
                var th = rand() * Math.PI * 2;
                var r = Math.Sqrt(1 - z * z);
                dirs[i] = new Vector3((float)(r * Math.Cos(th)), (float)(r * Math.Sin(th)), (float)z);
                attrs[i] = new Vector2((float)(0.25 + 1.4 * Math.Pow(rand(), 2)), (float)rand());
            }
            var mesh = PointMesh(dirs, attrs);
            fx.DebrisMat = new ShaderMaterial { Shader = EjectaShader };
            fx.DebrisMat.SetShaderParameter(PEjMode, 1);
            fx.DebrisMat.SetShaderParameter(PG, 1f);
            fx.DebrisMat.SetShaderParameter(POrigin, Vector3.Zero);
            fx.DebrisMat.SetShaderParameter(PLife, 12f);
            fx.Debris = Instance(fx.DebrisMat, mesh, 6);
            anchor.AddChild(fx.Debris);
            fx.Owned.Add(fx.DebrisMat);
            fx.Owned.Add(mesh);
        }

        if (velDirBody is Vec3 vb && Unit(vb) is Vector3 v && report != null && body.Atmosphere != null && o.Kind is OutcomeKind.Airburst or OutcomeKind.Crater)
        {
            var top = 1 + 130e3 / rm;
            double dv = dir.Dot(v);
            var l = dv + Math.Sqrt(Math.Max(dv * dv + top * top - 1, 0));
            var start = dir - v * (float)l;
            var endR = 1 + altitude;
            var lb = dv + Math.Sqrt(Math.Max(dv * dv + endR * endR - 1, 0));
            var end = dir - v * (float)lb;
            var arrays = new Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = new[] { start, end };
            arrays[(int)Mesh.ArrayType.TexUV] = new[] { new Vector2(0, 0), new Vector2(1, 0) };
            var mesh = new ArrayMesh();
            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Lines, arrays);
            fx.StreakMat = new ShaderMaterial { Shader = FlashShader };
            fx.StreakMat.SetShaderParameter(PMode, 2);
            fx.StreakMat.SetShaderParameter(PColorB, new Vector3(0.6f, 0.6f, 0.7f));
            fx.StreakMat.SetShaderParameter(PColor, new Vector3(6, 5, 3.5f));
            fx.StreakMat.SetShaderParameter(POcclude, true);
            fx.Streak = Instance(fx.StreakMat, mesh, 5);
            anchor.AddChild(fx.Streak);
            fx.Owned.Add(fx.StreakMat);
            fx.Owned.Add(mesh);
        }

        active[count++] = fx;
        Follow(fx);
        Tick(fx, ctx);
    }

    /// <summary><paramref name="d"/> as a float unit vector, or null when it has no direction (zero or not finite).</summary>
    static Vector3? Unit(Vec3 d)
    {
        var v = new Vector3((float)d.X, (float)d.Y, (float)d.Z).Normalized();
        return v.IsFinite() && v.LengthSquared() > 0.5f ? v : null;
    }

    /// <summary>
    /// The impact site in the body's geometry frame. A coincident pair has none: three rocks fired from one paused viewpoint
    /// spawn at the same point and merge with zero separation and zero relative velocity, so Collide's normal, DirBody and
    /// velocity direction are all zero, and <c>Quaternion(Back, 0)</c> threw "The vectors must not be zero" (M6 run). Then
    /// the site is the face the impactor came from, else the face towards the camera, so the flash is seen; else +Z.
    /// </summary>
    static Vector3 SiteDir(Vec3 dirBody, Vec3? velDirBody, BodyView view)
    {
        if (Unit(dirBody) is Vector3 d) return d;
        if (velDirBody is Vec3 vb && Unit(vb) is Vector3 v) return -v;
        // The view's position is the body's centre from the camera, render frame; its orientation maps geometry → render.
        var toCam = new Basis(view.Orient.ToGodot()).Inverse() * -view.Position;
        return toCam.IsFinite() && toCam.LengthSquared() > 0 ? toCam.Normalized() : Vector3.Back;
    }

    static MeshInstance3D Instance(ShaderMaterial mat, Mesh mesh, int priority)
    {
        mat.RenderPriority = priority;
        return new MeshInstance3D
        {
            Mesh = mesh,
            MaterialOverride = mat,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            // Shaders displace, billboard or fly vertices well outside the mesh's own bounds.
            ExtraCullMargin = 16,
        };
    }

    /// <summary>A point cloud whose vertices are launch directions and whose UV carries (speed, seed); the shader flies them.</summary>
    static ArrayMesh PointMesh(Vector3[] dirs, Vector2[] attrs)
    {
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = dirs;
        arrays[(int)Mesh.ArrayType.TexUV] = attrs;
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Points, arrays);
        // Debris reaches ~20 radii in its 12 s; the cull test must not drop it.
        mesh.CustomAabb = new Aabb(new Vector3(-24, -24, -24), new Vector3(48, 48, 48));
        return mesh;
    }

    /// <summary>Flat annulus in the XY plane facing +Z, three.js RingGeometry(inner, outer, segments).</summary>
    static ArrayMesh BuildRing(float inner, float outer, int segments)
    {
        var verts = new Vector3[segments * 6];
        for (var i = 0; i < segments; i++)
        {
            var a0 = i * Mathf.Tau / segments;
            var a1 = (i + 1) * Mathf.Tau / segments;
            var c0 = new Vector3(Mathf.Cos(a0), Mathf.Sin(a0), 0);
            var c1 = new Vector3(Mathf.Cos(a1), Mathf.Sin(a1), 0);
            var k = i * 6;
            verts[k] = c0 * inner;
            verts[k + 1] = c0 * outer;
            verts[k + 2] = c1 * outer;
            verts[k + 3] = c0 * inner;
            verts[k + 4] = c1 * outer;
            verts[k + 5] = c1 * inner;
        }
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = verts;
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        return mesh;
    }

    /// <summary>
    /// Advance every effect by <paramref name="dtReal"/> real seconds. <paramref name="views"/> is the app's live view map: an
    /// effect whose body view was replaced or freed retires.
    /// </summary>
    public void Update(double dtReal, ViewContext ctx, IReadOnlyDictionary<string, BodyView> views)
    {
        for (var i = count - 1; i >= 0; i--)
        {
            var fx = active[i]!;
            fx.T += dtReal;
            if (fx.T > Lifetime || !views.TryGetValue(fx.BodyId, out var v) || !ReferenceEquals(v, fx.View) || !IsInstanceValid(v))
            {
                Retire(i);
                continue;
            }
            Follow(fx);
            Tick(fx, ctx);
        }
    }

    /// <summary>Copy the view's placement: centre, orientation (geometry frame → render frame) and on-screen radius.</summary>
    static void Follow(Effect fx)
    {
        var v = fx.View;
        var s = (float)(v.VisualRadiusKm * RenderUnits.Scale * v.SqueezeFactor);
        fx.Anchor.Transform = new Transform3D(new Basis(v.Orient.ToGodot()) * Basis.FromScale(new Vector3(s, s, s)), v.Position);
    }

    static void Tick(Effect fx, ViewContext ctx)
    {
        var t = fx.T;
        var v = fx.View;
        var anchorScale = Math.Max(v.VisualRadiusKm * RenderUnits.Scale * v.SqueezeFactor, 1e-12);
        var camDist = (double)v.Position.Length();
        // Minimum on-screen sizes, body radii, re-evaluated every frame: the cinematic camera moves a lot right after impact.
        var pxToRadii = camDist / ctx.PixelsPerRadian / anchorScale;
        var fireR = Math.Max(fx.Rf, 4 * pxToRadii);
        var dir = fx.Dir;

        var a = t < 0.15 ? t / 0.15 : Math.Max(0, 1 - (t - 0.15) / 1.4);
        var flashVisible = a > 0;
        if (fx.Flash.Visible != flashVisible) fx.Flash.Visible = flashVisible;
        if (flashVisible)
        {
            var flashR = Math.Max(fireR * 6, 18 * pxToRadii);
            var fs = (float)(flashR * 2 * (0.4 + 0.6 * Smooth(t / 0.3)));
            fx.Flash.Scale = new Vector3(fs, fs, fs);
            fx.FlashMat.SetShaderParameter(POpacity, (float)a);
        }

        if (fx.Fire != null)
        {
            var tau = Math.Clamp(t / fx.FireDur, 0, 1);
            var on = tau < 1;
            if (fx.Fire.Visible != on) fx.Fire.Visible = on;
            if (on)
            {
                fx.FireMat!.SetShaderParameter(PTNorm, (float)tau);
                fx.FireMat.SetShaderParameter(PTime, (float)(ctx.RealTime % 1000));
                var s = fireR * (0.15 + 0.85 * Smooth(tau / 0.25)) * (1 + 0.8 * tau);
                fx.Fire.Transform = new Transform3D(Basis.FromScale(new Vector3((float)s, (float)s, (float)s)), dir * (float)(1 + fx.Altitude + s * 0.55 + 0.5 * tau * s));
            }
        }

        if (fx.Ring != null)
        {
            var tau = Math.Clamp(t / 4, 0, 1);
            var on = tau < 1;
            if (fx.Ring.Visible != on) fx.Ring.Visible = on;
            if (on)
            {
                var r = (float)Math.Max(fx.Rf * (0.6 + 26 * tau), 3 * pxToRadii);
                fx.Ring.Transform = new Transform3D(fx.RingRot * Basis.FromScale(new Vector3(r, r, r)), dir * 1.002f);
                fx.RingMat!.SetShaderParameter(POpacity, (float)(fx.RingStrength * (1 - tau) * (1 - tau)));
            }
        }

        if (fx.Ejecta != null)
        {
            var on = t < 8;
            if (fx.Ejecta.Visible != on) fx.Ejecta.Visible = on;
            if (on)
            {
                fx.EjectaMat!.SetShaderParameter(PT, (float)(t * fx.EjectaTimeScale));
                fx.EjectaMat.SetShaderParameter(PPointScale, (float)(3 * camDist));
            }
        }

        if (fx.Debris != null)
        {
            fx.DebrisMat!.SetShaderParameter(PT, (float)t);
            fx.DebrisMat.SetShaderParameter(PPointScale, (float)(3.5 * camDist));
        }

        if (fx.Streak != null)
        {
            var on = t < 1.6;
            if (fx.Streak.Visible != on) fx.Streak.Visible = on;
            if (on) fx.StreakMat!.SetShaderParameter(POpacity, (float)Math.Max(0, 1 - t / 1.6));
        }
    }

    void Retire(int i)
    {
        var fx = active[i]!;
        fx.Anchor.QueueFree();
        // The nodes keep their own references until they are freed at the end of the frame; this only drops ours.
        foreach (var d in fx.Owned) d.Dispose();
        for (var k = i; k < count - 1; k++) active[k] = active[k + 1];
        active[--count] = null;
    }

    /// <summary>Drop every effect (undo, fault rollback).</summary>
    public void Clear()
    {
        while (count > 0) Retire(count - 1);
    }
}
