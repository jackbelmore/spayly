namespace Spayly.Sim.Sph;

/// <summary>
/// Particle state as structure-of-arrays, double precision, SI: m, m/s, m/s², kg, kg/m³, J/kg, Pa. Positions are relative
/// to the patch's own origin (the collision's centre of mass for a hero collision), which keeps them small enough for the
/// GPU's float32 copy. <see cref="Id"/> is the particle's index at creation and survives any reordering a backend does.
/// </summary>
public sealed class SphState
{
    public int Count { get; }
    /// <summary>Material table; <see cref="Mat"/> indexes it.</summary>
    public Material[] Materials { get; }

    public readonly double[] X, Y, Z;
    public readonly double[] Vx, Vy, Vz;
    /// <summary>Total acceleration from the last force evaluation, m/s².</summary>
    public readonly double[] Ax, Ay, Az;
    /// <summary>Gravitational part of the last acceleration, m/s² (for the gravity timestep criterion).</summary>
    public readonly double[] Gx, Gy, Gz;
    public readonly double[] M;
    /// <summary>Smoothing length h, m; the kernel reaches 2h.</summary>
    public readonly double[] H;
    public readonly double[] Rho;
    /// <summary>Specific internal energy, J/kg.</summary>
    public readonly double[] U;
    /// <summary>du/dt from the last force evaluation, W/kg.</summary>
    public readonly double[] DuDt;
    /// <summary>Pressure after the floor, Pa.</summary>
    public readonly double[] P;
    /// <summary>Sound speed, m/s.</summary>
    public readonly double[] Cs;
    /// <summary>Balsara (1995) switch f ∈ [0, 1].</summary>
    public readonly double[] Balsara;
    /// <summary>Largest |μᵢⱼ| of the last force pass (the AV signal term of the Courant condition), m/s.</summary>
    public readonly double[] MuMax;
    public readonly int[] Mat;
    /// <summary>Which source body a particle came from (0 = target, 1 = impactor for a hero collision).</summary>
    public readonly int[] Body;
    public readonly int[] Id;

    public SphState(int count, Material[] materials)
    {
        Count = count;
        Materials = materials;
        X = new double[count]; Y = new double[count]; Z = new double[count];
        Vx = new double[count]; Vy = new double[count]; Vz = new double[count];
        Ax = new double[count]; Ay = new double[count]; Az = new double[count];
        Gx = new double[count]; Gy = new double[count]; Gz = new double[count];
        M = new double[count]; H = new double[count]; Rho = new double[count]; U = new double[count];
        DuDt = new double[count]; P = new double[count]; Cs = new double[count]; Balsara = new double[count]; MuMax = new double[count];
        Mat = new int[count]; Body = new int[count]; Id = new int[count];
        for (var i = 0; i < count; i++) Id[i] = i;
    }

    public Vec3 Position(int i) => new(X[i], Y[i], Z[i]);
    public Vec3 Velocity(int i) => new(Vx[i], Vy[i], Vz[i]);

    /// <summary>Deep copy (for running two backends from one initial state).</summary>
    public SphState Clone()
    {
        var c = new SphState(Count, Materials);
        void Cp(double[] a, double[] b) => Array.Copy(a, b, a.Length);
        Cp(X, c.X); Cp(Y, c.Y); Cp(Z, c.Z); Cp(Vx, c.Vx); Cp(Vy, c.Vy); Cp(Vz, c.Vz);
        Cp(Ax, c.Ax); Cp(Ay, c.Ay); Cp(Az, c.Az); Cp(Gx, c.Gx); Cp(Gy, c.Gy); Cp(Gz, c.Gz);
        Cp(M, c.M); Cp(H, c.H); Cp(Rho, c.Rho); Cp(U, c.U); Cp(DuDt, c.DuDt); Cp(P, c.P); Cp(Cs, c.Cs);
        Cp(Balsara, c.Balsara); Cp(MuMax, c.MuMax);
        Array.Copy(Mat, c.Mat, Count); Array.Copy(Body, c.Body, Count); Array.Copy(Id, c.Id, Count);
        return c;
    }

    /// <summary>Σm (kg), Σm r (kg m), Σm v (kg m/s) and Σ r × m v (kg m²/s) about the state's origin.</summary>
    public Ledger Totals()
    {
        double m = 0, dx = 0, dy = 0, dz = 0, px = 0, py = 0, pz = 0, lx = 0, ly = 0, lz = 0;
        for (var i = 0; i < Count; i++)
        {
            var mi = M[i];
            double x = X[i], y = Y[i], z = Z[i];
            double qx = mi * Vx[i], qy = mi * Vy[i], qz = mi * Vz[i];
            m += mi;
            dx += mi * x; dy += mi * y; dz += mi * z;
            px += qx; py += qy; pz += qz;
            lx += y * qz - z * qy; ly += z * qx - x * qz; lz += x * qy - y * qx;
        }
        return new Ledger(m, new Vec3(dx, dy, dz), new Vec3(px, py, pz), new Vec3(lx, ly, lz));
    }

    /// <summary>Σ m|v| (kg m/s) and Σ |r × m v| (kg m²/s): the scales a conservation error is judged against.</summary>
    public (double Momentum, double AngularMomentum) Scales()
    {
        double p = 0, l = 0;
        for (var i = 0; i < Count; i++)
        {
            var v = Velocity(i);
            p += M[i] * Vec3.Length(v);
            l += M[i] * Vec3.Length(Vec3.Cross(Position(i), v));
        }
        return (p, l);
    }

    /// <summary>Σ ½mv², J.</summary>
    public double KineticEnergy()
    {
        double e = 0;
        for (var i = 0; i < Count; i++) e += 0.5 * M[i] * (Vx[i] * Vx[i] + Vy[i] * Vy[i] + Vz[i] * Vz[i]);
        return e;
    }

    /// <summary>Σ m u, J.</summary>
    public double InternalEnergy()
    {
        double e = 0;
        for (var i = 0; i < Count; i++) e += M[i] * U[i];
        return e;
    }

    /// <summary>
    /// Plummer-softened gravitational potential energy −Σᵢ&lt;ⱼ G mᵢmⱼ/√(r² + ε²), J, by direct summation (O(N²): for
    /// diagnostics and tests). <paramref name="g"/> in m³ kg⁻¹ s⁻², <paramref name="eps"/> in m.
    /// </summary>
    public double PotentialEnergy(double g, double eps)
    {
        var e2 = eps * eps;
        double w = 0;
        for (var i = 0; i < Count; i++)
        {
            double xi = X[i], yi = Y[i], zi = Z[i];
            double s = 0;
            for (var j = i + 1; j < Count; j++)
            {
                double dx = X[j] - xi, dy = Y[j] - yi, dz = Z[j] - zi;
                s += M[j] / Math.Sqrt(dx * dx + dy * dy + dz * dz + e2);
            }
            w -= g * M[i] * s;
        }
        return w;
    }
}
