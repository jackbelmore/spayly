// Ballistic ejecta curtain (u_mode 0) and free-flying shatter debris (u_mode 1) as analytic point particles; misc.ts
// EJECTA_VERT/FRAG. Every point's path is closed-form in time, so a frame costs one uniform write, not a particle step.
// Vertex attributes: VERTEX = launch direction (body frame, unit), UV.x = speed (radii per unit time), UV.y = seed.
shader_type spatial;
render_mode unshaded, blend_mix, depth_draw_never, cull_disabled;

uniform float u_t = 0.0;
/// Surface gravity in radii per unit time squared (curtain only).
uniform float u_g = 1.0;
/// Launch point on the unit sphere (curtain) or the body centre (debris).
uniform vec3 u_origin = vec3(0.0, 1.0, 0.0);
/// Pixels × render units: point size is this over the view depth, so ~3 px at the camera's distance.
uniform float u_point_scale = 1.0;
uniform int u_mode = 0;
/// Debris lifetime, same units as u_t.
uniform float u_life = 1.0;
varying float v_life;
varying float v_seed;
varying float v_alive;

void vertex() {
	vec3 dir = VERTEX;
	float speed = UV.x;
	v_seed = UV.y;
	vec3 p;
	if (u_mode == 1) {
		v_life = clamp(u_t / u_life, 0.0, 1.0);
		p = u_origin + dir * speed * u_t;
		v_alive = u_t < u_life ? 1.0 : 0.0;
	} else {
		float up = dot(dir, u_origin);
		float tf = max(2.0 * speed * up / u_g, 1e-4);
		v_life = clamp(u_t / tf, 0.0, 1.0);
		p = u_origin + dir * speed * u_t - u_origin * 0.5 * u_g * u_t * u_t;
		float r = length(p);
		v_alive = (r < 0.999 && u_t > 0.02 * tf) || u_t > tf ? 0.0 : 1.0;
		p = normalize(p) * max(r, 1.0005);
	}
	VERTEX = p;
	vec4 mv = MODELVIEW_MATRIX * vec4(p, 1.0);
	POINT_SIZE = clamp(u_point_scale * (1.0 - 0.6 * v_life) / max(-mv.z, 1e-6), 1.5, 7.0);
}

void fragment() {
	if (v_alive < 0.5) discard;
	vec2 c = POINT_COORD - 0.5;
	float d2 = dot(c, c);
	if (d2 > 0.25) discard;
	// ×5 summed to flat white wherever the points overlapped (a shatter's 6000 of them round the whole body); ×2.2 keeps the hue.
	vec3 hot = vec3(1.0, 0.7, 0.32) * 2.2;
	vec3 warm = vec3(1.0, 0.35, 0.1) * 1.5;
	vec3 ash = vec3(0.35, 0.33, 0.3);
	float k = v_life;
	vec3 col = k < 0.35 ? mix(hot, warm, k / 0.35) : mix(warm, ash, (k - 0.35) / 0.65);
	ALBEDO = col * (0.7 + 0.6 * v_seed);
	ALPHA = clamp((1.0 - d2 * 4.0) * (1.0 - 0.7 * k), 0.0, 1.0);
}
