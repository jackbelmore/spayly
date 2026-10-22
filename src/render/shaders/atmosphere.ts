import { CHAPMAN, OCCLUDERS, RAY_SPHERE, SPHERE_SHADOW } from './common'

/**
 * Single-scattering atmosphere on a thin shell. Sun and view optical depths are analytic
 * (Chapman function); the in-scatter integral uses samples clustered around the densest point.
 */
export const ATMOSPHERE_FRAG = /* glsl */ `
precision highp float;
#include <common>
#include <logdepthbuf_pars_fragment>
${CHAPMAN}
${RAY_SPHERE}
${SPHERE_SHADOW}
${OCCLUDERS}
uniform vec3 uSunDir;
uniform vec3 uViewPos;
uniform float uAtmR;
uniform vec3 uBetaR;
uniform float uBetaM, uHR, uHM, uG;
uniform vec3 uSunColor;
uniform float uSunIntensity;
uniform float uStrength;
uniform float uDust;
uniform vec3 uDustColor;
varying vec3 vObj;

void main() {
  vec3 ro = uViewPos;
  vec3 rd = normalize(vObj - ro);
  vec2 ta = raySphere(ro, rd, uAtmR);
  if (ta.y <= 0.0) discard;
  float t0 = max(ta.x, 0.0);
  float t1 = ta.y;
  vec2 tp = raySphere(ro, rd, 1.0);
  if (tp.x > 0.0) t1 = min(t1, tp.x);
  if (t1 <= t0) discard;
  float tc = clamp(-dot(ro, rd), t0, t1);
  vec3 L = normalize(uSunDir);
  float XR = 1.0 / uHR;
  float XM = 1.0 / uHM;
  float dustMul = 1.0 + 1.3 * uDust;
  vec3 sumR = vec3(0.0);
  vec3 sumM = vec3(0.0);
  const int NS = 7;
  for (int side = 0; side < 2; side++) {
    float tEnd = side == 0 ? t0 : t1;
    float len = abs(tEnd - tc);
    if (len < 1e-7) continue;
    float dir = side == 0 ? -1.0 : 1.0;
    for (int i = 0; i < NS; i++) {
      float u = (float(i) + 0.5) / float(NS);
      float t = tc + dir * u * u * len;
      float ds = 2.0 * u * len / float(NS);
      vec3 p = ro + rd * t;
      float r = length(p);
      float h = max(r - 1.0, 0.0);
      vec3 up = p / r;
      float dR = exp(-h / uHR);
      float dM = exp(-h / uHM) * dustMul;
      float cs = dot(up, L);
      float x0 = sqrt(max(1.0 - cs * cs, 0.0)) * r;
      if (cs < 0.0 && x0 < 1.0) continue;
      float colSR = uHR * chapman(XR, h / uHR, cs);
      float colSM = uHM * chapman(XM, h / uHM, cs) * dustMul;
      float cv = dot(up, -rd);
      float colVR = uHR * chapman(XR, h / uHR, cv);
      float colVM = uHM * chapman(XM, h / uHM, cv) * dustMul;
      vec3 tau = uBetaR * (colSR + colVR) + uBetaM * 1.1 * (colSM + colVM);
      vec3 attn = exp(-tau) * occluderShadow(p, L);
      sumR += attn * dR * ds;
      sumM += attn * dM * ds;
    }
  }
  float mu = dot(rd, L);
  float phR = 3.0 / (16.0 * PI) * (1.0 + mu * mu);
  float g2 = uG * uG;
  float phM = 3.0 / (8.0 * PI) * ((1.0 - g2) * (1.0 + mu * mu)) / ((2.0 + g2) * pow(1.0 + g2 - 2.0 * uG * mu, 1.5));
  vec3 mieColor = mix(vec3(1.0), uDustColor, uDust);
  vec3 col = uSunColor * uSunIntensity * (uBetaR * phR * sumR + uBetaM * phM * sumM * mieColor);
  gl_FragColor = vec4(col * uStrength, 1.0);
  #include <logdepthbuf_fragment>
}
`
