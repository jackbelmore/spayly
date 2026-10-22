// Shared chunks; port of src/render/shaders/common.ts. Every body shader works in the body's geometry frame (unit sphere,
// pole +y, prime meridian +x), the frame Orientation.cs, crater maps and the uniforms the views set all agree on.

float hash3(vec3 p) {
	p = fract(p * 0.3183099 + vec3(0.1, 0.2, 0.3));
	p *= 17.0;
	return fract(p.x * p.y * p.z * (p.x + p.y + p.z));
}

float vnoise(vec3 p) {
	vec3 i = floor(p);
	vec3 f = fract(p);
	f = f * f * (3.0 - 2.0 * f);
	return mix(
		mix(mix(hash3(i + vec3(0, 0, 0)), hash3(i + vec3(1, 0, 0)), f.x), mix(hash3(i + vec3(0, 1, 0)), hash3(i + vec3(1, 1, 0)), f.x), f.y),
		mix(mix(hash3(i + vec3(0, 0, 1)), hash3(i + vec3(1, 0, 1)), f.x), mix(hash3(i + vec3(0, 1, 1)), hash3(i + vec3(1, 1, 1)), f.x), f.y),
		f.z);
}

float fbm(vec3 p) {
	float v = 0.0;
	float a = 0.5;
	for (int i = 0; i < 5; i++) {
		v += a * vnoise(p);
		p = p * 2.03 + vec3(1.7, 9.2, 3.1);
		a *= 0.5;
	}
	return v;
}

// Column density of an exponential atmosphere along a ray, in units of (surface density x scale height).
// X = planet radius / scale height, h = altitude / scale height, c = cosine of the ray's zenith angle (Schüler, GPU Pro 3).
float chapman(float X, float h, float c) {
	float cs = sqrt((X + h) * 1.5707963);
	if (c >= 0.0) return cs / (cs * c + 1.0) * exp(-h);
	float x0 = max(sqrt(1.0 - c * c) * (X + h), X);
	float c0 = sqrt(x0 * 1.5707963);
	return 2.0 * c0 * exp(X - x0) - cs / (1.0 - cs * c) * exp(-h);
}

// Soft shadow of a sphere (centre C, radius R) on point P lit from direction L by a sun of angular radius sun_ang.
float sphere_shadow(vec3 P, vec3 L, vec3 C, float R, float sun_ang) {
	vec3 d = C - P;
	float t = dot(d, L);
	if (t <= 0.0) return 1.0;
	float perp = length(d - t * L);
	float pen = max(t * sun_ang, 1e-6);
	return smoothstep(R - pen, R + pen, perp);
}

vec2 ray_sphere(vec3 ro, vec3 rd, float R) {
	float b = dot(ro, rd);
	float c = dot(ro, ro) - R * R;
	float disc = b * b - c;
	if (disc < 0.0) return vec2(1e9, -1e9);
	float s = sqrt(disc);
	return vec2(-b - s, -b + s);
}

// Godot's SphereMesh puts u = 0 at +z (x = sin 2πu, z = cos 2πu); three.js SphereGeometry, which every map, crater and
// Orientation.BodyDirToUv follow, puts it at −x. A quarter turn apart; v = 0 is the north pole in both once Godot's
// top-down image rows are accounted for.
vec2 body_uv(vec2 mesh_uv) {
	return vec2(mesh_uv.x + 0.25, mesh_uv.y);
}
