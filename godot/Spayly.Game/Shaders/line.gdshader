// Camera-relative additive polyline (PolyLine in src/render/lines.ts). One colour per line; the web's per-vertex fade is
// derived from VERTEX_ID here, so the vertex buffer stays positions-only and one upload per frame suffices.
shader_type spatial;
render_mode unshaded, blend_add, depth_draw_never, cull_disabled, shadows_disabled, fog_disabled;

// Linear RGB; the C# side converts the catalogue's sRGB hex, as three.js does for new THREE.Color('#hex').
uniform vec3 color = vec3(1.0);
uniform float opacity = 0.5;
uniform float brightness = 1.0;
// > 0 darkens toward the first vertex as (i / (n - 1))^fade (a trail's tail); 0 is a flat line (a conic).
uniform float fade = 0.0;
// Vertices in use; the tail beyond it repeats the last point and draws nothing.
uniform int count = 2;

varying float k;

void vertex() {
  float last = float(max(count - 1, 1));
  k = fade > 0.0 ? pow(min(float(VERTEX_ID), last) / last, fade) : 1.0;
}

void fragment() {
  ALBEDO = color * k * brightness;
  ALPHA = opacity;
}
