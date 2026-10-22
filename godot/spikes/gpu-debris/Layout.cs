using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Spayly.Spikes.GpuDebris;

/// <summary>std430/std140 vec4: 16 bytes, 16-byte stride. pos: xyz km from the central body, w radius km. vel: xyz km/s.</summary>
[StructLayout(LayoutKind.Explicit, Size = 16)]
public struct Float4
{
    [FieldOffset(0)] public float X;
    [FieldOffset(4)] public float Y;
    [FieldOffset(8)] public float Z;
    [FieldOffset(12)] public float W;

    public Float4(float x, float y, float z, float w) { X = x; Y = y; Z = z; W = w; }
}

/// <summary>The Bodies UBO (std140): vec4 b0[16] at 0, vec4 b1[16] at 256. xyz km, w = GM km^3/s^2.</summary>
[StructLayout(LayoutKind.Explicit, Size = 512)]
public struct BodiesUbo
{
    public const int Max = 16;
    [FieldOffset(0)] public Bodies16 Now;
    [FieldOffset(256)] public Bodies16 Next;
}

[InlineArray(BodiesUbo.Max)]
public struct Bodies16 { Float4 _e; }

static class Layout
{
    /// <summary>Throws if the managed layout differs from what the GLSL declares; the byte round-trip then proves the GPU side.</summary>
    public static void AssertManaged()
    {
        Check(Unsafe.SizeOf<Float4>() == 16, "sizeof(Float4) != 16");
        Check(Marshal.OffsetOf<Float4>(nameof(Float4.W)) == 12, "Float4.W not at 12");
        Check(Unsafe.SizeOf<BodiesUbo>() == 512, "sizeof(BodiesUbo) != 512");
        Check(Marshal.OffsetOf<BodiesUbo>(nameof(BodiesUbo.Next)) == 256, "BodiesUbo.Next not at 256");
    }

    static void Check(bool ok, string what)
    {
        if (!ok) throw new InvalidOperationException("layout: " + what);
    }
}
