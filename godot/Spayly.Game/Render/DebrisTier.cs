using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Godot;
using Spayly.Sim;
using Spayly.Sim.Debris;

namespace Spayly.Game.Render;

/// <summary>Why a particle left the GPU tier.</summary>
public enum DebrisEventKind { Absorbed = 0, Dust = 1 }

/// <summary>One particle leaving T2, read back from the GPU; the app turns it into a ledger hand-off.</summary>
public readonly struct DebrisEvent
{
    public required int Slot { get; init; }
    public required DebrisEventKind Kind { get; init; }
    /// <summary>The body it hit (absorbed) or the one whose pull dominated where it went to dust.</summary>
    public required Body Body { get; init; }
    /// <summary>kg, from the CPU mirror (the ledger's truth, not the float on the GPU).</summary>
    public required double Mass { get; init; }
    /// <summary>Velocity relative to <see cref="Body"/> when it happened, km/s.</summary>
    public required Vec3 RelVel { get; init; }
    /// <summary>Position relative to <see cref="Body"/>'s centre, km.</summary>
    public required Vec3 RelPos { get; init; }
    /// <summary>Particle age, s.</summary>
    public required double Age { get; init; }
}

public delegate void DebrisEventHandler(in DebrisEvent e);

/// <summary>std430/std140 vec4: 16 bytes.</summary>
[StructLayout(LayoutKind.Explicit, Size = 16)]
public struct DebrisFloat4
{
    [FieldOffset(0)] public float X;
    [FieldOffset(4)] public float Y;
    [FieldOffset(8)] public float Z;
    [FieldOffset(12)] public float W;

    public DebrisFloat4(Vec3 v, double w)
    {
        X = (float)v.X;
        Y = (float)v.Y;
        Z = (float)v.Z;
        W = (float)w;
    }
}

[InlineArray(DebrisTier.MaxBodies)]
public struct DebrisBodyArray { DebrisFloat4 _e; }

/// <summary>
/// The Bodies UBO (std140): o2, o3 (origin acceleration ö(s) = o2 + s·o3, km/s²), then per body the cubic Hermite
/// coefficients of its position relative to the moving origin over the frame, b(s) = c0 + s c1 + s² c2 + s³ c3 (km, s the
/// frame fraction); c0.w = GM (km³/s²), c1.w = contact radius (km: a black hole's 2 r_s capture radius, else its radius),
/// c2.w = the Paczyński–Wiita r_s (km, 0 for anything but a black hole).
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = Bytes)]
public struct DebrisBodiesUbo
{
    public const int Bytes = 32 + 4 * DebrisTier.MaxBodies * 16;
    [FieldOffset(0)] public DebrisFloat4 O2;
    [FieldOffset(16)] public DebrisFloat4 O3;
    [FieldOffset(32)] public DebrisBodyArray C0;
    [FieldOffset(32 + DebrisTier.MaxBodies * 16)] public DebrisBodyArray C1;
    [FieldOffset(32 + 2 * DebrisTier.MaxBodies * 16)] public DebrisBodyArray C2;
    [FieldOffset(32 + 3 * DebrisTier.MaxBodies * 16)] public DebrisBodyArray C3;
}

/// <summary>One staged injection (std430 <c>struct Inj { vec4 p; vec4 v; uvec4 meta; }</c>).</summary>
[StructLayout(LayoutKind.Explicit, Size = 48)]
public struct DebrisInjectRecord
{
    [FieldOffset(0)] public DebrisFloat4 Pos;
    [FieldOffset(16)] public DebrisFloat4 Vel;
    [FieldOffset(32)] public uint Slot;
    [FieldOffset(36)] public float Mass;
    [FieldOffset(40)] public uint Pad0;
    [FieldOffset(44)] public uint Pad1;
}

/// <summary>One removal event as the integrate kernel writes it (std430 <c>struct Event { uvec4 a; vec4 b; vec4 c; }</c>).</summary>
[StructLayout(LayoutKind.Explicit, Size = 48)]
public struct DebrisEventRecord
{
    [FieldOffset(0)] public uint Index;
    /// <summary>Body slot in the low byte, kind above it.</summary>
    [FieldOffset(4)] public uint BodyKind;
    [FieldOffset(8)] public uint Tag;
    [FieldOffset(12)] public float S;
    [FieldOffset(16)] public float Wx;
    [FieldOffset(20)] public float Wy;
    [FieldOffset(24)] public float Wz;
    [FieldOffset(28)] public float Age;
    [FieldOffset(32)] public float Xx;
    [FieldOffset(36)] public float Xy;
    [FieldOffset(40)] public float Xz;
    [FieldOffset(44)] public float Pad;
}

/// <summary>
/// T2 of the debris hierarchy (docs/research/physics-and-visuals.md §2.4) on the main RenderingDevice, built on spike M1a:
/// up to <see cref="Capacity"/> test particles as SoA std430 buffers (pos vec4: xyz km, w radius km; vel vec4: xyz km/s,
/// w age s; mass float), integrated by kick-drift-kick leapfrog under the heaviest ≤ 64 bodies, re-chosen every frame.
/// <b>Lighter bodies exert no gravity on the particles and cannot absorb them.</b> Particles feel the bodies and nothing
/// else: no self-gravity, and no reaction on the bodies (test-particle approximation).
///
/// Frame: positions and velocities are float32 relative to a moving origin, the camera's focus body. Within a frame the
/// origin and every body follow cubic Hermite curves through their start and end states (double on the CPU, only the
/// relative coefficients narrowed), and the kernel adds the origin's exact −ö, so the co-moving frame needs no per-frame
/// re-basing at all; a focus change re-bases in a compute pass, never on the CPU.
///
/// A particle whose drift segment passes inside a body is absorbed; one older than its lifetime or beyond the bounding
/// radius goes to dust. Either way the kernel appends (slot, body, relative velocity, relative position) to a per-frame
/// event region and marks the slot dead; the CPU reads the region back with two <c>BufferGetDataAsync</c> calls (the
/// 16-byte count, then exactly the events) and hands each to <see cref="OnEvent"/>. A slot is reused only after its event
/// was handled, so the CPU mass mirror is always exactly what T2 holds.
///
/// Drawn as spike route 1: the kernels write xyz + packed speed/age into an RGBA32F texture, a <c>Texture2Drd</c> wraps it,
/// and a point mesh fetches its texel by VERTEX_ID. The per-frame path allocates nothing managed except the byte arrays
/// the async readbacks hand over (16 B a frame, plus the events when there are any).
/// </summary>
public sealed partial class DebrisTier : Node3D
{
    public const int DefaultCapacity = 1_000_000;
    public const int MaxBodies = 64;
    /// <summary>Event regions in flight; one per dispatched frame, freed when its events are handled (~6 frames).</summary>
    public const int Regions = 16;
    public const int EventsPerRegion = 8192;
    /// <summary>Most injections uploaded in one frame.</summary>
    public const int StagingCapacity = 65536;
    const int TexW = 1024;
    const int Group = 256;
    const int TimingRing = 8192;

    /// <summary>Most substeps one frame may take; above it the step grows instead (accuracy falls at warp ≳ 1e5).</summary>
    public int MaxSubsteps = 64;
    /// <summary>Longest substep, s: ~0.02 of the dynamical time at a rocky body's surface.</summary>
    public double MaxStep = 20;
    /// <summary>Particle lifetime, s, jittered ±20 % per slot; then it goes to dust.</summary>
    public double Lifetime = 30 * Constants.Day;
    /// <summary>Distance from the origin body past which a particle goes to dust, km.</summary>
    public double BoundRadius = 2e6;
    /// <summary>Speed (km/s) and age (s) that saturate the hot → ash colour ramp.</summary>
    public double SpeedRef = 3, AgeRef = 3600;
    /// <summary>Normal restitution of particle contacts.</summary>
    public double Restitution = 0.5;
    public float PointSize = 1.5f;
    /// <summary>[ART] Smallest on-screen size of a tidal-stream particle, px: a star's stream spans 10⁸ km and must read at that framing.</summary>
    public float StreamPointSize = 3f;
    /// <summary>Skip drawing (capture comparisons); the particles still integrate.</summary>
    public bool Hidden;
    /// <summary>The age a stream particle starts at: negative, so the sign of vel.w flags it; a float32 zero has no sign to keep.</summary>
    const float StreamAgeSeed = -1e-20f;
    public float Brightness = 1;

    public int Capacity { get; }
    public bool Contacts { get; }
    /// <summary>False when there is no RenderingDevice (dummy renderer) or a kernel failed to compile.</summary>
    public bool IsReady { get; private set; }
    /// <summary>Slots [0, HighWater) are dispatched.</summary>
    public int HighWater { get; private set; }
    /// <summary>Slots that hold mass: alive on the GPU, queued, or dead there with the event still in flight.</summary>
    public int Occupied { get; private set; }
    public long Queued { get; private set; }
    public long AbsorbedCount { get; private set; }
    public long DustCount { get; private set; }
    /// <summary>Events recorded but dropped because their region's tag no longer matched (should stay 0).</summary>
    public long StaleEvents { get; private set; }
    /// <summary>Frames that ran without an event region (all busy): removals waited a frame.</summary>
    public long BusyFrames { get; private set; }
    public Body? Origin => origin;
    /// <summary>Largest particle radius queued, km: the contact grid's cell is twice it.</summary>
    public double MaxRadius { get; private set; }
    public DebrisEventHandler? OnEvent;

