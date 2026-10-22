using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;

// Namespace Spayly.Game, not Spayly.Game.Realism: a namespace of that name would shadow the class for every caller under
// Spayly.Game (Spayly.Game.App does exactly that to the App class, which is why the HUD needs an AppNode alias).
namespace Spayly.Game;

/// <summary>How far a choice departs from physics; the tags of docs/research/physics-and-visuals.md.</summary>
public enum RealismTag
{
    /// <summary>Established or peer-reviewed science.</summary>
    Sci,
    /// <summary>A reasonable game-oriented simplification of that science.</summary>
    Game,
    /// <summary>Creative licence, taken for fun, that departs from physical accuracy.</summary>
    Art,
}

/// <summary>Where a choice lives; the Realism panel groups by it, in this order.</summary>
public enum RealismArea { Physics, Collisions, Rendering, Time, Camera, UI }

/// <summary>One row of the registry: a feature, its tag, one sentence of why that names the source or the departure.</summary>
public sealed record RealismEntry(string Feature, RealismTag Tag, string Why, RealismArea Area)
{
    /// <summary>The unmerged branch expected to register this entry; null for entries of this build.</summary>
    public string? Owner { get; init; }
    /// <summary>Pre-written for <see cref="Owner"/> and not yet registered by it: not in this build.</summary>
    public bool Pending { get; init; }
    /// <summary>The feature whose registration removes this entry: the branch that replaces what this one describes.</summary>
    public string? RetiredBy { get; init; }
}

/// <summary>
/// The registry behind <see cref="Realism"/>. Thread-safe, because other files register from static constructors, which
/// run on whichever thread first touches their class. Never throws: a bad registration is recorded as a problem for
/// <see cref="Realism.SelfTest"/>, since an exception in a static constructor would take the caller's class down with it.
/// Feature names match case-insensitively after trimming.
/// </summary>
public sealed class RealismRegistry
{
    readonly object gate = new();
    readonly List<RealismEntry> entries = [];
    readonly Dictionary<string, int> index = new(StringComparer.OrdinalIgnoreCase);
    readonly List<string> problems = [];
    readonly List<string> retired = [];
    int version;

    /// <summary>Bumped by every change; the HUD rebuilds its panel when it moves.</summary>
    public int Version => Volatile.Read(ref version);

    /// <summary>Add pre-written entries; a name already present is a problem, and the first one stays.</summary>
    public void Seed(IEnumerable<RealismEntry> list, string source)
    {
        lock (gate)
        {
            foreach (var e in list)
            {
                var key = e.Feature.Trim();
                if (index.ContainsKey(key))
                {
                    problems.Add($"duplicate feature '{key}' in {source}");
                    continue;
                }
                index[key] = entries.Count;
                entries.Add(e);
            }
            version++;
        }
    }

    /// <summary>
    /// Add an entry, or make a pending one live with the registering branch's own text. Registering a live name again with
    /// identical text does nothing; with different text the later text wins and the clash is recorded.
    /// </summary>
    public void Register(string feature, RealismTag tag, string why, RealismArea area)
    {
        feature = feature.Trim();
        why = why.Trim();
        lock (gate)
        {
            if (feature.Length == 0)
            {
                problems.Add("Register called with an empty feature name");
                return;
            }
            if (index.TryGetValue(feature, out var i))
            {
                var old = entries[i];
                if (!old.Pending)
                {
                    if (old.Tag == tag && old.Why == why && old.Area == area) return;
                    problems.Add($"'{feature}' registered twice with different text (the later one wins)");
                }
                entries[i] = old with { Feature = feature, Tag = tag, Why = why, Area = area, Pending = false };
            }
            else
            {
                index[feature] = entries.Count;
                entries.Add(new RealismEntry(feature, tag, why, area));
            }
            RetireFor(feature);
            version++;
        }
    }

    /// <summary>As <see cref="Register(string, RealismTag, string, RealismArea)"/> with the tag spelled "SCI", "GAME" or "ART".</summary>
    public void Register(string feature, string tag, string why, RealismArea area)
    {
        if (TryParseTag(tag, out var t))
        {
            Register(feature, t, why, area);
            return;
        }
        lock (gate) problems.Add($"'{feature}': unknown tag '{tag}' (SCI, GAME or ART)");
    }

    /// <summary>"SCI", "GAME" or "ART", any case.</summary>
    public static bool TryParseTag(string s, out RealismTag tag)
    {
        switch (s.Trim().ToUpperInvariant())
        {
            case "SCI": tag = RealismTag.Sci; return true;
            case "GAME": tag = RealismTag.Game; return true;
            case "ART": tag = RealismTag.Art; return true;
            default: tag = RealismTag.Art; return false;
        }
    }

    /// <summary>Make every pending entry of <paramref name="owner"/> live exactly as pre-written; returns how many.</summary>
    public int ConfirmPending(string owner)
    {
        lock (gate)
        {
            var confirmed = new List<string>();
            for (var i = 0; i < entries.Count; i++)
            {
                if (entries[i] is not { Pending: true } e || e.Owner != owner) continue;
                entries[i] = e with { Pending = false };
                confirmed.Add(e.Feature);
            }
            foreach (var f in confirmed) RetireFor(f);
            if (confirmed.Count > 0) version++;
            return confirmed.Count;
        }
    }

