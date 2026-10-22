using System;
using System.Globalization;
using Godot;
using Spayly.Game.Render;
using Spayly.Game.Sim;
using Spayly.Sim;

namespace Spayly.Game.App;

/// <summary>
/// Scripted actions for <c>CaptureDriver --actions</c>: the Godot counterpart of the web's <c>window.spayly</c> contract, so
/// parity screenshots are scriptable. Verbs are the public API's names in camelCase; the argument follows a colon.
///
/// The probe (<c>probe:ID</c>) fixes a surface point of a body in its own rotating frame and reports its projected
/// position each frame — the spike M1c jitter method applied to the real app.
/// </summary>
public partial class App
{
    Body? probeBody;
    Vec3 probeDirBody;
    bool probeValid;
    double probeX, probeY;

    public bool RunAction(string verb, string arg)
    {
        double Num() => double.Parse(arg, NumberStyles.Float, CultureInfo.InvariantCulture);
        bool On() => arg is "1" or "true" or "on" or "";
        switch (verb)
        {
            case "viewTabletop": ViewTabletop(); return true;
            case "viewInner": ViewInner(); return true;
            case "viewOuter": ViewOuter(); return true;
            case "select": Select(arg); return true;
            case "follow": Follow(arg); return true;
            case "flyAbove": FlyAbove(arg); return true;
            case "setWarp": SetWarp(Num()); return true;
            case "logWarp": GD.Print(string.Create(CultureInfo.InvariantCulture, $"[warp] {arg} warp={Warp:G4} paused={Paused}")); return true;
            case "togglePause": TogglePause(); return true;
            case "toggleRewind": ToggleRewind(); return true;
            case "setMode": SetMode(Enum.Parse<Mode>(arg, true)); return true;
            case "setDiorama": SetDiorama(On()); return true;
            case "setMoons": SetMoonDetail(Enum.Parse<MoonDetail>(arg, true)); return true;
            case "setFalloff": Settings.Falloff = Num(); return true;
            case "setLabels": Settings.Labels = On(); return true;
            case "setOrbits": Settings.Orbits = On(); return true;
            case "resetBody": ResetBody(arg); return true;
            case "resetScene": ResetScene(); return true;
            case "undoImpact": UndoImpact(); return true;
            // Undo and redo (App.Undo.cs); logFullState[:tag] hashes the bodies and everything an undo point restores besides them.
            case "undo": Undo(); return true;
            case "redo": Redo(); return true;
            case "logFullState": LogFullState(arg.Length > 0 ? arg : "now"); return true;
            // The history panel (UI/Hud.History.cs): open or close it, print its rows, press the row N points back (negative: redo).
            case "history": hud.ToggleHistory(On()); return true;
            case "logHistory": GD.Print($"[history] {hud.HistorySummary()}"); return true;
            case "historyJump": hud.PressHistoryRow(int.Parse(arg, CultureInfo.InvariantCulture)); return true;
            case "lookAtLastImpact": LookAtLastImpact(); return true;
            // A chord (key:ctrl+z, key:ctrl+shift+z, key:ctrl+y) goes through the real input path as an InputEventKey.
            case "key":
                if (arg.Contains('+') && arg.Length > 1) SendKeyChord(arg);
                else PressKey(arg == "space" ? " " : arg == "esc" ? "Escape" : arg);
                return true;
            // Multiplicative zoom as a wheel would do it; `zoom:1e-9` lands on the surface stop.
            case "zoom": Rig.Zoom(Num()); return true;
            case "probe": ArmProbe(arg); return true;
            case "probeOff": probeBody = null; probeValid = false; return true;
            case "logLabels": LogLabels(); return true;
            // Swing the camera, radians "dYaw,dPitch" (what a drag or Q/E does).
            case "rotate":
            {
                var p = arg.Split(',');
                Rig.RotateBy(double.Parse(p[0], CultureInfo.InvariantCulture), p.Length > 1 ? double.Parse(p[1], CultureInfo.InvariantCulture) : 0);
                return true;
            }
            case "setSqueeze": ctx.Origin.SqueezeEnabled = On(); return true;
            // Asteroid, erase and impact verbs live with their code in App.Impacts.cs.
            case "fireAt" or "setAsteroid" or "applyPreset" or "erase" or "aimStart" or "aimMove" or "aimFire" or "logImpact" or "logMarkers" or "speedAtMarker": return ImpactAction(verb, arg);
            // Meteor showers (App.Shower.cs): shower:N,target fires one; showerMode:1|0[,N] sets the panel's option; logShower.
            case "shower" or "showerMode" or "logShower": return ShowerAction(verb, arg);
            case "setOutcomeModel" or "logDebris" or "debrisVisible": return DebrisAction(verb, arg);
            // Black-hole verbs live with their code in App.BlackHole.cs.
            case "spawnBH": SpawnBlackHoleAction(arg); return true;
            case "lensing": SetLensing(On()); return true;
            case "disc": SetDisc(On()); return true;
            case "doppler": SetDoppler(On()); return true;
            case "alignBH": AlignBlackHoleAction(arg); return true;
            case "logBH": LogBlackHoles(); return true;
            case "holePair" or "logPair": return InspiralAction(verb, arg);
            // Black-hole mode verbs live with their code in App.HoleMode.cs.
            case "feedBH" or "logDiscs": return DiscAction(verb, arg);
            case "bhMass" or "bhVelocity" or "placeBH" or "bhDrag" or "removeBH" or "cameraAt" or "logHolePanel" or "logState": return HoleAction(verb, arg);
            // Hero-collision SPH verbs live with their code in App.Sph.cs: sph:1|0, sphDemo:earth,theia, sphN, sphSubsteps, sphWall, sphParity:K, logSph.
            case "sph" or "setSph" or "sphDemo" or "sphN" or "sphSubsteps" or "sphWall" or "sphParity" or "logSph": return SphAction(verb, arg);
            // Tidal-disruption verbs live with their code in App.Tidal.cs.
            case "tidal" or "tidalDemo" or "tdeDemo" or "logTidal": return TidalAction(verb, arg);
            case "hudSelftest": HudSelftest(); return true;
            case "toggleHelp": ToggleHelp(); return true;
            case "showReportDemo": ShowReportDemo(); return true;
            case "hudVisible": hud.Visible = On(); return true;
            // The Realism panel (UI/Hud.Realism.cs): open or close it, check the registry, rest the pointer on a View control.
            case "realism": hud.ToggleRealism(On()); return true;
            case "realismSelftest": RealismSelftest(); return true;
            case "realismHover": return hud.HoverRealismControl(arg);
            // A left press and release at viewport px "x,y", through the GUI like a real click (rows, labels, panels).
            case "click": ClickAt(arg); return true;
            case "logSelection": GD.Print($"[capture] selected={Selected?.Id ?? "none"} focus={Rig.Focus?.Id ?? "none"} distance={Rig.Distance:G4}"); return true;
            // The inspector's text after its 8 Hz refresh, and a mass-slider drag (log10 of the multiple of the original mass).
            case "logInspector": GD.Print($"[inspector] {hud.InspectorSummary()}"); return true;
            case "setMassSlider": hud.SetMassSlider(Num()); return true;
            case "setRadiusSlider": hud.SetRadiusSlider(Num()); return true;
            default: return false;
        }
    }

