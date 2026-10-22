#[compute]
#version 450
// Gravity pyramid, pass 3: (x, y, z, G m) of every particle into its leaf's slot, so a leaf's members are contiguous.
#include "sph_common.glslinc"
layout(local_size_x = 256) in;
layout(set = 0, binding = 0, std430) restrict readonly buffer PosH { vec4 d[]; } posh;
layout(set = 0, binding = 1, std430) restrict readonly buffer VelM { vec4 d[]; } velm;
layout(set = 0, binding = 2, std430) restrict readonly buffer GRank { uvec2 d[]; } grank;
layout(set = 0, binding = 3, std430) restrict readonly buffer GStart { uint d[]; } gstart;
layout(set = 0, binding = 4, std430) restrict writeonly buffer GPosM { vec4 d[]; } gposm;

void main() {
  uint i = gl_GlobalInvocationID.x;
  if (i >= pc.n) return;
  uvec2 cr = grank.d[i];
  // G m rather than m: the pyramid's second moments reach ~1e39 in kg m^2, past float32's range; G m m^2 stays ~1e30.
  gposm.d[gstart.d[cr.x] + cr.y] = vec4(posh.d[i].xyz, pc.G * velm.d[i].w);
}
