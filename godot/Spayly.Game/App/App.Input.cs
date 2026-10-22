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
                // Any press or wheel notch that reaches the view ends a skip; HUD buttons that act on time or focus do it themselves.
                if (mb.Pressed) CancelSkip();
                if (mb.ButtonIndex is MouseButton.WheelUp or MouseButton.WheelDown)
                {
                    if (!mb.Pressed) break;
                    // A browser wheel notch is deltaY ≈ ±100; Godot reports notches (factor scales trackpad deltas).
                    var dy = (mb.ButtonIndex == MouseButton.WheelDown ? 100 : -100) * (mb.Factor > 0 ? mb.Factor : 1);
                    Wheel(dy, mb.Position, mb.ShiftPressed, Time.GetTicksMsec() / 1000.0);
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
                if (k.Keycode is not (Key.Shift or Key.Ctrl or Key.Alt or Key.Meta) && (k.CtrlPressed || k.AltPressed || k.MetaPressed)) CancelSkip();
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
        if (Mode == Mode.KeepInside && button == 0 && !shift)
        {
            KeepPointerDown(at);
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
        if (Mode == Mode.KeepInside) KeepPointerMove(at, drag is { Button: 0, Shift: false });
        forecastSuppressed = false;
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
        if (Mode == Mode.KeepInside && d.Button == 0 && !d.Shift)
        {
            d.Moved = true;
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
        var rotate = (Mode == Mode.Explore && d.Button == 0 && !d.Shift) || (Mode is Mode.Asteroid or Mode.BlackHole or Mode.KeepInside && d.Button == 2);
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
        if (Mode == Mode.KeepInside && d.Button == 0 && !d.Shift)
        {
            KeepPointerUp();
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

    /// <summary>Log-space size of one unhurried wheel notch (×1.127), as the web build had it.</summary>
    const double WheelStep = 0.12;
    /// <summary>Largest log step per notch once a scroll is running: ×3.7, so the 13 e-folds from a planet to 50 AU take ~13 notches.</summary>
    const double WheelStepMax = 1.3;
    /// <summary>Shift+wheel: a fixed ×3 per notch, unaccelerated.</summary>
    const double WheelStepCoarse = 1.1;
    /// <summary>Wheel events closer together than this (s) belong to one scroll and ramp up; a longer pause starts over at <see cref="WheelStep"/>.</summary>
    const double WheelBurstGap = 0.15;
    /// <summary>Per-notch growth of the step inside a scroll. Applied per notch-equivalent of travel, so a trackpad's many small events ramp like the same distance of wheel notches.</summary>
    const double WheelRamp = 1.7;

    double wheelLastTime = double.NegativeInfinity;
    double wheelRamp = 1;
    int wheelDir;

    /// <summary>
    /// One wheel event. <paramref name="deltaY"/> is a browser-style delta (±100 per notch, scaled by the event's factor);
    /// <paramref name="now"/> is its time in s, passed in so a script can space events without waiting for them.
    /// </summary>
    void Wheel(double deltaY, Vector2 at, bool shift, double now)
    {
        if (Mode == Mode.Erase && !shift)
        {
            brushRadius = Math.Clamp(brushRadius * Math.Exp(Math.Clamp(-deltaY, -200, 200) * 0.0015), 8, 400);
            ShowBrush(at);
            return;
        }
        if (Mode == Mode.KeepInside && !shift && !keepConfirming)
        {
            KeepWheel(deltaY);
            return;
        }
        // Notch-equivalents: 1 for a wheel notch, a fraction for one trackpad / hi-res-wheel event.
        var notches = Math.Min(Math.Abs(deltaY) / 100, 2);
        var dir = Math.Sign(deltaY);
        if (dir == 0) return;
        // The step grows in log space, so a hurried scroll covers the same number of e-folds per second at 10 km as at 10 AU.
        if (now - wheelLastTime > WheelBurstGap || dir != wheelDir) wheelRamp = 1;
        else wheelRamp = Math.Min(wheelRamp * Math.Pow(WheelRamp, notches), WheelStepMax / WheelStep);
        wheelLastTime = now;
        wheelDir = dir;
        var step = shift ? WheelStepCoarse * Math.Min(notches, 1) : WheelStep * wheelRamp * notches;
        Rig.Zoom(Math.Exp(dir * step));
    }

    /// <summary>The web's keydown handler, by key text (" " is Space, "Escape"); also what the capture action <c>key:X</c> calls.</summary>
    public void PressKey(string k)
    {
        // N toggles the skip itself; a bare modifier (shift speeds up WASD) is not an input that changes anything.
        if (!k.Equals("n", StringComparison.OrdinalIgnoreCase) && k is not ("Shift" or "Ctrl" or "Alt" or "Meta")) CancelSkip();
        switch (k)
        {
            case "\r" or "\n" or "Enter" or "Return" or "KpEnter":
                if (keepConfirming) ConfirmKeep();
                else if (Mode == Mode.KeepInside) RequestKeepApply();
                return;
            case " ": TogglePause(); return;
            case "[": SetWarp(Warp / 4); return;
            case "]": SetWarp(Warp * 4); return;
            case "Escape":
                // With the prompt open Esc closes only the prompt; the sphere stays to be adjusted.
                if (keepConfirming) { CancelKeepConfirm(); return; }
                // Cancels a skip, aim and open panels first (above and in SetMode/OnEscape); what is left is the camera.
                SetMode(Mode.Explore);
                OnEscape();
                ClearSiteLock();
                Rig.Freeze();
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
            case "k": SetMode(Mode == Mode.KeepInside ? Mode.Explore : Mode.KeepInside); break;
            case "b": SetMode(Mode == Mode.BlackHole ? Mode.Explore : Mode.BlackHole); break;
            case "f": if (Selected != null) FlyAbove(Selected); break;
            case "t": ViewTabletop(); break;
            case "h": ToggleHelp(); break;
            case "u": hud.ToggleHistory(); break;
            case "m": ToggleBottomBar(); break;
            case "n":
                if (skipping) CancelSkip();
                else StartSkip();
                break;
            case "c":
                SetDirector(Settings.Director switch { DirectorMode.Off => DirectorMode.Notable, DirectorMode.Notable => DirectorMode.Major, _ => DirectorMode.Off }, true);
                break;
        }
    }

    /// <summary>WASD slides the view, Q/E swings it around; shift goes faster.</summary>
    void FlyWithKeys(double dt) => FlyWithKeys(dt, k => Input.IsKeyPressed(k), Input.IsKeyPressed(Key.Shift));

    /// <summary>Seconds for the pan and spin to reach ~63 % of full speed after a key goes down.</summary>
    const double KeyAccelTime = 0.14;
    /// <summary>Seconds for them to shed ~63 % of their speed after release; longer than the attack, so the view glides to rest.</summary>
    const double KeyGlideTime = 0.28;

    /// <summary>Held-key velocity, in pan scales per second (right, up) and rad/s (spin); the eased value, not the key state.</summary>
    double keyRight, keyUp, keySpin;

    /// <summary>The key handling with the key state passed in, so a capture script can drive it.</summary>
    void FlyWithKeys(double dt, Func<Key, bool> down, bool shift)
    {
        int Held(Key key) => down(key) ? 1 : 0;
        double right = Held(Key.D) - Held(Key.A);
        double up = Held(Key.W) - Held(Key.S);
        var spin = Held(Key.Q) - Held(Key.E);
        var boost = shift ? 3.5 : 1;
        // A diagonal is as fast as a straight line, not 1.41×.
        var len = Math.Sqrt(right * right + up * up);
        if (len > 0) { right /= len; up /= len; }
        // Ease the velocity, not the position: the view speeds up and glides to a stop, and a zoom in the middle of a glide
        // keeps it relative because the velocity is in pan scales, not km.
        double Ease(double v, double target)
        {
            var tau = target == 0 || Math.Abs(target) < Math.Abs(v) ? KeyGlideTime : KeyAccelTime;
            return v + (target - v) * (1 - Math.Exp(-dt / tau));
        }
        keyRight = Ease(keyRight, right * 0.85 * boost);
        keyUp = Ease(keyUp, up * 0.85 * boost);
        keySpin = Ease(keySpin, spin * 1.5 * boost);
        if (Math.Abs(keyRight) < 1e-4) keyRight = 0;
        if (Math.Abs(keyUp) < 1e-4) keyUp = 0;
        if (Math.Abs(keySpin) < 1e-4) keySpin = 0;
        if (keyRight == 0 && keyUp == 0 && keySpin == 0) return;
        ClearSiteLock();
        if (keyRight != 0 || keyUp != 0)
        {
            var scale = Rig.PanScaleKm * dt;
            Rig.PanBy(keyRight * scale, keyUp * scale);
        }
        if (keySpin != 0) Rig.RotateBy(keySpin * dt, 0);
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

    // ---------- hooks for the asteroid, erase and HUD tasks ----------

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
