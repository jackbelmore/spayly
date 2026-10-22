using static Spayly.Sim.Constants;

namespace Spayly.Sim;

/// <summary>Collision outcome regimes of Leinhardt &amp; Stewart (2012), in the order a rising impact speed passes through them.</summary>
public enum CollisionRegime
{
    /// <summary>V_i below the escape speed of the interacting mass: everything merges.</summary>
    PerfectMerge,
    /// <summary>Grazing, but slower than the Kokubo–Genda hit-and-run speed: the bodies separate and re-collide.</summary>
    GrazeAndMerge,
    /// <summary>Grazing and fast: the target survives intact, the projectile leaves (possibly eroded).</summary>
    HitAndRun,
    /// <summary>Grazing and energetic enough that the target loses mass.</summary>
    Erosion,
    /// <summary>Non-grazing, M_lr &gt; M_t: the target gains mass and some debris escapes.</summary>
    PartialAccretion,
    /// <summary>Non-grazing, 0.1 M_tot &lt; M_lr ≤ M_t.</summary>
    PartialErosion,
    /// <summary>Non-grazing, M_lr ≤ 0.1 M_tot: the power-law tail of the universal law.</summary>
    SuperCatastrophic,
}

/// <summary>
/// Everything <see cref="CollisionRegimes.Evaluate"/> derives for one impact. Masses kg, radii km, speeds km/s,
/// specific energies J/kg (SI, like every energy in Sim).
/// </summary>
public readonly record struct CollisionRegimeResult
{
    public required CollisionRegime Regime { get; init; }
    /// <summary>Target (larger) mass, kg.</summary>
    public required double TargetMass { get; init; }
    /// <summary>Projectile (smaller) mass, kg.</summary>
    public required double ProjectileMass { get; init; }
    /// <summary>M_p / M_t, ≤ 1.</summary>
    public required double Gamma { get; init; }
    /// <summary>Reduced mass μ = M_p M_t / M_tot, kg.</summary>
    public required double Mu { get; init; }
    /// <summary>b = sin θ, θ measured from the line of centres (0 = head-on).</summary>
    public required double B { get; init; }
    /// <summary>Impact speed V_i, km/s.</summary>
    public required double ImpactSpeed { get; init; }
    /// <summary>Q_R = μV_i²/(2M_tot), J/kg (LS12).</summary>
    public required double QR { get; init; }
    /// <summary>R_C1: radius of M_tot at ρ₁ = 1000 kg/m³, km.</summary>
    public required double RC1 { get; init; }
    /// <summary>Q*_RD,γ=1 = c*(4/5)πρ₁G R_C1², J/kg.</summary>
    public required double QStarRDGamma1 { get; init; }
    /// <summary>V*_γ=1 = (32πc*/5)^½ (ρ₁G)^½ R_C1, km/s.</summary>
    public required double VStarGamma1 { get; init; }
    /// <summary>Q*_RD after the mass-ratio correction, J/kg.</summary>
    public required double QStarRD { get; init; }
    /// <summary>α: fraction of the projectile's mass inside the target's cross-section, 0..1.</summary>
    public required double Alpha { get; init; }
    /// <summary>μ_α = αM_pM_t/(αM_p+M_t), kg.</summary>
    public required double MuAlpha { get; init; }
    /// <summary>Q'*_RD, the oblique-impact disruption threshold, J/kg.</summary>
    public required double QPrimeStarRD { get; init; }
    /// <summary>V'* = √(2Q'*_RD M_tot/μ), the impact speed at which Q_R = Q'*_RD, km/s.</summary>
    public required double VPrimeStar { get; init; }
    /// <summary>Mutual escape speed of the full masses at contact, √(2GM_tot/(R_p+R_t)), km/s. Kokubo–Genda's V_esc.</summary>
    public required double VEsc { get; init; }
    /// <summary>V_esc' = √(2G(αM_p+M_t)/(R_p+R_t)), km/s: the perfect-merge threshold.</summary>
    public required double VEscPrime { get; init; }
    /// <summary>b_crit = R_t/(R_t+R_p): above it the projectile's centre misses the target.</summary>
    public required double BCrit { get; init; }
    /// <summary>Kokubo &amp; Genda (2010) critical (hit-and-run) speed over V_esc.</summary>
    public required double VHrOverVEsc { get; init; }
    /// <summary>Hit-and-run speed, km/s.</summary>
    public required double VHr { get; init; }
    /// <summary>M_lr/M_tot from the universal law at Q_R/Q'*_RD, 0..1. For a hit-and-run it is the law's value, not the target's fate.</summary>
    public required double LargestRemnantFraction { get; init; }
    /// <summary>M_lr = LargestRemnantFraction × M_tot, kg.</summary>
    public required double LargestRemnantMass { get; init; }
    /// <summary>Largest remnant of the projectile after a hit-and-run (reverse impact), kg; M_p unless Regime is HitAndRun.</summary>
    public required double ProjectileRemnantMass { get; init; }
    /// <summary>Q_R/Q'*_RD of the reverse impact that erodes a hit-and-run projectile; 0 unless Regime is HitAndRun.</summary>
    public required double ProjectileQRatio { get; init; }
}

