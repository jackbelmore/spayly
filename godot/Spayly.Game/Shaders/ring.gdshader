// Planetary ring in the body's equator plane (mesh in body radii); port of RING_FRAG: planet shadow on the ring,
// lit-side/unlit-side asymmetry, forward-scattered light through thin parts seen from the dark side.
shader_type spatial;
render_mode unshaded, blend_mix, cull_disabled, depth_draw_never;

#include "res://Shaders/common.gdshaderinc"

uniform sampler2D ring_map : source_color, filter_linear, repeat_disable;
uniform float inner = 1.2;
uniform float outer = 2.3;
uniform vec3 sun_dir = vec3(0.0, 0.0, 1.0);
uniform vec3 sun_color = vec3(1.0);
uniform float sun_intensity = 1.0;
uniform vec3 view_pos = vec3(0.0, 0.0, 3.0);
uniform float sun_angular = 0.0047;

varying vec3 v_obj;

void vertex() {
	v_obj = VERTEX;
}

void fragment() {
	float r = length(v_obj.xz);
	float u = (r - inner) / (outer - inner);
	if (u < 0.0 || u > 1.0) discard;
	vec4 tex = texture(ring_map, vec2(u, 0.5));
	float alpha = tex.a;
	if (alpha < 0.01) discard;
	vec3 L = normalize(sun_dir);
	vec3 V = normalize(view_pos - v_obj);
	float sun_elev = abs(L.y);
	float same_side = step(0.0, L.y * V.y);
	float lighting = mix(0.3, 1.0, same_side) * (0.3 + 0.7 * sun_elev) + 0.35 * (1.0 - same_side) * (1.0 - alpha);
	float shadow = sphere_shadow(v_obj, L, vec3(0.0), 1.0, sun_angular);
	ALBEDO = tex.rgb * sun_color * sun_intensity * lighting * shadow;
	ALPHA = alpha;
}
