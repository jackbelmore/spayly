using System.Collections.Generic;
using Godot;
using Spayly.Sim;

namespace Spayly.Game.App;

/// <summary>
/// Erase mode (app.ts eraseAt/showBrush): a screen-space brush deletes asteroids and fragments whose disc it touches.
/// Catalogued bodies, the Sun included, are never erased: this clears up debris, it does not demolish the solar system.
/// </summary>
public partial class App
{
    readonly List<Body> doomed = [];
    bool brushShown;
    /// <summary>This stroke already has its undo point; PointerDown (or the erase verb) starts a new stroke.</summary>
    bool eraseStrokeSaved;

    partial void EraseAt(Vector2 at, ref int count)
    {
        doomed.Clear();
        foreach (var view in views.Values)
        {
            var b = view.Body;
            if (b.Kind is not (BodyKind.Asteroid or BodyKind.Fragment)) continue;
            if (!labels.Project(view.Position, out var s)) continue;
            if (s.DistanceTo(at) <= brushRadius + view.PixelRadius) doomed.Add(b);
        }
        // One undo point per stroke, taken before its first rock goes, so a drag across a debris field undoes in one press.
        if (doomed.Count > 0 && !eraseStrokeSaved)
        {
            RecordUndo(UndoKind.Erase, "an erase stroke");
            eraseStrokeSaved = true;
        }
        foreach (var b in doomed)
        {
            if (ReferenceEquals(Rig.Focus, b)) Rig.Focus = null;
            if (ReferenceEquals(Selected, b)) Select((Body?)null);
            if (LastImpact != null && (ReferenceEquals(LastImpact.Impactor, b) || ReferenceEquals(LastImpact.Target, b))) LastImpact = null;
            Universe.Remove(b);
            periods.Remove(b.Id);
        }
        if (doomed.Count > 0) SyncViews();
        count += doomed.Count;
    }

    partial void ShowBrush(Vector2 at)
    {
        brushShown = true;
        Sink?.SetBrush(at, brushRadius);
    }
}