    readonly double[] mass;
    readonly int[] freeStack;
    int freeCount;
    DD massTotal;

    RenderingDevice rd = null!;
    readonly List<Rid> buffers = [], shaders = [], pipelines = [], sets = [];
    readonly Dictionary<Rid, Rid> shaderOf = [];
    Rid pipeIntegrate, pipeInject, pipeRebase;
    Rid posBuf, velBuf, massBuf, uboBuf, headBuf, eventBuf, stageBuf, posTex;
    Rid setIntegrate, setInject, setRebase;
    int texH;

    // contacts (optional)
    Rid pipeClear, pipeCount, pipeScan, pipeAdd, pipeScatter, pipeContacts;
    Rid setClear, setCount, setScatter, setContacts;
    readonly List<(Rid Set, uint Count)> scanLevels = [], addLevels = [];
    uint tableSize, tableMask;

    // bodies and frame
    readonly Body?[] chosen = new Body?[MaxBodies];
    int nChosen;
    readonly Body?[] regionBodies = new Body?[Regions * MaxBodies];
    readonly uint[] regionTag = new uint[Regions];
    readonly int[] regionCount = new int[Regions];
    readonly RegionState[] regionState = new RegionState[Regions];
    readonly long[] regionSince = new long[Regions];
    bool stuckWarned;
    uint nextTag = 1;
    DebrisBodiesUbo ubo;
    Body? origin;
    long frame;

    struct EndState
    {
        public Vec3 Pos;
        public Vec3 Vel;
        public long Seen;
    }

    readonly Dictionary<Body, EndState> endState = [];
    readonly List<Body> stale = [];

    internal enum RegionState : byte { Free, AwaitHead, NeedData, AwaitData }

    internal struct Pending
    {
        public int Slot;
        public Vec3 Pos;
        public Vec3 Vel;
        public float Mass;
        public float Radius;
        public bool Stream;
    }

    readonly Pending[] pending = new Pending[2 * StagingCapacity];
    int pendingCount;
    readonly DebrisInjectRecord[] stage = new DebrisInjectRecord[StagingCapacity];

    Callable headCallback, dataCallback;

    // render
    MeshInstance3D? node;
    ArrayMesh? mesh;
    ShaderMaterial? material;
    Texture2Drd? texture;
    int meshVerts;
    static readonly StringName PTex = "pos_tex", PTexW = "tex_w", POrigin = "origin_render", PScale = "offset_scale", PSize = "point_size", PIntensity = "intensity",
        PStreamSize = "stream_point_size";

    // timing (CaptureTimestamp), ms
    static readonly string[] TsNames = ["debris0", "debris1", "debris2", "debris3"];
    readonly int[] tsIdx = [-1, -1, -1, -1];
    uint tsCount;
    bool tsResolved;
    int tsMisses;
    readonly double[] tIntegrate = new double[TimingRing], tTotal = new double[TimingRing];
    long tSamples;
    long lastTimedFrame = long.MinValue / 2;

    public DebrisTier() : this(DefaultCapacity, false) { }

    public DebrisTier(int capacity, bool contacts)
    {
        Name = "DebrisTier";
        Capacity = Math.Max(1024, capacity);
        Contacts = contacts;
        mass = new double[Capacity];
        freeStack = new int[Capacity];
    }

    public override void _Ready()
    {
        rd = RenderingServer.GetRenderingDevice();
        if (rd == null)
        {
            GD.Print("[debris] no RenderingDevice (dummy renderer): the GPU tier is off");
            return;
        }
        try
        {
            Build();
            IsReady = true;
            GD.Print(string.Create(CultureInfo.InvariantCulture,
                $"[debris] tier ready: capacity={Capacity} contacts={Contacts} tex={TexW}x{texH} table={tableSize} rd {MemMb()}"));
        }
        catch (Exception e)
        {
            GD.PushError($"[debris] GPU tier disabled: {e.Message}");
            FreeAll();
        }
    }

    // ------------------------------------------------------------------ slots

    /// <summary>Slots a caller may still queue this frame (free capacity and staging room).</summary>
    public int CanQueue => IsReady ? Math.Min(Capacity - HighWater + freeCount, pending.Length - pendingCount) : 0;

    /// <summary>Exact Σ of the CPU mass mirror, kg (kept incrementally; <see cref="RecountMass"/> checks it).</summary>
    public DD MassTotal => massTotal;

    /// <summary>Σ of the mirror recomputed from scratch, kg; O(HighWater).</summary>
    public DD RecountMass()
    {
        var s = DD.Zero;
        for (var i = 0; i < HighWater; i++)
            if (mass[i] != 0) s += mass[i];
        return s;
    }

    /// <summary>
    /// Queue one particle: absolute position (km) and velocity (km/s), mass (kg) into the mirror now, uploaded at the end of
    /// this frame's step relative to the origin. False when full (the caller must not book it).
    /// </summary>
    /// <summary>
    /// Stage a particle. <paramref name="stream"/> marks tidal-stream material, which is drawn larger and glowing (the sign of
    /// its age slot carries the flag: vel.w counts age downwards from −0 for a stream particle, upwards for everything else).
    /// </summary>
    public bool Queue(Vec3 pos, Vec3 vel, double massKg, double radiusKm, bool stream = false)
    {
        if (!IsReady || pendingCount >= pending.Length || !(massKg > 0)) return false;
        int slot;
        if (freeCount > 0) slot = freeStack[--freeCount];
        else if (HighWater < Capacity) slot = HighWater++;
        else return false;
        mass[slot] = massKg;
        massTotal += massKg;
        Occupied++;
        Queued++;
        if (radiusKm > MaxRadius) MaxRadius = radiusKm;
        pending[pendingCount++] = new Pending { Slot = slot, Pos = pos, Vel = vel, Mass = (float)massKg, Radius = (float)Math.Max(radiusKm, 1e-6), Stream = stream };
        return true;
    }

    void FreeSlot(int slot)
    {
        massTotal -= mass[slot];
        mass[slot] = 0;
        freeStack[freeCount++] = slot;
        if (--Occupied == 0 && pendingCount == 0)
        {
            // Everything is gone: shrink the dispatch back to nothing.
            HighWater = 0;
            freeCount = 0;
            massTotal = DD.Zero;
        }
    }

    /// <summary>Drop every particle and every in-flight event (undo, fault rollback). The caller rebaselines the ledger.</summary>
    public void Reset()
    {
        Array.Clear(mass);
        Array.Clear(regionBodies);
        for (var r = 0; r < Regions; r++)
        {
            regionTag[r] = 0;
            regionState[r] = RegionState.Free;
        }
        HighWater = Occupied = freeCount = pendingCount = 0;
        massTotal = DD.Zero;
        MaxRadius = 0;
        if (!IsReady) return;
        rd.BufferClear(posBuf, 0, (uint)(Capacity * 16));
        rd.BufferClear(velBuf, 0, (uint)(Capacity * 16));
        rd.TextureClear(posTex, new Color(0, 0, 0, 0), 0, 1, 0, 1);
    }

    // ------------------------------------------------------------------ frame

    /// <summary>
    /// One frame: re-base if the focus changed, integrate [t − dt, t] under the bodies, upload this frame's injections, run
    /// contacts, place the cloud for the camera. <paramref name="dt"/> is the sim time the bodies just advanced (signed).
    /// </summary>
    public void Step(IReadOnlyList<Body> bodies, Body focus, double dt, ViewContext ctx)
    {
        if (!IsReady) return;
        frame++;
        ReadTimestamps();
        RequestEventData();
        var previous = origin;
        origin = focus;
        var active = HighWater > 0;
        if (active)
        {
            lastTimedFrame = frame;
            rd.CaptureTimestamp(TsNames[0]);
            if (previous != null && !ReferenceEquals(previous, focus)) Rebase(previous, focus, dt);
            if (dt != 0) Integrate(bodies, dt);
            rd.CaptureTimestamp(TsNames[1]);
        }
        FlushInjections();
        if (active) rd.CaptureTimestamp(TsNames[2]);
        if (Contacts && HighWater > 0) DispatchContacts();
        if (active) rd.CaptureTimestamp(TsNames[3]);
        UpdateRender(ctx);
        SaveEndStates(bodies);
    }

    (Vec3 Pos, Vec3 Vel) StartState(Body b, double dt) =>
        endState.TryGetValue(b, out var s) ? (s.Pos, s.Vel) : (b.Pos - b.Vel * dt, b.Vel);

