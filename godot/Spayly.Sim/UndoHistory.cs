using System.Collections;

namespace Spayly.Sim;

/// <summary>What an undo point was taken before. The app keys its side effects on it (an impact's undo drops the player's own rock).</summary>
public enum UndoKind
{
    /// <summary>A collision Collide resolved, automatic, one per cascade.</summary>
    Collision,
    /// <summary>A tidal disruption, automatic.</summary>
    TidalDisruption,
    /// <summary>A collision taken over by a hero SPH run, automatic.</summary>
    SphCollision,
    PlaceHole,
    RemoveHole,
    Launch,
    Erase,
    Mass,
    Radius,
    Reset,
    Moons,
    /// <summary>A capture or demo verb that adds bodies.</summary>
    Demo,
    /// <summary>
    /// A meteor shower: one point for the whole volley. Its <see cref="UniverseSnapshot.Subject"/> is the id prefix its rocks
    /// share, and while it is the newest point the rocks' own impacts record none (<see cref="Universe.PushHistory"/>).
    /// </summary>
    Shower,
    Other,
}

/// <summary>
/// The undo and redo lists, trimmed by memory rather than by a fixed count. Undo moves the newest point out and puts the
/// state it replaced on the redo list under the same label, redo does the reverse, and any new point clears the redo list:
/// what was undone is gone once the player acts again.
///
/// Budgets are the app's to measure. <see cref="UniverseSnapshot.Bytes"/> is RAM (bodies plus whatever the attachment holds
/// on the CPU) and <see cref="UniverseSnapshot.VramBytes"/> is GPU memory the attachment holds. Past <see cref="VramBudget"/>
/// the oldest points first lose their GPU state through <see cref="ShedVram"/> and stay in the history, so their bodies
/// still come back; past <see cref="MaxPoints"/> or <see cref="ByteBudget"/> the oldest points go altogether.
/// </summary>
public sealed class UndoHistory : IReadOnlyList<UniverseSnapshot>
{
    public const int DefaultMaxPoints = 50;
    /// <summary>bytes.</summary>
    public const long DefaultByteBudget = 300L << 20;
    /// <summary>bytes.</summary>
    public const long DefaultVramBudget = 256L << 20;

    readonly List<UniverseSnapshot> undo = [];
    readonly List<UniverseSnapshot> redo = [];

    /// <summary>Most points kept, undo and redo together.</summary>
    public int MaxPoints { get; set; } = DefaultMaxPoints;
    /// <summary>RAM the points may hold together, bytes.</summary>
    public long ByteBudget { get; set; } = DefaultByteBudget;
    /// <summary>GPU memory the points' attachments may hold together, bytes.</summary>
    public long VramBudget { get; set; } = DefaultVramBudget;
    /// <summary>
    /// Called once for every point that leaves the history for good: evicted, cleared from the redo list by a new point, or
    /// popped by a fault rollback. The app frees what its attachment holds. Not called for the point <see cref="TakeUndo"/>
    /// or <see cref="TakeRedo"/> hands back: the caller restores from it and owns it from then on.
    /// </summary>
    public Action<UniverseSnapshot>? Dropped { get; set; }
    /// <summary>
    /// Asked to free a point's GPU state when the history is over <see cref="VramBudget"/>; it sets the point's
    /// <see cref="UniverseSnapshot.VramBytes"/> to 0 (and lowers its <see cref="UniverseSnapshot.AttachmentBytes"/> by what
    /// went with it). A point left with VRAM stops the shedding.
    /// </summary>
    public Action<UniverseSnapshot>? ShedVram { get; set; }

    /// <summary>Undo points, oldest first.</summary>
    public int Count => undo.Count;
    public UniverseSnapshot this[int index] => undo[index];
    /// <summary>Redo points; the next to redo is last.</summary>
    public IReadOnlyList<UniverseSnapshot> Redo => redo;
    /// <summary>RAM held by every point, bytes.</summary>
    public long Bytes => Sum(p => p.Bytes);
    /// <summary>GPU memory held by every point, bytes.</summary>
    public long VramBytes => Sum(p => p.VramBytes);
    /// <summary>Points dropped for count or RAM since the start.</summary>
    public int Evictions { get; private set; }
    /// <summary>Points whose GPU state was shed for VRAM since the start.</summary>
    public int Sheds { get; private set; }
    /// <summary>
    /// Moves on every change to either list or to a point's GPU state (a push, undo, redo, pop, eviction, shed or clear), so a
    /// view of the history polls one int and rebuilds only when it moves.
    /// </summary>
    public int Version { get; private set; }

