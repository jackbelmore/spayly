namespace Spayly.Spikes.GpuDebris;

/// <summary>
/// GLSL compute sources, compiled at runtime (RDShaderSource → SPIR-V) so no .glsl import step exists.
/// Units: km, s, GM in km^3/s^2. Positions are float32 relative to the central body.
/// </summary>
static class Shaders
{
    // One key function for count, contacts and overlap counting: a mismatch would silently drop pairs.
    // blocked: 4x4x4 cells share 64 consecutive buckets and only the block index is hashed, so the 27 neighbour
    // lookups mostly hit one 256-byte span and consecutive sorted particles are spatial neighbours (warp coherence).
    const string Hash = """
        uint cell_hash(ivec3 c) {
          return (uint(c.x) * 73856093u) ^ (uint(c.y) * 19349663u) ^ (uint(c.z) * 83492791u);
        }
        uint cell_key(ivec3 c, uint mask, bool blocked) {
          if (!blocked) return cell_hash(c) & mask;
          uvec3 l = uvec3(c & 3);
          return ((cell_hash(c >> 2) << 6u) | l.x | (l.y << 2u) | (l.z << 4u)) & mask;
        }

        """;

    public const string Integrate = """
        #version 450
        layout(local_size_x = 256) in;
        layout(set = 0, binding = 0, std430) restrict buffer Pos { vec4 p[]; } pos;
        layout(set = 0, binding = 1, std430) restrict buffer Vel { vec4 v[]; } vel;
        // b0 = bodies at t, b1 = bodies at t+dt: the closing kick of KDK needs the moved bodies.
        layout(set = 0, binding = 2, std140) uniform Bodies { vec4 b0[16]; vec4 b1[16]; } bodies;
        layout(set = 0, binding = 3, rgba32f) uniform restrict writeonly image2D pos_tex;
        layout(push_constant, std430) uniform PC {
          uint n; float dt; uint n_bodies; float eps2;
          uint gravity; uint frame; uint tex_w; uint tex_h;
        } pc;

        vec3 accel(vec3 x, bool late) {
          vec3 a = vec3(0.0);
          for (uint k = 0u; k < pc.n_bodies; k++) {
            vec4 b = late ? bodies.b1[k] : bodies.b0[k];
            vec3 d = b.xyz - x;
            float r2 = dot(d, d) + pc.eps2;
            float inv = inversesqrt(r2);
            a += b.w * inv * inv * inv * d;
          }
          return a;
        }

        void main() {
          uint i = gl_GlobalInvocationID.x;
          if (i == 0u) {
            // Frame probe: the last texel is never a particle; the probe quad decodes it into bars.
            imageStore(pos_tex, ivec2(pc.tex_w - 1u, pc.tex_h - 1u), vec4(float(pc.frame), 0.0, 0.0, 1.0));
          }
          if (i >= pc.n) return;
          vec4 x = pos.p[i];
          vec4 v = vel.v[i];
          if (pc.gravity != 0u) v.xyz += 0.5 * pc.dt * accel(x.xyz, false);
          x.xyz += pc.dt * v.xyz;
          if (pc.gravity != 0u) v.xyz += 0.5 * pc.dt * accel(x.xyz, true);
          pos.p[i] = x;
          vel.v[i] = v;
          imageStore(pos_tex, ivec2(i % pc.tex_w, i / pc.tex_w), vec4(x.xyz, length(v.xyz)));
        }
        """;

    public const string Clear = """
        #version 450
        layout(local_size_x = 256) in;
        layout(set = 0, binding = 0, std430) restrict writeonly buffer Buf { uint d[]; } buf;
        layout(push_constant, std430) uniform PC { uint count; uint p0; uint p1; uint p2; } pc;
        void main() {
          uint i = gl_GlobalInvocationID.x;
          if (i < pc.count) buf.d[i] = 0u;
        }
        """;