    void SaveEndStates(IReadOnlyList<Body> bodies)
    {
        for (var i = 0; i < bodies.Count; i++)
        {
            var b = bodies[i];
            endState[b] = new EndState { Pos = b.Pos, Vel = b.Vel, Seen = frame };
        }
        if (frame % 120 != 0) return;
        stale.Clear();
        foreach (var (b, s) in endState)
            if (s.Seen < frame && !ReferenceEquals(b, origin)) stale.Add(b);
        foreach (var b in stale) endState.Remove(b);
    }

    /// <summary>The heaviest ≤ 64 bodies, heaviest first, with the origin forced in.</summary>
    void ChooseBodies(IReadOnlyList<Body> bodies)
    {
        nChosen = 0;
        var hasOrigin = false;
        for (var i = 0; i < bodies.Count; i++)
        {
            var b = bodies[i];
            if (nChosen == MaxBodies && b.Mass <= chosen[MaxBodies - 1]!.Mass) continue;
            var at = nChosen < MaxBodies ? nChosen++ : MaxBodies - 1;
            while (at > 0 && chosen[at - 1]!.Mass < b.Mass)
            {
                chosen[at] = chosen[at - 1];
                at--;
            }
            chosen[at] = b;
        }
        for (var k = 0; k < nChosen; k++) hasOrigin |= ReferenceEquals(chosen[k], origin);
        if (!hasOrigin && origin != null)
        {
            if (nChosen < MaxBodies) chosen[nChosen++] = origin;
            else chosen[MaxBodies - 1] = origin;
        }
    }

    /// <summary>
    /// Hermite coefficients over the frame, relative to the origin's own Hermite curve so the origin sits at 0 exactly and
    /// nothing large is ever narrowed to float except a far body's position (whose force needs only 1e-7 relative).
    /// </summary>
    void FillUbo(double dt)
    {
        var o = origin!;
        var (f0, fv0) = StartState(o, dt);
        var f1 = o.Pos;
        var fv1 = o.Vel;
        // ö(s) = (2 F2 + 6 F3 s)/T², F2 = 3ΔF − 2 T v0 − T v1, F3 = −2ΔF + T v0 + T v1; formed in double (they cancel).
        var dF = f1 - f0;
        var a0 = fv0 * dt;
        var a1 = fv1 * dt;
        var t2 = 1 / (dt * dt);
        ubo.O2 = new DebrisFloat4((dF * 3 - a0 * 2 - a1) * (2 * t2), 0);
        ubo.O3 = new DebrisFloat4((dF * -2 + a0 + a1) * (6 * t2), 0);
        for (var k = 0; k < MaxBodies; k++)
        {
            if (k >= nChosen)
            {
                ubo.C0[k] = ubo.C1[k] = ubo.C2[k] = ubo.C3[k] = default;
                continue;
            }
            var b = chosen[k]!;
            var (p0, v0) = StartState(b, dt);
            var d0 = p0 - f0;
            var d1 = b.Pos - f1;
            var w0 = (v0 - fv0) * dt;
            var w1 = (b.Vel - fv1) * dt;
            var delta = d1 - d0;
            ubo.C0[k] = new DebrisFloat4(d0, Constants.G * b.Mass);
            ubo.C1[k] = new DebrisFloat4(w0, PseudoNewtonian.ContactRadius(b));
            ubo.C2[k] = new DebrisFloat4(delta * 3 - w0 * 2 - w1, PseudoNewtonian.HoleRs(b));
            ubo.C3[k] = new DebrisFloat4(delta * -2 + w0 + w1, 0);
        }
    }

    static void PushU(Span<byte> pc, int slot, uint v) => MemoryMarshal.Cast<byte, uint>(pc)[slot] = v;
    static void PushF(Span<byte> pc, int slot, double v) => MemoryMarshal.Cast<byte, float>(pc)[slot] = (float)v;
    static uint Groups(int count, int size) => (uint)((count + size - 1) / size);

    void Integrate(IReadOnlyList<Body> bodies, double dt)
    {
        ChooseBodies(bodies);
        FillUbo(dt);
        rd.BufferUpdate(uboBuf, 0, DebrisBodiesUbo.Bytes, MemoryMarshal.AsBytes(new ReadOnlySpan<DebrisBodiesUbo>(ref ubo)));

        // An event region per dispatched frame; if the next one still holds unhandled events, record nothing this frame
        // (removals wait: the kernel only kills a particle whose event it could write).
        var region = (int)(nextTag % Regions);
        var usable = regionState[region] == RegionState.Free;
        var headSlot = usable ? region : Regions;
        var tag = usable ? nextTag++ : 0u;
        if (usable)
        {
            regionTag[region] = tag;
            regionState[region] = RegionState.AwaitHead;
            regionSince[region] = frame;
            Array.Copy(chosen, 0, regionBodies, region * MaxBodies, MaxBodies);
            Span<byte> head = stackalloc byte[16];
            head.Clear();
            MemoryMarshal.Cast<byte, uint>(head)[1] = tag;
            rd.BufferUpdate(headBuf, (uint)(region * 16), 16, head);
        }
        else
        {
            BusyFrames++;
            // A readback that never returns would stall every removal; it is an engine fault, so say so once, loudly.
            if (!stuckWarned && frame - regionSince[region] > 300)
            {
                stuckWarned = true;
                GD.PushError($"[debris] event region {region} (tag {regionTag[region]}, {regionState[region]}) has waited {frame - regionSince[region]} frames for its readback; particle removals are stalled");
            }
        }

        var nSub = (int)Math.Clamp(Math.Ceiling(Math.Abs(dt) / MaxStep), 1, MaxSubsteps);
        Span<byte> pc = stackalloc byte[64];
        pc.Clear();
        PushU(pc, 0, (uint)HighWater);
        PushU(pc, 1, (uint)nSub);
        PushU(pc, 2, (uint)nChosen);
        PushF(pc, 3, dt);
        PushF(pc, 4, Lifetime);
        PushF(pc, 5, BoundRadius * BoundRadius);
        PushU(pc, 6, (uint)headSlot);
        PushU(pc, 7, usable ? EventsPerRegion : 0u);
        PushU(pc, 8, tag);
        PushU(pc, 9, (uint)(region * EventsPerRegion));
        PushU(pc, 10, TexW);
        PushF(pc, 11, SpeedRef);
        PushF(pc, 12, AgeRef);
        var cl = rd.ComputeListBegin();
        rd.ComputeListBindComputePipeline(cl, pipeIntegrate);
        rd.ComputeListBindUniformSet(cl, setIntegrate, 0);
        rd.ComputeListSetPushConstant(cl, pc, 64);
        rd.ComputeListDispatch(cl, Groups(HighWater, Group), 1, 1);
        rd.ComputeListEnd();
        if (usable) rd.BufferGetDataAsync(headBuf, headCallback, (uint)(region * 16), 16);
    }

    /// <summary>The focus changed: shift every particle into the new origin's frame (start-of-frame states of both).</summary>
    void Rebase(Body from, Body to, double dt)
    {
        var (p0, v0) = StartState(from, dt);
        var (p1, v1) = StartState(to, dt);
        var dx = p0 - p1;
        var dv = v0 - v1;
        Span<byte> pc = stackalloc byte[48];
        pc.Clear();
        PushU(pc, 0, (uint)HighWater);
        PushU(pc, 1, TexW);
        PushF(pc, 2, SpeedRef);
        PushF(pc, 3, AgeRef);
        PushF(pc, 4, dx.X);
        PushF(pc, 5, dx.Y);
        PushF(pc, 6, dx.Z);
        PushF(pc, 8, dv.X);
        PushF(pc, 9, dv.Y);
        PushF(pc, 10, dv.Z);
        var cl = rd.ComputeListBegin();
        rd.ComputeListBindComputePipeline(cl, pipeRebase);
        rd.ComputeListBindUniformSet(cl, setRebase, 0);
        rd.ComputeListSetPushConstant(cl, pc, 48);
        rd.ComputeListDispatch(cl, Groups(HighWater, Group), 1, 1);
        rd.ComputeListEnd();
        GD.Print(string.Create(CultureInfo.InvariantCulture, $"[debris] rebase {from.Id} -> {to.Id}: dx={Vec3.Length(dx):G6} km dv={Vec3.Length(dv):G6} km/s particles={Occupied}"));
    }

