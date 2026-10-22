// Probe: the streamed texture is bound and sampled, but only at a constant UV and the shader never touches UV or
// STREAMING_UV. If the resident LOD still follows the camera, the LOD is not coming from this shader's derivatives.
shader_type spatial;
render_mode diffuse_lambert, specular_disabled;

uniform sampler2D albedo : source_color, filter_linear_mipmap_anisotropic;

void fragment() {
	ALBEDO = vec3(0.5) + texture(albedo, vec2(0.58, 0.36)).rgb * 0.001;
	ROUGHNESS = 1.0;
}
