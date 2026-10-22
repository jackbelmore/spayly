using System;
using System.IO;
using Godot;
using Spayly.Sim;

namespace Spayly.Game.Render;

/// <summary>
/// Land/water lookup from a specular map (bright = ocean); port of SurfaceMask in src/render/textures.ts. The impact model
/// asks it where an impactor struck Earth, through the <c>SurfaceAt</c> delegate the app injects into Collide.
/// Kept as one byte per texel at 512 × 256: the question is "ocean or not" at the scale of a crater, and the full 8K map
/// would cost 32 MB of managed memory for no difference in the answer.
/// </summary>
public sealed class SurfaceMask
{
    public const int Width = 512;
    public const int Height = 256;
    readonly byte[] data;

    SurfaceMask(byte[] data) => this.data = data;

    /// <summary>
    /// Decode <paramref name="resPath"/> straight from disk (not the imported texture, whose VRAM-compressed copy would need a
    /// decompress) and shrink it. Null when the file is missing or unreadable: every impact then reads as land, which is what
    /// the web does without its map. Slow (an 8K JPEG), so call it off the main thread.
    /// </summary>
    public static SurfaceMask? Load(string resPath)
    {
        var path = ProjectSettings.GlobalizePath(resPath);
        if (!File.Exists(path)) return null;
        var img = Image.LoadFromFile(path);
        if (img == null || img.IsEmpty()) return null;
        if (img.IsCompressed()) img.Decompress();
        img.Convert(Image.Format.R8);
        img.Resize(Width, Height, Image.Interpolation.Lanczos);
        var bytes = img.GetData();
        return bytes.Length >= Width * Height ? new SurfaceMask(bytes) : null;
    }

    /// <summary>Whether a body-frame (geometry frame) direction points at ocean.</summary>
    public bool IsWater(Vec3 dirBody)
    {
        var (u, v) = Orientation.BodyDirToUv(dirBody);
        var x = Math.Min(Width - 1, (int)Math.Floor(u * Width));
        var y = Math.Min(Height - 1, (int)Math.Floor((1 - v) * Height));
        return data[y * Width + x] > 128;
    }
}
