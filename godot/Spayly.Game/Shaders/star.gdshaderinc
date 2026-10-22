// Star colour LUT and cellular noise shared by sun.gdshader and corona.gdshader.

// 256 texels, log-spaced 1000–40000 K, linear sRGB with luminance 1 (Render/StarColor.cs, from Spayly.Sim/Photometry.cs).
uniform sampler2D star_lut : filter_linear, repeat_disable;

// [SCI] Planck × CIE 1931 → linear sRGB (D65), hue only; brightness is the caller's.
vec3 star_rgb(float temp_k) {
	float t = clamp(log(temp_k / 1000.0) / log(40.0), 0.0, 1.0);
	return texture(star_lut, vec2((0.5 + 255.0 * t) / 256.0, 0.5)).rgb;
}

// PCG3D (Jarzynski & Olano, "Hash Functions for GPU Rendering", JCGT 2020): integer, so cell indices in the hundreds
// hash cleanly where a sin() hash falls apart in float32.
uvec3 pcg3d(uvec3 v) {
	v = v * 1664525u + 1013904223u;
	v.x += v.y * v.z; v.y += v.z * v.x; v.z += v.x * v.y;
	v ^= v >> 16u;
	v.x += v.y * v.z; v.y += v.z * v.x; v.z += v.x * v.y;
	return v;
}

// Worley noise: (F1, F2, life) with F1/F2 the nearest and second-nearest feature distances in cell units and life in
// [0, 1] the nearest cell's point in its birth-to-death cycle. Features orbit inside their cells and every cell's
// brightness cycles once per unit of phase, both periodic in phase, so the caller may wrap phase to [0, 1).
vec3 worley(vec3 p, float phase) {
	vec3 ip = floor(p);
	vec3 fp = p - ip;
	float f1 = 8.0;
	float f2 = 8.0;
	float seed = 0.0;
	for (int z = -1; z <= 1; z++) {
		for (int y = -1; y <= 1; y++) {
			for (int x = -1; x <= 1; x++) {
				vec3 o = vec3(float(x), float(y), float(z));
				vec3 r = vec3(pcg3d(uvec3(ivec3(ip + o) + ivec3(32768)))) * (1.0 / 4294967295.0);
				vec3 q = o + 0.5 + 0.5 * (r - 0.5) + 0.22 * sin(6.2831853 * (phase + r.zxy));
				float d = length(q - fp);
				if (d < f1) {
					f2 = f1;
					f1 = d;
					seed = r.x;
				} else if (d < f2) {
					f2 = d;
				}
			}
		}
	}
	float life = sin(3.14159265 * (phase + seed));
	return vec3(f1, f2, life * life);
}