    public const string Count = """
        #version 450
        layout(local_size_x = 256) in;
        layout(set = 0, binding = 0, std430) restrict readonly buffer Pos { vec4 p[]; } pos;
        layout(set = 0, binding = 1, std430) restrict buffer Cnt { uint c[]; } cnt;
        layout(set = 0, binding = 2, std430) restrict writeonly buffer CellRank { uvec2 cr[]; } cell_rank;
        layout(push_constant, std430) uniform PC { uint n; uint mask; float inv_cell; uint blocked; } pc;
        """ + Hash + """
        void main() {
          uint i = gl_GlobalInvocationID.x;
          if (i >= pc.n) return;
          ivec3 c = ivec3(floor(pos.p[i].xyz * pc.inv_cell));
          uint h = cell_key(c, pc.mask, pc.blocked != 0u);
          // The rank from the atomic is the slot inside the cell, so scatter needs no second atomic.
          uint r = atomicAdd(cnt.c[h], 1u);
          cell_rank.cr[i] = uvec2(h, r);
        }
        """;

    // Blelloch exclusive scan of 1024 elements per workgroup; the block total goes to sums[] for the next level.
    public const string ScanBlock = """
        #version 450
        layout(local_size_x = 512) in;
        layout(set = 0, binding = 0, std430) restrict readonly buffer In { uint d[]; } src;
        layout(set = 0, binding = 1, std430) restrict writeonly buffer Out { uint d[]; } dst;
        layout(set = 0, binding = 2, std430) restrict writeonly buffer Sums { uint d[]; } sums;
        layout(push_constant, std430) uniform PC { uint count; uint p0; uint p1; uint p2; } pc;
        shared uint s[1024];
        void main() {
          uint t = gl_LocalInvocationID.x;
          uint base = gl_WorkGroupID.x * 1024u;
          uint a = base + 2u * t;
          uint b = a + 1u;
          s[2u * t] = a < pc.count ? src.d[a] : 0u;
          s[2u * t + 1u] = b < pc.count ? src.d[b] : 0u;
          uint off = 1u;
          for (uint dd = 512u; dd > 0u; dd >>= 1u) {
            barrier();
            if (t < dd) {
              uint ai = off * (2u * t + 1u) - 1u;
              uint bi = off * (2u * t + 2u) - 1u;
              s[bi] += s[ai];
            }
            off <<= 1u;
          }
          barrier();
          if (t == 0u) {
            sums.d[gl_WorkGroupID.x] = s[1023];
            s[1023] = 0u;
          }
          for (uint dd = 1u; dd < 1024u; dd <<= 1u) {
            off >>= 1u;
            barrier();
            if (t < dd) {
              uint ai = off * (2u * t + 1u) - 1u;
              uint bi = off * (2u * t + 2u) - 1u;
              uint tmp = s[ai];
              s[ai] = s[bi];
              s[bi] += tmp;
            }
          }
          barrier();
          if (a < pc.count) dst.d[a] = s[2u * t];
          if (b < pc.count) dst.d[b] = s[2u * t + 1u];
        }
        """;

    public const string ScanAdd = """
        #version 450
        layout(local_size_x = 256) in;
        layout(set = 0, binding = 0, std430) restrict buffer Data { uint d[]; } data;
        layout(set = 0, binding = 1, std430) restrict readonly buffer Carry { uint d[]; } carry;
        layout(push_constant, std430) uniform PC { uint count; uint p0; uint p1; uint p2; } pc;
        void main() {
          uint i = gl_GlobalInvocationID.x;
          if (i < pc.count) data.d[i] += carry.d[i >> 10u];
        }
        """;

    public const string Scatter = """
        #version 450
        layout(local_size_x = 256) in;
        layout(set = 0, binding = 0, std430) restrict readonly buffer Pos { vec4 p[]; } pos;
        layout(set = 0, binding = 1, std430) restrict readonly buffer Vel { vec4 v[]; } vel;
        layout(set = 0, binding = 2, std430) restrict writeonly buffer PosB { vec4 p[]; } pos_b;
        layout(set = 0, binding = 3, std430) restrict writeonly buffer VelB { vec4 v[]; } vel_b;
        layout(set = 0, binding = 4, std430) restrict readonly buffer CellRank { uvec2 cr[]; } cell_rank;
        layout(set = 0, binding = 5, std430) restrict readonly buffer Start { uint s[]; } start;
        layout(push_constant, std430) uniform PC { uint n; uint p0; uint p1; uint p2; } pc;
        void main() {
          uint i = gl_GlobalInvocationID.x;
          if (i >= pc.n) return;
          uvec2 hr = cell_rank.cr[i];
          uint dst = start.s[hr.x] + hr.y;
          pos_b.p[dst] = pos.p[i];
          vel_b.v[dst] = vel.v[i];
        }
        """;

