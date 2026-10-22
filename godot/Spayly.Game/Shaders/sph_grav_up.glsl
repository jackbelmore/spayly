#[compute]
#version 450
// Gravity pyramid, pass 5 (one dispatch per level, finest-1 up to the root; pc.count = the level being built): each node
// sums its 8 children, moving their quadrupoles to its own centre of mass by the parallel-axis shift (Octree.Moments).
#include "sph_common.glslinc"
layout(local_size_x = 256) in;
layout(set = 0, binding = 0, std430) restrict buffer Nodes { vec4 d[]; } nodes;

void main() {
  uint l = pc.count;
  uint code = gl_GlobalInvocationID.x;
  if (code >= (1u << (3u * l))) return;
  uint first = level_offset(l + 1u) + (code << 3u);
  float m = 0.0;
  vec3 sx = vec3(0.0);
  for (uint c = 0u; c < 8u; c++) {
    vec4 n0 = nodes.d[3u * (first + c)];
    m += n0.w;
    sx += n0.w * n0.xyz;
  }
  float side = 2.0 * pc.g_box / float(1u << l);
  vec3 centre = vec3(-pc.g_box) + (vec3(demorton3(code)) + 0.5) * side;
  uint node = level_offset(l) + code;
  if (m <= 0.0) {
    nodes.d[3u * node] = vec4(centre, 0.0);
    nodes.d[3u * node + 1u] = vec4(0.0);
    nodes.d[3u * node + 2u] = vec4(0.0);
    return;
  }
  vec3 cm = sx / m;
  vec4 q1 = vec4(0.0);
  vec2 q2 = vec2(0.0);
  for (uint c = 0u; c < 8u; c++) {
    uint k = first + c;
    vec4 n0 = nodes.d[3u * k];
    if (n0.w <= 0.0) continue;
    vec3 d = n0.xyz - cm;
    float r2 = dot(d, d);
    vec4 a = nodes.d[3u * k + 1u];
    vec4 b = nodes.d[3u * k + 2u];
    q1 += a + n0.w * vec4(3.0 * d.x * d.x - r2, 3.0 * d.y * d.y - r2, 3.0 * d.z * d.z - r2, 3.0 * d.x * d.y);
    q2 += b.xy + n0.w * vec2(3.0 * d.x * d.z, 3.0 * d.y * d.z);
  }
  nodes.d[3u * node] = vec4(cm, m);
  nodes.d[3u * node + 1u] = q1;
  nodes.d[3u * node + 2u] = vec4(q2, 0.0, 0.0);
}
