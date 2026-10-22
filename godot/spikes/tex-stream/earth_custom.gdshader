// The question M1b asks: does a hand-written spatial shader sampling a StreamedTexture2D through a plain sampler2D
// feed the engine's mip feedback? Lambert only, so the StandardMaterial3D control renders the same pixels.
shader_type spatial;
render_mode diffuse_lambert, specular_disabled;

uniform sampler2D albedo : source_color, filter_linear_mipmap_anisotropic;

void fragment() {
	ALBEDO = texture(albedo, UV).rgb;
	ROUGHNESS = 1.0;
}
