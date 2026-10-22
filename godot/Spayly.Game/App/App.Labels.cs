using System;
using System.Collections.Generic;
using Godot;
using Spayly.Game.Render;
using Spayly.Sim;

namespace Spayly.Game.App;

/// <summary>
/// Label occlusion and declutter (app.ts updateLabels). A projection alone would let a body 19 AU behind Earth write its
/// name across Earth's disc, and a planet's moons pile their names into one smear (spike M1c saw exactly that from 30 AU).
/// First a label is hidden if a nearer disc of at least 8 px covers the body or its label box; then, where boxes collide,
/// the more important body keeps its name: selected/focused, then star > planet > moon > asteroid > fragment, then larger
/// on screen.
/// </summary>
public partial class App
{
    struct Mark
    {
        public Body Body;
        public BodyLabel Label;
        /// <summary>False once the body fills the view (BodyView.LabelAllowed).</summary>
        public bool Allowed;
        /// <summary>Px the label's bottom edge sits above the point.</summary>
        public float Lift;
        public float X, Y, R;
        public double D;
        public Rect2 Box;
        public bool Pinned;
        public int Rank;
    }

    sealed class ByDistance : IComparer<Mark>
    {
        public int Compare(Mark a, Mark b) => a.D.CompareTo(b.D);
    }

    sealed class ByImportance : IComparer<Mark>
    {
        public int Compare(Mark a, Mark b)
        {
            if (a.Pinned != b.Pinned) return a.Pinned ? -1 : 1;
            if (a.Rank != b.Rank) return a.Rank.CompareTo(b.Rank);
            return b.R.CompareTo(a.R);
        }
    }

    readonly List<Mark> marks = [];
    readonly List<Mark> occluders = [];
    readonly List<Mark> candidates = [];
    readonly List<Rect2> placed = [];
    static readonly ByDistance byDistance = new();
    static readonly ByImportance byImportance = new();

    static int KindRank(BodyKind k) => k switch
    {
        BodyKind.Star => 0,
        BodyKind.Planet => 1,
        BodyKind.Moon => 2,
        BodyKind.Asteroid => 3,
        _ => 4,
    };

    void UpdateLabels()
    {
        // The remnant points keep moving with the sim even while the labels are hidden, so they are right when shown again.
        if (!Settings.Labels)
        {
            AdvanceRemnants();
            return;
        }
        marks.Clear();
        foreach (var view in views.Values)
        {
            var label = labels.For(view.Body.Id);
            if (label == null) continue;
            if (!labels.Project(view.Position, out var s))
            {
                label.Visible = false;
                continue;
            }
            marks.Add(new Mark
            {
                Body = view.Body,
                Label = label,
                Allowed = view.LabelAllowed,
                // The label's bottom edge sits `height` radii straight up the screen from the body's centre.
                Lift = (float)view.LabelHeight * (float)view.PixelRadius,
                X = s.X,
                Y = s.Y,
                R = (float)view.PixelRadius,
                D = view.TrueDistance,
                Pinned = ReferenceEquals(view.Body, Selected) || ReferenceEquals(view.Body, Rig.Focus),
                Rank = KindRank(view.Body.Kind),
            });
        }
        // The debris of a torn-apart planet is not a body in the scene, but it is still something the player should be able to name.
        AdvanceRemnants();
        foreach (var r in remnants)
        {
            var label = labels.For(r.Id);
            if (label == null) continue;
            if (!labels.Project(ctx.Origin.Place(r.Pos, out _, out var dist), out var s))
            {
                label.Visible = false;
                continue;
            }
            // A dot-sized point that ranks with planets: it stands for one.
            marks.Add(new Mark { Body = r.Ghost, Label = label, Allowed = true, Lift = 6, X = s.X, Y = s.Y, R = 0, D = dist, Rank = KindRank(BodyKind.Planet) });
        }
        marks.Sort(byDistance);
        // Only a disc big enough to read as a body hides what is behind it; a far planet's dot on a label is left to the overlap pass.
        occluders.Clear();
        foreach (var m in marks)
            if (m.R >= 8) occluders.Add(m);
        candidates.Clear();
        foreach (var m0 in marks)
        {
            var m = m0;
            if (!m.Allowed)
            {
                m.Label.Visible = false;
                continue;
            }
            // The label's bottom edge sits `height` radii straight up the screen from the body's centre.
            var ay = m.Y - m.Lift;
            var halfW = m.Label.HalfWidth;
            m.Box = new Rect2(m.X - halfW, ay - BodyLabel.BoxHeight, halfW * 2, BodyLabel.BoxHeight);
            var hidden = false;
            foreach (var o in occluders)
            {
                if (o.D >= m.D) break;
                var cx = Math.Clamp(o.X, m.Box.Position.X, m.Box.End.X);
                var cy = Math.Clamp(o.Y, m.Box.Position.Y, m.Box.End.Y);
                if (Hypot(o.X - m.X, o.Y - m.Y) < o.R || Hypot(o.X - cx, o.Y - cy) < o.R)
                {
                    hidden = true;
                    break;
                }
            }
            if (hidden) m.Label.Visible = false;
            else
            {
                m.Label.Place(m.X, ay);
                candidates.Add(m);
            }
        }
        candidates.Sort(byImportance);
        placed.Clear();
        foreach (var m in candidates)
        {
            var b = m.Box;
            var overlap = false;
            foreach (var p in placed)
            {
                if (b.Position.X < p.End.X && b.End.X > p.Position.X && b.Position.Y < p.End.Y && b.End.Y > p.Position.Y)
                {
                    overlap = true;
                    break;
                }
            }
            // A label under a panel still claims its box, as in the web, where the panel only covers it.
            m.Label.Visible = !overlap && !hud.CoversRect(b);
            if (!overlap) placed.Add(b);
        }
    }

    static float Hypot(float x, float y) => MathF.Sqrt(x * x + y * y);
}
