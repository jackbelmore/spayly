// Same Lambert shader, but the equirectangular UV is computed from the object-space position instead of read from UV,
// as a planet shader that ignores mesh UVs would. Without STREAMING_UV the engine's feedback pass has no UV to use.
shader_type spatial;
render_mode diffuse_lambert, specular_disabled;

uniform sampler2D albedo : source_color, filter_linear_mipmap_anisotropic;
uniform bool write_streaming_uv = false;
varying vec3 obj_pos;

void vertex() {
	obj_pos = VERTEX;
}

void fragment() {
	vec3 n = normalize(obj_pos);
	vec2 uv = vec2(fract(atan(n.x, n.z) / TAU), acos(clamp(n.y, -1.0, 1.0)) / PI);
	if (write_streaming_uv) {
		STREAMING_UV = uv;
	}
	ALBEDO = texture(albedo, uv).rgb;
	ROUGHNESS = 1.0;
}
