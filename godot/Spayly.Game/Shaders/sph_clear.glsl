#[compute]
#version 450
// Zero two uint arrays of pc.count entries (bucket counts and bucket h maxima; the pyramid's leaf counts twice).
#include "sph_common.glslinc"
layout(local_size_x = 256) in;
layout(set = 0, binding = 0, std430) restrict writeonly buffer A { uint d[]; } a;
layout(set = 0, binding = 1, std430) restrict writeonly buffer B { uint d[]; } b;

void main() {
  uint i = gl_GlobalInvocationID.x;
  if (i >= pc.count) return;
  a.d[i] = 0u;
  b.d[i] = 0u;
}
