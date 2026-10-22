// Asteroid or fragment on a displaced welded icosphere; port of ROCK_VERT/ROCK_FRAG (src/render/shaders/misc.ts). The
// welded mesh has no UV seam, so the undisplaced direction rides in UV (x, y) and UV2.x (z) and the map is looked up from it.
shader_type spatial;
render_mode unshaded, cull_back, depth_draw_opaque;

#include "res://Shaders/common.gdshaderinc"

uniform sampler2D map : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform float has_map = 0.0;
uniform vec3 seed_offset = vec3(0.0);
uniform vec3 sun_dir = vec3(0.0, 0.0, 1.0);
uniform vec3 sun_color = vec3(1.0);
uniform float sun_intensity = 1.0;
uniform vec3 view_pos = vec3(0.0, 0.0, 3.0);
uniform float magma = 0.0;
uniform float time = 0.0;
uniform vec3 tint = vec3(0.55, 0.5, 0.45);

varying vec3 v_dir;
varying vec3 v_obj;
varying vec3 v_nrm;

void vertex() {
	v_dir = vec3(UV.x, UV.y, UV2.x);
	v_obj = VERTEX;
	v_nrm = NORMAL;
}

// Equirectangular lookup in the layout of three's SphereGeometry and Orientation.BodyDirToUv, v = 0 at the north pole as
// Godot stores image rows. atan wraps at the back meridian, where u jumps 1 → 0 across one pixel and would pick the smallest
// mip; take u from whichever of two parameterisations is continuous here.
vec2 rock_uv(vec3 p) {
	vec3 d = normalize(p);
	float u = atan(d.z, -d.x) / (2.0 * PI);
	float u1 = fract(u);
	float u2 = fract(u + 0.5) - 0.5;
	return vec2(fwidth(u1) <= fwidth(u2) + 1e-6 ? u1 : u2, 0.5 - asin(clamp(d.y, -1.0, 1.0)) / PI);
}

void fragment() {
	vec2 uv = rock_uv(v_dir);
	STREAMING_UV = uv;
	vec3 N = normalize(v_nrm);
	vec3 L = normalize(sun_dir);
	vec3 V = normalize(view_pos - v_obj);
	float lit = max(dot(N, L), 0.0);
	vec3 albedo;
	if (has_map > 0.5) {
		albedo = texture(map, uv).rgb;
	} else {
		// No map: mottling plus grit from 3D noise, which unlike a painted map has no seam and no polar spokes.
		vec3 p = normalize(v_dir) + seed_offset;
		float s = 0.35 + 0.5 * fbm(p * 2.5) + 0.15 * fbm(p * 8.0);
		albedo = pow(s * vec3(0.92, 0.86, 0.78), vec3(2.2));
	}
	albedo *= tint;
	vec3 H = normalize(L + V);
	float spec = pow(max(dot(N, H), 0.0), 24.0) * 0.08;
	vec3 col = albedo * sun_color * sun_intensity * (lit + 0.015) + sun_color * sun_intensity * spec * lit;
	if (magma > 0.001) {
		float n = fbm(normalize(v_obj) * 5.0 + vec3(0.0, time * 0.01, 0.0));
		float cracks = smoothstep(0.45, 0.62, n);
		vec3 lava = mix(vec3(0.45, 0.04, 0.0), vec3(1.0, 0.5, 0.12), cracks) * (0.8 + 0.4 * fbm(normalize(v_obj) * 20.0));
		col = mix(col * (1.0 - 0.8 * magma), lava * 1.5, magma * (0.25 + 0.75 * cracks));
	}
	ALBEDO = col;
}