    // Jacobi contact pass over the sorted copy: each particle sums its own share of every pair impulse, reading only
    // pre-contact velocities, so pair (i,j) contributes exactly opposite momenta. Writes back in sorted order.
    public const string Contacts = """
        #version 450
        layout(local_size_x = 256) in;
        layout(set = 0, binding = 0, std430) restrict readonly buffer PosB { vec4 p[]; } pos_b;
        layout(set = 0, binding = 1, std430) restrict readonly buffer VelB { vec4 v[]; } vel_b;
        layout(set = 0, binding = 2, std430) restrict writeonly buffer Pos { vec4 p[]; } pos;
        layout(set = 0, binding = 3, std430) restrict writeonly buffer Vel { vec4 v[]; } vel;
        layout(set = 0, binding = 4, std430) restrict readonly buffer Start { uint s[]; } start;
        layout(set = 0, binding = 5, std430) restrict readonly buffer Cnt { uint c[]; } cnt;
        layout(set = 0, binding = 6, std430) restrict buffer Stats { uint s[]; } stats;
        layout(push_constant, std430) uniform PC { uint n; uint mask; float inv_cell; uint mode; } pc;
        """ + Hash + """
        void main() {
          uint i = gl_GlobalInvocationID.x;
          if (i >= pc.n) return;
          vec4 pi = pos_b.p[i];
          vec4 vi = vel_b.v[i];
          float ri = pi.w;
          float mi = ri * ri * ri;
          ivec3 c = ivec3(floor(pi.xyz * pc.inv_cell));
          uint seen[27];
          uint n_seen = 0u;
          vec3 dv = vec3(0.0);
          uint overlaps = 0u;
          uint hits = 0u;
          for (int dz = -1; dz <= 1; dz++)
          for (int dy = -1; dy <= 1; dy++)
          for (int dx = -1; dx <= 1; dx++) {
            uint h = cell_key(c + ivec3(dx, dy, dz), pc.mask, (pc.mode & 2u) != 0u);
            // Two neighbour cells can hash to one bucket; visiting it twice would apply its impulses twice.
            bool dup = false;
            for (uint k = 0u; k < n_seen; k++) dup = dup || seen[k] == h;
            if (dup) continue;
            seen[n_seen++] = h;
            uint s0 = start.s[h];
            uint s1 = s0 + cnt.c[h];
            for (uint j = s0; j < s1; j++) {
              if (j == i) continue;
              vec4 pj = pos_b.p[j];
              vec3 d = pj.xyz - pi.xyz;
              float rs = ri + pj.w;
              float d2 = dot(d, d);
              if (d2 >= rs * rs || d2 <= 0.0) continue;
              if (j > i) overlaps++;
              if ((pc.mode & 1u) != 0u) continue;
              vec3 nrm = d * inversesqrt(d2);
              float vn = dot(vel_b.v[j].xyz - vi.xyz, nrm);
              if (vn < 0.0) {
                float mj = pj.w * pj.w * pj.w;
                dv += (2.0 * mj / (mi + mj)) * vn * nrm;
                hits++;
              }
            }
          }
          if ((pc.mode & 1u) != 0u) {
            if (overlaps > 0u) atomicAdd(stats.s[0], overlaps);
            return;
          }
          pos.p[i] = pi;
          vel.v[i] = vec4(vi.xyz + dv, vi.w);
        }
        """;

