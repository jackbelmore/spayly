using System;
using Godot;
using Spayly.Game.Render;
using Spayly.Sim;

namespace Spayly.Game.App;

/// <summary>
/// Sun state from live mass, auto-exposure and the Sun's light (app.ts updateSunState, tick steps 5, 6, 16).
///
/// Light: one OmniLight3D at the Sun's true (unsqueezed) camera-relative position, so every body's terminator faces the
/// real Sun at once — a DirectionalLight3D is right only for one body. Its attenuation exponent is settings.falloff and its
/// energy is luminosity × (1 AU in render units)^falloff, which makes the light at a surface exactly the web's
/// irradiance() = L × (AU/d)^falloff (Godot's omni term is energy × d^−attenuation inside a range window).
/// </summary>
public partial class App
{
    struct SunState
    {
        /// <summary>Relative to the present Sun.</summary>
        public double Luminosity;
        /// <summary>W.</summary>
        public double Watts;
        /// <summary>K.</summary>
        public double TempK;
        /// <summary>Linear RGB.</summary>
        public Color Color;
    }

    SunState sunState = new() { Luminosity = 1, Watts = Constants.SunLuminosity, TempK = 5772, Color = new Color(1, 0.96f, 0.9f) };

    /// <summary>Luminosity ∝ (M/M☉)^3.5 with a brown-dwarf branch below 0.08 M☉; temperature from luminosity and radius.</summary>
    void UpdateSunState()
    {
        var sun = Star();
        if (sun.Kind != BodyKind.Star)
        {
            sunState.Luminosity = 0;
            sunState.Watts = 0;
            sunState.TempK = 1000;
            sunState.Color = new Color(1, 0.6f, 0.4f);
            return;
        }
        var mRel = sun.Mass / Constants.SunMass;
        var lum = mRel < 0.08 ? 1e-4 * Math.Pow(mRel / 0.08, 2) : Math.Pow(mRel, 3.5);
        var watts = lum * Constants.SunLuminosity;
        var r = sun.Radius * 1000;
        var t = Math.Pow(watts / (4 * Math.PI * r * r * Constants.StefanBoltzmann), 0.25);
        sunState.Luminosity = lum;
        sunState.Watts = watts;
        sunState.TempK = t;
        sunState.Color = RenderUnits.Blackbody(t);
    }

    /// <summary>Exposure follows irradiance at the focus^−0.85, smoothed in log space with a 1/3 s time constant.</summary>
    void UpdateExposure(double dtReal)
    {
        var sun = Star();
        var focusBody = Rig.Focus ?? Selected ?? Universe.ById("earth");
        var irr = 1.0;
        if (focusBody != null && focusBody.Kind != BodyKind.Star)
        {
            var d = Math.Max(Vec3.Length(focusBody.Pos - sun.Pos), 1);
            irr = Math.Max(sunState.Luminosity, 1e-6) * Math.Pow(Constants.AU / d, Settings.Falloff);
        }
        var target = Math.Clamp(Math.Pow(irr, -0.85), 1e-3, 100);
        var k = 1 - Math.Exp(-dtReal * 3);
        Exposure = Math.Exp(Math.Log(Exposure) + (Math.Log(target) - Math.Log(Exposure)) * k);
    }

    void UpdateSunLight()
    {
        var sun = Star();
        var lit = sun.Kind == BodyKind.Star && sunState.Luminosity > 0;
        sunLight.Visible = lit;
        if (!lit) return;
        sunLight.Position = ctx.Origin.PlaceTrue(sun.Pos);
        sunLight.OmniAttenuation = (float)Settings.Falloff;
        var auUnits = Constants.AU * RenderUnits.Scale;
        sunLight.LightEnergy = (float)(sunState.Luminosity * Math.Pow(auUnits, Settings.Falloff));
        sunLight.LightColor = sunState.Color.LinearToSrgb();
    }
}
