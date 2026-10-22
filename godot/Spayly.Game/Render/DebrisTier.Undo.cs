using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Godot;
using Spayly.Sim;
using Spayly.Sim.Debris;

namespace Spayly.Game.Render;

/// <summary>
/// Undo copies of the tier (M7.3). The particle buffers are copied GPU-side with <c>RenderingDevice.BufferCopy</c> into
/// buffers of their own, never read back, so taking an undo point queues a few copies and does not stall the frame. Only
/// the dispatched slots [0, HighWater) are copied, and an empty tier copies nothing.
///
/// A copy also holds the event regions still in flight (their records and the head counts), so a particle that died on the
/// GPU just before the point was taken is handed to the ledger after a restore exactly as it would have been; and the CPU
/// mirror (masses, free slots, queued injections), which is the ledger's truth for T2.
/// </summary>
public sealed partial class DebrisTier
{
    /// <summary>The tier at one moment; <see cref="SaveCopy"/> makes one, <see cref="RestoreCopy"/> puts it back, <see cref="ReleaseCopy"/> frees it.</summary>
    public sealed class UndoCopy
    {
        internal Rid Pos, Vel, Mass, Events, Head;
        internal uint PosCap, MassCap, EventsCap, HeadCap;
        internal int HighWater, Occupied, FreeCount, PendingCount;
        internal double[] MassMirror = [];
        internal int[] FreeStack = [];
        internal Pending[] PendingItems = [];
        internal DD MassTotal;
        internal double MaxRadius, Lifetime, BoundRadius;
        internal string? OriginId;
        internal RegionCopy[] Regions = [];
        /// <summary>Particles alive or queued when the copy was taken.</summary>
        public int Particles => Occupied;
        /// <summary>Σ of the mirror when the copy was taken, kg.</summary>
        public double MassKg => MassTotal.Value;
        /// <summary>RAM held (the mirror), bytes.</summary>
        public long RamBytes { get; internal set; }
        /// <summary>GPU memory held, bytes.</summary>
        public long VramBytes { get; internal set; }
        /// <summary>Its buffers are gone (released, or the tier shut down): it can no longer restore particles.</summary>
        public bool Released { get; internal set; }
    }

    internal readonly record struct RegionCopy(int Region, uint Tag, RegionState State, int Count, string?[] BodyIds);

    /// <summary>Idle copy buffers kept for reuse, bytes at most: an undo point per action would otherwise create and free 30 MB buffers.</summary>
    const long UndoPoolCap = 32L << 20;
    /// <summary>Smallest copy buffer, bytes; sizes are rounded up to a power of two so the pool can reuse them.</summary>
    const uint UndoMinBuffer = 4096;

    readonly Dictionary<uint, Stack<Rid>> undoPool = [];
    readonly HashSet<Rid> undoLive = [];
    long undoPoolBytes;

    /// <summary>Idle pooled copy buffers, bytes (VRAM the history does not count).</summary>
    public long UndoPoolBytes => undoPoolBytes;

    static uint UndoClass(long bytes)
    {
        var size = UndoMinBuffer;
        while (size < bytes) size <<= 1;
        return size;
    }

    Rid UndoBuffer(uint size)
    {
        Rid b;
        if (undoPool.TryGetValue(size, out var stack) && stack.Count > 0)
        {
            b = stack.Pop();
            undoPoolBytes -= size;
        }
        else
        {
            b = rd.StorageBufferCreate(size, ReadOnlySpan<byte>.Empty, (RenderingDevice.StorageBufferUsage)0);
            if (!b.IsValid) throw new InvalidOperationException($"StorageBufferCreate({size}) for an undo copy failed");
        }
        undoLive.Add(b);
        return b;
    }

    void ReturnUndoBuffer(Rid b, uint size)
    {
        if (!b.IsValid || !undoLive.Remove(b)) return;
        if (undoPoolBytes + size <= UndoPoolCap)
        {
            if (!undoPool.TryGetValue(size, out var stack)) undoPool[size] = stack = new Stack<Rid>();
            stack.Push(b);
            undoPoolBytes += size;
        }
        else rd.FreeRid(b);
    }

