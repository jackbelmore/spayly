using System;
using System.Collections.Generic;
using System.Globalization;
using Godot;
using Spayly.Game.Render;
using Spayly.Sim;

namespace Spayly.Game.App;

/// <summary>
/// Accretion-fed discs, one per hole (Spayly.Sim.Accretion does the physics). Each hole has its own Ṁ: bodies Collide merges
/// into it and debris particles it absorbs are deposited in its <see cref="AccretionFeed"/> and drain over the viscous time,
/// and a tidal disruption adds its own t^(−5/3) fallback. L = ε Ṁ c², capped at Eddington, sets that hole's disc brightness
/// and display temperature; with nothing falling in the disc fades out and the hole is dark apart from its lensing.
/// Dust re-accreting through the debris ledger does not count: it is bookkeeping for particles no longer tracked, and a TDE
/// stream's return is already its fallback.
///
/// Capture verbs (App.Capture.cs routes them here): <c>feedBH:id,kg</c> (a deposit, as a swallowed body would make) and
/// <c>logDiscs</c>.
/// </summary>
public partial class App
{
    /// <summary>[GAME] Disc brightness at the Eddington luminosity (the look the M4 disc had at its fixed brightness).</summary>
    const double DiscBrightnessAtEddington = 6;
    /// <summary>[ART] Decades of luminosity below Eddington that stay visible; below 10^−6 L_Edd the disc is off.</summary>
    const double DiscDecadesShown = 6;
    /// <summary>[GAME] Coolest display temperature, K: a dim disc glows deep orange rather than vanishing into the red.</summary>
    const double DiscDisplayFloorK = 2000;
    /// <summary>[ART] Real seconds from a disruption to the first fallback return, and per tenfold of fallback time after it.</summary>
    const double FlareArtSeconds = 10, FlareArtSecondsPerDecade = 5;

    sealed class TdeFallback
    {
        public required TidalDisruption Event;
        public double SimStart;
        public double ArtReal;
        /// <summary>Mass the hole has swallowed directly since the disruption, kg: a plunging stream falls in as particles.</summary>
        public double Absorbed;
    }

    sealed class HoleDisc
    {
        public required Body Hole;
        public required AccretionFeed Feed;
        public TdeFallback? Tde;
        /// <summary>kg/s: from the reservoir and from fallback.</summary>
        public double FeedMdot, FallbackMdot;
        /// <summary>W.</summary>
        public double Luminosity;
        public double Brightness, PeakK;
    }

    readonly Dictionary<string, HoleDisc> holeDiscs = [];
    readonly List<string> staleDiscs = [];

    HoleDisc DiscFor(Body hole)
    {
        // A fault rollback replaces every Body: a restored hole starts dark, as it was before whatever lit it. Undo rebinds the
        // discs its point kept to the restored holes itself (App.Undo.cs), so they never reach here.
        if (holeDiscs.TryGetValue(hole.Id, out var d) && ReferenceEquals(d.Hole, hole)) return d;
        d = new HoleDisc { Hole = hole, Feed = new AccretionFeed(Accretion.ViscousTime(hole.Mass)), PeakK = DiscDisplayFloorK };
        holeDiscs[hole.Id] = d;
        return d;
    }

    /// <summary>Mass <paramref name="kg"/> handed to <paramref name="hole"/>'s disc; it radiates over the viscous time.</summary>
    void FeedHole(Body hole, double kg, string source)
    {
        if (hole.Kind != BodyKind.BlackHole || !(kg > 0)) return;
        var d = DiscFor(hole);
        d.Feed.Deposit(kg);
        if (d.Tde is { } f) f.Absorbed += kg;
        if (source.Length > 0)
            GD.Print(string.Create(CultureInfo.InvariantCulture,
                $"[disc] {hole.Id} fed {kg:G4} kg by {source}: reservoir={d.Feed.Reservoir:G4} kg tau={d.Feed.ViscousTime:G4} s mdot={d.Feed.Mdot:G4} kg/s"));
    }

    /// <summary>A TDE on <paramref name="hole"/>: its disc follows the fallback; replaces any earlier one on the same hole.</summary>
    void StartTdeFallback(Body hole, TidalDisruption d)
    {
        DiscFor(hole).Tde = new TdeFallback { Event = d, SimStart = Universe.Time };
        GD.Print(string.Create(CultureInfo.InvariantCulture,
            $"[tidal] flare on {hole.Id}: bound={d.BoundMass:G4} kg t_min={d.ReturnTime / Constants.Day:G4} d peak_mdot={d.PeakFallbackRate:G4} kg/s ({d.PeakFallbackRate * Constants.Year / Constants.SunMass:G3} Msun/yr, {d.PeakFallbackRate / Accretion.EddingtonRate(hole.Mass):G3} x Eddington)"));
    }

