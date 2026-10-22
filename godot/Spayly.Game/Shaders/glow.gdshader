// Additive glow sprite that keeps a far body visible as a soft point (three's Sprite + radialSprite in planetView/rockView).
// The view sets the quad's basis from the camera; colour is linear and already carries exposure compensation where the web
// applies it.
shader_type spatial;
render_mode unshaded, blend_add, cull_disabled, depth_draw_never;

uniform sampler2D sprite : filter_linear_mipmap, repeat_disable;
uniform vec3 color = vec3(1.0);
uniform float opacity = 1.0;

void fragment() {
	vec4 s = texture(sprite, UV);
	ALBEDO = s.rgb * color * s.a * opacity;
	ALPHA = 1.0;
}