    /// <summary>A left press and release at viewport px "x,y", through the GUI like a real click.</summary>
    void ClickAt(string arg)
    {
        var p = arg.Split(',');
        var at = new Vector2(float.Parse(p[0], CultureInfo.InvariantCulture), float.Parse(p[1], CultureInfo.InvariantCulture));
        Input.WarpMouse(at);
        foreach (var down in new[] { true, false })
            Input.ParseInputEvent(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = down, Position = at, GlobalPosition = at, ButtonMask = down ? MouseButtonMask.Left : 0 });
    }

    /// <summary>
    /// A key chord such as "ctrl+shift+z", pressed and released through <c>Input.ParseInputEvent</c> like a real keyboard, so
    /// it takes the same path (GUI first, then App._UnhandledInput) a player's key does.
    /// </summary>
    void SendKeyChord(string chord)
    {
        var parts = chord.ToLowerInvariant().Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var name = parts.Length > 0 ? parts[^1] : "";
        var key = name.Length == 1 && char.IsAsciiLetterOrDigit(name[0]) ? (Key)char.ToUpperInvariant(name[0]) : Key.None;
        if (key == Key.None)
        {
            GD.PushError($"[capture] key chord '{chord}': no key");
            return;
        }
        var ctrl = Array.IndexOf(parts, "ctrl") >= 0;
        var shift = Array.IndexOf(parts, "shift") >= 0;
        var alt = Array.IndexOf(parts, "alt") >= 0;
        GD.Print($"[capture] key chord {chord}: {(ctrl ? "Ctrl+" : "")}{(shift ? "Shift+" : "")}{(alt ? "Alt+" : "")}{key}");
        foreach (var down in new[] { true, false })
            Input.ParseInputEvent(new InputEventKey { Keycode = key, PhysicalKeycode = key, Pressed = down, CtrlPressed = ctrl, ShiftPressed = shift, AltPressed = alt });
    }

    /// <summary>Realism.SelfTest, printed as "[realism] selftest N/M PASS|FAIL", one line per check, the counts, then the panel.</summary>
    void RealismSelftest()
    {
        var t = Realism.SelfTest();
        GD.Print($"[realism] selftest {t.Passed}/{t.Total} {(t.Passed == t.Total ? "PASS" : "FAIL")}");
        foreach (var line in t.Lines) GD.Print("[realism] " + line);
        GD.Print($"[realism] panel {hud.RealismSummary()}");
    }

    /// <summary>Print which labels survived occlusion and declutter this frame, for scripted checks.</summary>
    void LogLabels()
    {
        var names = new System.Collections.Generic.List<string>();
        var onScreen = 0;
        var screen = GetViewport().GetVisibleRect();
        foreach (var v in views.Values)
        {
            if (labels.For(v.Body.Id) is not { Visible: true } l) continue;
            var inside = screen.Intersects(new Rect2(l.Position, l.Size));
            if (inside) onScreen++;
            // The box centre, so a script can click the label.
            var c = l.Position + l.Size / 2;
            names.Add(inside ? $"{v.Body.Name}@{c.X:F0},{c.Y:F0}" : v.Body.Name + "(off-screen)");
        }
        GD.Print($"[labels] visible={names.Count} on_screen={onScreen}: {string.Join(", ", names)}");
    }

    public bool TryProbe(out double x, out double y)
    {
        x = probeX;
        y = probeY;
        return probeValid;
    }

    /// <summary>
    /// Fix a surface point of <paramref name="id"/> near the sub-camera point in the body's frame, so it rides the body's
    /// rotation; off-centre so a pure-rotation error cannot hide at the optical axis.
    /// </summary>
    void ArmProbe(string id)
    {
        probeBody = Universe.ById(id);
        probeValid = false;
        if (probeBody == null) return;
        var rel = Rig.Position - probeBody.Pos;
        var toCam = Vec3.Normalize(rel);
        var side = Vec3.Normalize(Vec3.Cross(toCam, new Vec3(0, 0, 1)));
        // Arc that puts the point ~15° off the view axis from the current altitude, so it lands on screen at any zoom.
        var radiusKm = ctx.SolidRadius(probeBody) / RenderUnits.Scale;
        var altitude = Math.Max(Vec3.Length(rel) - radiusKm, 1e-6);
        var a = Math.Min(altitude * Math.Tan(15 * Constants.Deg) / radiusKm, 3 * Constants.Deg);
        var dir = Vec3.Normalize(toCam * Math.Cos(a) + side * Math.Sin(a));
        probeDirBody = Render.Orientation.WorldToBodyFrame(probeBody, dir, Universe.Jd);
    }

    void UpdateProbe()
    {
        var b = probeBody;
        if (b == null) return;
        if (Universe.ById(b.Id) != b)
        {
            probeBody = null;
            probeValid = false;
            return;
        }
        var dirWorld = Render.Orientation.BodyFrameToWorld(b, probeDirBody, Universe.Jd);
        var point = b.Pos + dirWorld * (ctx.SolidRadius(b) / RenderUnits.Scale);
        var render = ctx.Origin.Place(point, out _, out _);
        probeValid = labels.Project(render, out var s);
        probeX = s.X;
        probeY = s.Y;
    }
}
