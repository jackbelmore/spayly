// The "Keep inside" sphere (Render/KeepSphereOverlay.cs): a faint shell that brightens toward its silhouette, with the
// ecliptic-plane equator drawn as a band, so the sphere reads as a volume and where it cuts the orbital plane stays clear.
// Drawn over everything (no depth test) as the aim overlay is: the point is to see what is inside, planets included.
shader_type spatial;
render_mode unshaded, blend_mix, depth_draw_never, depth_test_disabled, cull_disabled, shadows_disabled, fog_disabled;

uniform vec3 tint = vec3(0.45, 0.85, 1.0);
uniform float strength = 1.0;

varying vec3 local;
varying float inside;

void vertex() {
  local = VERTEX;
  // Camera inside the unit sphere (model space): zoomed in on a planet, every pixel is then 'rim' and the shell tinted the whole view.
  inside = length((inverse(MODEL_MATRIX) * vec4(CAMERA_POSITION_WORLD, 1.0)).xyz) < 1.0 ? 1.0 : 0.0;
}

void fragment() {
  float rim = pow(1.0 - abs(dot(normalize(NORMAL), normalize(VIEW))), 3.0);
  // The unit sphere's y is the render frame's up, which is the ecliptic pole: y = 0 is the orbital plane.
  float equator = exp(-pow(local.y / 0.008, 2.0));
  ALBEDO = tint * (1.0 + 1.5 * equator);
  ALPHA = clamp(((0.08 + 0.5 * rim) * mix(1.0, 0.2, inside) + 0.7 * equator) * strength, 0.0, 0.9);
}
