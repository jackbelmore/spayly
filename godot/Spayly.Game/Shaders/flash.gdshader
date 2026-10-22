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
uniform sampler2D depth_tex : hint_depth_texture, filter_nearest;

void vertex() {
	if (mode == 0) {
		// Billboard: the node's scale is the sprite's full width, as a three.js Sprite's scale is.
		vec3 c = (MODELVIEW_MATRIX * vec4(0.0, 0.0, 0.0, 1.0)).xyz;
		float s = length(MODELVIEW_MATRIX[0].xyz);
		VERTEX = c + vec3(VERTEX.xy * s, 0.0);
	} else {
		VERTEX = (MODELVIEW_MATRIX * vec4(VERTEX, 1.0)).xyz;
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