/// <summary>Output of <see cref="CollisionRegimes.SampleFragments"/>. Σ Masses + ParticleTierMass equals the escaping mass.</summary>
/// <param name="Masses">Discrete fragment masses, kg, largest first.</param>
/// <param name="ParticleTierMass">Escaping mass left for a debris/particle tier, kg.</param>
public readonly record struct FragmentSample(double[] Masses, double ParticleTierMass);

/// <summary>
/// Leinhardt &amp; Stewart (2012, ApJ 745:79; "LS12") collision-outcome scaling laws for gravity-dominated bodies, with the
/// Kokubo &amp; Genda (2010, ApJL 714:L21; "KG10") hit-and-run boundary. Written from the published equations. Pure: no
/// Universe, no Body. Library units (kg, km, km/s, G = <see cref="Constants.G"/> in km³ kg⁻¹ s⁻²) throughout; the only
/// SI in the maths is ρ₁, converted once to kg/km³, and the specific energies, reported in J/kg (km²/s² × 10⁶) to match
/// the literature and the rest of Sim's energies.
///
/// Gravity regime only: there is no material-strength term, so for bodies below ~1 km the model is far too weak.
/// </summary>
public static class CollisionRegimes
{
    /// <summary>c* for fluid or strengthless planets (LS12: 1.9 ± 0.3).</summary>
    public const double CStarStrengthless = 1.9;
    /// <summary>c* for solid planetesimals (LS12: 5 ± 2).</summary>
    public const double CStarSolid = 5;
    /// <summary>μ̄, the velocity exponent of the point-source coupling parameter (LS12: 0.36 ± 0.01).</summary>
    public const double MuBarDefault = 0.36;
    /// <summary>ρ₁ = 1000 kg/m³, the reference density that defines R_C1.</summary>
    public const double Rho1 = 1000;
    /// <summary>ρ₁ in kg/km³ (the one SI→km conversion in the model).</summary>
    private const double Rho1Km = Rho1 * 1e9;
    /// <summary>km²/s² → J/kg.</summary>
    private const double KmToJ = 1e6;
    /// <summary>Q_R/Q'*_RD where the universal law turns from linear to the super-catastrophic power law (M_lr = 0.1 M_tot).</summary>
    public const double SuperCatastrophicOnset = 1.8;
    /// <summary>η, the super-catastrophic power-law exponent (LS12: ≈ −1.5).</summary>
    public const double EtaDefault = -1.5;
    /// <summary>β of the fragment cumulative size distribution N(&gt;D) ∝ D^−β (LS12 fit: 2.85).</summary>
    public const double BetaDefault = 2.85;

    // Kokubo & Genda 2010: v_cr/v_esc = c1 ζ² Θ^c5 + c2 ζ² + c3 Θ^c5 + c4, Θ = 1 − sin θ, ζ = (M_t − M_p)/M_tot.
    private const double Kg1 = 2.43, Kg2 = -0.0408, Kg3 = 1.86, Kg4 = 1.08, Kg5 = 2.5;

