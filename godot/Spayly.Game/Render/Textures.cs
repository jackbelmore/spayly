using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;

namespace Spayly.Game.Render;

/// <summary>Which of a body's maps (manifest.json field names).</summary>
public enum MapSlot { Albedo, Night, Specular, Normal, Clouds, Ring }

/// <summary>A view whose surface map the focus-first forcing may raise to full resolution (App.Textures).</summary>
public interface IStreamedView
{
    /// <summary>The surface map as loaded (a <see cref="StreamedTexture2D"/> when the import streams it), or null when procedural.</summary>
    Texture2D? AlbedoMap { get; }
    /// <summary>Width of the file behind <see cref="AlbedoMap"/>, px; 0 when there is none.</summary>
    int AlbedoWidth { get; }
}

/// <summary>
/// Body maps: <c>res://textures/manifest.json</c> (written by godot/tools/prepare-textures.mjs) names each body's files,
/// which load as <see cref="StreamedTexture2D"/> so the engine streams their mips (spike M1b) — this replaces the web's
/// MapStream, decode workers and paced uploads. Textures are gitignored, so every map has a procedural fallback here
/// (ports of textures.ts); the game runs with no textures folder at all.
/// </summary>
public static class TextureLibrary
{
    const string Root = "res://textures/";

    /// <summary>One manifest row: file names in the textures folder plus the albedo's size.</summary>
    public sealed class Entry
    {
        public string? Albedo, Night, Specular, Normal, Clouds, Ring, Credit;
        public int Width, Height;
        public bool FillGaps;
    }

    static Dictionary<string, Entry>? manifest;
    static bool manifestRead;
    static readonly Dictionary<string, Texture2D?> Loaded = [];

    /// <summary>Manifest key for a body: its id, except the Sun, which the tool files as <c>_sun</c>.</summary>
    public static string KeyFor(Spayly.Sim.Body b) => b.Kind == Spayly.Sim.BodyKind.Star ? "_sun" : b.Id;

    /// <summary>The manifest row for a key, or null (no manifest, no row).</summary>
    public static Entry? EntryFor(string key)
    {
        if (!manifestRead) ReadManifest();
        return manifest != null && manifest.TryGetValue(key, out var e) ? e : null;
    }

