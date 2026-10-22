using System;
using System.Collections.Generic;
using Godot;
using Spayly.Game.Render;
using Spayly.Sim;

namespace Spayly.Game.App;

/// <summary>
/// One input dispatcher for every mode (app.ts bindInput). Buttons use the web's numbering: 0 left, 1 middle, 2 right.
/// Explore: left-drag orbits, right/middle/shift-drag pans, wheel zooms (log), click selects, a second click on the same
/// body within 380 ms flies there. Asteroid: left-click on a body fires at it, on empty space starts a hand aim; right-drag
/// orbits. Erase: left-drag paints the brush, wheel resizes it. The asteroid and erase actions are hooks the asteroid and
/// erase tasks fill.
/// </summary>
public partial class App
{
    const double DoubleClickSeconds = 0.38;

    sealed class Drag
    {
        public int Button;
        public Vector2 At;
        public bool Moved;
        public bool Shift;
    }

    Drag? drag;
    (double Time, string Id) lastClick = (-1, "");
    /// <summary>True while a hand aim is being dragged; the asteroid task's StartAim sets it, CancelAim/FireManual clear it.</summary>
    bool aiming;
    double brushRadius = 46;
    int erased;

    public override void _UnhandledInput(InputEvent e)
    {
        switch (e)
        {
            case InputEventMouseButton mb:
                if (mb.ButtonIndex is MouseButton.WheelUp or MouseButton.WheelDown)
                {
                    if (!mb.Pressed) break;
                    // A browser wheel notch is deltaY ≈ ±100; Godot reports notches (factor scales trackpad deltas).
                    var dy = (mb.ButtonIndex == MouseButton.WheelDown ? 100 : -100) * (mb.Factor > 0 ? mb.Factor : 1);
                    Wheel(dy, mb.Position, mb.ShiftPressed);
                    break;
                }
                var button = mb.ButtonIndex switch { MouseButton.Left => 0, MouseButton.Middle => 1, MouseButton.Right => 2, _ => -1 };
                if (button < 0) break;
                if (mb.Pressed) PointerDown(button, mb.Position, mb.ShiftPressed);
                else PointerUp(button, mb.Position);
                break;
            case InputEventMouseMotion mm:
                PointerMove(mm.Position);
                break;
            case InputEventKey k when k.Pressed && !k.Echo:
                // Ahead of the modifier filter below, which drops every Ctrl chord; in every mode and while paused. Plain Z
                // stays the erase toggle.
                if (k.CtrlPressed && !k.AltPressed && !k.MetaPressed && UndoKey(k) is bool redo)
                {
                    if (redo) Redo();
                    else Undo();
                    GetViewport().SetInputAsHandled();
                    break;
                }
                if (k.AltPressed && !k.CtrlPressed && !k.MetaPressed && k.Keycode == Key.R) { hud.ToggleRealism(); break; }
                if (k.CtrlPressed || k.AltPressed || k.MetaPressed) break;
                PressKey(KeyName(k));
                break;
        }
    }

    /// <summary>With Ctrl held: false for Z (undo), true for Shift+Z or Y (redo), null for any other key. By the key's letter, as editors do.</summary>
    static bool? UndoKey(InputEventKey k)
    {
        var key = k.Keycode != Key.None ? k.Keycode : k.PhysicalKeycode;
        return key switch
        {
            Key.Z => k.ShiftPressed,
            Key.Y => true,
            _ => null,
        };
    }

    static string KeyName(InputEventKey k)
    {
        if (k.Keycode == Key.Escape) return "Escape";
        if (k.Keycode == Key.Space) return " ";
        if (k.Unicode > 0) return char.ConvertFromUtf32((int)k.Unicode);
        return k.Keycode.ToString();
    }

    void PointerDown(int button, Vector2 at, bool shift)
    {
        drag = new Drag { Button = button, At = at, Moved = false, Shift = shift };
        if (Mode == Mode.Erase && button == 0 && !shift)
        {
            erased = 0;
            eraseStrokeSaved = false;
            EraseAt(at, ref erased);
            return;
        }
        if (Mode == Mode.Asteroid && button == 0 && !shift)
        {
            var hit = PickBody(at);
            if (hit == null && AimPlanePoint(at) is Vec3 p) StartAim(p, at);
        }
        if (Mode == Mode.BlackHole && button == 0 && !shift) HolePointerDown(at);
    }

