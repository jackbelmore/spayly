import { NOISE } from './common'

export const SUN_FRAG = /* glsl */ `
precision highp float;
#include <common>
#include <logdepthbuf_pars_fragment>
${NOISE}
uniform float uTime;
uniform vec3 uColor;
uniform float uIntensity;
uniform vec3 uViewPos;
uniform sampler2D uTex;
uniform float uHasTex;
varying vec2 vUv;
varying vec3 vObj;
void main() {
  vec3 N = normalize(vObj);
  vec3 V = normalize(uViewPos - vObj);
  float mu = clamp(dot(N, V), 0.0, 1.0);
  float limb = 1.0 - 0.62 * (1.0 - mu);
  float gran = fbm(N * 34.0 + vec3(0.0, uTime * 0.05, 0.0));
  float cells = fbm(N * 9.0 - vec3(uTime * 0.02, 0.0, 0.0));
  float spots = smoothstep(0.67, 0.75, fbm(N * 12.0 + vec3(3.0, 0.0, uTime * 0.002)));
  vec3 base = uHasTex > 0.5 ? texture2D(uTex, vec2(vUv.x + uTime * 0.0005, vUv.y)).rgb : vec3(1.0);
  base = mix(vec3(1.0), base / max(max(base.r, base.g), max(base.b, 0.05)), 0.3);
  vec3 col = uColor * vec3(1.0, 0.9, 0.72) * base * (0.72 + 0.45 * gran + 0.3 * cells) * (1.0 - 0.7 * spots);
  gl_FragColor = vec4(col * limb * uIntensity, 1.0);
  #include <logdepthbuf_fragment>
}
`

export const BILLBOARD_VERT = /* glsl */ `
#include <common>
#include <logdepthbuf_pars_vertex>
varying vec2 vUv;
void main() {
  vUv = uv;
  vec4 mvPosition = modelViewMatrix * vec4(position, 1.0);
  gl_Position = projectionMatrix * mvPosition;
  #include <logdepthbuf_vertex>
}
`

/** Corona and glow drawn on a camera-facing plane 6 solar radii wide (r = 1 at 3 radii). */
export const CORONA_FRAG = /* glsl */ `
precision highp float;
#include <common>
#include <logdepthbuf_pars_fragment>
${NOISE}
uniform float uTime;
uniform vec3 uColor;
uniform float uIntensity;
uniform float uFlare;
varying vec2 vUv;
void main() {
  vec2 p = vUv * 2.0 - 1.0;
  float r = length(p);
  if (r > 1.0) discard;
  float ang = atan(p.y, p.x);
  float rays = fbm(vec3(cos(ang) * 3.0, sin(ang) * 3.0, uTime * 0.04));
  float rays2 = fbm(vec3(ang * 2.0, r * 6.0 - uTime * 0.08, 1.7));
  float core = exp(-r * 9.0) * 3.5;
  float streaks = smoothstep(1.0, 0.16, r) * (0.3 + 0.7 * rays) * (0.5 + 0.5 * rays2) * pow(max(1.0 - r, 0.0), 1.4) * 1.1;
  float halo = exp(-r * 3.2) * 0.9;
  float edge = smoothstep(1.0, 0.8, r);
  vec3 col = uColor * (core + halo + streaks) * edge * uIntensity * (1.0 + uFlare);
  gl_FragColor = vec4(col, 1.0);
  #include <logdepthbuf_fragment>
}
`
