namespace Spayly.Game.Render;

/// <summary>
/// GLSL compute sources of <see cref="LensingPass"/>, compiled at run time (RDShaderSource → SPIR-V) as spike M1a's are: a
/// CompositorEffect cannot run a .gdshader, and a .glsl file in the project would need an import step the capture harness
/// only runs on a fresh checkout.
///
/// The lens kernel is Bruneton 2020's method (arXiv:2010.08735; BSD-licensed black_hole_shader) re-derived and written
/// from the equations, not copied: see godot/CREDITS.md. Its table arithmetic is mirrored line for line by
/// <see cref="Spayly.Sim.LensingTables.Trace"/> and <see cref="Spayly.Sim.LensingTables.OrbitU"/>, which the Sim tests hold to
/// an RK4 reference; change one, change the other.
/// </summary>
static class LensingShader
{
    /// <summary>Copies the colour buffer so the lens kernel can read any texel while it writes its own.</summary>
    public const string Copy = """
        #version 450
        layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;
        layout(rgba16f, set = 0, binding = 0) uniform restrict readonly image2D src;
        layout(rgba16f, set = 0, binding = 1) uniform restrict writeonly image2D dst;
        layout(push_constant, std430) uniform PC { vec2 size; vec2 pad; } pc;
        void main() {
          ivec2 p = ivec2(gl_GlobalInvocationID.xy);
          if (p.x >= int(pc.size.x) || p.y >= int(pc.size.y)) return;
          imageStore(dst, p, imageLoad(src, p));
        }
        """;

