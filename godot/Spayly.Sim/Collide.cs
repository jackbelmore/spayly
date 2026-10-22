using System.Globalization;
using Spayly.Sim.Debris;
using static Spayly.Sim.Body;
using static Spayly.Sim.Constants;
using static Spayly.Sim.Vec3;

namespace Spayly.Sim;

/// <summary>
/// StellarMerge: a star took in a body big enough to matter (<see cref="ImpactOutcome.StellarShock"/> says how hard); a swallowed
/// planet stays <see cref="Absorb"/>.
/// </summary>
public enum OutcomeKind { Absorb, Crater, Airburst, Gas, Merge, Shatter, Disperse, HitAndRun, StellarMerge }

/// <summary>Which model decides merge vs disruption. LS12 is the default (rock and ice of 10 km and up); Legacy is the web build's Benz–Asphaug severity.</summary>
public enum OutcomeModel { Legacy, LS12 }

/// <summary>What one resolved collision did; mirrors ImpactOutcome in src/sim/collide.ts.</summary>
public sealed class ImpactOutcome
{
    public required OutcomeKind Kind { get; set; }
    public required Body Target { get; init; }
    public required Body Impactor { get; init; }
    /// <summary>Contact point, world km.</summary>
    public required Vec3 ContactWorld { get; init; }
    /// <summary>Outward unit normal at the contact point, world frame.</summary>
    public required Vec3 NormalWorld { get; init; }
    /// <summary>Same normal in the target's body frame (rotates with the surface).</summary>
    public required Vec3 DirBody { get; init; }
    /// <summary>Unit direction of the impactor's velocity relative to the target, world frame.</summary>
    public required Vec3 VelDirWorld { get; init; }
    /// <summary>km/s.</summary>
    public required double RelSpeed { get; init; }
    /// <summary>Radians from horizontal.</summary>
    public required double Angle { get; init; }
    /// <summary>J, centre-of-mass frame.</summary>
    public required double Energy { get; init; }
    /// <summary>Change of the target's velocity from the momentum exchange, km/s.</summary>
    public required double TargetDeltaV { get; set; }
    public ImpactReport? Report { get; set; }
    public required List<Body> Fragments { get; set; }
    /// <summary>Universe time of the collision, s.</summary>
    public required double Time { get; init; }
    /// <summary>LS12 only: escaping mass too small to be a discrete fragment, handed to a debris tier, kg. 0 in Legacy.</summary>
    public double ParticleTierMass { get; set; }
    /// <summary>LS12 only: velocity the particle-tier mass carries (its momentum is ParticleTierMass × this), km/s.</summary>
    public Vec3 ParticleTierVelocity { get; set; }
    /// <summary>LS12 only: the regime classification that drove the outcome; null in Legacy.</summary>
    public CollisionRegimeResult? Ls12 { get; set; }
    /// <summary>
    /// Star target only: the collision's kinetic energy in the centre-of-mass frame over the pair's binding energy, scaled by the
    /// share of the impactor that overlaps the star (0 for every other target). 0.17 is two Suns meeting on a parabola; 1 is
    /// the whole binding energy.
    /// </summary>
    public double StellarShock { get; set; }
    /// <summary>Black-hole merger only: the two holes before and the remnant right after, for booking what the waves took.</summary>
    public MergerRecord? Merger { get; set; }
}

/// <summary>
/// A black-hole merger: the holes before (M1 the target) and the remnant after, kg, km, km/s. The remnant is lighter by the
/// radiated energy and carries the recoil, so the pair's mass, momentum and angular momentum differ from the remnant's by
/// exactly what the gravitational waves took.
/// </summary>
public readonly record struct MergerRecord(double M1, Vec3 R1, Vec3 V1, double M2, Vec3 R2, Vec3 V2, double Mf, Vec3 Rf, Vec3 Vf, Vec3 Kick)
{
    /// <summary>Mass radiated, kg.</summary>
    public double RadiatedMass => M1 + M2 - Mf;

    /// <summary>What the waves carried off, exact in double-double: the pair's tally minus the remnant's, angular momentum about <paramref name="origin"/>.</summary>
    public Tally Radiated(Vec3 origin) => Tally.Of(M1, R1, V1, origin) + Tally.Of(M2, R2, V2, origin) - Tally.Of(Mf, Rf, Vf, origin);
}

/// <summary>Converts a world direction into the body's rotating frame.</summary>
public delegate Vec3 BodyFrameFn(Body b, Vec3 dirWorld);

