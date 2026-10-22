#[compute]
#version 450
// First half of KDK (SphSolver.Step): v += a dt/2, u += du dt/2, x += v dt, then the predicted v and u at the end of
// the drift (for the viscosity, divergence and EOS) and h from the previous step's density.
#include "sph_common.glslinc"
layout(local_size_x = WG) in;
layout(set = 0, binding = 0, std430) restrict buffer PosH { vec4 d[]; } posh;
layout(set = 0, binding = 1, std430) restrict buffer VelM { vec4 d[]; } velm;
layout(set = 0, binding = 2, std430) restrict readonly buffer Acc { vec4 d[]; } acc;
layout(set = 0, binding = 3, std430) restrict buffer Therm { vec4 d[]; } therm;
layout(set = 0, binding = 4, std430) restrict writeonly buffer Pred { vec4 d[]; } pred;
layout(set = 0, binding = 5, std430) restrict readonly buffer Params { float dt; float time; uint dt_bits; uint steps; float dt_max; float growth; float fixed_dt; uint pad; } params;

void main() {
  uint i = gl_GlobalInvocationID.x;
  if (i >= pc.n) return;
  float dt = params.dt;
  float hdt = 0.5 * dt;
  vec4 x = posh.d[i];
  vec4 v = velm.d[i];
  vec4 a = acc.d[i];
  vec4 th = therm.d[i];
  v.xyz += a.xyz * hdt;
  th.x = max(th.x + a.w * hdt, 0.0);
  x.xyz += v.xyz * dt;
  vec3 vp = v.xyz + a.xyz * hdt;
  float up = max(th.x + a.w * hdt, 0.0);
  x.w = clamp(pc.eta * pow(v.w / th.y, 1.0 / 3.0), pc.h_min, pc.h_max);
  posh.d[i] = x;
  velm.d[i] = v;
  therm.d[i] = th;
  pred.d[i] = vec4(vp, up);
}
