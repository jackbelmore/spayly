import { CHAPMAN, NOISE, OCCLUDERS, SPHERE_SHADOW } from './common'

export const PLANET_FRAG = /* glsl */ `
precision highp float;
#include <common>
#include <logdepthbuf_pars_fragment>
${NOISE}
${CHAPMAN}
${SPHERE_SHADOW}
${OCCLUDERS}

uniform sampler2D uDay;
uniform sampler2D uNight;
uniform sampler2D uSpecular;
uniform sampler2D uNormal;
uniform sampler2D uClouds;
uniform sampler2D uDamage;
uniform sampler2D uRing;
uniform float uHasNight, uHasSpecular, uHasNormal, uHasClouds, uHasRing;
uniform float uCloudShift;
uniform vec3 uSunDir;
uniform vec3 uSunColor;
uniform float uSunIntensity;
uniform vec3 uViewPos;
uniform vec4 uHeat[12];
uniform float uHeatGlow[12];
uniform int uHeatCount;
uniform float uMagma;
uniform float uClimate;
uniform float uTime;
uniform float uGas;
uniform float uRingInner, uRingOuter;
uniform vec3 uBetaR;
uniform float uBetaM, uHR, uHM, uAtmoOn, uDust;

varying vec2 vUv;
varying vec3 vObj;
varying vec3 vNrm;

float ringShadow(vec3 P, vec3 L) {
  if (uHasRing < 0.5 || abs(L.y) < 1e-4) return 1.0;
  float t = -P.y / L.y;
  if (t <= 0.0) return 1.0;
  vec3 hit = P + t * L;
  float r = length(hit.xz);
  if (r < uRingInner || r > uRingOuter) return 1.0;
  float a = texture2D(uRing, vec2((r - uRingInner) / (uRingOuter - uRingInner), 0.5)).a;
  return 1.0 - 0.92 * a;
}

void main() {
  vec3 N0 = normalize(vObj);
  vec3 N = N0;
  if (uHasNormal > 0.5) {
    vec3 T = normalize(vec3(N0.z, 0.0, -N0.x));
    vec3 B = normalize(cross(N0, T));
    vec3 nm = texture2D(uNormal, vUv).xyz * 2.0 - 1.0;
    N = normalize(T * nm.x * 0.7 + B * nm.y * 0.7 + N0 * nm.z);
  }
  vec3 L = normalize(uSunDir);
  vec3 V = normalize(uViewPos - N0);
  float NdotL = dot(N, L);
  float NdotL0 = dot(N0, L);
  float lit = clamp(NdotL, 0.0, 1.0);
  float shadow = occluderShadow(N0, L) * ringShadow(N0, L);

  vec3 albedo = texture2D(uDay, vUv).rgb;
  vec3 dmg = texture2D(uDamage, vUv).rgb * 2.0;
  albedo *= dmg;

  float frozen = clamp(-uClimate, 0.0, 1.0);
  float scorched = clamp(uClimate, 0.0, 1.0);
  float lat = abs(N0.y);
  float iceLine = 1.0 - frozen * 1.08;
  float ice = smoothstep(iceLine - 0.14, iceLine + 0.04, lat) * step(0.001, frozen);
  albedo = mix(albedo, vec3(0.92, 0.95, 1.0), ice * 0.95);
  albedo = mix(albedo, vec3(0.40, 0.28, 0.18) * (0.6 + 0.5 * fbm(N0 * 8.0)), scorched * 0.85);
  if (uHasClouds > 0.5) {
    float c = texture2D(uClouds, vec2(vUv.x - uCloudShift, vUv.y)).r;
    albedo *= 1.0 - 0.5 * c * (1.0 - scorched);
  }

  vec3 sunTrans = vec3(1.0);
  vec3 viewTrans = vec3(1.0);
  if (uAtmoOn > 0.5) {
    float XR = 1.0 / uHR;
    float XM = 1.0 / uHM;
    float cSun = max(NdotL0, 0.0);
    float colR = uHR * chapman(XR, 0.0, cSun);
    float colM = uHM * chapman(XM, 0.0, cSun);
    sunTrans = exp(-(uBetaR * colR + uBetaM * 1.1 * colM) * (1.0 + 1.0 * uDust));
    float cView = max(dot(N0, V), 0.0);
    float vR = uHR * chapman(XR, 0.0, cView);
    float vM = uHM * chapman(XM, 0.0, cView);
    viewTrans = exp(-(uBetaR * vR + uBetaM * 1.1 * vM) * (1.0 + 1.0 * uDust));
  }
  vec3 light = uSunColor * uSunIntensity * lit * shadow * sunTrans;
  vec3 color = albedo * light;

  if (uHasSpecular > 0.5) {
    float s = texture2D(uSpecular, vUv).r * (1.0 - ice);
    vec3 H = normalize(L + V);
    float sp = pow(max(dot(N, H), 0.0), 220.0);
    color += uSunColor * uSunIntensity * sp * s * 0.5 * shadow * sunTrans * smoothstep(0.0, 0.1, NdotL0);
  }
  color *= mix(1.0, pow(max(dot(N0, V), 0.0), 0.45) * 1.05, uGas * 0.7);
  color *= viewTrans;

  if (uHasNight > 0.5) {
    float night = smoothstep(0.08, -0.12, NdotL0);
    vec3 nl = texture2D(uNight, vUv).rgb;
    color += nl * night * (1.0 - scorched) * (1.0 - 0.7 * frozen) * smoothstep(0.6, 0.95, dmg.g);
  }

  for (int i = 0; i < 12; i++) {
    if (i >= uHeatCount) break;
    vec4 h = uHeat[i];
    float ang = acos(clamp(dot(N0, h.xyz), -1.0, 1.0));
    float r = ang / max(h.w, 1e-5);
    if (r < 1.2) {
      float g = uHeatGlow[i];
      float n = 0.55 + 0.45 * fbm(N0 * 60.0 + uTime * 0.02);
      float floorGlow = smoothstep(1.05, 0.55, r);
      color += vec3(1.0, 0.32, 0.05) * floorGlow * g * g * g * 0.9 * n;
      color += vec3(1.0, 0.72, 0.4) * smoothstep(0.45, 0.0, r) * g * g * g * g * 1.0;
    }
  }

  if (uMagma > 0.001) {
    float n = fbm(N0 * 6.0 + vec3(0.0, uTime * 0.01, 0.0));
    float cracks = smoothstep(0.42, 0.68, n);
    float fine = 0.7 + 0.5 * fbm(N0 * 28.0);
    vec3 lava = mix(vec3(0.45, 0.04, 0.0), vec3(1.0, 0.5, 0.12), cracks) * fine;
    vec3 crust = color * (1.0 - 0.8 * uMagma) + vec3(0.12, 0.02, 0.0) * uMagma;
    color = mix(crust, lava * 1.5, uMagma * (0.2 + 0.8 * cracks));
  }

  gl_FragColor = vec4(color, 1.0);
  #include <logdepthbuf_fragment>
}
`