    /// <summary>Q_R = μV²/(2M_tot) (LS12), J/kg. Masses kg, v km/s.</summary>
    public static double SpecificImpactEnergy(double mTarget, double mProjectile, double v)
    {
        var mTot = mTarget + mProjectile;
        var mu = mTarget * mProjectile / mTot;
        return mu * v * v / (2 * mTot) * KmToJ;
    }

    /// <summary>R_C1: radius (km) of a sphere of mass <paramref name="mTot"/> (kg) at ρ₁ = 1000 kg/m³.</summary>
    public static double CombinedRadiusAtRho1(double mTot) => Math.Cbrt(3 * mTot / (4 * Math.PI * Rho1Km));

    /// <summary>Equal-mass principal disruption curve Q*_RD,γ=1 = c*(4/5)πρ₁G R_C1² (LS12), J/kg. R_C1 in km.</summary>
    public static double QStarRDEqualMass(double rc1, double cStar = CStarStrengthless) => cStar * 0.8 * Math.PI * Rho1Km * G * rc1 * rc1 * KmToJ;

    /// <summary>Equal-mass critical speed V*_γ=1 = (32πc*/5)^½ (ρ₁G)^½ R_C1 (LS12), km/s. R_C1 in km.</summary>
    public static double VStarEqualMass(double rc1, double cStar = CStarStrengthless) => Math.Sqrt(32 * Math.PI * cStar / 5 * Rho1Km * G) * rc1;

    /// <summary>
    /// Mass-ratio correction factor [¼(γ+1)²/γ]^(2/(3μ̄) − 1) that turns Q*_RD,γ=1 into Q*_RD (LS12). Symmetric in γ ↔ 1/γ;
    /// 1 at γ = 1.
    /// </summary>
    public static double MassRatioCorrection(double gamma, double muBar = MuBarDefault) =>
        Math.Pow(0.25 * (gamma + 1) * (gamma + 1) / gamma, 2 / (3 * muBar) - 1);

    /// <summary>
    /// α, the fraction of the projectile's volume that overlaps the target's cross-section (LS12): 1 when
    /// R_t &gt; b(R_p+R_t) + R_p, else the spherical cap of depth l = (R_p+R_t)(1−b), (3R_p l² − l³)/(4R_p³). Radii km.
    /// </summary>
    public static double InteractingFraction(double rTarget, double rProjectile, double b)
    {
        if (rTarget > b * (rProjectile + rTarget) + rProjectile) return 1;
        var l = (rProjectile + rTarget) * (1 - b);
        return Math.Clamp((3 * rProjectile * l * l - l * l * l) / (4 * rProjectile * rProjectile * rProjectile), 0, 1);
    }

    /// <summary>Q'*_RD = (μ/μ_α)^(2 − 3μ̄/2) Q*_RD (LS12 oblique correction), in Q*_RD's units.</summary>
    public static double ObliqueCorrection(double qStarRD, double mu, double muAlpha, double muBar = MuBarDefault) =>
        Math.Pow(mu / muAlpha, 2 - 1.5 * muBar) * qStarRD;

    /// <summary>b_crit = R_t/(R_t+R_p) (LS12 grazing threshold). Radii km.</summary>
    public static double GrazingThreshold(double rTarget, double rProjectile) => rTarget / (rTarget + rProjectile);

    /// <summary>
    /// Kokubo &amp; Genda (2010) merging criterion as V_cr/V_esc = 2.43ζ²(1−b)^{5/2} − 0.0408ζ² + 1.86(1−b)^{5/2} + 1.08, with
    /// ζ = (M_t−M_p)/M_tot. Above it a grazing impact is a hit-and-run. Masses kg.
    /// </summary>
    public static double HitAndRunVelocityRatio(double mTarget, double mProjectile, double b)
    {
        var zeta = (mTarget - mProjectile) / (mTarget + mProjectile);
        var z2 = zeta * zeta;
        var theta = Math.Pow(1 - b, Kg5);
        return Kg1 * z2 * theta + Kg2 * z2 + Kg3 * theta + Kg4;
    }

