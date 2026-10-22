// T2 debris (Render/DebrisTier.cs): one point per particle slot, its position fetched by VERTEX_ID from the RGBA32F
// texture the compute kernels write (spike M1a route 1), so the draw needs no CPU round trip.
// Texel: xyz = km from the debris origin body in sim axes (z up); w = 1 + age_q·4096 + stream·2048 + speed_q (11 bits),
// or < 1 for no particle.
shader_type spatial;
render_mode unshaded, blend_add, depth_draw_never, cull_disabled, shadows_disabled, fog_disabled;

uniform sampler2D pos_tex : filter_nearest;
uniform int tex_w = 1024;
// Camera-relative render position of the origin body, after the depth squeeze (render units).
uniform vec3 origin_render = vec3(0.0);
// Render units per km of offset from the origin body: 1e-3 × its squeeze factor × its diorama inflation.
uniform float offset_scale = 0.001;
uniform float point_size = 1.5;
// [ART] Tidal-stream particles: at least this many px, and glowing stellar plasma rather than cooling rock.
uniform float stream_point_size = 3.0;
// Base brightness × exposure compensation, so auto-exposure neither blinds nor hides the cloud.
uniform float intensity = 1.0;

varying vec3 v_color;

void vertex() {
  vec4 t = texelFetch(pos_tex, ivec2(VERTEX_ID % tex_w, VERTEX_ID / tex_w), 0);
  if (t.w < 1.0) {
    // No particle in this slot: park it at the eye, where the near plane clips it, at size 0 (as the belt does).
    VERTEX = vec3(0.0);
    POINT_SIZE = 0.0;
    v_color = vec3(0.0);
  } else {
    float packed = t.w - 1.0;
    float age_q = floor(packed / 4096.0);
    float rest = packed - age_q * 4096.0;
    bool stream = rest >= 2048.0;
    float speed = (rest - (stream ? 2048.0 : 0.0)) / 2047.0;
    float age = age_q / 4095.0;
    VERTEX = origin_render + vec3(t.x, t.z, -t.y) * offset_scale;
    POINT_SIZE = stream ? max(point_size, stream_point_size) : point_size;
    // Fast, young ejecta glow hot; slow or old material cools through warm to ash (the effects' ejecta palette).
    float heat = sqrt(speed) * (1.0 - age);
    // ×4 over a few thousand additive points clipped a whole shatter's shell to uniform white; ×1.8 keeps its orange.
    vec3 hot = vec3(1.0, 0.7, 0.32) * 1.8;
    vec3 warm = vec3(1.0, 0.35, 0.1) * 1.5;
    vec3 ash = vec3(0.35, 0.33, 0.3) * 0.6;
    v_color = heat > 0.5 ? mix(warm, hot, (heat - 0.5) * 2.0) : mix(ash, warm, heat * 2.0);
    if (stream) v_color = mix(vec3(1.0, 0.85, 0.7) * 3.0, vec3(1.0, 0.5, 0.2) * 2.5, age);
  }
}

void fragment() {
  ALBEDO = v_color * intensity;
}