export const CLOUD_FRAG = /* glsl */ `
precision highp float;
#include <common>
#include <logdepthbuf_pars_fragment>
${SPHERE_SHADOW}
${OCCLUDERS}
uniform sampler2D uClouds;
uniform vec3 uSunDir;
uniform vec3 uSunColor;
uniform float uSunIntensity;
uniform float uDust;
varying vec2 vUv;
varying vec3 vObj;
void main() {
  vec3 N = normalize(vObj);
  vec3 L = normalize(uSunDir);
  float lit = clamp(dot(N, L) * 1.15 + 0.08, 0.0, 1.0);
  float shadow = occluderShadow(N, L);
  float c = texture2D(uClouds, vUv).r;
  float alpha = smoothstep(0.04, 0.55, c) * (1.0 - 0.5 * uDust);
  vec3 tint = mix(vec3(1.0), vec3(0.75, 0.6, 0.45), uDust);
  vec3 col = tint * uSunColor * uSunIntensity * (lit * shadow * 0.95 + 0.015);
  gl_FragColor = vec4(col, alpha);
  #include <logdepthbuf_fragment>
}
`

export const RING_FRAG = /* glsl */ `
precision highp float;
#include <common>
#include <logdepthbuf_pars_fragment>
${SPHERE_SHADOW}
uniform sampler2D uRing;
uniform float uInner, uOuter;
uniform vec3 uSunDir;
uniform vec3 uSunColor;
uniform float uSunIntensity;
uniform vec3 uViewPos;
uniform float uSunAngular;
varying vec3 vObj;
void main() {
  float r = length(vObj.xz);
  float u = (r - uInner) / (uOuter - uInner);
  if (u < 0.0 || u > 1.0) discard;
  vec4 tex = texture2D(uRing, vec2(u, 0.5));
  float alpha = tex.a;
  if (alpha < 0.01) discard;
  vec3 L = normalize(uSunDir);
  vec3 V = normalize(uViewPos - vObj);
  float sunElev = abs(L.y);
  float sameSide = step(0.0, L.y * V.y);
  float lighting = mix(0.3, 1.0, sameSide) * (0.3 + 0.7 * sunElev) + 0.35 * (1.0 - sameSide) * (1.0 - alpha);
  float shadow = sphereShadow(vObj, L, vec3(0.0), 1.0, uSunAngular);
  vec3 col = tex.rgb * uSunColor * uSunIntensity * lighting * shadow;
  gl_FragColor = vec4(col, alpha);
  #include <logdepthbuf_fragment>
}
`