    void RetireFor(string feature)
    {
        var before = entries.Count;
        foreach (var e in entries)
            if (e.RetiredBy != null && string.Equals(e.RetiredBy.Trim(), feature, StringComparison.OrdinalIgnoreCase)) retired.Add(e.Feature);
        entries.RemoveAll(e => e.RetiredBy != null && string.Equals(e.RetiredBy.Trim(), feature, StringComparison.OrdinalIgnoreCase));
        if (entries.Count == before) return;
        index.Clear();
        for (var i = 0; i < entries.Count; i++) index[entries[i].Feature.Trim()] = i;
    }

    public RealismEntry? Find(string feature)
    {
        lock (gate) return index.TryGetValue(feature.Trim(), out var i) ? entries[i] : null;
    }

    /// <summary>A copy of every entry in registration order (allocates; for the panel's rebuild and the self-test).</summary>
    public RealismEntry[] Snapshot()
    {
        lock (gate) return [.. entries];
    }

    public string[] Problems()
    {
        lock (gate) return [.. problems];
    }

    /// <summary>Entries removed because the feature named in their <see cref="RealismEntry.RetiredBy"/> registered.</summary>
    public string[] Retired()
    {
        lock (gate) return [.. retired];
    }
}

/// <summary>
/// The Realism registry: every modelling and presentation choice in the game, tagged SCI, GAME or ART, shown in the HUD's
/// Realism panel and as tooltips on the View panel's controls. Policy (research doc §6): conserved quantities stay exact,
/// regime boundaries stay sourced, only time and appearance are faked, and every ART choice is labelled here.
///
/// Other files add entries with <see cref="Register(string, RealismTag, string, RealismArea)"/> from a static constructor
/// or an init method in their own file; a branch whose entries are pre-written below as pending can register them with
/// its own text (same feature names) or, if the text fits what it built, with one <see cref="ConfirmPending"/> call.
/// </summary>
public static class Realism
{
    // Owners of the pre-written pending entries (RealismCatalogue.Expected).
    /// <summary>Star rendering: Planck × CIE colour, limb darkening, granulation, spots (committed, unmerged).</summary>
    public const string StarsBranch = "worktree-agent-ab3b8d0000724ec9c";
    /// <summary>Tiered debris: Housen–Holsapple ejecta, exact ledger, GPU T2 tier, T3 dust (committed, unmerged).</summary>
    public const string DebrisBranch = "worktree-agent-a60cd64196e555786";
    /// <summary>Black holes, lensing and the accretion disc (being coded when this list was written).</summary>
    public const string BlackHoleBranch = "worktree-agent-aa68c19e8437eee68";
    /// <summary>SPH hero collisions (being coded when this list was written).</summary>
    public const string SphBranch = "worktree-agent-a4df24d824ca1cb0c";

    // Entries the HUD binds to controls; the self-test checks they exist.
    public const string Diorama = "Diorama scale";
    public const string TrueScale = "True-scale physics";
    public const string FalloffBalanced = "Sunlight falloff: Balanced and Flat";
    public const string FalloffPhysical = "Sunlight falloff: Physical";
    public const string Bloom = "Bloom";
    public const string Belts = "Belts as test particles";
    public const string Cinematic = "Cinematic impact camera";

    static readonly RealismRegistry Global = Create();

    static RealismRegistry Create()
    {
        var r = new RealismRegistry();
        r.Seed(RealismCatalogue.BuiltIn, "the built-in list");
        r.Seed(RealismCatalogue.Expected, "the pending list");
        return r;
    }

    /// <summary>Bumped by every registration.</summary>
    public static int Version => Global.Version;

    /// <summary>Add an entry, or make the pending entry of that name live with this text.</summary>
    public static void Register(string feature, RealismTag tag, string why, RealismArea area) => Global.Register(feature, tag, why, area);

    /// <summary>As <see cref="Register(string, RealismTag, string, RealismArea)"/> with the tag as "SCI", "GAME" or "ART".</summary>
    public static void Register(string feature, string tag, string why, RealismArea area) => Global.Register(feature, tag, why, area);

    /// <summary>Register a (Feature, Tag, Why) list such as StarColor.RealismNotes, all in one area.</summary>
    public static void RegisterAll(IEnumerable<(string Feature, string Tag, string Why)> notes, RealismArea area)
    {
        foreach (var (feature, tag, why) in notes) Global.Register(feature, tag, why, area);
    }

    /// <summary>Make every pending entry of <paramref name="owner"/> (a branch constant above) live as pre-written.</summary>
    public static int ConfirmPending(string owner) => Global.ConfirmPending(owner);

    public static RealismEntry? Find(string feature) => Global.Find(feature);

    /// <summary>Every entry, live and pending, in registration order. Allocates.</summary>
    public static RealismEntry[] Snapshot() => Global.Snapshot();

    public static bool TryParseTag(string s, out RealismTag tag) => RealismRegistry.TryParseTag(s, out tag);

    public static string TagName(RealismTag t) => t switch { RealismTag.Sci => "SCI", RealismTag.Game => "GAME", _ => "ART" };

    public static string TagMeaning(RealismTag t) => t switch
    {
        RealismTag.Sci => "established science",
        RealismTag.Game => "a reasonable game simplification",
        _ => "creative licence that departs from physics",
    };

    public static string AreaName(RealismArea a) => a == RealismArea.UI ? "Interface" : a.ToString();

    /// <summary>A short name for a branch constant, for the panel's pending note.</summary>
    public static string OwnerLabel(string? owner) => owner switch
    {
        StarsBranch => "stars",
        DebrisBranch => "debris",
        BlackHoleBranch => "black holes",
        SphBranch => "SPH",
        null => "",
        _ => owner,
    };