    /// <summary>
    /// [ART] Fallback time shown by the disc: linear to the first return in <see cref="FlareArtSeconds"/> real seconds, then a
    /// tenfold every <see cref="FlareArtSecondsPerDecade"/>, or sim time when that is further on (high warp).
    /// </summary>
    double FallbackTime(TdeFallback f)
    {
        var tMin = f.Event.ReturnTime;
        var art = f.ArtReal <= FlareArtSeconds ? tMin * f.ArtReal / FlareArtSeconds : tMin * Math.Pow(10, (f.ArtReal - FlareArtSeconds) / FlareArtSecondsPerDecade);
        return Math.Max(Math.Max(0, Universe.Time - f.SimStart), art);
    }

    /// <summary>Step 13: advance every hole's feed and fallback and hand the brightness to its view.</summary>
    void UpdateHoleDiscs(double dtReal)
    {
        var forward = !Paused && !Rewind;
        var dt = forward ? Math.Max(0, Sim.LastAdvanced) : 0;
        foreach (var b in Universe.Bodies)
        {
            if (b.Kind != BodyKind.BlackHole) continue;
            var d = DiscFor(b);
            d.Feed.ViscousTime = Accretion.ViscousTime(b.Mass);
            d.Feed.Advance(dt);
            d.FeedMdot = d.Feed.Mdot;
            d.FallbackMdot = 0;
            if (d.Tde is { } f)
            {
                if (forward) f.ArtReal += dtReal;
                // Bound debris the hole already swallowed as particles (a plunging star's stream) is not returned twice: the
                // analytic fallback keeps only the share of the bound mass still out there.
                var left = f.Event.BoundMass > 0 ? Math.Max(0, 1 - f.Absorbed / f.Event.BoundMass) : 0;
                d.FallbackMdot = left * f.Event.FallbackRate(FallbackTime(f));
                // Over once it is too faint to show: 10⁻⁶ of Eddington, after the first return.
                if (FallbackTime(f) > f.Event.ReturnTime && Accretion.Luminosity(d.FallbackMdot, b.Mass) < 1e-6 * BlackHole.EddingtonLuminosity(b.Mass))
                {
                    d.Tde = null;
                    GD.Print($"[tidal] flare on {b.Id} over");
                }
            }
            var edd = BlackHole.EddingtonLuminosity(b.Mass);
            d.Luminosity = Accretion.Luminosity(d.FeedMdot + d.FallbackMdot, b.Mass);
            var ratio = d.Luminosity / edd;
            d.Brightness = ratio > 0 ? DiscBrightnessAtEddington * Math.Max(0, 1 + Math.Log10(ratio) / DiscDecadesShown) : 0;
            // The thin disc's temperature goes as Ṁ^(1/4) at a given mass; Eddington shows at the M4 display peak.
            d.PeakK = Math.Max(DiscDisplayFloorK, BlackHoles.DiscPeakDisplayK * Math.Pow(Math.Max(ratio, 0), 0.25));
            if (views.TryGetValue(b.Id, out var v) && v is BlackHoleView hv)
            {
                hv.DiscBrightness = d.Brightness;
                hv.DiscPeakK = d.PeakK;
            }
        }
        staleDiscs.Clear();
        foreach (var (id, d) in holeDiscs)
            if (!ReferenceEquals(Universe.ById(id), d.Hole)) staleDiscs.Add(id);
        foreach (var id in staleDiscs) holeDiscs.Remove(id);
    }

    /// <summary>The inspector's disc row for a hole: luminosity, fraction of Eddington and what feeds it.</summary>
    public string DiscSummary(Body hole)
    {
        if (!holeDiscs.TryGetValue(hole.Id, out var d) || !(d.Luminosity > 0)) return "dark: nothing falling in";
        var edd = d.Luminosity / BlackHole.EddingtonLuminosity(hole.Mass);
        var what = d.FallbackMdot > d.FeedMdot ? "tidal fallback" : "infalling matter";
        return $"{UI.Format.Sci(d.Luminosity, 2)} W ({UI.Format.Num(100 * edd)} % Eddington) from {what}";
    }

    string DiscLogText(Body b)
    {
        if (!holeDiscs.TryGetValue(b.Id, out var d)) return " disc: none";
        return string.Create(CultureInfo.InvariantCulture,
            $" disc: reservoir={d.Feed.Reservoir:G4} kg tau={d.Feed.ViscousTime:G4} s mdot_feed={d.FeedMdot:G4} mdot_fallback={d.FallbackMdot:G4} kg/s L={d.Luminosity:G4} W L/L_Edd={d.Luminosity / BlackHole.EddingtonLuminosity(b.Mass):G4} brightness={d.Brightness:G4} peak_k={d.PeakK:F0} deposited={d.Feed.Deposited:G4} accreted={d.Feed.Accreted:G4} kg");
    }

    bool DiscAction(string verb, string arg)
    {
        switch (verb)
        {
            case "feedBH":
            {
                var p = arg.Split(',', StringSplitOptions.TrimEntries);
                if (Universe.ById(p[0]) is Body b) FeedHole(b, double.Parse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture), "feedBH");
                return true;
            }
            case "logDiscs":
                foreach (var b in Universe.Bodies)
                    if (b.Kind == BodyKind.BlackHole) GD.Print($"[disc] {b.Id} t={Universe.Time.ToString("G8", CultureInfo.InvariantCulture)} s{DiscLogText(b)}");
                return true;
            default:
                return false;
        }
    }
}
