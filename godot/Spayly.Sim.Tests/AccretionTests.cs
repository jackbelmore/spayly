using static Spayly.Sim.Constants;

namespace Spayly.Sim.Tests;

// No vitest file: accretion-fed discs exist only in the Godot port. Checked against numbers from outside the code: the
// Schwarzschild efficiency 1 − √(8/9) = 5.72 %, the Eddington luminosity 1.26 × 10³¹ W per solar mass, and the exact
// exponential reservoir.
public class AccretionTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Fact]
    public void EfficiencyIsTheBindingEnergyAtTheIsco()
    {
        Assert.True(Math.Abs(Accretion.Efficiency - 0.05719) < 1e-5, $"ε = {Accretion.Efficiency}");
    }

    [Fact]
    public void ViscousTimeGrowsLinearlyWithMass()
    {
        var t10 = Accretion.ViscousTime(10 * SunMass);
        var t1e6 = Accretion.ViscousTime(1e6 * SunMass);
        output.WriteLine($"viscous time: 10 Msun {t10:G4} s, 1e6 Msun {t1e6 / Day:G3} d");
        Assert.True(Math.Abs(t1e6 / t10 / 1e5 - 1) < 1e-12, $"ratio {t1e6 / t10}");
        // (1/α)(r/H)² = 1000 dynamical times at 10 r_s, where Ω_PW = 252 rad/s for 10 M☉.
        Assert.InRange(t10, 3.9, 4.1);
    }

    [Fact]
    public void LuminosityIsEpsilonMdotCSquaredCappedAtEddington()
    {
        var m = 10 * SunMass;
        var edd = BlackHole.EddingtonLuminosity(m);
        Assert.True(Math.Abs(edd / (10 * 1.257e31) - 1) < 0.01, $"L_Edd {edd:G4} W");
        Assert.Equal(0, Accretion.Luminosity(0, m));
        var mdot = 1e12;
        Close.To(Accretion.Efficiency * mdot * 299792458.0 * 299792458.0, Accretion.Luminosity(mdot, m), 12);
        Assert.Equal(edd, Accretion.Luminosity(1e3 * Accretion.EddingtonRate(m), m));
        Close.To(edd, Accretion.Luminosity(Accretion.EddingtonRate(m), m), 12);
    }

    [Fact]
    public void FeedSpreadsADepositOverTheViscousTimeAndConservesIt()
    {
        var tau = 1000.0;
        var f = new AccretionFeed(tau);
        f.Deposit(5e15);
        Close.To(5e15 / tau, f.Mdot, 12);
        f.Advance(tau);
        Close.To(5e15 * Math.Exp(-1), f.Reservoir, 12);
        Assert.Equal(f.Deposited, f.Accreted + f.Reservoir, 1e-3);
        // Ṁ integrated over many small steps is what left the reservoir.
        double radiated = 0;
        for (var k = 0; k < 20000; k++)
        {
            var before = f.Reservoir;
            f.Advance(0.5);
            radiated += before - f.Reservoir;
        }
        Assert.True(Math.Abs((f.Accreted - 5e15 * (1 - Math.Exp(-1))) / radiated - 1) < 1e-9);
        // Rewinding or pausing leaves it alone.
        var r = f.Reservoir;
        f.Advance(-100);
        f.Advance(0);
        Assert.Equal(r, f.Reservoir);
        Assert.True(Math.Abs((f.Accreted + f.Reservoir) / f.Deposited - 1) < 1e-14);
    }
}
