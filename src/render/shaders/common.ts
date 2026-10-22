/** Shared GLSL chunks. All custom materials include the log-depth chunks so they sort correctly at solar-system scale. */

export const NOISE = /* glsl */ `
float hash3(vec3 p) {
  p = fract(p * 0.3183099 + vec3(0.1, 0.2, 0.3));
  p *= 17.0;
  return fract(p.x * p.y * p.z * (p.x + p.y + p.z));
}
float vnoise(vec3 p) {
  vec3 i = floor(p);
  vec3 f = fract(p);
  f = f * f * (3.0 - 2.0 * f);
  return mix(
    mix(mix(hash3(i + vec3(0, 0, 0)), hash3(i + vec3(1, 0, 0)), f.x), mix(hash3(i + vec3(0, 1, 0)), hash3(i + vec3(1, 1, 0)), f.x), f.y),
    mix(mix(hash3(i + vec3(0, 0, 1)), hash3(i + vec3(1, 0, 1)), f.x), mix(hash3(i + vec3(0, 1, 1)), hash3(i + vec3(1, 1, 1)), f.x), f.y),
    f.z);
}
float fbm(vec3 p) {
  float v = 0.0;
  float a = 0.5;
  for (int i = 0; i < 5; i++) {
    v += a * vnoise(p);
    p = p * 2.03 + vec3(1.7, 9.2, 3.1);
    a *= 0.5;
  }
  return v;
}
`

/**
 * Column density of an exponential atmosphere along a ray, in units of (surface density x scale height).
 * X = planet radius / scale height, h = altitude / scale height, c = cosine of the zenith angle of the ray.
 * Approximation of the Chapman function after Schüler (GPU Pro 3).
 */
export const CHAPMAN = /* glsl */ `
float chapman(float X, float h, float c) {
  float cs = sqrt((X + h) * 1.5707963);
  if (c >= 0.0) return cs / (cs * c + 1.0) * exp(-h);
  float x0 = max(sqrt(1.0 - c * c) * (X + h), X);
  float c0 = sqrt(x0 * 1.5707963);
  return 2.0 * c0 * exp(X - x0) - cs / (1.0 - cs * c) * exp(-h);
}
`

/** Soft shadow of a sphere (centre C, radius R) on point P lit from direction L by a sun of angular radius sunAng. */
export const SPHERE_SHADOW = /* glsl */ `
float sphereShadow(vec3 P, vec3 L, vec3 C, float R, float sunAng) {
  vec3 d = C - P;
  float t = dot(d, L);
  if (t <= 0.0) return 1.0;
  float perp = length(d - t * L);
  float pen = max(t * sunAng, 1e-6);
  return smoothstep(R - pen, R + pen, perp);
}
`

export const RAY_SPHERE = /* glsl */ `
vec2 raySphere(vec3 ro, vec3 rd, float R) {
  float b = dot(ro, rd);
  float c = dot(ro, ro) - R * R;
  float disc = b * b - c;
  if (disc < 0.0) return vec2(1e9, -1e9);
  float s = sqrt(disc);
  return vec2(-b - s, -b + s);
}
`

export const OCCLUDERS = /* glsl */ `
uniform vec4 uOccluders[4];
uniform int uOccluderCount;
uniform float uSunAngular;
float occluderShadow(vec3 P, vec3 L) {
  float s = 1.0;
  for (int i = 0; i < 4; i++) {
    if (i >= uOccluderCount) break;
    s *= sphereShadow(P, L, uOccluders[i].xyz, uOccluders[i].w, uSunAngular);
  }
  return s;
}
`

/** Vertex shader shared by the sphere-based materials (unit sphere geometry in the body frame). */
export const SPHERE_VERT = /* glsl */ `
#include <common>
#include <logdepthbuf_pars_vertex>
varying vec2 vUv;
varying vec3 vObj;
varying vec3 vNrm;
void main() {
  vUv = uv;
  vObj = position;
  vNrm = normal;
  vec4 mvPosition = modelViewMatrix * vec4(position, 1.0);
  gl_Position = projectionMatrix * mvPosition;
  #include <logdepthbuf_vertex>
}
`