    /// <summary>
    /// LS12 universal law for the largest remnant, M_lr/M_tot, at <paramref name="qRatio"/> = Q_R/Q'*_RD: linear
    /// −0.5(q − 1) + 0.5 below <see cref="SuperCatastrophicOnset"/>, (0.1/1.8^η) q^η above it. Continuous at 1.8 (both 0.1).
    /// </summary>
    public static double LargestRemnantFraction(double qRatio, double eta = EtaDefault)
    {
        if (qRatio < SuperCatastrophicOnset) return Math.Clamp(-0.5 * (qRatio - 1) + 0.5, 0, 1);
        return 0.1 / Math.Pow(SuperCatastrophicOnset, eta) * Math.Pow(qRatio, eta);
    }

    /// <summary>
    /// Head-on disruption threshold Q*_RD (J/kg) for a pair: R_C1 from the total mass, then the mass-ratio correction.
    /// Masses kg; order does not matter.
    /// </summary>
    private static double QStarRD(double m1, double m2, double cStar, double muBar)
    {
        var gamma = Math.Min(m1, m2) / Math.Max(m1, m2);
        return QStarRDEqualMass(CombinedRadiusAtRho1(m1 + m2), cStar) * MassRatioCorrection(gamma, muBar);
    }

    /// <summary>
    /// Classify one impact and derive every LS12/KG10 quantity. Masses kg, radii km, <paramref name="v"/> the impact speed
    /// in km/s, <paramref name="b"/> = sin θ in [0, 1]. The larger mass is the target whichever order they are passed in.
    /// </summary>
    public static CollisionRegimeResult Evaluate(double mTarget, double mProjectile, double rTarget, double rProjectile, double v, double b,
        double cStar = CStarStrengthless, double muBar = MuBarDefault, double eta = EtaDefault)
    {
        if (mProjectile > mTarget) (mTarget, mProjectile, rTarget, rProjectile) = (mProjectile, mTarget, rProjectile, rTarget);
        b = Math.Clamp(b, 0, 1);
        var mTot = mTarget + mProjectile;
        var gamma = mProjectile / mTarget;
        var mu = mTarget * mProjectile / mTot;
        var qR = SpecificImpactEnergy(mTarget, mProjectile, v);
        var rc1 = CombinedRadiusAtRho1(mTot);
        var q1 = QStarRDEqualMass(rc1, cStar);
        var qStar = q1 * MassRatioCorrection(gamma, muBar);
        var alpha = InteractingFraction(rTarget, rProjectile, b);
        var muAlpha = alpha * mProjectile * mTarget / (alpha * mProjectile + mTarget);
        var qPrime = ObliqueCorrection(qStar, mu, muAlpha, muBar);
        var vPrimeStar = Math.Sqrt(2 * (qPrime / KmToJ) * mTot / mu);
        var rSum = rTarget + rProjectile;
        var vEsc = Math.Sqrt(2 * G * mTot / rSum);
        var vEscPrime = Math.Sqrt(2 * G * (alpha * mProjectile + mTarget) / rSum);
        var bCrit = GrazingThreshold(rTarget, rProjectile);
        var vhrRatio = HitAndRunVelocityRatio(mTarget, mProjectile, b);
        var vHr = vhrRatio * vEsc;
        var fLr = LargestRemnantFraction(qR / qPrime, eta);
        var mLr = fLr * mTot;

        CollisionRegime regime;
        if (v < vEscPrime) regime = CollisionRegime.PerfectMerge;
        else if (b > bCrit)
        {
            if (v < vHr) regime = CollisionRegime.GrazeAndMerge;
            else if (mLr >= mTarget) regime = CollisionRegime.HitAndRun;
            else regime = CollisionRegime.Erosion;
        }
        else if (mLr > mTarget) regime = CollisionRegime.PartialAccretion;
        else if (mLr > 0.1 * mTot) regime = CollisionRegime.PartialErosion;
        else regime = CollisionRegime.SuperCatastrophic;

        var pRemnant = mProjectile;
        double pQ = 0;
        if (regime == CollisionRegime.HitAndRun)
        {
            // LS12's reverse impact: the projectile is the target, struck at the same speed by the slice of the real
            // target it ploughs through. That slice is the target's spherical cap of the same overlap depth l; the
            // reverse impact is taken head-on (no second oblique correction), which is this port's reading.
            var l = rSum * (1 - b);
            var mSlice = mTarget * Math.Clamp(l * l * (3 * rTarget - l) / (4 * rTarget * rTarget * rTarget), 0, 1);
            if (mSlice > 0)
            {
                pQ = SpecificImpactEnergy(mProjectile, mSlice, v) / QStarRD(mProjectile, mSlice, cStar, muBar);
                pRemnant = Math.Min(mProjectile, LargestRemnantFraction(pQ, eta) * (mProjectile + mSlice));
            }
        }

        return new CollisionRegimeResult
        {
            Regime = regime, TargetMass = mTarget, ProjectileMass = mProjectile, Gamma = gamma, Mu = mu, B = b, ImpactSpeed = v,
            QR = qR, RC1 = rc1, QStarRDGamma1 = q1, VStarGamma1 = VStarEqualMass(rc1, cStar), QStarRD = qStar, Alpha = alpha,
            MuAlpha = muAlpha, QPrimeStarRD = qPrime, VPrimeStar = vPrimeStar, VEsc = vEsc, VEscPrime = vEscPrime, BCrit = bCrit,
            VHrOverVEsc = vhrRatio, VHr = vHr, LargestRemnantFraction = fLr, LargestRemnantMass = mLr,
            ProjectileRemnantMass = pRemnant, ProjectileQRatio = pQ,
        };
    }

