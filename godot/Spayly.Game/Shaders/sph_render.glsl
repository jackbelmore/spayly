#[compute]
#version 450
// [GAME] What the points draw: position and h into one RD texture, a display colour into another (spike M1a route a,
// read by VERTEX_ID). The colour is a display temperature from the thermal part of u (u minus the cold-compression
// energy) with a melting plateau and a vaporisation plateau across the Tillotson E_iv..E_cv range (Tillotson.
// DisplayTemperature), then the material's cold colour blended into a blackbody glow.
#include "sph_common.glslinc"
layout(local_size_x = 256) in;
layout(set = 0, binding = 0, std430) restrict readonly buffer PosH { vec4 d[]; } posh;
layout(set = 0, binding = 1, std430) restrict readonly buffer Therm { vec4 d[]; } therm;
layout(set = 0, binding = 2, std430) restrict readonly buffer Ids { uvec4 d[]; } ids;
layout(set = 0, binding = 3, std140) uniform Materials { vec4 m[40]; } mats;
layout(set = 0, binding = 4, rgba32f) uniform restrict writeonly image2D pos_tex;
layout(set = 0, binding = 5, rgba32f) uniform restrict writeonly image2D col_tex;

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

// (cv, T_ref, T_melt, L_melt) in mats.m[5k+3].
float display_temperature(Mat m, vec4 t, float u) {
  float cv = t.x;
  float melt0 = cv * (t.z - t.y);
  if (u < melt0) return t.y + u / cv;
  float melt1 = melt0 + t.w;
  if (u < melt1) return t.z;
  float vap0 = max(m.Eiv, melt1);
  float tv = t.z + (vap0 - melt1) / cv;
  if (u < vap0) return t.z + (u - melt1) / cv;
  float vap1 = max(m.Ecv, vap0);
  if (u < vap1) return tv;
  return tv + (u - vap1) / cv;
}

// Tanner Helland's fit, as RenderUnits.Blackbody, normalised to the brightest channel.
vec3 blackbody(float temp) {
  float t = clamp(temp, 1000.0, 40000.0) / 100.0;
  vec3 c;
  if (t <= 66.0) {
    c.r = 255.0;
    c.g = 99.4708025861 * log(t) - 161.1195681661;
    c.b = t <= 19.0 ? 0.0 : 138.5177312231 * log(t - 10.0) - 305.0447927307;
  } else {
    c.r = 329.698727446 * pow(t - 60.0, -0.1332047592);
    c.g = 288.1221695283 * pow(t - 60.0, -0.0755148492);
    c.b = 255.0;
  }
  c = clamp(c / 255.0, 0.0, 1.0);
  c = pow(c, vec3(2.2));
  return c / max(max(c.r, c.g), max(c.b, 1e-6));
}

void main() {
  uint i = gl_GlobalInvocationID.x;
  if (i >= pc.n) return;
  vec4 p = posh.d[i];
  vec4 th = therm.d[i];
  uint k = ids.d[i].x;
  Mat m = load_mat(k);
  float temp = display_temperature(m, mats.m[5u * k + 3u], max(th.x - cold_energy(m, th.y), 0.0));
  vec3 base = mats.m[5u * k + 4u].rgb * 0.6;
  float glow = smoothstep(800.0, 2200.0, temp);
  vec3 hot = blackbody(temp) * (0.8 + 3.0 * smoothstep(1500.0, 6000.0, temp));
  ivec2 px = ivec2(int(i % pc.count), int(i / pc.count));
  imageStore(pos_tex, px, p);
  imageStore(col_tex, px, vec4(mix(base, hot, glow), 1.0));
}
