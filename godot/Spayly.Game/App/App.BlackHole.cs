using System;
using System.Collections.Generic;
using System.Globalization;
using Godot;
using Spayly.Game.Render;
using Spayly.Sim;

namespace Spayly.Game.App;

/// <summary>
/// Black holes: spawning one into the running universe, the lensing pass on the camera's compositor, the switches, and the
/// capture verbs (dispatched from App.Capture.cs): <c>spawnBH:massSolar,nearBodyId[,offsetKm]</c>, <c>lensing:0|1</c>,
/// <c>disc:0|1</c>, <c>doppler:0|1</c>, <c>alignBH:bhId,targetId[,distanceRs]</c> and <c>logBH</c>.
/// </summary>
public partial class App
{
    /// <summary>Lensing, disc and Doppler switches and the disc's look, shared by every black hole's view.</summary>
    public readonly BlackHoleOptions BlackHoles = new();
    /// <summary>Default distance of a new hole from the body it is spawned near, in that body's radii (Mars: 1.7e5 km).</summary>
    public const double SpawnOffsetRadii = 50;

    LensingPass? lensing;
    int blackHoleCount;
    readonly Dictionary<string, string> blackHoleNear = [];
    readonly Dictionary<string, (double Time, Vec3 RelVel)> blackHoleLog = [];

    /// <summary>
    /// Add a black hole of <paramref name="massSolar"/> solar masses near <paramref name="nearBodyId"/>: offset (km, sim frame)
    /// from that body, by default <see cref="SpawnOffsetRadii"/> of its radii towards the Sun; velocity (km/s) relative to it,
    /// by default co-moving. Returns null if the body does not exist.
    /// </summary>
    public Body? SpawnBlackHole(double massSolar, string nearBodyId, Vec3? offsetKm = null, Vec3? velocityKms = null)
    {
        var near = Universe.ById(nearBodyId);
        if (near == null || !(massSolar > 0))
        {
            GD.PushError($"[bh] cannot spawn {massSolar} Msun near '{nearBodyId}'");
            return null;
        }
        var rs = BlackHole.SchwarzschildRadiusKm(massSolar * Constants.SunMass);
        var offset = offsetKm ?? DefaultSpawnDirection(near) * Math.Max(SpawnOffsetRadii * near.Radius, 1000 * rs);
        return SpawnBlackHoleAt(massSolar, near.Pos + offset, near.Vel + (velocityKms ?? Vec3.Zero), near);
    }

