using System;
using Spayly.Sim;

namespace Spayly.Game.Render;

/// <summary>
/// Crater overlay painted into an equirectangular RGB8 buffer (128 grey = no change; the planet shader doubles it and
/// multiplies the albedo); port of planetView.ts paintDamage/drawCrater, which paints with the canvas 2D API. The same
/// layers in the same order — ejecta blanket, rays, rim, floor, central peak, or a dark bruise on a gas giant — with
/// source-over blending, canvas-style gradients and a one-pixel antialiased edge. Reads <see cref="Body.Damage"/> only.
/// </summary>
public static class Damage
{
    /// <summary>Overlay width for a body: 2048 above 3000 km radius, else 1024; height is half.</summary>
    public static int WidthFor(Body b) => b.Radius > 3000 ? 2048 : 1024;

    /// <summary>Repaint every crater of <paramref name="damage"/> (normally <paramref name="body"/>.Damage) into <paramref name="px"/> (w × h × 3 bytes).</summary>
    public static void Paint(Body body, DamageState damage, byte[] px, int w, int h)
    {
        Array.Fill(px, (byte)128);
        foreach (var c in damage.Craters) DrawCrater(body, c, px, w, h);
    }

    static void DrawCrater(Body body, Crater c, byte[] px, int w, int h)
    {
        var (u, v) = Orientation.BodyDirToUv(c.Dir);
        var cx = u * w;
        var cy = (1 - v) * h;
        var len = Vec3.Length(c.Dir);
        if (len == 0) len = 1;
        var lat = Math.Asin(Math.Clamp(c.Dir.Y / len, -1, 1));
        var sx = 1 / Math.Max(Math.Cos(lat), 0.15);
        var theta = c.DiameterKm / 2 / body.Radius;
        var r = Math.Max(theta / Math.PI * h, 2.5);
        // JS: Math.abs(Math.floor(...)) | 0 — the magnitude stays well inside int32, so the ToInt32 wrap never bites.
        var seed = (long)Math.Abs(Math.Floor((c.Dir.X * 1000 + c.Dir.Z * 7919 + c.Dir.Y * 104729) * 1e3));
        var gas = body.Surface == Surface.Gas;
        foreach (var x0 in (ReadOnlySpan<double>)[cx - w, cx, cx + w])
        {
            if (x0 + r * 5 * sx < 0 || x0 - r * 5 * sx > w) continue;
            var p = new Pen(px, w, h, x0, cy, sx);
            if (gas)
            {
                p.RadialFill(0, r * 1.6, r * 1.6, [(0, 38, 28, 20, 0.9), (0.5, 50, 38, 28, 0.6), (1, 60, 45, 35, 0)]);
                continue;
            }
            p.RadialFill(r * 0.9, r * 2.4, r * 2.4, [(0, 152, 150, 146, 0.85), (1, 152, 150, 146, 0)]);
            var rays = 14 + (int)(seed % 9);
            var lw = Math.Max(1, r * 0.06);
            for (var k = 0; k < rays; k++)
            {
                var jitter = seed * (k + 3) % 97 / 97.0;
                var ang = 2 * Math.PI * k / rays + jitter * 0.4;
                var l = r * (2.6 + 3 * (seed * (k + 7) % 89 / 89.0));
                p.Line(Math.Cos(ang) * r * 1.1, Math.Sin(ang) * r * 1.1, Math.Cos(ang) * l, Math.Sin(ang) * l, lw, 178, 175, 170, 0.32);
            }
            p.Ring(r, Math.Max(1, r * 0.18), 184, 181, 177, 1);
            p.RadialFill(0, r * 0.92, r * 0.92, [(0, 66, 64, 62, 1), (0.7, 92, 90, 88, 1), (1, 122, 120, 117, 1)]);
            if (c.DiameterKm > 4 && r > 8) p.RadialFill(0, r * 0.12, r * 0.12, [(0, 150, 148, 145, 1), (1, 150, 148, 145, 1)]);
        }
    }

    /// <summary>The canvas state of one crater copy: translate to (x0, y0), scale x by sx; coordinates below are local.</summary>
    struct Pen(byte[] px, int w, int h, double x0, double y0, double sx)
    {
        void Blend(int x, int y, double rr, double gg, double bb, double a)
        {
            if (a <= 0 || x < 0 || x >= w || y < 0 || y >= h) return;
            var i = (y * w + x) * 3;
            px[i] = (byte)Math.Round(px[i] + (rr - px[i]) * a);
            px[i + 1] = (byte)Math.Round(px[i + 1] + (gg - px[i + 1]) * a);
            px[i + 2] = (byte)Math.Round(px[i + 2] + (bb - px[i + 2]) * a);
        }

