using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using Godot;

namespace Spayly.Game;

/// <summary>
/// Scene-agnostic capture harness, registered as an autoload. Driven by user args after `--`:
///   --capture-dir DIR   output directory (default res://capture, i.e. the project's capture/)
///   --frames N          quit after N process frames
///   --shots F:NAME,...  save the root viewport as NN-NAME.png at process frame F (NN = 1-based order in the list)
///   --warmup N          frames tagged as warmup in device.json for summarisers (default 10); every frame is still logged
/// Writes DIR/device.json at startup and appends frame,dt_ms,cpu_ms,gpu_ms to DIR/frametimes.csv every frame.
/// Inactive (frees itself) when none of these args is present, so a normal run writes nothing.
/// Depends on nothing but Godot and the BCL: spikes copy this file and add the same autoload line.
/// </summary>
public partial class CaptureDriver : Node
{
    string _dir = "";
    int _frames = -1;
    int _warmup = 10;
    readonly Dictionary<int, List<string>> _shots = new();
    int _frame;
    Rid _viewport;
    StreamWriter? _csv;

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
        GD.Print($"[capture] dir={_dir} frames={_frames} warmup={_warmup} shots={_shots.Count}");
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
                    var n = 0;
                    foreach (var spec in Next().Split(',', StringSplitOptions.RemoveEmptyEntries))
                    {
                        var parts = spec.Split(':', 2);
                        if (parts.Length != 2) throw new ArgumentException($"bad --shots entry '{spec}', want FRAME:NAME");
                        var f = int.Parse(parts[0], CultureInfo.InvariantCulture);
                        if (!_shots.TryGetValue(f, out var list)) _shots[f] = list = new List<string>();
                        list.Add($"{++n:00}-{parts[1]}.png");
                    }
                    active = true;
                    break;
            }
        }
        _dir = dir.StartsWith("res://") || dir.StartsWith("user://") ? ProjectSettings.GlobalizePath(dir) : Path.GetFullPath(dir);
        return active;
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
        {
            // The viewport texture holds the last drawn frame; GetImage stalls on the GPU, so this frame's dt is inflated.
            var img = GetViewport().GetTexture().GetImage();
            foreach (var name in names)
            {
                var path = Path.Combine(_dir, name);
                var err = img.SavePng(path);
                GD.Print($"[capture] shot frame={_frame} {path} {img.GetWidth()}x{img.GetHeight()} {err}");
            }
        }

        if (_frames > 0 && _frame >= _frames)
        {
            _csv.Flush();
            _csv.Dispose();
            _csv = null;
            GD.Print($"[capture] done frames={_frame}");
            GetTree().Quit();
        }
    }

    public override void _ExitTree() => _csv?.Dispose();

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
