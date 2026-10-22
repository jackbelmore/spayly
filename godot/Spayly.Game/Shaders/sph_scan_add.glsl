#[compute]
#version 450
// Scan carry: add each 1024-block's scanned total back into its elements.
#include "sph_common.glslinc"
layout(local_size_x = 256) in;
layout(set = 0, binding = 0, std430) restrict buffer Data { uint d[]; } data;
layout(set = 0, binding = 1, std430) restrict readonly buffer Carry { uint d[]; } carry;

void main() {
  uint i = gl_GlobalInvocationID.x;
  if (i < pc.count) data.d[i] += carry.d[i >> 10u];
}