    /// <summary>
    /// Copy the tier as it stands: GPU buffers into VRAM-side copies (no readback), the mirror into arrays. Lifetime and bound
    /// radius go with it, since a tidal stream raises both. Throws if a copy buffer cannot be made (the caller drops the copy).
    /// </summary>
    public UndoCopy SaveCopy()
    {
        var c = new UndoCopy { Lifetime = Lifetime, BoundRadius = BoundRadius, MaxRadius = MaxRadius, OriginId = origin?.Id };
        if (!IsReady || (HighWater == 0 && pendingCount == 0)) return c;
        c.HighWater = HighWater;
        c.Occupied = Occupied;
        c.FreeCount = freeCount;
        c.PendingCount = pendingCount;
        c.MassTotal = massTotal;
        c.MassMirror = new double[HighWater];
        Array.Copy(mass, c.MassMirror, HighWater);
        c.FreeStack = new int[freeCount];
        Array.Copy(freeStack, c.FreeStack, freeCount);
        c.PendingItems = new Pending[pendingCount];
        Array.Copy(pending, c.PendingItems, pendingCount);

        var regions = new List<RegionCopy>();
        for (var r = 0; r < Regions; r++)
        {
            if (regionState[r] == RegionState.Free) continue;
            var ids = new string?[MaxBodies];
            for (var k = 0; k < MaxBodies; k++) ids[k] = regionBodies[r * MaxBodies + k]?.Id;
            regions.Add(new RegionCopy(r, regionTag[r], regionState[r], regionCount[r], ids));
        }
        c.Regions = [.. regions];
        c.RamBytes = 8L * HighWater + 4L * freeCount + (long)Unsafe.SizeOf<Pending>() * pendingCount + regions.Count * (MaxBodies * 8L + 64);

        try
        {
            if (HighWater > 0)
            {
                c.PosCap = UndoClass(16L * HighWater);
                c.MassCap = UndoClass(4L * HighWater);
                c.Pos = UndoBuffer(c.PosCap);
                c.Vel = UndoBuffer(c.PosCap);
                c.Mass = UndoBuffer(c.MassCap);
                rd.BufferCopy(posBuf, c.Pos, 0, 0, (uint)(16 * HighWater));
                rd.BufferCopy(velBuf, c.Vel, 0, 0, (uint)(16 * HighWater));
                rd.BufferCopy(massBuf, c.Mass, 0, 0, (uint)(4 * HighWater));
            }
            if (regions.Count > 0)
            {
                const uint region = EventsPerRegion * 48;
                c.EventsCap = UndoClass((long)regions.Count * region);
                c.HeadCap = UndoClass((Regions + 1) * 16);
                c.Events = UndoBuffer(c.EventsCap);
                c.Head = UndoBuffer(c.HeadCap);
                for (var k = 0; k < regions.Count; k++) rd.BufferCopy(eventBuf, c.Events, (uint)regions[k].Region * region, (uint)k * region, region);
                rd.BufferCopy(headBuf, c.Head, 0, 0, (Regions + 1) * 16);
            }
        }
        catch
        {
            ReleaseCopy(c);
            throw;
        }
        c.VramBytes = 2L * c.PosCap + c.MassCap + c.EventsCap + c.HeadCap;
        return c;
    }