    /// <summary>
    /// Per pixel and per hole: the ray's angle ψ from the hole and the static observer's e = u_c√(1 − u_c)/sin ψ; the tables
    /// give the angle Φ it sweeps round the hole before escaping (or reaching the horizon) and u along the way. The escape
    /// direction cos Φ e_r + sin Φ e_t re-samples the frame (thin-lens scaled for a source at finite depth) or the sky
    /// when it leaves the screen; a captured ray is black; plane crossings at the nodes φ_node + kπ that land between the
    /// ISCO and the outer radius shade the disc front to back. Several holes superpose their deflections [ART].
    /// Units: lengths inside a hole's block in that hole's r_s, u = r_s/r; vectors in the camera-relative render frame.
    /// </summary>
    public const string Lens = """
        #version 450
        layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

        layout(rgba16f, set = 0, binding = 0) uniform restrict writeonly image2D color_out;
        layout(set = 0, binding = 1) uniform sampler2D scene;
        layout(set = 0, binding = 2) uniform sampler2D depth_tex;
        layout(set = 0, binding = 3) uniform sampler2D esc_tab;
        layout(set = 0, binding = 4) uniform sampler2D cap_tab;
        layout(set = 0, binding = 5) uniform sampler2D sky_map;
        layout(set = 0, binding = 6) uniform sampler2D disc_lut;

        struct Hole {
          vec4 pos;    // xyz unit direction camera -> hole; w camera distance in r_s (true, unsqueezed)
          vec4 geo;    // x r_s in render units; y squeezed distance, render units; z disc inner, w disc outer radius (r_s)
          vec4 disc_n; // xyz disc normal (prograde pole); w disc brightness, 0 without a disc
          vec4 disc_x; // xyz first in-plane axis of the disc; w display temperature at the profile peak, K
          vec4 misc;   // x true distance, render units; y squeeze factor of the hole's position; zw unused
        };

        layout(set = 0, binding = 7, std140) uniform Params {
          Hole holes[4];
          vec4 cam_x;  // camera basis columns: view -> render frame
          vec4 cam_y;
          vec4 cam_z;
          vec4 gal_x;  // Milky Way sphere basis (Sky.cs)
          vec4 gal_y;
          vec4 gal_z;
        } P;

        layout(push_constant, std430) uniform PC {
          vec2 size; float p00; float p11;
          float near; float far; float squeeze; float ppr;
          float exposure; float time; float count; float flags;
        } pc;

        const float PI = 3.14159265358979;
        const float MU = 4.0 / 27.0;
        const float SQRT_MU = 0.38490017945975050;
        const float B_CRIT = 2.59807621135331600;
        const float X_MAX = 0.999999;

        // Bilinear on texel centres at x = i/(w-1), y = j/(h-1), done by hand in float32: hardware filtering keeps only
        // ~8 bits of the weight, which would quantise weak-field bending into visible steps.
        vec4 tab(sampler2D t, vec2 xy) {
          ivec2 n = textureSize(t, 0);
          vec2 f = clamp(xy, 0.0, 1.0) * vec2(n - 1);
          ivec2 i = min(ivec2(f), n - 2);
          vec2 w = f - vec2(i);
          vec4 a = texelFetch(t, i, 0);
          vec4 b = texelFetch(t, i + ivec2(1, 0), 0);
          vec4 c = texelFetch(t, i + ivec2(0, 1), 0);
          vec4 d = texelFetch(t, i + ivec2(1, 1), 0);
          return mix(mix(a, b, w.x), mix(c, d, w.x), w.y);
        }

        // Periapsis of a ray from infinity: the series below e = 0.03, where asin near -1 loses float32 digits; else the
        // trigonometric root of u^3 - u^2 + e^2 = 0 with one Newton step away from the photon sphere (BlackHole.PeriapsisU).
        float periapsis_u(float e) {
          if (e < 0.03) return e * (1.0 + e * (0.5 + e * (0.625 + e * (1.0 + e * (231.0 / 128.0)))));
          float u = 1.0 / 3.0 + 2.0 / 3.0 * sin(asin(clamp(2.0 * e * e / MU - 1.0, -1.0, 1.0)) / 3.0);
          float d = u * (2.0 - 3.0 * u);
          if (d > 0.05) u -= (u * u * (1.0 - u) - e * e) / d;
          return u;
        }

        struct Ray { bool escapes; bool esc_orbit; bool inward; float swept; float x; float th_c; float th_max; float ua; };

        // LensingTables.Trace.
        Ray trace(float uc, float cp, float sp) {
          Ray r;
          r.inward = cp > 0.0;
          r.escapes = false; r.esc_orbit = false; r.swept = 0.0; r.x = 0.0; r.th_c = 0.0; r.th_max = 0.0; r.ua = 1.0;
          float e = uc * sqrt(1.0 - uc) / max(sp, 1e-9);
          if (e * e < MU) {
            float x = min(1.0 - sqrt(max(1.0 - e / SQRT_MU, 0.0)), X_MAX);
            float phia = tab(esc_tab, vec2(x, 0.0)).b - log(1.0 - x);
            float ua = periapsis_u(e);
            // Inside the photon sphere on the inner branch: it falls in without reaching the disc.
            if (uc > ua * 1.000001) return r;
            // t_c^2 = 1 - uc/ua, in a form that keeps its digits for a ray grazing the camera's radius.
            float den = ua + uc - (ua * ua + ua * uc + uc * uc);
            float tc = min(1.0, e * abs(cp) / sqrt(max(ua * den, 1e-30)));
            float fc = tab(esc_tab, vec2(x, 1.0 - tc)).r * phia;
            r.escapes = true; r.esc_orbit = true; r.x = x; r.th_max = phia; r.ua = ua;
            r.th_c = r.inward ? fc : 2.0 * phia - fc;
            r.swept = r.inward ? 2.0 * phia - fc : fc;
            return r;
          }
          float x = min(1.0 - sqrt(max(1.0 - 1.0 / (e * B_CRIT), 0.0)), X_MAX);
          float phih = tab(cap_tab, vec2(x, 0.0)).b - 2.0 * log(1.0 - x);
          float f = tab(cap_tab, vec2(x, uc)).r * phih;
          r.x = x; r.th_max = phih; r.th_c = f;
          r.escapes = !r.inward;
          r.swept = r.inward ? phih - f : f;
          return r;
        }

        // LensingTables.OrbitU: u at swept angle s from the camera.
        float orbit_u(Ray r, float s) {
          if (r.esc_orbit) {
            float th = r.th_c + s;
            if (th > r.th_max) th = 2.0 * r.th_max - th;
            return r.ua * tab(esc_tab, vec2(r.x, th / r.th_max)).g;
          }
          float th = r.inward ? r.th_c + s : r.th_c - s;
          return tab(cap_tab, vec2(r.x, th / max(r.th_max, 1e-9))).g;
        }

        // common.gdshaderinc's hash3 / vnoise / fbm, so the lensed disc and the flat one (disc.gdshader) share their pattern.
        float hash3(vec3 p) {
          p = fract(p * 0.3183099 + vec3(0.1, 0.2, 0.3));
          p *= 17.0;
          return fract(p.x * p.y * p.z * (p.x + p.y + p.z));
        }
        float vnoise(vec3 p) {
          vec3 i = floor(p);
          vec3 f = fract(p);
          f = f * f * (3.0 - 2.0 * f);
          return mix(
            mix(mix(hash3(i + vec3(0, 0, 0)), hash3(i + vec3(1, 0, 0)), f.x), mix(hash3(i + vec3(0, 1, 0)), hash3(i + vec3(1, 1, 0)), f.x), f.y),
            mix(mix(hash3(i + vec3(0, 0, 1)), hash3(i + vec3(1, 0, 1)), f.x), mix(hash3(i + vec3(0, 1, 1)), hash3(i + vec3(1, 1, 1)), f.x), f.y),
            f.z);
        }
        float fbm(vec3 p) {
          float v = 0.0;
          float a = 0.5;
          for (int i = 0; i < 5; i++) {
            v += a * vnoise(p);
            p = p * 2.03 + vec3(1.7, 9.2, 3.1);
            a *= 0.5;
          }
          return v;
        }

        vec3 blackbody(float t) {
          float n = float(textureSize(disc_lut, 0).x);
          float s = clamp(log(max(t, 1.0) / 1000.0) / log(40.0), 0.0, 1.0);
          return textureLod(disc_lut, vec2((s * (n - 1.0) + 0.5) / n, 0.5), 0.0).rgb;
        }

        // [ART] Turbulence advected at the Keplerian rate (r_in/r)^1.5, slowed to a watchable 0.6 rad/s at the ISCO. Two
        // layers restart half a period apart and cross-fade, so the shear never winds the pattern into hairlines.
        // disc.gdshader has the same function.
        float turbulence(float r, float az, float rin) {
          float w = 0.6 * pow(rin / r, 1.5);
          const float T = 8.0;
          float ph0 = fract(pc.time / T);
          float ph1 = fract(pc.time / T + 0.5);
          float n0 = fbm(vec3(cos(az - w * ph0 * T) * 2.5, sin(az - w * ph0 * T) * 2.5, log(r) * 7.0));
          float n1 = fbm(vec3(cos(az - w * ph1 * T) * 2.5, sin(az - w * ph1 * T) * 2.5, log(r) * 7.0 + 5.3));
          return mix(n1, n0, 1.0 - abs(2.0 * ph0 - 1.0));
        }

        // Emission and opacity where the ray crosses the disc at p (r_s, hole-centred). [SCI] Shakura-Sunyaev T(r) with the
        // bolometric (T/T_peak)^4; with Doppler on, g = sqrt(1 - 3M/r)/(1 - Omega b_z) for the circular emitter, over
        // sqrt(1 - u_c) for the observer at rest, scales colour temperature by g and brightness by g^4. [GAME] The display
        // temperature is the profile scaled to disc_x.w at its peak: a stellar-mass disc is ~1e7 K and would be X-ray white.
        vec4 disc_emission(Hole h, vec3 p, float rr, float u, float bz, float uc, bool doppler) {
          float rin = h.geo.z, rout = h.geo.w;
          float rp = 49.0 / 36.0 * rin;
          float f = max((1.0 - sqrt(rin / rr)) / (rr * rr * rr), 0.0) / ((1.0 - sqrt(rin / rp)) / (rp * rp * rp));
          float g = 1.0;
          if (doppler) g = sqrt(max(1.0 - 1.5 * u, 1e-4)) / max(1.0 - sqrt(0.5 * u * u * u) * bz, 1e-3) / sqrt(1.0 - uc);
          vec3 X = h.disc_x.xyz;
          vec3 Y = cross(h.disc_n.xyz, X);
          float n = turbulence(rr, atan(dot(p, Y), dot(p, X)), rin);
          float g2 = g * g;
          vec3 c = blackbody(h.disc_x.w * pow(f, 0.25) * g) * (h.disc_n.w * f * g2 * g2 * mix(0.35, 1.45, n) * pc.exposure);
          float alpha = smoothstep(rin, rin * 1.1, rr) * (1.0 - smoothstep(0.75 * rout, rout, rr)) * mix(0.55, 1.0, n);
          return vec4(c, alpha);
        }

        vec3 star_field(vec3 d) {
          // One candidate star per cell of a 40 x 40 cube map, ~9100 in all like Sky.cs's 9000, drawn like points.gdshader.
          vec3 a = abs(d);
          float face;
          vec2 st;
          if (a.x >= a.y && a.x >= a.z) { face = d.x > 0.0 ? 0.0 : 1.0; st = d.yz / a.x; }
          else if (a.y >= a.z) { face = d.y > 0.0 ? 2.0 : 3.0; st = d.xz / a.y; }
          else { face = d.z > 0.0 ? 4.0 : 5.0; st = d.xy / a.z; }
          const float N = 40.0;
          vec2 cell = floor((st * 0.5 + 0.5) * N);
          vec3 key = vec3(cell, face * 131.0);
          if (hash3(key) > 0.95) return vec3(0.0);
          vec2 sp = (cell + 0.15 + 0.7 * vec2(hash3(key + 11.0), hash3(key + 23.0))) / N * 2.0 - 1.0;
          float sgn = mod(face, 2.0) < 0.5 ? 1.0 : -1.0;
          vec3 sd = face < 1.5 ? vec3(sgn, sp.x, sp.y) : face < 3.5 ? vec3(sp.x, sgn, sp.y) : vec3(sp.x, sp.y, sgn);
          sd = normalize(sd);
          float size = 0.9 + 2.4 * pow(hash3(key + 37.0), 9.0);
          float q = length(cross(d, sd)) * pc.ppr / size;
          float w = smoothstep(0.25, 0.05, q * q);
          if (w <= 0.0) return vec3(0.0);
          float temp = 3200.0 + 9000.0 * pow(hash3(key + 53.0), 2.2);
          return blackbody(temp) * (0.35 + 0.65 * pow(hash3(key + 71.0), 1.5)) * w * pc.exposure;
        }

        vec3 milky_way(vec3 d) {
          vec3 l = vec3(dot(d, P.gal_x.xyz), dot(d, P.gal_y.xyz), dot(d, P.gal_z.xyz));
          float v = acos(clamp(l.y, -1.0, 1.0)) / PI;
          if ((int(pc.flags) & 4) != 0) {
            // sky.gdshader's mapping (three.js SphereGeometry u, v) and its 0.55 x exposure compensation.
            float u = fract(atan(l.z, -l.x) / (2.0 * PI));
            float w = float(textureSize(sky_map, 0).x);
            float lod = max(0.0, log2(w / (2.0 * PI * pc.ppr)));
            return textureLod(sky_map, vec2(u, v), lod).rgb * 0.55 * pc.exposure;
          }
          // No map: the procedural band's mean glow (textures.ts proceduralMilkyWay), without its clouds.
          float band = exp(-pow((v - 0.5) / 0.09, 2.0));
          return vec3(0.95, 0.9, 1.05) * band * 0.025 * 0.55 * pc.exposure;
        }

        vec3 sky(vec3 d) { return milky_way(d) + star_field(d); }

        // Reverse-Z: depth 1 at the near plane, 0 at the far plane (the clear value, where the sky writes nothing).
        float view_z(vec2 uv) {
          float dz = textureLod(depth_tex, uv, 0.0).r;
          return pc.near * pc.far / (pc.near + dz * (pc.far - pc.near));
        }

        // FloatingOrigin.Squeeze inverted: d_sq = s (1 + 0.1 ln(d / s)) beyond s.
        float unsqueeze(float d) {
          float s = pc.squeeze;
          return d <= s ? d : s * exp(min((d / s - 1.0) * 10.0, 80.0));
        }

        bool project(vec3 dw, out vec2 uv, out vec3 v) {
          v = vec3(dot(dw, P.cam_x.xyz), dot(dw, P.cam_y.xyz), dot(dw, P.cam_z.xyz));
          uv = vec2(0.0);
          if (v.z > -1e-6) return false;
          vec2 ndc = vec2(pc.p00 * v.x, pc.p11 * v.y) / -v.z;
          uv = vec2(ndc.x * 0.5 + 0.5, 0.5 - ndc.y * 0.5);
          return uv.x >= 0.0 && uv.x <= 1.0 && uv.y >= 0.0 && uv.y <= 1.0;
        }

        // What the deflected ray sees: the unlensed frame where it lands on screen, else the sky. A source at finite
        // depth D_s behind a hole at D_l is bent by D_ls/D_s of the far-field angle (thin lens; one fixed-point step on the
        // depth found along the far-field direction). Where the frame there shows something in front of the dominant
        // hole, what lies behind it was never rendered, so the sky stands in.
        vec3 background(vec3 d, vec3 dev[4], float dl[4]) {
          vec3 dir = normalize(d + dev[0] + dev[1] + dev[2] + dev[3]);
          vec2 suv;
          vec3 sv;
          if (!project(dir, suv, sv)) return sky(dir);
          float sz = view_z(suv);
          if (sz > 0.85 * pc.far) return textureLod(scene, suv, 0.0).rgb;
          float ds = unsqueeze(sz / max(-sv.z, 1e-6));
          vec3 dev2 = vec3(0.0);
          int dom = 0;
          float big = -1.0;
          for (int i = 0; i < 4; i++) {
            float m = length(dev[i]);
            if (m > big) { big = m; dom = i; }
            if (dl[i] > 0.0) dev2 += dev[i] * clamp((ds - dl[i]) / ds, 0.0, 1.0);
          }
          if (ds < dl[dom]) return sky(dir);
          vec3 dir2 = normalize(d + dev2);
          if (!project(dir2, suv, sv)) return sky(dir2);
          return textureLod(scene, suv, 0.0).rgb;
        }

        void main() {
          ivec2 px = ivec2(gl_GlobalInvocationID.xy);
          if (px.x >= int(pc.size.x) || px.y >= int(pc.size.y)) return;
          vec2 uv = (vec2(px) + 0.5) / pc.size;
          vec3 rv = normalize(vec3((uv.x * 2.0 - 1.0) / pc.p00, (1.0 - uv.y * 2.0) / pc.p11, -1.0));
          vec3 d = normalize(P.cam_x.xyz * rv.x + P.cam_y.xyz * rv.y + P.cam_z.xyz * rv.z);
          float z = view_z(uv);
          bool sky_px = z > 0.85 * pc.far;
          float dist = z / max(-rv.z, 1e-6);
          int flags = int(pc.flags);
          bool disc_on = (flags & 1) != 0;
          bool doppler = (flags & 2) != 0;
          float px_rad = 1.0 / pc.ppr;

          vec3 dev[4];
          float dl[4];
          bool touched = false;
          bool shadow = false;
          vec3 glow = vec3(0.0);
          float trans = 1.0;
          int n = int(pc.count);
          for (int i = 0; i < 4; i++) {
            dev[i] = vec3(0.0);
            dl[i] = 0.0;
            if (i >= n) continue;
            Hole h = P.holes[i];
            float rin = h.geo.z;
            float rout = h.geo.w;
            // A pixel whose surface is in front of this hole (and its disc) is not bent by it.
            if (!sky_px && dist < h.geo.y - rout * h.geo.x * h.misc.y) continue;
            float rc = h.pos.w;
            float uc = min(1.0 / rc, 0.98);
            float cp = dot(d, h.pos.xyz);
            vec3 perp = d - cp * h.pos.xyz;
            float sp = length(perp);
            float e = uc * sqrt(1.0 - uc) / max(sp, 1e-9);
            bool inward = cp > 0.0;
            // Weak-field bound on the bend (2/b inward, ~2 u_c outward) and on the closest approach: skip the lookups for
            // a ray this hole moves by less than a tenth of a pixel and that never comes near its disc.
            float bend = inward ? 2.0 * e : 2.0 * uc;
            float closest = inward ? 1.0 / e : rc;
            bool disc_reach = disc_on && h.disc_n.w > 0.0 && closest < rout * 1.2;
            if (bend < 0.1 * px_rad && !disc_reach) continue;
            dl[i] = h.misc.x;
            touched = true;
            Ray r = trace(uc, cp, sp);
            vec3 er = -h.pos.xyz;
            vec3 et = sp > 1e-7 ? perp / sp : normalize(cross(er, abs(er.y) < 0.9 ? vec3(0.0, 1.0, 0.0) : vec3(1.0, 0.0, 0.0)));
            if (disc_reach && r.swept > 0.0 && trans > 0.01) {
              vec3 N = h.disc_n.xyz;
              float A = dot(er, N);
              float B = dot(et, N);
              if (abs(A) + abs(B) > 1e-6) {
                // Nodes of the ray's plane on the disc plane: A cos s + B sin s = 0, first one in [0, pi).
                float s0 = atan(-A, B);
                if (s0 < 0.0) s0 += PI;
                // The photon travels camera-ward, against the traced direction: its angular momentum is -b (e_r x e_t).
                float bz = -dot(cross(er, et), N) / e;
                for (int k = 0; k < 4; k++) {
                  float s = s0 + float(k) * PI;
                  if (s > r.swept || trans < 0.01) break;
                  float u = orbit_u(r, s);
                  float rr = 1.0 / max(u, 1e-6);
                  if (rr < rin || rr > rout) continue;
                  vec4 em = disc_emission(h, rr * (cos(s) * er + sin(s) * et), rr, u, bz, uc, doppler);
                  glow += trans * em.a * em.rgb;
                  trans *= 1.0 - em.a;
                }
              }
            }
            if (!r.escapes) {
              shadow = true;
              continue;
            }
            dev[i] = cos(r.swept) * er + sin(r.swept) * et - d;
          }
          if (!touched) return;
          vec3 total = dev[0] + dev[1] + dev[2] + dev[3];
          if (!shadow && trans > 0.999 && length(total) < 0.1 * px_rad) return;
          vec3 bg = shadow ? vec3(0.0) : background(d, dev, dl);
          imageStore(color_out, px, vec4(glow + trans * bg, 1.0));
        }
        """;
}
