// Additive round point sprites for the belt and the star field (POINTS_VERT/POINTS_FRAG in src/render/shaders/misc.ts).
// Per point: linear colour in COLOR.rgb, size in pixels in UV.x.
shader_type spatial;
render_mode unshaded, blend_add, depth_draw_never, cull_disabled, shadows_disabled, fog_disabled;

// Device pixel ratio (the web's uScale).
uniform float point_scale = 1.0;
// Base alpha times exposure compensation (the web's uAlpha).
uniform float alpha = 1.0;
// 0: VERTEX is a camera-relative render position (belt). > 0: VERTEX is a unit direction placed at this fraction of the
// far plane, so a camera-anchored backdrop sits behind everything the depth squeeze keeps inside 0.79 × far.
uniform float far_fraction = 0.0;

varying vec3 v_color;

void vertex() {
  v_color = COLOR.rgb;
  POINT_SIZE = UV.x * point_scale;
  if (far_fraction > 0.0) {
    // Reverse-Z puts the far plane at clip depth 0; taking the larger end keeps this right either way.
    vec4 a = INV_PROJECTION_MATRIX * vec4(0.0, 0.0, 0.0, 1.0);
    vec4 b = INV_PROJECTION_MATRIX * vec4(0.0, 0.0, 1.0, 1.0);
    float far = max(abs(a.z / a.w), abs(b.z / b.w));
    VERTEX = normalize(VERTEX) * far * far_fraction;
  } else if (VERTEX == vec3(0.0)) {
    // A swallowed particle: the belt parks it at the eye, where it is clipped anyway; size 0 as the web does.
    POINT_SIZE = 0.0;
  }
}

void fragment() {
  vec2 c = POINT_COORD - 0.5;
  float d2 = dot(c, c);
  if (d2 > 0.25) discard;
  float a = smoothstep(0.25, 0.05, d2);
  ALBEDO = v_color * a * alpha;
}
