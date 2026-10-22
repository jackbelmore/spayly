using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Godot;
using Spayly.Sim;
using Spayly.Sim.Sph;

namespace Spayly.Game.Render;

/// <summary>std430 vec4 of floats: 16 bytes, 16-byte stride.</summary>
[StructLayout(LayoutKind.Explicit, Size = 16)]
public struct SphF4
{
    [FieldOffset(0)] public float X;
    [FieldOffset(4)] public float Y;
    [FieldOffset(8)] public float Z;
    [FieldOffset(12)] public float W;

    public SphF4(double x, double y, double z, double w) { X = (float)x; Y = (float)y; Z = (float)z; W = (float)w; }
}

/// <summary>std430 uvec4: (material, source body, particle id, 0).</summary>
[StructLayout(LayoutKind.Explicit, Size = 16)]
public struct SphU4
{
    [FieldOffset(0)] public uint X;
    [FieldOffset(4)] public uint Y;
    [FieldOffset(8)] public uint Z;
    [FieldOffset(12)] public uint W;
}

/// <summary>The Params SSBO every sph_*.glsl that steps time declares: 32 bytes.</summary>
[StructLayout(LayoutKind.Explicit, Size = 32)]
public struct SphParams
{
    [FieldOffset(0)] public float Dt;
    [FieldOffset(4)] public float Time;
    [FieldOffset(8)] public uint DtBits;
    [FieldOffset(12)] public uint Steps;
    [FieldOffset(16)] public float DtMax;
    [FieldOffset(20)] public float Growth;
    [FieldOffset(24)] public float FixedDt;
    [FieldOffset(28)] public uint Pad;
}

/// <summary>The push-constant block of sph_common.glslinc, field for field: 80 bytes.</summary>
[StructLayout(LayoutKind.Explicit, Size = 80)]
public struct SphPc
{
    [FieldOffset(0)] public uint N;
    [FieldOffset(4)] public uint Mask;
    [FieldOffset(8)] public float Cell;
    [FieldOffset(12)] public float InvCell;
    [FieldOffset(16)] public float Eta;
    [FieldOffset(20)] public float HMin;
    [FieldOffset(24)] public float HMax;
    [FieldOffset(28)] public float Eps2;
    [FieldOffset(32)] public float Alpha;
    [FieldOffset(36)] public float Beta;
    [FieldOffset(40)] public float Courant;
    [FieldOffset(44)] public float AccFactor;
    [FieldOffset(48)] public float GravFactor;
    [FieldOffset(52)] public float G;
    [FieldOffset(56)] public float PFloor;
    [FieldOffset(60)] public uint Flags;
    [FieldOffset(64)] public float GBox;
    [FieldOffset(68)] public float Theta;
    [FieldOffset(72)] public uint Levels;
    [FieldOffset(76)] public uint Count;
}

/// <summary>
/// The hero-collision SPH solver on the GPU: the loops of <see cref="SphSolver"/> as GLSL compute on the main
/// RenderingDevice, following the research §1.3 structure per substep — kick+drift → hash/count → scan → scatter (the
/// whole state is re-sorted into the other buffer side) → density+EOS → forces+AV → gravity → kick → dt — and drawing the
/// particles with spike M1a's route a (RD textures read by VERTEX_ID, no CPU round trip).
///
/// float32 throughout, positions relative to the collision's centre of mass (the caller keeps that point in double and
/// places this node at it). Timestep chosen on the GPU (atomicMin reduction), so stepping never waits on the CPU; the
/// CPU reads the clock back asynchronously every 20 frames and the whole state only when it asks (<see cref="Download"/>).
///
/// Gravity: tiled direct summation up to <see cref="DirectMax"/> particles; above that a Barnes–Hut walk of a complete
/// octree (a dense pyramid of Levels = 6, 64³ leaves over a box around the CM) built each substep by counting sort into
/// Morton-ordered leaves, a leaf pass and an up-sweep, with the monopole + quadrupole opening rules of <see cref="Octree"/>.
/// Every RID is freed in <see cref="Release"/> (called from _ExitTree).
/// </summary>
public sealed partial class SphGpu : Node3D
{
    /// <summary>Largest N for tiled direct gravity; above it the pyramid tree.</summary>
    public const int DirectMax = 20_000;
    /// <summary>Pyramid depth: 8^6 = 262,144 leaves.</summary>
    public const int Levels = 6;
    private const int Wg = 128;
    private const int TexW = 1024;
    private const string ShaderDir = "res://Shaders/";
    private const RenderingDevice.UniformType Ssbo = RenderingDevice.UniformType.StorageBuffer;

    private RenderingDevice rd = null!;
    private readonly List<Rid> buffers = [], sets = [];
    private readonly Dictionary<Rid, Rid> shaderOf = [];
    private Rid pKickDrift, pClear, pCount, pScan, pScanAdd, pScatter, pDensity, pForce, pGravDirect, pGravCount, pGravScatter, pGravLeaf, pGravUp, pGravWalk, pKick, pDt, pRender;
    private readonly Rid[] posh = new Rid[2], velm = new Rid[2], acc = new Rid[2], therm = new Rid[2], pred = new Rid[2], aux = new Rid[2], ids = new Rid[2];
    private Rid cellRank, cnt, start, hmax, paramsBuf, mats, grank, gcnt, gstart, gposm, nodes, posTex, colTex;
    private readonly Rid[] sKickDrift = new Rid[2], sCount = new Rid[2], sScatter = new Rid[2], sDensity = new Rid[2], sForce = new Rid[2];
    private readonly Rid[] sGravDirect = new Rid[2], sGravCount = new Rid[2], sGravScatter = new Rid[2], sGravWalk = new Rid[2], sKick = new Rid[2], sRender = new Rid[2];
    private Rid sClearGrid, sClearGrav, sGravLeaf, sGravUp, sDt;
    private readonly List<(Rid Set, uint Count)> scanLevels = [], scanAdds = [], gScanLevels = [], gScanAdds = [];
    private uint table, leaves;
    private int texH;
    private int side;
    private SphPc pc;
    private bool ready, released, withRender;

    private Texture2Drd? posTexRd, colTexRd;
    private ShaderMaterial? material;
    private MeshInstance3D? meshNode;
    private static readonly StringName PRenderScale = "render_scale", PPxPerRad = "px_per_rad", PIntensity = "intensity";

    private Callable paramsCallback;
    private int frames;
    private int tsA = -1, tsB = -1;
    private uint tsCount;
    private int lastTimedCall = -1;
    // Each Step call names its timestamp pair after itself (M7.4): the results come back some frames later, and how many
    // is not fixed (pauses, readback stalls and fast frames all moved it), so dividing by the substep count of "the call
    // 3 frames ago" charged 16 substeps' time to a 1-substep frame and flipped the controller. The tag is the call number
    // mod TagPeriod; names are built once so capturing allocates nothing. The ring holds the last Ring calls' substep
    // counts; a tag older than that is dropped. TagPeriod ≫ Ring, so a tag cannot alias a newer call.
    private const int Ring = 16, TagPeriod = 64;
    private static readonly string[] TagA = Tags("sph_a:"), TagB = Tags("sph_b:");
    private readonly int[] substepRing = new int[Ring];
    private readonly double[] msRing = new double[120];
    private readonly double[] msScratch = new double[120];
    /// <summary>Every measured substep of this run, for whole-run percentiles (M6.7); sized so a 30 s run never grows it.</summary>
    private readonly List<double> runMs = new(1 << 15);
    private int msCount;