    void FlushInjections()
    {
        if (pendingCount == 0 || origin == null) return;
        var k = Math.Min(pendingCount, StagingCapacity);
        var op = origin.Pos;
        var ov = origin.Vel;
        for (var j = 0; j < k; j++)
        {
            ref readonly var p = ref pending[j];
            stage[j] = new DebrisInjectRecord
            {
                Pos = new DebrisFloat4(p.Pos - op, p.Radius),
                Vel = new DebrisFloat4(p.Vel - ov, p.Stream ? StreamAgeSeed : 0),
                Slot = (uint)p.Slot,
                Mass = p.Mass,
            };
        }
        rd.BufferUpdate(stageBuf, 0, (uint)(k * 48), MemoryMarshal.AsBytes(stage.AsSpan(0, k)));
        Span<byte> pc = stackalloc byte[16];
        PushU(pc, 0, (uint)k);
        PushU(pc, 1, TexW);
        PushF(pc, 2, SpeedRef);
        PushF(pc, 3, AgeRef);
        var cl = rd.ComputeListBegin();
        rd.ComputeListBindComputePipeline(cl, pipeInject);
        rd.ComputeListBindUniformSet(cl, setInject, 0);
        rd.ComputeListSetPushConstant(cl, pc, 16);
        rd.ComputeListDispatch(cl, Groups(k, Group), 1, 1);
        rd.ComputeListEnd();
        pendingCount -= k;
        if (pendingCount > 0) Array.Copy(pending, k, pending, 0, pendingCount);
    }

    // ------------------------------------------------------------------ readback

    void OnHead(byte[] data)
    {
        if (data.Length < 8) return;
        var u = MemoryMarshal.Cast<byte, uint>(data.AsSpan());
        var count = u[0];
        var tag = u[1];
        var r = (int)(tag % Regions);
        if (tag == 0 || regionTag[r] != tag || regionState[r] != RegionState.AwaitHead) return;
        var n = (int)Math.Min(count, (uint)EventsPerRegion);
        if (n == 0)
        {
            regionState[r] = RegionState.Free;
            return;
        }
        regionCount[r] = n;
        // Not requested from inside the callback: the RD is mid-frame here. Step issues it next frame.
        regionState[r] = RegionState.NeedData;
    }

    void RequestEventData()
    {
        for (var r = 0; r < Regions; r++)
        {
            if (regionState[r] != RegionState.NeedData) continue;
            regionState[r] = RegionState.AwaitData;
            rd.BufferGetDataAsync(eventBuf, dataCallback, (uint)(r * EventsPerRegion * 48), (uint)(regionCount[r] * 48));
        }
    }

    void OnData(byte[] data)
    {
        var ev = MemoryMarshal.Cast<byte, DebrisEventRecord>(data.AsSpan());
        if (ev.Length == 0) return;
        var tag = ev[0].Tag;
        var r = (int)(tag % Regions);
        if (tag == 0 || regionTag[r] != tag || regionState[r] != RegionState.AwaitData)
        {
            StaleEvents += ev.Length;
            return;
        }
        for (var i = 0; i < ev.Length; i++)
        {
            ref readonly var e = ref ev[i];
            var slot = (int)e.Index;
            if (e.Tag != tag || slot >= Capacity || mass[slot] == 0)
            {
                StaleEvents++;
                continue;
            }
            var k = (int)(e.BodyKind & 0xFF);
            var body = k < MaxBodies ? regionBodies[r * MaxBodies + k] : null;
            var kind = (DebrisEventKind)(e.BodyKind >> 8);
            if (body != null)
            {
                OnEvent?.Invoke(new DebrisEvent
                {
                    Slot = slot, Kind = kind, Body = body, Mass = mass[slot],
                    RelVel = new Vec3(e.Wx, e.Wy, e.Wz), RelPos = new Vec3(e.Xx, e.Xy, e.Xz), Age = e.Age,
                });
            }
            if (kind == DebrisEventKind.Absorbed) AbsorbedCount++;
            else DustCount++;
            FreeSlot(slot);
        }
        regionState[r] = RegionState.Free;
    }

    // ------------------------------------------------------------------ contacts (optional, spike M1a grid)

    void DispatchContacts()
    {
        var n = (uint)HighWater;
        var invCell = (float)(1 / (2 * Math.Max(MaxRadius, 1e-6)));
        Span<byte> pc = stackalloc byte[16];
        var cl = rd.ComputeListBegin();
        pc.Clear();
        PushU(pc, 0, tableSize);
        rd.ComputeListBindComputePipeline(cl, pipeClear);
        rd.ComputeListBindUniformSet(cl, setClear, 0);
        rd.ComputeListSetPushConstant(cl, pc, 16);
        rd.ComputeListDispatch(cl, Groups((int)tableSize, Group), 1, 1);
        rd.ComputeListAddBarrier(cl);

        pc.Clear();
        PushU(pc, 0, n);
        PushU(pc, 1, tableMask);
        MemoryMarshal.Cast<byte, float>(pc)[2] = invCell;
        rd.ComputeListBindComputePipeline(cl, pipeCount);
        rd.ComputeListBindUniformSet(cl, setCount, 0);
        rd.ComputeListSetPushConstant(cl, pc, 16);
        rd.ComputeListDispatch(cl, Groups((int)n, Group), 1, 1);
        rd.ComputeListAddBarrier(cl);

        foreach (var (set, count) in scanLevels)
        {
            pc.Clear();
            PushU(pc, 0, count);
            rd.ComputeListBindComputePipeline(cl, pipeScan);
            rd.ComputeListBindUniformSet(cl, set, 0);
            rd.ComputeListSetPushConstant(cl, pc, 16);
            rd.ComputeListDispatch(cl, Groups((int)count, 1024), 1, 1);
            rd.ComputeListAddBarrier(cl);
        }
        foreach (var (set, count) in addLevels)
        {
            pc.Clear();
            PushU(pc, 0, count);
            rd.ComputeListBindComputePipeline(cl, pipeAdd);
            rd.ComputeListBindUniformSet(cl, set, 0);
            rd.ComputeListSetPushConstant(cl, pc, 16);
            rd.ComputeListDispatch(cl, Groups((int)count, Group), 1, 1);
            rd.ComputeListAddBarrier(cl);
        }

        pc.Clear();
        PushU(pc, 0, n);
        rd.ComputeListBindComputePipeline(cl, pipeScatter);
        rd.ComputeListBindUniformSet(cl, setScatter, 0);
        rd.ComputeListSetPushConstant(cl, pc, 16);
        rd.ComputeListDispatch(cl, Groups((int)n, Group), 1, 1);
        rd.ComputeListAddBarrier(cl);

        pc.Clear();
        PushU(pc, 0, n);
        PushU(pc, 1, tableMask);
        MemoryMarshal.Cast<byte, float>(pc)[2] = invCell;
        MemoryMarshal.Cast<byte, float>(pc)[3] = (float)(1 + Restitution);
        rd.ComputeListBindComputePipeline(cl, pipeContacts);
        rd.ComputeListBindUniformSet(cl, setContacts, 0);
        rd.ComputeListSetPushConstant(cl, pc, 16);
        rd.ComputeListDispatch(cl, Groups((int)n, Group), 1, 1);
        rd.ComputeListEnd();
    }

    // ------------------------------------------------------------------ render

    void UpdateRender(ViewContext ctx)
    {
        if (node == null || material == null || origin == null) return;
        var show = HighWater > 0 && !Hidden;
        if (node.Visible != show) node.Visible = show;
        if (!show) return;
        EnsureMesh(HighWater);
        var at = ctx.Origin.Place(origin.Pos, out var k, out _);
        // The diorama inflates the origin body on screen; the cloud around it inflates with it (render-only).
        var inflation = ctx.SolidRadius(origin) / Math.Max(origin.Radius * RenderUnits.Scale, 1e-300);
        material.SetShaderParameter(POrigin, at);
        material.SetShaderParameter(PScale, (float)(RenderUnits.Scale * k * inflation));
        material.SetShaderParameter(PIntensity, (float)(Brightness * ctx.ExposureComp));
        material.SetShaderParameter(PSize, HighWater > 500_000 ? Math.Min(PointSize, 1f) : PointSize);
        material.SetShaderParameter(PStreamSize, StreamPointSize);
    }

    /// <summary>A point mesh of at least <paramref name="needed"/> vertices; only its VERTEX_ID matters, so it is rebuilt only when it grows.</summary>
    void EnsureMesh(int needed)
    {
        if (needed <= meshVerts || material == null || node == null) return;
        var verts = 65536;
        while (verts < needed) verts <<= 1;
        verts = Math.Min(verts, TexW * texH);
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = new Vector3[verts];
        var m = new ArrayMesh();
        m.AddSurfaceFromArrays(Mesh.PrimitiveType.Points, arrays);
        m.CustomAabb = new Aabb(new Vector3(-1e12f, -1e12f, -1e12f), new Vector3(2e12f, 2e12f, 2e12f));
        m.SurfaceSetMaterial(0, material);
        node.Mesh = m;
        mesh?.Dispose();
        mesh = m;
        meshVerts = verts;
    }

    // ------------------------------------------------------------------ timing

