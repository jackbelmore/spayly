#[compute]
#version 450
// Closing half-kick of KDK, then this particle's next-step limit: min(Courant, C_a sqrt(h/|a|), C_g sqrt(eps/|g|)),
// reduced into params.dt_bits with atomicMin (positive floats order like their bit patterns). SphSolver.NextDt.
#include "sph_common.glslinc"
layout(local_size_x = WG) in;
layout(set = 0, binding = 0, std430) restrict readonly buffer PosH { vec4 d[]; } posh;
layout(set = 0, binding = 1, std430) restrict buffer VelM { vec4 d[]; } velm;
layout(set = 0, binding = 2, std430) restrict readonly buffer Acc { vec4 d[]; } acc;
layout(set = 0, binding = 3, std430) restrict buffer Therm { vec4 d[]; } therm;
layout(set = 0, binding = 4, std430) restrict readonly buffer Aux { vec4 d[]; } aux;
layout(set = 0, binding = 5, std430) restrict buffer Params { float dt; float time; uint dt_bits; uint steps; float dt_max; float growth; float fixed_dt; uint pad; } params;

void main() {
  uint i = gl_GlobalInvocationID.x;
  if (i >= pc.n) return;
  float hdt = 0.5 * params.dt;
  vec4 v = velm.d[i];
  vec4 a = acc.d[i];
  vec4 th = therm.d[i];
  v.xyz += a.xyz * hdt;
  th.x = max(th.x + a.w * hdt, 0.0);
  velm.d[i] = v;
  therm.d[i] = th;
  float h = posh.d[i].w;
  vec4 x = aux.d[i];
  float dt = x.y;
  float am = length(a.xyz);
  if (am > 0.0) dt = min(dt, pc.acc_factor * sqrt(h / am));
  if (x.z > 0.0 && pc.eps2 > 0.0) dt = min(dt, pc.grav_factor * sqrt(sqrt(pc.eps2) / x.z));
  atomicMin(params.dt_bits, floatBitsToUint(dt));
}
