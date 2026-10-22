// earth_procuv.gdshader with every STREAMING_UV token removed: a uniform-guarded write still defines STREAMING_UV_USED.
shader_type spatial;
render_mode diffuse_lambert, specular_disabled;

uniform sampler2D albedo : source_color, filter_linear_mipmap_anisotropic;
varying vec3 obj_pos;

void vertex() {
	obj_pos = VERTEX;
}

void fragment() {
	vec3 n = normalize(obj_pos);
	vec2 uv = vec2(fract(atan(n.x, n.z) / TAU), acos(clamp(n.y, -1.0, 1.0)) / PI);
	ALBEDO = texture(albedo, uv).rgb;
	ROUGHNESS = 1.0;
}
