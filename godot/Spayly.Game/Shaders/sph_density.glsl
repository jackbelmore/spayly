#[compute]
#version 450
// Density by gather with the particle's own h, rho_i = sum_j m_j W(r_ij, h_i); the velocity divergence and curl for the
// Balsara (1995) switch from the same neighbours; then the Tillotson EOS and sound speed (SphSolver.Density + Eos).
#include "sph_common.glslinc"
layout(local_size_x = WG) in;
layout(set = 0, binding = 0, std430) restrict readonly buffer PosH { vec4 d[]; } posh;
layout(set = 0, binding = 1, std430) restrict readonly buffer VelM { vec4 d[]; } velm;
layout(set = 0, binding = 2, std430) restrict readonly buffer Pred { vec4 d[]; } pred;
layout(set = 0, binding = 3, std430) restrict buffer Therm { vec4 d[]; } therm;
layout(set = 0, binding = 4, std430) restrict writeonly buffer Aux { vec4 d[]; } aux;
layout(set = 0, binding = 5, std430) restrict readonly buffer Ids { uvec4 d[]; } ids;
layout(set = 0, binding = 6, std430) restrict readonly buffer Start { uint d[]; } start;
layout(set = 0, binding = 7, std430) restrict readonly buffer Cnt { uint d[]; } cnt;
layout(set = 0, binding = 8, std140) uniform Materials { vec4 m[40]; } mats;

Mat load_mat(uint k) {
  vec4 a = mats.m[5u * k];
  vec4 b = mats.m[5u * k + 1u];
  vec4 c = mats.m[5u * k + 2u];
  Mat r;
  r.rho0 = a.x; r.A = a.y; r.B = a.z; r.E0 = a.w;
  r.Eiv = b.x; r.Ecv = b.y; r.a = b.z; r.b = b.w;
  r.alpha = c.x; r.beta = c.y;
  return r;
}

void main() {
  uint i = gl_GlobalInvocationID.x;
  if (i >= pc.n) return;
  vec4 pi = posh.d[i];
  float hi = pi.w;
  vec3 vi = pred.d[i].xyz;
  ivec3 ci = cell_of(pi.xyz);
  float reach2 = 4.0 * hi * hi;
  float rho = 0.0;
  float divs = 0.0;
  vec3 curl = vec3(0.0);
  for (int dz = -STENCIL; dz <= STENCIL; dz++)
  for (int dy = -STENCIL; dy <= STENCIL; dy++)
  for (int dx = -STENCIL; dx <= STENCIL; dx++) {
    ivec3 c = ci + ivec3(dx, dy, dz);
    if (box_dist2(pi.xyz, c) >= reach2) continue;
    uint k = cell_key(c);
    uint s0 = start.d[k];
    uint s1 = s0 + cnt.d[k];
    for (uint j = s0; j < s1; j++) {
      vec4 pj = posh.d[j];
      // Buckets are shared by distant cells; only this cell's members count, so no pair is seen twice.
      if (any(notEqual(cell_of(pj.xyz), c))) continue;
      vec3 d = pi.xyz - pj.xyz;
      float r2 = dot(d, d);
      if (r2 >= reach2) continue;
      float r = sqrt(r2);
      float mj = velm.d[j].w;
      rho += mj * kernel_w(r, hi);
      if (r > 0.0) {
        float gw = mj * kernel_dw(r, hi) / r;
        vec3 w = vi - pred.d[j].xyz;
        divs += gw * dot(w, d);
        curl += gw * cross(w, d);
      }
    }
  }
  uint mid = ids.d[i].x;
  Mat m = load_mat(mid);
  float up = pred.d[i].w;
  vec3 e = tillotson(m, rho, up);
  float p = max(e.x, pc.p_floor);
  float cs = sqrt(max(e.y + p / (rho * rho) * e.z, 1e-4 * m.A / m.rho0));
  float f = 1.0;
  if ((pc.flags & FLAG_BALSARA) != 0u) {
    float dv = abs(divs) / rho;
    float cv = length(curl) / rho;
    float den = dv + cv + 1e-4 * cs / hi;
    f = den > 0.0 ? dv / den : 1.0;
  }
  float u = therm.d[i].x;
  therm.d[i] = vec4(u, rho, p, cs);
  aux.d[i] = vec4(f, 0.0, 0.0, 0.0);
}
