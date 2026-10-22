// The Milky Way panorama on a camera-anchored inside-out sphere (src/render/sky.ts). The map is in galactic coordinates;
// each fragment's direction is turned into the sphere's local frame (x toward the galactic centre, y the north galactic
// pole) and looked up with three.js SphereGeometry's own (u, v) convention, so the orientation is the web's exactly.
shader_type spatial;
render_mode unshaded, depth_draw_never, cull_disabled, shadows_disabled, fog_disabled;

uniform sampler2D map : source_color, filter_linear_mipmap, repeat_enable;
// Render-frame basis of the sphere: local +x, +y, +z (sky.ts makeBasis(X, pole, Z)).
uniform vec3 axis_x = vec3(1.0, 0.0, 0.0);
uniform vec3 axis_y = vec3(0.0, 1.0, 0.0);
uniform vec3 axis_z = vec3(0.0, 0.0, 1.0);
// The web's material colour: 0.55 × exposure compensation.
uniform float intensity = 0.55;
// Fraction of the far plane the sphere sits at: behind the squeeze's 0.79 × far and the stars' 0.9 × far.
uniform float far_fraction = 0.95;

varying vec3 dir;

void vertex() {
  dir = normalize(VERTEX);
  vec4 a = INV_PROJECTION_MATRIX * vec4(0.0, 0.0, 0.0, 1.0);
  vec4 b = INV_PROJECTION_MATRIX * vec4(0.0, 0.0, 1.0, 1.0);
  float far = max(abs(a.z / a.w), abs(b.z / b.w));
  VERTEX = dir * far * far_fraction;
}

void fragment() {
  vec3 d = normalize(dir);
  vec3 l = vec3(dot(d, axis_x), dot(d, axis_y), dot(d, axis_z));
  // SphereGeometry: local = (-cos(phi) sin(theta), cos(theta), sin(phi) sin(theta)), u = phi / 2pi, image row = theta / pi.
  float u = fract(atan(l.z, -l.x) / TAU);
  float v = acos(clamp(l.y, -1.0, 1.0)) / PI;
  // u jumps 1 -> 0 across the seam; take its derivatives from whichever of u and u + 0.5 is continuous here, or the
  // seam shows as a line of the smallest mip.
  float u2 = fract(u + 0.5);
  float dux = abs(dFdx(u)) < abs(dFdx(u2)) ? dFdx(u) : dFdx(u2);
  float duy = abs(dFdy(u)) < abs(dFdy(u2)) ? dFdy(u) : dFdy(u2);
  vec3 c = textureGrad(map, vec2(u, v), vec2(dux, dFdx(v)), vec2(duy, dFdy(v))).rgb;
  ALBEDO = c * intensity;
}