    void ReadTimestamps()
    {
        // Results arrive a few frames late; outside that window the set holds only the engine's own names, and trying to
        // resolve ours there would allocate strings every 30 frames while nothing is being timed.
        if (frame - lastTimedFrame > 6) return;
        var count = rd.GetCapturedTimestampsCount();
        if (count == 0) return;
        if (!tsResolved || count != tsCount)
        {
            // Stalled frames carry a partial set; re-resolving allocates strings, so only when the set looks stable.
            if (tsResolved && ++tsMisses < 30) return;
            tsMisses = 0;
            tsResolved = false;
            var found = 0;
            for (var k = 0; k < tsIdx.Length; k++) tsIdx[k] = -1;
            for (uint i = 0; i < count; i++)
            {
                var name = rd.GetCapturedTimestampName(i);
                for (var k = 0; k < TsNames.Length; k++)
                {
                    if (name != TsNames[k]) continue;
                    tsIdx[k] = (int)i;
                    found++;
                }
            }
            tsCount = count;
            tsResolved = found == TsNames.Length;
            if (!tsResolved) return;
        }
        tsMisses = 0;
        double Ms(int a, int b) => (rd.GetCapturedTimestampGpuTime((uint)tsIdx[b]) - (double)rd.GetCapturedTimestampGpuTime((uint)tsIdx[a])) / 1e6;
        var slot = (int)(tSamples % TimingRing);
        tIntegrate[slot] = Ms(0, 1);
        tTotal[slot] = Ms(0, 3);
        tSamples++;
    }

    /// <summary>Mean and max GPU compute time (ms) over the last <paramref name="n"/> timed frames, and how many there were.</summary>
    public (double Mean, double Max, double IntegrateMean, int Count) RecentCompute(int n)
    {
        var c = (int)Math.Min(n, Math.Min(tSamples, TimingRing));
        double sum = 0, max = 0, integ = 0;
        for (var i = 0; i < c; i++)
        {
            var slot = (int)((tSamples - 1 - i) % TimingRing);
            sum += tTotal[slot];
            integ += tIntegrate[slot];
            max = Math.Max(max, tTotal[slot]);
        }
        return c > 0 ? (sum / c, max, integ / c, c) : (0, 0, 0, 0);
    }

    /// <summary>min / p5 / p50 / p95 of the whole-pass compute time (ms) over every timed frame kept, skipping the first 10. Allocates.</summary>
    public string ComputePercentiles()
    {
        var c = (int)Math.Min(tSamples, TimingRing);
        var skip = tSamples <= TimingRing ? Math.Min(10, c) : 0;
        var total = new double[c - skip];
        var integ = new double[c - skip];
        for (var i = 0; i < total.Length; i++)
        {
            total[i] = tTotal[(i + skip) % TimingRing];
            integ[i] = tIntegrate[(i + skip) % TimingRing];
        }
        if (total.Length == 0) return "compute: no timed frames";
        Array.Sort(total);
        Array.Sort(integ);
        double P(double[] a, double q) => a[Math.Min(a.Length - 1, (int)Math.Floor(q * (a.Length - 1) + 0.5))];
        return string.Create(CultureInfo.InvariantCulture,
            $"compute_ms min/p5/p50/p95 {P(total, 0):F3}/{P(total, 0.05):F3}/{P(total, 0.5):F3}/{P(total, 0.95):F3} integrate {P(integ, 0):F3}/{P(integ, 0.05):F3}/{P(integ, 0.5):F3}/{P(integ, 0.95):F3} n={total.Length}");
    }

    public string MemMb() => string.Create(CultureInfo.InvariantCulture,
        $"buffers={rd.GetMemoryUsage(RenderingDevice.MemoryType.Buffers) / 1048576.0:F1}MB textures={rd.GetMemoryUsage(RenderingDevice.MemoryType.Textures) / 1048576.0:F1}MB");

    // ------------------------------------------------------------------ setup / teardown

    Rid Compile(string name, string src)
    {
        var source = new RDShaderSource { Language = RenderingDevice.ShaderLanguage.Glsl, SourceCompute = src };
        var spirv = rd.ShaderCompileSpirVFromSource(source);
        var err = spirv.GetStageCompileError(RenderingDevice.ShaderStage.Compute);
        if (!string.IsNullOrEmpty(err))
        {
            GD.PrintErr($"[debris] GLSL compile error in {name}:\n{err}");
            throw new InvalidOperationException($"shader {name} failed to compile");
        }
        var shader = rd.ShaderCreateFromSpirV(spirv, name);
        if (!shader.IsValid) throw new InvalidOperationException($"ShaderCreateFromSpirV({name}) returned an invalid RID");
        shaders.Add(shader);
        var pipe = rd.ComputePipelineCreate(shader);
        if (!pipe.IsValid) throw new InvalidOperationException($"ComputePipelineCreate({name}) invalid");
        pipelines.Add(pipe);
        shaderOf[pipe] = shader;
        return pipe;
    }

    Rid Buffer(int bytes)
    {
        var size = (uint)Math.Max(16, bytes);
        var b = rd.StorageBufferCreate(size, ReadOnlySpan<byte>.Empty, (RenderingDevice.StorageBufferUsage)0);
        if (!b.IsValid) throw new InvalidOperationException($"StorageBufferCreate({size}) failed");
        buffers.Add(b);
        rd.BufferClear(b, 0, size);
        return b;
    }

    const RenderingDevice.UniformType Ssbo = RenderingDevice.UniformType.StorageBuffer;

    Rid Set(Rid pipe, params (int Binding, RenderingDevice.UniformType Type, Rid Rid)[] us)
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

    void Build()
    {
        CheckLayout();
        pipeIntegrate = Compile("debris_integrate", DebrisShaders.Integrate);
        pipeInject = Compile("debris_inject", DebrisShaders.Inject);
        pipeRebase = Compile("debris_rebase", DebrisShaders.Rebase);

        texH = (Capacity + TexW - 1) / TexW;
        posBuf = Buffer(Capacity * 16);
        velBuf = Buffer(Capacity * 16);
        massBuf = Buffer(Capacity * 4);
        headBuf = Buffer((Regions + 1) * 16);
        eventBuf = Buffer(Regions * EventsPerRegion * 48);
        stageBuf = Buffer(StagingCapacity * 48);
        var uboBytes = MemoryMarshal.AsBytes(new ReadOnlySpan<DebrisBodiesUbo>(ref ubo));
        uboBuf = rd.UniformBufferCreate((uint)uboBytes.Length, uboBytes);
        if (!uboBuf.IsValid) throw new InvalidOperationException("UniformBufferCreate failed");
        buffers.Add(uboBuf);

        var fmt = new RDTextureFormat
        {
            Format = RenderingDevice.DataFormat.R32G32B32A32Sfloat,
            TextureType = RenderingDevice.TextureType.Type2D,
            Width = TexW,
            Height = (uint)texH,
            Depth = 1,
            ArrayLayers = 1,
            Mipmaps = 1,
            UsageBits = RenderingDevice.TextureUsageBits.SamplingBit | RenderingDevice.TextureUsageBits.StorageBit
                        | RenderingDevice.TextureUsageBits.CanUpdateBit | RenderingDevice.TextureUsageBits.CanCopyFromBit | RenderingDevice.TextureUsageBits.CanCopyToBit,
        };
        posTex = rd.TextureCreate(fmt, new RDTextureView(), new Godot.Collections.Array<byte[]>());
        if (!posTex.IsValid) throw new InvalidOperationException("TextureCreate(rgba32f) failed");
        buffers.Add(posTex);
        rd.TextureClear(posTex, new Color(0, 0, 0, 0), 0, 1, 0, 1);

        const RenderingDevice.UniformType Image = RenderingDevice.UniformType.Image;
        setIntegrate = Set(pipeIntegrate, (0, Ssbo, posBuf), (1, Ssbo, velBuf), (2, RenderingDevice.UniformType.UniformBuffer, uboBuf),
            (3, Image, posTex), (4, Ssbo, headBuf), (5, Ssbo, eventBuf));
        setInject = Set(pipeInject, (0, Ssbo, stageBuf), (1, Ssbo, posBuf), (2, Ssbo, velBuf), (3, Ssbo, massBuf), (4, Image, posTex));
        setRebase = Set(pipeRebase, (0, Ssbo, posBuf), (1, Ssbo, velBuf), (2, Image, posTex));
        if (Contacts) BuildContacts();
        RoundTrip();

        headCallback = Callable.From<byte[]>(OnHead);
        dataCallback = Callable.From<byte[]>(OnData);

        texture = new Texture2Drd { TextureRdRid = posTex };
        material = new ShaderMaterial { Shader = GD.Load<Shader>("res://Shaders/debris_points.gdshader"), RenderPriority = 4 };
        material.SetShaderParameter(PTex, texture);
        material.SetShaderParameter(PTexW, TexW);
        node = new MeshInstance3D { CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, ExtraCullMargin = 16384, Visible = false };
        AddChild(node);
    }

