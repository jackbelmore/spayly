#[compute]
#version 450
// Momentum and energy equations with Monaghan (1992) viscosity and the Balsara switch (SphSolver.Forces), gathered over
// every j with r < 2 max(h_i, h_j), found through the buckets' largest h. Symmetric kernel gradient, so pair terms
// cancel in the sum up to float rounding. Also the Courant timestep candidate.
#include "sph_common.glslinc"
layout(local_size_x = WG) in;
layout(set = 0, binding = 0, std430) restrict readonly buffer PosH { vec4 d[]; } posh;
layout(set = 0, binding = 1, std430) restrict readonly buffer VelM { vec4 d[]; } velm;
layout(set = 0, binding = 2, std430) restrict readonly buffer Pred { vec4 d[]; } pred;
layout(set = 0, binding = 3, std430) restrict readonly buffer Therm { vec4 d[]; } therm;
layout(set = 0, binding = 4, std430) restrict buffer Aux { vec4 d[]; } aux;
layout(set = 0, binding = 5, std430) restrict writeonly buffer Acc { vec4 d[]; } acc;
layout(set = 0, binding = 6, std430) restrict readonly buffer Start { uint d[]; } start;
layout(set = 0, binding = 7, std430) restrict readonly buffer Cnt { uint d[]; } cnt;
layout(set = 0, binding = 8, std430) restrict readonly buffer HMax { uint d[]; } hmax;

void main() {
  uint i = gl_GlobalInvocationID.x;
  if (i >= pc.n) return;
  vec4 pi = posh.d[i];
  float hi = pi.w;
  vec3 vi = pred.d[i].xyz;
  vec4 ti = therm.d[i];
  float fi = aux.d[i].x;
  float qi = ti.z / (ti.y * ti.y);
  ivec3 ci = cell_of(pi.xyz);
  vec3 a = vec3(0.0);
  float du = 0.0;
  float mumax = 0.0;
  for (int dz = -STENCIL; dz <= STENCIL; dz++)
  for (int dy = -STENCIL; dy <= STENCIL; dy++)
  for (int dx = -STENCIL; dx <= STENCIL; dx++) {
    ivec3 c = ci + ivec3(dx, dy, dz);
    float b2 = box_dist2(pi.xyz, c);
    // No h exceeds the cell, so a box farther than two cells can never be in reach: skip the bucket read.
    if (b2 >= 4.0 * pc.cell * pc.cell) continue;
    uint k = cell_key(c);
    float reach = 2.0 * max(hi, uintBitsToFloat(hmax.d[k]));
    if (b2 >= reach * reach) continue;
    uint s0 = start.d[k];
    uint s1 = s0 + cnt.d[k];
    for (uint j = s0; j < s1; j++) {
      vec4 pj = posh.d[j];
      if (any(notEqual(cell_of(pj.xyz), c))) continue;
      vec3 d = pi.xyz - pj.xyz;
      float r2 = dot(d, d);
      float hj = pj.w;
      float hh = max(hi, hj);
      if (r2 >= 4.0 * hh * hh || r2 <= 0.0) continue;
      float r = sqrt(r2);
      float gbar = 0.5 * (kernel_dw(r, hi) + kernel_dw(r, hj)) / r;
      vec3 w = vi - pred.d[j].xyz;
      float vr = dot(w, d);
      vec4 tj = therm.d[j];
      float qj = tj.z / (tj.y * tj.y);
      float visc = 0.0;
      if (vr < 0.0) {
        float hbar = 0.5 * (hi + hj);
        float mu = hbar * vr / (r2 + 0.01 * hbar * hbar);
        float cbar = 0.5 * (ti.w + tj.w);
        float rhobar = 0.5 * (ti.y + tj.y);
        float f = 0.5 * (fi + aux.d[j].x);
        visc = f * (-pc.alpha * cbar * mu + pc.beta * mu * mu) / rhobar;
        mumax = max(mumax, -mu);
      }
      float mj = velm.d[j].w;
      float sij = (qi + qj + visc) * gbar;
      a -= mj * sij * d;
      du += mj * (qi + 0.5 * visc) * vr * gbar;
    }
  }
  acc.d[i] = vec4(a, du);
  // Only .y is written, so other invocations reading this particle's .x (its Balsara factor) never race with it.
  aux.d[i].y = pc.courant * hi / (ti.w + 1.2 * (pc.alpha * ti.w + pc.beta * mumax));
}
