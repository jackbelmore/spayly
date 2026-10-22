#[compute]
#version 450
// Gravity pyramid, pass 4: one thread per leaf sums its particles' G m, centre of mass and traceless quadrupole
// Q_ab = sum m (3 d_a d_b - |d|^2 delta_ab) about that centre (Octree.cs). Offsets are taken from the leaf centre first so
// float32 does not cancel. Node layout: 3 vec4 = (cm, G M), (G Qxx, G Qyy, G Qzz, G Qxy), (G Qxz, G Qyz, 0, 0).
#include "sph_common.glslinc"
layout(local_size_x = 256) in;
layout(set = 0, binding = 0, std430) restrict readonly buffer GPosM { vec4 d[]; } gposm;
layout(set = 0, binding = 1, std430) restrict readonly buffer GStart { uint d[]; } gstart;
layout(set = 0, binding = 2, std430) restrict readonly buffer GCnt { uint d[]; } gcnt;
layout(set = 0, binding = 3, std430) restrict writeonly buffer Nodes { vec4 d[]; } nodes;

void main() {
  uint code = gl_GlobalInvocationID.x;
  uint leaves = 1u << (3u * pc.levels);
  if (code >= leaves) return;
  float side = 2.0 * pc.g_box / float(1u << pc.levels);
  vec3 centre = vec3(-pc.g_box) + (vec3(demorton3(code)) + 0.5) * side;
  uint s0 = gstart.d[code];
  uint s1 = s0 + gcnt.d[code];
  float m = 0.0;
  vec3 sx = vec3(0.0);
  for (uint j = s0; j < s1; j++) {
    vec4 q = gposm.d[j];
    m += q.w;
    sx += q.w * (q.xyz - centre);
  }
  uint node = level_offset(pc.levels) + code;
  if (m <= 0.0) {
    nodes.d[3u * node] = vec4(centre, 0.0);
    nodes.d[3u * node + 1u] = vec4(0.0);
    nodes.d[3u * node + 2u] = vec4(0.0);
    return;
  }
  vec3 cmo = sx / m;
  float xx = 0.0, yy = 0.0, zz = 0.0, xy = 0.0, xz = 0.0, yz = 0.0;
  for (uint j = s0; j < s1; j++) {
    vec4 q = gposm.d[j];
    vec3 d = (q.xyz - centre) - cmo;
    float r2 = dot(d, d);
    xx += q.w * (3.0 * d.x * d.x - r2);
    yy += q.w * (3.0 * d.y * d.y - r2);
    zz += q.w * (3.0 * d.z * d.z - r2);
    xy += q.w * 3.0 * d.x * d.y;
    xz += q.w * 3.0 * d.x * d.z;
    yz += q.w * 3.0 * d.y * d.z;
  }
  nodes.d[3u * node] = vec4(centre + cmo, m);
  nodes.d[3u * node + 1u] = vec4(xx, yy, zz, xy);
  nodes.d[3u * node + 2u] = vec4(xz, yz, 0.0, 0.0);
}
