using System;
using System.Globalization;
using Godot;
using Spayly.Sim;

namespace Spayly.Game.App;

/// <summary>
/// Gravitational-wave inspiral in the running game (M6.4): the physics is in <see cref="Universe"/> and
/// <see cref="GravitationalWaves.RadiationReaction"/>; this holds its Realism entry and two capture verbs.
/// <c>holePair:m1,m2,aKm[,e[,distanceAu]]</c> puts a bound pair of holes (M☉) far from the Sun, separation a (1 + e) at
/// apoapsis, orbiting in the ecliptic; <c>logPair</c> prints the separation, the osculating a and e, and Peters' circular a(t)
/// from the spawn.
/// </summary>
public partial class App
{
    (string A, string B, double A0, double Tau, double T0)? pair;

    public static void RegisterInspiralRealism()
    {
        Realism.Register("Gravitational-wave inspiral", RealismTag.Sci,
            "Two black holes lose orbital energy and angular momentum to gravitational waves through the 2.5PN radiation-reaction force, whose orbit average is Peters' (1964) rate: a circular pair merges after (5/256) c⁵ a⁴/(G³ m₁ m₂ M). Only hole–hole pairs feel it.",
            RealismArea.Physics);
        Realism.Register("Merged holes spin but are drawn still", RealismTag.Game,
            "A merger gives the remnant the spin of the Healy–Lousto–Zlochower (2014) fit, 0.69 for equal masses, and its radiated energy uses that spinning hole's innermost orbit; its gravity, horizon size, lensing and disc still treat it as non-spinning.",
            RealismArea.Rendering);
    }

    bool InspiralAction(string verb, string arg)
    {
        var c = CultureInfo.InvariantCulture;
        switch (verb)
        {
            case "holePair":
            {
                var p = arg.Split(',', StringSplitOptions.TrimEntries);
                var m1 = double.Parse(p[0], NumberStyles.Float, c);
                var m2 = double.Parse(p[1], NumberStyles.Float, c);
                var a = double.Parse(p[2], NumberStyles.Float, c);
                var e = p.Length > 3 ? double.Parse(p[3], NumberStyles.Float, c) : 0;
                var au = p.Length > 4 ? double.Parse(p[4], NumberStyles.Float, c) : 2000;
                RecordUndo(UndoKind.Demo, "adding a black-hole pair");
                var sun = Star();
                var k1 = m1 * Constants.SunMass;
                var k2 = m2 * Constants.SunMass;
                var mu = Constants.G * (k1 + k2);
                var rs = BlackHole.SchwarzschildRadiusKm(k1) + BlackHole.SchwarzschildRadiusKm(k2);
                var r = a * (1 + e);
                var v = e == 0 ? PseudoNewtonian.CircularSpeed(mu, r, rs) : Math.Sqrt(mu / a * (1 - e) / (1 + e));
                var com = sun.Pos + new Vec3(au * Constants.AU, 0, 0);
                var f1 = k2 / (k1 + k2);
                var f2 = k1 / (k1 + k2);
                var b1 = SpawnBlackHoleAt(m1, com + new Vec3(r * f1, 0, 0), sun.Vel + new Vec3(0, v * f1, 0));
                var b2 = SpawnBlackHoleAt(m2, com - new Vec3(r * f2, 0, 0), sun.Vel - new Vec3(0, v * f2, 0));
                if (b1 == null || b2 == null) return true;
                var tau = 5.0 / 256 * Math.Pow(BlackHole.SpeedOfLight, 5) * Math.Pow(a, 4) / (Math.Pow(Constants.G, 3) * k1 * k2 * (k1 + k2));
                pair = (b1.Id, b2.Id, a, tau, Universe.Time);
                GD.Print(string.Create(c, $"[inspiral] pair {b1.Id}+{b2.Id}: {m1:G4}+{m2:G4} Msun a={a:G6} km ({a / rs:G4} pair r_s) e={e:G3} v_rel={v:G6} km/s period={2 * Math.PI * Math.Sqrt(a * a * a / mu):G4} s Peters tau={tau:G6} s at {au:G4} AU from the Sun"));
                return true;
            }
            case "logPair":
            {
                if (pair is not { } q || Universe.ById(q.A) is not Body b1 || Universe.ById(q.B) is not Body b2)
                {
                    GD.Print(string.Create(c, $"[inspiral] t={Universe.Time - (pair?.T0 ?? 0):G6} s pair gone; bodies={Universe.Bodies.Count}"));
                    return true;
                }
                var rv = b2.Pos - b1.Pos;
                var vv = b2.Vel - b1.Vel;
                var d = Vec3.Length(rv);
                var mu = Constants.G * (b1.Mass + b2.Mass);
                var energy = 0.5 * Vec3.Dot(vv, vv) - mu / d;
                var a = -mu / (2 * energy);
                var h = Vec3.Cross(rv, vv);
                var ecc = Vec3.Length(Vec3.Cross(vv, h) * (1 / mu) - rv * (1 / d));
                var t = Universe.Time - q.T0;
                var peters = t < q.Tau ? q.A0 * Math.Pow(1 - t / q.Tau, 0.25) : 0;
                GD.Print(string.Create(c, $"[inspiral] t={t:G6} s d={d:G6} km a={a:G6} km e={ecc:G3} Peters a={peters:G6} km ({(peters > 0 ? d / peters - 1 : double.NaN):+0.000;-0.000}) t/tau={t / q.Tau:F4}"));
                return true;
            }
            default:
                return false;
        }
    }
}