    void BuildContacts()
    {
        pipeClear = Compile("debris_clear", DebrisShaders.Clear);
        pipeCount = Compile("debris_count", DebrisShaders.Count);
        pipeScan = Compile("debris_scan", DebrisShaders.ScanBlock);
        pipeAdd = Compile("debris_scan_add", DebrisShaders.ScanAdd);
        pipeScatter = Compile("debris_scatter", DebrisShaders.Scatter);
        pipeContacts = Compile("debris_contacts", DebrisShaders.ContactsKernel);
        tableSize = 1;
        while (tableSize < 2u * (uint)Capacity) tableSize <<= 1;
        tableMask = tableSize - 1;
        var posB = Buffer(Capacity * 16);
        var velB = Buffer(Capacity * 16);
        var orig = Buffer(Capacity * 4);
        var cellRank = Buffer(Capacity * 8);
        var cnt = Buffer((int)tableSize * 4);
        var start = Buffer((int)tableSize * 4);
        setClear = Set(pipeClear, (0, Ssbo, cnt));
        setCount = Set(pipeCount, (0, Ssbo, posBuf), (1, Ssbo, cnt), (2, Ssbo, cellRank));
        setScatter = Set(pipeScatter, (0, Ssbo, posBuf), (1, Ssbo, velBuf), (2, Ssbo, massBuf), (3, Ssbo, posB), (4, Ssbo, velB),
            (5, Ssbo, orig), (6, Ssbo, cellRank), (7, Ssbo, start));
        setContacts = Set(pipeContacts, (0, Ssbo, posB), (1, Ssbo, velB), (2, Ssbo, velBuf), (3, Ssbo, orig), (4, Ssbo, start), (5, Ssbo, cnt));
        // Multi-level Blelloch scan, 1024 per workgroup; block totals recurse until one block remains (spike M1a).
        var src = cnt;
        var dst = start;
        var count = tableSize;
        var levelDst = new List<(Rid Dst, uint Count)>();
        while (true)
        {
            var blocks = (count + 1023) / 1024;
            var sums = Buffer((int)Math.Max(blocks, 4) * 4);
            scanLevels.Add((Set(pipeScan, (0, Ssbo, src), (1, Ssbo, dst), (2, Ssbo, sums)), count));
            levelDst.Add((dst, count));
            if (blocks == 1) break;
            src = sums;
            dst = Buffer((int)Math.Max(blocks, 4) * 4);
            count = blocks;
        }
        for (var l = levelDst.Count - 2; l >= 0; l--)
            addLevels.Add((Set(pipeAdd, (0, Ssbo, levelDst[l].Dst), (1, Ssbo, levelDst[l + 1].Dst)), levelDst[l].Count));
    }

    /// <summary>
    /// Proves the managed layouts are the bytes the kernels see: a patterned UBO and staging record go up and come back
    /// identical (one synchronous readback, at startup only). Throws, which disables the tier, on any mismatch.
    /// </summary>
    void RoundTrip()
    {
        ubo = default;
        ubo.O2 = new DebrisFloat4(new Vec3(1, 2, 3), 4);
        ubo.C0[0] = new DebrisFloat4(new Vec3(5, 6, 7), 8);
        ubo.C3[MaxBodies - 1] = new DebrisFloat4(new Vec3(9, 10, 11), 12);
        var uboBytes = MemoryMarshal.AsBytes(new ReadOnlySpan<DebrisBodiesUbo>(ref ubo));
        rd.BufferUpdate(uboBuf, 0, (uint)uboBytes.Length, uboBytes);
        var got = rd.BufferGetData(uboBuf);
        var uboOk = got.Length >= uboBytes.Length && got.AsSpan(0, uboBytes.Length).SequenceEqual(uboBytes);
        var lastC3 = MemoryMarshal.Cast<byte, float>(got.AsSpan(32 + 3 * MaxBodies * 16 + (MaxBodies - 1) * 16, 16));
        uboOk &= lastC3[0] == 9 && lastC3[3] == 12;
        stage[0] = new DebrisInjectRecord { Pos = new DebrisFloat4(new Vec3(1.5, -2.5, 3.5), 0.25), Vel = new DebrisFloat4(new Vec3(-7, 8, -9), 0), Slot = 123456, Mass = 6.5e11f };
        var stageBytes = MemoryMarshal.AsBytes(stage.AsSpan(0, 1));
        rd.BufferUpdate(stageBuf, 0, 48, stageBytes);
        var back = rd.BufferGetData(stageBuf, 0, 48);
        var stageOk = back.Length == 48 && back.AsSpan().SequenceEqual(stageBytes) && MemoryMarshal.Cast<byte, uint>(back.AsSpan())[8] == 123456;
        ubo = default;
        rd.BufferUpdate(uboBuf, 0, (uint)uboBytes.Length, MemoryMarshal.AsBytes(new ReadOnlySpan<DebrisBodiesUbo>(ref ubo)));
        GD.Print($"[debris] layout round-trip: bodies UBO {got.Length} B {(uboOk ? "identical" : "MISMATCH")}, staging record {(stageOk ? "identical" : "MISMATCH")}");
        if (!uboOk || !stageOk) throw new InvalidOperationException("layout round-trip failed");
    }

    static void CheckLayout()
    {
        void Check(bool ok, string what)
        {
            if (!ok) throw new InvalidOperationException("layout: " + what);
        }
        Check(Unsafe.SizeOf<DebrisFloat4>() == 16, "sizeof(DebrisFloat4) != 16");
        Check(Unsafe.SizeOf<DebrisBodiesUbo>() == DebrisBodiesUbo.Bytes, "sizeof(DebrisBodiesUbo)");
        Check(Marshal.OffsetOf<DebrisBodiesUbo>(nameof(DebrisBodiesUbo.C3)) == 32 + 3 * MaxBodies * 16, "DebrisBodiesUbo.C3 offset");
        Check(Unsafe.SizeOf<DebrisInjectRecord>() == 48, "sizeof(DebrisInjectRecord) != 48");
        Check(Unsafe.SizeOf<DebrisEventRecord>() == 48, "sizeof(DebrisEventRecord) != 48");
    }

    int FreeAll()
    {
        if (rd == null) return 0;
        if (texture != null) texture.TextureRdRid = default;
        var freed = 0;
        foreach (var s in sets) if (rd.UniformSetIsValid(s)) { rd.FreeRid(s); freed++; }
        foreach (var p in pipelines) if (p.IsValid) { rd.FreeRid(p); freed++; }
        foreach (var s in shaders) if (s.IsValid) { rd.FreeRid(s); freed++; }
        foreach (var b in buffers) if (b.IsValid) { rd.FreeRid(b); freed++; }
        freed += FreeUndoCopies();
        sets.Clear();
        pipelines.Clear();
        shaders.Clear();
        buffers.Clear();
        IsReady = false;
        return freed;
    }

    public override void _ExitTree()
    {
        if (rd == null) return;
        var timing = IsReady ? ComputePercentiles() : "";
        var mem = IsReady ? MemMb() : "";
        var freed = FreeAll();
        GD.Print(string.Create(CultureInfo.InvariantCulture,
            $"[debris] exit: {timing}; queued={Queued} absorbed={AbsorbedCount} dust={DustCount} stale_events={StaleEvents} busy_frames={BusyFrames}; freed {freed} RIDs; rd before free {mem}"));
    }
}

/// <summary>
/// GLSL compute for <see cref="DebrisTier"/>, compiled at runtime (RDShaderSource → SPIR-V) like spike M1a. Units: km, s,
/// km/s, GM km³/s². Positions and velocities are relative to the moving origin body.
/// </summary>
static class DebrisShaders
{
    // Texel w: 1 + age_q·4096 + stream·2048 + speed_q (age 12-bit, speed 11-bit, exact in float32), or −1 where there is no
    // live particle. A negative vel.w is a tidal-stream particle whose age is |vel.w|.
    const string Pack = """
        float pack_state(float radius, vec4 v, float speed_ref, float age_ref) {
          if (radius <= 0.0) return -1.0;
          float sn = clamp(length(v.xyz) / speed_ref, 0.0, 1.0);
          float an = clamp(abs(v.w) / age_ref, 0.0, 1.0);
          uint stream = v.w < 0.0 ? 2048u : 0u;
          return 1.0 + float(uint(an * 4095.0 + 0.5) * 4096u + stream + uint(sn * 2047.0 + 0.5));
        }

        """;

