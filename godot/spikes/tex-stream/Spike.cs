using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using Godot;

namespace TexStream;

/// <summary>
/// Spike M1b. Earth (radius 6.371 units, 1 unit = 1000 km) textured with a 16384x8192 StreamedTexture2D; the camera moves
/// by frame count (never by time) so two runs' screenshots of the same frame are comparable.
/// User args (after `--`, alongside CaptureDriver's):
///   --material custom|standard|procuv|procuv-streaminguv|procuv-plain|constuv   custom spatial shader with a plain sampler2D, the
///                                StandardMaterial3D control, or UV computed in the shader (optionally written to STREAMING_UV)
///   --texture 16k|2048           the streamed 16K map, or the 2048 LOD copy imported as a plain CompressedTexture2D
///   --streaming 0|1              0 sets rendering/textures/streaming/enabled=false before the texture loads
///   --no-mesh-uv                 drop the sphere's UV channel (pair with --material procuv*)
///   --decay-ms N / --budget-far2 MB / --hide-far2   step-4 probes: what makes a resident LOD 0 go back down
///   --force max|min|none         at --force-frame (default 390): MaxLodOverride = 1 or MinLodOverride = 1 (both mean LOD 0), then flush
/// Writes stream.csv (per frame) and stream-events.txt into the capture dir.
/// </summary>
public partial class Spike : Node3D
{
    const float EarthRadius = 6.371f;
    // Texel pitch at the equator: 2*pi*6371 km / 16384 = 2.443 km. At FOV 6 deg the 900 px screen height spans
    // 2*h*tan(3 deg) = 0.1048 h, so h = 17 units gives 1.78 units / 900 px = 0.81 texel per pixel: LOD 0 is needed.
    const float NearAltitude = 17f;
    int _forceFrame = 390;
    int _decayMs = -1, _budgetFar2 = -1;
    bool _hideFar2, _noMeshUv;
    MeshInstance3D _earth = null!;

    string _material = "custom", _textureArg = "16k", _force = "max", _dir = "";
    bool _streamingArg = true;
    Camera3D _cam = null!;
    Texture2D _tex = null!;
    StreamedTexture2D? _stex;
    StreamWriter? _csv;
    StreamWriter? _events;
    int _frame;
    int _flushFrame = -1;
    readonly Stopwatch _sinceReady = new();
    Vector3 _target;
    bool _firstDrawLogged;

    public override void _Ready()
    {
        ParseArgs(OS.GetCmdlineUserArgs());
        Directory.CreateDirectory(_dir);
        _events = new StreamWriter(Path.Combine(_dir, "stream-events.txt"), false, new UTF8Encoding(false)) { AutoFlush = true };
        Log($"engine ticks at _Ready: {Time.GetTicksMsec()} ms");
        Log($"args material={_material} texture={_textureArg} streaming={_streamingArg} force={_force}");

        const string key = "rendering/textures/streaming/enabled";
        Log($"setting {key} at startup = {ProjectSettings.GetSetting(key)}");
        if (!_streamingArg)
        {
            ProjectSettings.SetSetting(key, false);
            Log($"set {key}=false, now reads {ProjectSettings.GetSetting(key)}");
        }
        if (_decayMs >= 0)
        {
            ProjectSettings.SetSetting("rendering/textures/streaming/inactivity_decay_rate_ms", _decayMs);
            Log($"set inactivity_decay_rate_ms={_decayMs}");
        }
        Log($"TextureStreaming overrides: min_lod={TextureStreaming.MinLodOverride} max_lod={TextureStreaming.MaxLodOverride} budget_mb={TextureStreaming.MemoryBudgetMbOverride}");

        var path = _textureArg == "2048" ? "res://textures/earth_day_2048.jpg" : "res://textures/earth_day.jpg";
        var sw = Stopwatch.StartNew();
        var res = ResourceLoader.Load(path);
        sw.Stop();
        _tex = (Texture2D)res;
        _stex = res as StreamedTexture2D;
        Log($"ResourceLoader.Load({path}) {sw.Elapsed.TotalMilliseconds:F2} ms -> runtime type {res.GetType().Name} (Godot class {res.GetClass()}) size {_tex.GetWidth()}x{_tex.GetHeight()} mipmaps={_tex.HasMipmaps()}");
        if (_stex != null)
            Log($"StreamedTexture2D LoadPath={_stex.LoadPath} MinLodOverride={_stex.MinLodOverride} MaxLodOverride={_stex.MaxLodOverride}");

        BuildScene();
        TextureStreaming.FlushCompleted += OnFlushCompleted;
        RenderingServer.FramePostDraw += OnFramePostDraw;

        _csv = new StreamWriter(Path.Combine(_dir, "stream.csv"), false, new UTF8Encoding(false));
        _csv.WriteLine("frame,t_ms,phase,textureMemMB,streamingTexMemMB,budgetUsedMB,rdWidth,rdMips,nvidiaUsedMB,nvsmi_ms");
        _sinceReady.Start();
    }

