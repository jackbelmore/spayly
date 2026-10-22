using System;

namespace Spayly.Game.Render;

/// <summary>
/// Value noise; port of src/render/noise.ts (3D, rock shapes) and the 2D helpers of src/render/textures.ts (procedural
/// maps). Integer arithmetic is JS <c>Math.imul</c> / <c>&gt;&gt;&gt;</c> bit for bit, so a seed gives the web's shape.
/// </summary>
public static class Noise
{
    static int Imul(int a, int b) => unchecked(a * b);

    static double Hash3i(int ix, int iy, int iz, int seed)
    {
        unchecked
        {
            var h = Imul(ix, 374761393) + Imul(iy, 668265263) + Imul(iz, 2147483647 ^ 12582917) + Imul(seed, 1442695041);
            h = Imul(h ^ (int)((uint)h >> 13), 1274126177);
            return (uint)(h ^ (int)((uint)h >> 16)) / 4294967296.0;
        }
    }

    public static double VNoise3(double x, double y, double z, int seed)
    {
        int ix = (int)Math.Floor(x), iy = (int)Math.Floor(y), iz = (int)Math.Floor(z);
        double fx = x - ix, fy = y - iy, fz = z - iz;
        double sx = fx * fx * (3 - 2 * fx), sy = fy * fy * (3 - 2 * fy), sz = fz * fz * (3 - 2 * fz);
        double c000 = Hash3i(ix, iy, iz, seed), c100 = Hash3i(ix + 1, iy, iz, seed);
        double c010 = Hash3i(ix, iy + 1, iz, seed), c110 = Hash3i(ix + 1, iy + 1, iz, seed);
        double c001 = Hash3i(ix, iy, iz + 1, seed), c101 = Hash3i(ix + 1, iy, iz + 1, seed);
        double c011 = Hash3i(ix, iy + 1, iz + 1, seed), c111 = Hash3i(ix + 1, iy + 1, iz + 1, seed);
        var x00 = c000 + (c100 - c000) * sx;
        var x10 = c010 + (c110 - c010) * sx;
        var x01 = c001 + (c101 - c001) * sx;
        var x11 = c011 + (c111 - c011) * sx;
        var y0 = x00 + (x10 - x00) * sy;
        var y1 = x01 + (x11 - x01) * sy;
        return y0 + (y1 - y0) * sz;
    }

    /// <summary>Fractal value noise in three dimensions; sampling it on a sphere has no pole or seam.</summary>
    public static double Fbm3(double x, double y, double z, int seed, int oct = 4)
    {
        double v = 0, a = 0.5, f = 1;
        for (var i = 0; i < oct; i++)
        {
            v += a * VNoise3(x * f, y * f, z * f, seed + i * 31);
            a *= 0.5;
            f *= 2.1;
        }
        return v;
    }

    /// <summary>textures.ts hash(): 2D lattice hash in [0, 1).</summary>
    public static double Hash2(int ix, int iy, int seed)
    {
        unchecked
        {
            var h = Imul(ix, 374761393) + Imul(iy, 668265263) + Imul(seed, 1442695041);
            h = Imul(h ^ (int)((uint)h >> 13), 1274126177);
            return (uint)(h ^ (int)((uint)h >> 16)) / 4294967296.0;
        }
    }

    static double VNoise2(double x, double y, int seed)
    {
        int ix = (int)Math.Floor(x), iy = (int)Math.Floor(y);
        double fx = x - ix, fy = y - iy;
        double sx = fx * fx * (3 - 2 * fx), sy = fy * fy * (3 - 2 * fy);
        double a = Hash2(ix, iy, seed), b = Hash2(ix + 1, iy, seed), c = Hash2(ix, iy + 1, seed), d = Hash2(ix + 1, iy + 1, seed);
        return a + (b - a) * sx + (c - a) * sy + (a - b - c + d) * sx * sy;
    }

    /// <summary>textures.ts fbm(): 2D, octaves at ×2.03.</summary>
    public static double Fbm2(double x, double y, int seed, int octaves = 5)
    {
        double v = 0, amp = 0.5, f = 1;
        for (var i = 0; i < octaves; i++)
        {
            v += amp * VNoise2(x * f, y * f, seed + i * 17);
            amp *= 0.5;
            f *= 2.03;
        }
        return v;
    }
}
