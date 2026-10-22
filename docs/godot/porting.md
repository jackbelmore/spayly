# Porting src/sim to Spayly.Sim — conventions

Read before touching `godot/Spayly.Sim/`. The web build in `src/sim` is the spec; its tests in `tests/` are the
acceptance criteria. Port a module's test file **first**, then the module, then make the test green without weakening
any tolerance (`.claude/skills/physics-change/SKILL.md` rules apply verbatim).

## Shape
- One C# file per TS module, PascalCase: `Vec3.cs, Constants.cs, Frames.cs, Kepler.cs, Body.cs, Ephemeris.cs,
  SatelliteTypes.cs, Satellites.g.cs (generated), Catalog.cs, Particles.cs, Impact.cs, Collide.cs, Aim.cs, Universe.cs`.
  Namespace `Spayly.Sim`, file-scoped. Public names are the TS names in PascalCase (`elementsToState` → `ElementsToState`,
  `AIM_PAIR_BUDGET` → `AimPairBudget`). Keep the TS doc comments as `///`, with units.
- `Vec3` is `public readonly record struct Vec3(double X, double Y, double Z)` with `+ - * /` operators, unary `-`, and
  static `Dot, Cross, Length, Distance, Normalize, Lerp, AnyPerpendicular, RandomUnit(Func<double>)`. It is a value
  type: where TS mutates `b.pos.x`, C# assigns a new `Vec3`.
- Randomness: `Rng.Mulberry32(uint seed)` returns `Func<double>` and is **bit-exact** with `makeRandom` (`unchecked`
  uint arithmetic; `Math.imul` is the low 32 bits of the product). Every function that takes `rand` takes
  `Func<double>? rand = null` and falls back to `Random.Shared.NextDouble`.
- TS string unions → `enum`s. Interfaces → `sealed class` (mutable state such as `Body`) or `readonly record struct`
  (pure data). `structuredClone` → an explicit `Clone()` that deep-copies. `Float64Array` → `double[]`.
- Callbacks injected from the app (`bodyFrame`, `surfaceAt`) become delegates declared beside `Collide`.
- Numerics: JS `%` and C# `%` agree; `Number.isFinite` → `double.IsFinite`; `Infinity` → `double.PositiveInfinity`;
  `Math.hypot` → write it out. Do not "improve" a formula while porting; a difference from the TS is a bug.
- Optional fields (`posA?`) → nullable (`Vec3?`).
- A module's free functions are statics on a class named for it (`Kepler.SolveKepler`, `Frames.PlaneBasis`); body.ts's
  live on `Body` itself (its constructor is private). `using static` restores the TS spelling. Ω stays `Omega`; ω
  (`omega`) becomes `ArgPeri`, since PascalCase would collide.

## Tests
- xUnit, one class per vitest file (`KeplerTests`, `UniverseTests`, …), one `[Fact]` per `it`, method name = the `it`
  title in PascalCase. At the top of each class, a comment lists the vitest titles in order so the mapping is auditable.
- `toBeCloseTo(x, n)` → `Close.To(x, actual, n)` (`Spayly.Sim.Tests/Close.cs`), never xUnit's precision overload.
  `toBeGreaterThan` → `Assert.True(a > b, $"...")` with the values in the message.
- No timeout tricks: a year of the 39-body system is hundreds of thousands of substeps and must simply pass.
- `dotnet test godot/Spayly.Sim.Tests` is the gate. `Spayly.Sim` has `TreatWarningsAsErrors`.

## Style
4-space indent, `var` where the type is obvious, no `#region`, comments explain *why*. No Godot anywhere in Sim
(`LayeringTests` fails the build of the idea).