    void ParseArgs(string[] args)
    {
        var dir = "res://capture";
        for (var i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
            switch (args[i])
            {
                case "--capture-dir": dir = Next(); break;
                case "--material": _material = Next(); break;
                case "--texture": _textureArg = Next(); break;
                case "--streaming": _streamingArg = Next() != "0"; break;
                case "--force": _force = Next(); break;
                case "--decay-ms": _decayMs = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--budget-far2": _budgetFar2 = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--hide-far2": _hideFar2 = true; break;
                case "--no-mesh-uv": _noMeshUv = true; break;
                case "--force-frame": _forceFrame = int.Parse(Next(), CultureInfo.InvariantCulture); break;
            }
        }
        _dir = dir.StartsWith("res://") || dir.StartsWith("user://") ? ProjectSettings.GlobalizePath(dir) : Path.GetFullPath(dir);
    }

    void BuildScene()
    {
        var env = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Color,
            BackgroundColor = Colors.Black,
            AmbientLightSource = Godot.Environment.AmbientSource.Color,
            AmbientLightColor = Colors.Black,
            AmbientLightEnergy = 0f,
            TonemapMode = Godot.Environment.ToneMapper.Linear,
        };
        AddChild(new WorldEnvironment { Environment = env });

        var mesh = new SphereMesh { Radius = EarthRadius, Height = 2 * EarthRadius, RadialSegments = 128, Rings = 64 };
        Material mat;
        if (_material == "standard")
        {
            mat = new StandardMaterial3D
            {
                AlbedoTexture = _tex,
                DiffuseMode = BaseMaterial3D.DiffuseModeEnum.Lambert,
                SpecularMode = BaseMaterial3D.SpecularModeEnum.Disabled,
                Roughness = 1f,
                TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmapsAnisotropic,
            };
        }
        else if (_material == "constuv")
        {
            var sm = new ShaderMaterial { Shader = GD.Load<Shader>("res://earth_constuv.gdshader") };
            sm.SetShaderParameter("albedo", _tex);
            mat = sm;
        }
        else if (_material == "procuv-plain")
        {
            var sm = new ShaderMaterial { Shader = GD.Load<Shader>("res://earth_procuv_plain.gdshader") };
            sm.SetShaderParameter("albedo", _tex);
            mat = sm;
        }
        else if (_material.StartsWith("procuv"))
        {
            var sm = new ShaderMaterial { Shader = GD.Load<Shader>("res://earth_procuv.gdshader") };
            sm.SetShaderParameter("albedo", _tex);
            sm.SetShaderParameter("write_streaming_uv", _material == "procuv-streaminguv");
            mat = sm;
        }
        else
        {
            var shader = GD.Load<Shader>("res://earth_custom.gdshader");
            var sm = new ShaderMaterial { Shader = shader };
            sm.SetShaderParameter("albedo", _tex);
            mat = sm;
        }
        Mesh earthMesh = mesh;
        if (_noMeshUv)
        {
            // Same sphere with the UV channel dropped: only a shader-computed UV (and STREAMING_UV) can drive feedback.
            var arrays = mesh.GetMeshArrays();
            arrays[(int)Mesh.ArrayType.TexUV] = default;
            arrays[(int)Mesh.ArrayType.Tangent] = default;
            var am = new ArrayMesh();
            am.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
            earthMesh = am;
            Log($"mesh without UV: surface format {am.SurfaceGetFormat(0)}");
        }
        _earth = new MeshInstance3D { Mesh = earthMesh, MaterialOverride = mat, Name = "Earth" };
        AddChild(_earth);

