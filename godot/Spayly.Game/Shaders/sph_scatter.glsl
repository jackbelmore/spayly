#[compute]
#version 450
// Counting sort, pass 3: every particle's whole state moves to its sorted slot in the other buffer side, so the SPH
// passes read spatial neighbours from consecutive memory (spike M1a: the sort is what makes the gather fast).
#include "sph_common.glslinc"
layout(local_size_x = 256) in;
layout(set = 0, binding = 0, std430) restrict readonly buffer PosH0 { vec4 d[]; } posh0;
layout(set = 0, binding = 1, std430) restrict readonly buffer VelM0 { vec4 d[]; } velm0;
layout(set = 0, binding = 2, std430) restrict readonly buffer Acc0 { vec4 d[]; } acc0;
layout(set = 0, binding = 3, std430) restrict readonly buffer Therm0 { vec4 d[]; } therm0;
layout(set = 0, binding = 4, std430) restrict readonly buffer Pred0 { vec4 d[]; } pred0;
layout(set = 0, binding = 5, std430) restrict readonly buffer Ids0 { uvec4 d[]; } ids0;
layout(set = 0, binding = 6, std430) restrict writeonly buffer PosH1 { vec4 d[]; } posh1;
layout(set = 0, binding = 7, std430) restrict writeonly buffer VelM1 { vec4 d[]; } velm1;
layout(set = 0, binding = 8, std430) restrict writeonly buffer Acc1 { vec4 d[]; } acc1;
layout(set = 0, binding = 9, std430) restrict writeonly buffer Therm1 { vec4 d[]; } therm1;
layout(set = 0, binding = 10, std430) restrict writeonly buffer Pred1 { vec4 d[]; } pred1;
layout(set = 0, binding = 11, std430) restrict writeonly buffer Ids1 { uvec4 d[]; } ids1;
layout(set = 0, binding = 12, std430) restrict readonly buffer CellRank { uvec2 d[]; } cell_rank;
layout(set = 0, binding = 13, std430) restrict readonly buffer Start { uint d[]; } start;

void main() {
  uint i = gl_GlobalInvocationID.x;
  if (i >= pc.n) return;
  uvec2 kr = cell_rank.d[i];
  uint j = start.d[kr.x] + kr.y;
  posh1.d[j] = posh0.d[i];
  velm1.d[j] = velm0.d[i];
  acc1.d[j] = acc0.d[i];
  therm1.d[j] = therm0.d[i];
  pred1.d[j] = pred0.d[i];
  ids1.d[j] = ids0.d[i];
}
