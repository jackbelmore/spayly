namespace Spayly.Sim.Sph;

/// <summary>
/// The conserved totals of a set of masses about one origin: Σm, the first mass moment Σm r, the momentum Σm v and the
/// angular momentum Σ(r × m v + L_spin). Units are the caller's (SI inside the SPH module, km-kg-s in the world frame).
/// The first moment is kept so that a ledger can be moved to another origin and frame exactly, without knowing where
/// its members are.
/// </summary>
public readonly record struct Ledger(double Mass, Vec3 FirstMoment, Vec3 Momentum, Vec3 AngularMomentum)
{
    public static readonly Ledger Zero = new(0, Vec3.Zero, Vec3.Zero, Vec3.Zero);

    /// <summary>A point mass <paramref name="m"/> at <paramref name="r"/> moving at <paramref name="v"/>, carrying <paramref name="spin"/> about itself.</summary>
    public static Ledger Point(double m, Vec3 r, Vec3 v, Vec3 spin) => new(m, r * m, v * m, Vec3.Cross(r, v * m) + spin);

    public static Ledger operator +(Ledger a, Ledger b) =>
        new(a.Mass + b.Mass, a.FirstMoment + b.FirstMoment, a.Momentum + b.Momentum, a.AngularMomentum + b.AngularMomentum);

    public static Ledger operator -(Ledger a, Ledger b) =>
        new(a.Mass - b.Mass, a.FirstMoment - b.FirstMoment, a.Momentum - b.Momentum, a.AngularMomentum - b.AngularMomentum);

    /// <summary>
    /// The same masses seen from a frame whose origin is at −<paramref name="dr"/> and which moves at −<paramref name="dv"/>
    /// relative to this one, i.e. every r → r + dr and v → v + dv:
    /// D' = D + M dr, P' = P + M dv, L' = L + dr × P + D × dv + M dr × dv.
    /// </summary>
    public Ledger Shift(Vec3 dr, Vec3 dv) => new(
        Mass,
        FirstMoment + dr * Mass,
        Momentum + dv * Mass,
        AngularMomentum + Vec3.Cross(dr, Momentum) + Vec3.Cross(FirstMoment, dv) + Vec3.Cross(dr, dv) * Mass);

    /// <summary>Change units: lengths × <paramref name="length"/>, velocities × <paramref name="velocity"/> (e.g. 1e-3, 1e-3 for m → km).</summary>
    public Ledger Scale(double length, double velocity) =>
        new(Mass, FirstMoment * length, Momentum * velocity, AngularMomentum * (length * velocity));

    /// <summary>Centre of mass (FirstMoment / Mass), or zero for an empty ledger.</summary>
    public Vec3 CentreOfMass => Mass != 0 ? FirstMoment * (1 / Mass) : Vec3.Zero;

    /// <summary>Velocity of the centre of mass, or zero for an empty ledger.</summary>
    public Vec3 CentreOfMassVelocity => Mass != 0 ? Momentum * (1 / Mass) : Vec3.Zero;
}
