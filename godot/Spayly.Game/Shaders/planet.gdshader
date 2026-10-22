// Planet and moon surface; port of PLANET_FRAG (src/render/shaders/planet.ts). Unshaded: the Sun's light is computed here
// from sun_dir/sun_intensity as the web does (the scene's OmniLight lights only StandardMaterial3D), then Godot's ACES
// tonemap and exposure apply as the web's OutputPass does.
shader_type spatial;
render_mode unshaded, cull_back, depth_draw_opaque;

#include "res://Shaders/common.gdshaderinc"
#include "res://Shaders/occluders.gdshaderinc"

uniform sampler2D day_map : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D night_map : source_color, hint_default_black, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D specular_map : hint_default_black, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D normal_map : hint_normal, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D clouds_map : hint_default_black, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D damage_map : filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D ring_map : source_color, filter_linear, repeat_disable;
uniform float has_night = 0.0;
uniform float has_specular = 0.0;
uniform float has_normal = 0.0;
uniform float has_clouds = 0.0;
uniform float has_ring = 0.0;
uniform float has_damage = 0.0;
uniform float cloud_shift = 0.0;
// Body frame: unit vector to the Sun; camera position in body radii.
uniform vec3 sun_dir = vec3(0.0, 0.0, 1.0);
uniform vec3 sun_color = vec3(1.0);
uniform float sun_intensity = 1.0;
uniform vec3 view_pos = vec3(0.0, 0.0, 3.0);
// Up to 12 heat spots: texel (i, 0) = body-frame direction and angular radius, texel (i, 1).r = glow. A texture rather than
// uniform arrays because a packed-array uniform costs a managed allocation on every per-frame set from C#.
uniform sampler2D heat_tex : filter_nearest, repeat_disable;
uniform int heat_count = 0;
uniform float magma = 0.0;
uniform float climate = 0.0;
uniform float time = 0.0;
uniform float gas = 0.0;
uniform float ring_inner = 0.0;
uniform float ring_outer = 0.0;
uniform vec3 beta_r = vec3(0.0);
uniform float beta_m = 0.0;
uniform float h_r = 0.001;
uniform float h_m = 0.001;
uniform float atmo_on = 0.0;
uniform float dust = 0.0;

varying vec3 v_obj;

void vertex() {
	v_obj = VERTEX;
}

float ring_shadow(vec3 P, vec3 L) {
	if (has_ring < 0.5 || abs(L.y) < 1e-4) return 1.0;
	float t = -P.y / L.y;
	if (t <= 0.0) return 1.0;
	vec3 hit = P + t * L;
	float r = length(hit.xz);
	if (r < ring_inner || r > ring_outer) return 1.0;
	float a = texture(ring_map, vec2((r - ring_inner) / (ring_outer - ring_inner), 0.5)).a;
	return 1.0 - 0.92 * a;
}

