#[compute]
#version 450
// One invocation: close the step (time += the dt just taken) and choose the next dt from the reduced candidate, capped
// by dt_max and a growth factor over the last one, or the fixed dt of the parity mode. The GPU never waits on the CPU.
#include "sph_common.glslinc"
layout(local_size_x = 1) in;
layout(set = 0, binding = 0, std430) restrict buffer Params { float dt; float time; uint dt_bits; uint steps; float dt_max; float growth; float fixed_dt; uint pad; } params;

void main() {
  // Reading pc keeps the push-constant block in the SPIR-V, so SphGpu's common 80-byte push is valid for this pipeline.
  if (gl_GlobalInvocationID.x != pc.count) return;
  float taken = params.dt;
  float cand = uintBitsToFloat(params.dt_bits);
  if (taken > 0.0) {
    params.time += taken;
    params.steps += 1u;
  }
  float next = min(cand, params.dt_max);
  if (taken > 0.0) next = min(next, params.growth * taken);
  if (params.fixed_dt > 0.0) next = params.fixed_dt;
  params.dt = next;
  params.dt_bits = floatBitsToUint(3.0e38);
}
