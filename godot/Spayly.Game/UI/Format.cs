using System;
using System.Globalization;
using Godot;
using Spayly.Sim;

namespace Spayly.Game.UI;

/// <summary>
/// Port of src/ui/format.ts. Same thresholds, units and text; two deliberate differences, both web bugs:
/// <see cref="Num"/> strips trailing zeros only after a decimal point (the web's <c>/\.?0+$/</c> turns num(20, 2) into
/// "2" and a 99.96 that rounds to "100" into "1"), and <see cref="Date"/> covers the JS Date range (±275 000 years) itself
/// because DateTime stops at year 1.
/// </summary>
public static class Format
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    const string Sup = "⁰¹²³⁴⁵⁶⁷⁸⁹";

    static string Superscript(int n)
    {
        Span<char> buf = stackalloc char[12];
        var i = buf.Length;
        var a = Math.Abs((long)n);
        do
        {
            buf[--i] = Sup[(int)(a % 10)];
            a /= 10;
        } while (a > 0);
        if (n < 0) buf[--i] = '⁻';
        return new string(buf[i..]);
    }

    /// <summary>JS Math.round: half rounds towards +∞.</summary>
    static double JsRound(double x) => Math.Floor(x + 0.5);

    /// <summary>Number.prototype.toLocaleString() in en-US for an integer-valued double: "1,234,567".</summary>
    static string Grouped(double x) => x.ToString("#,0", Inv);

    /// <summary>5.97 × 10²⁴ style.</summary>
    public static string Sci(double x, int digits = 3)
    {
        if (x == 0) return "0";
        if (!double.IsFinite(x)) return "∞";
        var e = (int)Math.Floor(Math.Log10(Math.Abs(x)));
        var m = x / Math.Pow(10, e);
        return m.ToString("F" + (digits - 1), Inv) + " × 10" + Superscript(e);
    }

    public static string Num(double x, int digits = 3)
    {
        if (!double.IsFinite(x)) return "∞";
        var a = Math.Abs(x);
        if (a >= 1e6 || (a > 0 && a < 1e-3)) return Sci(x, digits);
        if (a >= 100) return Grouped(JsRound(x));
        // G{digits} is toPrecision without its padding zeros; in [1e-3, 100) it never switches to exponent form.
        return x.ToString("G" + digits, Inv);
    }

    /// <summary>Number.prototype.toPrecision: exponent form ("1.5e+2") when the exponent is ≥ p or &lt; −6.</summary>
    public static string ToPrecision(double x, int p)
    {
        if (!double.IsFinite(x)) return x.ToString(Inv);
        if (x == 0) return 0.0.ToString("F" + (p - 1), Inv);
        // Round to p significant digits first: 99.96 at p = 2 is 1.0e+2, whose exponent decides the form.
        var rounded = double.Parse(x.ToString("E" + (p - 1), Inv), Inv);
        var e = (int)Math.Floor(Math.Log10(Math.Abs(rounded)));
        if (e < -6 || e >= p)
        {
            var m = rounded / Math.Pow(10, e);
            return m.ToString("F" + (p - 1), Inv) + "e" + (e < 0 ? "-" : "+") + Math.Abs(e).ToString(Inv);
        }
        return rounded.ToString("F" + Math.Max(0, p - 1 - e), Inv);
    }

    /// <summary>kg, with M☉ / M⊕ / Moons alongside when meaningful.</summary>
    public static string Mass(double kg)
    {
        var earth = kg / 5.9722e24;
        var sun = kg / 1.98841e30;
        var rel = "";
        if (sun >= 0.01) rel = $" ({Num(sun)} M☉)";
        else if (earth >= 0.001) rel = $" ({Num(earth)} M⊕)";
        else if (kg >= 1e15) rel = $" ({Num(kg / 7.3458e22)} Moons)";
        return $"{Sci(kg)} kg{rel}";
    }

    /// <summary>km → AU / million km / km / m.</summary>
    public static string Distance(double km)
    {
        if (km >= 0.02 * Constants.AU) return $"{Num(km / Constants.AU)} AU";
        if (km >= 1e6) return $"{Num(km / 1e6)} million km";
        if (km >= 1) return $"{Grouped(JsRound(km))} km";
        return $"{JsRound(km * 1000).ToString(Inv)} m";
    }

    /// <summary>m → thousand km / km / m.</summary>
    public static string LengthM(double m)
    {
        if (m >= 1e6) return $"{Num(m / 1e6)} thousand km";
        if (m >= 1000) return $"{Num(m / 1000)} km";
        return $"{Num(m)} m";
    }

    /// <summary>km/s → mm/s / m/s / km/s.</summary>
    public static string Speed(double kms)
    {
        if (kms < 0.001) return $"{Num(kms * 1e6)} mm/s";
        if (kms < 1) return $"{Num(kms * 1000)} m/s";
        return $"{Num(kms)} km/s";
    }

    /// <summary>s → s / min / h / days / years.</summary>
    public static string Duration(double s)
    {
        var a = Math.Abs(s);
        if (!double.IsFinite(a)) return "∞";
        if (a < 60) return $"{Num(s)} s";
        if (a < 3600) return $"{Num(s / 60)} min";
        if (a < 86400 * 2) return $"{Num(s / 3600)} h";
        if (a < 86400 * 365.25 * 2) return $"{Num(s / 86400)} days";
        return $"{Num(s / (86400 * 365.25))} years";
    }

    /// <summary>Warp as the web prints it, sim s per real s; negative is rewind (◀).</summary>
    public static string Warp(double w)
    {
        if (w == 0) return "paused";
        var a = Math.Abs(w);
        var sign = w < 0 ? "◀ " : "";
        if (a < 1.5) return $"{sign}real time";
        if (a < 60) return $"{sign}{Num(a, 2)} s/s";
        if (a < 3600) return $"{sign}{Num(a / 60, 2)} min/s";
        if (a < 86400) return $"{sign}{Num(a / 3600, 2)} h/s";
        if (a < 86400 * 365.25) return $"{sign}{Num(a / 86400, 2)} days/s";
        return $"{sign}{Num(a / (86400 * 365.25), 2)} yr/s";
    }

    /// <summary>
    /// UTC date of a Julian day, "YYYY-MM-DD HH:MM UTC", proleptic Gregorian like a JS Date: year 0 prints "0", negative
    /// years "N BCE", years past 9999 grouped. "—" outside the JS Date range (±8.64e15 ms from 1970).
    /// </summary>
    public static string Date(double jd)
    {
        if (!double.IsFinite(jd)) return "—";
        var ms = Math.Truncate((jd - 2440587.5) * 86400000);
        if (!(Math.Abs(ms) <= 8.64e15)) return "—";
        var days = (long)Math.Floor(ms / 86400000);
        var msOfDay = (long)(ms - days * 86400000.0);
        var (y, mo, d) = CivilFromDays(days);
        var h = msOfDay / 3600000;
        var mi = msOfDay / 60000 % 60;
        var yStr = y < 0 ? (-y).ToString(Inv) + " BCE" : y > 9999 ? y.ToString("#,0", Inv) : y.ToString(Inv);
        return string.Create(Inv, $"{yStr}-{mo:00}-{d:00} {h:00}:{mi:00} UTC");
    }

    /// <summary>Days since 1970-01-01 → proleptic Gregorian (year, month, day); H. Hinnant's civil_from_days.</summary>
    static (long Y, int M, int D) CivilFromDays(long z)
    {
        z += 719468;
        var era = (z >= 0 ? z : z - 146096) / 146097;
        var doe = z - era * 146097;
        var yoe = (doe - doe / 1460 + doe / 36524 - doe / 146096) / 365;
        var y = yoe + era * 400;
        var doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
        var mp = (5 * doy + 2) / 153;
        var d = (int)(doy - (153 * mp + 2) / 5 + 1);
        var m = (int)(mp < 10 ? mp + 3 : mp - 9);
        return (m <= 2 ? y + 1 : y, m, d);
    }

    /// <summary>J, with the TNT equivalent in t / kt / Mt / million Mt.</summary>
    public static string Energy(double J)
    {
        var mt = J / Constants.MtTnt;
        string tnt;
        if (mt >= 1e6) tnt = $"{Num(mt / 1e6)} million Mt";
        else if (mt >= 1) tnt = $"{Num(mt)} Mt";
        else if (mt >= 1e-3) tnt = $"{Num(mt * 1000)} kt";
        else tnt = $"{Num(mt * 1e6)} t";
        return $"{Sci(J, 2)} J ({tnt} of TNT)";
    }

    /// <summary>K → "288 K (15 °C)".</summary>
    public static string Temperature(double K) => string.Create(Inv, $"{JsRound(K)} K ({JsRound(K - 273.15)} °C)");

    /// <summary>rad → whole degrees.</summary>
    public static string Angle(double rad) => string.Create(Inv, $"{JsRound(rad * 180 / Math.PI)}°");

    /// <summary>Ten checks of the port against what format.ts prints; returns the pass count and logs each failure.</summary>
    public static int SelfTest()
    {
        (string Got, string Want)[] cases =
        [
            (Mass(5.9722e24), "5.97 × 10²⁴ kg (1 M⊕)"),
            (Distance(Constants.AU), "1 AU"),
            (Distance(384400), "384,400 km"),
            (LengthM(1500), "1.5 km"),
            (Speed(0.5), "500 m/s"),
            (Duration(7200), "2 h"),
            (Warp(-86400), "◀ 1 days/s"),
            (Date(2451545.0), "2000-01-01 12:00 UTC"),
            // JD 0 is 4713 BC Jan 1 Julian = −4713-11-24 12:00 proleptic Gregorian, which a JS Date reports as year −4713.
            (Date(0), "4713 BCE-11-24 12:00 UTC"),
            (Energy(4.184e15), "4.2 × 10¹⁵ J (1 Mt of TNT)"),
        ];
        var pass = 0;
        foreach (var (got, want) in cases)
        {
            if (got == want) pass++;
            else GD.Print($"[hud] selftest FAIL got \"{got}\" want \"{want}\"");
        }
        return pass;
    }
}
