// Additive glow for impact flashes, aim markers, the surface blast ring and meteor streaks (effects.ts flash/ring/streak,
// app.ts hit/spawn markers). One shader, three shapes, so an effect costs no extra shader compile.
//   mode 0: camera-facing radial sprite on a unit quad (the web's radialSprite texture, computed instead of sampled)
//   mode 1: flat colour (blast ring mesh in the body frame)
//   mode 2: gradient along UV.x from color_b to color (a two-point streak line)
// Depth testing is off for the whole shader so the flash reads over the limb as the web's depthTest:false sprite does;
// `occlude` puts it back by hand against the opaque depth for the ring and the streak.
shader_type spatial;
render_mode unshaded, blend_add, depth_draw_never, depth_test_disabled, cull_disabled, skip_vertex_transform;

uniform int mode = 0;
/// Linear HDR colour.
uniform vec3 color = vec3(1.0);
uniform vec3 color_b = vec3(1.0);
uniform float opacity = 1.0;
/// Falloff exponent of the radial sprite (radialSprite's power).
uniform float power = 2.5;
uniform bool occlude = false;
/// Blast ring (mode 1) only: > 0 lays the annulus on the unit sphere as a cap ring of this angular radius, radians, instead of
/// a flat disc in the tangent plane. A flat disc ring of Chicxulub size sank into the planet past its limb and left a jagged band.
uniform float arc = 0.0;
uniform sampler2D depth_tex : hint_depth_texture, filter_nearest;

void vertex() {
	if (mode == 0) {
		// Billboard: the node's scale is the sprite's full width, as a three.js Sprite's scale is.
		vec3 c = (MODELVIEW_MATRIX * vec4(0.0, 0.0, 0.0, 1.0)).xyz;
		float s = length(MODELVIEW_MATRIX[0].xyz);
		VERTEX = c + vec3(VERTEX.xy * s, 0.0);
	} else {
		vec3 v = VERTEX;
		if (mode == 1 && arc > 0.0) {
			// 1.006: above the cloud shell (1.0045), which writes depth and hid a ring at 1.002 over every cloudy region.
			// The annulus is unit-radius in XY facing +Z: its radius (0.93..1) is the fraction of the arc, its angle the azimuth.
			float rho = length(v.xy);
			float th = min(rho * arc, 3.14159);
			v = vec3(sin(th) * v.xy / max(rho, 1e-6), cos(th)) * 1.006;
		}
		VERTEX = (MODELVIEW_MATRIX * vec4(v, 1.0)).xyz;
	}
}

void fragment() {
	// Reverse-Z: a larger depth is nearer, so anything nearer than this fragment hides it.
	if (occlude && FRAGCOORD.z < texture(depth_tex, SCREEN_UV).r) discard;
	vec3 col = color;
	float a = 1.0;
	if (mode == 0) {
		float r = length(UV - 0.5) * 2.0;
		a = pow(max(0.0, 1.0 - r), power);
	} else if (mode == 2) {
		col = mix(color_b, color, UV.x);
	}
	ALBEDO = col;
	ALPHA = clamp(a * opacity, 0.0, 1.0);
}
