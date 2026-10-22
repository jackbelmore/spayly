// Photosphere; port of SUN_FRAG (fbm granulation, supergranule cells and spots, limb darkening, optional map), recoloured
// by the Planck LUT and with a physical layer on top: per-channel limb darkening, Worley granules and supergranules at their
// real sizes, umbra/penumbra spots. Realism tags: [SCI] physics, [GAME] a rule chosen for play, [ART] a look. The same
// tags, in prose, are StarColor.RealismNotes.
shader_type spatial;
render_mode unshaded, cull_back, depth_draw_opaque;

#include "res://Shaders/common.gdshaderinc"
#include "res://Shaders/star.gdshaderinc"

// Wall-clock s; drives the web's fbm drift only.
uniform float time = 0.0;
// Granule lifetimes elapsed (SunView, from sim time), wrapped to [0, 1).
uniform float gran_phase = 0.0;
// Supergranule lifetimes elapsed, wrapped to [0, 1).
uniform float super_phase = 0.0;
// Effective temperature, K.
uniform float star_temp = 5772.0;
// Per-channel quadratic limb law I(μ) = 1 − u1 (1 − μ) − u2 (1 − μ)² (Photometry.LimbDarkeningCoefficients).
uniform vec3 limb_u1 = vec3(0.544, 0.619, 0.760);
uniform vec3 limb_u2 = vec3(0.076, 0.044, -0.029);
// 1.3 × exposure compensation: the Sun reads the same whatever the auto-exposure does.
uniform float intensity = 4.0;
uniform vec3 view_pos = vec3(0.0, 0.0, 3.0);
uniform sampler2D map : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform float has_map = 1.0;
// Granulation contrast, 1 = the web's look plus the physical cells; 0 = a smooth disc.
uniform float granulation = 1.0;
// Spot darkness, 1 = the web's look.
uniform float spots = 1.0;
// Spot coverage for this star's temperature and size (SunView.SpotCover): 1 for the Sun, 0 for no spots.
uniform float spot_cover = 1.0;
// fbm threshold where penumbra starts; the web's 0.67 at the Sun, lower for cooler (spottier) stars.
uniform float spot_lo = 0.67;
// Umbra temperature deficit as a fraction of T_eff (SunView.SpotDeficit), 0.29 on the Sun, and the brightness contrast
// that goes with it relative to the Sun's.
uniform float spot_dt = 0.29;
uniform float spot_contrast = 1.0;
// Cells per stellar radius: R / 1000 km and R / 30000 km.
uniform float gran_freq = 695.7;
uniform float super_freq = 23.19;

varying vec3 v_obj;

void vertex() {
	v_obj = VERTEX;
}

void fragment() {
	vec2 uv = body_uv(UV);
	vec2 map_uv = vec2(uv.x + time * 0.0005, uv.y);
	STREAMING_UV = map_uv;
	vec3 N = normalize(v_obj);
	vec3 V = normalize(view_pos - v_obj);
	float mu = clamp(dot(N, V), 0.0, 1.0);
	float x = 1.0 - mu;
	// [SCI] quadratic law per channel from a grey Eddington atmosphere (Photometry.cs): the limb is darker and redder.
	vec3 limb = max(vec3(1.0) - limb_u1 * x - limb_u2 * x * x, vec3(0.0));

	// [ART] the web's fbm mottling, kept as the base so the far view matches the web build.
	float gran = fbm(N * 34.0 + vec3(0.0, time * 0.05, 0.0));
	float cells = fbm(N * 9.0 - vec3(time * 0.02, 0.0, 0.0));
	float bright = 0.72 + 0.45 * mix(0.48, gran, granulation) + 0.3 * cells;

	// Cells per pixel; a layer fades out before its cells shrink under ~2 px, so it never aliases, and averages to no change.
	float px = length(fwidth(N));
	// [SCI] granules ~1000 km, bright hot centres and dark intergranular lanes over ~1/3 of the area, ~15 % RMS contrast;
	// visible only close in. The profile's mean (0.46, measured offline) is subtracted, so fading in shifts no brightness.
	float gw = granulation * (1.0 - smoothstep(0.3, 0.6, px * gran_freq));
	if (gw > 0.001) {
		vec3 w = worley(N * gran_freq, gran_phase);
		float lanes = smoothstep(0.0, 0.22, w.y - w.x);
		float c = lanes * (0.6 + 0.4 * w.z) * (1.1 - 0.5 * w.x);
		bright *= 1.0 + gw * 0.5 * (c - 0.46);
	}
	// [SCI] supergranules ~30000 km; their network (faculae) is faint at disc centre and brightens toward the limb.
	// [GAME] the contrast is raised so the network reads at all.
	float sw = granulation * (1.0 - smoothstep(0.3, 0.6, px * super_freq));
	if (sw > 0.001) {
		vec3 s = worley(N * super_freq, super_phase);
		float net = 1.0 - smoothstep(0.0, 0.08, s.y - s.x);
		bright *= 1.0 + sw * (net - 0.14) * (0.02 + 0.1 * x);
	}

	// [GAME] spots are a threshold on the web's low-frequency fbm (same places as the web), with a penumbra ring and a
	// darker umbra. [SCI] the umbra is spot_dt below T_eff (≈ 4100 K on the Sun) and the penumbra a fifth of that, so
	// both redden; [GAME] brightness contrast is the web's on the Sun, scaled by spot_contrast elsewhere.
	float sn = fbm(N * 12.0 + vec3(3.0, 0.0, time * 0.002));
	float pen = spot_cover * smoothstep(spot_lo, spot_lo + 0.03, sn);
	float umb = spot_cover * smoothstep(spot_lo + 0.045, spot_lo + 0.075, sn);
	bright *= 1.0 - spots * spot_contrast * (0.28 * pen + 0.5 * umb);
	float t_local = star_temp * (1.0 - spots * spot_dt * (0.22 * pen + 0.78 * umb));

	// [ART] the map (false-colour imagery or the procedural one) adds structure only; its hue would fight the Planck colour.
	float map_l = 1.0;
	if (has_map > 0.5) {
		vec3 m = texture(map, map_uv).rgb;
		map_l = dot(m, vec3(0.2126, 0.7152, 0.0722)) / max(max(m.r, m.g), max(m.b, 0.05));
	}
	float base = mix(1.0, map_l, 0.3);

	// [SCI] colour from the LUT at the local temperature; [GAME] intensity is exposure-compensated so the disc lands near
	// white after tone mapping, as the eye sees the Sun, instead of dimming or blowing out with the auto-exposure.
	// [ART] 0.83 is the luminance the web's disc had (its (1, 0.9, 0.72) tint × curve-fit colour), so the mottling keeps its
	// contrast under ACES instead of clipping.
	ALBEDO = star_rgb(t_local) * (0.83 * base * bright) * limb * intensity;
}