    /// <summary>
    /// Tooltip text for the live entries named, blank-line separated: "Feature · TAG — meaning" then the why, broken into
    /// lines by hand because Godot's default tooltip label does not wrap. Unknown or pending names are skipped.
    /// </summary>
    public static string Tooltip(params string[] features)
    {
        var sb = new StringBuilder();
        foreach (var f in features)
        {
            if (Global.Find(f) is not { Pending: false } e) continue;
            if (sb.Length > 0) sb.Append("\n\n");
            sb.Append(e.Feature).Append("  ·  ").Append(TagName(e.Tag)).Append(" — ").Append(TagMeaning(e.Tag)).Append('\n');
            Wrap(sb, e.Why, 66);
        }
        return sb.ToString();
    }

    static void Wrap(StringBuilder sb, string text, int width)
    {
        var col = 0;
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (col > 0 && col + 1 + word.Length > width)
            {
                sb.Append('\n');
                col = 0;
            }
            else if (col > 0)
            {
                sb.Append(' ');
                col++;
            }
            sb.Append(word);
            col += word.Length;
        }
    }

    /// <summary>What <see cref="SelfTest"/> found: checks passed of those run, and one line per check plus the counts.</summary>
    public readonly record struct SelfTestResult(int Passed, int Total, string[] Lines);

    /// <summary>
    /// Checks the registry: every entry has a feature and a one-line why; no feature name twice; every tag and every area
    /// used by a live entry; the pre-written entries are pending (with a known owner) until their branch registers them;
    /// registering really clears pending and retires what it replaces (on a scratch registry); every retirement names a
    /// real entry; the entries the HUD's tooltips use exist. Run by the <c>realismSelftest</c> capture verb.
    /// </summary>
    public static SelfTestResult SelfTest()
    {
        var lines = new List<string>();
        int passed = 0, total = 0;
        void Check(string name, List<string> fail)
        {
            total++;
            if (fail.Count == 0) passed++;
            lines.Add(fail.Count == 0 ? $"ok   {name}" : $"FAIL {name}: {string.Join("; ", fail)}");
        }
        var all = Global.Snapshot();

        var whys = new List<string>();
        foreach (var e in all)
        {
            if (string.IsNullOrWhiteSpace(e.Feature)) whys.Add("an entry has no feature name");
            else if (string.IsNullOrWhiteSpace(e.Why)) whys.Add($"'{e.Feature}' has no why");
            else if (e.Why.Contains('\n')) whys.Add($"'{e.Feature}' has a line break in its why");
        }
        Check("every entry has a feature and a one-line why", whys);

        var dup = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in RealismCatalogue.BuiltIn) if (!seen.Add(e.Feature.Trim())) dup.Add($"'{e.Feature}' twice in Realism.cs");
        foreach (var e in RealismCatalogue.Expected) if (!seen.Add(e.Feature.Trim())) dup.Add($"'{e.Feature}' twice in Realism.cs");
        dup.AddRange(Global.Problems());
        Check("no duplicate feature names, no bad registrations", dup);

        var tagCount = new int[3];
        var areaCount = new int[6];
        int live = 0, pending = 0;
        foreach (var e in all)
        {
            if (e.Pending)
            {
                pending++;
                continue;
            }
            live++;
            tagCount[(int)e.Tag]++;
            areaCount[(int)e.Area]++;
        }
        var tags = new List<string>();
        foreach (var t in Enum.GetValues<RealismTag>()) if (tagCount[(int)t] == 0) tags.Add($"no live {TagName(t)} entry");
        Check("every tag used", tags);
        var areas = new List<string>();
        foreach (var a in Enum.GetValues<RealismArea>()) if (areaCount[(int)a] == 0) areas.Add($"no live {a} entry");
        Check("every area used", areas);

        var pend = new List<string>();
        var byOwner = new Dictionary<string, (int Pending, int Registered)>();
        foreach (var x in RealismCatalogue.Expected)
        {
            if (x.Owner is not (StarsBranch or DebrisBranch or BlackHoleBranch or SphBranch)) pend.Add($"'{x.Feature}' has no known owner");
            if (!x.Pending) pend.Add($"'{x.Feature}' is pre-written but not marked pending");
            var owner = x.Owner ?? "?";
            var (p, r) = byOwner.GetValueOrDefault(owner);
            var g = Global.Find(x.Feature);
            if (g == null) pend.Add($"'{x.Feature}' vanished");
            else if (g.Pending) byOwner[owner] = (p + 1, r);
            else byOwner[owner] = (p, r + 1);
        }
        foreach (var x in RealismCatalogue.BuiltIn)
            if (x.Pending || x.Owner != null) pend.Add($"built-in '{x.Feature}' carries an owner or pending mark");
        foreach (var e in all)
            if (e.Pending && e.Owner == null) pend.Add($"'{e.Feature}' is pending with no owner");
        Check("pre-written entries pending until their branch registers", pend);

        Check("registering clears pending and retires the replaced entry (scratch registry)", Mechanism());

        var ret = new List<string>();
        foreach (var e in all)
        {
            if (e.RetiredBy == null) continue;
            var by = Global.Find(e.RetiredBy);
            if (by == null) ret.Add($"'{e.Feature}' is retired by '{e.RetiredBy}', which is no entry");
            else if (!by.Pending) ret.Add($"'{e.Feature}' should have been retired by the live '{by.Feature}'");
        }
        Check("retirements name real, still-pending entries", ret);

        var tips = new List<string>();
        foreach (var f in new[] { Diorama, TrueScale, FalloffBalanced, FalloffPhysical, Bloom, Belts, Cinematic })
            if (Global.Find(f) is not { Pending: false }) tips.Add($"'{f}' (a HUD tooltip) is missing or pending");
        Check("HUD tooltip entries exist", tips);

        lines.Add($"entries live={live} pending={pending} SCI={tagCount[0]} GAME={tagCount[1]} ART={tagCount[2]}");
        var sb = new StringBuilder("areas");
        foreach (var a in Enum.GetValues<RealismArea>()) sb.Append(' ').Append(a).Append('=').Append(areaCount[(int)a]);
        lines.Add(sb.ToString());
        sb.Clear().Append("owners");
        foreach (var (owner, (p, r)) in byOwner) sb.Append(' ').Append(OwnerLabel(owner)).Append(": pending=").Append(p).Append(" registered=").Append(r).Append(';');
        lines.Add(sb.ToString());
        var gone = Global.Retired();
        if (gone.Length > 0) lines.Add("retired " + string.Join(", ", gone));
        return new SelfTestResult(passed, total, [.. lines]);
    }

    /// <summary>The pending → live and retirement paths on a registry of their own, so the global one is untouched.</summary>
    static List<string> Mechanism()
    {
        var fail = new List<string>();
        var r = new RealismRegistry();
        r.Seed(
        [
            new RealismEntry("Old look", RealismTag.Art, "Stand-in.", RealismArea.Rendering) { RetiredBy = "New look" },
            new RealismEntry("New look", RealismTag.Sci, "Pre-written.", RealismArea.Rendering) { Owner = "b", Pending = true },
            new RealismEntry("Other", RealismTag.Game, "Pre-written.", RealismArea.Physics) { Owner = "c", Pending = true },
        ], "the self-test");
        if (r.Find("New look") is not { Pending: true }) fail.Add("seeded entry not pending");
        var v0 = r.Version;
        r.Register(" new LOOK ", RealismTag.Sci, "Registered.", RealismArea.Rendering);
        if (r.Find("New look") is not { Pending: false, Why: "Registered.", Owner: "b" }) fail.Add("Register did not make the pending entry live with its text");
        if (r.Find("Old look") != null) fail.Add("the retired entry survived");
        if (r.Version == v0) fail.Add("Version did not move");
        var v1 = r.Version;
        r.Register("New look", RealismTag.Sci, "Registered.", RealismArea.Rendering);
        if (r.Version != v1 || r.Problems().Length != 0) fail.Add("an identical re-registration changed something");
        r.Register("New look", RealismTag.Art, "Different.", RealismArea.Rendering);
        if (r.Problems().Length != 1) fail.Add("a clashing re-registration was not recorded");
        r.Register("Tagged", "game", "By name.", RealismArea.UI);
        if (r.Find("Tagged") is not { Tag: RealismTag.Game }) fail.Add("the string-tag overload failed");
        r.Register("Bad", "SCIENCE", "Unknown tag.", RealismArea.UI);
        if (r.Find("Bad") != null || r.Problems().Length != 2) fail.Add("an unknown tag was not refused and recorded");
        if (r.ConfirmPending("c") != 1 || r.Find("Other") is not { Pending: false, Why: "Pre-written." }) fail.Add("ConfirmPending did not make the owner's entry live");
        return fail;
    }
}