/// <summary>Tells whether a body-frame direction points at water (Earth's oceans); Depth in m, passed to the impact model as is.</summary>
public delegate (bool Water, double Depth) SurfaceAtFn(Body b, Vec3 dirBody);

public sealed class CollideOptions
{
    /// <summary>Converts a world direction into the body's rotating frame.</summary>
    public required BodyFrameFn BodyFrame { get; init; }
    /// <summary>Tells whether a body-frame direction points at water (Earth's oceans).</summary>
    public SurfaceAtFn? SurfaceAt { get; init; }
    /// <summary>Outcome model for rock and ice; gas targets and bodies under <see cref="Ls12MinRadiusKm"/> stay Legacy whatever this says.</summary>
    public OutcomeModel OutcomeModel { get; init; } = OutcomeModel.LS12;
    /// <summary>LS12 material constant c* (1.9 strengthless, 5 solid planetesimals). Ignored by Legacy.</summary>
    public double CStar { get; init; } = CollisionRegimes.CStarStrengthless;
}

/// <summary>
/// Collision resolution; mirrors src/sim/collide.ts. km, kg, s like the rest of Sim; SI only across the call into
/// <see cref="Impact.ImpactReport"/>, whose distances come back in metres.
/// </summary>
public static class Collide
{
    /// <summary>
    /// Smallest target LS12 decides, km. The LS12 laws are gravity-only ("far too weak" for bodies below ~1 km, see
    /// <see cref="CollisionRegimes"/>), and Legacy's Benz–Asphaug strength term is what keeps a boulder in one piece, so small
    /// targets keep it. 10 km is a game choice, a safe margin over that 1 km.
    /// </summary>
    public const double Ls12MinRadiusKm = 10;

    private static double ClampTau(double s, double lo, double hi) => Clamp(s, lo, hi);

    /// <summary>
    /// Energy that catastrophically disrupts a body, J.
    ///
    /// Self-gravity sets the scale above a few kilometres — a fifth of the gravitational binding energy,
    /// which is where the shatter threshold has always sat here. Below that, material strength dominates:
    /// the Benz &amp; Asphaug (1999) basalt strength term (Q*_D = 3.5e7 R_cm^-0.38 erg/g, converted to SI)
    /// stops a boulder from being blown apart by a gentle nudge, which self-gravity alone would allow.
    /// </summary>
    public static double DisruptionEnergy(Body b)
    {
        var rm = Math.Max(b.Radius * 1000, 1);
        var strength = 608 * Math.Pow(rm, -0.38); // J/kg
        return 0.2 * BindingEnergy(b) + strength * b.Mass;
    }

    private static void MergeInto(Body target, Body impactor)
    {
        var mTot = target.Mass + impactor.Mass;
        var rhoT = Density(target);
        var rhoI = Density(impactor);
        target.Vel = (target.Vel * target.Mass + impactor.Vel * impactor.Mass) * (1 / mTot);
        target.Pos = (target.Pos * target.Mass + impactor.Pos * impactor.Mass) * (1 / mTot);
        var volume = target.Mass / rhoT + impactor.Mass / rhoI; // m^3
        target.Mass = mTot;
        // ---- black hole: the horizon grows with the mass (r_s = 2GM/c^2), not with the swallowed volume ----
        if (target.Kind == BodyKind.BlackHole) target.Radius = BlackHole.SchwarzschildRadiusKm(mTot);
        // ---- end black hole ----
        else if (target.Kind != BodyKind.Star) target.Radius = Math.Cbrt((3 * volume) / (4 * Math.PI)) / 1000;
        target.Mass0 = target.Mass;
        target.Radius0 = target.Radius;
    }

    private static double Decayed(double value, double since, double tau, double now) => value * Math.Exp(-Math.Max(0, now - since) / tau);

    /// <summary>JS ToInt32, which the TS <c>^</c> applies to its operands: truncate, then wrap modulo 2^32.</summary>
    private static int ToInt32(double x)
    {
        if (!double.IsFinite(x)) return 0;
        var m = Math.Truncate(x) % 4294967296.0;
        if (m < 0) m += 4294967296.0;
        return unchecked((int)(uint)m);
    }

    /// <summary>Math.floor(time) the way a JS template literal prints it, for fragment ids.</summary>
    private static string FloorString(double time) => ((long)Math.Floor(time)).ToString(CultureInfo.InvariantCulture);

