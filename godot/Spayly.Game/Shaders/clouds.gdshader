// Cloud shell (1.0045 radii, rotated by the view for drift); port of CLOUD_FRAG.
shader_type spatial;
render_mode unshaded, blend_mix, cull_back, depth_draw_never;

#include "res://Shaders/common.gdshaderinc"
#include "res://Shaders/occluders.gdshaderinc"

uniform sampler2D clouds_map : hint_default_black, filter_linear_mipmap_anisotropic, repeat_enable;
uniform vec3 sun_dir = vec3(0.0, 0.0, 1.0);
uniform vec3 sun_color = vec3(1.0);
uniform float sun_intensity = 1.0;
uniform float dust = 0.0;

varying vec3 v_obj;

void vertex() {
	v_obj = VERTEX;
}

void fragment() {
	vec2 uv = body_uv(UV);
	STREAMING_UV = uv;
	vec3 N = normalize(v_obj);
	vec3 L = normalize(sun_dir);
	float lit = clamp(dot(N, L) * 1.15 + 0.08, 0.0, 1.0);
	float shadow = occluder_shadow(N, L);
	float c = texture(clouds_map, uv).r;
	float alpha = smoothstep(0.04, 0.55, c) * (1.0 - 0.5 * dust);
	vec3 tint = mix(vec3(1.0), vec3(0.75, 0.6, 0.45), dust);
	ALBEDO = tint * sun_color * sun_intensity * (lit * shadow * 0.95 + 0.015);
	ALPHA = alpha;
}
