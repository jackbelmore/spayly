import { NOISE } from './common'

export const ROCK_VERT = /* glsl */ `
#include <common>
#include <logdepthbuf_pars_vertex>
attribute vec3 aDir;
varying vec3 vDir;
varying vec3 vObj;
varying vec3 vNrm;
void main() {
  vDir = aDir;
  vObj = position;
  vNrm = normal;
  vec4 mvPosition = modelViewMatrix * vec4(position, 1.0);
  gl_Position = projectionMatrix * mvPosition;
  #include <logdepthbuf_vertex>
}
`

export const ROCK_FRAG = /* glsl */ `
precision highp float;
#include <common>
#include <logdepthbuf_pars_fragment>
${NOISE}
uniform sampler2D uTex;
uniform float uHasMap;
uniform vec3 uSeed;
uniform vec3 uSunDir;
uniform vec3 uSunColor;
uniform float uSunIntensity;
uniform vec3 uViewPos;
uniform float uMagma;
uniform float uTime;
uniform vec3 uTint;
varying vec3 vDir;
varying vec3 vObj;
varying vec3 vNrm;
// Equirectangular lookup from the undisplaced direction, in the layout SphereGeometry and
// bodyDirToUv use. atan wraps at the back meridian, where u jumps from 1 to 0 across one pixel and
// the jump would pick the smallest mip; take u from whichever of two parameterisations is
// continuous here.
vec2 rockUv(vec3 p) {
  vec3 d = normalize(p);
  float u = atan(d.z, -d.x) / (2.0 * PI);
  float u1 = fract(u);
  float u2 = fract(u + 0.5) - 0.5;
  return vec2(fwidth(u1) <= fwidth(u2) + 1e-6 ? u1 : u2, 0.5 + asin(clamp(d.y, -1.0, 1.0)) / PI);
}
void main() {
  vec3 N = normalize(vNrm);
  vec3 L = normalize(uSunDir);
  vec3 V = normalize(uViewPos - vObj);
  float lit = max(dot(N, L), 0.0);
  vec3 albedo;
  if (uHasMap > 0.5) {
    albedo = texture2D(uTex, rockUv(vDir)).rgb;
  } else {
    // No map: mottling plus grit from noise in three dimensions, which unlike a painted map has
    // no seam and does not pinch into spokes at the poles.
    vec3 p = normalize(vDir) + uSeed;
    float s = 0.35 + 0.5 * fbm(p * 2.5) + 0.15 * fbm(p * 8.0);
    albedo = pow(s * vec3(0.92, 0.86, 0.78), vec3(2.2));
  }
  albedo *= uTint;
  vec3 H = normalize(L + V);
  float spec = pow(max(dot(N, H), 0.0), 24.0) * 0.08;
  vec3 col = albedo * uSunColor * uSunIntensity * (lit + 0.015) + uSunColor * uSunIntensity * spec * lit;
  if (uMagma > 0.001) {
    float n = fbm(normalize(vObj) * 5.0 + vec3(0.0, uTime * 0.01, 0.0));
    float cracks = smoothstep(0.45, 0.62, n);
    vec3 lava = mix(vec3(0.45, 0.04, 0.0), vec3(1.0, 0.5, 0.12), cracks) * (0.8 + 0.4 * fbm(normalize(vObj) * 20.0));
    col = mix(col * (1.0 - 0.8 * uMagma), lava * 1.5, uMagma * (0.25 + 0.75 * cracks));
  }
  gl_FragColor = vec4(col, 1.0);
  #include <logdepthbuf_fragment>
}
`

/** Ballistic ejecta particles in the planet's unit-sphere frame. */
export const EJECTA_VERT = /* glsl */ `
#include <common>
#include <logdepthbuf_pars_vertex>
attribute vec3 aDir;
attribute float aSpeed;
attribute float aSeed;
uniform float uT;
uniform float uG;
uniform vec3 uOrigin;
uniform float uPointScale;
uniform float uMode;
uniform float uLife;
varying float vLife;
varying float vSeed;
varying float vAlive;
void main() {
  vSeed = aSeed;
  vec3 p;
  if (uMode > 0.5) {
    vLife = clamp(uT / uLife, 0.0, 1.0);
    p = uOrigin + aDir * aSpeed * uT;
    vAlive = uT < uLife ? 1.0 : 0.0;
  } else {
    float up = dot(aDir, uOrigin);
    float tf = max(2.0 * aSpeed * up / uG, 1e-4);
    vLife = clamp(uT / tf, 0.0, 1.0);
    p = uOrigin + aDir * aSpeed * uT - uOrigin * 0.5 * uG * uT * uT;
    float r = length(p);
    vAlive = (r < 0.999 && uT > 0.02 * tf) || uT > tf ? 0.0 : 1.0;
    p = normalize(p) * max(r, 1.0005);
  }
  vec4 mvPosition = modelViewMatrix * vec4(p, 1.0);
  gl_Position = projectionMatrix * mvPosition;
  gl_PointSize = clamp(uPointScale * (1.0 - 0.6 * vLife) / max(-mvPosition.z, 1e-6), 1.5, 7.0);
  #include <logdepthbuf_vertex>
}
`