    /// <summary>
    /// Put the tier back as <paramref name="c"/> had it. <paramref name="resolve"/> maps a body id to the restored universe's
    /// body: the origin, and the bodies the in-flight event regions name. False, with the tier emptied, when the particles
    /// cannot come back (the copy was released, or the body they are relative to is gone); the caller rebaselines the ledger.
    /// </summary>
    public bool RestoreCopy(UndoCopy c, Func<string, Body?> resolve)
    {
        Lifetime = c.Lifetime;
        BoundRadius = c.BoundRadius;
        if (!IsReady) return c.HighWater == 0 && c.PendingCount == 0;
        var o = c.OriginId != null ? resolve(c.OriginId) : null;
        // Everything below is relative to the frame the copy was taken in; the restored bodies are new objects.
        endState.Clear();
        var empty = c.HighWater == 0 && c.PendingCount == 0;
        if (empty || c.Released || o == null)
        {
            Reset();
            if (o != null) origin = o;
            return empty;
        }
        Array.Clear(mass);
        Array.Copy(c.MassMirror, mass, c.HighWater);
        Array.Copy(c.FreeStack, freeStack, c.FreeCount);
        Array.Copy(c.PendingItems, pending, c.PendingCount);
        HighWater = c.HighWater;
        Occupied = c.Occupied;
        freeCount = c.FreeCount;
        pendingCount = c.PendingCount;
        massTotal = c.MassTotal;
        MaxRadius = c.MaxRadius;
        origin = o;

        if (c.HighWater > 0)
        {
            rd.BufferCopy(c.Pos, posBuf, 0, 0, (uint)(16 * c.HighWater));
            rd.BufferCopy(c.Vel, velBuf, 0, 0, (uint)(16 * c.HighWater));
            rd.BufferCopy(c.Mass, massBuf, 0, 0, (uint)(4 * c.HighWater));
        }

        // Regions: whatever was in flight now is moot (its callbacks find a different tag or state and are dropped); what was
        // in flight then comes back and is read again. Tags keep counting up from now, so none is reused.
        Array.Clear(regionBodies);
        for (var r = 0; r < Regions; r++)
        {
            regionTag[r] = 0;
            regionState[r] = RegionState.Free;
        }
        if (c.Regions.Length > 0)
        {
            const uint region = EventsPerRegion * 48;
            rd.BufferCopy(c.Head, headBuf, 0, 0, (Regions + 1) * 16);
            for (var k = 0; k < c.Regions.Length; k++)
            {
                var rc = c.Regions[k];
                rd.BufferCopy(c.Events, eventBuf, (uint)k * region, (uint)rc.Region * region, region);
                regionTag[rc.Region] = rc.Tag;
                regionCount[rc.Region] = rc.Count;
                regionSince[rc.Region] = frame;
                for (var j = 0; j < MaxBodies; j++)
                    regionBodies[rc.Region * MaxBodies + j] = rc.BodyIds[j] is string id ? resolve(id) : null;
                // A head not yet read is asked for again; events whose count was known are fetched again next frame.
                regionState[rc.Region] = rc.State == RegionState.AwaitHead ? RegionState.AwaitHead : RegionState.NeedData;
            }
            foreach (var rc in c.Regions)
                if (rc.State == RegionState.AwaitHead) rd.BufferGetDataAsync(headBuf, headCallback, (uint)(rc.Region * 16), 16);
        }
        RepaintTexture();
        return true;
    }

    /// <summary>
    /// Rewrite the draw texture from the restored buffers: cleared, then the rebase kernel with a zero shift writes every live
    /// slot, so a paused restore shows the restored cloud at once instead of the one it replaced.
    /// </summary>
    void RepaintTexture()
    {
        rd.TextureClear(posTex, new Color(0, 0, 0, 0), 0, 1, 0, 1);
        if (HighWater == 0) return;
        Span<byte> pc = stackalloc byte[48];
        pc.Clear();
        PushU(pc, 0, (uint)HighWater);
        PushU(pc, 1, TexW);
        PushF(pc, 2, SpeedRef);
        PushF(pc, 3, AgeRef);
        var cl = rd.ComputeListBegin();
        rd.ComputeListBindComputePipeline(cl, pipeRebase);
        rd.ComputeListBindUniformSet(cl, setRebase, 0);
        rd.ComputeListSetPushConstant(cl, pc, 48);
        rd.ComputeListDispatch(cl, Groups(HighWater, Group), 1, 1);
        rd.ComputeListEnd();
    }

    /// <summary>Free a copy's buffers (to the pool) and arrays. Safe to call twice, and after the tier shut down.</summary>
    public void ReleaseCopy(UndoCopy c)
    {
        if (c.Released) return;
        c.Released = true;
        if (rd != null)
        {
            ReturnUndoBuffer(c.Pos, c.PosCap);
            ReturnUndoBuffer(c.Vel, c.PosCap);
            ReturnUndoBuffer(c.Mass, c.MassCap);
            ReturnUndoBuffer(c.Events, c.EventsCap);
            ReturnUndoBuffer(c.Head, c.HeadCap);
        }
        c.Pos = c.Vel = c.Mass = c.Events = c.Head = default;
        c.MassMirror = [];
        c.FreeStack = [];
        c.PendingItems = [];
        c.Regions = [];
        c.RamBytes = c.VramBytes = 0;
    }

    /// <summary>Every copy buffer, live or pooled (shutdown). A copy still held by the history then restores no particles.</summary>
    int FreeUndoCopies()
    {
        var freed = 0;
        foreach (var b in undoLive) if (b.IsValid) { rd.FreeRid(b); freed++; }
        foreach (var stack in undoPool.Values)
            foreach (var b in stack) if (b.IsValid) { rd.FreeRid(b); freed++; }
        undoLive.Clear();
        undoPool.Clear();
        undoPoolBytes = 0;
        return freed;
    }
}
