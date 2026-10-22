// Fireball / plume: a noise-bulged sphere cooling hot → mid → cool → smoke (misc.ts FIREBALL_VERT/FRAG).
shader_type spatial;
render_mode unshaded, blend_mix, depth_draw_never, cull_back;

/// Normalised age, 0 at impact, 1 when gone.
uniform float t_norm = 0.0;
/// Wall-clock seconds, so the boil keeps moving while the sim is paused.
uniform float u_time = 0.0;
uniform float seed = 0.0;
varying vec3 v_obj;

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

void vertex() {
	v_obj = VERTEX;
	float n = fbm(VERTEX * 2.2 + vec3(seed, u_time * 0.25, 0.0));
	VERTEX *= 1.0 + (n - 0.5) * (0.45 + 0.35 * t_norm);
}

void fragment() {
	vec3 N = normalize(v_obj);
	float n = fbm(N * 3.0 + vec3(seed) + vec3(0.0, u_time * 0.35, 0.0));
	float n2 = fbm(N * 9.0 - vec3(u_time * 0.2));
	vec3 hot = vec3(1.0, 0.96, 0.85) * 9.0;
	vec3 mid = vec3(1.0, 0.45, 0.1) * 3.5;
	vec3 cool = vec3(0.55, 0.12, 0.03) * 1.2;
	vec3 smoke = vec3(0.14, 0.12, 0.1);
	float t = t_norm;
	vec3 col = t < 0.25 ? mix(hot, mid, t / 0.25) : t < 0.55 ? mix(mid, cool, (t - 0.25) / 0.3) : mix(cool, smoke, (t - 0.55) / 0.45);
	col *= 0.55 + 0.9 * n + 0.2 * n2;
	// The undisplaced sphere normal against the view ray, both view space: the web's object-space rim term.
	float rim = 1.0 - clamp(dot(normalize(NORMAL), VIEW), 0.0, 1.0);
	ALBEDO = col;
	ALPHA = clamp(smoothstep(0.0, 0.03, t) * (t < 0.8 ? 1.0 : 1.0 - (t - 0.8) / 0.2) * (1.0 - 0.6 * rim * rim), 0.0, 1.0);
}
