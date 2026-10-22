// Corona and glow on a camera-facing quad 6 solar radii wide (r = 1 at 3 radii); port of CORONA_FRAG. The view sets the
// quad's basis from the camera each frame, so no billboard maths here.
shader_type spatial;
render_mode unshaded, blend_add, cull_disabled, depth_draw_never;

#include "res://Shaders/common.gdshaderinc"
#include "res://Shaders/star.gdshaderinc"

uniform float time = 0.0;
// Effective temperature, K.
uniform float star_temp = 5772.0;
// Exposure compensation × luminosity term × (1 − 0.7 × big); SunView.
uniform float intensity = 1.0;
// Transient brightening after an impact (SunView.Flare).
uniform float flare = 0.0;

void fragment() {
	vec2 p = UV * 2.0 - 1.0;
	float r = length(p);
	if (r > 1.0) discard;
	float ang = atan(p.y, p.x);
	float rays = fbm(vec3(cos(ang) * 3.0, sin(ang) * 3.0, time * 0.04));
	float rays2 = fbm(vec3(ang * 2.0, r * 6.0 - time * 0.08, 1.7));
	float core = exp(-r * 9.0) * 3.5;
	float streaks = smoothstep(1.0, 0.16, r) * (0.3 + 0.7 * rays) * (0.5 + 0.5 * rays2) * pow(max(1.0 - r, 0.0), 1.4) * 1.1;
	float halo = exp(-r * 3.2) * 0.9;
	float edge = smoothstep(1.0, 0.8, r);
	// [SCI] the white-light (K) corona is photospheric light Thomson-scattered by free electrons, which is grey, so it has
	// the photosphere's colour; [ART] brightest channel 1 as in the web, and the ray/streak shapes are noise, not field lines.
	vec3 c = star_rgb(star_temp);
	vec3 color = c / max(max(c.r, c.g), c.b);
	ALBEDO = color * (core + halo + streaks) * edge * intensity * (1.0 + flare);
	ALPHA = 1.0;
}