/// <summary>The entries themselves. A separate class so its arrays exist before <see cref="Realism"/> seeds from them.</summary>
static class RealismCatalogue
{
    const RealismTag Sci = RealismTag.Sci, Game = RealismTag.Game, Art = RealismTag.Art;
    const RealismArea Physics = RealismArea.Physics, Collisions = RealismArea.Collisions, Rendering = RealismArea.Rendering,
        Time = RealismArea.Time, Camera = RealismArea.Camera, UI = RealismArea.UI;

    static RealismEntry E(string feature, RealismTag tag, RealismArea area, string why) => new(feature, tag, why, area);

    /// <summary>What this build does.</summary>
    public static readonly RealismEntry[] BuiltIn =
    [
        // ---------- physics ----------
        E("N-body gravity", Sci, Physics,
            "Every body pulls on every other by Newton's law of gravitation (Paczyński–Wiita's near a black hole), integrated with kick-drift-kick leapfrog, which is symplectic and time-reversible, so orbital energy stays within a bound instead of drifting."),
        E("Adaptive substeps", Game, Physics,
            "Each substep is 2 % of the shortest timescale in the system (a pair's √(r³/GM), or its crossing time r/v), clamped to 0.25 s–4 h, and a fly-by too fast to bend its pair is sampled only as finely as that bend deserves."),
        E("Warp throttle", Game, Physics,
            "A frame may spend at most 3×10⁵ pair evaluations and 12 000 substeps, so when a high warp or many moons ask for more, time runs slower than the dial (the 'limited' badge) instead of taking coarser, less accurate steps."),
        E(Realism.TrueScale, Sci, Physics,
            "Masses, radii, distances and speeds in the simulation are the real ones (km, kg, s); diorama scale, pixel floors and exposure are drawn on top and never write back to a body."),
        E("Point-mass gravity", Game, Physics,
            "Bodies attract as point masses, with no oblateness (J2), tides, radiation pressure or general relativity (beyond the pseudo-Newtonian potential round black holes), so for instance the 43″ per century that relativity adds to Mercury's perihelion precession is missing."),
        E("Softening at contact", Game, Physics,
            "Once two bodies touch, the separation in the force law is held at the sum of their radii, so a head-on hit cannot deliver an unbounded kick in the substep before the collision is resolved."),
        E(Realism.Belts, Game, Physics,
            "The 4200 main-belt, Trojan and Kuiper-belt particles feel the major bodies (on a coarser leapfrog) but are massless and pull on nothing, where the real belts hold millions of objects."),
        E("Kirkwood gaps", Game, Physics,
            "The main belt starts with its gaps already cut at the 3:1, 5:2, 7:3 and 2:1 resonances with Jupiter, because clearing them takes far longer than anyone watches."),
        E("Standish mean elements", Sci, Physics,
            "Planets start from E. M. Standish's JPL mean Keplerian elements and rates at the chosen date (valid 1800–2050), a fit rather than a full ephemeris, so starting positions are close but not exact."),
        E("Lunar and satellite elements", Sci, Physics,
            "The Moon starts from Meeus' mean lunar elements (Astronomical Algorithms ch. 47, good to a few degrees) and the other moons from JPL mean satellite elements, each system placed about its own barycentre."),
        E("Barycentre at rest", Sci, Physics,
            "The whole system's centre of mass is put at the origin and its momentum removed, so the simulation runs in the inertial frame Newton's laws hold in."),
        E("IAU rotation", Sci, Physics,
            "Spin poles, prime meridians and rotation rates follow the IAU Working Group on Cartographic Coordinates and Rotational Elements, while asteroids and fragments tumble about a seeded random axis."),

        // ---------- collisions ----------
        E("Collins–Melosh–Marcus impact model", Sci, Collisions,
            "Entry, airburst, crater, fireball, thermal, seismic, air-blast, ejecta and tsunami numbers follow Collins, Melosh & Marcus (2005), the Earth Impact Effects Program, including its pancake model of break-up in the atmosphere."),
        E("Legacy fragmentation", Game, Collisions,
            "The default outcome model shatters a body once the impact energy passes a fifth of its binding energy (plus Benz & Asphaug's 1999 strength term for small bodies) and leaves a largest remnant f = 0.5(2 − E/E_bind), clamped to 0.05–0.9: a rule of thumb, not a fitted law."),
        E("Legacy fragments", Game, Collisions,
            "Legacy splits the lost mass into 6–18 fragments whose masses fall off as k^−1.2, placed on a shell at 1.6 radii and flung out at a spread of speeds scaled to the escape speed."),
        E("LS12 + KG10 collision regimes", Sci, Collisions,
            "Opt-in (Legacy is the default): the Leinhardt & Stewart (2012) scaling laws choose merge, graze-and-merge, hit-and-run, partial accretion, erosion or super-catastrophic disruption, with the hit-and-run speed of Kokubo & Genda (2010)."),
        E("LS12 validity", Game, Collisions,
            "LS12 is gravity-only (no material strength, so far too weak below about 1 km) and was fitted to strengthless rubble-pile simulations, KG10 to fluid Earth-mass planets, so outcomes outside those suites are extrapolation."),
        E("Fragment power law", Sci, Collisions,
            "LS12 fragments below the largest remnant follow the cumulative size distribution N(>D) ∝ D^−β with the paper's fitted β = 2.85, normalised so the fragments carry exactly the escaping mass."),
        E("LS12 fragment cap", Game, Collisions,
            "One LS12 event makes at most 64 discrete fragments, the rest of the escaping mass goes to a particle tier, and debris under 1 % of the projectile's mass stays with its body."),
        E("Body cap", Game, Collisions,
            "At most 240 bodies exist at once, and a shatter that would pass the cap folds the extra fragments back into the remnant, which still conserves mass and momentum exactly."),
        E("Momentum-exact outcomes", Sci, Collisions,
            "Merges and shatters conserve total mass and linear momentum exactly, the remnant taking the fragments' recoil, and a merged body keeps the combined volume of the two."),
        E("Spin not tracked", Game, Collisions,
            "Collisions change orbits, not rotation: a struck or merged body keeps its catalogue spin, so the angular momentum of an off-centre hit is never turned into spin."),
        E("Star and gas-giant impacts", Game, Collisions,
            "A star simply absorbs whatever hits it, and a gas giant takes a dark scar and a plume sized from the fireball, in the manner of Shoemaker–Levy 9, instead of a crater."),
        E("Aftermath fade times", Game, Collisions,
            "Heat spots, dust veils and magma oceans fade on rule-of-thumb timescales (a crater glows for about D^1.5 hours with D in km, a dust veil lasts about a year × (E/10²¹ J)^0.3), not from a thermal or climate model."),

        // ---------- rendering ----------
        E(Realism.Diorama, Art, Rendering,
            "Optional: planets, moons and rocks are drawn up to 800× and the Sun 30× their true size, each capped at a quarter of the way to its nearest heavy neighbour, so the system reads at a glance while the physics keeps the true radii."),
        E("Legibility floor", Art, Rendering,
            "No body is drawn smaller than 3.5 px in radius, so a moon 40 AU away is still a visible dot; it is cosmetic only, and the camera's surface stop ignores it."),
        E("Auto-exposure", Art, Rendering,
            "Exposure follows the sunlight at the focused body (irradiance^−0.85, eased over a third of a second), so Neptune, with 1/900 of Earth's sunlight, is as readable as Mercury."),
        E(Realism.FalloffBalanced, Art, Rendering,
            "The default lights a body with 1/r^0.5 of the Sun's intensity (Flat: no falloff at all) so the outer planets are not near-black; only the Physical setting is the real inverse square."),
        E(Realism.FalloffPhysical, Sci, Rendering,
            "Irradiance falls as 1/r², the inverse-square law, which together with auto-exposure makes the outer planets dim but correctly lit."),
        E(Realism.Bloom, Art, Rendering,
            "The glow around bright things imitates light scattering in a camera lens or the eye, since space itself has no bloom, and ACES tone mapping squeezes the Sun's brightness into what a screen can show."),
        E("Depth squeeze", Game, Rendering,
            "Render-only: anything beyond 10⁵ × the near plane is pulled toward the camera along its own line of sight and shrunk by the same factor, so direction and apparent size stay exact while depth fits the GPU's range (spike M1c)."),
        E("Single-scattering atmosphere", Game, Rendering,
            "Skies and limbs come from one bounce of Rayleigh and Mie scattering with Chapman-function optical depths and 7 samples per ray, whereas real air scatters light many times over, which mostly brightens twilight."),
        E("Eclipse shadows", Sci, Rendering,
            "Up to four nearby bodies cast analytic shadows against the Sun's true angular radius, so eclipses have a real umbra and penumbra, and Saturn's rings and globe shadow each other."),
        E("Climate cue", Art, Rendering,
            "When a body's equilibrium temperature moves from its starting value, ice caps grow or deserts spread in proportion (full effect at ±45 K), a picture of the trend rather than a climate model."),
        E("Invented terrain", Art, Rendering,
            "Voyager 2 saw only the southern halves of the Uranian moons and about 40 % of Triton, and New Horizons one side of Charon, so their blank regions are filled by growing imaged terrain over them and that terrain is invented (public/textures/CREDITS.md)."),
        E("Borrowed and imagined maps", Art, Rendering,
            "Proteus, Nereid and Pallas were never mapped and wear Phobos', Deimos' and Vesta's maps tinted to their measured colours, and Eris, Haumea and Makemake are artists' impressions (public/textures/CREDITS.md)."),
        E("Procedural fallback maps", Art, Rendering,
            "Textures are never shipped with the code, so wherever a map file is missing the body gets a procedural rock, gas or ice pattern instead."),
        E("Crater decals", Game, Rendering,
            "Craters, rays and impact scars are painted into one equirectangular overlay per body (1024 or 2048 px wide, stretched by 1/cos latitude to stay round), much coarser than the surface map beneath."),
        E("Impact effect timings", Art, Rendering,
            "Flash, fireball (3–9 s), blast ring (4 s) and ejecta curtain play out in real seconds whatever the warp, with a minimum on-screen size, so every impact is watchable, where real plumes rise and fall over minutes to hours."),
        E("Milky Way orientation", Sci, Rendering,
            "The Milky Way panorama is placed by the galactic north pole and the galactic centre, so the band crosses the sky where it really lies."),
        E("Background stars", Art, Rendering,
            "The 9000 point stars are scattered at random with random blackbody colours between 3200 and 12 200 K; they are not a real star catalogue."),
        E("Sun from mass", Sci, Rendering,
            "The Sun's luminosity follows the main-sequence mass–luminosity relation L ∝ M^3.5 (a brown-dwarf branch below 0.08 M☉) and its temperature follows from luminosity and radius, so a heavier Sun burns hotter and bluer."),
        // The stars branch replaces this look; its "Star colour" entry retires this one when it registers.
        new("Sun surface", Art,
            "Granulation, cells and sunspots are noise patterns with limb darkening under a billboard corona: a look, not a model of the solar surface.",
            Rendering) { RetiredBy = "Star colour" },
        E("Orbit lines", Sci, Rendering,
            "Each orbit line is the osculating Kepler conic about the body's current dominant primary, recomputed from its state every frame, so it shows where pure two-body motion would take it from here."),

        // ---------- time ----------
        E("Time warp", Game, Time,
            "Time runs from real time up to 10⁸× (about three years a second), and the substep budget decides how fast it can actually go."),
        E("Rewind", Game, Time,
            "Rewind runs the same leapfrog backwards, which is exact for gravity because the integrator is time-reversible, but collisions do not un-happen: undo them with Ctrl+Z."),
        E("Undo", Game, Time,
            "Every action and every collision cascade (hits under 30 steps apart) records the whole state first, bodies, debris, discs and all, and Ctrl+Z restores it, which no real universe allows; about 50 points are kept within 300 MB of RAM and 256 MB of GPU memory, oldest dropped first, and an old point whose particle cloud was dropped for GPU memory brings back only the bodies."),
        E("Fault recovery", Game, Time,
            "If any position or velocity ever becomes non-finite, the simulation rolls back to the last undo point and pauses rather than drawing nonsense."),

        // ---------- camera ----------
        E(Realism.Cinematic, Game, Camera,
            "After an impact the camera flies to the site, stays locked on it as the body turns and holds the warp at 1× for 9 s, so the event plays out at a watchable speed."),
        E("Lens zoom", Game, Camera,
            "Past the surface stop, zooming narrows the field of view from 50° down to 6° instead of moving closer, like a telephoto lens."),
        E("Surface stop", Game, Camera,
            "The camera cannot come closer than 3 % above a body's drawn surface (25 % for a star), so it never ends up inside a planet."),

        // ---------- interface ----------
        E("Mass and radius sliders", Game, UI,
            "The inspector's sliders rewrite a body's mass or radius instantly, which no natural process can do; everything that happens afterwards is ordinary physics."),
        E("Auto-aim", Game, UI,
            "Clicking a body flies up to eight trial shots through a copy of the full N-body system to find the launch velocity, and the rock then flies real physics, so a miss is a real miss."),
        E("Impactor presets", Game, UI,
            "Preset sizes are published values (Chelyabinsk 19 m, Bennu 490 m, Chicxulub about 10 km, Theia roughly Mars-sized), but their speeds and the idea of Vesta or Ceres striking are what-if scenarios."),
        E("Impactor densities", Game, UI,
            "Composition is one of four bulk densities (rock 3000, iron 7800, carbonaceous 1500, ice 900 kg/m³), whereas real bodies vary in porosity and layering."),
        E("Label declutter", Game, UI,
            "Names that would overlap are hidden, ranked by selection, kind and on-screen size, so not every body is labelled at once."),
        E("Equilibrium temperature", Sci, UI,
            "The inspector's equilibrium temperature is the radiative balance for the body's Bond albedo at its present distance from the Sun at its present luminosity."),
        E("Surface temperature readout", Game, UI,
            "Surface temperature is the body's known mean shifted by the change in its equilibrium temperature since the start, not a greenhouse or climate model."),
    ];

