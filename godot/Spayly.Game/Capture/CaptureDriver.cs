using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using Godot;

namespace Spayly.Game.Capture;

/// <summary>
/// What a scene exposes to scripted captures: the Godot counterpart of the web build's <c>window.spayly</c>, which
/// scripts/shoot.mjs drives. The driver knows only this interface, so it stays scene-agnostic.
/// </summary>
public interface ICaptureTarget
{
    /// <summary>Run one scripted action (<c>verb</c> or <c>verb:arg</c>); false when the verb is unknown.</summary>
    bool RunAction(string verb, string arg);

    /// <summary>A per-frame probe sample (e.g. a projected surface point, px) for probe.csv; false while no probe is armed.</summary>
    bool TryProbe(out double x, out double y);
}

/// <summary>
/// Scene-agnostic capture harness, registered as an autoload. Driven by user args after `--`:
///   --capture-dir DIR   output directory (default res://capture, i.e. the project's capture/)
///   --frames N          quit after N process frames
///   --shots F:NAME,...  save the root viewport as NN-NAME.png at process frame F (NN = 1-based order in the list)
///   --warmup N          frames tagged as warmup in device.json for summarisers (default 10); every frame is still logged
///   --actions "A;B;..." scripted actions, run in order from frame 1: `wait:N` waits N frames, `shot:NAME` saves
///                       NN-NAME.png (numbered after --shots), anything else goes to the scene's <see cref="ICaptureTarget"/>
///                       (e.g. `viewTabletop;wait:60;select:mars;flyAbove:earth;setWarp:1e6;shot:name;setDiorama:1;key:t`)
/// Writes DIR/device.json at startup, appends frame,dt_ms,cpu_ms,gpu_ms to DIR/frametimes.csv every frame, and
/// frame,x,y to DIR/probe.csv on frames where the target reports a probe sample.
/// Inactive (frees itself) when none of these args is present, so a normal run writes nothing.
/// Depends on nothing but Godot and the BCL: spikes copy this file (dropping ICaptureTarget if unused).
/// </summary>
public partial class CaptureDriver : Node
{
    string _dir = "";
    int _frames = -1;
    int _warmup = 10;
    readonly Dictionary<int, List<string>> _shots = new();
    int _shotCount;
    readonly List<(string Verb, string Arg)> _actions = [];
    int _nextAction;
    int _waitUntil;
    int _frame;
    Rid _viewport;
    StreamWriter? _csv;
    StreamWriter? _probe;
    ICaptureTarget? _target;

    public override void _Ready()
    {
        if (!ParseArgs(OS.GetCmdlineUserArgs()))
        {
            QueueFree();
            return;
        }
        // Runs after every scene node so a frame's timing covers the whole scene's _Process.
        ProcessPriority = int.MaxValue;
        Directory.CreateDirectory(_dir);
        _viewport = GetViewport().GetViewportRid();
        RenderingServer.ViewportSetMeasureRenderTime(_viewport, true);
        WriteDeviceJson();
        _csv = new StreamWriter(Path.Combine(_dir, "frametimes.csv"), false, new UTF8Encoding(false));
        _csv.WriteLine("frame,dt_ms,cpu_ms,gpu_ms");
        GD.Print($"[capture] adapter={RenderingServer.GetVideoAdapterName()}");
        GD.Print($"[capture] dir={_dir} frames={_frames} warmup={_warmup} shots={_shots.Count} actions={_actions.Count}");
    }

    bool ParseArgs(string[] args)
    {
        var active = false;
        var dir = "res://capture";
        for (var i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
            switch (args[i])
            {
                case "--capture-dir": dir = Next(); active = true; break;
                case "--frames": _frames = int.Parse(Next(), CultureInfo.InvariantCulture); active = true; break;
                case "--warmup": _warmup = int.Parse(Next(), CultureInfo.InvariantCulture); active = true; break;
                case "--shots":
                    foreach (var spec in Next().Split(',', StringSplitOptions.RemoveEmptyEntries))
                    {
                        var parts = spec.Split(':', 2);
                        if (parts.Length != 2) throw new ArgumentException($"bad --shots entry '{spec}', want FRAME:NAME");
                        var f = int.Parse(parts[0], CultureInfo.InvariantCulture);
                        if (!_shots.TryGetValue(f, out var list)) _shots[f] = list = new List<string>();
                        list.Add($"{++_shotCount:00}-{parts[1]}.png");
                    }
                    active = true;
                    break;
                case "--actions":
                    foreach (var spec in Next().Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        var parts = spec.Split(':', 2);
                        _actions.Add((parts[0], parts.Length > 1 ? parts[1] : ""));
                    }
                    active = true;
                    break;
            }
        }
        _dir = dir.StartsWith("res://") || dir.StartsWith("user://") ? ProjectSettings.GlobalizePath(dir) : Path.GetFullPath(dir);
        return active;
    }