    /// <summary>Resolve one overlapping pair: crater, airburst, merge or fragmentation. Records an undo snapshot first.</summary>
    public static ImpactOutcome ResolveCollision(Universe u, CollisionEvent ev, CollideOptions opts)
    {
        // ---- black hole: always the one that absorbs, whatever the mass ratio (a hole inside a star eats the star) ----
        var holeA = ev.A.Kind == BodyKind.BlackHole;
        var targetIsA = holeA != (ev.B.Kind == BodyKind.BlackHole) ? holeA : ev.A.Mass >= ev.B.Mass;
        // ---- end black hole ----
        var (target, impactor) = targetIsA ? (ev.A, ev.B) : (ev.B, ev.A);
        u.PushHistory($"{impactor.Name} hitting {target.Name}", kind: UndoKind.Collision, subject: impactor.Id);
        var time = ev.Time;
        var rel = impactor.Vel - target.Vel;
        var relSpeed = Length(rel);
        // Contact geometry comes from the closest approach inside the substep, which for a fast small
        // impactor is nowhere near where the pair ended up at the end of it.
        var contactA = (targetIsA ? ev.PosA : ev.PosB) ?? target.Pos;
        var contactB = (targetIsA ? ev.PosB : ev.PosA) ?? impactor.Pos;
        var normalWorld = Normalize(contactB - contactA);
        var vin = relSpeed > 0 ? rel * (1 / relSpeed) : normalWorld * -1;
        var angle = Math.Asin(Clamp(Math.Abs(Dot(vin, normalWorld)), 0, 1));
        var contactWorld = target.Pos + normalWorld * target.Radius;
        var dirBody = opts.BodyFrame(target, normalWorld);
        var reduced = (target.Mass * impactor.Mass) / (target.Mass + impactor.Mass);
        var energy = 0.5 * reduced * Math.Pow(relSpeed * 1000, 2);
        var vBefore = target.Vel;
        var @base = new ImpactOutcome
        {
            Kind = OutcomeKind.Absorb, Target = target, Impactor = impactor, ContactWorld = contactWorld, NormalWorld = normalWorld, DirBody = dirBody,
            VelDirWorld = vin, RelSpeed = relSpeed, Angle = angle, Energy = energy, TargetDeltaV = 0, Fragments = [], Time = time,
        };
        var dmg = target.Damage;
        dmg.Hits++;
        dmg.Energy += energy;

        if (target.Kind == BodyKind.BlackHole && impactor.Kind == BodyKind.BlackHole)
        {
            MergeHoles(target, impactor, @base);
            u.Remove(impactor);
            @base.TargetDeltaV = Length(target.Vel - vBefore);
            return @base;
        }

        if (target.Kind == BodyKind.BlackHole)
        {
            MergeInto(target, impactor);
            u.Remove(impactor);
            @base.TargetDeltaV = Length(target.Vel - vBefore);
            return @base;
        }

        if (target.Kind == BodyKind.Star)
        {
            var dn = Dot(vin, normalWorld);
            return ResolveStellar(u, @base, Math.Sqrt(Math.Max(0, 1 - dn * dn)), contactB - contactA, vBefore);
        }

        // ---- LS12. Gas targets and small bodies skip it; ls12Merge then stays false and the Legacy branches below run as before. ----
        var ls12Merge = false;
        if (opts.OutcomeModel == OutcomeModel.LS12 && target.Surface != Surface.Gas && target.Radius >= Ls12MinRadiusKm)
        {
            var dotN = Dot(vin, normalWorld);
            var b = Math.Sqrt(Math.Max(0, 1 - dotN * dotN));
            var ls = CollisionRegimes.Evaluate(target.Mass, impactor.Mass, target.Radius, impactor.Radius, relSpeed, b, opts.CStar);
            @base.Ls12 = ls;
            switch (ls.Regime)
            {
                case CollisionRegime.HitAndRun:
                    return Ls12HitAndRun(u, @base, ls, contactB - contactA);
                case CollisionRegime.PerfectMerge or CollisionRegime.GrazeAndMerge:
                case CollisionRegime.PartialAccretion when target.Mass + impactor.Mass - ls.LargestRemnantMass < Math.Max(Ls12DebrisFloor * impactor.Mass, 4e-16 * (target.Mass + impactor.Mass)):
                    ls12Merge = true; // crater/merge path below, with the Legacy disruption branches skipped
                    break;
                default:
                    var disrupted = Ls12Disrupt(u, @base, ls);
                    disrupted.TargetDeltaV = Length(target.Vel - vBefore);
                    return disrupted;
            }
        }
        // ---- end LS12 ----

        // 1 = catastrophic disruption; the old crater/shatter boundary was a fifth of the binding energy,
        // so a severity of 1 here is the same event for a planet-sized target.
        var severity = energy / DisruptionEnergy(target);
        if (!ls12Merge && severity >= 1 && u.RoomForBodies >= 2)
        {
            var shattered = Fragment(u, @base, severity);
            shattered.TargetDeltaV = Length(target.Vel - vBefore);
            return shattered;
        }
        if (!ls12Merge && severity >= 1)
        {
            // Out of room for new bodies: the target still melts, it just does not scatter.
            @base.Kind = OutcomeKind.Merge;
            MergeInto(target, impactor);
            target.Surface = Surface.Lava;
            dmg.Craters = [];
            dmg.Heat = [];
            dmg.Magma = 1;
            dmg.MagmaTime = time;
            dmg.MagmaTau = ClampTau(30 * Year * Math.Sqrt(0.2 * severity), Year, 100 * Year);
            dmg.Version++;
            u.Remove(impactor);
            @base.TargetDeltaV = Length(target.Vel - vBefore);
            return @base;
        }

        var surf = opts.SurfaceAt?.Invoke(target, dirBody);
        var surface = target.Surface == Surface.Gas ? TargetSurface.Gas
            : surf?.Water == true ? TargetSurface.Water
            : target.Surface == Surface.Ice ? TargetSurface.Ice
            : TargetSurface.Rock;
        var tgt = new TargetSpec
        {
            Radius = target.Radius * 1000,
            Mass = target.Mass,
            Gravity = SurfaceGravity(target),
            Surface = surface,
            SurfaceDensity = surface == TargetSurface.Water ? 1000 : surface == TargetSurface.Ice ? 900 : 2500,
            WaterDepth = surf?.Depth,
            Atmosphere = target.Atmosphere,
        };
        var report = Impact.ImpactReport(new ImpactorSpec { Diameter = impactor.Radius * 2000, Density = Density(impactor), Speed = relSpeed * 1000, Angle = angle }, tgt);
        @base.Report = report;

        var now = time;
        var R = target.Radius * 1000;
        if (target.Surface == Surface.Gas)
        {
            @base.Kind = OutcomeKind.Gas;
            var scarRadius = Math.Max(report.FireballRadius * 6, R * 0.01);
            dmg.Heat.Add(new HeatSpot(dirBody, scarRadius / R, now, ClampTau(30 * Day * Math.Cbrt(energy / 1e23), 2 * Day, 3 * Year)));
            dmg.Craters.Add(new Crater(dirBody, (scarRadius * 2) / 1000, now, energy));
            dmg.Version++;
        }
        else if (report.Entry.Airburst)
        {
            @base.Kind = OutcomeKind.Airburst;
            dmg.Heat.Add(new HeatSpot(dirBody, Math.Max(report.FireballRadius, 2000) / R, now, 20 * 60));
        }
        else if (report.Crater is { } crater)
        {
            @base.Kind = impactor.Mass > 0.02 * target.Mass ? OutcomeKind.Merge : OutcomeKind.Crater;
            var D = crater.FinalDiameter;
            dmg.Craters.Add(new Crater(dirBody, D / 1000, now, energy));
            dmg.Version++;
            var Dkm = D / 1000;
            dmg.Heat.Add(new HeatSpot(dirBody, Math.Max((D / 2) * 1.6, 1500) / R, now, ClampTau(Math.Pow(Dkm, 1.5) * Hour, 10 * 60, 50 * Year)));
            if (target.Atmosphere is not null && energy > 1e18)
            {
                var dust = Clamp(0.25 * Math.Log10(energy / 1e18), 0, 1);
                var prev = Decayed(dmg.Dust, dmg.DustTime, dmg.DustTau, now);
                if (dust > prev)
                {
                    dmg.Dust = dust;
                    dmg.DustTime = now;
                    dmg.DustTau = ClampTau(Year * Math.Pow(energy / 1e21, 0.3), 30 * Day, 30 * Year);
                }
            }
        }
        if (severity > 0.015)
        {
            var magma = Clamp(Math.Log10(severity / 0.015) / Math.Log10(1 / 0.015), 0, 1);
            var prev = Decayed(dmg.Magma, dmg.MagmaTime, dmg.MagmaTau, now);
            if (magma > prev)
            {
                dmg.Magma = magma;
                dmg.MagmaTime = now;
                dmg.MagmaTau = ClampTau(30 * Year * Math.Sqrt(severity), Year, 30 * Year);
            }
        }
        if (dmg.Heat.Count > 12) dmg.Heat.RemoveRange(0, dmg.Heat.Count - 12);
        MergeInto(target, impactor);
        u.Remove(impactor);
        @base.TargetDeltaV = Length(target.Vel - vBefore);
        return @base;
    }

