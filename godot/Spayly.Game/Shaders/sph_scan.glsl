#[compute]
#version 450
// Blelloch (1990) exclusive scan of 1024 elements per workgroup (up-sweep, clear, down-sweep in shared memory); each
// block's total goes to sums[] for the next level, as in spike M1a.
#include "sph_common.glslinc"
layout(local_size_x = 512) in;
layout(set = 0, binding = 0, std430) restrict readonly buffer Src { uint d[]; } src;
layout(set = 0, binding = 1, std430) restrict writeonly buffer Dst { uint d[]; } dst;
layout(set = 0, binding = 2, std430) restrict writeonly buffer Sums { uint d[]; } sums;
shared uint s[1024];

void main() {
  uint t = gl_LocalInvocationID.x;
  uint base = gl_WorkGroupID.x * 1024u;
  uint ia = base + 2u * t;
  uint ib = ia + 1u;
  s[2u * t] = ia < pc.count ? src.d[ia] : 0u;
  s[2u * t + 1u] = ib < pc.count ? src.d[ib] : 0u;
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
  if (ia < pc.count) dst.d[ia] = s[2u * t];
  if (ib < pc.count) dst.d[ib] = s[2u * t + 1u];
}
