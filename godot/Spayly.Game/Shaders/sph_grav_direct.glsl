#[compute]
#version 450
// Plummer-softened self-gravity by direct summation in shared-memory tiles (research 2.4's all-pairs kernel); the path
// for N up to ~2e4, and the parity reference. Adds to acc.xyz, records |g| for the gravity timestep criterion.
#include "sph_common.glslinc"
layout(local_size_x = 256) in;
layout(set = 0, binding = 0, std430) restrict readonly buffer PosH { vec4 d[]; } posh;
layout(set = 0, binding = 1, std430) restrict readonly buffer VelM { vec4 d[]; } velm;
layout(set = 0, binding = 2, std430) restrict buffer Acc { vec4 d[]; } acc;
layout(set = 0, binding = 3, std430) restrict buffer Aux { vec4 d[]; } aux;
shared vec4 tile[256];

void main() {
  uint i = gl_GlobalInvocationID.x;
  uint lid = gl_LocalInvocationID.x;
  bool live = i < pc.n;
  vec3 p = live ? posh.d[i].xyz : vec3(0.0);
  vec3 g = vec3(0.0);
  for (uint t = 0u; t < pc.n; t += 256u) {
    uint j = t + lid;
    tile[lid] = j < pc.n ? vec4(posh.d[j].xyz, pc.G * velm.d[j].w) : vec4(0.0);
    barrier();
    uint lim = min(256u, pc.n - t);
    for (uint k = 0u; k < lim; k++) {
      vec3 d = tile[k].xyz - p;
      float inv = inversesqrt(dot(d, d) + pc.eps2);
      g += (tile[k].w * inv * inv * inv) * d;
    }
    barrier();
  }
  if (!live) return;
  vec4 a = acc.d[i];
  acc.d[i] = vec4(a.xyz + g, a.w);
  aux.d[i].z = length(g);
}