    /// <summary>
    /// Add a black hole of <paramref name="massSolar"/> solar masses at <paramref name="posKm"/> (absolute, sim frame) moving
    /// at <paramref name="velKms"/> (absolute). Its radius is its Schwarzschild radius (Body.MakeBody). A position inside a body
    /// is pushed out to 1.5 × contact, which would otherwise be an instant, silent swallow on the first substep.
    /// <paramref name="near"/> is the body logBH reports against (defaults to the one dominating there); <paramref name="undoable"/>
    /// records an undo point first, as a placement from the HUD does.
    /// </summary>
    public Body? SpawnBlackHoleAt(double massSolar, Vec3 posKm, Vec3 velKms, Body? near = null, bool undoable = false)
    {
        if (!(massSolar > 0) || !double.IsFinite(posKm.X + posKm.Y + posKm.Z + velKms.X + velKms.Y + velKms.Z))
        {
            GD.PushError($"[bh] cannot spawn {massSolar} Msun at a non-finite state");
            return null;
        }
        var mass = massSolar * Constants.SunMass;
        var rs = BlackHole.SchwarzschildRadiusKm(mass);
        posKm = ClearOfBodies(posKm, rs);
        near ??= Universe.DominantBody(posKm);
        string id;
        do id = $"bh-{++blackHoleCount}";
        while (Universe.ById(id) != null);
        if (undoable)
        {
            RecordUndo(UndoKind.PlaceHole, $"placing Black hole {blackHoleCount}", id);
            LogState("before-hole");
        }
        var b = Body.MakeBody(new BodySpec
        {
            Id = id,
            Name = $"Black hole {blackHoleCount}",
            Kind = BodyKind.BlackHole,
            Surface = Surface.Star,
            Mass = mass,
            Radius = rs,
            Pos = posKm,
            Vel = velKms,
            Visual = new BodyVisual { Color = "#8a7fb0", Albedo = 0 },
            Born = Universe.Time,
        });
        Universe.Add(b);
        if (near != null) blackHoleNear[id] = near.Id;
        SyncViews();
        var d = near != null ? Vec3.Length(posKm - near.Pos) : double.NaN;
        GD.Print(string.Create(CultureInfo.InvariantCulture,
            $"[bh] spawn {id} mass={massSolar:G4} Msun rs={rs:G5} km near={near?.Id ?? "none"} d={d:G6} km v_rel={(near != null ? Vec3.Length(velKms - near.Vel) : double.NaN):G6} km/s a_near={Constants.G * mass / (d * d):G4} km/s^2 roche_fluid={(near != null ? BlackHole.RocheLimitFluidKm(mass, near.Mass, near.Radius) : double.NaN):G4} km bodies={Universe.Bodies.Count} undoable={undoable}"));
        var holes = 0;
        foreach (var o in Universe.Bodies) if (o.Kind == BodyKind.BlackHole) holes++;
        if (holes > LensingPass.MaxHoles)
            GD.Print($"[bh] {id} is hole {holes}: over {LensingPass.MaxHoles}, drawn without lensing");
        if (holes > LensingPass.MaxHoles)
            Hint($"{b.Name} placed, but only {LensingPass.MaxHoles} black holes bend light at once: this one is drawn without lensing.", 6);
        else
            Hint($"{b.Name}: {massSolar:G3} solar masses, horizon {KmText(2 * rs)} across{(near != null ? $". It will pull {near.Name} in." : ".")}", 5);
        return b;
    }

    static string KmText(double km) => km < 1 ? string.Create(CultureInfo.InvariantCulture, $"{km * 1000:G3} m") : string.Create(CultureInfo.InvariantCulture, $"{km:G3} km");

    Vec3 DefaultSpawnDirection(Body near)
    {
        var sun = Star();
        var toSun = sun.Pos - near.Pos;
        return ReferenceEquals(sun, near) || Vec3.Length(toSun) <= 0 ? new Vec3(1, 0, 0) : Vec3.Normalize(toSun);
    }

    /// <summary>The CreateView branch for <see cref="BodyKind.BlackHole"/>; puts the lensing pass on the camera the first time.</summary>
    BodyView NewBlackHoleView(Body b)
    {
        if (lensing == null)
        {
            lensing = new LensingPass();
            // Appended, so another effect already on the camera keeps running.
            var compositor = camera.Compositor ?? new Compositor();
            var effects = compositor.CompositorEffects;
            effects.Add(lensing);
            compositor.CompositorEffects = effects;
            camera.Compositor = compositor;
        }
        lensing.Enabled = BlackHoles.Lensing;
        return new BlackHoleView(b, lensing, BlackHoles);
    }

    // The camera's Compositor still holds the pass at shutdown, so its predelete notification never comes before the
    // RenderingDevice goes: without this, 12 RIDs leak on every exit.
    public override void _ExitTree()
    {
        lensing?.Release();
        // After the children: a running SphGpu has already freed the uniform sets that use the shared kernels.
        SphGpu.FreeShared();
    }

    public void SetLensing(bool on)
    {
        BlackHoles.Lensing = on;
        if (lensing != null) lensing.Enabled = on;
    }

    public void SetDisc(bool on) => BlackHoles.Disc = on;

    public void SetDoppler(bool on) => BlackHoles.Doppler = on;

