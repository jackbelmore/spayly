// A black hole's accretion disc drawn flat, for when lensing is off (AccretionDisc.cs). With lensing on, LensingShader.cs
// ray-traces the same disc; the profile, g factor, colour table and turbulence below are that kernel's disc_emission(),
// with the photon's angular momentum taken from the straight line to the camera instead of the bent ray.
// Mesh: Meshes.Ring in r_s, pole +y, azimuth atan(-z, x) increasing prograde.
shader_type spatial;
render_mode unshaded, blend_mix, cull_disabled, depth_draw_always, shadows_disabled, fog_disabled;

#include "res://Shaders/common.gdshaderinc"

// Linear RGB by log temperature, 1000 K .. 40000 K (AccretionDisc.BuildColorLut).
uniform sampler2D color_lut : filter_linear, repeat_disable;
// Camera in the disc frame, r_s.
uniform vec3 view_pos = vec3(0.0, 10.0, 40.0);
uniform float r_in = 3.0;
uniform float r_out = 20.0;
// Display temperature at the Shakura-Sunyaev peak, K [GAME].
uniform float peak_k = 9000.0;
// Brightness at the peak times exposure compensation.
uniform float intensity = 6.0;
// 1: the g factor shifts colour by g and brightness by g^4 [SCI]; 0: every radius at rest.
uniform float doppler = 1.0;
uniform float time = 0.0;

varying vec3 disc_pos;

void vertex() {
	disc_pos = VERTEX;
}

vec3 blackbody(float t) {
	float n = float(textureSize(color_lut, 0).x);
	float s = clamp(log(max(t, 1.0) / 1000.0) / log(40.0), 0.0, 1.0);
	return textureLod(color_lut, vec2((s * (n - 1.0) + 0.5) / n, 0.5), 0.0).rgb;
}

// [ART] Keplerian-advected turbulence, two layers restarted half a period apart (LensingShader.cs turbulence()).
float turbulence(float r, float az) {
	float w = 0.6 * pow(r_in / r, 1.5);
	float T = 8.0;
	float ph0 = fract(time / T);
	float ph1 = fract(time / T + 0.5);
	float n0 = fbm(vec3(cos(az - w * ph0 * T) * 2.5, sin(az - w * ph0 * T) * 2.5, log(r) * 7.0));
	float n1 = fbm(vec3(cos(az - w * ph1 * T) * 2.5, sin(az - w * ph1 * T) * 2.5, log(r) * 7.0 + 5.3));
	return mix(n1, n0, 1.0 - abs(2.0 * ph0 - 1.0));
}

void fragment() {
	float r = length(disc_pos.xz);
	if (r < r_in || r > r_out) discard;
	float u = 1.0 / r;
	float rp = 49.0 / 36.0 * r_in;
	// (T / T_peak)^4 of the Shakura-Sunyaev profile.
	float f = max((1.0 - sqrt(r_in / r)) / (r * r * r), 0.0) / ((1.0 - sqrt(r_in / rp)) / (rp * rp * rp));
	float g = 1.0;
	if (doppler > 0.5) {
		// b_z of the photon to the camera, straight-line: (x × k)·pole.
		vec3 k = normalize(view_pos - disc_pos);
		float bz = cross(disc_pos, k).y;
		g = sqrt(max(1.0 - 1.5 * u, 1e-4)) / max(1.0 - sqrt(0.5 * u * u * u) * bz, 1e-3);
	}
	float n = turbulence(r, atan(-disc_pos.z, disc_pos.x));
	float g2 = g * g;
	ALBEDO = blackbody(peak_k * pow(f, 0.25) * g) * intensity * f * g2 * g2 * mix(0.35, 1.45, n);
	ALPHA = clamp(smoothstep(r_in, r_in * 1.1, r) * (1.0 - smoothstep(0.75 * r_out, r_out, r)) * mix(0.55, 1.0, n), 0.0, 1.0);
}