        // Near-view target: 25 N, 30 E (Nile valley / Red Sea), a high-contrast patch of the map. SphereMesh puts
        // u = 0 at -Z... the direction is found from the mesh convention: x = sin(2*pi*u), z = cos(2*pi*u) scaled by
        // the ring radius, u = 0.5 + lon/360 for an equirectangular map starting at -180.
        _target = SurfaceDir(25f, 30f);
        _cam = new Camera3D { Current = true, Near = 0.01f, Far = 10000f, Fov = 75f };
        AddChild(_cam);

        // Sun behind the camera and a little to the side of the near target, so the near view is lit and has relief.
        var sunDir = (_target + new Vector3(0.3f, 0.2f, 0f)).Normalized();
        var light = new DirectionalLight3D { LightEnergy = 1.0f, ShadowEnabled = false };
        AddChild(light);
        light.LookAt(-sunDir, Vector3.Up.Cross(sunDir).Length() < 1e-3 ? Vector3.Forward : Vector3.Up);
        SetCamera(0);
    }

    static Vector3 SurfaceDir(float latDeg, float lonDeg)
    {
        var lat = Mathf.DegToRad(latDeg);
        var u = 0.5f + lonDeg / 360f;
        var phi = u * Mathf.Tau;
        // SphereMesh: v = 0 at the top (y = +r); a ring at latitude lat has radius cos(lat), and vertex x = sin(phi), z = cos(phi).
        return new Vector3(Mathf.Sin(phi) * Mathf.Cos(lat), Mathf.Sin(lat), Mathf.Cos(phi) * Mathf.Cos(lat)).Normalized();
    }

    static string Phase(int f) => f < 90 ? "far" : f < 180 ? "mid" : f < 300 ? "near" : f < 480 ? "hold" : "far2";

    void SetCamera(int f)
    {
        var dir = _target;
        float dist, fov = 75f;
        switch (Phase(f))
        {
            case "far": case "far2":
                // Earth ~3 px of 900 at FOV 75: angular diameter 0.25 deg -> distance 6.371 / tan(0.125 deg).
                dist = EarthRadius / Mathf.Tan(Mathf.DegToRad(0.125f));
                break;
            case "mid":
                // Earth ~half the screen height: angular diameter 37.5 deg.
                dist = EarthRadius / Mathf.Sin(Mathf.DegToRad(18.75f));
                break;
            default:
                dist = EarthRadius + NearAltitude;
                fov = 6f;
                break;
        }
        _cam.Fov = fov;
        var pos = dir * dist;
        _cam.Position = pos;
        _cam.LookAt(dir * EarthRadius, Vector3.Up);
    }

    public override void _Process(double delta)
    {
        _frame++;
        var f = _frame;
        SetCamera(f);

        if (f == _forceFrame && _stex != null && _force != "none")
            Force();
        if (f == 480 && _budgetFar2 >= 0)
        {
            TextureStreaming.MemoryBudgetMbOverride = (uint)_budgetFar2;
            Log($"frame 480: MemoryBudgetMbOverride={_budgetFar2}");
        }
        if (f == 480 && _hideFar2)
        {
            _earth.Visible = false;
            Log("frame 480: Earth hidden");
        }

        double texMem = Performance.GetMonitor(Performance.Monitor.RenderTextureMemUsed) / 1e6;
        double streamMem = Performance.GetMonitor(Performance.Monitor.RenderStreamingTextureMemUsed) / 1e6;
        double budget = TextureStreaming.GetMemoryBudgetBytesUsed() / 1e6;
        var (rdW, rdMips) = ResidentSize();
        string nv = "", nvMs = "";
        // Off the 30-frame grid by 15 so the sample never lands on a phase change or the force frame.
        if (f % 30 == 15)
        {
            var sw = Stopwatch.StartNew();
            nv = NvidiaUsedMb();
            nvMs = sw.Elapsed.TotalMilliseconds.ToString("F1", CultureInfo.InvariantCulture);
        }
        _csv?.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{f},{_sinceReady.Elapsed.TotalMilliseconds:F1},{Phase(f)},{texMem:F2},{streamMem:F2},{budget:F2},{rdW},{rdMips},{nv},{nvMs}"));
        if (f % 60 == 0) _csv?.Flush();
    }

    void Force()
    {
        var sw = Stopwatch.StartNew();
        if (_force == "min") _stex!.MinLodOverride = 1;
        else _stex!.MaxLodOverride = 1;
        var setMs = sw.Elapsed.TotalMilliseconds;
        sw.Restart();
        TextureStreaming.FlushTextureStreaming();
        var flushMs = sw.Elapsed.TotalMilliseconds;
        var (w, m) = ResidentSize();
        Log($"force frame={_frame} mode={_force} set_override={setMs:F3} ms flush_call={flushMs:F3} ms; after: MinLodOverride={_stex.MinLodOverride} MaxLodOverride={_stex.MaxLodOverride} rd={w}px/{m} mips budgetUsed={TextureStreaming.GetMemoryBudgetBytesUsed() / 1e6:F2} MB");
    }

    void OnFlushCompleted()
    {
        _flushFrame = _frame;
        var (w, m) = ResidentSize();
        Log($"flush_completed at frame={_frame} rd={w}px/{m} mips budgetUsed={TextureStreaming.GetMemoryBudgetBytesUsed() / 1e6:F2} MB");
    }

    void OnFramePostDraw()
    {
        if (_firstDrawLogged) return;
        _firstDrawLogged = true;
        Log($"first frame drawn: engine ticks {Time.GetTicksMsec()} ms, {_sinceReady.Elapsed.TotalMilliseconds:F1} ms after Spike._Ready end");
    }

    // The RD texture behind the resource: if streaming reallocates on mip changes, its width/mip count is the resident LOD.
    (long, long) ResidentSize()
    {
        var rd = RenderingServer.GetRenderingDevice();
        if (rd == null) return (-1, -1);
        var rid = RenderingServer.TextureGetRdTexture(_tex.GetRid());
        if (!rid.IsValid) return (-2, -2);
        var fmt = rd.TextureGetFormat(rid);
        return (fmt.Width, fmt.Mipmaps);
    }

    static string NvidiaUsedMb()
    {
        try
        {
            var psi = new ProcessStartInfo("nvidia-smi", "--query-gpu=memory.used --format=csv,noheader,nounits")
            { RedirectStandardOutput = true, UseShellExecute = false };
            using var p = Process.Start(psi)!;
            var s = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit();
            return s;
        }
        catch (Exception e) { return "err:" + e.GetType().Name; }
    }

    void Log(string msg)
    {
        GD.Print($"[spike] {msg}");
        _events?.WriteLine(msg);
    }

    public override void _ExitTree()
    {
        TextureStreaming.FlushCompleted -= OnFlushCompleted;
        RenderingServer.FramePostDraw -= OnFramePostDraw;
        Log($"exit: flush_completed frame={_flushFrame}");
        _csv?.Dispose();
        _events?.Dispose();
    }
}