    /// <summary>
    /// Two black holes merge (the target is the heavier): the remnant sits at their centre of mass with M_rem from the
    /// Healy–Lousto–Zlochower fit, and moves at the centre-of-mass velocity plus the González recoil
    /// (<see cref="GravitationalWaves"/>). [GAME] The recoil points in the orbital plane against the lighter hole's orbital
    /// motion at contact; the real direction depends on the phase at merger. A head-on pair recoils along its axis. The
    /// horizon grows to r_s of the lighter remnant, and it takes the HLZ14 spin (which nothing else reads yet). What the waves
    /// take is recorded in <see cref="ImpactOutcome.Merger"/>.
    /// </summary>
    private static void MergeHoles(Body target, Body impactor, ImpactOutcome @out)
    {
        double m1 = target.Mass, m2 = impactor.Mass, m = m1 + m2;
        var (r1, v1, r2, v2) = (target.Pos, target.Vel, impactor.Pos, impactor.Vel);
        var mf = GravitationalWaves.RemnantMassFraction(m1, m2) * m;
        var vCom = (v1 * m1 + v2 * m2) * (1 / m);
        var rCom = (r1 * m1 + r2 * m2) * (1 / m);
        var sep = r2 - r1;
        var rel = v2 - v1;
        var axis = Length(sep) > 0 ? Normalize(sep) : Length(rel) > 0 ? Normalize(rel) : new Vec3(1, 0, 0);
        var tangential = rel - axis * Dot(rel, axis);
        var dir = Length(tangential) > 1e-9 * Math.Max(Length(rel), 1e-300) ? Normalize(tangential) * -1 : axis * -1;
        var kick = dir * GravitationalWaves.RecoilSpeed(GravitationalWaves.SymmetricMassRatio(m1, m2));
        target.Mass = mf;
        target.Pos = rCom;
        target.Vel = vCom + kick;
        target.Radius = BlackHole.SchwarzschildRadiusKm(mf);
        target.Spin = GravitationalWaves.RemnantSpin(m1, m2);
        target.Mass0 = target.Mass;
        target.Radius0 = target.Radius;
        @out.Kind = OutcomeKind.Merge;
        @out.Merger = new MergerRecord(m1, r1, v1, m2, r2, v2, mf, rCom, target.Vel, kick);
    }