    // =====================================================================================================================
    // PENDING — pre-written entries for branches that are not merged yet. Each shows only under the panel's "Pending"
    // filter until its branch registers it (same feature name) or calls Realism.ConfirmPending(<its branch constant>).
    // Owners:
    //   stars        worktree-agent-ab3b8d0000724ec9c  committed; verbatim copy of StarColor.RealismNotes
    //   debris       worktree-agent-a60cd64196e555786  committed; written from App.Debris.cs and DebrisTier.cs
    //   black holes  worktree-agent-aa68c19e8437eee68  still being coded; written from its worktree and research §3
    //   SPH          worktree-agent-a4df24d824ca1cb0c  still being coded; written from its worktree and research §1–2
    // Do not edit those branches' files from here; move an entry out of this block only when its branch merges.
    // =====================================================================================================================
    static RealismEntry P(string owner, string feature, RealismTag tag, RealismArea area, string why) =>
        new(feature, tag, why, area) { Owner = owner, Pending = true };

    const string Stars = Realism.StarsBranch, Debris = Realism.DebrisBranch, Holes = Realism.BlackHoleBranch, Sph = Realism.SphBranch;

    /// <summary>Entries of the unmerged branches, pending until they register.</summary>
    public static readonly RealismEntry[] Expected =
    [
        // ---------- stars: worktree-agent-ab3b8d0000724ec9c (StarColor.RealismNotes, word for word) ----------
        P(Stars, "Star colour", Sci, Rendering, "Planck spectrum integrated against the CIE 1931 2° observer, converted to sRGB (D65); the Sun is 5772 K."),
        P(Stars, "Sun looks white", Game, Rendering, "Brightness is exposure-compensated so the disc saturates to near-white after tone mapping, as the eye sees it, instead of following the auto-exposure."),
        P(Stars, "Star brightness", Game, Rendering, "Every star's disc is drawn with its brightest colour channel at the Sun's level, whatever its luminosity, so cool stars stay orange and hot ones blue rather than clipping to white."),
        P(Stars, "Close-up dimming", Game, Rendering, "Like a solar filter, the disc dims as it fills the view so granulation is not lost to overexposure."),
        P(Stars, "Limb darkening", Sci, Rendering, "Quadratic law per colour channel from a grey (Eddington) atmosphere: the edge is dimmer and redder, more so for cool stars."),
        P(Stars, "Granules", Sci, Rendering, "Cells about 1000 km across with bright hot centres and dark lanes, ~15 % contrast; only resolved when you fly close."),
        P(Stars, "Granule lifetime", Art, Rendering, "Real granules live about 8 minutes; compressed 20× so they visibly boil at normal speed, and capped at high warp so they never flicker."),
        P(Stars, "Supergranules", Sci, Rendering, "A network about 30000 km across that brightens toward the limb (faculae)."),
        P(Stars, "Supergranule contrast", Game, Rendering, "Raised well above the real sub-percent white-light contrast so the network is visible at all."),
        P(Stars, "Surface mottling", Art, Rendering, "The web build's fbm noise, kept under the physical cells so both builds look alike from afar."),
        P(Stars, "Sunspots", Game, Rendering, "A threshold on low-frequency noise, not magnetic flux, with a penumbra ring and a darker umbra."),
        P(Stars, "Spot temperature", Sci, Rendering, "Umbrae are ~1700 K cooler than the Sun's surface and only ~200 K cooler on red dwarfs (Berdyugina 2005), so spots redden and cool stars' spots are shallow."),
        P(Stars, "Spot count", Game, Rendering, "More spots on cool stars, none above ~7000 K (no deep convection zone, no dynamo), fewer on large stars."),
        P(Stars, "Surface map", Art, Rendering, "The texture map adds structure only; its false colour is discarded in favour of the Planck colour."),
        P(Stars, "Corona colour", Sci, Rendering, "The white-light corona is sunlight scattered by electrons, which is grey, so it takes the star's colour."),
        P(Stars, "Corona shape", Art, Rendering, "Rays and streaks are noise, not magnetic field lines, and the corona is far brighter than the real one, which only shows in an eclipse."),

        // ---------- debris: worktree-agent-a60cd64196e555786 ----------
        P(Debris, "Housen–Holsapple ejecta", Sci, Collisions,
            "Crater ejecta speeds and masses follow Housen & Holsapple (2011), v ∝ (x/a)^−1/μ (1 − x/n₂R)^p with μ = 0.55 for rock and 0.4 for porous targets, launched at 50° near the impact point falling to 40° at the rim."),
        P(Debris, "Ejecta mass from the transient crater", Game, Collisions,
            "The impact model gives no ejecta mass, so it is taken as Collins et al.'s transient crater volume πD³/(16√2) times the surface density, capped at 1 % of the target's mass."),
        P(Debris, "Debris as test particles", Game, Collisions,
            "Up to a million GPU debris particles feel the 64 heaviest bodies but not each other and pull on nothing (no back-reaction), and lighter bodies neither attract nor absorb them."),
        P(Debris, "Exact debris ledger", Sci, Collisions,
            "Every hand-off between bodies, particles and dust is booked in double-double arithmetic, so total mass, momentum and angular momentum are conserved to rounding rather than approximately."),
        P(Debris, "Dust re-accretion", Game, Collisions,
            "Particles older than about 30 days or farther than 2 million km become dust that rains back onto the body dominating where they went, with a 300 s time constant, instead of being tracked."),
        P(Debris, "LS12 particle-tier dispersal", Game, Collisions,
            "Escaping mass too small for a discrete fragment leaves as pairs of equal particles on opposite random directions from 1.3 remnant radii, their speeds carrying escape energy plus the excess of Q_R over binding with a ±40 % spread."),

        // ---------- black holes: worktree-agent-aa68c19e8437eee68 (still being coded) ----------
        P(Holes, "Black-hole lensing", Sci, Rendering,
            "Light bending around a Schwarzschild hole comes from Bruneton's (2020) precomputed deflection tables of the photon orbit equation d²u/dφ² = 3u²/2 − u rather than from ray marching."),
        P(Holes, "Several black holes", Art, Rendering,
            "Where more than one hole bends the same light, each bends it in turn, nearest first, which is an approximation because two holes together have no closed-form spacetime."),
        P(Holes, "Accretion disc temperature", Sci, Rendering,
            "The thin disc runs outward from the innermost stable orbit (3 r_s) with the Shakura–Sunyaev (1973) temperature profile and a brightness that goes as T⁴."),
        P(Holes, "Disc Doppler beaming", Sci, Rendering,
            "Gas on the approaching side is blueshifted and brightened by the circular-orbit factor g (colour × g, brightness × g⁴), which includes the gravitational redshift."),
        P(Holes, "Disc display temperature", Game, Rendering,
            "The temperature profile is rescaled to a visible peak, because a stellar-mass disc at about 10⁷ K would glow X-ray white."),
        P(Holes, "Disc turbulence", Art, Rendering,
            "Streaks in the disc are noise carried round at the Keplerian rate, not a simulated flow."),
        P(Holes, "Disc size", Art, Rendering,
            "The disc's outer edge at 20 r_s is chosen for the look; its brightness comes from what the hole is swallowing (Accretion-fed discs)."),

        // ---------- SPH: worktree-agent-a4df24d824ca1cb0c (still being coded) ----------
        P(Sph, "Hero SPH collisions", Game, Collisions,
            "Only a collision you are watching (the camera follows one of the bodies, or the cinematic camera flies to it) runs smoothed-particle hydrodynamics, at 3 × 10⁴ or 10⁵ particles, so it looks right but is qualitative: giant-impact results need 10⁷–10⁸ particles to converge (Kegerreis et al. 2019). It runs until the physics settles (kinetic energy down to 5 % of its value at contact, or 8 h of SPH time), judged at fixed substeps, so the outcome no longer depends on your frame rate; only a GPU too slow to get there in 2 minutes is cut short, with a notice."),
        P(Sph, "SPH remainder as debris", Game, Collisions,
            "Material in no clump becomes debris particles at its SPH positions and velocities, nudged by one velocity shift and one rigid rotation so it carries exactly the mass, momentum and angular momentum the bodies did not."),
        P(Sph, "Tillotson equation of state", Sci, Collisions,
            "Pressure follows from density and internal energy by the Tillotson (1962) equation of state, with granite, basalt, iron and ice parameters from Melosh (1989) and Benz & Asphaug (1999)."),
        P(Sph, "SPH artificial viscosity", Sci, Collisions,
            "Shocks are captured by Monaghan (1992) artificial viscosity with the Balsara (1995) switch, which turns it off in pure shear such as a spinning disc."),
        P(Sph, "SPH self-gravity", Sci, Collisions,
            "Particle self-gravity comes from a Barnes–Hut octree, an approximation whose error is set by its opening angle."),
        P(Sph, "SPH display temperature", Game, Collisions,
            "Glow colour comes from a display temperature derived from internal energy with handbook heat capacities and a melting plateau, because the Tillotson equation of state defines no temperature."),
        P(Sph, "SPH clump hand-off", Sci, Collisions,
            "Clumps that re-accumulate become ordinary bodies with the summed mass, the centre-of-mass velocity and a spin of ω = L/I, so mass, momentum and angular momentum carry over exactly."),
    ];
}
