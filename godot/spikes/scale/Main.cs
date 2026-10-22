using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Godot;

namespace Spayly.Spikes.Scale;

/// <summary>
/// M1c floating-origin scale spike. Builds the Sun/Earth/Moon/cube/Neptune/marker system, advances their
/// orbits analytically in double precision, and each frame re-bases every node's Position onto
/// (body.pos - camera.pos) / RENDER_SCALE so no Godot Vector3 ever holds an absolute km position.
/// CLI args (after --, alongside CaptureDriver's own --capture-dir/--frames/--shots/--warmup):
///   --dt SECONDS            sim seconds advanced per rendered frame (default 60)
///   --scenario NAME         force one scenario (cube|moon|system|neptune) for the whole run instead of
///                            cycling through all four in 120-frame blocks
///   --diorama               force diorama radius scaling even outside the system scenario
///   --no-floating-origin    control mode: nodes get absolute float positions (pos - Sun) / RENDER_SCALE,
///                            camera included, to show what floating origin is fixing
/// </summary>
public partial class Main : Node3D
{
    const double KmPerAu = 149597870.7;
    const double RenderScale = 1000.0; // km per render unit

    // ---- catalogue (km) ----
    const double SunRadius = 696000;
    const double EarthOrbitR = KmPerAu; // 1 AU, circular
    const double EarthSpeed = 29.78; // km/s
    const double EarthRadius = 6371;
    const double MoonOrbitR = 384400;
    const double MoonSpeed = 1.022; // km/s
    const double MoonRadius = 1737;
    const double CubeEdge = 0.01; // 10 m
    const double NeptuneOrbitR = 30 * KmPerAu;
    const double NeptuneRadius = 24622;
    const double MarkerOrbitR = 50 * KmPerAu;
    const double MarkerRadius = 3000; // fictional marker sphere, not a real body

    const double DioramaPlanet = 800.0;
    const double DioramaSun = 30.0;

    class BodyState
    {
        public string Name = "";
        public double RadiusKm;
        public bool Dioramaable; // planets/marker scale x800, Sun x30, cube never scales
        public double DioramaMult = 1.0;
        public Vec3d Pos;
        public MeshInstance3D Node = null!;
        public SphereMesh? Sphere;
        public BoxMesh? Box;
        public Label Label = null!;
    }

    readonly Dictionary<string, BodyState> _bodies = new();
    Camera3D _camera = null!;
    CanvasLayer _hud = null!;

    // cube surface frame (world/double), recomputed every frame; used for the jitter corner too.
    Vec3d _cubeRight, _cubeUp, _cubeFwd;

    double _dt = 60.0;
    string? _forcedScenario;
    bool _forceDiorama;
    bool _noFloatingOrigin;
    int _frame = -1; // becomes 0 on the first _Process call

    string _outDir = "";
    StreamWriter? _jitterCsv;

    public override void _Ready()
    {
        ParseArgs(OS.GetCmdlineUserArgs());

        var env = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Color,
            BackgroundColor = new Color(0.02f, 0.02f, 0.03f),
            AmbientLightSource = Godot.Environment.AmbientSource.Color,
            AmbientLightColor = new Color(0.15f, 0.15f, 0.15f),
        };
        AddChild(new WorldEnvironment { Environment = env });

        _camera = new Camera3D { Current = true, Near = 0.05f, Far = 4000f };
        AddChild(_camera);

        _hud = new CanvasLayer();
        AddChild(_hud);

        AddBody("Sun", SunRadius, new Color(1.0f, 0.85f, 0.2f), dioramaMult: DioramaSun);
        AddBody("Earth", EarthRadius, new Color(0.25f, 0.45f, 0.9f), dioramaMult: DioramaPlanet);
        AddBody("Moon", MoonRadius, new Color(0.6f, 0.6f, 0.6f), dioramaMult: DioramaPlanet);
        AddCube();
        AddBody("Neptune", NeptuneRadius, new Color(0.3f, 0.6f, 0.95f), dioramaMult: DioramaPlanet);
        AddBody("Marker(50AU)", MarkerRadius, new Color(1f, 1f, 1f), dioramaMult: DioramaPlanet);

