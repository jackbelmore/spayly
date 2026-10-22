# Rule format

One imperative line. Name what catches a violation, or mark it `(unenforced)`.

Good:
- `sim/` imports nothing outside `src/sim/`. *(`tests/layering.test.ts`)*
- Bump `damage.version` whenever `craters` changes, or render never repaints. *(unenforced)*
- Use `toRender` from `units.ts`; never inline the axis swap. *(unenforced)*

Bad — prose, no enforcement, no consequence:
- "Try to keep the simulation layer clean and well-separated from rendering concerns."
- "The integrator uses leapfrog because symplectic integrators conserve energy well over long runs."
  (That is rationale. It belongs in `docs/design.md`.)

Before writing the rule, ask whether it can be enforced instead. A check in `tests/layering.test.ts` is worth
more than ten lines an agent has to remember.