void fragment() {
	vec2 uv = body_uv(UV);
	// Streaming feedback reads this coordinate's derivatives (spike M1b), so it is written whatever else happens.
	STREAMING_UV = uv;
	vec3 N0 = normalize(v_obj);
	vec3 N = N0;
	if (has_normal > 0.5) {
		vec3 T = normalize(vec3(N0.z, 0.0, -N0.x));
		vec3 B = normalize(cross(N0, T));
		// Imported as a normal map (RG compressed), so z is rebuilt as Godot's NORMAL_MAP does.
		vec2 nxy = texture(normal_map, uv).xy * 2.0 - 1.0;
		vec3 nm = vec3(nxy, sqrt(max(0.0, 1.0 - dot(nxy, nxy))));
		N = normalize(T * nm.x * 0.7 + B * nm.y * 0.7 + N0 * nm.z);
	}
	vec3 L = normalize(sun_dir);
	vec3 V = normalize(view_pos - N0);
	float NdotL = dot(N, L);
	float NdotL0 = dot(N0, L);
	float lit = clamp(NdotL, 0.0, 1.0);
	float shadow = occluder_shadow(N0, L) * ring_shadow(N0, L);

	vec3 albedo = texture(day_map, uv).rgb;
	vec3 dmg = has_damage > 0.5 ? texture(damage_map, uv).rgb * 2.0 : vec3(1.0);
	albedo *= dmg;

	float frozen = clamp(-climate, 0.0, 1.0);
	float scorched = clamp(climate, 0.0, 1.0);
	float lat = abs(N0.y);
	float ice_line = 1.0 - frozen * 1.08;
	float ice = smoothstep(ice_line - 0.14, ice_line + 0.04, lat) * step(0.001, frozen);
	albedo = mix(albedo, vec3(0.92, 0.95, 1.0), ice * 0.95);
	if (scorched > 0.0) albedo = mix(albedo, vec3(0.40, 0.28, 0.18) * (0.6 + 0.5 * fbm(N0 * 8.0)), scorched * 0.85);
	if (has_clouds > 0.5) {
		float c = texture(clouds_map, vec2(uv.x - cloud_shift, uv.y)).r;
		albedo *= 1.0 - 0.5 * c * (1.0 - scorched);
	}

	vec3 sun_trans = vec3(1.0);
	vec3 view_trans = vec3(1.0);
	if (atmo_on > 0.5) {
		float XR = 1.0 / h_r;
		float XM = 1.0 / h_m;
		float c_sun = max(NdotL0, 0.0);
		float col_r = h_r * chapman(XR, 0.0, c_sun);
		float col_m = h_m * chapman(XM, 0.0, c_sun);
		sun_trans = exp(-(beta_r * col_r + beta_m * 1.1 * col_m) * (1.0 + 1.0 * dust));
		float c_view = max(dot(N0, V), 0.0);
		float v_r = h_r * chapman(XR, 0.0, c_view);
		float v_m = h_m * chapman(XM, 0.0, c_view);
		view_trans = exp(-(beta_r * v_r + beta_m * 1.1 * v_m) * (1.0 + 1.0 * dust));
	}
	vec3 light = sun_color * sun_intensity * lit * shadow * sun_trans;
	vec3 color = albedo * light;

	if (has_specular > 0.5) {
		float s = texture(specular_map, uv).r * (1.0 - ice);
		vec3 H = normalize(L + V);
		float sp = pow(max(dot(N, H), 0.0), 220.0);
		color += sun_color * sun_intensity * sp * s * 0.5 * shadow * sun_trans * smoothstep(0.0, 0.1, NdotL0);
	}
	color *= mix(1.0, pow(max(dot(N0, V), 0.0), 0.45) * 1.05, gas * 0.7);
	color *= view_trans;

	if (has_night > 0.5) {
		float night = smoothstep(0.08, -0.12, NdotL0);
		vec3 nl = texture(night_map, uv).rgb;
		color += nl * night * (1.0 - scorched) * (1.0 - 0.7 * frozen) * smoothstep(0.6, 0.95, dmg.g);
	}

	for (int i = 0; i < 12; i++) {
		if (i >= heat_count) break;
		vec4 h = texelFetch(heat_tex, ivec2(i, 0), 0);
		float ang = acos(clamp(dot(N0, h.xyz), -1.0, 1.0));
		float r = ang / max(h.w, 1e-5);
		if (r < 1.2) {
			float g = texelFetch(heat_tex, ivec2(i, 1), 0).r;
			float n = 0.55 + 0.45 * fbm(N0 * 60.0 + time * 0.02);
			float floor_glow = smoothstep(1.05, 0.55, r);
			color += vec3(1.0, 0.32, 0.05) * floor_glow * g * g * g * 0.9 * n;
			color += vec3(1.0, 0.72, 0.4) * smoothstep(0.45, 0.0, r) * g * g * g * g * 1.0;
		}
	}

	if (magma > 0.001) {
		float n = fbm(N0 * 6.0 + vec3(0.0, time * 0.01, 0.0));
		float cracks = smoothstep(0.42, 0.68, n);
		float fine = 0.7 + 0.5 * fbm(N0 * 28.0);
		vec3 lava = mix(vec3(0.45, 0.04, 0.0), vec3(1.0, 0.5, 0.12), cracks) * fine;
		vec3 crust = color * (1.0 - 0.8 * magma) + vec3(0.12, 0.02, 0.0) * magma;
		color = mix(crust, lava * 1.5, magma * (0.2 + 0.8 * cracks));
	}

	ALBEDO = color;
}
