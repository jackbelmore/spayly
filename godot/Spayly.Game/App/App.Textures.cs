using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using Godot;
using Spayly.Game.Render;
using Spayly.Sim;

namespace Spayly.Game.App;

/// <summary>
/// Texture policy on top of engine streaming (spike M1b), replacing the web's MapStream/sweep/paced-upload machinery:
///   - the View panel's ceiling (Settings.Textures) caps every streamed map at the first mip level within it (per-map
///     <c>MinLodOverride</c>, TextureLibrary.SetCeiling) and sets the streaming memory budget, which is what lets maps the
///     camera has left fall back (a visible map never drops without budget pressure);
///   - focus-first: when the camera's focus changes, its surface map is forced to min(file width, ceiling) with
///     <c>MaxLodOverride</c> + <c>FlushTextureStreaming()</c>, but only if the resident width is below that (re-setting an
///     override on a resident map reloads it synchronously, ~55 ms), and the previous focus's override is cleared.
/// Harness-only user args: <c>--texture-ceiling N</c> (start with that ceiling), <c>--no-focus-first</c>,
/// <c>--tex-probe gminlod:N|gmaxlod:N</c> (set the global TextureStreaming override, to measure what it does).
/// </summary>
public partial class App
{
    string? texFocusId;
    bool texFocusPending;
    int texFocusTries, texFocusWaits;
    long texFocusRetryAt;
    const long TexFirstForceFrame = 10;
    StreamedTexture2D? texForced;
    string? texForcedId;
    int texCeilingApplied = -1;
    long texFrame;
    bool texArgsRead, texFocusFirst = true;
    long texFlushFrame = -1;

    /// <summary>Frames at which the streaming state is logged for the capture report.</summary>
    static readonly long[] TexLogFrames = [30, 120, 240, 400, 560, 700, 850];

