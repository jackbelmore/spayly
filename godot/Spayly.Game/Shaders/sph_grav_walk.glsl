#[compute]
#version 450
// Gravity pyramid, pass 6: Barnes-Hut walk of the complete octree (Octree.Accel's rules). A node is accepted when
// d > s/theta + delta (Barnes 1994), with softened monopole + quadrupole; an unaccepted leaf is summed particle by
// particle; otherwise its 8 children go on a per-thread stack. Node masses already carry G. Adds to acc.xyz.
#include "sph_common.glslinc"
layout(local_size_x = WG) in;
layout(set = 0, binding = 0, std430) restrict readonly buffer PosH { vec4 d[]; } posh;
layout(set = 0, binding = 1, std430) restrict buffer Acc { vec4 d[]; } acc;
layout(set = 0, binding = 2, std430) restrict buffer Aux { vec4 d[]; } aux;
layout(set = 0, binding = 3, std430) restrict readonly buffer Nodes { vec4 d[]; } nodes;
layout(set = 0, binding = 4, std430) restrict readonly buffer GPosM { vec4 d[]; } gposm;
layout(set = 0, binding = 5, std430) restrict readonly buffer GStart { uint d[]; } gstart;
layout(set = 0, binding = 6, std430) restrict readonly buffer GCnt { uint d[]; } gcnt;

void main() {
  uint i = gl_GlobalInvocationID.x;
  if (i >= pc.n) return;
  vec3 p = posh.d[i].xyz;
  vec3 g = vec3(0.0);
  // Entries pack (level << 24) | Morton code; the deepest possible stack is 1 + 7 * levels.
  uint stack[64];
  int sp = 0;
  stack[sp++] = 0u;
  float inv_theta = 1.0 / pc.theta;
  while (sp > 0) {
    uint e = stack[--sp];
    uint l = e >> 24u;
    uint code = e & 0x00FFFFFFu;
    uint node = level_offset(l) + code;
    vec4 n0 = nodes.d[3u * node];
    if (n0.w <= 0.0) continue;
    vec3 d = p - n0.xyz;
    float r2 = dot(d, d);
    float side = 2.0 * pc.g_box / float(1u << l);
    vec3 centre = vec3(-pc.g_box) + (vec3(demorton3(code)) + 0.5) * side;
    float lim = side * inv_theta + length(n0.xyz - centre);
    if (r2 > lim * lim) {
      // In u = d / r_s rather than d: in SI, d.Q.d reaches ~1e42 (past float32's 3.4e38) while r_s^-7 falls to ~1e-49
      // (flushed to 0), and inf * 0 made every accepted node NaN. Same terms, every factor now within range.
      float inv = inversesqrt(r2 + pc.eps2);
      float inv2 = inv * inv;
      float inv4 = inv2 * inv2;
      vec3 u = d * inv;
      vec4 qa = nodes.d[3u * node + 1u];
      vec4 qb = nodes.d[3u * node + 2u];
      vec3 qu = vec3(qa.x * u.x + qa.w * u.y + qb.x * u.z,
                     qa.w * u.x + qa.y * u.y + qb.y * u.z,
                     qb.x * u.x + qb.y * u.y + qa.z * u.z);
      float uqu = dot(u, qu);
      g += -n0.w * inv2 * u + (qu - 2.5 * uqu * u) * inv4;
    } else if (l == pc.levels) {
      uint s0 = gstart.d[code];
      uint s1 = s0 + gcnt.d[code];
      for (uint j = s0; j < s1; j++) {
        vec4 q = gposm.d[j];
        vec3 e3 = p - q.xyz;
        float inv = inversesqrt(dot(e3, e3) + pc.eps2);
        g -= (q.w * inv * inv * inv) * e3;
      }
    } else if (sp <= 56) {
      uint child = ((l + 1u) << 24u) | (code << 3u);
      for (uint c = 0u; c < 8u; c++) stack[sp++] = child | c;
    }
  }
  vec4 a = acc.d[i];
  acc.d[i] = vec4(a.xyz + g, a.w);
  aux.d[i].z = length(g);
}