    public const string Integrate = """
        #version 450
        layout(local_size_x = 256) in;
        layout(set = 0, binding = 0, std430) restrict buffer Pos { vec4 p[]; } pos;
        layout(set = 0, binding = 1, std430) restrict buffer Vel { vec4 v[]; } vel;
        layout(set = 0, binding = 2, std140) uniform Bodies {
          vec4 o2; vec4 o3; vec4 c0[64]; vec4 c1[64]; vec4 c2[64]; vec4 c3[64];
        } B;
        layout(set = 0, binding = 3, rgba32f) uniform restrict writeonly image2D pos_tex;
        layout(set = 0, binding = 4, std430) restrict buffer Head { uint h[]; } head;
        struct Event { uvec4 a; vec4 b; vec4 c; };
        layout(set = 0, binding = 5, std430) restrict writeonly buffer Events { Event e[]; } events;
        layout(push_constant, std430) uniform PC {
          uint n; uint n_sub; uint n_bodies; float dt;
          float lifetime; float bound2; uint head_slot; uint cap;
          uint tag; uint ev_base; uint tex_w; float speed_ref;
          float age_ref; uint p0; uint p1; uint p2;
        } pc;
        """ + Pack + """
        shared vec4 s_prev[64];
        shared vec4 s_next[64];

        // Body k relative to the origin at frame fraction s, and its velocity (d/dt = d/ds / dt).
        vec3 body_at(uint k, float s) { return B.c0[k].xyz + s * (B.c1[k].xyz + s * (B.c2[k].xyz + s * B.c3[k].xyz)); }
        vec3 body_vel(uint k, float s) { return (B.c1[k].xyz + s * (2.0 * B.c2[k].xyz + 3.0 * s * B.c3[k].xyz)) / pc.dt; }

        // Gravity of the bodies at s_next's time, minus the origin's own acceleration (the frame's fictitious force).
        // Inside a body the force is held at its surface value, as Universe does, so it never diverges. A black hole
        // (c2.w = r_s > 0) pulls by Paczyński–Wiita, GM/(r (r − r_s)²) per unit d, as Universe.Accelerate does; its
        // contact radius is 2 r_s, so r − r_s ≥ r_s.
        vec3 accel(vec3 x, float s) {
          vec3 a = -(B.o2.xyz + s * B.o3.xyz);
          for (uint k = 0u; k < pc.n_bodies; k++) {
            vec3 d = s_next[k].xyz - x;
            float r = B.c1[k].w;
            float r2 = max(dot(d, d), r * r);
            float hs = B.c2[k].w;
            if (hs > 0.0) {
              float rr = sqrt(r2);
              float e = rr - hs;
              a += (s_next[k].w / (rr * e * e)) * d;
            } else {
              a += (s_next[k].w * inversesqrt(r2) / r2) * d;
            }
          }
          return a;
        }

        // The particle dies only if its event could be written; otherwise it lives on and is caught next time.
        bool record(uint i, uint k, uint kind, float s, vec3 w, float age, vec3 xr) {
          uint slot = atomicAdd(head.h[pc.head_slot * 4u], 1u);
          if (slot >= pc.cap) return false;
          events.e[pc.ev_base + slot] = Event(uvec4(i, k | (kind << 8u), pc.tag, floatBitsToUint(s)), vec4(w, age), vec4(xr, 0.0));
          return true;
        }

        void main() {
          uint i = gl_GlobalInvocationID.x;
          uint lid = gl_LocalInvocationID.x;
          vec4 x = vec4(0.0);
          vec4 v = vec4(0.0);
          bool live = false;
          if (i < pc.n) {
            x = pos.p[i];
            v = vel.v[i];
            live = x.w > 0.0;
          }
          float inv_sub = 1.0 / float(pc.n_sub);
          float h = pc.dt * inv_sub;
          float life = pc.lifetime * (0.8 + 0.4 * fract(float(i) * 0.61803398875));
          if (lid < pc.n_bodies) s_next[lid] = vec4(body_at(lid, 0.0), B.c0[lid].w);
          barrier();
          vec3 a = live ? accel(x.xyz, 0.0) : vec3(0.0);
          // Every invocation runs every substep (the barriers need the whole workgroup); dead ones just skip the work.
          for (uint j = 0u; j < pc.n_sub; j++) {
            float s1 = float(j + 1u) * inv_sub;
            barrier();
            if (lid < pc.n_bodies) {
              s_prev[lid] = s_next[lid];
              s_next[lid] = vec4(body_at(lid, s1), B.c0[lid].w);
            }
            barrier();
            if (live) {
              v.xyz += (0.5 * h) * a;
              vec3 x0 = x.xyz;
              x.xyz += h * v.xyz;
              // Swept test: the straight relative path from x0 − b(s0) to x1 − b(s1) against every sphere, so a fast
              // particle cannot step through a body between two samples.
              uint hit = 0xFFu;
              float th = 2.0;
              vec3 ch = vec3(0.0);
              for (uint k = 0u; k < pc.n_bodies; k++) {
                vec3 e = x0 - s_prev[k].xyz;
                vec3 d = (x.xyz - s_next[k].xyz) - e;
                float r = B.c1[k].w;
                float dd = dot(d, d);
                float t = dd > 0.0 ? clamp(-dot(e, d) / dd, 0.0, 1.0) : 0.0;
                vec3 c = e + t * d;
                if (dot(c, c) < r * r && t < th) {
                  hit = k;
                  th = t;
                  ch = c;
                }
              }
              bool gone = false;
              if (hit != 0xFFu) {
                float s = (float(j) + th) * inv_sub;
                gone = record(i, hit, 0u, s, v.xyz - body_vel(hit, s), abs(v.w), ch);
              }
              if (!gone) {
                a = accel(x.xyz, s1);
                v.xyz += (0.5 * h) * a;
                // Age grows in magnitude; its sign (the stream flag) stays.
                v.w += v.w < 0.0 ? -abs(h) : abs(h);
                if (abs(v.w) > life || dot(x.xyz, x.xyz) > pc.bound2) {
                  uint best = 0u;
                  float pull = -1.0;
                  for (uint k = 0u; k < pc.n_bodies; k++) {
                    vec3 d = s_next[k].xyz - x.xyz;
                    float g = s_next[k].w / max(dot(d, d), 1e-30);
                    if (g > pull) {
                      pull = g;
                      best = k;
                    }
                  }
                  gone = record(i, best, 1u, s1, v.xyz - body_vel(best, s1), abs(v.w), x.xyz - s_next[best].xyz);
                }
              }
              if (gone) {
                live = false;
                x.w = -x.w;
              }
            }
          }
          if (i < pc.n) {
            pos.p[i] = x;
            vel.v[i] = v;
            imageStore(pos_tex, ivec2(int(i % pc.tex_w), int(i / pc.tex_w)), vec4(x.xyz, pack_state(x.w, v, pc.speed_ref, pc.age_ref)));
          }
        }
        """;

    public const string Inject = """
        #version 450
        layout(local_size_x = 256) in;
        struct Inj { vec4 p; vec4 v; uvec4 meta; };
        layout(set = 0, binding = 0, std430) restrict readonly buffer Stage { Inj s[]; } st;
        layout(set = 0, binding = 1, std430) restrict writeonly buffer Pos { vec4 p[]; } pos;
        layout(set = 0, binding = 2, std430) restrict writeonly buffer Vel { vec4 v[]; } vel;
        layout(set = 0, binding = 3, std430) restrict writeonly buffer Mass { float m[]; } mass;
        layout(set = 0, binding = 4, rgba32f) uniform restrict writeonly image2D pos_tex;
        layout(push_constant, std430) uniform PC { uint count; uint tex_w; float speed_ref; float age_ref; } pc;
        """ + Pack + """
        void main() {
          uint k = gl_GlobalInvocationID.x;
          if (k >= pc.count) return;
          Inj r = st.s[k];
          uint i = r.meta.x;
          pos.p[i] = r.p;
          vel.v[i] = r.v;
          mass.m[i] = uintBitsToFloat(r.meta.y);
          imageStore(pos_tex, ivec2(int(i % pc.tex_w), int(i / pc.tex_w)), vec4(r.p.xyz, pack_state(r.p.w, r.v, pc.speed_ref, pc.age_ref)));
        }
        """;

    public const string Rebase = """
        #version 450
        layout(local_size_x = 256) in;
        layout(set = 0, binding = 0, std430) restrict buffer Pos { vec4 p[]; } pos;
        layout(set = 0, binding = 1, std430) restrict buffer Vel { vec4 v[]; } vel;
        layout(set = 0, binding = 2, rgba32f) uniform restrict writeonly image2D pos_tex;
        layout(push_constant, std430) uniform PC {
          uint n; uint tex_w; float speed_ref; float age_ref;
          float dx; float dy; float dz; float p0;
          float dvx; float dvy; float dvz; float p1;
        } pc;
        """ + Pack + """
        void main() {
          uint i = gl_GlobalInvocationID.x;
          if (i >= pc.n) return;
          vec4 x = pos.p[i];
          if (x.w == 0.0) return;
          vec4 v = vel.v[i];
          x.xyz += vec3(pc.dx, pc.dy, pc.dz);
          v.xyz += vec3(pc.dvx, pc.dvy, pc.dvz);
          pos.p[i] = x;
          vel.v[i] = v;
          imageStore(pos_tex, ivec2(int(i % pc.tex_w), int(i / pc.tex_w)), vec4(x.xyz, pack_state(x.w, v, pc.speed_ref, pc.age_ref)));
        }
        """;

    // ---- contacts: spike M1a's counting-sort grid with the 4×4×4 block key, over live particles only ----