        /// <summary>Pixel box covering local radius <paramref name="ext"/>, clipped to the image.</summary>
        void Box(double ext, out int xa, out int xb, out int ya, out int yb)
        {
            xa = Math.Max(0, (int)Math.Floor(x0 - ext * sx - 1));
            xb = Math.Min(w - 1, (int)Math.Ceiling(x0 + ext * sx + 1));
            ya = Math.Max(0, (int)Math.Floor(y0 - ext - 1));
            yb = Math.Min(h - 1, (int)Math.Ceiling(y0 + ext + 1));
        }

        /// <summary>
        /// Fill a disc of radius <paramref name="fillR"/> with createRadialGradient(0, 0, r0, 0, 0, r1): offset t = (d − r0)/(r1 − r0),
        /// clamped, colour stops interpolated unpremultiplied.
        /// </summary>
        public void RadialFill(double r0, double r1, double fillR, ReadOnlySpan<(double T, double R, double G, double B, double A)> stops)
        {
            Box(fillR, out var xa, out var xb, out var ya, out var yb);
            for (var y = ya; y <= yb; y++)
            {
                var ly = y + 0.5 - y0;
                for (var x = xa; x <= xb; x++)
                {
                    var lx = (x + 0.5 - x0) / sx;
                    var d = Math.Sqrt(lx * lx + ly * ly);
                    var cover = Math.Clamp(fillR - d + 0.5, 0, 1);
                    if (cover <= 0) continue;
                    var t = r1 > r0 ? Math.Clamp((d - r0) / (r1 - r0), 0, 1) : 1;
                    var s = 1;
                    while (s < stops.Length - 1 && stops[s].T < t) s++;
                    var a = stops[s - 1];
                    var b = stops[s];
                    var f = b.T > a.T ? Math.Clamp((t - a.T) / (b.T - a.T), 0, 1) : 1;
                    Blend(x, y, a.R + (b.R - a.R) * f, a.G + (b.G - a.G) * f, a.B + (b.B - a.B) * f, (a.A + (b.A - a.A) * f) * cover);
                }
            }
        }

        /// <summary>Stroke a butt-capped segment of width <paramref name="lw"/> (local units).</summary>
        public void Line(double ax, double ay, double bx, double by, double lw, double rr, double gg, double bb, double alpha)
        {
            var ext = Math.Max(Math.Sqrt(bx * bx + by * by), Math.Sqrt(ax * ax + ay * ay)) + lw;
            Box(ext, out var xa, out var xb, out var ya, out var yb);
            double dx = bx - ax, dy = by - ay;
            var segLen = Math.Sqrt(dx * dx + dy * dy);
            if (segLen <= 0) return;
            double ux = dx / segLen, uy = dy / segLen;
            // Only the rows and columns the segment's own bounding box touches, not the whole ray disc.
            var minX = x0 + Math.Min(ax, bx) * sx - lw * sx - 1;
            var maxX = x0 + Math.Max(ax, bx) * sx + lw * sx + 1;
            var minY = y0 + Math.Min(ay, by) - lw - 1;
            var maxY = y0 + Math.Max(ay, by) + lw + 1;
            xa = Math.Max(xa, (int)Math.Floor(minX)); xb = Math.Min(xb, (int)Math.Ceiling(maxX));
            ya = Math.Max(ya, (int)Math.Floor(minY)); yb = Math.Min(yb, (int)Math.Ceiling(maxY));
            for (var y = ya; y <= yb; y++)
            {
                var ly = y + 0.5 - y0;
                for (var x = xa; x <= xb; x++)
                {
                    var lx = (x + 0.5 - x0) / sx;
                    double qx = lx - ax, qy = ly - ay;
                    var along = qx * ux + qy * uy;
                    if (along < 0 || along > segLen) continue;
                    var across = Math.Abs(qx * uy - qy * ux);
                    var cover = Math.Clamp(lw / 2 - across + 0.5, 0, 1);
                    if (cover > 0) Blend(x, y, rr, gg, bb, alpha * cover);
                }
            }
        }

        /// <summary>Stroke a circle of radius <paramref name="rad"/> with line width <paramref name="lw"/>.</summary>
        public void Ring(double rad, double lw, double rr, double gg, double bb, double alpha)
        {
            Box(rad + lw, out var xa, out var xb, out var ya, out var yb);
            for (var y = ya; y <= yb; y++)
            {
                var ly = y + 0.5 - y0;
                for (var x = xa; x <= xb; x++)
                {
                    var lx = (x + 0.5 - x0) / sx;
                    var d = Math.Sqrt(lx * lx + ly * ly);
                    var cover = Math.Clamp(lw / 2 - Math.Abs(d - rad) + 0.5, 0, 1);
                    if (cover > 0) Blend(x, y, rr, gg, bb, alpha * cover);
                }
            }
        }
    }
}
