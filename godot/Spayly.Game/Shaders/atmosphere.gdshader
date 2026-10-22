// Single-scattering shell; port of ATMOSPHERE_FRAG (src/render/shaders/atmosphere.ts). Sun and view optical depths are
// analytic (Chapman), the in-scatter integral takes 7 samples each side of the densest point, clustered towards it.
// Additive and double-sided like the web's, so its strength constants carry over unchanged.
shader_type spatial;
render_mode unshaded, blend_add, cull_disabled, depth_draw_never;

#include "res://Shaders/common.gdshaderinc"
#include "res://Shaders/occluders.gdshaderinc"

uniform vec3 sun_dir = vec3(0.0, 0.0, 1.0);
uniform vec3 view_pos = vec3(0.0, 0.0, 3.0);
// Shell radius in body radii; the mesh is a unit sphere scaled by it.
uniform float atm_r = 1.03;
uniform vec3 beta_r = vec3(0.0);
uniform float beta_m = 0.0;
uniform float h_r = 0.001;
uniform float h_m = 0.001;
uniform float g = 0.76;
uniform vec3 sun_color = vec3(1.0);
uniform float sun_intensity = 1.0;
uniform float strength = 1.0;
uniform float dust = 0.0;
uniform vec3 dust_color = vec3(0.62, 0.45, 0.3);

varying vec3 v_obj;

void vertex() {
	// The unit-sphere point, not the shell point (VERTEX * atm_r), exactly as the web's vObj: its rays aim at the planet's
	// limb from across the whole shell, which spreads the ~1 px physical limb glow over the shell's thickness. That spread
	// is the rim the web shows (and what its strength constants were tuned against); the "correct" ray gives a hairline.
	v_obj = VERTEX;
}

void fragment() {
	vec3 ro = view_pos;
	vec3 rd = normalize(v_obj - ro);
	vec2 ta = ray_sphere(ro, rd, atm_r);
	if (ta.y <= 0.0) discard;
	float t0 = max(ta.x, 0.0);
	float t1 = ta.y;
	vec2 tp = ray_sphere(ro, rd, 1.0);
	if (tp.x > 0.0) t1 = min(t1, tp.x);
	if (t1 <= t0) discard;
	float tc = clamp(-dot(ro, rd), t0, t1);
	vec3 L = normalize(sun_dir);
	float XR = 1.0 / h_r;
	float XM = 1.0 / h_m;
	float dust_mul = 1.0 + 1.3 * dust;
	vec3 sum_r = vec3(0.0);
	vec3 sum_m = vec3(0.0);
	const int NS = 7;
	for (int side = 0; side < 2; side++) {
		float t_end = side == 0 ? t0 : t1;
		float len = abs(t_end - tc);
		if (len < 1e-7) continue;
		float dir = side == 0 ? -1.0 : 1.0;
		for (int i = 0; i < NS; i++) {
			float u = (float(i) + 0.5) / float(NS);
			float t = tc + dir * u * u * len;
			float ds = 2.0 * u * len / float(NS);
			vec3 p = ro + rd * t;
			float r = length(p);
			float h = max(r - 1.0, 0.0);
			vec3 up = p / r;
			float dR = exp(-h / h_r);
			float dM = exp(-h / h_m) * dust_mul;
			float cs = dot(up, L);
			float x0 = sqrt(max(1.0 - cs * cs, 0.0)) * r;
			if (cs < 0.0 && x0 < 1.0) continue;
			float col_sr = h_r * chapman(XR, h / h_r, cs);
			float col_sm = h_m * chapman(XM, h / h_m, cs) * dust_mul;
			float cv = dot(up, -rd);
			float col_vr = h_r * chapman(XR, h / h_r, cv);
			float col_vm = h_m * chapman(XM, h / h_m, cv) * dust_mul;
			vec3 tau = beta_r * (col_sr + col_vr) + beta_m * 1.1 * (col_sm + col_vm);
			vec3 attn = exp(-tau) * occluder_shadow(p, L);
			sum_r += attn * dR * ds;
			sum_m += attn * dM * ds;
		}
	}
	float mu = dot(rd, L);
	float ph_r = 3.0 / (16.0 * PI) * (1.0 + mu * mu);
	float g2 = g * g;
	float ph_m = 3.0 / (8.0 * PI) * ((1.0 - g2) * (1.0 + mu * mu)) / ((2.0 + g2) * pow(1.0 + g2 - 2.0 * g * mu, 1.5));
	vec3 mie_color = mix(vec3(1.0), dust_color, dust);
	vec3 col = sun_color * sun_intensity * (beta_r * ph_r * sum_r + beta_m * ph_m * sum_m * mie_color);
	ALBEDO = col * strength;
	ALPHA = 1.0;
}