    private static double Spread(double severity) => 0.6 + 0.6 * Math.Sqrt(Math.Min(0.2 * severity, 3));

    /// <summary>
    /// Place fragments of <paramref name="masses"/> (kg) on a shell around <paramref name="center"/> (km), clear of every
    /// entry in <paramref name="placed"/> and of each other, flying out from <paramref name="vBase"/> (km/s). Returns them
    /// with the momentum they carry relative to vBase, which the caller's remnant must take back.
    /// </summary>
    private static List<Body> PlaceFragments(Body source, double time, IReadOnlyList<double> masses, double rho, Vec3 center, double shell,
        Vec3 vBase, double vEsc, double spread, Vec3 impactDir, Func<double> rand, List<(Vec3 Pos, double R)> placed, out Vec3 momentum)
    {
        var fragments = new List<Body>(masses.Count);
        momentum = Vec3.Zero;
        for (var k = 0; k < masses.Count; k++)
        {
            var mass = masses[k];
            var radius = RadiusForDensity(mass, rho);
            var dir = RandomUnit(rand);
            for (var tries = 0; tries < 24; tries++)
            {
                var candidate = center + dir * (shell + radius);
                if (placed.TrueForAll(p => Length(p.Pos - candidate) > 1.2 * (p.R + radius))) break;
                dir = RandomUnit(rand);
            }
            var pos = center + dir * (shell + radius);
            var radial = dir * (vEsc * (0.4 + 0.9 * rand()) * spread);
            var tangential = Normalize(Cross(dir, impactDir)) * (vEsc * 0.15 * rand());
            var dv = radial + tangential;
            var vel = vBase + dv;
            momentum += dv * mass;
            placed.Add((pos, radius));
            var body = MakeBody(new BodySpec
            {
                Id = $"{source.Id}-frag-{FloorString(time)}-{k}",
                Name = $"{source.Name} fragment {k + 1}",
                Kind = BodyKind.Fragment,
                Surface = Surface.Lava,
                Mass = mass,
                Radius = radius,
                Pos = pos,
                Vel = vel,
                PrimaryId = source.PrimaryId,
                Visual = new BodyVisual { Color = source.Visual.Color, Albedo = 0.1 },
                Seed = (int)Math.Floor(rand() * 1e9),
                Born = time,
            });
            body.Damage.Magma = 1;
            body.Damage.MagmaTime = time;
            body.Damage.MagmaTau = Clamp(Day * Math.Pow(radius / 10, 1.5), Hour, 30 * Year);
            fragments.Add(body);
        }
        return fragments;
    }