    /// <summary>Two holes merged (Collide): log what the waves took and tell the player.</summary>
    void OnHoleMerger(ImpactOutcome o, MergerRecord m)
    {
        var sun = Constants.SunMass;
        GD.Print(string.Create(CultureInfo.InvariantCulture,
            $"[merger] {o.Impactor.Id} into {o.Target.Id}: {m.M1 / sun:G6} + {m.M2 / sun:G6} Msun -> {m.Mf / sun:G6} Msun (M_rem/M={m.Mf / (m.M1 + m.M2):F6}) spin={o.Target.Spin:F6} radiated={m.RadiatedMass / sun:G6} Msun = {m.RadiatedMass * 8.987551787368176e16:G4} J kick={Vec3.Length(m.Kick):G6} km/s eta={GravitationalWaves.SymmetricMassRatio(m.M1, m.M2):F4} rs={o.Target.Radius:G6} km"));
        Hint(string.Create(CultureInfo.InvariantCulture,
            $"{o.Impactor.Name} merged into {o.Target.Name}: {m.RadiatedMass / sun:G3} solar masses went out as gravitational waves, and the remnant recoils at {Vec3.Length(m.Kick):G3} km/s"), 6);
    }

    /// <summary>Black-hole physics Realism entries (M5); the HUD calls this when it builds the Realism panel.</summary>
    public static void RegisterBlackHoleRealism()
    {
        Realism.Register("Pseudo-Newtonian gravity near black holes", RealismTag.Game,
            "Any pair with a black hole attracts by the Paczyński–Wiita (1980) law G M/(r − r_s)² instead of full general relativity: it puts the innermost stable orbit at 3 r_s and the last bound one at 2 r_s, where orbits reach light speed, and is Newton's law far away.",
            RealismArea.Physics);
        Realism.Register("Accretion-fed discs", RealismTag.Sci,
            "Each hole's disc shines with L = ε Ṁ c², ε = 1 − √(8/9) ≈ 5.7 % for a Schwarzschild hole, capped at the Eddington luminosity, where Ṁ is what it is actually swallowing; a hole with nothing falling in is dark apart from its lensing.",
            RealismArea.Rendering);
        Realism.Register("Viscous delay", RealismTag.Game,
            "Swallowed mass reaches the inner edge over the α-disc viscous time (1/α)(r/H)²/Ω at 10 r_s with α = 0.1 and H/r = 0.1 — 4 s for a 10 M☉ hole, 4.6 days at 10⁶ M☉ — modelled as one exponential reservoir.",
            RealismArea.Rendering);
        Realism.Register("Disc display brightness", RealismTag.Art,
            "Brightness is drawn on a log scale, six decades of luminosity from 10⁻⁶ of Eddington (off) to Eddington, and the display temperature follows Ṁ^¼ from 2000 K to the peak colour, so a faint feed still shows.",
            RealismArea.Rendering);
        Realism.Register("Black-hole mergers", RealismTag.Sci,
            "Two holes that touch merge into one lighter by the energy radiated in gravitational waves — 4.85 % of the total for equal masses, from the Healy, Lousto & Zlochower (2014) fit to numerical relativity — and recoiling at up to 175 km/s by González et al. (2007); what the waves take is booked as leaving the system.",
            RealismArea.Collisions);
        Realism.Register("Merger recoil direction", RealismTag.Game,
            "The recoil is put in the orbital plane against the lighter hole's motion at contact; the real direction depends on the orbital phase at merger. Holes are non-spinning, and pairs do not spiral in by radiating: they merge only when their orbits bring them together.",
            RealismArea.Collisions);
        Realism.Register("Capture at 2 r_s", RealismTag.Sci,
            "Anything that crosses twice the Schwarzschild radius, the marginally bound orbit, is swallowed: no orbit that gets that close comes back out. The horizon drawn and shown in the inspector is still r_s.",
            RealismArea.Physics);
    }

    // ---------- capture verbs ----------

