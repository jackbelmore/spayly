#[compute]
#version 450
// Gravity pyramid, pass 1: each particle's leaf (Morton code at the finest level of a complete octree over the box) and
// its rank in that leaf. Particles outside the box land in the edge leaves; the walk still uses their true positions.
#include "sph_common.glslinc"
layout(local_size_x = 256) in;
layout(set = 0, binding = 0, std430) restrict readonly buffer PosH { vec4 d[]; } posh;
layout(set = 0, binding = 1, std430) restrict buffer GCnt { uint d[]; } gcnt;
layout(set = 0, binding = 2, std430) restrict writeonly buffer GRank { uvec2 d[]; } grank;

void main() {
  uint i = gl_GlobalInvocationID.x;
  if (i >= pc.n) return;
  uint code = leaf_code(posh.d[i].xyz);
  grank.d[i] = uvec2(code, atomicAdd(gcnt.d[code], 1u));
}