    /// <summary>Particles on the GPU.</summary>
    public int Count { get; private set; }
    /// <summary>Gravity by the pyramid tree (else tiled direct summation).</summary>
    public bool Tree { get; set; }
    /// <summary>SPH time since contact, s, from the latest params readback.</summary>
    public double SimTime { get; private set; }
    /// <summary>The Δt the GPU will take next, s, from the latest readback.</summary>
    public double NextDt { get; private set; }
    public long StepsTaken { get; private set; }
    /// <summary>GPU time of one substep, ms, from the frame timestamps (a few frames late); 0 until measured.</summary>
    public double MsPerSubstep { get; private set; }
    /// <summary>Timestamp pairs thrown away: tag unreadable, pair split, or older than the ring. Logged, so a capture shows the tags round-trip.</summary>
    public int TimestampsDropped { get; private set; }
    /// <summary>Gravity pyramid half-size, m.</summary>
    public double GravityBox => pc.GBox;

    // ---------------------------------------------------------------- setup

    /// <summary>
    /// Compile, allocate and upload <paramref name="s"/> (SI, CM frame) with the constants of <paramref name="o"/>, which
    /// must carry CellSize and Softening (<see cref="HeroCollision.ResolvedOptions"/>), then evaluate forces at t = 0.
    /// <paramref name="gravityBox"/> (m) is the pyramid's half-size. Throws when a shader does not compile.
    /// </summary>
    public void Init(SphState s, SphOptions o, double gravityBox, bool? tree = null, bool render = true, Packed? packed = null)
    {
        if (o.CellSize <= 0 || o.Softening <= 0) throw new ArgumentException("SphOptions need CellSize and Softening resolved", nameof(o));
        rd = RenderingServer.GetRenderingDevice() ?? throw new InvalidOperationException("no RenderingDevice (compatibility renderer?)");
        Count = s.Count;
        Tree = tree ?? Count > DirectMax;
        withRender = render;
        table = 1024;
        while (table < 2 * (uint)Count) table <<= 1;
        leaves = 1u << (3 * Levels);
        texH = (Count + TexW - 1) / TexW;
        var cell = o.CellSize;
        pc = new SphPc
        {
            N = (uint)Count, Mask = table - 1, Cell = (float)cell, InvCell = (float)(1 / cell), Eta = (float)o.Eta,
            HMin = (float)(o.HMinFraction * cell), HMax = (float)cell, Eps2 = (float)(o.Softening * o.Softening),
            Alpha = (float)o.Alpha, Beta = (float)o.Beta, Courant = (float)o.Courant, AccFactor = (float)o.AccelerationFactor,
            GravFactor = (float)o.GravityFactor, G = (float)o.G, PFloor = (float)o.PressureFloor, Flags = o.Balsara ? 1u : 0u,
            GBox = (float)gravityBox, Theta = (float)o.Theta, Levels = Levels, Count = 0,
        };
        CheckLayout();
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        CompileAll();
        var t1 = System.Diagnostics.Stopwatch.GetTimestamp();
        CreateBuffers(s);
        CreateSets();
        paramsCallback = Callable.From<byte[]>(OnParams);
        WriteParams(o);
        UploadPacked(packed ?? Pack(s, o));
        if (withRender) BuildMesh();
        ready = true;
        InitialForces();
        GD.Print(string.Create(CultureInfo.InvariantCulture,
            $"[sph] gpu ready: n={Count} table={table} cell={cell / 1e3:F1} km eps={o.Softening / 1e3:F1} km gravity={(Tree ? "pyramid tree" : "direct")} box={gravityBox / 1e3:F0} km tex={TexW}x{texH} init_ms={System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds:F1} (pipelines {System.Diagnostics.Stopwatch.GetElapsedTime(t0, t1).TotalMilliseconds:F1}{(packed != null ? ", packed off the main thread" : "")})"));
    }

    private static void CheckLayout()
    {
        if (Unsafe.SizeOf<SphF4>() != 16 || Unsafe.SizeOf<SphU4>() != 16 || Unsafe.SizeOf<SphParams>() != 32 || Unsafe.SizeOf<SphPc>() != 80)
            throw new InvalidOperationException("SPH GPU struct layout differs from the GLSL declarations");
    }