    /// <summary>
    /// Split <paramref name="escapingMass"/> (kg) into discrete fragments drawn from N(&gt;D) ∝ D^−β, i.e. N(&gt;m) ∝ m^−β/3.
    ///
    /// Normalisation (LS12 style): the power law with N(&gt;D_max) = 1 whose integrated mass is the escaping mass has a
    /// largest fragment m_max = M_esc(3−β)/β, capped at <paramref name="largestCap"/> (the largest remnant, which a fragment
    /// may not outweigh). The same law puts <paramref name="maxFragments"/> bodies above m_min = m_max·N^(−3/β); fragments are
    /// drawn i.i.d. from the law truncated to [m_min, m_max] by inverse transform, until the mass runs out or N are drawn.
    /// Whatever is left — the unresolved small end, which for β near 3 is most of the mass — is the particle tier.
    /// Σ Masses + ParticleTierMass = escapingMass up to rounding in the running subtraction. Requires 0 &lt; β &lt; 3.
    /// </summary>
    public static FragmentSample SampleFragments(double escapingMass, Func<double> rand, int maxFragments = 64, double beta = BetaDefault,
        double largestCap = double.PositiveInfinity)
    {
        if (!(beta > 0 && beta < 3)) throw new ArgumentOutOfRangeException(nameof(beta), beta, "the mass integral of N(>D) ∝ D^-β converges only for 0 < β < 3");
        if (!(escapingMass > 0) || maxFragments <= 0) return new FragmentSample([], Math.Max(0, escapingMass));
        var mMax0 = escapingMass * (3 - beta) / beta;
        var mMax = Math.Min(mMax0, largestCap);
        if (!(mMax > 0)) return new FragmentSample([], escapingMass);
        var mMin = Math.Min(mMax0 * Math.Pow(maxFragments, -3 / beta), mMax);
        var q = beta / 3;
        var aMax = Math.Pow(mMax, -q);
        var aMin = Math.Pow(mMin, -q);
        var masses = new List<double>(maxFragments);
        var remaining = escapingMass;
        while (masses.Count < maxFragments && remaining >= mMin && remaining > 0)
        {
            // Clamped because the pow round trip can land an ulp outside the range, and a fragment may never outweigh the cap.
            var m = Math.Clamp(Math.Pow(aMax + rand() * (aMin - aMax), -1 / q), mMin, mMax);
            if (m > remaining) m = remaining;
            masses.Add(m);
            remaining -= m;
        }
        masses.Sort((x, y) => y.CompareTo(x));
        return new FragmentSample([.. masses], remaining);
    }
}