    void ReadTextureArgs()
    {
        texArgsRead = true;
        var args = OS.GetCmdlineUserArgs();
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--no-focus-first": texFocusFirst = false; break;
                case "--texture-ceiling" when i + 1 < args.Length:
                    Settings.Textures = int.Parse(args[++i], CultureInfo.InvariantCulture);
                    break;
                case "--damage-preview" when i + 1 < args.Length:
                {
                    var p = args[++i].Split(':');
                    texPreviewId = p[0];
                    texPreviewFrame = long.Parse(p[1], CultureInfo.InvariantCulture);
                    break;
                }
                case "--capture-dir" when i + 1 < args.Length: texCaptureDir = args[++i]; break;
                case "--tex-probe" when i + 1 < args.Length:
                {
                    var p = args[++i].Split(':');
                    var n = int.Parse(p[1], CultureInfo.InvariantCulture);
                    if (p[0] == "gminlod") TextureStreaming.MinLodOverride = n;
                    else if (p[0] == "gmaxlod") TextureStreaming.MaxLodOverride = n;
                    GD.Print($"[tex] probe {p[0]}={n}: TextureStreaming min_lod_override={TextureStreaming.MinLodOverride} max_lod_override={TextureStreaming.MaxLodOverride}");
                    break;
                }
            }
        }
        TextureStreaming.FlushCompleted += () =>
        {
            texFlushFrame = texFrame;
            LogStreaming("flush_completed");
        };
    }

    partial void PumpTextureUploads()
    {
        texFrame++;
        if (!texArgsRead) ReadTextureArgs();
        if (Settings.Textures != texCeilingApplied) ApplyTextureCeiling();
        if (ctx.FocusId != texFocusId)
        {
            // The camera moving to a hero collision's proxy, or back to the body whose map is forced, keeps the override:
            // clearing it costs 18 ms on Earth's 16K map, and that body's view returns (App.Views.cs parkedViews).
            var keep = ctx.FocusId == texForcedId || ViewParked(texForcedId);
            if (!keep) ReleaseForcedTexture();
            texFocusId = ctx.FocusId;
            texFocusPending = texFocusFirst && texFocusId != null && texFocusId != texForcedId;
            texFocusTries = texFocusWaits = 0;
            texFocusRetryAt = 0;
        }
        if (texFocusPending) ForceFocusTexture();
        foreach (var f in TexLogFrames)
            if (f == texFrame) LogStreaming("frame");
        if (texFrame == texPreviewFrame) DamagePreview();
    }

    string? texPreviewId, texCaptureDir;
    long texPreviewFrame = -1;

    /// <summary>
    /// Harness check of the crater painter and the heat/magma uniforms without an impact: a crater field on a copy of the
    /// body's damage (the sim's own is untouched) centred under the camera, shown through PlanetView.PreviewDamage, and the
    /// painted overlay saved as damage-ID.png next to the shots.
    /// </summary>
    void DamagePreview()
    {
        if (texPreviewId == null || Universe.ById(texPreviewId) is not { } b || views.GetValueOrDefault(b.Id) is not PlanetView pv) return;
        var dmg = b.Damage.Clone();
        var toCam = Vec3.Normalize(Rig.Position - b.Pos);
        var side = Vec3.Normalize(Vec3.Cross(toCam, new Vec3(0, 0, 1)));
        var up = Vec3.Cross(side, toCam);
        var rand = Rng.Mulberry32(7);
        // Diameters 20–800 km scattered over ±35° around the sub-camera point, plus one big basin in the middle.
        for (var k = 0; k < 24; k++)
        {
            var a = (rand() * 2 - 1) * 0.6;
            var e = (rand() * 2 - 1) * 0.6;
            var dir = Vec3.Normalize(toCam + side * Math.Tan(a) + up * Math.Tan(e));
            dmg.Craters.Add(new Crater(Render.Orientation.WorldToBodyFrame(b, dir, Universe.Jd), 20 + rand() * rand() * 780, Universe.Time, 1e20));
        }
        dmg.Craters.Add(new Crater(Render.Orientation.WorldToBodyFrame(b, toCam, Universe.Jd), 900, Universe.Time, 1e24));
        var hotDir = Vec3.Normalize(toCam - side * 0.35 + up * 0.25);
        dmg.Heat.Add(new HeatSpot(Render.Orientation.WorldToBodyFrame(b, hotDir, Universe.Jd), 0.08, Universe.Time, 1e9));
        dmg.Version++;
        pv.PreviewDamage(dmg);
        // Painted on the view's next Update; save it one frame later.
        GetTree().CreateTimer(0.2).Timeout += () =>
        {
            if (pv.DamageImage is { } img && texCaptureDir != null)
            {
                var path = System.IO.Path.Combine(texCaptureDir, $"damage-{b.Id}.png");
                GD.Print($"[tex] damage preview {b.Id}: {dmg.Craters.Count} craters, 1 heat spot, overlay {img.GetWidth()}x{img.GetHeight()} -> {path} {img.SavePng(path)}");
            }
        };
    }

    /// <summary>Every 4 s. The engine streams and evicts on its own; this only records what the budget holds.</summary>
    partial void SweepTextures() => GD.Print(string.Create(CultureInfo.InvariantCulture,
        $"[tex] sweep frame={texFrame} budget_used_mb={TextureStreaming.GetMemoryBudgetBytesUsed() / 1e6:F1} budget_mb={TextureStreaming.MemoryBudgetMbOverride}"));

    partial void OnViewCreated(Body b, BodyView v)
    {
        // A rebuilt view (undo, fault rollback) of the focus body holds a fresh texture reference: force it again. A view
        // back from parking still has its forced map.
        if (b.Id == texFocusId && texFocusFirst && !(v is IStreamedView sv && texForced != null && ReferenceEquals(sv.AlbedoMap, texForced)))
        {
            texFocusPending = true;
            texFocusTries = texFocusWaits = 0;
            texFocusRetryAt = 0;
        }
    }

    partial void OnViewRemoved(string id, BodyView v)
    {
        if (id == texForcedId) ReleaseForcedTexture();
    }

    void ApplyTextureCeiling()
    {
        texCeilingApplied = Settings.Textures;
        TextureStreaming.MemoryBudgetMbOverride = TextureLibrary.BudgetMbFor(texCeilingApplied);
        var sw = Stopwatch.StartNew();
        var recapped = TextureLibrary.SetCeiling(texCeilingApplied);
        GD.Print(string.Create(CultureInfo.InvariantCulture,
            $"[tex] ceiling={texCeilingApplied} memory_budget_mb={TextureStreaming.MemoryBudgetMbOverride} recapped={recapped} maps in {sw.Elapsed.TotalMilliseconds:F1} ms"));
        if (texFocusId != null && texFocusFirst)
        {
            ReleaseForcedTexture();
            texFocusPending = true;
            texFocusTries = texFocusWaits = 0;
            texFocusRetryAt = 0;
        }
    }

    /// <summary>
    /// Coarsest-level override for the focus map: 1 + the level whose width fits the ceiling (1 = LOD 0 = the whole file).
    /// The texture's override counts from 1 because 0 means "use the global setting" (spike M1b). With the ceiling cap
    /// (TextureLibrary's MinLodOverride) at the same level, the focus map is pinned exactly there.
    /// </summary>
    static int OverrideFor(int fileWidth, int ceiling, out int targetWidth) => TextureLibrary.LodFor(fileWidth, ceiling, out targetWidth) + 1;

    void ForceFocusTexture()
    {
        if (texFocusId == null || !views.TryGetValue(texFocusId, out var view) || view is not IStreamedView sv || sv.AlbedoMap is not StreamedTexture2D tex)
        {
            texFocusPending = false;
            return;
        }
        // An override set before streaming has seen the map drawn is flushed with nothing queued and then never acts (first
        // run: Earth, forced on frame 1, stayed at 2048). Wait out the first frames, and re-check after each attempt.
        if (texFrame < TexFirstForceFrame || texFrame < texFocusRetryAt) return;
        var resident = TextureLibrary.ResidentWidth(tex);
        if (resident <= 0)
        {
            if (++texFocusWaits > 30) texFocusPending = false;
            return;
        }
        var ov = OverrideFor(sv.AlbedoWidth, Settings.Textures, out var target);
        // A map kept forced while its view was parked gives way to the new focus's.
        if (texForced != null && !ReferenceEquals(texForced, tex)) ReleaseForcedTexture();
        texForced = tex;
        texForcedId = texFocusId;
        if (resident >= target)
        {
            texFocusPending = false;
            GD.Print($"[tex] focus {texFocusId}: resident {resident} >= target {target}, done (frame {texFrame}, attempts {texFocusTries})");
            return;
        }
        if (++texFocusTries > 3)
        {
            texFocusPending = false;
            GD.Print($"[tex] focus {texFocusId}: still {resident} < {target} after 3 attempts; left to feedback streaming");
            return;
        }
        // Checked again in 15 frames; a repeat attempt clears the override first so the set is a change.
        texFocusRetryAt = texFrame + 15;
        if (tex.MaxLodOverride == ov) tex.MaxLodOverride = 0;
        var sw = Stopwatch.StartNew();
        tex.MaxLodOverride = ov;
        var setMs = sw.Elapsed.TotalMilliseconds;
        sw.Restart();
        TextureStreaming.FlushTextureStreaming();
        GD.Print(string.Create(CultureInfo.InvariantCulture,
            $"[tex] focus {texFocusId}: resident {resident} < target {target} (file {sv.AlbedoWidth}): max_lod_override={ov} set {setMs:F2} ms, flush call {sw.Elapsed.TotalMilliseconds:F2} ms (frame {texFrame})"));
    }

    void ReleaseForcedTexture()
    {
        var tex = texForced;
        texForced = null;
        var id = texForcedId;
        texForcedId = null;
        if (tex == null || tex.MaxLodOverride == 0) return;
        var sw = Stopwatch.StartNew();
        tex.MaxLodOverride = 0;
        GD.Print(string.Create(CultureInfo.InvariantCulture, $"[tex] focus left {id}: override cleared in {sw.Elapsed.TotalMilliseconds:F2} ms (frame {texFrame})"));
    }

    void LogStreaming(string why)
    {
        var earth = Universe.ById("earth") is { } e && views.TryGetValue(e.Id, out var ev) && ev is IStreamedView es ? es.AlbedoMap : null;
        var focus = texFocusId != null && views.TryGetValue(texFocusId, out var fv) && fv is IStreamedView fs ? fs.AlbedoMap : null;
        if (TextureLibrary.EntryFor("earth") is { } ee)
        {
            // Every Earth map, not just the forced albedo: the others stream on feedback alone.
            long W(string? f) => f != null && TextureLibrary.LoadFile(f) is { } t ? TextureLibrary.ResidentWidth(t) : -1;
            GD.Print($"[tex] {why} frame={texFrame} earth_maps day={W(ee.Albedo)} night={W(ee.Night)} clouds={W(ee.Clouds)} normal={W(ee.Normal)} specular={W(ee.Specular)}");
        }
        GD.Print(string.Create(CultureInfo.InvariantCulture,
            $"[tex] {why} frame={texFrame} earth_resident={(earth != null ? TextureLibrary.ResidentWidth(earth) : -1)} focus={texFocusId} focus_resident={(focus != null ? TextureLibrary.ResidentWidth(focus) : -1)} budget_used_mb={TextureStreaming.GetMemoryBudgetBytesUsed() / 1e6:F1} tex_mem_mb={Performance.GetMonitor(Performance.Monitor.RenderTextureMemUsed) / 1e6:F1} nvidia_used_mb={NvidiaUsedMb()} last_flush_frame={texFlushFrame}"));
    }

    static string NvidiaUsedMb()
    {
        try
        {
            var psi = new ProcessStartInfo("nvidia-smi", "--query-gpu=memory.used --format=csv,noheader,nounits") { RedirectStandardOutput = true, UseShellExecute = false };
            using var p = Process.Start(psi)!;
            var s = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit();
            return s;
        }
        catch (Exception ex) { return "err:" + ex.GetType().Name; }
    }
}