    /// <summary>
    /// GLSL text of a Shaders/ file for runtime compilation: Godot's <c>#[compute]</c> stage marker dropped and
    /// <c>#include "x"</c> inlined from the same directory (so the files also stay valid for Godot's own RDShaderFile import).
    /// </summary>
    private static string Source(string file)
    {
        var text = Godot.FileAccess.GetFileAsString(ShaderDir + file);
        if (string.IsNullOrEmpty(text)) throw new InvalidOperationException($"cannot read {ShaderDir}{file}: {Godot.FileAccess.GetOpenError()}");
        var sb = new StringBuilder(text.Length + 8192);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var t = line.Trim();
            if (t.StartsWith("#[", StringComparison.Ordinal)) continue;
            if (t.StartsWith("#include", StringComparison.Ordinal))
            {
                int q0 = t.IndexOf('"'), q1 = t.LastIndexOf('"');
                if (q0 < 0 || q1 <= q0) throw new InvalidOperationException($"{file}: bad include '{t}'");
                sb.Append(Source(t.Substring(q0 + 1, q1 - q0 - 1))).Append('\n');
                continue;
            }
            sb.Append(line).Append('\n');
        }
        return sb.ToString();
    }

    private static readonly string[] Files =
    [
        "sph_kick_drift.glsl", "sph_clear.glsl", "sph_count.glsl", "sph_scan.glsl", "sph_scan_add.glsl", "sph_scatter.glsl",
        "sph_density.glsl", "sph_force.glsl", "sph_grav_direct.glsl", "sph_grav_count.glsl", "sph_grav_scatter.glsl",
        "sph_grav_leaf.glsl", "sph_grav_up.glsl", "sph_grav_walk.glsl", "sph_kick.glsl", "sph_dt.glsl", "sph_render.glsl",
    ];

    // Compute pipelines and the particles' draw shader, built once for the app's life and shared by every run: compiling the
    // 17 kernels at each start cost 36–45 ms of the start frame (capture m7-perf-sph-before). Freed by FreeShared.
    private static readonly Dictionary<string, (Rid Shader, Rid Pipe)> shared = [];
    private static RenderingDevice? sharedRd;
    private static Shader? pointsShader;

    /// <summary>
    /// Compile every SPH kernel and the points shader now (App start, a one-off load cost) so a hero collision starts
    /// without it. Safe to call again; a kernel that fails is left for <see cref="Init"/> to report.
    /// </summary>
    public static void Warm()
    {
        var device = RenderingServer.GetRenderingDevice();
        if (device == null) return;
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            foreach (var f in Files) SharedPipeline(device, f);
        }
        catch (Exception e)
        {
            GD.PushError($"[sph] warm-up: {e.Message}");
        }
        pointsShader ??= new Shader { Code = PointsShader };
        GD.Print(string.Create(CultureInfo.InvariantCulture, $"[sph] {shared.Count} kernels compiled at load in {System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds:F1} ms"));
    }

    /// <summary>Free the shared pipelines and shaders (app exit), after every run's sets are gone.</summary>
    public static void FreeShared()
    {
        if (sharedRd == null) return;
        foreach (var (_, (sh, pipe)) in shared)
        {
            if (pipe.IsValid) sharedRd.FreeRid(pipe);
            if (sh.IsValid) sharedRd.FreeRid(sh);
        }
        GD.Print($"[sph] freed {shared.Count} shared kernels");
        shared.Clear();
        sharedRd = null;
    }

    private static (Rid Shader, Rid Pipe) SharedPipeline(RenderingDevice device, string file)
    {
        if (shared.TryGetValue(file, out var got)) return got;
        sharedRd = device;
        var source = new RDShaderSource { Language = RenderingDevice.ShaderLanguage.Glsl, SourceCompute = Source(file) };
        var spirv = device.ShaderCompileSpirVFromSource(source);
        var err = spirv.GetStageCompileError(RenderingDevice.ShaderStage.Compute);
        if (!string.IsNullOrEmpty(err))
        {
            GD.PrintErr($"[sph] GLSL compile error in {file}:\n{err}");
            throw new InvalidOperationException($"shader {file} failed to compile");
        }
        var shader = device.ShaderCreateFromSpirV(spirv, file);
        if (!shader.IsValid) throw new InvalidOperationException($"ShaderCreateFromSpirV({file}) returned an invalid RID");
        var pipe = device.ComputePipelineCreate(shader);
        if (!pipe.IsValid)
        {
            device.FreeRid(shader);
            throw new InvalidOperationException($"ComputePipelineCreate({file}) failed");
        }
        shared[file] = (shader, pipe);
        return (shader, pipe);
    }

    private Rid Compile(string file)
    {
        var (shader, pipe) = SharedPipeline(rd, file);
        shaderOf[pipe] = shader;
        return pipe;
    }

    private void CompileAll()
    {
        if (pKickDrift.IsValid) return;
        pKickDrift = Compile("sph_kick_drift.glsl");
        pClear = Compile("sph_clear.glsl");
        pCount = Compile("sph_count.glsl");
        pScan = Compile("sph_scan.glsl");
        pScanAdd = Compile("sph_scan_add.glsl");
        pScatter = Compile("sph_scatter.glsl");
        pDensity = Compile("sph_density.glsl");
        pForce = Compile("sph_force.glsl");
        pGravDirect = Compile("sph_grav_direct.glsl");
        pGravCount = Compile("sph_grav_count.glsl");
        pGravScatter = Compile("sph_grav_scatter.glsl");
        pGravLeaf = Compile("sph_grav_leaf.glsl");
        pGravUp = Compile("sph_grav_up.glsl");
        pGravWalk = Compile("sph_grav_walk.glsl");
        pKick = Compile("sph_kick.glsl");
        pDt = Compile("sph_dt.glsl");
        pRender = Compile("sph_render.glsl");
    }

    private Rid Buf(uint bytes)
    {
        // Zeroed on the GPU: a zero-filled byte[] per buffer (~30 MB at 10⁵ particles) was most of the 23 ms buffer setup.
        var b = rd.StorageBufferCreate(bytes);
        if (!b.IsValid) throw new InvalidOperationException($"StorageBufferCreate({bytes}) failed");
        buffers.Add(b);
        rd.BufferClear(b, 0, bytes);
        return b;
    }

    private void CreateBuffers(SphState s)
    {
        var n4 = (uint)(Count * 16);
        for (var k = 0; k < 2; k++)
        {
            posh[k] = Buf(n4); velm[k] = Buf(n4); acc[k] = Buf(n4); therm[k] = Buf(n4);
            pred[k] = Buf(n4); aux[k] = Buf(n4); ids[k] = Buf(n4);
        }
        cellRank = Buf((uint)(Count * 8));
        cnt = Buf(table * 4);
        start = Buf(table * 4);
        hmax = Buf(table * 4);
        paramsBuf = Buf(32);
        grank = Buf((uint)(Count * 8));
        gcnt = Buf(leaves * 4);
        gstart = Buf(leaves * 4);
        gposm = Buf(n4);
        var nodeCount = ((1u << (3 * (Levels + 1))) - 1) / 7;
        nodes = Buf(nodeCount * 48);

        // Materials UBO (std140 vec4[40]): per material (ρ₀, A, B, E₀), (E_iv, E_cv, a, b), (α, β, 0, 0), (c_v, T_ref, T_melt, L_melt), (colour, 0).
        var ubo = new SphF4[40];
        for (var k = 0; k < Math.Min(8, s.Materials.Length); k++)
        {
            var m = s.Materials[k];
            ubo[5 * k] = new SphF4(m.Rho0, m.A, m.B, m.E0);
            ubo[5 * k + 1] = new SphF4(m.Eiv, m.Ecv, m.SmallA, m.SmallB);
            ubo[5 * k + 2] = new SphF4(m.Alpha, m.Beta, 0, 0);
            ubo[5 * k + 3] = new SphF4(m.HeatCapacity, m.ReferenceTemperature, m.MeltTemperature, m.MeltLatent);
            ubo[5 * k + 4] = new SphF4(m.Color.R, m.Color.G, m.Color.B, 0);
        }
        var bytes = MemoryMarshal.AsBytes(ubo.AsSpan());
        mats = rd.UniformBufferCreate((uint)bytes.Length, bytes);
        if (!mats.IsValid) throw new InvalidOperationException("UniformBufferCreate(materials) failed");
        buffers.Add(mats);

        if (!withRender) return;
        RDTextureFormat Fmt() => new()
        {
            Format = RenderingDevice.DataFormat.R32G32B32A32Sfloat, TextureType = RenderingDevice.TextureType.Type2D,
            Width = TexW, Height = (uint)texH, Depth = 1, ArrayLayers = 1, Mipmaps = 1,
            UsageBits = RenderingDevice.TextureUsageBits.SamplingBit | RenderingDevice.TextureUsageBits.StorageBit
                        | RenderingDevice.TextureUsageBits.CanUpdateBit | RenderingDevice.TextureUsageBits.CanCopyFromBit,
        };
        posTex = rd.TextureCreate(Fmt(), new RDTextureView(), new Godot.Collections.Array<byte[]>());
        colTex = rd.TextureCreate(Fmt(), new RDTextureView(), new Godot.Collections.Array<byte[]>());
        if (!posTex.IsValid || !colTex.IsValid) throw new InvalidOperationException("TextureCreate(rgba32f) failed");
        buffers.Add(posTex);
        buffers.Add(colTex);
    }

    private Rid Set(Rid pipe, params (int Binding, RenderingDevice.UniformType Type, Rid Rid)[] us)
    {
        var arr = new Godot.Collections.Array<RDUniform>();
        foreach (var (binding, type, rid) in us)
        {
            var u = new RDUniform { Binding = binding, UniformType = type };
            u.AddId(rid);
            arr.Add(u);
        }
        var set = rd.UniformSetCreate(arr, shaderOf[pipe], 0);
        if (!set.IsValid) throw new InvalidOperationException("UniformSetCreate failed");
        sets.Add(set);
        return set;
    }

    /// <summary>Multi-level Blelloch scan of <paramref name="count"/> uints from src into dst, as spike M1a.</summary>
    private void BuildScan(Rid src, Rid dst, uint count, List<(Rid, uint)> levels, List<(Rid, uint)> adds)
    {
        var outs = new List<(Rid Dst, uint Count)>();
        while (true)
        {
            var blocks = (count + 1023) / 1024;
            var sums = Buf(Math.Max(blocks, 4) * 4);
            levels.Add((Set(pScan, (0, Ssbo, src), (1, Ssbo, dst), (2, Ssbo, sums)), count));
            outs.Add((dst, count));
            if (blocks == 1) break;
            src = sums;
            dst = Buf(Math.Max(blocks, 4) * 4);
            count = blocks;
        }
        for (var l = outs.Count - 2; l >= 0; l--)
            adds.Add((Set(pScanAdd, (0, Ssbo, outs[l].Dst), (1, Ssbo, outs[l + 1].Dst)), outs[l].Count));
    }

    private void CreateSets()
    {
        const RenderingDevice.UniformType Ubo = RenderingDevice.UniformType.UniformBuffer;
        const RenderingDevice.UniformType Img = RenderingDevice.UniformType.Image;
        for (var a = 0; a < 2; a++)
        {
            var b = 1 - a;
            sKickDrift[a] = Set(pKickDrift, (0, Ssbo, posh[a]), (1, Ssbo, velm[a]), (2, Ssbo, acc[a]), (3, Ssbo, therm[a]), (4, Ssbo, pred[a]), (5, Ssbo, paramsBuf));
            sCount[a] = Set(pCount, (0, Ssbo, posh[a]), (1, Ssbo, cnt), (2, Ssbo, hmax), (3, Ssbo, cellRank));
            sScatter[a] = Set(pScatter,
                (0, Ssbo, posh[a]), (1, Ssbo, velm[a]), (2, Ssbo, acc[a]), (3, Ssbo, therm[a]), (4, Ssbo, pred[a]), (5, Ssbo, ids[a]),
                (6, Ssbo, posh[b]), (7, Ssbo, velm[b]), (8, Ssbo, acc[b]), (9, Ssbo, therm[b]), (10, Ssbo, pred[b]), (11, Ssbo, ids[b]),
                (12, Ssbo, cellRank), (13, Ssbo, start));
            sDensity[a] = Set(pDensity, (0, Ssbo, posh[a]), (1, Ssbo, velm[a]), (2, Ssbo, pred[a]), (3, Ssbo, therm[a]), (4, Ssbo, aux[a]),
                (5, Ssbo, ids[a]), (6, Ssbo, start), (7, Ssbo, cnt), (8, Ubo, mats));
            sForce[a] = Set(pForce, (0, Ssbo, posh[a]), (1, Ssbo, velm[a]), (2, Ssbo, pred[a]), (3, Ssbo, therm[a]), (4, Ssbo, aux[a]),
                (5, Ssbo, acc[a]), (6, Ssbo, start), (7, Ssbo, cnt), (8, Ssbo, hmax));
            sGravDirect[a] = Set(pGravDirect, (0, Ssbo, posh[a]), (1, Ssbo, velm[a]), (2, Ssbo, acc[a]), (3, Ssbo, aux[a]));
            sGravCount[a] = Set(pGravCount, (0, Ssbo, posh[a]), (1, Ssbo, gcnt), (2, Ssbo, grank));
            sGravScatter[a] = Set(pGravScatter, (0, Ssbo, posh[a]), (1, Ssbo, velm[a]), (2, Ssbo, grank), (3, Ssbo, gstart), (4, Ssbo, gposm));
            sGravWalk[a] = Set(pGravWalk, (0, Ssbo, posh[a]), (1, Ssbo, acc[a]), (2, Ssbo, aux[a]), (3, Ssbo, nodes), (4, Ssbo, gposm),
                (5, Ssbo, gstart), (6, Ssbo, gcnt));
            sKick[a] = Set(pKick, (0, Ssbo, posh[a]), (1, Ssbo, velm[a]), (2, Ssbo, acc[a]), (3, Ssbo, therm[a]), (4, Ssbo, aux[a]), (5, Ssbo, paramsBuf));
            if (withRender)
                sRender[a] = Set(pRender, (0, Ssbo, posh[a]), (1, Ssbo, therm[a]), (2, Ssbo, ids[a]), (3, Ubo, mats), (4, Img, posTex), (5, Img, colTex));
        }
        sClearGrid = Set(pClear, (0, Ssbo, cnt), (1, Ssbo, hmax));
        // gstart is rewritten by the scan, so clearing it alongside gcnt is harmless and saves a second clear kernel.
        sClearGrav = Set(pClear, (0, Ssbo, gcnt), (1, Ssbo, gstart));
        sGravLeaf = Set(pGravLeaf, (0, Ssbo, gposm), (1, Ssbo, gstart), (2, Ssbo, gcnt), (3, Ssbo, nodes));
        sGravUp = Set(pGravUp, (0, Ssbo, nodes));
        sDt = Set(pDt, (0, Ssbo, paramsBuf));
        BuildScan(cnt, start, table, scanLevels, scanAdds);
        BuildScan(gcnt, gstart, leaves, gScanLevels, gScanAdds);
    }

    private void WriteParams(SphOptions o)
    {
        var p = new SphParams
        {
            Dt = 0, Time = 0, DtBits = BitConverter.SingleToUInt32Bits(3.0e38f), Steps = 0,
            DtMax = (float)Math.Min(o.DtMax, 1e30), Growth = (float)o.DtGrowth, FixedDt = (float)o.FixedDt, Pad = 0,
        };
        rd.BufferUpdate(paramsBuf, 0, 32, MemoryMarshal.AsBytes(new ReadOnlySpan<SphParams>(ref p)));
        SimTime = 0;
        NextDt = 0;
        StepsTaken = 0;
    }

    /// <summary>The seven particle arrays <see cref="Upload"/> writes, packed as the GPU wants them.</summary>
    public sealed class Packed
    {
        public required SphF4[] P, V, A, T, Q, X;
        public required SphU4[] Id;
    }

    /// <summary>
    /// Pack <paramref name="s"/> for upload with the smoothing-length clamp of <paramref name="o"/> (resolved). Pure CPU, so
    /// App packs on the worker that builds the bodies: packing 10⁵ particles here took ~10 ms of the start frame.
    /// </summary>
    public static Packed Pack(SphState s, SphOptions o) => Pack(s, (float)(o.HMinFraction * o.CellSize), (float)o.CellSize);

    private static Packed Pack(SphState s, float hMin, float hMax)
    {
        var n = s.Count;
        var k = new Packed
        {
            P = new SphF4[n], V = new SphF4[n], A = new SphF4[n], T = new SphF4[n], Q = new SphF4[n], X = new SphF4[n], Id = new SphU4[n],
        };
        for (var i = 0; i < n; i++)
        {
            var h = Math.Clamp(s.H[i], hMin, hMax);
            k.P[i] = new SphF4(s.X[i], s.Y[i], s.Z[i], h);
            k.V[i] = new SphF4(s.Vx[i], s.Vy[i], s.Vz[i], s.M[i]);
            k.A[i] = new SphF4(s.Ax[i], s.Ay[i], s.Az[i], s.DuDt[i]);
            k.T[i] = new SphF4(s.U[i], s.Rho[i] > 0 ? s.Rho[i] : 1, s.P[i], s.Cs[i]);
            k.Q[i] = new SphF4(s.Vx[i], s.Vy[i], s.Vz[i], s.U[i]);
            k.X[i] = new SphF4(1, 0, 0, 0);
            k.Id[i] = new SphU4 { X = (uint)s.Mat[i], Y = (uint)s.Body[i], Z = (uint)i };
        }
        return k;
    }

    /// <summary>Write <paramref name="s"/> into buffer side 0 (particle k gets id k), predicted v and u set to the current ones.</summary>
    public void Upload(SphState s)
    {
        if (s.Count != Count) throw new ArgumentException($"state has {s.Count} particles, GPU {Count}");
        UploadPacked(Pack(s, pc.HMin, pc.HMax));
    }

    private void UploadPacked(Packed k)
    {
        if (k.P.Length != Count) throw new ArgumentException($"packed state has {k.P.Length} particles, GPU {Count}");
        void Up<T>(Rid rid, T[] data) where T : unmanaged
        {
            var bytes = MemoryMarshal.AsBytes(data.AsSpan());
            rd.BufferUpdate(rid, 0, (uint)bytes.Length, bytes);
        }
        Up(posh[0], k.P); Up(velm[0], k.V); Up(acc[0], k.A); Up(therm[0], k.T); Up(pred[0], k.Q); Up(aux[0], k.X); Up(ids[0], k.Id);
        side = 0;
    }

    // ---------------------------------------------------------------- stepping

    private static uint Groups(uint n, uint size) => (n + size - 1) / size;

    private void Run(long cl, Rid pipe, Rid set, uint groups, uint count = 0)
    {
        var p = pc;
        p.Count = count;
        rd.ComputeListBindComputePipeline(cl, pipe);
        rd.ComputeListBindUniformSet(cl, set, 0);
        rd.ComputeListSetPushConstant(cl, MemoryMarshal.AsBytes(new ReadOnlySpan<SphPc>(ref p)), 80);
        rd.ComputeListDispatch(cl, groups, 1, 1);
        rd.ComputeListAddBarrier(cl);
    }

    private void RecordSubstep(long cl, bool kickDrift)
    {
        int s = side, t = 1 - side;
        var n = (uint)Count;
        if (kickDrift) Run(cl, pKickDrift, sKickDrift[s], Groups(n, Wg));
        Run(cl, pClear, sClearGrid, Groups(table, 256), table);
        Run(cl, pCount, sCount[s], Groups(n, 256));
        foreach (var (set, c) in scanLevels) Run(cl, pScan, set, Groups(c, 1024), c);
        foreach (var (set, c) in scanAdds) Run(cl, pScanAdd, set, Groups(c, 256), c);
        Run(cl, pScatter, sScatter[s], Groups(n, 256));
        Run(cl, pDensity, sDensity[t], Groups(n, Wg));
        Run(cl, pForce, sForce[t], Groups(n, Wg));
        if (Tree)
        {
            Run(cl, pClear, sClearGrav, Groups(leaves, 256), leaves);
            Run(cl, pGravCount, sGravCount[t], Groups(n, 256));
            foreach (var (set, c) in gScanLevels) Run(cl, pScan, set, Groups(c, 1024), c);
            foreach (var (set, c) in gScanAdds) Run(cl, pScanAdd, set, Groups(c, 256), c);
            Run(cl, pGravScatter, sGravScatter[t], Groups(n, 256));
            Run(cl, pGravLeaf, sGravLeaf, Groups(leaves, 256));
            for (var l = Levels - 1; l >= 0; l--) Run(cl, pGravUp, sGravUp, Groups(1u << (3 * l), 256), (uint)l);
            Run(cl, pGravWalk, sGravWalk[t], Groups(n, Wg));
        }
        else Run(cl, pGravDirect, sGravDirect[t], Groups(n, 256));
        Run(cl, pKick, sKick[t], Groups(n, Wg));
        Run(cl, pDt, sDt, 1);
        side = t;
    }

    private void RecordRender(long cl)
    {
        if (withRender) Run(cl, pRender, sRender[side], Groups((uint)Count, 256), TexW);
    }

    /// <summary>Forces and the first Δt at the uploaded positions, without moving anything (Δt = 0 in params).</summary>
    public void InitialForces()
    {
        var cl = rd.ComputeListBegin();
        RecordSubstep(cl, false);
        RecordRender(cl);
        rd.ComputeListEnd();
    }

    /// <summary>Record <paramref name="substeps"/> KDK steps (and the draw textures) for this frame. Allocates nothing.</summary>
    public void Step(int substeps, bool render = true)
    {
        if (!ready || substeps <= 0) return;
        ReadTimestamps();
        var tag = frames % TagPeriod;
        rd.CaptureTimestamp(TagA[tag]);
        var cl = rd.ComputeListBegin();
        for (var k = 0; k < substeps; k++) RecordSubstep(cl, true);
        if (render) RecordRender(cl);
        rd.ComputeListEnd();
        rd.CaptureTimestamp(TagB[tag]);
        substepRing[frames % Ring] = substeps;
        frames++;
        if (frames % 20 == 0) rd.BufferGetDataAsync(paramsBuf, paramsCallback, 0, 32);
    }

    /// <summary>Change the pyramid's half-size (m), e.g. as ejecta spread.</summary>
    public void SetGravityBox(double box) => pc.GBox = (float)box;

    private void OnParams(byte[] data)
    {
        if (data.Length < 32) return;
        var p = MemoryMarshal.Read<SphParams>(data);
        SimTime = p.Time;
        NextDt = p.Dt;
        StepsTaken = p.Steps;
    }

    private static string[] Tags(string prefix)
    {
        var t = new string[TagPeriod];
        for (var i = 0; i < TagPeriod; i++) t[i] = prefix + i.ToString(CultureInfo.InvariantCulture);
        return t;
    }

    /// <summary>The call tag in <paramref name="name"/> ("sph_a:17" → 17), or -1 when it is not one of ours. Allocates nothing.</summary>
    private static int TagOf(string name, string prefix)
    {
        if (!name.StartsWith(prefix, StringComparison.Ordinal) || name.Length == prefix.Length || name.Length > prefix.Length + 2) return -1;
        var v = 0;
        for (var i = prefix.Length; i < name.Length; i++)
        {
            var d = name[i] - '0';
            if (d is < 0 or > 9) return -1;
            v = 10 * v + d;
        }
        return v < TagPeriod ? v : -1;
    }

    private void ReadTimestamps()
    {
        var count = rd.GetCapturedTimestampsCount();
        if (count == 0) return;
        if (count != tsCount)
        {
            // Resolving every index allocates a string per timestamp, so only when the set of timestamps changes.
            tsCount = count;
            tsA = tsB = -1;
            for (uint i = 0; i < count; i++)
            {
                var name = rd.GetCapturedTimestampName(i);
                if (TagOf(name, "sph_a:") >= 0) tsA = (int)i;
                else if (TagOf(name, "sph_b:") >= 0) tsB = (int)i;
            }
        }
        if (tsA < 0 || tsB < 0) return;
        // The tag changes every call, so these two names are read every frame: two short strings, the price of knowing
        // which call the interval belongs to.
        int ta = TagOf(rd.GetCapturedTimestampName((uint)tsA), "sph_a:"), tb = TagOf(rd.GetCapturedTimestampName((uint)tsB), "sph_b:");
        if (ta < 0 || ta != tb)
        {
            // A frame that stalled on a readback carries a partial set at the same count: re-resolve next frame.
            tsCount = 0;
            TimestampsDropped++;
            return;
        }
        // The newest call carrying this tag; frames − 1 is the last call recorded, since this runs before the new capture.
        var age = ((frames - 1 - ta) % TagPeriod + TagPeriod) % TagPeriod;
        var call = frames - 1 - age;
        if (call <= lastTimedCall) return; // the same set seen again (no frame drawn in between)
        if (age >= Ring || call < 0)
        {
            TimestampsDropped++;
            return;
        }
        lastTimedCall = call;
        var ms = (rd.GetCapturedTimestampGpuTime((uint)tsB) - (double)rd.GetCapturedTimestampGpuTime((uint)tsA)) / 1e6;
        var k = substepRing[call % Ring];
        if (ms <= 0 || k <= 0) return;
        MsPerSubstep = ms / k;
        msRing[msCount % msRing.Length] = MsPerSubstep;
        msCount++;
        runMs.Add(MsPerSubstep);
    }

    /// <summary>p50, p95 and max over every substep measured since <see cref="Init"/>, ms (allocates: call once, at the end).</summary>
    public (double P50, double P95, double Max, int Samples) RunStats()
    {
        var n = runMs.Count;
        if (n == 0) return (0, 0, 0, 0);
        var a = runMs.ToArray();
        Array.Sort(a);
        return (a[n / 2], a[Math.Min(n - 1, (int)(0.95 * n))], a[n - 1], n);
    }

    /// <summary>p50 and p95 of the last 120 measured substeps, ms (0, 0 before any).</summary>
    public (double P50, double P95, int Samples) SubstepStats()
    {
        var n = Math.Min(msCount, msRing.Length);
        if (n == 0) return (0, 0, 0);
        Array.Copy(msRing, msScratch, n);
        Array.Sort(msScratch, 0, n);
        return (msScratch[n / 2], msScratch[Math.Min(n - 1, (int)(0.95 * n))], n);
    }

    // ---------------------------------------------------------------- readback

    /// <summary>Clock and step count now (a GPU sync).</summary>
    public (double Time, double Dt, long Steps) ReadParams()
    {
        var p = MemoryMarshal.Read<SphParams>(rd.BufferGetData(paramsBuf, 0, 32));
        SimTime = p.Time;
        NextDt = p.Dt;
        StepsTaken = p.Steps;
        return (SimTime, NextDt, StepsTaken);
    }

    /// <summary>Copy the GPU state into <paramref name="s"/>, each particle back at its id (a GPU sync; for checks and demotion).</summary>
    public void Download(SphState s)
    {
        if (s.Count != Count) throw new ArgumentException($"state has {s.Count} particles, GPU {Count}");
        // A checkpoint still unpacking into the same state would race this copy (the wall cap can end a run meanwhile).
        try { unpacking?.Wait(); }
        catch (AggregateException) { }
        unpacking = null;
        asyncTarget = null;
        asyncWaiting = 0;
        Unpack(s, rd.BufferGetData(posh[side]), rd.BufferGetData(velm[side]), rd.BufferGetData(acc[side]), rd.BufferGetData(therm[side]),
            rd.BufferGetData(aux[side]), rd.BufferGetData(ids[side]));
        ReadParams();
    }

    readonly byte[][] asyncData = new byte[7][];
    Callable[]? asyncCallbacks;
    int asyncWaiting;
    SphState? asyncTarget;
    Func<SphState, double>? asyncBox;
    System.Threading.Tasks.Task<double>? unpacking;

    /// <summary>
    /// An <see cref="DownloadAsync"/> is in flight or still being unpacked. The caller runs no substeps meanwhile, so the
    /// buffers hold the checkpoint.
    /// </summary>
    public bool DownloadPending => asyncWaiting > 0 || unpacking is { IsCompleted: false };

    /// <summary>
    /// The last <see cref="DownloadAsync"/> has landed in its state (and the clock); true once, then false until the next.
    /// <paramref name="box"/> is what the download's box function gave for the landed state (NaN without one).
    /// </summary>
    public bool TakeDownload(out double box)
    {
        box = double.NaN;
        if (unpacking is not { IsCompleted: true } u) return false;
        unpacking = null;
        if (u.IsFaulted)
        {
            GD.PushError($"[sph] checkpoint unpack failed: {u.Exception?.GetBaseException().Message}");
            return false;
        }
        box = u.Result;
        return true;
    }

    /// <summary>
    /// <see cref="Download"/> without the sync: the six particle buffers and the clock come back 2–3 frames later. The sync
    /// version held the frame for 50–60 ms at 10⁵ particles every checkpoint (M7.5 capture m7-3-sph-undo). They are unpacked
    /// into <paramref name="s"/> on a worker, which then evaluates <paramref name="boxOf"/> on it: on the main thread the
    /// unpack cost 15–26 ms and the gravity box's sort 6–8 ms of a checkpoint frame (capture m7-perf-sph-prof).
    /// </summary>
    public void DownloadAsync(SphState s, Func<SphState, double>? boxOf = null)
    {
        if (s.Count != Count) throw new ArgumentException($"state has {s.Count} particles, GPU {Count}");
        if (asyncCallbacks == null)
        {
            asyncCallbacks = new Callable[7];
            for (var i = 0; i < 7; i++)
            {
                var slot = i;
                asyncCallbacks[i] = Callable.From<byte[]>(d => OnAsyncData(slot, d));
            }
        }
        asyncTarget = s;
        asyncBox = boxOf;
        asyncWaiting = 7;
        unpacking = null;
        Rid[] bufs = [posh[side], velm[side], acc[side], therm[side], aux[side], ids[side]];
        for (var i = 0; i < 6; i++) rd.BufferGetDataAsync(bufs[i], asyncCallbacks[i], 0, 0);
        rd.BufferGetDataAsync(paramsBuf, asyncCallbacks[6], 0, 32);
    }

    void OnAsyncData(int slot, byte[] data)
    {
        if (asyncTarget == null || asyncWaiting <= 0) return;
        asyncData[slot] = data;
        if (--asyncWaiting > 0) return;
        OnParams(asyncData[6]);
        var (target, boxOf) = (asyncTarget, asyncBox);
        var (pb, vb, ab, tb, xb, idb) = (asyncData[0], asyncData[1], asyncData[2], asyncData[3], asyncData[4], asyncData[5]);
        Array.Clear(asyncData);
        asyncTarget = null;
        asyncBox = null;
        // The buffers are the callbacks' own arrays and the state is the caller's alone until TakeDownload says it landed.
        unpacking = System.Threading.Tasks.Task.Run(() =>
        {
            Unpack(target, pb, vb, ab, tb, xb, idb);
            return boxOf != null ? boxOf(target) : double.NaN;
        });
    }

    void Unpack(SphState s, byte[] pb, byte[] vb, byte[] ab, byte[] tb, byte[] xb, byte[] idb)
    {
        var p = MemoryMarshal.Cast<byte, SphF4>(pb);
        var v = MemoryMarshal.Cast<byte, SphF4>(vb);
        var a = MemoryMarshal.Cast<byte, SphF4>(ab);
        var t = MemoryMarshal.Cast<byte, SphF4>(tb);
        var x = MemoryMarshal.Cast<byte, SphF4>(xb);
        var id = MemoryMarshal.Cast<byte, SphU4>(idb);
        for (var k = 0; k < Count; k++)
        {
            var i = (int)id[k].Z;
            if ((uint)i >= (uint)Count) continue;
            s.X[i] = p[k].X; s.Y[i] = p[k].Y; s.Z[i] = p[k].Z; s.H[i] = p[k].W;
            s.Vx[i] = v[k].X; s.Vy[i] = v[k].Y; s.Vz[i] = v[k].Z; s.M[i] = v[k].W;
            s.Ax[i] = a[k].X; s.Ay[i] = a[k].Y; s.Az[i] = a[k].Z; s.DuDt[i] = a[k].W;
            s.U[i] = t[k].X; s.Rho[i] = t[k].Y; s.P[i] = t[k].Z; s.Cs[i] = t[k].W;
            s.Balsara[i] = x[k].X;
            s.Mat[i] = (int)id[k].X; s.Body[i] = (int)id[k].Y;
        }
    }

    // ---------------------------------------------------------------- drawing

    private const string PointsShader = """
        shader_type spatial;
        render_mode unshaded, cull_disabled, shadows_disabled, fog_disabled;
        uniform sampler2D pos_tex : filter_nearest;
        uniform sampler2D col_tex : filter_nearest;
        uniform int tex_w;
        uniform float render_scale;
        uniform float px_per_rad;
        uniform float intensity;
        varying vec3 col;
        void vertex() {
          ivec2 t = ivec2(VERTEX_ID % tex_w, VERTEX_ID / tex_w);
          vec4 p = texelFetch(pos_tex, t, 0);
          VERTEX = vec3(p.x, p.z, -p.y) * render_scale; // sim z-up, Godot y-up (RenderUnits)
          float dist = max(-(MODELVIEW_MATRIX * vec4(VERTEX, 1.0)).z, 1e-6);
          // A point as wide as the particle's kernel core on screen, so the dense bodies read as surfaces.
          POINT_SIZE = clamp(1.8 * p.w * render_scale / dist * px_per_rad, 1.0, 24.0);
          col = texelFetch(col_tex, t, 0).rgb * intensity;
        }
        void fragment() {
          // Round, like points.gdshader: square sprites read as blocks at the kernel-core size.
          vec2 c = POINT_COORD - 0.5;
          if (dot(c, c) > 0.25) discard;
          ALBEDO = col;
        }
        """;

    private void BuildMesh()
    {
        posTexRd = new Texture2Drd { TextureRdRid = posTex };
        colTexRd = new Texture2Drd { TextureRdRid = colTex };
        pointsShader ??= new Shader { Code = PointsShader };
        material = new ShaderMaterial { Shader = pointsShader };
        material.SetShaderParameter("pos_tex", posTexRd);
        material.SetShaderParameter("col_tex", colTexRd);
        material.SetShaderParameter("tex_w", TexW);
        material.SetShaderParameter(PRenderScale, 1e-6f);
        material.SetShaderParameter(PPxPerRad, 1000f);
        material.SetShaderParameter(PIntensity, 1f);
        // The vertex buffer only supplies VERTEX_ID; its contents are never read.
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = new Vector3[Count];
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Points, arrays);
        mesh.SurfaceSetMaterial(0, material);
        meshNode = new MeshInstance3D { Mesh = mesh, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        AddChild(meshNode);
    }

    /// <summary>
    /// Put the cloud at <paramref name="renderPos"/> (the CM's camera-relative, squeezed render position) with the squeeze
    /// factor <paramref name="squeeze"/>, the viewport's pixels per radian and a brightness multiplier.
    /// </summary>
    public void Place(Vector3 renderPos, double squeeze, double pixelsPerRadian, double intensity)
    {
        Position = renderPos;
        if (material == null || meshNode == null) return;
        var scale = (float)(1e-6 * squeeze);
        material.SetShaderParameter(PRenderScale, scale);
        material.SetShaderParameter(PPxPerRad, (float)pixelsPerRadian);
        material.SetShaderParameter(PIntensity, (float)intensity);
        var e = 4 * pc.GBox * scale;
        meshNode.CustomAabb = new Aabb(new Vector3(-e, -e, -e), new Vector3(2 * e, 2 * e, 2 * e));
    }

    // ---------------------------------------------------------------- teardown

    /// <summary>Free this run's RIDs (sets → buffers and textures), detaching the textures first; the kernels are shared.</summary>
    public void Release()
    {
        if (released || rd == null) return;
        released = true;
        ready = false;
        if (posTexRd != null) posTexRd.TextureRdRid = default;
        if (colTexRd != null) colTexRd.TextureRdRid = default;
        var freed = 0;
        foreach (var s in sets) if (rd.UniformSetIsValid(s)) { rd.FreeRid(s); freed++; }
        foreach (var b in buffers) if (b.IsValid) { rd.FreeRid(b); freed++; }
        // Pipelines and shaders are shared across runs (FreeShared).
        GD.Print($"[sph] gpu freed {freed} RIDs (sets {sets.Count}, buffers+textures {buffers.Count})");
        sets.Clear(); buffers.Clear(); shaderOf.Clear();
    }

    public override void _ExitTree()
    {
        // A download still in flight must not write into a state the app has moved on from.
        asyncTarget = null;
        asyncWaiting = 0;
        unpacking = null;
        Release();
    }

    // ---------------------------------------------------------------- parity

    /// <summary>
    /// Debug: run the CPU <see cref="SphSolver"/> (double) and this GPU port (float32) from one state for
    /// <paramref name="steps"/> steps at the same fixed Δt, both with direct gravity, and report the largest relative
    /// differences of ρ and |a| at step 0 and after the last step; then the pyramid tree's error against direct gravity
    /// at step 0. <paramref name="o"/> must be resolved (<see cref="HeroCollision.ResolvedOptions"/>).
    /// </summary>
    public static string Parity(SphState initial, SphOptions o, int steps, double gravityBox)
    {
        var sb = new StringBuilder();
        var so = Copy(o);
        so.Gravity = GravityMode.Direct;
        so.FixedDt = 0;
        var cpu = initial.Clone();
        var solver = new SphSolver(cpu, so);
        solver.Initialize();
        var dt = solver.Dt;
        so.FixedDt = dt;
        var cpu0 = cpu.Clone();
        for (var k = 0; k < steps; k++) solver.Step();

        var gpu = new SphGpu();
        try
        {
            var go = Copy(o);
            go.FixedDt = dt;
            gpu.Init(initial.Clone(), go, gravityBox, tree: false, render: false);
            var g0 = initial.Clone();
            gpu.Download(g0);
            gpu.Step(steps, render: false);
            var gk = initial.Clone();
            gpu.Download(gk);
            sb.Append(CultureInfo.InvariantCulture, $"[sph-parity] n={initial.Count} fixed dt={dt:E4} s steps={steps} gpu_steps={gpu.StepsTaken} gpu_t={gpu.SimTime:E4} cpu_t={solver.Time:E4}\n");
            sb.Append("[sph-parity] step 0: ").Append(Compare(cpu0, g0)).Append('\n');
            sb.Append(CultureInfo.InvariantCulture, $"[sph-parity] step {steps}: ").Append(Compare(cpu, gk)).Append('\n');
            // The reference is direct gravity on the same re-uploaded state, not g0: after the steps the buffers carry
            // state Upload does not reset, so g0 would charge that difference to the tree.
            gpu.Tree = false;
            gpu.WriteParams(go);
            gpu.Upload(initial);
            gpu.InitialForces();
            var gd = initial.Clone();
            gpu.Download(gd);
            gpu.Tree = true;
            gpu.WriteParams(go);
            gpu.Upload(initial);
            gpu.InitialForces();
            var gt = initial.Clone();
            gpu.Download(gt);
            sb.Append("[sph-parity] direct after re-upload vs step 0: ").Append(Compare(g0, gd, accelOnly: true)).Append('\n');
            sb.Append(CultureInfo.InvariantCulture, $"[sph-parity] pyramid tree (theta {go.Theta}) vs direct, step 0: ").Append(Compare(gd, gt, accelOnly: true)).Append('\n');
            // Hydrostatic bodies: pressure nearly cancels gravity, so |a| is a small residual and the line above overstates
            // the tree's error. Hydro is identical in gd and gt, so Δa is the gravity error alone; scale it by gravity.
            sb.Append("[sph-parity] pyramid tree vs direct, gravity-normalised: ").Append(GravityError(cpu0, gd, gt)).Append('\n');
        }
        finally
        {
            gpu.Release();
            gpu.Free();
        }
        return sb.ToString();
    }

    private static SphOptions Copy(SphOptions o) => new()
    {
        Alpha = o.Alpha, Beta = o.Beta, Balsara = o.Balsara, Courant = o.Courant, AccelerationFactor = o.AccelerationFactor,
        GravityFactor = o.GravityFactor, Eta = o.Eta, PressureFloor = o.PressureFloor, Gravity = o.Gravity, G = o.G,
        Softening = o.Softening, Theta = o.Theta, Quadrupole = o.Quadrupole, CellSize = o.CellSize, HMinFraction = o.HMinFraction,
        DtMax = o.DtMax, DtGrowth = o.DtGrowth, FixedDt = o.FixedDt,
    };

    /// <summary>|a_tree − a_direct| per particle over the rms of the CPU's direct gravity <paramref name="g"/> (Gx, Gy, Gz).</summary>
    private static string GravityError(SphState g, SphState direct, SphState tree)
    {
        var n = g.Count;
        double g2 = 0;
        for (var i = 0; i < n; i++) g2 += g.Gx[i] * g.Gx[i] + g.Gy[i] * g.Gy[i] + g.Gz[i] * g.Gz[i];
        var rms = Math.Sqrt(g2 / Math.Max(n, 1));
        var err = new double[n];
        for (var i = 0; i < n; i++)
        {
            double dx = tree.Ax[i] - direct.Ax[i], dy = tree.Ay[i] - direct.Ay[i], dz = tree.Az[i] - direct.Az[i];
            var gi = Math.Sqrt(g.Gx[i] * g.Gx[i] + g.Gy[i] * g.Gy[i] + g.Gz[i] * g.Gz[i]);
            err[i] = Math.Sqrt(dx * dx + dy * dy + dz * dz) / Math.Max(gi, 1e-300);
        }
        var maxOverRms = 0.0;
        for (var i = 0; i < n; i++) maxOverRms = Math.Max(maxOverRms, err[i] * Math.Sqrt(g.Gx[i] * g.Gx[i] + g.Gy[i] * g.Gy[i] + g.Gz[i] * g.Gz[i]) / Math.Max(rms, 1e-300));
        Array.Sort(err);
        return string.Create(CultureInfo.InvariantCulture,
            $"|Δa|/|g| p50 {err[n / 2]:E3}, p99 {err[Math.Min(n - 1, (int)(0.99 * n))]:E3}, max {err[^1]:E3}; max |Δa|/rms|g| {maxOverRms:E3}");
    }

    private static string Compare(SphState a, SphState b, bool accelOnly = false)
    {
        double rhoMax = 0, accMax = 0, accRmsNorm = 0, a2 = 0;
        var n = a.Count;
        var rel = new double[n];
        for (var i = 0; i < n; i++) a2 += a.Ax[i] * a.Ax[i] + a.Ay[i] * a.Ay[i] + a.Az[i] * a.Az[i];
        var rms = Math.Sqrt(a2 / Math.Max(n, 1));
        var worst = -1;
        for (var i = 0; i < n; i++)
        {
            var dr = Math.Abs(a.Rho[i] - b.Rho[i]) / a.Rho[i];
            if (dr > rhoMax) rhoMax = dr;
            double dx = a.Ax[i] - b.Ax[i], dy = a.Ay[i] - b.Ay[i], dz = a.Az[i] - b.Az[i];
            var da = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            var am = Math.Sqrt(a.Ax[i] * a.Ax[i] + a.Ay[i] * a.Ay[i] + a.Az[i] * a.Az[i]);
            rel[i] = am > 0 ? da / am : 0;
            if (rel[i] > accMax) { accMax = rel[i]; worst = i; }
            accRmsNorm = Math.Max(accRmsNorm, da / Math.Max(rms, 1e-300));
        }
        Array.Sort(rel);
        var p99 = rel[Math.Min(n - 1, (int)(0.99 * n))];
        var head = accelOnly ? "" : string.Create(CultureInfo.InvariantCulture, $"rho max rel {rhoMax:E3}; ");
        return head + string.Create(CultureInfo.InvariantCulture,
            $"|a| max rel {accMax:E3} (particle {worst}), p99 rel {p99:E3}, max |Δa|/rms|a| {accRmsNorm:E3}");
    }
}