    // Route b: write Godot's 3D multimesh layout (3x4 row-major basis|origin, then RGBA) straight into its RD buffer.
    public const string MultimeshWrite = """
        #version 450
        layout(local_size_x = 256) in;
        layout(set = 0, binding = 0, std430) restrict readonly buffer Pos { vec4 p[]; } pos;
        layout(set = 0, binding = 1, std430) restrict readonly buffer Vel { vec4 v[]; } vel;
        layout(set = 0, binding = 2, std430) restrict writeonly buffer MM { float f[]; } mm;
        layout(push_constant, std430) uniform PC { uint n; float scale; uint stride; uint frame; } pc;
        void main() {
          uint i = gl_GlobalInvocationID.x;
          if (i >= pc.n) return;
          vec3 x = pos.p[i].xzy * vec3(1.0, 1.0, -1.0) * pc.scale; // sim z-up → Godot y-up
          float spd = length(vel.v[i].xyz);
          uint o = i * pc.stride;
          mm.f[o + 0u] = 1.0; mm.f[o + 1u] = 0.0; mm.f[o + 2u] = 0.0; mm.f[o + 3u] = x.x;
          mm.f[o + 4u] = 0.0; mm.f[o + 5u] = 1.0; mm.f[o + 6u] = 0.0; mm.f[o + 7u] = x.y;
          mm.f[o + 8u] = 0.0; mm.f[o + 9u] = 0.0; mm.f[o + 10u] = 1.0; mm.f[o + 11u] = x.z;
          float k = clamp((spd - 4.0) / 4.0, 0.0, 1.0);
          vec3 col = mix(vec3(0.35, 0.55, 1.0), vec3(1.0, 0.65, 0.3), k);
          mm.f[o + 12u] = col.r; mm.f[o + 13u] = col.g; mm.f[o + 14u] = col.b; mm.f[o + 15u] = 1.0;
        }
        """;

    // --- spatial shaders (Godot shading language) ---

    public const string PointsRouteA = """
        shader_type spatial;
        render_mode unshaded, blend_add, depth_draw_never, cull_disabled, shadows_disabled, fog_disabled;
        uniform sampler2D pos_tex : filter_nearest;
        uniform int tex_w;
        uniform float render_scale;
        uniform float point_size;
        uniform float intensity;
        varying vec3 col;
        void vertex() {
          vec4 t = texelFetch(pos_tex, ivec2(VERTEX_ID % tex_w, VERTEX_ID / tex_w), 0);
          VERTEX = vec3(t.x, t.z, -t.y) * render_scale; // sim is z-up, Godot is y-up
          POINT_SIZE = point_size;
          float k = clamp((t.w - 4.0) / 4.0, 0.0, 1.0);
          col = mix(vec3(0.35, 0.55, 1.0), vec3(1.0, 0.65, 0.3), k) * intensity;
        }
        void fragment() {
          ALBEDO = col;
        }
        """;

    public const string PointsRouteB = """
        shader_type spatial;
        render_mode unshaded, blend_add, depth_draw_never, cull_disabled, shadows_disabled, fog_disabled;
        uniform float point_size;
        uniform float intensity;
        void vertex() {
          POINT_SIZE = point_size;
        }
        void fragment() {
          ALBEDO = COLOR.rgb * intensity;
        }
        """;

    // 16 bars per row, bar k white when bit k is set. Top row: frame number the CPU set as a uniform this process
    // frame (what the draw belongs to). Bottom row: frame number the integrate compute wrote into the probe texel.
    public const string Probe = """
        shader_type spatial;
        render_mode unshaded, cull_disabled, shadows_disabled, fog_disabled;
        uniform sampler2D pos_tex : filter_nearest;
        uniform int tex_w;
        uniform int tex_h;
        uniform int cpu_frame;
        void fragment() {
          int gpu_frame = int(texelFetch(pos_tex, ivec2(tex_w - 1, tex_h - 1), 0).x + 0.5);
          int counter = UV.y < 0.5 ? cpu_frame : gpu_frame;
          int bit = int(floor(UV.x * 16.0));
          ALBEDO = ((counter >> bit) & 1) != 0 ? vec3(1.0) : vec3(0.0);
        }
        """;
}