    /// <summary><c>spawnBH:massSolar,nearBodyId[,offsetKm]</c>; the offset, if given, is a distance along the default direction. An undo point first.</summary>
    void SpawnBlackHoleAction(string arg)
    {
        var p = arg.Split(',', StringSplitOptions.TrimEntries);
        var mass = double.Parse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture);
        var nearId = p.Length > 1 ? p[1] : "earth";
        RecordUndo(UndoKind.Demo, string.Create(CultureInfo.InvariantCulture, $"spawning a {mass:G3} solar-mass black hole"));
        Vec3? offset = null;
        if (p.Length > 2 && Universe.ById(nearId) is Body near)
            offset = DefaultSpawnDirection(near) * double.Parse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture);
        SpawnBlackHole(mass, nearId, offset);
    }

    /// <summary>
    /// <c>alignBH:bhId,targetId[,distanceRs]</c>: put the camera on the far side of the hole from the target, looking through
    /// the hole at it, <c>distanceRs</c> (default 40) Schwarzschild radii out, diorama off: the target's Einstein ring.
    /// </summary>
    void AlignBlackHoleAction(string arg)
    {
        var p = arg.Split(',', StringSplitOptions.TrimEntries);
        if (p.Length < 2 || Universe.ById(p[0]) is not Body bh || Universe.ById(p[1]) is not Body target)
        {
            GD.PushError($"[bh] alignBH: bad argument '{arg}'");
            return;
        }
        var radii = p.Length > 2 ? double.Parse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture) : 40;
        var w = Vec3.Normalize(bh.Pos - target.Pos);
        Settings.Diorama = false;
        Select(bh);
        Rig.Set(new ViewSpec
        {
            Focus = bh, SetFocus = true, Distance = radii * bh.Radius,
            Yaw = CameraRig.YawTowards(w), Pitch = Math.Asin(Math.Clamp(w.Z, -1, 1)),
        });
    }

    /// <summary>
    /// <c>logBH</c>: per hole, the numbers a capture checks — mass, r_s, the camera's distance in r_s, the expected shadow radius
    /// (angle and pixels at the screen centre), and for the body it was spawned near: distance, relative speed, speed about the
    /// Sun, the Paczyński–Wiita pull GM/(d − r_s)² (and Newton's GM/d²) and the measured |Δv_rel|/Δt since the last logBH.
    /// </summary>
    void LogBlackHoles()
    {
        var c = CultureInfo.InvariantCulture;
        var sun = Star();
        foreach (var v in views.Values)
        {
            if (v is not BlackHoleView view) continue;
            var b = view.Body;
            var rs = b.Radius;
            var camRs = Vec3.Length(Rig.Position - b.Pos) / rs;
            var shadow = view.ShadowAngularRadius;
            var px = shadow < Math.PI / 2 ? Math.Tan(shadow) * ctx.PixelsPerRadian : double.PositiveInfinity;
            var line = string.Create(c,
                $"[bh] {b.Id} mass={b.Mass / Constants.SunMass:G6} Msun rs={rs:G6} km rs_render={view.RsUnits / RenderUnits.Scale:G6} km cam={camRs:G5} rs lensing={(BlackHoles.Lensing ? 1 : 0)} disc={(BlackHoles.Disc ? 1 : 0)} doppler={(BlackHoles.Doppler ? 1 : 0)} shadow_expected={shadow:G5} rad = {px:F1} px (ppr={ctx.PixelsPerRadian:F1}) view_brightness={view.DiscBrightness:G4}");
            if (blackHoleNear.TryGetValue(b.Id, out var nearId) && Universe.ById(nearId) is Body near)
            {
                var rel = near.Pos - b.Pos;
                var d = Vec3.Length(rel);
                var relVel = near.Vel - b.Vel;
                var measured = "n/a";
                if (blackHoleLog.TryGetValue(b.Id, out var last) && Universe.Time - last.Time > 0)
                    measured = string.Create(c, $"{Vec3.Length(relVel - last.RelVel) / (Universe.Time - last.Time):G5} km/s^2 over {Universe.Time - last.Time:G4} s");
                blackHoleLog[b.Id] = (Universe.Time, relVel);
                line += string.Create(c,
                    $" near={near.Id} d={d:G6} km v_rel={Vec3.Length(relVel):G6} km/s v_sun={Vec3.Length(near.Vel - sun.Vel):G6} km/s a_expected={PseudoNewtonian.Acceleration(Constants.G * b.Mass, d, PseudoNewtonian.HoleRs(b) + PseudoNewtonian.HoleRs(near)):G5} km/s^2 (PW; Newton {Constants.G * b.Mass / (d * d):G5}) dv/dt={measured}");
            }
            else if (blackHoleNear.ContainsKey(b.Id)) line += " near=swallowed-or-gone";
            line += HoleOrbitText(b.Id);
            line += DiscLogText(b);
            GD.Print(line);
        }
    }
}