export const EJECTA_FRAG = /* glsl */ `
precision highp float;
#include <common>
#include <logdepthbuf_pars_fragment>
varying float vLife;
varying float vSeed;
varying float vAlive;
void main() {
  if (vAlive < 0.5) discard;
  vec2 c = gl_PointCoord - 0.5;
  float d2 = dot(c, c);
  if (d2 > 0.25) discard;
  vec3 hot = vec3(1.0, 0.75, 0.4) * 5.0;
  vec3 warm = vec3(1.0, 0.35, 0.1) * 1.5;
  vec3 ash = vec3(0.35, 0.33, 0.3);
  float k = vLife;
  vec3 col = k < 0.35 ? mix(hot, warm, k / 0.35) : mix(warm, ash, (k - 0.35) / 0.65);
  col *= 0.7 + 0.6 * vSeed;
  float alpha = (1.0 - d2 * 4.0) * (1.0 - 0.7 * k);
  gl_FragColor = vec4(col, alpha);
  #include <logdepthbuf_fragment>
}
`

export const FIREBALL_VERT = /* glsl */ `
#include <common>
#include <logdepthbuf_pars_vertex>
${NOISE}
uniform float uTime;
uniform float uSeed;
uniform float uT;
varying vec3 vObj;
varying vec3 vNrm;
void main() {
  vObj = position;
  vNrm = normal;
  float n = fbm(position * 2.2 + vec3(uSeed, uTime * 0.25, 0.0));
  float bulge = 1.0 + (n - 0.5) * (0.45 + 0.35 * uT);
  vec3 p = position * bulge;
  vec4 mvPosition = modelViewMatrix * vec4(p, 1.0);
  gl_Position = projectionMatrix * mvPosition;
  #include <logdepthbuf_vertex>
}
`

export const FIREBALL_FRAG = /* glsl */ `
precision highp float;
#include <common>
#include <logdepthbuf_pars_fragment>
${NOISE}
uniform float uT;
uniform float uTime;
uniform float uSeed;
uniform vec3 uViewPos;
varying vec3 vObj;
varying vec3 vNrm;
void main() {
  vec3 N = normalize(vObj);
  float n = fbm(N * 3.0 + vec3(uSeed) + vec3(0.0, uTime * 0.35, 0.0));
  float n2 = fbm(N * 9.0 - vec3(uTime * 0.2));
  vec3 hot = vec3(1.0, 0.96, 0.85) * 9.0;
  vec3 mid = vec3(1.0, 0.45, 0.1) * 3.5;
  vec3 cool = vec3(0.55, 0.12, 0.03) * 1.2;
  vec3 smoke = vec3(0.14, 0.12, 0.1);
  vec3 col = uT < 0.25 ? mix(hot, mid, uT / 0.25) : uT < 0.55 ? mix(mid, cool, (uT - 0.25) / 0.3) : mix(cool, smoke, (uT - 0.55) / 0.45);
  col *= 0.55 + 0.9 * n + 0.2 * n2;
  vec3 V = normalize(uViewPos - vObj);
  float rim = 1.0 - clamp(dot(N, V), 0.0, 1.0);
  float alpha = smoothstep(0.0, 0.03, uT) * (uT < 0.8 ? 1.0 : 1.0 - (uT - 0.8) / 0.2) * (1.0 - 0.6 * rim * rim);
  gl_FragColor = vec4(col, alpha);
  #include <logdepthbuf_fragment>
}
`

export const POINTS_VERT = /* glsl */ `
#include <common>
#include <logdepthbuf_pars_vertex>
attribute float aSize;
attribute vec3 aColor;
uniform float uScale;
varying vec3 vColor;
void main() {
  vColor = aColor;
  vec4 mvPosition = modelViewMatrix * vec4(position, 1.0);
  gl_Position = projectionMatrix * mvPosition;
  gl_PointSize = aSize * uScale;
  #include <logdepthbuf_vertex>
}
`

export const POINTS_FRAG = /* glsl */ `
precision highp float;
#include <common>
#include <logdepthbuf_pars_fragment>
uniform float uAlpha;
varying vec3 vColor;
void main() {
  vec2 c = gl_PointCoord - 0.5;
  float d2 = dot(c, c);
  if (d2 > 0.25) discard;
  float a = smoothstep(0.25, 0.05, d2);
  gl_FragColor = vec4(vColor * a * uAlpha, 1.0);
  #include <logdepthbuf_fragment>
}
`
