#[compute]
#version 450
// Counting sort, pass 1 (research 2.3): bucket of each particle's cell, its rank inside the bucket from the atomic, and
// the bucket's largest h (floatBitsToUint keeps the order of positive floats), which lets a gather find every pair with
// r < 2 max(h_i, h_j) from either side.
#include "sph_common.glslinc"
layout(local_size_x = 256) in;
layout(set = 0, binding = 0, std430) restrict readonly buffer PosH { vec4 d[]; } posh;
layout(set = 0, binding = 1, std430) restrict buffer Cnt { uint d[]; } cnt;
layout(set = 0, binding = 2, std430) restrict buffer HMax { uint d[]; } hmax;
layout(set = 0, binding = 3, std430) restrict writeonly buffer CellRank { uvec2 d[]; } cell_rank;

void main() {
  uint i = gl_GlobalInvocationID.x;
  if (i >= pc.n) return;
  vec4 p = posh.d[i];
  uint k = cell_key(cell_of(p.xyz));
  uint r = atomicAdd(cnt.d[k], 1u);
  atomicMax(hmax.d[k], floatBitsToUint(p.w));
  cell_rank.d[i] = uvec2(k, r);
}