    void PointerMove(Vector2 at)
    {
        if (Mode == Mode.Erase) ShowBrush(at);
        // Remembered in every mode, so black-hole mode's readout knows where the pointer is from the moment it opens.
        holePointer = at;
        var d = drag;
        if (Mode == Mode.BlackHole && HolePointerMove(at))
        {
            if (d != null && Math.Abs(at.X - d.At.X) + Math.Abs(at.Y - d.At.Y) > 3) d.Moved = true;
            return;
        }
        if (d == null) return;
        if (Mode == Mode.Erase && d.Button == 0 && !d.Shift)
        {
            d.Moved = true;
            EraseAt(at, ref erased);
            d.At = at;
            return;
        }
        var dx = at.X - d.At.X;
        var dy = at.Y - d.At.Y;
        if (Math.Abs(dx) + Math.Abs(dy) > 3) d.Moved = true;
        if (aiming)
        {
            if (AimPlanePoint(at) is Vec3 p) MoveAim(p, at);
            return;
        }
        var rotate = (Mode == Mode.Explore && d.Button == 0 && !d.Shift) || (Mode is Mode.Asteroid or Mode.BlackHole && d.Button == 2);
        var pan = d.Button == 1 || (d.Button == 2 && Mode == Mode.Explore) || (d.Button == 0 && d.Shift);
        if (rotate || pan) ClearSiteLock();
        if (rotate) Rig.Orbit(dx, dy);
        else if (pan) Rig.Pan(dx, dy);
        d.At = at;
    }

    void PointerUp(int button, Vector2 at)
    {
        var d = drag;
        if (d == null || d.Button != button) return;
        drag = null;
        if (aiming)
        {
            if (d.Moved) FireManual();
            else CancelAim();
            return;
        }
        if (Mode == Mode.BlackHole && d.Button == 0 && !d.Shift)
        {
            HolePointerUp(at, d.Moved);
            return;
        }
        if (Mode == Mode.Erase && d.Button == 0 && !d.Shift)
        {
            if (erased > 0) Hint($"Erased {erased} {(erased == 1 ? "rock" : "rocks")}", 1.8);
            erased = 0;
            return;
        }
        if (!d.Moved && d.Button == 0)
        {
            var hit = PickBody(at);
            if (Mode == Mode.Asteroid)
            {
                if (hit != null) FireAt(hit);
                return;
            }
            if (hit != null)
            {
                if (lastClick.Id == hit.Id && realTime - lastClick.Time < DoubleClickSeconds) FlyAbove(hit);
                else Select(hit);
                lastClick = (realTime, hit.Id);
            }
        }
    }

    void Wheel(double deltaY, Vector2 at, bool shift)
    {
        if (Mode == Mode.Erase && !shift)
        {
            brushRadius = Math.Clamp(brushRadius * Math.Exp(Math.Clamp(-deltaY, -200, 200) * 0.0015), 8, 400);
            ShowBrush(at);
            return;
        }
        Rig.Zoom(Math.Exp(Math.Clamp(deltaY, -200, 200) * 0.0012));
    }

    /// <summary>The web's keydown handler, by key text (" " is Space, "Escape"); also what the capture action <c>key:X</c> calls.</summary>
    public void PressKey(string k)
    {
        switch (k)
        {
            case " ": TogglePause(); return;
            case "[": SetWarp(Warp / 4); return;
            case "]": SetWarp(Warp * 4); return;
            case "Escape":
                SetMode(Mode.Explore);
                OnEscape();
                return;
        }
        if (k.Length == 1 && char.IsDigit(k[0]))
        {
            var idx = k[0] == '0' ? 9 : k[0] - '1';
            var b = idx < Catalog.PlanetOrder.Count ? Universe.ById(Catalog.PlanetOrder[idx]) : null;
            if (b != null)
            {
                Select(b);
                FlyAbove(b);
            }
            return;
        }
        switch (k.ToLowerInvariant())
        {
            case "r": ToggleRewind(); break;
            case "x": SetMode(Mode == Mode.Asteroid ? Mode.Explore : Mode.Asteroid); break;
            case "z": SetMode(Mode == Mode.Erase ? Mode.Explore : Mode.Erase); break;
            case "b": SetMode(Mode == Mode.BlackHole ? Mode.Explore : Mode.BlackHole); break;
            case "f": if (Selected != null) FlyAbove(Selected); break;
            case "t": ViewTabletop(); break;
            case "h": ToggleHelp(); break;
            case "u": hud.ToggleHistory(); break;
            case "c":
                Settings.Cinematic = !Settings.Cinematic;
                Hint($"Cinematic impact camera {(Settings.Cinematic ? "on" : "off")}");
                break;
        }
    }

