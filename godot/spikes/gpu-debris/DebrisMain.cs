using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Godot;

namespace Spayly.Spikes.GpuDebris;

/// <summary>
/// Spike M1a: GPU debris on the main RenderingDevice. Compute integrates N particles around a central body plus
/// moons, resolves particle contacts through a hashed counting-sort grid, and the renderer draws the result without
/// a CPU round trip (route a: RD texture → Texture2DRD → texelFetch by VERTEX_ID; route b: compute writes the
/// MultiMesh RD buffer). User args after `--`: --n N  --contacts 0|1  --gravity 0|1  --route a|b|ab.
/// </summary>
public partial class DebrisMain : Node3D
{
    // --- configuration (CLI) ---
    int _n = 100_000;
    bool _contacts = true;
    bool _gravity = true;
    bool _routeA = true;
    bool _routeB;
    bool _taa;
    bool _blockHash = true;
    int _frames = 300;
    string _outDir = "";

    // --- physics constants: km, s, km^3/s^2 ---
    const double GmCentral = 398600.4418;
    const double RCentral = 6371.0;
    const double RingIn = 10000.0, RingOut = 20000.0;
    const int Layers = 3;
    const float Dt = 20f;
    const float Eps2 = 50f * 50f;
    const float RenderScale = 0.001f; // 1 render unit = 1000 km
    static readonly (double gm, double a, double inclDeg, double phase)[] Moons =
    {
        (4902.8, 30000.0, 0.0, 0.0),
        (1500.0, 42000.0, 5.0, 2.0),
        (300.0, 24000.0, 1.0, 4.0),
    };

    RenderingDevice _rd = null!;
    readonly List<Rid> _buffers = new();
    readonly List<Rid> _shaders = new();
    readonly List<Rid> _pipelines = new();
    readonly List<Rid> _sets = new();

    Rid _pipeIntegrate, _pipeClear, _pipeCount, _pipeScan, _pipeAdd, _pipeScatter, _pipeContacts, _pipeMm;
    Rid _pos, _vel, _posB, _velB, _cellRank, _cnt, _start, _stats, _bodies, _posTex;
    Rid _setIntegrate, _setClear, _setCount, _setScatter, _setContacts, _setMm;
    // Scan hierarchy: level l scans src_l into dst_l, block totals into sums_l (= src_{l+1}).
    readonly List<(Rid set, uint count)> _scanLevels = new();
    readonly List<(Rid set, uint count)> _addLevels = new(); // top-down order
    uint _tableSize, _mask;
    float _invCell, _rMax;
    int _texW = 1024, _texH;

    Texture2Drd? _tex;
    ShaderMaterial? _pointsMat, _probeMat;
    MeshInstance3D? _probe;
    Camera3D _cam = null!;
    readonly MeshInstance3D[] _moonNodes = new MeshInstance3D[3];
    MultiMesh? _mm;
    Rid _mmBuffer;
    int _mmStride = 16;
    bool _mmSetInvalidLogged;

    Float4[] _pos0 = Array.Empty<Float4>(), _vel0 = Array.Empty<Float4>();
    BodiesUbo _ubo;
    int _nBodies;
    int _frame;

    // --- timing: preallocated, written at the end so the steady-state path allocates nothing ---
    double[] _tInt = Array.Empty<double>(), _tGrid = Array.Empty<double>(), _tCon = Array.Empty<double>(), _tMm = Array.Empty<double>(), _tTot = Array.Empty<double>();
    long[] _tCapFrame = Array.Empty<long>();
    bool[] _tHave = Array.Empty<bool>();
    readonly int[] _tsIdx = new int[5];
    uint _tsCount;
    bool _tsResolved;
    int _tsLag = -1;
    int _tsReads;
    int _tsSkipped;
    string _tsNote = "";

    int _gc0At50 = -1;
    long _myAllocSteady;
    long _threadAllocAt50;
    readonly StringName _cpuFrameParam = "cpu_frame";

    readonly Dictionary<string, object> _report = new();
    readonly List<string> _probeLog = new();
    readonly List<Vector2> _probeCpuPx = new(), _probeGpuPx = new();

    public override void _Ready()
    {
        ParseArgs(OS.GetCmdlineUserArgs());
        Layout.AssertManaged();
        _rd = RenderingServer.GetRenderingDevice();
        if (_rd == null) { Fail("RenderingServer.GetRenderingDevice() returned null"); return; }
        _report["n"] = _n; _report["contacts"] = _contacts; _report["gravity"] = _gravity;
        _report["route"] = (_routeA ? "a" : "") + (_routeB ? "b" : "");
        _report["taa"] = _taa;
        _report["hash"] = _blockHash ? "block" : "random";
        if (_taa) GetViewport().UseTaa = true; // TAA turns on motion vectors, which double-buffer MultiMesh data
        _report["thread_model"] = (int)ProjectSettings.GetSetting("rendering/driver/threads/thread_model");

        BuildPipelines();
        InitParticles();
        CreateBuffers();
        RoundTripTest();
        CreateSets();
        BuildScene();
        _report["vram_rd_after_setup_mb"] = MemMb();
        GD.Print($"[debris] n={_n} contacts={_contacts} gravity={_gravity} route={_report["route"]} table={_tableSize} cell={2 * _rMax:F2}km tex={_texW}x{_texH}");
    }