    /// <summary>Turn the target into the molten largest remnant: mass kg at density rho kg/m^3, pos km, vel km/s.</summary>
    private static void BecomeRemnant(Body target, double mass, double rho, Vec3 pos, Vec3 vel, double time, double severity)
    {
        target.Mass = mass;
        target.Radius = RadiusForDensity(mass, rho);
        target.Mass0 = target.Mass;
        target.Radius0 = target.Radius;
        target.Pos = pos;
        target.Vel = vel;
        target.Surface = Surface.Lava;
        target.Damage.Craters = [];
        target.Damage.Heat = [];
        target.Damage.Magma = 1;
        target.Damage.MagmaTime = time;
        target.Damage.MagmaTau = Clamp(30 * Year * Math.Sqrt(0.2 * severity), Year, 100 * Year);
        target.Damage.Version++;
    }

    // ---- stars ----

    /// <summary>
    /// Polytrope n = 3 binding energy over the uniform sphere's, 3/(5−n) over 3/5 = 2.5: the Sun is far more centrally condensed
    /// than a rock, so it is more bound than <see cref="Body.BindingEnergy"/> says (≈ 5.7e41 J against 2.3e41 J).
    /// </summary>
    private const double StellarBindingFactor = 2.5;

    /// <summary>Most of the pair's mass a collision may blow off, so something always remains (game choice: no total dispersal, never a supernova).</summary>
    public const double StellarMassLossCap = 0.9;

    private static double Binding(Body b) => BindingEnergy(b) * (b.Kind == BodyKind.Star ? StellarBindingFactor : 1);

    /// <summary>
    /// Fraction of the pair's mass a stellar merger sheds for a given <see cref="ImpactOutcome.StellarShock"/> x: 1 − e^(−x²),
    /// capped at <see cref="StellarMassLossCap"/>. Game tuning, anchored on two things: the SPH studies of main-sequence collisions
    /// (Lombardi, Rasio &amp; Shapiro 1996) find a few percent lost when two stars meet on a parabola (x ≈ 0.17 gives 2.9 %), and Freitag
    /// &amp; Benz (2005) find mass loss and disruption growing with speed, which here means x → 1 and up (63 % at x = 1).
    /// </summary>
    public static double StellarMassLossFraction(double shock) => Math.Min(StellarMassLossCap, 1 - Math.Exp(-shock * shock));

    /// <summary>
    /// A star on either side of the pair (the target is the star). Grazing and faster than the Kokubo–Genda hit-and-run speed:
    /// both survive and the projectile flies on. Otherwise the stars merge, centre of mass and momentum kept, and the remnant
    /// sheds <see cref="StellarMassLossFraction"/> of the total, reported as <see cref="ImpactOutcome.ParticleTierMass"/> leaving
    /// at the centre-of-mass velocity (so it takes no momentum with it). A body under 0.1 % of the star is simply swallowed.
    /// The remnant keeps the target's radius: nothing here models the merged star relaxing.
    /// </summary>
    private static ImpactOutcome ResolveStellar(Universe u, ImpactOutcome @out, double b, Vec3 contactRel, Vec3 vBefore)
    {
        var (target, impactor) = (@out.Target, @out.Impactor);
        var ls = CollisionRegimes.Evaluate(target.Mass, impactor.Mass, target.Radius, impactor.Radius, @out.RelSpeed, b);
        var mTot = target.Mass + impactor.Mass;
        var reduced = target.Mass * impactor.Mass / mTot;
        // Only the overlapping part of the projectile is shocked; for a head-on hit alpha is 1.
        var shock = 0.5 * reduced * Math.Pow(@out.RelSpeed * 1000, 2) * ls.Alpha / (Binding(target) + Binding(impactor));
        @out.StellarShock = shock;
        var grazingFast = b > ls.BCrit && @out.RelSpeed >= ls.VHr;
        if (grazingFast)
        {
            @out.Ls12 = ls with { Regime = CollisionRegime.HitAndRun, ProjectileRemnantMass = impactor.Mass };
            return Ls12HitAndRun(u, @out, @out.Ls12.Value, contactRel);
        }
        var lost = impactor.Mass < 0.001 * target.Mass ? 0 : StellarMassLossFraction(shock) * mTot;
        MergeInto(target, impactor);
        target.Mass -= lost;
        target.Mass0 = target.Mass;
        u.Remove(impactor);
        @out.Kind = lost > 0 ? OutcomeKind.StellarMerge : OutcomeKind.Absorb;
        @out.ParticleTierMass = lost;
        @out.ParticleTierVelocity = target.Vel;
        @out.TargetDeltaV = Length(target.Vel - vBefore);
        return @out;
    }