    const string Hash = """
        uint cell_hash(ivec3 c) {
          return (uint(c.x) * 73856093u) ^ (uint(c.y) * 19349663u) ^ (uint(c.z) * 83492791u);
        }
        uint cell_key(ivec3 c, uint mask) {
          uvec3 l = uvec3(c & 3);
          return ((cell_hash(c >> 2) << 6u) | l.x | (l.y << 2u) | (l.z << 4u)) & mask;
        }

        """;

    public const string Clear = """
        #version 450
        layout(local_size_x = 256) in;
        layout(set = 0, binding = 0, std430) restrict writeonly buffer Buf { uint d[]; } buf;
        layout(push_constant, std430) uniform PC { uint count; uint p0; uint p1; uint p2; } pc;
        void main() {
          uint i = gl_GlobalInvocationID.x;
          if (i < pc.count) buf.d[i] = 0u;
        }
        """;

    public const string Count = """
        #version 450
        layout(local_size_x = 256) in;
        layout(set = 0, binding = 0, std430) restrict readonly buffer Pos { vec4 p[]; } pos;
        layout(set = 0, binding = 1, std430) restrict buffer Cnt { uint c[]; } cnt;
        layout(set = 0, binding = 2, std430) restrict writeonly buffer CellRank { uvec2 cr[]; } cell_rank;
        layout(push_constant, std430) uniform PC { uint n; uint mask; float inv_cell; uint p0; } pc;
        """ + Hash + """
        void main() {
          uint i = gl_GlobalInvocationID.x;
          if (i >= pc.n) return;
          vec4 p = pos.p[i];
          if (p.w <= 0.0) {
            cell_rank.cr[i] = uvec2(0xFFFFFFFFu, 0u);
            return;
          }
          uint h = cell_key(ivec3(floor(p.xyz * pc.inv_cell)), pc.mask);
          uint r = atomicAdd(cnt.c[h], 1u);
          cell_rank.cr[i] = uvec2(h, r);
        }
        """;

    public const string ScanBlock = """
        #version 450
        layout(local_size_x = 512) in;
        layout(set = 0, binding = 0, std430) restrict readonly buffer In { uint d[]; } src;
        layout(set = 0, binding = 1, std430) restrict writeonly buffer Out { uint d[]; } dst;
        layout(set = 0, binding = 2, std430) restrict writeonly buffer Sums { uint d[]; } sums;
        layout(push_constant, std430) uniform PC { uint count; uint p0; uint p1; uint p2; } pc;
        shared uint s[1024];
        void main() {
          uint t = gl_LocalInvocationID.x;
          uint base = gl_WorkGroupID.x * 1024u;
          uint a = base + 2u * t;
          uint b = a + 1u;
          s[2u * t] = a < pc.count ? src.d[a] : 0u;
          s[2u * t + 1u] = b < pc.count ? src.d[b] : 0u;
          uint off = 1u;
          for (uint dd = 512u; dd > 0u; dd >>= 1u) {
            barrier();
            if (t < dd) {
              uint ai = off * (2u * t + 1u) - 1u;
              uint bi = off * (2u * t + 2u) - 1u;
              s[bi] += s[ai];
            }
            off <<= 1u;
          }
          barrier();
          if (t == 0u) {
            sums.d[gl_WorkGroupID.x] = s[1023];
            s[1023] = 0u;
          }
          for (uint dd = 1u; dd < 1024u; dd <<= 1u) {
            off >>= 1u;
            barrier();
            if (t < dd) {
              uint ai = off * (2u * t + 1u) - 1u;
              uint bi = off * (2u * t + 2u) - 1u;
              uint tmp = s[ai];
              s[ai] = s[bi];
              s[bi] += tmp;
            }
          }
          barrier();
          if (a < pc.count) dst.d[a] = s[2u * t];
          if (b < pc.count) dst.d[b] = s[2u * t + 1u];
        }
        """;

    public const string ScanAdd = """
        #version 450
        layout(local_size_x = 256) in;
        layout(set = 0, binding = 0, std430) restrict buffer Data { uint d[]; } data;
        layout(set = 0, binding = 1, std430) restrict readonly buffer Carry { uint d[]; } carry;
        layout(push_constant, std430) uniform PC { uint count; uint p0; uint p1; uint p2; } pc;
        void main() {
          uint i = gl_GlobalInvocationID.x;
          if (i < pc.count) data.d[i] += carry.d[i >> 10u];
        }
        """;

    // Sorted copies carry the mass in vel.w and the original slot alongside, so contacts write back to the slot (the CPU
    // mirror is indexed by slot; the spike's write-back in sorted order would scramble it).
    public const string Scatter = """
        #version 450
        layout(local_size_x = 256) in;
        layout(set = 0, binding = 0, std430) restrict readonly buffer Pos { vec4 p[]; } pos;
        layout(set = 0, binding = 1, std430) restrict readonly buffer Vel { vec4 v[]; } vel;
        layout(set = 0, binding = 2, std430) restrict readonly buffer Mass { float m[]; } mass;
        layout(set = 0, binding = 3, std430) restrict writeonly buffer PosB { vec4 p[]; } pos_b;
        layout(set = 0, binding = 4, std430) restrict writeonly buffer VelB { vec4 v[]; } vel_b;
        layout(set = 0, binding = 5, std430) restrict writeonly buffer Orig { uint o[]; } orig;
        layout(set = 0, binding = 6, std430) restrict readonly buffer CellRank { uvec2 cr[]; } cell_rank;
        layout(set = 0, binding = 7, std430) restrict readonly buffer Start { uint s[]; } start;
        layout(push_constant, std430) uniform PC { uint n; uint p0; uint p1; uint p2; } pc;
        void main() {
          uint i = gl_GlobalInvocationID.x;
          if (i >= pc.n) return;
          uvec2 hr = cell_rank.cr[i];
          if (hr.x == 0xFFFFFFFFu) return;
          uint dst = start.s[hr.x] + hr.y;
          pos_b.p[dst] = pos.p[i];
          vel_b.v[dst] = vec4(vel.v[i].xyz, mass.m[i]);
          orig.o[dst] = i;
        }
        """;

    // Jacobi impulse on pre-contact velocities: pair (i,j) contributes exactly opposite momenta, so T2's momentum is kept.
    // Δv_i = (1+e)·m_j/(m_i+m_j)·(v_rel·n)·n for approaching pairs; only velocities change, positions stay in place.
    public const string ContactsKernel = """
        #version 450
        layout(local_size_x = 256) in;
        layout(set = 0, binding = 0, std430) restrict readonly buffer PosB { vec4 p[]; } pos_b;
        layout(set = 0, binding = 1, std430) restrict readonly buffer VelB { vec4 v[]; } vel_b;
        layout(set = 0, binding = 2, std430) restrict buffer Vel { vec4 v[]; } vel;
        layout(set = 0, binding = 3, std430) restrict readonly buffer Orig { uint o[]; } orig;
        layout(set = 0, binding = 4, std430) restrict readonly buffer Start { uint s[]; } start;
        layout(set = 0, binding = 5, std430) restrict readonly buffer Cnt { uint c[]; } cnt;
        layout(push_constant, std430) uniform PC { uint n; uint mask; float inv_cell; float one_plus_e; } pc;
        """ + Hash + """
        void main() {
          uint i = gl_GlobalInvocationID.x;
          uint total = start.s[pc.mask] + cnt.c[pc.mask];
          if (i >= pc.n || i >= total) return;
          vec4 pi = pos_b.p[i];
          vec4 vi = vel_b.v[i];
          float mi = vi.w;
          ivec3 c = ivec3(floor(pi.xyz * pc.inv_cell));
          uint seen[27];
          uint n_seen = 0u;
          vec3 dv = vec3(0.0);
          for (int dz = -1; dz <= 1; dz++)
          for (int dy = -1; dy <= 1; dy++)
          for (int dx = -1; dx <= 1; dx++) {
            uint h = cell_key(c + ivec3(dx, dy, dz), pc.mask);
            bool dup = false;
            for (uint k = 0u; k < n_seen; k++) dup = dup || seen[k] == h;
            if (dup) continue;
            seen[n_seen++] = h;
            uint s0 = start.s[h];
            uint s1 = s0 + cnt.c[h];
            for (uint j = s0; j < s1; j++) {
              if (j == i) continue;
              vec4 pj = pos_b.p[j];
              vec3 d = pj.xyz - pi.xyz;
              float rs = pi.w + pj.w;
              float d2 = dot(d, d);
              if (d2 >= rs * rs || d2 <= 0.0) continue;
              vec3 nrm = d * inversesqrt(d2);
              vec4 vj = vel_b.v[j];
              float vn = dot(vj.xyz - vi.xyz, nrm);
              if (vn < 0.0) dv += (pc.one_plus_e * vj.w / (mi + vj.w)) * vn * nrm;
            }
          }
          if (dv != vec3(0.0)) vel.v[orig.o[i]].xyz = vi.xyz + dv;
        }
        """;
}