        if (CurrentScenarioFor(0) == "cube")
            OpenJitterCsv();

        GD.Print($"[scale] dt={_dt} scenario={_forcedScenario ?? "cycle"} diorama_forced={_forceDiorama} no_floating_origin={_noFloatingOrigin} outDir={_outDir}");
    }

    void ParseArgs(string[] args)
    {
        var dir = "res://capture";
        for (var i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
            switch (args[i])
            {
                case "--dt": _dt = double.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--scenario": _forcedScenario = Next(); break;
                case "--diorama": _forceDiorama = true; break;
                case "--no-floating-origin": _noFloatingOrigin = true; break;
                case "--capture-dir": dir = Next(); break; // mirror CaptureDriver so jitter.csv lands next to the shots
            }
        }
        _outDir = dir.StartsWith("res://") || dir.StartsWith("user://") ? ProjectSettings.GlobalizePath(dir) : Path.GetFullPath(dir);
    }

    void OpenJitterCsv()
    {
        Directory.CreateDirectory(_outDir);
        _jitterCsv = new StreamWriter(Path.Combine(_outDir, "jitter.csv"), false, new UTF8Encoding(false));
        _jitterCsv.WriteLine("frame,screen_x,screen_y");
    }

    void AddBody(string name, double radiusKm, Color color, double dioramaMult)
    {
        var mesh = new SphereMesh { Radius = 0.5f, Height = 1f }; // unit sphere, scaled via Node.Scale each frame
        var mat = new StandardMaterial3D { ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, AlbedoColor = color };
        mesh.Material = mat;
        var node = new MeshInstance3D { Mesh = mesh };
        AddChild(node);

        var label = new Label { Text = name };
        _hud.AddChild(label);

        _bodies[name] = new BodyState { Name = name, RadiusKm = radiusKm, Dioramaable = true, DioramaMult = dioramaMult, Node = node, Sphere = mesh, Label = label };
    }

    void AddCube()
    {
        var mesh = new BoxMesh { Size = Vector3.One }; // unit box, scaled via Node.Scale each frame
        var mat = new StandardMaterial3D { ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, AlbedoColor = new Color(0.95f, 0.15f, 0.1f) };
        mesh.Material = mat;
        var node = new MeshInstance3D { Mesh = mesh };
        AddChild(node);

        var label = new Label { Text = "Cube" };
        _hud.AddChild(label);

        _bodies["Cube"] = new BodyState { Name = "Cube", RadiusKm = CubeEdge * 0.5, Dioramaable = false, DioramaMult = 1.0, Node = node, Box = mesh, Label = label };
    }

    // ---- physics: analytic circular orbits in double, advanced by a fixed sim dt per rendered frame ----
    void UpdatePhysics(double simTime)
    {
        var sunPos = Vec3d.Zero;

        var earthOmega = EarthSpeed / EarthOrbitR; // rad/s
        var earthAngle = earthOmega * simTime;
        var earthPos = new Vec3d(Math.Cos(earthAngle) * EarthOrbitR, 0, Math.Sin(earthAngle) * EarthOrbitR);

        var moonOmega = MoonSpeed / MoonOrbitR;
        var moonAngle = moonOmega * simTime;
        var moonPos = earthPos + new Vec3d(Math.Cos(moonAngle) * MoonOrbitR, 0, Math.Sin(moonAngle) * MoonOrbitR);

        // Neptune and the 50 AU marker are static markers for this spike (no speed was specified for them);
        // placed off the Earth/Neptune axis so they don't visually overlap in the system/neptune scenarios.
        var neptunePos = new Vec3d(NeptuneOrbitR, 0, 0);
        var markerPos = new Vec3d(0, 0.15 * MarkerOrbitR, MarkerOrbitR).Normalized() * MarkerOrbitR;

        // Tidal lock: the cube sits at the Moon's sub-Earth point, so its outward normal is simply the
        // Moon->Earth direction recomputed every frame -- that direction rotating with the orbit *is* the
        // tidal-lock rotation, no separate spin integration needed.
        var dirToEarth = (earthPos - moonPos).Normalized();
        var cubeCenter = moonPos + dirToEarth * MoonRadius;
        var worldUpHint = Math.Abs(dirToEarth.Y) > 0.99 ? new Vec3d(1, 0, 0) : new Vec3d(0, 1, 0);
        var right = worldUpHint.Cross(dirToEarth).Normalized();
        var fwd = right.Cross(dirToEarth).Normalized(); // right-handed: fwd = right x up, not up x right (that was mirrored)
        _cubeUp = dirToEarth;
        _cubeRight = right;
        _cubeFwd = fwd;

        _bodies["Sun"].Pos = sunPos;
        _bodies["Earth"].Pos = earthPos;
        _bodies["Moon"].Pos = moonPos;
        _bodies["Cube"].Pos = cubeCenter;
        _bodies["Neptune"].Pos = neptunePos;
        _bodies["Marker(50AU)"].Pos = markerPos;
    }

    string CurrentScenarioFor(int frameIndex)
    {
        if (_forcedScenario != null) return _forcedScenario;
        var block = (frameIndex / 120) % 4;
        return block switch { 0 => "cube", 1 => "moon", 2 => "system", _ => "neptune" };
    }

    // Camera placement per scenario, all in double (km) world space. Returns camera position, look target,
    // and an up hint safe from being parallel to the view direction for that scenario.
    (Vec3d pos, Vec3d target, Vec3d up) PlaceCamera(string scenario)
    {
        var earth = _bodies["Earth"].Pos;
        var moon = _bodies["Moon"].Pos;
        var cube = _bodies["Cube"].Pos;
        var neptune = _bodies["Neptune"].Pos;

        switch (scenario)
        {
            case "cube":
            {
                // Fixed offset in the cube's own surface frame, not a world-axis offset: this is what
                // "hold the camera fixed relative to the cube" means once the cube is itself rotating
                // (tidal lock) -- a world-axis offset would still show 27-day-orbit-scale sway.
                var offset = (_cubeRight * 0.6 + _cubeUp * 0.5 + _cubeFwd * 0.6).Normalized() * 0.03; // 30 m
                var pos = cube + offset;
                return (pos, cube, new Vec3d(0, 1, 0));
            }
            case "moon":
            {
                var mid = (earth + moon) * 0.5;
                var dir = new Vec3d(1, 0.5, 0.2).Normalized();
                var pos = earth + dir * 1_000_000.0;
                return (pos, mid, new Vec3d(0, 1, 0));
            }
            case "system":
            {
                // 60 AU straight up puts the 50 AU marker just outside the default 75 deg FOV
                // (atan(50/60)=39.8 deg > 37.5 deg half-angle); 75 AU brings it in with margin.
                var pos = new Vec3d(0, 75 * KmPerAu, 0);
                return (pos, Vec3d.Zero, new Vec3d(0, 0, -1));
            }
            case "neptune":
            {
                // Camera-to-Sun is ~30 AU regardless of the offset below, so "forward" is essentially fixed
                // along -outward; the offset direction alone decides where Neptune lands on screen (dirToNeptune
                // is -offset, independent of standoff distance). Pure +outward puts Neptune dead centre, exactly
                // eclipsing the Sun/Earth point behind it (Neptune's own ~7 deg angular radius at 8R covers the
                // <2 deg Sun-Earth separation). Pure +side pushed Neptune's ~78 deg bearing outside the 37.5 deg
                // half-FOV entirely (empty frame). 7.5R outward + 2.75R side gives a ~20 deg bearing: inside the
                // FOV, clear of the Sun/Earth point at frame centre.
                var outward = neptune.Normalized();
                var side = outward.Cross(new Vec3d(0, 1, 0)).Normalized();
                var pos = neptune + outward * (NeptuneRadius * 7.5) + side * (NeptuneRadius * 2.75);
                return (pos, Vec3d.Zero, new Vec3d(0, 1, 0));
            }
            default:
                throw new ArgumentException($"unknown scenario '{scenario}'");
        }
    }

    // Empirically found on this machine (RTX 3080 Ti, Vulkan Forward+, reverse-Z): a near:far ratio
    // beyond somewhere between 1.3e7 and 3.9e7 makes Godot's frustum-point construction fail outright
    // ("ERROR: Condition "!res" is true ... create_frustum_points", rendering_light_culler.cpp:597) and
    // the ENTIRE frame goes black, not just the far end -- verified frame-by-frame with SphereMesh/BoxMesh
    // (see RESULT.md). 1e6 is a safe margin under that cliff. A body farther than near*MaxNearFarRatio is
    // simply not depth-tested that frame -- for the cube scenario that means Neptune/marker drop out while
    // near/far span only cube-to-Moon-surface, which is exactly the trade-off this spike is measuring.
    const double MaxNearFarRatio = 1e6;
    int _farClampedFrames;

    (float near, float far) ComputeNearFar(Vec3d camPos, Vec3d forward, Func<BodyState, double> dioramaMultFor)
    {
        var nearest = double.PositiveInfinity;
        var farthest = 0.0;
        var any = false;
        foreach (var b in _bodies.Values)
        {
            var to = b.Pos - camPos;
            var dist = to.Length();
            if (dist < 1e-9) continue;
            if (to.Normalized().Dot(forward) <= 0) continue; // behind camera
            var renderRadiusKm = b.RadiusKm * dioramaMultFor(b);
            var surfaceDist = Math.Max(dist - renderRadiusKm, 1e-6);
            nearest = Math.Min(nearest, surfaceDist);
            farthest = Math.Max(farthest, dist + renderRadiusKm);
            any = true;
        }
        if (!any) return (0.05f, 4000f);
        var near = (float)Math.Max(1e-6, 0.5 * nearest / RenderScale);
        var far = (float)(1.1 * farthest / RenderScale);
        if (far <= near) far = near * 2f;
        var maxFar = (float)(near * MaxNearFarRatio);
        if (far > maxFar) { far = maxFar; _farClampedFrames++; }
        return (near, far);
    }

    public override void _Process(double delta)
    {
        _frame++;
        var simTime = _frame * _dt;
        UpdatePhysics(simTime);

        var scenario = CurrentScenarioFor(_frame);
        var diorama = _forceDiorama || scenario == "system";
        double DioramaMultFor(BodyState b) => diorama ? (b.Dioramaable ? b.DioramaMult : 1.0) : 1.0;

        var (camPosW, camTargetW, upHint) = PlaceCamera(scenario);
        var forward = (camTargetW - camPosW).Normalized();

        if (_noFloatingOrigin)
        {
            _camera.Position = (camPosW / RenderScale).ToVector3();
            _camera.LookAt((camTargetW / RenderScale).ToVector3(), upHint.ToVector3());
        }
        else
        {
            _camera.Position = Vector3.Zero;
            _camera.LookAt(((camTargetW - camPosW) / RenderScale).ToVector3(), upHint.ToVector3());
        }

        var (near, far) = ComputeNearFar(camPosW, forward, DioramaMultFor);
        _camera.Near = near;
        _camera.Far = far;

        if (_frame % 50 == 0)
            GD.Print($"[dbg] frame={_frame} scenario={scenario} camPos={camPosW.X:E3},{camPosW.Y:E3},{camPosW.Z:E3} target={camTargetW.X:E3},{camTargetW.Y:E3},{camTargetW.Z:E3} near={near:E3} far={far:E3} fov={_camera.Fov} camNodePos={_camera.Position}");

        foreach (var b in _bodies.Values)
        {
            var renderPosW = _noFloatingOrigin ? b.Pos / RenderScale : (b.Pos - camPosW) / RenderScale;
            b.Node.Position = renderPosW.ToVector3();
            var renderRadiusUnits = (float)(b.RadiusKm * DioramaMultFor(b) / RenderScale);
            var edgeUnits = renderRadiusUnits * 2f; // unit meshes are 1-unit across
            if (b.Box != null)
                // Node3D.Basis IS the transform's rotation+scale combined: assigning Scale first and then a
                // pure-rotation Basis (as an earlier version of this did) silently discards the scale, and a
                // (right, up, fwd) triple built as up x right instead of right x up is left-handed, which
                // Godot then reports back as a Scale of (-1,-1,-1) rather than erroring. Fold the edge length
                // into the basis vectors directly so both mistakes are structurally impossible here.
                b.Node.Basis = new Basis((_cubeRight * edgeUnits).ToVector3(), (_cubeUp * edgeUnits).ToVector3(), (_cubeFwd * edgeUnits).ToVector3());
            else
                b.Node.Scale = Vector3.One * edgeUnits;

            UpdateLabel(b, camPosW, forward);
        }

        if (_frame % 50 == 0 && _bodies.TryGetValue("Cube", out var cubeB))
            GD.Print($"[dbg]   cube.nodePos={cubeB.Node.Position} cube.nodeScale={cubeB.Node.Scale} cube.visible={cubeB.Node.Visible} camBasisZ={_camera.GlobalTransform.Basis.Z}");

        RecordJitter(scenario, camPosW);
    }

    void UpdateLabel(BodyState b, Vec3d camPosW, Vec3d forward)
    {
        var behind = _camera.IsPositionBehind(b.Node.Position);
        var occluded = !behind && IsOccluded(b, camPosW);
        if (_frame % 50 == 0)
            GD.Print($"[dbg]   label {b.Name}: nodePos={b.Node.Position} behind={behind} occluded={occluded}");
        if (behind) { b.Label.Visible = false; return; }
        if (occluded) { b.Label.Visible = false; return; }
        var screen = _camera.UnprojectPosition(b.Node.Position);
        b.Label.Position = screen;
        b.Label.Visible = true;
    }

    bool IsOccluded(BodyState target, Vec3d camPosW)
    {
        var toTarget = target.Pos - camPosW;
        var distTarget = toTarget.Length();
        if (distTarget < 1e-9) return false;
        var dirTarget = toTarget / distTarget;
        foreach (var o in _bodies.Values)
        {
            if (ReferenceEquals(o, target)) continue;
            var toO = o.Pos - camPosW;
            var distO = toO.Length();
            if (distO >= distTarget || distO < 1e-9) continue;
            var proj = toO.Dot(dirTarget);
            if (proj <= 0) continue;
            var perp = (toO - dirTarget * proj).Length();
            if (perp < o.RadiusKm) return true; // true physical radius: occlusion is a physics fact, not a diorama one
        }
        return false;
    }

    void RecordJitter(string scenario, Vec3d camPosW)
    {
        if (_jitterCsv == null || scenario != "cube") return;
        var corner = _bodies["Cube"].Pos + (_cubeRight + _cubeUp + _cubeFwd) * (CubeEdge * 0.5);
        var renderPos = _noFloatingOrigin ? corner / RenderScale : (corner - camPosW) / RenderScale;
        var screen = _camera.UnprojectPosition(renderPos.ToVector3());
        _jitterCsv.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{_frame},{screen.X:F6},{screen.Y:F6}"));
    }

    public override void _ExitTree()
    {
        _jitterCsv?.Dispose();
        GD.Print($"[scale] far-clamped frames: {_farClampedFrames} / {_frame + 1}");
    }
}
