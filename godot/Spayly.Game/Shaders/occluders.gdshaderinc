// Eclipse shadows from up to four bodies (Shadows.OccludersFor): xyz = centre, w = radius, both in units of this body's
// true radius, in its geometry frame. Separate vec4s rather than an array so the per-frame update is four plain sets.
uniform vec4 occ0;
uniform vec4 occ1;
uniform vec4 occ2;
uniform vec4 occ3;
uniform int occ_count = 0;
// Sun's angular radius seen from the body, rad.
uniform float sun_angular = 0.0047;

float occluder_shadow(vec3 P, vec3 L) {
	float s = 1.0;
	if (occ_count > 0) s *= sphere_shadow(P, L, occ0.xyz, occ0.w, sun_angular);
	if (occ_count > 1) s *= sphere_shadow(P, L, occ1.xyz, occ1.w, sun_angular);
	if (occ_count > 2) s *= sphere_shadow(P, L, occ2.xyz, occ2.w, sun_angular);
	if (occ_count > 3) s *= sphere_shadow(P, L, occ3.xyz, occ3.w, sun_angular);
	return s;
}