    ICaptureTarget? FindTarget()
    {
        if (GetTree().CurrentScene is ICaptureTarget t) return t;
        foreach (var n in GetTree().Root.GetChildren())
            if (n is ICaptureTarget c) return c;
        return null;
    }

    public override void _Process(double delta)
    {
        if (_csv == null) return;
        _frame++;
        // Measured times lag a frame or two behind (GPU timestamps are read back later); that is fine for percentiles.
        var cpu = RenderingServer.ViewportGetMeasuredRenderTimeCpu(_viewport);
        var gpu = RenderingServer.ViewportGetMeasuredRenderTimeGpu(_viewport);
        _csv.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{_frame},{delta * 1000.0:F4},{cpu:F4},{gpu:F4}"));

        if (_shots.TryGetValue(_frame, out var names))
            foreach (var name in names) SaveShot(name);

        RunActions();

        if (_target != null && _target.TryProbe(out var px, out var py))
        {
            _probe ??= NewProbeCsv();
            _probe.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{_frame},{px:F6},{py:F6}"));
        }

        if (_frames > 0 && _frame >= _frames)
        {
            _csv.Flush();
            _csv.Dispose();
            _csv = null;
            _probe?.Dispose();
            _probe = null;
            GD.Print($"[capture] done frames={_frame}");
            GetTree().Quit();
        }
    }

    StreamWriter NewProbeCsv()
    {
        var w = new StreamWriter(Path.Combine(_dir, "probe.csv"), false, new UTF8Encoding(false));
        w.WriteLine("frame,x,y");
        return w;
    }

    void RunActions()
    {
        if (_frame < _waitUntil) return;
        while (_nextAction < _actions.Count)
        {
            var (verb, arg) = _actions[_nextAction++];
            if (verb == "wait")
            {
                _waitUntil = _frame + int.Parse(arg, CultureInfo.InvariantCulture);
                return;
            }
            if (verb == "shot")
            {
                SaveShot($"{++_shotCount:00}-{arg}.png");
                continue;
            }
            _target ??= FindTarget();
            if (_target == null)
            {
                GD.PushError($"[capture] action '{verb}' but the scene has no ICaptureTarget");
                continue;
            }
            var ok = _target.RunAction(verb, arg);
            GD.Print($"[capture] action frame={_frame} {verb}{(arg.Length > 0 ? ":" + arg : "")} {(ok ? "ok" : "UNKNOWN")}");
        }
        // Probing needs the target even when every action is a wait or a shot.
        _target ??= FindTarget();
    }

    void SaveShot(string name)
    {
        // The viewport texture holds the last drawn frame; GetImage stalls on the GPU, so this frame's dt is inflated.
        var img = GetViewport().GetTexture().GetImage();
        var path = Path.Combine(_dir, name);
        var err = img.SavePng(path);
        GD.Print($"[capture] shot frame={_frame} {path} {img.GetWidth()}x{img.GetHeight()} {err}");
    }

    public override void _ExitTree()
    {
        _csv?.Dispose();
        _probe?.Dispose();
    }

    void WriteDeviceJson()
    {
        var info = Engine.GetVersionInfo();
        var doc = new Dictionary<string, object?>
        {
            ["godot_version"] = info["string"].AsString(),
            ["adapter_name"] = RenderingServer.GetVideoAdapterName(),
            ["adapter_vendor"] = RenderingServer.GetVideoAdapterVendor(),
            ["adapter_type"] = RenderingServer.GetVideoAdapterType().ToString(),
            ["api_version"] = RenderingServer.GetVideoAdapterApiVersion(),
            ["driver_info"] = OS.GetVideoAdapterDriverInfo(),
            ["rendering_method"] = RenderingServer.GetCurrentRenderingMethod(),
            ["rendering_driver"] = RenderingServer.GetCurrentRenderingDriverName(),
            ["display_server"] = DisplayServer.GetName(),
            ["viewport_size"] = new[] { (int)GetViewport().GetVisibleRect().Size.X, (int)GetViewport().GetVisibleRect().Size.Y },
            ["warmup_frames"] = _warmup,
        };
        var rd = RenderingServer.GetRenderingDevice();
        if (rd == null)
        {
            doc["rendering_device"] = null;
            doc["rendering_device_note"] = "RenderingServer.GetRenderingDevice() returned null (Compatibility renderer or dummy driver)";
        }
        else
        {
            var limits = new SortedDictionary<string, ulong>();
            foreach (var limit in Enum.GetValues<RenderingDevice.Limit>())
            {
                // MetalFX scaler limits are Apple-only; asking a Vulkan device for them only logs an error.
                if (limit.ToString().StartsWith("Metalfx")) continue;
                limits[limit.ToString()] = rd.LimitGet(limit);
            }
            doc["rendering_device"] = new Dictionary<string, object?> { ["device_name"] = rd.GetDeviceName(), ["limits"] = limits };
        }
        var json = JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(_dir, "device.json"), json + "\n");
    }
}