    // ---- end stars ----

    // ---- LS12 outcome paths (reached only with OutcomeModel.LS12) ----

    /// <summary>Most discrete fragments one LS12 event spawns; the rest of the escaping mass goes to the particle tier.</summary>
    public const int Ls12MaxFragments = 64;

    /// <summary>
    /// Escaping mass below this fraction of the projectile's mass is under the model's resolution and stays with its body:
    /// a partial accretion that sheds less becomes a plain merge (crater path), a hit-and-run projectile keeps its mass.
    /// </summary>
    public const double Ls12DebrisFloor = 0.01;

    /// <summary>
    /// LS12 disruption (partial accretion, partial erosion, super-catastrophic, grazing erosion): the target becomes the
    /// largest remnant M_lr, the escaping mass is sampled into discrete fragments plus a particle tier, and the remnant
    /// takes the fragments' recoil so momentum is conserved exactly.
    /// </summary>
    private static ImpactOutcome Ls12Disrupt(Universe u, ImpactOutcome @out, CollisionRegimeResult ls)
    {
        var (target, impactor, time) = (@out.Target, @out.Impactor, @out.Time);
        var mTot = target.Mass + impactor.Mass;
        var rho = Density(target);
        var remnantMass = Math.Min(ls.LargestRemnantMass, mTot);
        var vCom = (target.Vel * target.Mass + impactor.Vel * impactor.Mass) * (1 / mTot);
        var pCom = (target.Pos * target.Mass + impactor.Pos * impactor.Mass) * (1 / mTot);
        var vEsc = EscapeVelocity(target);
        var rand = Rng.Mulberry32(unchecked((uint)(target.Seed ^ ToInt32(Math.Floor(time)))));
        var severity = ls.QR / ls.QPrimeStarRD;
        var sample = CollisionRegimes.SampleFragments(mTot - remnantMass, rand, Math.Min(Ls12MaxFragments, u.RoomForBodies), largestCap: remnantMass);
        var fragments = PlaceFragments(target, time, sample.Masses, rho, pCom, target.Radius * 1.6, vCom, vEsc, Spread(severity),
            Normalize(impactor.Vel - target.Vel), rand, new List<(Vec3, double)>(sample.Masses.Length), out var momentum);
        BecomeRemnant(target, remnantMass, rho, pCom, vCom - momentum * (1 / remnantMass), time, severity);
        u.Remove(impactor);
        foreach (var f in fragments) u.Add(f);
        @out.Kind = remnantMass / mTot >= 0.3 ? OutcomeKind.Shatter : OutcomeKind.Disperse;
        @out.Fragments = fragments;
        @out.ParticleTierMass = sample.ParticleTierMass;
        @out.ParticleTierVelocity = vCom;
        return @out;
    }