    void ParseArgs(string[] args)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            var v = args[i + 1];
            switch (args[i])
            {
                case "--n": _n = int.Parse(v.Replace("_", ""), CultureInfo.InvariantCulture); break;
                case "--contacts": _contacts = v != "0"; break;
                case "--gravity": _gravity = v != "0"; break;
                case "--route": _routeA = v.Contains('a'); _routeB = v.Contains('b'); break;
                case "--hash": _blockHash = v == "block"; break;
                case "--taa": _taa = v != "0"; break;
                case "--frames": _frames = int.Parse(v, CultureInfo.InvariantCulture); break;
                case "--capture-dir": _outDir = v; break;
            }
        }
        if (_outDir == "") _outDir = ProjectSettings.GlobalizePath("res://capture/adhoc");
        Directory.CreateDirectory(_outDir);
        var cap = _frames + 8;
        _tInt = new double[cap]; _tGrid = new double[cap]; _tCon = new double[cap]; _tMm = new double[cap]; _tTot = new double[cap];
        _tCapFrame = new long[cap]; _tHave = new bool[cap];
    }

    // ---------------------------------------------------------------- setup

    Rid Compile(string name, string src)
    {
        var source = new RDShaderSource { Language = RenderingDevice.ShaderLanguage.Glsl, SourceCompute = src };
        var spirv = _rd.ShaderCompileSpirVFromSource(source);
        var err = spirv.GetStageCompileError(RenderingDevice.ShaderStage.Compute);
        if (!string.IsNullOrEmpty(err))
        {
            GD.PrintErr($"[debris] GLSL compile error in {name}:\n{err}");
            throw new InvalidOperationException($"shader {name} failed to compile");
        }
        var shader = _rd.ShaderCreateFromSpirV(spirv, name);
        if (!shader.IsValid) throw new InvalidOperationException($"ShaderCreateFromSpirV({name}) returned an invalid RID");
        _shaders.Add(shader);
        var pipe = _rd.ComputePipelineCreate(shader);
        if (!pipe.IsValid) throw new InvalidOperationException($"ComputePipelineCreate({name}) invalid");
        _pipelines.Add(pipe);
        return pipe;
    }

    readonly Dictionary<Rid, Rid> _shaderOf = new();

    void BuildPipelines()
    {
        Rid P(string name, string src)
        {
            var p = Compile(name, src);
            _shaderOf[p] = _shaders[^1];
            return p;
        }
        _pipeIntegrate = P("integrate", Shaders.Integrate);
        _pipeClear = P("clear", Shaders.Clear);
        _pipeCount = P("count", Shaders.Count);
        _pipeScan = P("scan_block", Shaders.ScanBlock);
        _pipeAdd = P("scan_add", Shaders.ScanAdd);
        _pipeScatter = P("scatter", Shaders.Scatter);
        _pipeContacts = P("contacts", Shaders.Contacts);
        if (_routeB) _pipeMm = P("multimesh_write", Shaders.MultimeshWrite);
        GD.Print("[debris] all compute shaders compiled");
    }

    void InitParticles()
    {
        // Jittered lattice inside the annulus: one particle per cell, so the initial state has no overlaps.
        var area = Math.PI * (RingOut * RingOut - RingIn * RingIn);
        var s = Math.Sqrt(area * Layers / (1.1 * _n));
        _rMax = (float)(0.3 * s);
        var jitter = 0.19 * s;
        var cells = new List<(int x, int y, int z)>();
        var half = (int)Math.Ceiling(RingOut / s);
        for (var z = 0; z < Layers; z++)
        for (var y = -half; y <= half; y++)
        for (var x = -half; x <= half; x++)
        {
            var r = Math.Sqrt((x * s) * (x * s) + (y * s) * (y * s));
            if (r >= RingIn && r <= RingOut) cells.Add((x, y, z));
        }
        if (cells.Count < _n) throw new InvalidOperationException($"lattice has {cells.Count} cells < n {_n}");
        var rng = new Random(12345);
        for (var i = 0; i < _n; i++)
        {
            var j = rng.Next(i, cells.Count);
            (cells[i], cells[j]) = (cells[j], cells[i]);
        }
        _pos0 = new Float4[_n];
        _vel0 = new Float4[_n];
        const double sigma = 0.1; // km/s velocity dispersion: what drives contacts
        for (var i = 0; i < _n; i++)
        {
            var (cx, cy, cz) = cells[i];
            var px = cx * s + (rng.NextDouble() * 2 - 1) * jitter;
            var py = cy * s + (rng.NextDouble() * 2 - 1) * jitter;
            var pz = (cz - (Layers - 1) * 0.5) * s + (rng.NextDouble() * 2 - 1) * jitter;
            var rad = _rMax * (0.5 + 0.5 * rng.NextDouble());
            var r = Math.Sqrt(px * px + py * py);
            var vc = Math.Sqrt(GmCentral / r);
            double G() => Math.Sqrt(-2 * Math.Log(1 - rng.NextDouble())) * Math.Cos(2 * Math.PI * rng.NextDouble());
            var vx = -py / r * vc + sigma * G();
            var vy = px / r * vc + sigma * G();
            var vz = sigma * G();
            _pos0[i] = new Float4((float)px, (float)py, (float)pz, (float)rad);
            _vel0[i] = new Float4((float)vx, (float)vy, (float)vz, 0f);
        }
        _invCell = 1f / (2f * _rMax);
        _tableSize = NextPow2((uint)(2 * _n));
        _mask = _tableSize - 1;
        _texH = (_n + 1 + _texW - 1) / _texW;
        _report["lattice_spacing_km"] = s;
        _report["r_max_km"] = _rMax;
        _report["cell_km"] = 2 * _rMax;
        _report["table_size"] = _tableSize;
        _report["tex"] = $"{_texW}x{_texH}";
        _nBodies = _gravity ? 1 + Moons.Length : 0;
        FillBodies(0);
    }

    static uint NextPow2(uint v)
    {
        var p = 1u;
        while (p < v) p <<= 1;
        return p;
    }

    static (double x, double y, double z) MoonPos(int k, double t)
    {
        var (gm, a, incl, ph) = Moons[k];
        var w = Math.Sqrt((GmCentral + gm) / (a * a * a));
        var th = ph + w * t;
        var i = incl * Math.PI / 180;
        return (a * Math.Cos(th), a * Math.Sin(th) * Math.Cos(i), a * Math.Sin(th) * Math.Sin(i));
    }

    void FillBodies(int frame)
    {
        double t0 = frame * (double)Dt, t1 = t0 + Dt;
        _ubo.Now[0] = new Float4(0, 0, 0, (float)GmCentral);
        _ubo.Next[0] = _ubo.Now[0];
        for (var k = 0; k < Moons.Length; k++)
        {
            var (x0, y0, z0) = MoonPos(k, t0);
            var (x1, y1, z1) = MoonPos(k, t1);
            _ubo.Now[k + 1] = new Float4((float)x0, (float)y0, (float)z0, (float)Moons[k].gm);
            _ubo.Next[k + 1] = new Float4((float)x1, (float)y1, (float)z1, (float)Moons[k].gm);
        }
    }

    static ReadOnlySpan<byte> Bytes<T>(T[] a) where T : struct => MemoryMarshal.AsBytes(a.AsSpan());

    Rid Buffer(uint bytes, ReadOnlySpan<byte> data = default)
    {
        var b = data.IsEmpty
            ? _rd.StorageBufferCreate(bytes, new byte[bytes])
            : _rd.StorageBufferCreate(bytes, data, (RenderingDevice.StorageBufferUsage)0);
        if (!b.IsValid) throw new InvalidOperationException($"StorageBufferCreate({bytes}) failed");
        _buffers.Add(b);
        return b;
    }

    void CreateBuffers()
    {
        var n4 = (uint)(_n * 16);
        _pos = Buffer(n4, Bytes(_pos0));
        _vel = Buffer(n4, Bytes(_vel0));
        _posB = Buffer(n4);
        _velB = Buffer(n4);
        _cellRank = Buffer((uint)(_n * 8));
        _cnt = Buffer(_tableSize * 4);
        _start = Buffer(_tableSize * 4);
        _stats = Buffer(16);
        var ubo = MemoryMarshal.AsBytes(new ReadOnlySpan<BodiesUbo>(ref _ubo));
        _bodies = _rd.UniformBufferCreate((uint)ubo.Length, ubo);
        if (!_bodies.IsValid) throw new InvalidOperationException("UniformBufferCreate failed");
        _buffers.Add(_bodies);

        var fmt = new RDTextureFormat
        {
            Format = RenderingDevice.DataFormat.R32G32B32A32Sfloat,
            TextureType = RenderingDevice.TextureType.Type2D,
            Width = (uint)_texW,
            Height = (uint)_texH,
            Depth = 1,
            ArrayLayers = 1,
            Mipmaps = 1,
            UsageBits = RenderingDevice.TextureUsageBits.SamplingBit | RenderingDevice.TextureUsageBits.StorageBit
                        | RenderingDevice.TextureUsageBits.CanUpdateBit | RenderingDevice.TextureUsageBits.CanCopyFromBit,
        };
        _posTex = _rd.TextureCreate(fmt, new RDTextureView(), new Godot.Collections.Array<byte[]>());
        if (!_posTex.IsValid) throw new InvalidOperationException("TextureCreate(rgba32f) failed");
        _buffers.Add(_posTex);
    }

    void RoundTripTest()
    {
        // Proves the C# struct layout is what std430/std140 sees: upload, download, compare bytes and fields.
        var ok = true;
        var sb = new StringBuilder();
        void Cmp(string name, Rid rid, ReadOnlySpan<byte> expect)
        {
            var got = _rd.BufferGetData(rid);
            var same = got.Length >= expect.Length && got.AsSpan(0, expect.Length).SequenceEqual(expect);
            ok &= same;
            sb.Append($"{name}: {got.Length} B {(same ? "identical" : "MISMATCH")}; ");
        }
        Cmp("pos", _pos, Bytes(_pos0));
        Cmp("vel", _vel, Bytes(_vel0));
        Cmp("bodies_ubo", _bodies, MemoryMarshal.AsBytes(new ReadOnlySpan<BodiesUbo>(ref _ubo)));
        var back = MemoryMarshal.Cast<byte, Float4>(_rd.BufferGetData(_pos));
        var k = _n - 1;
        var fieldOk = back[k].X == _pos0[k].X && back[k].Y == _pos0[k].Y && back[k].Z == _pos0[k].Z && back[k].W == _pos0[k].W;
        ok &= fieldOk;
        sb.Append($"pos[{k}] fields {(fieldOk ? "equal" : "DIFFER")} (w={back[k].W:F3} km)");
        _report["roundtrip"] = (ok ? "PASS " : "FAIL ") + sb;
        GD.Print($"[debris] round-trip {_report["roundtrip"]}");
    }

    Rid Set(Rid pipe, params (int binding, RenderingDevice.UniformType type, Rid rid)[] us)
    {
        var arr = new Godot.Collections.Array<RDUniform>();
        foreach (var (binding, type, rid) in us)
        {
            var u = new RDUniform { Binding = binding, UniformType = type };
            u.AddId(rid);
            arr.Add(u);
        }
        var set = _rd.UniformSetCreate(arr, _shaderOf[pipe], 0);
        if (!set.IsValid) throw new InvalidOperationException("UniformSetCreate failed");
        _sets.Add(set);
        return set;
    }

    const RenderingDevice.UniformType Ssbo = RenderingDevice.UniformType.StorageBuffer;

    void CreateSets()
    {
        _setIntegrate = Set(_pipeIntegrate, (0, Ssbo, _pos), (1, Ssbo, _vel),
            (2, RenderingDevice.UniformType.UniformBuffer, _bodies), (3, RenderingDevice.UniformType.Image, _posTex));
        _setClear = Set(_pipeClear, (0, Ssbo, _cnt));
        _setCount = Set(_pipeCount, (0, Ssbo, _pos), (1, Ssbo, _cnt), (2, Ssbo, _cellRank));
        _setScatter = Set(_pipeScatter, (0, Ssbo, _pos), (1, Ssbo, _vel), (2, Ssbo, _posB), (3, Ssbo, _velB), (4, Ssbo, _cellRank), (5, Ssbo, _start));
        _setContacts = Set(_pipeContacts, (0, Ssbo, _posB), (1, Ssbo, _velB), (2, Ssbo, _pos), (3, Ssbo, _vel), (4, Ssbo, _start), (5, Ssbo, _cnt), (6, Ssbo, _stats));

        // Multi-level scan: 1024 elements per workgroup; recurse on block totals until one block remains.
        var src = _cnt;
        var dst = _start;
        var count = _tableSize;
        var levelDst = new List<(Rid dst, uint count)>();
        while (true)
        {
            var blocks = (count + 1023) / 1024;
            var sums = Buffer(Math.Max(blocks, 4) * 4);
            _scanLevels.Add((Set(_pipeScan, (0, Ssbo, src), (1, Ssbo, dst), (2, Ssbo, sums)), count));
            levelDst.Add((dst, count));
            if (blocks == 1) break;
            src = sums;
            dst = Buffer(Math.Max(blocks, 4) * 4);
            count = blocks;
        }
        // Carry pass top-down: level l's output gets the scanned block totals of level l+1 added.
        for (var l = levelDst.Count - 2; l >= 0; l--)
            _addLevels.Add((Set(_pipeAdd, (0, Ssbo, levelDst[l].dst), (1, Ssbo, levelDst[l + 1].dst)), levelDst[l].count));
        _report["scan_levels"] = _scanLevels.Count;
    }

    void BuildScene()
    {
        var env = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Color,
            BackgroundColor = Colors.Black,
            AmbientLightSource = Godot.Environment.AmbientSource.Color,
            AmbientLightColor = new Color(0.2f, 0.2f, 0.2f),
            TonemapMode = Godot.Environment.ToneMapper.Linear,
        };
        AddChild(new WorldEnvironment { Environment = env });
        _cam = new Camera3D { Position = new Vector3(0, 21, 27), Fov = 50, Near = 0.05f, Far = 500, Current = true };
        AddChild(_cam);
        _cam.LookAt(new Vector3(0, -2, 0));

        var planetMat = new StandardMaterial3D { ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, AlbedoColor = new Color(0.06f, 0.07f, 0.1f) };
        AddChild(new MeshInstance3D
        {
            Mesh = new SphereMesh { Radius = (float)(RCentral * RenderScale), Height = (float)(2 * RCentral * RenderScale), Material = planetMat },
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        });
        var moonMat = new StandardMaterial3D { ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, AlbedoColor = new Color(0.8f, 0.8f, 0.75f) };
        for (var k = 0; k < _moonNodes.Length; k++)
        {
            _moonNodes[k] = new MeshInstance3D { Mesh = new SphereMesh { Radius = 0.5f, Height = 1f, Material = moonMat }, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
            AddChild(_moonNodes[k]);
        }
        UpdateMoonNodes(0);

        var bigAabb = new Aabb(new Vector3(-100, -100, -100), new Vector3(200, 200, 200));
        var pointSize = _n >= 500_000 ? 1f : 2f;
        var intensity = _n >= 500_000 ? 0.35f : 0.6f;
        if (_routeA)
        {
            _tex = new Texture2Drd { TextureRdRid = _posTex };
            _pointsMat = new ShaderMaterial { Shader = new Shader { Code = Shaders.PointsRouteA } };
            _pointsMat.SetShaderParameter("pos_tex", _tex);
            _pointsMat.SetShaderParameter("tex_w", _texW);
            _pointsMat.SetShaderParameter("render_scale", RenderScale);
            _pointsMat.SetShaderParameter("point_size", pointSize);
            _pointsMat.SetShaderParameter("intensity", intensity);
            // The vertex buffer only supplies VERTEX_ID; its contents are never read.
            var arrays = new Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = new Vector3[_n];
            var mesh = new ArrayMesh();
            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Points, arrays);
            mesh.SurfaceSetMaterial(0, _pointsMat);
            AddChild(new MeshInstance3D { Mesh = mesh, CustomAabb = bigAabb, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });

            // Frame-ordering probe, pinned in front of the camera: top row = CPU frame (uniform), bottom = GPU texel.
            _probeMat = new ShaderMaterial { Shader = new Shader { Code = Shaders.Probe } };
            _probeMat.SetShaderParameter("pos_tex", _tex);
            _probeMat.SetShaderParameter("tex_w", _texW);
            _probeMat.SetShaderParameter("tex_h", _texH);
            _probe = new MeshInstance3D { Mesh = new QuadMesh { Size = new Vector2(0.5f, 0.06f) }, MaterialOverride = _probeMat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
            _cam.AddChild(_probe);
            var hh = Mathf.Tan(Mathf.DegToRad(25f));
            _probe.Position = new Vector3(-hh * 16f / 9f + 0.27f, -hh + 0.045f, -1f);
        }
        if (_routeB)
        {
            _mm = new MultiMesh
            {
                TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
                UseColors = true,
                Mesh = new PointMesh(),
                CustomAabb = bigAabb,
            };
            _mm.InstanceCount = _n;
            var mat = new ShaderMaterial { Shader = new Shader { Code = Shaders.PointsRouteB } };
            mat.SetShaderParameter("point_size", pointSize);
            mat.SetShaderParameter("intensity", intensity);
            AddChild(new MultiMeshInstance3D { Multimesh = _mm, MaterialOverride = mat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, CustomAabb = bigAabb });
            _mmBuffer = RenderingServer.MultimeshGetBufferRdRid(_mm.GetRid());
            _report["route_b_buffer_rid_valid"] = _mmBuffer.IsValid;
            if (!_mmBuffer.IsValid) GD.PrintErr("[debris] route b: MultimeshGetBufferRdRid returned an invalid RID");
            else _setMm = Set(_pipeMm, (0, Ssbo, _pos), (1, Ssbo, _vel), (2, Ssbo, _mmBuffer));
        }
    }

    void UpdateMoonNodes(int frame)
    {
        for (var k = 0; k < _moonNodes.Length; k++)
        {
            var (x, y, z) = MoonPos(k, frame * (double)Dt);
            _moonNodes[k].Position = new Vector3((float)(x * RenderScale), (float)(z * RenderScale), (float)(-y * RenderScale));
        }
    }

    // ---------------------------------------------------------------- per frame

    public override void _Process(double delta)
    {
        var a0 = GC.GetAllocatedBytesForCurrentThread();
        _frame++;
        ReadTimestamps();

        FillBodies(_frame - 1);
        var ubo = MemoryMarshal.AsBytes(new ReadOnlySpan<BodiesUbo>(ref _ubo));
        _rd.BufferUpdate(_bodies, 0, (uint)ubo.Length, ubo);
        UpdateMoonNodes(_frame);
        _probeMat?.SetShaderParameter(_cpuFrameParam, _frame);

        var last = _frame == _frames;
        _rd.CaptureTimestamp("dbg0");
        DispatchIntegrate();
        _rd.CaptureTimestamp("dbg1");
        if (_contacts || last || _frame == 1) DispatchGrid();
        _rd.CaptureTimestamp("dbg2");
        if (_contacts) DispatchContacts(0);
        _rd.CaptureTimestamp("dbg3");
        if (_routeB && _setMm.IsValid) DispatchMultimesh();
        _rd.CaptureTimestamp("dbg4");

        if (_frame == 1) FirstFrameChecks();
        if (_routeA && _frame is >= 20 and <= 24) DecodeProbe();
        if (_frame == 50)
        {
            _gc0At50 = GC.CollectionCount(0);
            _threadAllocAt50 = GC.GetAllocatedBytesForCurrentThread();
        }
        if (last) FinalChecks();
        else if (_frame > 50) _myAllocSteady += GC.GetAllocatedBytesForCurrentThread() - a0;
    }

    static void PushU(Span<byte> pc, int slot, uint v) => MemoryMarshal.Cast<byte, uint>(pc)[slot] = v;
    static void PushF(Span<byte> pc, int slot, float v) => MemoryMarshal.Cast<byte, float>(pc)[slot] = v;
    static uint Groups(uint count, uint size) => (count + size - 1) / size;

    void DispatchIntegrate()
    {
        Span<byte> pc = stackalloc byte[32];
        PushU(pc, 0, (uint)_n); PushF(pc, 1, Dt); PushU(pc, 2, (uint)_nBodies); PushF(pc, 3, Eps2);
        PushU(pc, 4, _gravity ? 1u : 0u); PushU(pc, 5, (uint)_frame); PushU(pc, 6, (uint)_texW); PushU(pc, 7, (uint)_texH);
        var cl = _rd.ComputeListBegin();
        _rd.ComputeListBindComputePipeline(cl, _pipeIntegrate);
        _rd.ComputeListBindUniformSet(cl, _setIntegrate, 0);
        _rd.ComputeListSetPushConstant(cl, pc, 32);
        _rd.ComputeListDispatch(cl, Groups((uint)_n, 256), 1, 1);
        _rd.ComputeListEnd();
    }

    void DispatchGrid()
    {
        Span<byte> pc = stackalloc byte[16];
        var cl = _rd.ComputeListBegin();
        pc.Clear(); PushU(pc, 0, _tableSize);
        _rd.ComputeListBindComputePipeline(cl, _pipeClear);
        _rd.ComputeListBindUniformSet(cl, _setClear, 0);
        _rd.ComputeListSetPushConstant(cl, pc, 16);
        _rd.ComputeListDispatch(cl, Groups(_tableSize, 256), 1, 1);
        _rd.ComputeListAddBarrier(cl);

        pc.Clear(); PushU(pc, 0, (uint)_n); PushU(pc, 1, _mask); PushF(pc, 2, _invCell); PushU(pc, 3, _blockHash ? 1u : 0u);
        _rd.ComputeListBindComputePipeline(cl, _pipeCount);
        _rd.ComputeListBindUniformSet(cl, _setCount, 0);
        _rd.ComputeListSetPushConstant(cl, pc, 16);
        _rd.ComputeListDispatch(cl, Groups((uint)_n, 256), 1, 1);
        _rd.ComputeListAddBarrier(cl);

        foreach (var (set, count) in _scanLevels)
        {
            pc.Clear(); PushU(pc, 0, count);
            _rd.ComputeListBindComputePipeline(cl, _pipeScan);
            _rd.ComputeListBindUniformSet(cl, set, 0);
            _rd.ComputeListSetPushConstant(cl, pc, 16);
            _rd.ComputeListDispatch(cl, Groups(count, 1024), 1, 1);
            _rd.ComputeListAddBarrier(cl);
        }
        foreach (var (set, count) in _addLevels)
        {
            pc.Clear(); PushU(pc, 0, count);
            _rd.ComputeListBindComputePipeline(cl, _pipeAdd);
            _rd.ComputeListBindUniformSet(cl, set, 0);
            _rd.ComputeListSetPushConstant(cl, pc, 16);
            _rd.ComputeListDispatch(cl, Groups(count, 256), 1, 1);
            _rd.ComputeListAddBarrier(cl);
        }

        pc.Clear(); PushU(pc, 0, (uint)_n);
        _rd.ComputeListBindComputePipeline(cl, _pipeScatter);
        _rd.ComputeListBindUniformSet(cl, _setScatter, 0);
        _rd.ComputeListSetPushConstant(cl, pc, 16);
        _rd.ComputeListDispatch(cl, Groups((uint)_n, 256), 1, 1);
        _rd.ComputeListEnd();
    }

    void DispatchContacts(uint mode)
    {
        Span<byte> pc = stackalloc byte[16];
        PushU(pc, 0, (uint)_n); PushU(pc, 1, _mask); PushF(pc, 2, _invCell); PushU(pc, 3, mode | (_blockHash ? 2u : 0u));
        var cl = _rd.ComputeListBegin();
        _rd.ComputeListBindComputePipeline(cl, _pipeContacts);
        _rd.ComputeListBindUniformSet(cl, _setContacts, 0);
        _rd.ComputeListSetPushConstant(cl, pc, 16);
        _rd.ComputeListDispatch(cl, Groups((uint)_n, 256), 1, 1);
        _rd.ComputeListEnd();
    }

    void DispatchMultimesh()
    {
        if (!_rd.UniformSetIsValid(_setMm))
        {
            if (!_mmSetInvalidLogged) GD.PrintErr($"[debris] route b: multimesh uniform set became invalid at frame {_frame} (RS reallocated the buffer?)");
            _mmSetInvalidLogged = true;
            return;
        }
        Span<byte> pc = stackalloc byte[16];
        PushU(pc, 0, (uint)_n); PushF(pc, 1, RenderScale); PushU(pc, 2, (uint)_mmStride); PushU(pc, 3, (uint)_frame);
        var cl = _rd.ComputeListBegin();
        _rd.ComputeListBindComputePipeline(cl, _pipeMm);
        _rd.ComputeListBindUniformSet(cl, _setMm, 0);
        _rd.ComputeListSetPushConstant(cl, pc, 16);
        _rd.ComputeListDispatch(cl, Groups((uint)_n, 256), 1, 1);
        _rd.ComputeListEnd();
    }

    void ReadTimestamps()
    {
        var count = _rd.GetCapturedTimestampsCount();
        if (count == 0) return;
        if (_tsResolved && count != _tsCount)
        {
            // Frames that stalled on a readback (GetImage, BufferGetData) carry a partial timestamp set: skip them.
            _tsSkipped++;
            return;
        }
        if (!_tsResolved)
        {
            // Name lookup allocates strings, so it runs only until the indices are known (well before frame 50).
            var found = 0;
            for (var k = 0; k < 5; k++) _tsIdx[k] = -1;
            for (uint i = 0; i < count; i++)
            {
                var name = _rd.GetCapturedTimestampName(i);
                if (name.Length == 4 && name.StartsWith("dbg"))
                {
                    var k = name[3] - '0';
                    if (k is >= 0 and < 5) { _tsIdx[k] = (int)i; found++; }
                }
            }
            _tsResolved = found == 5;
            _tsCount = count;
            if (!_tsResolved) return;
            _tsLag = _frame - 1; // the first frame that carries our names was dispatched at frame 1
            _tsNote = $"indices resolved at frame {_frame}: {string.Join(",", _tsIdx)} of {count} timestamps";
        }
        var row = _frame - _tsLag;
        if (row < 1 || row >= _tInt.Length) return;
        double Ms(int a, int b) => (_rd.GetCapturedTimestampGpuTime((uint)_tsIdx[b]) - (double)_rd.GetCapturedTimestampGpuTime((uint)_tsIdx[a])) / 1e6;
        _tInt[row] = Ms(0, 1);
        _tGrid[row] = Ms(1, 2);
        _tCon[row] = Ms(2, 3);
        _tMm[row] = Ms(3, 4);
        _tTot[row] = Ms(0, 4);
        _tCapFrame[row] = (long)_rd.GetCapturedTimestampsFrame();
        _tHave[row] = true;
        _tsReads++;
    }

    // ---------------------------------------------------------------- checks

    void FirstFrameChecks()
    {
        // Scan correctness: start must be the exclusive prefix sum of cnt, and the total must be n.
        var cnt = MemoryMarshal.Cast<byte, uint>(_rd.BufferGetData(_cnt));
        var start = MemoryMarshal.Cast<byte, uint>(_rd.BufferGetData(_start));
        ulong acc = 0;
        var bad = 0;
        for (var i = 0; i < (int)_tableSize; i++)
        {
            if (start[i] != (uint)acc) bad++;
            acc += cnt[i];
        }
        _report["scan_check"] = $"{(bad == 0 && acc == (ulong)_n ? "PASS" : "FAIL")}: {bad} of {_tableSize} starts wrong, total={acc} (n={_n})";
        GD.Print($"[debris] scan {_report["scan_check"]}");

        // Texture layout: texel i must hold pos[i].xyz after this frame's integrate.
        var texel = MemoryMarshal.Cast<byte, Float4>(_rd.TextureGetData(_posTex, 0));
        var p = MemoryMarshal.Cast<byte, Float4>(_rd.BufferGetData(_pos));
        // Contacts re-sort pos after the texture write, so compare order-independently: sorted x, y, z columns.
        var mism = 0;
        for (var axis = 0; axis < 3; axis++)
        {
            var a = new float[_n];
            var b = new float[_n];
            for (var i = 0; i < _n; i++)
            {
                a[i] = axis == 0 ? texel[i].X : axis == 1 ? texel[i].Y : texel[i].Z;
                b[i] = axis == 0 ? p[i].X : axis == 1 ? p[i].Y : p[i].Z;
            }
            Array.Sort(a);
            Array.Sort(b);
            for (var i = 0; i < _n; i++) if (a[i] != b[i]) mism++;
        }
        var probe = texel[_texW * _texH - 1].X;
        _report["texture_check"] = $"{mism} of {_n} sorted texel coords differ from pos buffer; probe texel={probe}";
        GD.Print($"[debris] texture {_report["texture_check"]}");

        _report["overlaps_frame1"] = CountOverlaps();
        GD.Print($"[debris] overlapping pairs at frame 1: {_report["overlaps_frame1"]}");
    }

    uint CountOverlaps()
    {
        _rd.BufferUpdate(_stats, 0, 16, stackalloc byte[16]);
        DispatchContacts(1);
        return MemoryMarshal.Cast<byte, uint>(_rd.BufferGetData(_stats))[0];
    }

    void DecodeProbe()
    {
        if (_probe == null) return;
        var img = GetViewport().GetTexture().GetImage();
        int Row(float v, List<Vector2> pxLog)
        {
            var val = 0;
            for (var bit = 0; bit < 16; bit++)
            {
                var u = (bit + 0.5f) / 16f;
                var local = new Vector3((u - 0.5f) * 0.5f, (0.5f - v) * 0.06f, 0);
                var px = _cam.UnprojectPosition(_probe.GlobalTransform * local);
                if (_frame == 20) pxLog.Add(px);
                var c = img.GetPixel((int)px.X, (int)px.Y);
                if (c.R > 0.5f) val |= 1 << bit;
            }
            return val;
        }
        var cpu = Row(0.25f, _probeCpuPx);
        var gpu = Row(0.75f, _probeGpuPx);
        var line = $"process_frame={_frame} image: cpu_uniform_row={cpu} gpu_texel_row={gpu} (draw_minus_compute={cpu - gpu})";
        _probeLog.Add(line);
        GD.Print($"[debris] probe {line}");
    }

    void FinalChecks()
    {
        var gcEnd = GC.CollectionCount(0);
        _report["gc0_at_50"] = _gc0At50;
        _report["gc0_at_end"] = gcEnd;
        _report["my_process_alloc_bytes_frames_51_to_end"] = _myAllocSteady;
        _report["thread_alloc_bytes_50_to_end"] = GC.GetAllocatedBytesForCurrentThread() - _threadAllocAt50;
        _report["vram_rd_end_mb"] = MemMb();

        _report["overlaps_end"] = CountOverlaps();
        var pos = MemoryMarshal.Cast<byte, Float4>(_rd.BufferGetData(_pos)).ToArray();
        var vel = MemoryMarshal.Cast<byte, Float4>(_rd.BufferGetData(_vel)).ToArray();
        var (p0, e0, s0, m0) = Momentum(_pos0, _vel0);
        var (p1, e1, s1, m1) = Momentum(pos, vel);
        var dp = (p1.x - p0.x, p1.y - p0.y, p1.z - p0.z);
        var dpMag = Math.Sqrt(dp.Item1 * dp.Item1 + dp.Item2 * dp.Item2 + dp.Item3 * dp.Item3);
        _report["momentum_start"] = $"({p0.x:E4}, {p0.y:E4}, {p0.z:E4})";
        _report["momentum_end"] = $"({p1.x:E4}, {p1.y:E4}, {p1.z:E4})";
        _report["momentum_abs_scale_sum_m_absv"] = s0;
        _report["momentum_rel_change"] = dpMag / s0;
        _report["kinetic_rel_change"] = (e1 - e0) / e0;
        _report["mass_rel_change"] = (m1 - m0) / m0;
        var nan = 0;
        for (var i = 0; i < _n; i++) if (!float.IsFinite(pos[i].X) || !float.IsFinite(vel[i].X)) nan++;
        _report["nonfinite_particles"] = nan;

        if (_routeB && _mmBuffer.IsValid)
        {
            var mm = MemoryMarshal.Cast<byte, float>(_rd.BufferGetData(_mmBuffer, 0, 64));
            _report["route_b_mm_buffer_instance0"] = string.Join(",", mm.ToArray());
            _report["route_b_set_invalidated"] = _mmSetInvalidLogged;
        }
        _report["probe"] = _probeLog;
        _report["probe_px_cpu_row"] = _probeCpuPx.ConvertAll(v => $"{(int)v.X},{(int)v.Y}");
        _report["probe_px_gpu_row"] = _probeGpuPx.ConvertAll(v => $"{(int)v.X},{(int)v.Y}");
        _report["timestamps_lag_frames"] = _tsLag;
        _report["timestamps_reads"] = _tsReads;
        _report["timestamps_note"] = _tsNote;
        _report["timestamps_skipped_partial_frames"] = _tsSkipped;
        _report["rd_frame_delay"] = _rd.GetFrameDelay();

        using (var w = new StreamWriter(Path.Combine(_outDir, "compute.csv"), false, new UTF8Encoding(false)))
        {
            w.WriteLine("frame,integrate_ms,grid_ms,contacts_ms,mm_ms,total_ms,rd_captured_frame");
            for (var f = 1; f < _tInt.Length; f++)
                if (_tHave[f])
                    w.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{f},{_tInt[f]:F4},{_tGrid[f]:F4},{_tCon[f]:F4},{_tMm[f]:F4},{_tTot[f]:F4},{_tCapFrame[f]}"));
        }
        File.WriteAllText(Path.Combine(_outDir, "result.json"), System.Text.Json.JsonSerializer.Serialize(_report, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) + "\n");
        foreach (var (k, v) in _report)
            if (k != "probe_px_cpu_row" && k != "probe_px_gpu_row")
                GD.Print($"[debris] {k} = {(v is List<string> l ? string.Join(" | ", l) : v)}");
    }

    static ((double x, double y, double z) p, double ke, double absSum, double mass) Momentum(Float4[] pos, Float4[] vel)
    {
        double px = 0, py = 0, pz = 0, ke = 0, abs = 0, mt = 0;
        for (var i = 0; i < pos.Length; i++)
        {
            double r = pos[i].W, m = r * r * r;
            double vx = vel[i].X, vy = vel[i].Y, vz = vel[i].Z;
            px += m * vx; py += m * vy; pz += m * vz;
            var v2 = vx * vx + vy * vy + vz * vz;
            ke += 0.5 * m * v2;
            abs += m * Math.Sqrt(v2);
            mt += m;
        }
        return ((px, py, pz), ke, abs, mt);
    }

    string MemMb() => string.Create(CultureInfo.InvariantCulture,
        $"buffers={_rd.GetMemoryUsage(RenderingDevice.MemoryType.Buffers) / 1048576.0:F1} textures={_rd.GetMemoryUsage(RenderingDevice.MemoryType.Textures) / 1048576.0:F1} total={_rd.GetMemoryUsage(RenderingDevice.MemoryType.Total) / 1048576.0:F1}");

    void Fail(string msg)
    {
        GD.PrintErr("[debris] " + msg);
        GetTree().Quit(2);
    }

    // ---------------------------------------------------------------- teardown

    public override void _ExitTree()
    {
        if (_rd == null) return;
        // Detach the RD texture from the Texture2DRD before freeing it, so the material never samples a dead RID.
        if (_tex != null) _tex.TextureRdRid = default;
        var freed = 0;
        foreach (var s in _sets) if (_rd.UniformSetIsValid(s)) { _rd.FreeRid(s); freed++; }
        foreach (var p in _pipelines) if (p.IsValid) { _rd.FreeRid(p); freed++; }
        foreach (var s in _shaders) if (s.IsValid) { _rd.FreeRid(s); freed++; }
        foreach (var b in _buffers) if (b.IsValid) { _rd.FreeRid(b); freed++; }
        GD.Print($"[debris] freed {freed} RIDs (sets {_sets.Count}, pipelines {_pipelines.Count}, shaders {_shaders.Count}, buffers+textures {_buffers.Count}); rd {MemMb()}");
        _sets.Clear(); _pipelines.Clear(); _shaders.Clear(); _buffers.Clear();
    }
}