    /// <summary>WASD slides the view, Q/E swings it around; shift goes faster.</summary>
    void FlyWithKeys(double dt)
    {
        int Held(Key key) => Input.IsKeyPressed(key) ? 1 : 0;
        var right = Held(Key.D) - Held(Key.A);
        var up = Held(Key.W) - Held(Key.S);
        var spin = Held(Key.Q) - Held(Key.E);
        if (right == 0 && up == 0 && spin == 0) return;
        ClearSiteLock();
        var boost = Input.IsKeyPressed(Key.Shift) ? 3.5 : 1;
        if (right != 0 || up != 0)
        {
            var speed = Rig.Distance * 0.85 * boost * dt;
            Rig.PanBy(right * speed, up * speed);
        }
        if (spin != 0) Rig.RotateBy(spin * 1.5 * boost * dt, 0);
    }

    /// <summary>The nearest body whose disc (or a 14 px target) is under the pointer.</summary>
    Body? PickBody(Vector2 at)
    {
        BodyView? best = null;
        var bestD = double.PositiveInfinity;
        foreach (var view in views.Values)
        {
            if (view.TrueDistance >= bestD) continue;
            if (!labels.Project(view.Position, out var s)) continue;
            if (s.DistanceTo(at) <= Math.Max(view.PixelRadius + 4, 14))
            {
                best = view;
                bestD = view.TrueDistance;
            }
        }
        return best?.Body;
    }

    /// <summary>Where a pointer ray meets the horizontal (ecliptic-parallel) plane through the camera target, km; the hand-aim plane.</summary>
    Vec3? AimPlanePoint(Vector2 at)
    {
        var dir = Rig.RayDir(at);
        var pos = Rig.Position;
        var planeZ = Rig.Target.Z;
        if (Math.Abs(dir.Z) < 1e-9) return null;
        var t = (planeZ - pos.Z) / dir.Z;
        if (!(t > 0)) return null;
        return pos + dir * t;
    }

    // ---------- hooks for the asteroid, erase, cinematic and HUD tasks ----------

    /// <summary>Delete asteroids and fragments under the brush at <paramref name="at"/>, adding the count to <paramref name="count"/>.</summary>
    partial void EraseAt(Vector2 at, ref int count);
    /// <summary>Show the erase brush cursor at the pointer.</summary>
    partial void ShowBrush(Vector2 at);
    /// <summary>Begin a hand aim from a point on the aim plane (km); sets <see cref="aiming"/>.</summary>
    partial void StartAim(Vec3 spawn, Vector2 at);
    /// <summary>The aim drag moved to a new aim-plane point.</summary>
    partial void MoveAim(Vec3 current, Vector2 at);
    /// <summary>Launch the hand-aimed asteroid; clears <see cref="aiming"/>.</summary>
    partial void FireManual();
    /// <summary>Drop the hand aim, path line and markers; clears <see cref="aiming"/>.</summary>
    partial void CancelAim();
    /// <summary>AutoAim at <paramref name="target"/> from the camera and launch.</summary>
    partial void FireAt(Body target);
    /// <summary>Any drag or key flight takes the camera off an impact site.</summary>
    partial void ClearSiteLock();
    /// <summary>Esc also closes overlays and the report card.</summary>
    partial void OnEscape();
    /// <summary>H toggles the help overlay.</summary>
    partial void ToggleHelp();
}