    /// <summary>
    /// LS12 hit-and-run: the target is untouched, the projectile keeps its velocity, loses the reverse-impact debris
    /// (fragments + particle tier, flying with it), and is moved forward along its relative path to where it has cleared
    /// the target, so the next substep does not see the pair still overlapping. <paramref name="contactRel"/> is the
    /// projectile's position relative to the target at closest approach, km.
    /// </summary>
    private static ImpactOutcome Ls12HitAndRun(Universe u, ImpactOutcome @out, CollisionRegimeResult ls, Vec3 contactRel)
    {
        var (target, impactor, time) = (@out.Target, @out.Impactor, @out.Time);
        var mp = impactor.Mass;
        var keep = mp - ls.ProjectileRemnantMass >= Ls12DebrisFloor * mp ? ls.ProjectileRemnantMass : mp;
        var rho = Density(impactor);
        var vEsc = EscapeVelocity(impactor);
        var radius = keep < mp ? RadiusForDensity(keep, rho) : impactor.Radius;
        // Far root of |contactRel + s·dir| = clear: where the straight relative path leaves the target's reach.
        var dir = @out.VelDirWorld;
        var clear = 1.05 * (target.Radius + radius);
        var cv = Dot(contactRel, dir);
        var s = -cv + Math.Sqrt(Math.Max(0, cv * cv - Dot(contactRel, contactRel) + clear * clear));
        var exit = target.Pos + contactRel + dir * s;
        var vp = impactor.Vel;
        List<Body> fragments = [];
        double particle = 0;
        if (keep < mp)
        {
            var rand = Rng.Mulberry32(unchecked((uint)(impactor.Seed ^ ToInt32(Math.Floor(time)))));
            var sample = CollisionRegimes.SampleFragments(mp - keep, rand, Math.Min(Ls12MaxFragments, u.RoomForBodies), largestCap: keep);
            var severity = ls.ProjectileQRatio;
            List<(Vec3, double)> placed = [(target.Pos, target.Radius), (exit, radius)];
            fragments = PlaceFragments(impactor, time, sample.Masses, rho, exit, radius * 1.6, vp, vEsc, Spread(severity), dir, rand, placed, out var momentum);
            particle = sample.ParticleTierMass;
            impactor.Mass = keep;
            impactor.Radius = radius;
            impactor.Mass0 = keep;
            impactor.Radius0 = radius;
            impactor.Vel = vp - momentum * (1 / keep);
            impactor.Surface = Surface.Lava;
            impactor.Damage.Magma = 1;
            impactor.Damage.MagmaTime = time;
            impactor.Damage.MagmaTau = Clamp(30 * Year * Math.Sqrt(0.2 * severity), Year, 100 * Year);
            impactor.Damage.Version++;
        }
        impactor.Pos = exit;
        foreach (var f in fragments) u.Add(f);
        @out.Kind = OutcomeKind.HitAndRun;
        @out.Fragments = fragments;
        @out.ParticleTierMass = particle;
        @out.ParticleTierVelocity = vp;
        return @out;
    }

    // ---- end LS12 ----

    /// <summary>Break the target apart. <paramref name="severity"/> is the impact energy over the disruption energy (1 = catastrophic).</summary>
    private static ImpactOutcome Fragment(Universe u, ImpactOutcome @out, double severity)
    {
        var (target, impactor, time) = (@out.Target, @out.Impactor, @out.Time);
        var mTot = target.Mass + impactor.Mass;
        var rho = Density(target);
        var fLR = Clamp(1 - 0.1 * severity, 0.05, 0.9);
        var remnantMass = fLR * mTot;
        var fragMass = mTot - remnantMass;
        // Math.round breaks ties upward; the argument is at least 6 here, so away-from-zero is the same rule.
        var n = (int)Math.Min(Clamp(Math.Round(6 + 6 * Math.Log10(1 + 2 * severity), MidpointRounding.AwayFromZero), 6, 18), u.RoomForBodies);
        var vCom = (target.Vel * target.Mass + impactor.Vel * impactor.Mass) * (1 / mTot);
        var pCom = (target.Pos * target.Mass + impactor.Pos * impactor.Mass) * (1 / mTot);
        var vEsc = EscapeVelocity(target);
        var rand = Rng.Mulberry32(unchecked((uint)(target.Seed ^ ToInt32(Math.Floor(time)))));
        var impactDir = Normalize(impactor.Vel - target.Vel);
        var weights = new List<double>(n);
        double wSum = 0;
        for (var k = 0; k < n; k++)
        {
            var w = Math.Pow(k + 1, -1.2);
            weights.Add(w);
            wSum += w;
        }
        var masses = new double[n];
        for (var k = 0; k < n; k++) masses[k] = (fragMass * weights[k]) / wSum;
        var fragments = PlaceFragments(target, time, masses, rho, pCom, target.Radius * 1.6, vCom, vEsc, Spread(severity), impactDir, rand,
            new List<(Vec3, double)>(n), out var momentum);
        // The remnant carries the recoil so total momentum is conserved exactly.
        BecomeRemnant(target, remnantMass, rho, pCom, vCom - momentum * (1 / remnantMass), time, severity);
        u.Remove(impactor);
        foreach (var f in fragments) u.Add(f);
        @out.Kind = fLR >= 0.3 ? OutcomeKind.Shatter : OutcomeKind.Disperse;
        @out.Fragments = fragments;
        return @out;
    }
}