    public IEnumerator<UniverseSnapshot> GetEnumerator() => undo.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    long Sum(Func<UniverseSnapshot, long> f)
    {
        long s = 0;
        foreach (var p in undo) s += f(p);
        foreach (var p in redo) s += f(p);
        return s;
    }

    /// <summary>Record a new point: the redo list is cleared, then the history trimmed to its budgets.</summary>
    public void Push(UniverseSnapshot s)
    {
        ClearRedo();
        undo.Add(s);
        Version++;
        Trim();
    }

    public void ClearRedo()
    {
        if (redo.Count == 0) return;
        foreach (var r in redo) Dropped?.Invoke(r);
        redo.Clear();
        Version++;
    }

    /// <summary>The newest undo point, removed without a redo entry (fault rollback); <see cref="Dropped"/> is called for it.</summary>
    public UniverseSnapshot? Pop()
    {
        if (undo.Count == 0) return null;
        var s = undo[^1];
        undo.RemoveAt(undo.Count - 1);
        Version++;
        Dropped?.Invoke(s);
        return s;
    }

    /// <summary>The point <see cref="TakeUndo"/> would return, or null.</summary>
    public UniverseSnapshot? NextUndo => undo.Count > 0 ? undo[^1] : null;
    /// <summary>The point <see cref="TakeRedo"/> would return, or null.</summary>
    public UniverseSnapshot? NextRedo => redo.Count > 0 ? redo[^1] : null;

    /// <summary>
    /// Take the newest undo point out and put <paramref name="current"/> (the state it is about to replace, labelled like it)
    /// on the redo list. Returns the point, or null when there is none (and <paramref name="current"/> is dropped).
    /// </summary>
    public UniverseSnapshot? TakeUndo(UniverseSnapshot current)
    {
        if (undo.Count == 0)
        {
            Dropped?.Invoke(current);
            return null;
        }
        var s = undo[^1];
        undo.RemoveAt(undo.Count - 1);
        redo.Add(current);
        Version++;
        Trim();
        return s;
    }

    /// <summary>The mirror of <see cref="TakeUndo"/>: the next redo point out, <paramref name="current"/> onto the undo list.</summary>
    public UniverseSnapshot? TakeRedo(UniverseSnapshot current)
    {
        if (redo.Count == 0)
        {
            Dropped?.Invoke(current);
            return null;
        }
        var s = redo[^1];
        redo.RemoveAt(redo.Count - 1);
        undo.Add(current);
        Version++;
        Trim();
        return s;
    }

    /// <summary>Drop every point.</summary>
    public void Clear()
    {
        ClearRedo();
        if (undo.Count == 0) return;
        foreach (var u in undo) Dropped?.Invoke(u);
        undo.Clear();
        Version++;
    }

    /// <summary>
    /// Oldest first: the undo list's bottom, then the redo list's far end (the state furthest from now). The newest point
    /// always stays, even alone over budget: an action must stay undoable.
    /// </summary>
    void Trim()
    {
        while (undo.Count + redo.Count > 1 && (undo.Count + redo.Count > MaxPoints || Bytes > ByteBudget))
        {
            UniverseSnapshot gone;
            if (undo.Count > 0)
            {
                gone = undo[0];
                undo.RemoveAt(0);
            }
            else
            {
                gone = redo[0];
                redo.RemoveAt(0);
            }
            Evictions++;
            Version++;
            Dropped?.Invoke(gone);
        }
        if (ShedVram == null) return;
        while (VramBytes > VramBudget)
        {
            var oldest = FirstWithVram();
            if (oldest == null) return;
            ShedVram(oldest);
            Version++;
            if (oldest.VramBytes > 0) return;
            Sheds++;
        }
    }

    UniverseSnapshot? FirstWithVram()
    {
        foreach (var p in undo) if (p.VramBytes > 0) return p;
        foreach (var p in redo) if (p.VramBytes > 0) return p;
        return null;
    }
}