    static void ReadManifest()
    {
        manifestRead = true;
        const string path = Root + "manifest.json";
        if (!FileAccess.FileExists(path))
        {
            GD.Print("[tex] no textures/manifest.json: every body uses its procedural map");
            return;
        }
        try
        {
            using var doc = JsonDocument.Parse(FileAccess.GetFileAsString(path));
            manifest = [];
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                var v = p.Value;
                string? S(string name) => v.TryGetProperty(name, out var x) && x.ValueKind == JsonValueKind.String ? x.GetString() : null;
                int I(string name) => v.TryGetProperty(name, out var x) && x.ValueKind == JsonValueKind.Number ? x.GetInt32() : 0;
                manifest[p.Name] = new Entry
                {
                    Albedo = S("albedo"), Night = S("night"), Specular = S("specular"), Normal = S("normal"), Clouds = S("clouds"),
                    Ring = S("ring"), Credit = S("credit"), Width = I("width"), Height = I("height"),
                    FillGaps = v.TryGetProperty("fillGaps", out var fg) && fg.ValueKind == JsonValueKind.True,
                };
            }
            GD.Print($"[tex] manifest: {manifest.Count} entries");
        }
        catch (Exception e)
        {
            GD.PushWarning($"textures/manifest.json unreadable ({e.Message}); using procedural maps");
            manifest = null;
        }
    }

    /// <summary>One of a body's maps, or null when the manifest does not list it or its file (and import) is missing.</summary>
    public static Texture2D? Load(string key, MapSlot slot)
    {
        var e = EntryFor(key);
        if (e == null) return null;
        var file = slot switch
        {
            MapSlot.Albedo => e.Albedo, MapSlot.Night => e.Night, MapSlot.Specular => e.Specular,
            MapSlot.Normal => e.Normal, MapSlot.Clouds => e.Clouds, _ => e.Ring,
        };
        return file == null ? null : LoadFile(file);
    }

    /// <summary>A texture by file name in the textures folder, cached; null when absent.</summary>
    public static Texture2D? LoadFile(string file)
    {
        if (Loaded.TryGetValue(file, out var t)) return t;
        var path = Root + file;
        t = null;
        if (ResourceLoader.Exists(path))
        {
            t = ResourceLoader.Load(path) as Texture2D;
            if (t == null) GD.PushWarning($"{path} exists but did not load as a Texture2D");
            else if (t is StreamedTexture2D s) CapToCeiling(s);
        }
        Loaded[file] = t;
        return t;
    }

    /// <summary>
    /// Widest any map may stream to, px (the View panel's texture detail; the web's textureCeiling). Set through
    /// <see cref="SetCeiling"/>, which re-caps every loaded map.
    /// </summary>
    public static int Ceiling { get; private set; } = 8192;

    /// <summary>Mip level whose width first fits <paramref name="ceiling"/>: 0 for a map already within it.</summary>
    public static int LodFor(int width, int ceiling, out int levelWidth)
    {
        var lod = 0;
        levelWidth = width;
        while (levelWidth > ceiling && levelWidth > 1)
        {
            levelWidth = (levelWidth + 1) / 2;
            lod++;
        }
        return lod;
    }

    /// <summary>
    /// Keep a streamed map at or below the ceiling with its own <c>MinLodOverride</c> (finest level allowed; the override
    /// counts from 1, 0 = global), measured: with a finest-level override of LOD 2, Earth's 16384 map held at 4096 at the
    /// surface. Per map rather than the global override, because a global LOD cap would cut an 8K map as deep as a 16K one.
    /// A map within the ceiling is left at 0: setting any override reloads what is resident (M1b).
    /// </summary>
    static void CapToCeiling(StreamedTexture2D s)
    {
        var lod = LodFor(s.GetWidth(), Ceiling, out _);
        var want = lod == 0 ? 0 : lod + 1;
        if (s.MinLodOverride != want) s.MinLodOverride = want;
    }

    /// <summary>Change the ceiling and re-cap every map loaded so far; returns how many overrides changed.</summary>
    public static int SetCeiling(int ceiling)
    {
        Ceiling = ceiling;
        var n = 0;
        foreach (var t in Loaded.Values)
        {
            if (t is not StreamedTexture2D s) continue;
            var before = s.MinLodOverride;
            CapToCeiling(s);
            if (s.MinLodOverride != before) n++;
        }
        return n;
    }

    /// <summary>
    /// Width of the GPU texture behind <paramref name="tex"/> right now, px: for a streamed map, the resident mip level
    /// (spike M1b reads it the same way). -1 when it cannot be read. Allocates (an RDTextureFormat); not for every frame.
    /// </summary>
    public static long ResidentWidth(Texture2D tex)
    {
        var rd = RenderingServer.GetRenderingDevice();
        if (rd == null) return -1;
        var rid = RenderingServer.TextureGetRdTexture(tex.GetRid());
        if (!rid.IsValid) return -1;
        using var fmt = rd.TextureGetFormat(rid);
        return fmt.Width;
    }

    /// <summary>
    /// Streaming memory budget for a texture ceiling (the View panel's 2K/4K/8K/16K), MB. The ceiling caps what one map
    /// may cost; the budget caps what all of them may, and is what makes maps the camera has left fall back (M1b: a
    /// visible map never drops without budget pressure).
    /// </summary>
    public static uint BudgetMbFor(int ceiling) => ceiling >= 16384 ? 2048u : ceiling >= 8192 ? 1024u : ceiling >= 4096 ? 512u : 256u;

    // ---------- procedural fallbacks (textures.ts) ----------

    static readonly Dictionary<string, ImageTexture> Procedural = [];
    static ImageTexture? white, grey, sun, glow;

    /// <summary>
    /// Fill an RGBA8 image like textures.ts canvasTexture: <paramref name="fill"/> gets (u, v) of the texel's corner and
    /// writes bytes. Rows run in parallel: 40 fallback planets at 1024×512 is 20M texels of fbm.
    /// </summary>
    static ImageTexture Canvas(int w, int h, Action<double, double, byte[], int> fill, bool alpha)
    {
        var bpp = alpha ? 4 : 3;
        var px = new byte[w * h * bpp];
        Parallel.For(0, h, y =>
        {
            for (var x = 0; x < w; x++) fill((double)x / w, (double)y / h, px, (y * w + x) * bpp);
        });
        var img = Image.CreateFromData(w, h, false, alpha ? Image.Format.Rgba8 : Image.Format.Rgb8, px);
        img.GenerateMipmaps();
        return ImageTexture.CreateFromImage(img);
    }

    static byte B(double v) => (byte)Math.Clamp(v * 255, 0, 255);

    /// <summary>
    /// Banded (gas) or mottled (rock, ice) map in the catalogue colour. The web writes three's linear colour into sRGB canvas
    /// bytes and samples them as sRGB, so the colour is linearised here too, for the same look.
    /// </summary>
    public static ImageTexture ProceduralPlanet(int seed, Color baseSrgb, Spayly.Sim.Surface surface)
    {
        var kind = surface == Spayly.Sim.Surface.Gas ? 1 : surface == Spayly.Sim.Surface.Ice ? 2 : 0;
        var key = $"{seed}|{baseSrgb.ToHtml()}|{kind}";
        if (Procedural.TryGetValue(key, out var t)) return t;
        var c = baseSrgb.SrgbToLinear();
        double br = c.R, bg = c.G, bb = c.B;
        t = Canvas(1024, 512, (u, v, px, i) =>
        {
            double r, g, b;
            if (kind == 1)
            {
                var band = Noise.Fbm2(u * 3 + Noise.Fbm2(u * 8, v * 40, seed) * 0.15, v * 24, seed, 4);
                var s = 0.55 + 0.45 * Math.Sin(v * 40 + band * 6);
                r = br * (0.7 + 0.5 * s); g = bg * (0.7 + 0.5 * s); b = bb * (0.7 + 0.5 * s);
            }
            else
            {
                var n = Noise.Fbm2(u * 8, v * 4, seed);
                var m = Noise.Fbm2(u * 30, v * 15, seed + 5, 4);
                var shade = 0.55 + 0.6 * n + 0.15 * m;
                r = br * shade; g = bg * shade; b = bb * shade;
                if (kind == 2) { r = r * 0.5 + 0.5; g = g * 0.5 + 0.55; b = b * 0.5 + 0.6; }
            }
            px[i] = B(r); px[i + 1] = B(g); px[i + 2] = B(b);
        }, false);
        Procedural[key] = t;
        return t;
    }

    public static ImageTexture ProceduralSun() => sun ??= Canvas(1024, 512, (u, v, px, i) =>
    {
        var s = 0.75 + 0.35 * Noise.Fbm2(u * 16, v * 8, 99, 5);
        px[i] = B(s); px[i + 1] = B(s * 0.75); px[i + 2] = B(s * 0.45);
    }, false);

    /// <summary>Ring colour/alpha strip: rgb = colour, a = optical depth; sample with clamp.</summary>
    public static ImageTexture ProceduralRingStrip(int seed = 3)
    {
        var key = $"ring|{seed}";
        if (Procedural.TryGetValue(key, out var t)) return t;
        t = Canvas(1024, 4, (u, _, px, i) =>
        {
            var n = Noise.Fbm2(u * 60, 0.5, seed, 4);
            var gap = Math.Exp(-Math.Pow((u - 0.62) / 0.03, 2)) + 0.6 * Math.Exp(-Math.Pow((u - 0.85) / 0.01, 2));
            var a = Math.Max(0, (0.35 + 0.65 * n) * (1 - gap) * Math.Min(1, u * 12) * Math.Min(1, (1 - u) * 8));
            px[i] = 230; px[i + 1] = 215; px[i + 2] = 190; px[i + 3] = B(a);
        }, true);
        Procedural[key] = t;
        return t;
    }

    /// <summary>Soft radial sprite (alpha = (1 − d)^power) for glows; rgb white.</summary>
    public static ImageTexture RadialSprite(int size = 128, double power = 2.2)
    {
        if (size == 128 && power == 2.2 && glow != null) return glow;
        var t = Canvas(size, size, (u, v, px, i) =>
        {
            var d = Math.Sqrt((u - 0.5) * (u - 0.5) + (v - 0.5) * (v - 0.5)) * 2;
            px[i] = px[i + 1] = px[i + 2] = 255;
            px[i + 3] = B(Math.Pow(Math.Max(0, 1 - d), power));
        }, true);
        if (size == 128 && power == 2.2) glow = t;
        return t;
    }

    public static ImageTexture White() => white ??= Canvas(4, 4, (_, _, px, i) => { px[i] = px[i + 1] = px[i + 2] = 255; }, false);

    /// <summary>Neutral damage map: 128 grey, which the planet shader doubles to 1.</summary>
    public static ImageTexture Grey() => grey ??= Canvas(4, 4, (_, _, px, i) => { px[i] = px[i + 1] = px[i + 2] = 128; }, false);
}
