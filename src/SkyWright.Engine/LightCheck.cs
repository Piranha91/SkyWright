using System.Numerics;

namespace SkyWright.Engine;

// THE LIGHTING CHECK (2026-11-26, Gate B; run folder C:/hc-runs/2026-11-26-lighting-check): lighting clashes between two sides of a load
// order found from the load order alone. The sides are the selection and the rest; four arrangements are read, each as itself: the
// dependencies (D), the selection over them (S), the rest over them (R), and the combination (C). A row is listed when its class reads
// positive in C and negative in D and in each side alone wherever the subject exists ("needs both"). Each scene (an interior cell, or an
// exterior cell with its ring of eight neighbours) is evaluated alone: an object in another interior cell never hosts a light.
// The per-arrangement readings are lane L's (2026-10-04) and lane A's fixture rule (2026-10-13, CLAIMS LIGHT FIXTURE HOST), moved here
// from lightclash.py, with the Gate A review's findings: a stranded light has no emitting fixture at all within H in the arrangement read
// (finding 1); an implausible radius is not judged (finding 5); the mods a row names are the ones whose records did it (finding 6).

/// <summary>One class reading in one arrangement: whether the class applies, its value (null: undecided), and what it saw.</summary>
public sealed record LightReading(bool Applies, bool? Positive)
{
    public string? Label { get; init; }
    public string? Host { get; init; }
    public string? HostHow { get; init; }
    public double? DistOwn { get; init; }
    public double? DistHere { get; init; }
    public string? Replacement { get; init; }
    public IReadOnlyList<string> Unknown { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> LitOwn { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> LitHere { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Lights { get; init; } = Array.Empty<string>();
    public IReadOnlyList<LightSide> Owners { get; init; } = Array.Empty<LightSide>();
}

/// <summary>Which placed object hosts a light in one arrangement (the structural fixture rule).</summary>
public sealed record LightHost(string? Key, string? How, double? Dist, string? Label, IReadOnlyList<string> Unknown);

public static class LightCheck
{
    public const string PolicyVersion = "light_check_v1|sides from the arms: the selection against the rest|four arrangements, each read as itself"
        + "|needs both: positive in the combination, negative in the dependencies and in each side alone|each scene alone"
        + "|host: own model, else the nearest emitting part within H of an enabled non-item reference, unreadable = not judged"
        + "|stranded: no emitting part of any enabled fixture within H in the arrangement read|behind a wall: back-face majority, 8 of 64 hits"
        + "|lights nothing: no hit and no lit area|lit twice: lights of both sides within H|over the limit: a count, never a clash"
        + "|radius over 16384 u not judged; exterior radius over 4096 u not judged";

    /// <summary>H: how near an emitting part must be to host a light (the deployed fixture rule).</summary>
    public const double HostRadius = 64;
    /// <summary>The back-face reading decides only when at least this many of the 64 rays hit a surface.</summary>
    public const int MinFacingHits = 8;
    public const int VanillaLimit = 7, VanillaShadowLimit = 4, StrictLimit = 7;
    /// <summary>A light radius past this is not a real light (two Dragonborn test lights read 844,822.9 u); it is not judged.</summary>
    public const float ImplausibleRadius = 16384f;
    /// <summary>An exterior scene reads the centre cell and its eight neighbours, so a light whose radius passes one cell is not judged.</summary>
    public const float ExteriorReach = 4096f;

    public const string LightNoFixture = "light-no-fixture", FixtureNoLight = "fixture-no-light", LightBehindWall = "light-behind-wall",
                        LightLightsNothing = "light-lights-nothing", FixtureLitTwice = "fixture-lit-twice", OverLightLimit = "over-light-limit";
    /// <summary>Every class, in the order rows and counts are written; each is written whether admitted or held.</summary>
    public static readonly IReadOnlyList<string> Classes = new[] { LightNoFixture, FixtureNoLight, LightBehindWall, LightLightsNothing, FixtureLitTwice, OverLightLimit };
    /// <summary>The words a modder reads for each class.</summary>
    public static readonly IReadOnlyDictionary<string, string> ClassTitles = new Dictionary<string, string>
    {
        [LightNoFixture] = "A light left with no fixture", [FixtureNoLight] = "A fixture left with no light", [LightBehindWall] = "A light behind a wall",
        [LightLightsNothing] = "A light that lights nothing", [FixtureLitTwice] = "A fixture lit twice", [OverLightLimit] = "Over the light limit (a count)",
    };

    /// <summary>Inventory items never host a light (a record type list, not a name test).</summary>
    public static readonly IReadOnlySet<string> InventoryItemTypes = new HashSet<string>
        { "Ingredient", "Ingestible", "SoulGem", "MiscItem", "Book", "Weapon", "Armor", "Ammunition", "Key", "Scroll" };

    public const string NeedsBoth = "needs both", NotThePair = "not the pair", NoClash = "no clash in the combination", NotJudged = "not judged";
    const string OwnModel = "own model";
    /// <summary>A reference counts as moved between its own arrangement and the combination past this distance (units).</summary>
    const double Moved = 0.5;

    // ---------------------------------------------------------------- the fixture rule

    /// <summary>Distance from a point to a placed box (0 inside), measured in the box's own frame; a flat box's out-of-span offset counts in full.</summary>
    public static double BoxDistance(Vector3 pos, Vector3 origin, Vector3 edgeX, Vector3 edgeY, Vector3 edgeZ)
    {
        double dx = (double)pos.X - origin.X, dy = (double)pos.Y - origin.Y, dz = (double)pos.Z - origin.Z;
        double sq = 0;
        var units = new List<(double X, double Y, double Z)>(3);
        foreach (var e in new[] { edgeX, edgeY, edgeZ })
        {
            double ex = e.X, ey = e.Y, ez = e.Z;
            double ln = Math.Sqrt(ex * ex + ey * ey + ez * ez);
            if (ln <= 0) continue;
            var u = (ex / ln, ey / ln, ez / ln);
            units.Add(u);
            double t = dx * u.Item1 + dy * u.Item2 + dz * u.Item3;
            double off = t < 0 ? -t : t > ln ? t - ln : 0;
            sq += off * off;
        }
        if (units.Count < 3)
        {
            double rx = dx, ry = dy, rz = dz;
            foreach (var u in units)
            {
                double k = dx * u.X + dy * u.Y + dz * u.Z;
                rx -= k * u.X; ry -= k * u.Y; rz -= k * u.Z;
            }
            sq += rx * rx + ry * ry + rz * rz;
        }
        return Math.Sqrt(sq);
    }

    static IEnumerable<LightCheckEmitter> Parts(LightCheckRef r, double emit) =>
        (r.Emitters ?? Array.Empty<LightCheckEmitter>()).Where(e => e.Kind != "emissive" || (e.Value ?? 0) >= emit);

    static (double D, LightCheckEmitter Part)? NearestPart(Vector3 pos, LightCheckRef r, double emit)
    {
        (double, LightCheckEmitter)? best = null;
        foreach (var e in Parts(r, emit))
        {
            double d = BoxDistance(pos, e.Origin, e.EdgeX, e.EdgeY, e.EdgeZ);
            if (best is null || d < best.Value.Item1) best = (d, e);
        }
        return best;
    }

    /// <summary>Distance from a point to the reference's nearest emitting part (null when it has none).</summary>
    public static double? PartDistance(Vector3 pos, LightCheckRef r, double emit = LightReach.EmitThreshold) => NearestPart(pos, r, emit)?.D;

    /// <summary>A fixture: an enabled reference, not an inventory item and not a light, with an emitting part.</summary>
    public static bool IsEmitting(LightCheckRef r, double emit = LightReach.EmitThreshold) =>
        r.On && !InventoryItemTypes.Contains(r.Type) && r.Type != "Light" && Parts(r, emit).Any();

    static double Reach(LightCheckRef r)
    {
        var ob = r.Obnd ?? Vector3.Zero;
        double s = r.Scale == 0 ? 1.0 : r.Scale;
        return Math.Sqrt((double)ob.X * ob.X + (double)ob.Y * ob.Y + (double)ob.Z * ob.Z) / 2 * s;
    }

    static double Dist(Vector3 a, Vector3 b)
    {
        double x = (double)a.X - b.X, y = (double)a.Y - b.Y, z = (double)a.Z - b.Z;
        return Math.Sqrt(x * x + y * y + z * z);
    }

    /// <summary>Fixtures whose nearest emitting part is within h of pos, nearest first (ties by key).</summary>
    static List<(double D, string Key)> EmittingNear(LightCheckReading a, Vector3 pos, double h, double emit, string? skip)
    {
        var o = new List<(double, string)>();
        foreach (var r in a.Refs.Values)
        {
            if (r.Key == skip || !IsEmitting(r, emit)) continue;
            if (PartDistance(pos, r, emit) is double d && d <= h) o.Add((d, r.Key));
        }
        return o.OrderBy(x => x.Item1).ThenBy(x => x.Item2, StringComparer.Ordinal).ToList();
    }

    /// <summary>Enabled non-item, non-light references whose emitting parts could not be read and whose bounds reach within h of pos.</summary>
    static List<string> UnknownNear(LightCheckReading a, Vector3 pos, double h, string skip)
    {
        var o = new List<string>();
        foreach (var r in a.Refs.Values)
        {
            if (r.Key == skip || !r.On || string.IsNullOrEmpty(r.EmitterStatus) || InventoryItemTypes.Contains(r.Type) || r.Type == "Light") continue;
            if (Dist(pos, r.Pos) <= h + Reach(r)) o.Add(r.Key);
        }
        o.Sort(StringComparer.Ordinal);
        return o;
    }

    /// <summary>Which placed object hosts this light in this arrangement: its own model; else the enabled fixture whose nearest emitting
    /// part is within h (nearest first); else unknown when an unreadable model reaches within h; else free-standing.</summary>
    public static LightHost HostOf(string light, LightCheckReading a, double h = HostRadius, double emit = LightReach.EmitThreshold)
    {
        var lref = a.Refs.GetValueOrDefault(light);
        if (lref is { HasModel: true }) return new(light, OwnModel, 0, OwnModel, Array.Empty<string>());
        var pos = a.Lights.GetValueOrDefault(light)?.Pos ?? lref?.Pos ?? throw new ArgumentException($"light {light} is not in this arrangement");
        (double D, string Key, LightCheckEmitter Part)? best = null;
        foreach (var r in a.Refs.Values)
        {
            if (r.Key == light || !IsEmitting(r, emit)) continue;
            if (NearestPart(pos, r, emit) is not { } n || n.D > h) continue;
            if (best is null || n.D < best.Value.D || (n.D == best.Value.D && string.CompareOrdinal(r.Key, best.Value.Key) < 0)) best = (n.D, r.Key, n.Part);
        }
        if (best is { } b) return new(b.Key, b.Part.Kind, b.D, null, Array.Empty<string>());
        var unknown = UnknownNear(a, pos, h, light);
        if (unknown.Count > 0) return new(null, null, null, "host unknown (model unreadable): " + string.Join(", ", unknown), unknown);
        return new(null, null, null, "free-standing (no emitting part within H)", Array.Empty<string>());
    }

    // ---------------------------------------------------------------- the per-arrangement readings

    /// <summary>A light left with no fixture, read in arrangement <paramref name="a"/>: the light's own arrangement hosts it; in a the host is
    /// gone, disabled or its emitting parts beyond H, and no emitting part of any enabled fixture is within H of the light either.</summary>
    public static LightReading Stranded(string light, LightCheckReading own, LightCheckReading a, LightCheckOptions? options = null)
    {
        var o = options ?? new();
        var lOwn = own.Lights.GetValueOrDefault(light);
        if (lOwn is null || !lOwn.Enabled) return new(false, false) { Label = "not lit in its own arrangement" };
        var hs = HostOf(light, own, o.HostRadius, o.EmitThreshold);
        if (hs.How == OwnModel) return new(false, false) { Host = light, HostHow = OwnModel, Label = "hosted by its own model" };
        if (hs.Key is null)
            return hs.Unknown.Count > 0 ? new(true, null) { Label = hs.Label, Unknown = hs.Unknown } : new(false, false) { Label = hs.Label };
        var basis = new LightReading(true, false) { Host = hs.Key, HostHow = hs.How, DistOwn = hs.Dist };
        var lA = a.Lights.GetValueOrDefault(light);
        if (lA is null || !lA.Enabled) return basis with { Label = "not lit in this arrangement" };
        var h = a.Refs.GetValueOrDefault(hs.Key);
        string label;
        double? dHere = null;
        if (h is not null && h.On)
        {
            var d = PartDistance(lA.Pos, h, o.EmitThreshold);
            if (d is null) return basis with { Positive = null, Label = "host present, its emitting parts not read: " + (h.EmitterStatus ?? "no emitting part") };
            if (d <= o.HostRadius) return basis with { DistHere = d };
            dHere = d; label = $"host {d:0} u away";
        }
        else label = h is null ? "host gone" : "host removed or disabled";
        // finding 1: a light still at a burning object (another mod's candle in place of the base one) is not stranded
        var other = EmittingNear(a, lA.Pos, o.HostRadius, o.EmitThreshold, light);
        if (other.Count > 0) return basis with { DistHere = dHere, Replacement = other[0].Key, Label = label + "; another fixture within H: " + other[0].Key };
        var unknown = UnknownNear(a, lA.Pos, o.HostRadius, light);
        if (unknown.Count > 0) return basis with { Positive = null, DistHere = dHere, Unknown = unknown, Label = label + "; a model within H could not be read" };
        return basis with { Positive = true, DistHere = dHere, Label = label };
    }

    /// <summary>A fixture left with no light, read in arrangement a: its own arrangement lights it (an enabled light within H of its emitting
    /// parts); in a no enabled light is.</summary>
    public static LightReading Unlit(string fixture, LightCheckReading own, LightCheckReading a, LightCheckOptions? options = null)
    {
        var o = options ?? new();
        var fOwn = own.Refs.GetValueOrDefault(fixture);
        if (fOwn is null || !IsEmitting(fOwn, o.EmitThreshold)) return new(false, false);
        var litOwn = own.Lights.Values.Where(l => l.Enabled && PartDistance(l.Pos, fOwn, o.EmitThreshold) <= o.HostRadius).Select(l => l.Key)
                        .OrderBy(k => k, StringComparer.Ordinal).ToList();
        if (litOwn.Count == 0) return new(false, false) { Label = "not lit in its own arrangement" };
        var fA = a.Refs.GetValueOrDefault(fixture);
        if (fA is null || !fA.On) return new(false, false) { Label = "fixture absent in this arrangement", LitOwn = litOwn };
        if (PartDistance(fA.Pos, fA, o.EmitThreshold) is null)
            return new(true, null) { Label = "the fixture's emitting parts not read: " + (fA.EmitterStatus ?? "no emitting part"), LitOwn = litOwn };
        var litA = a.Lights.Values.Where(l => l.Enabled && PartDistance(l.Pos, fA, o.EmitThreshold) <= o.HostRadius).Select(l => l.Key)
                      .OrderBy(k => k, StringComparer.Ordinal).ToList();
        return new(true, litA.Count == 0) { LitOwn = litOwn, LitHere = litA };
    }

    /// <summary>The side a light belongs to: its defining plugin's, else the side of the plugin that places it.</summary>
    public static LightSide SideOfLight(string key, LightCheckReading a, LightCheckSides sides)
    {
        var s = sides.Of(PluginOf(key));
        return s != LightSide.Base ? s : sides.Of(a.Refs.GetValueOrDefault(key)?.Responsible);
    }

    /// <summary>A fixture lit twice, read in arrangement a: enabled lights of both sides within H of one fixture's emitting parts.</summary>
    public static LightReading Doubled(string fixture, LightCheckReading a, LightCheckSides sides, LightCheckOptions? options = null)
    {
        var o = options ?? new();
        var f = a.Refs.GetValueOrDefault(fixture);
        if (f is null || !IsEmitting(f, o.EmitThreshold)) return new(false, null);
        var lights = a.Lights.Values.Where(l => l.Enabled && PartDistance(l.Pos, f, o.EmitThreshold) <= o.HostRadius).Select(l => l.Key)
                        .OrderBy(k => k, StringComparer.Ordinal).ToList();
        var owners = lights.Select(k => SideOfLight(k, a, sides)).Where(s => s != LightSide.Base).Distinct().OrderBy(s => s).ToList();
        return new(true, owners.Count >= 2) { Lights = lights, Owners = owners };
    }

    /// <summary>Behind a wall, raw: most first hits are back faces (from 8 of 64 hits); false for a light that lights nothing; null when
    /// undecided (too few hits, or a model in reach unread) or not lit.</summary>
    public static bool? BehindWall(LightCheckLight? l)
    {
        if (l is null || !l.Enabled || l.Facing is not { } f) return null;
        if (f.Front + f.Back < MinFacingHits) return LightsNothing(l) ? false : null;
        if (f.Back > f.Front) return true;
        if (l.Incomplete.Count > 0) return null;
        return false;
    }

    /// <summary>Lights nothing, raw: no ray hits a surface and the leak sampler finds no lit area.</summary>
    public static bool LightsNothing(LightCheckLight? l) => l?.Facing is { } f && f.Front + f.Back == 0 && l.Lit + l.Leak == 0;

    /// <summary>Over the light limit, raw: the vanilla renderer's (more than 7 lights, or more than 4 shadow lights, on one shape), or Light
    /// Limit Fix's when it is installed (more than 7 portal-strict lights).</summary>
    public static bool OverLimit(LightCheckShape s, bool llf) =>
        llf ? s.Strict > StrictLimit : s.Count > VanillaLimit || s.Shadow > VanillaShadowLimit;

    /// <summary>Needs both: positive in the combination and in no single arrangement where the subject exists.</summary>
    public static string Attribute(bool? combined, IEnumerable<bool?> singles)
    {
        if (combined is null) return NotJudged;
        if (combined == false) return NoClash;
        if (singles.Any(v => v == true)) return NotThePair;
        return NeedsBoth;
    }

    public static string PluginOf(string key) => key.IndexOf(':') is int i and >= 0 ? key[(i + 1)..] : "";

    /// <summary>The plugin whose record changed a reference in the combination: the last plugin carrying a record of it (or of its enable
    /// parent) that the subject's own arrangement does not load. Null when no such record exists.</summary>
    public static string? ChangedBy(LightCheckRef? x, LightCheckReading own)
    {
        if (x is null) return null;
        var who = x.Touchers.LastOrDefault(p => !own.Plugins.Contains(p));
        return who ?? x.ParentTouchers.LastOrDefault(p => !own.Plugins.Contains(p));
    }

    // ---------------------------------------------------------------- one scene

    /// <summary>Every class over one scene's four readings. Rows are the listed ones ("listed"; "cannot tell" for the over-limit count) and
    /// the not-judged ones, each with its sentence; counts are per class.</summary>
    public static LightCheckSceneResult Evaluate(string scene, bool exterior, LightCheckReading dependencies, LightCheckReading selection,
                                                 LightCheckReading rest, LightCheckReading combined, LightCheckSides sides, LightCheckOptions? options = null)
    {
        var o = options ?? new();
        var d = dependencies; var s = selection; var r = rest; var c = combined;
        var singles = new[] { d, s, r };
        var rows = new List<LightCheckRow>();
        var counts = Classes.ToDictionary(k => k, _ => new LightCheckCount());
        LightCheckReading Own(string key) => sides.Of(PluginOf(key)) switch { LightSide.Selection => s, LightSide.Rest => r, _ => d };
        LightCheckMod Mod(string plugin) => new(sides.ModOf(plugin), plugin);
        LightCheckMod? ModOrNull(string? plugin) => plugin is null ? null : Mod(plugin);
        string Name(LightCheckMod? m) => m?.Mod ?? "another mod";
        void Tally(string cls, string att)
        {
            var k = counts[cls];
            switch (att) { case NeedsBoth: k.Listed++; break; case NotThePair: k.NotThePair++; break; case NoClash: k.NoClash++; break; default: k.NotJudged++; break; }
        }
        bool Subject<T>(string key, Func<LightCheckReading, T?> get, Func<T, bool> subject) where T : class
        {
            if (get(c) is { } x) return subject(x);
            foreach (var a in singles) if (get(a) is { } y) return subject(y);
            return true;
        }
        string? RadiusRefusal(LightCheckLight l, string who)
        {
            if (l.Radius > ImplausibleRadius)
                return $"Not judged: {who} has a radius of {l.Radius:N0} units, far past any real light (the check stops at {ImplausibleRadius:N0}); check the light's radius in xEdit.";
            if (exterior && l.Radius > ExteriorReach)
                return $"Not judged: {who} reaches {l.Radius:N0} units, past the neighbouring cells this check reads ({ExteriorReach:N0}); look at it in game.";
            return null;
        }
        string Who(string key, string? edid) => $"{Name(Mod(PluginOf(key)))}'s light {edid ?? key}";
        string? gaps = c.SurfaceGaps.Concat(singles.SelectMany(a => a.SurfaceGaps)).FirstOrDefault();

        // ---- lights
        var lightKeys = c.Lights.Keys.Concat(singles.SelectMany(a => a.Lights.Keys)).Distinct().OrderBy(k => k, StringComparer.Ordinal).ToList();
        foreach (var k in lightKeys)
        {
            if (!Subject(k, a => a.Lights.GetValueOrDefault(k), l => l.Subject)) continue;
            var own = Own(k);
            var lo = own.Lights.GetValueOrDefault(k); var lc = c.Lights.GetValueOrDefault(k);
            var any = lc ?? lo ?? singles.Select(a => a.Lights.GetValueOrDefault(k)).First(x => x is not null)!;
            var who = Who(k, any.Edid);
            var lightMod = Mod(PluginOf(k));
            var refusal = RadiusRefusal(any, who);
            var at = (lc ?? any).Pos;

            // a light left with no fixture
            if (lo is { Enabled: true } && lc is not null)
            {
                var rc = Stranded(k, own, c, o);
                if (rc.Applies)
                {
                    counts[LightNoFixture].Subjects++;
                    string att = refusal is not null ? NotJudged
                        : Attribute(rc.Positive, singles.Where(a => a != own && a.Lights.ContainsKey(k)).Select(a => Stranded(k, own, a, o).Positive));
                    Tally(LightNoFixture, att);
                    if (att is NeedsBoth or NotJudged)
                    {
                        var hostOwn = rc.Host is null ? null : own.Refs.GetValueOrDefault(rc.Host);
                        var hostC = rc.Host is null ? null : c.Refs.GetValueOrDefault(rc.Host);
                        string fx = hostOwn?.Edid ?? rc.Host ?? "a fixture";
                        // Gate B review finding 2: when the host still stands but beyond H, the sentence names what moved, by which
                        // reference the other side's record changed (the fixture's, else the light's); with no such record it names no mover
                        LightCheckMod? other;
                        string? moved = null;
                        string listed, clause;
                        if (hostC is null || !hostC.On)
                        {
                            other = ModOrNull(ChangedBy(hostC, own));
                            string what = hostC is null || hostC.Deleted ? "removes" : "disables";
                            listed = $"{who} sits at {fx}, which {Name(other)} {what}; the light now glows where nothing burns.";
                            clause = $"{who} sat at {fx}, which {Name(other)} {what}";
                        }
                        else
                        {
                            var lightC = c.Refs.GetValueOrDefault(k);
                            var fixMover = hostOwn is not null && Dist(hostOwn.Pos, hostC.Pos) > Moved ? ChangedBy(hostC, own) : null;
                            var lightMover = lightC is not null && Dist(lo.Pos, lc.Pos) > Moved ? ChangedBy(lightC, own) : null;
                            if (fixMover is not null)
                            {
                                other = Mod(fixMover); moved = "fixture";
                                listed = $"{who} sits at {fx}, which {other.Mod} moves {rc.DistHere:N0} units away; the light now glows where nothing burns.";
                                clause = $"{who} sat at {fx}, which {other.Mod} moves {rc.DistHere:N0} units away";
                            }
                            else if (lightMover is not null)
                            {
                                other = Mod(lightMover); moved = "light";
                                listed = $"{who} sits at {fx} in its own setup, and {other.Mod} moves the light {rc.DistHere:N0} units away from it; the light now glows where nothing burns.";
                                clause = $"{who} sat at {fx} in its own setup, and {other.Mod} moves the light {rc.DistHere:N0} units away from it";
                            }
                            else
                            {
                                other = null;
                                listed = $"{who} sits at {fx} in its own setup, but with both sides installed the two stand {rc.DistHere:N0} units apart and no record of either says why; the light now glows where nothing burns.";
                                clause = $"{who} sat at {fx} in its own setup, and with both sides installed the two stand {rc.DistHere:N0} units apart";
                            }
                        }
                        // finding 4: what was measured of the light's reach, and no more (never "it still lights the room")
                        int? lhits = lc.Facing is { } lf ? lf.Front + lf.Back : null;
                        if (lhits is int nh)
                            listed = listed.TrimEnd('.') + $"; {nh} of {lc.Facing!.Rays} checks from the light hit a surface" + (nh < MinFacingHits ? "; it reaches very little." : ".");
                        string summary = att == NotJudged
                            ? refusal ?? (rc.Unknown.Count > 0
                                ? $"Not judged: a model within {o.HostRadius:0} units of {who} could not be read ({string.Join(", ", rc.Unknown)}), so whether it sits at a fixture is unknown; check that the mesh is installed and readable."
                                : $"Not judged: the burning parts of {fx} could not be read with both sides installed ({rc.Label}); check that its mesh is installed and readable.")
                            : listed;
                        rows.Add(new LightCheckRow(LightNoFixture, att == NeedsBoth ? "listed" : "not judged", scene, k)
                        {
                            SubjectEdid = any.Edid, Fixture = rc.Host, FixtureEdid = hostOwn?.Edid, SubjectMod = lightMod,
                            OtherMods = other is null ? Array.Empty<LightCheckMod>() : new[] { other }, Summary = summary,
                            Reason = att == NotJudged ? summary : rc.Label, At = at, Shadow = any.Shadow,
                            FixtureAt = hostOwn is not null && (hostC is null || !hostC.On || Dist(hostOwn.Pos, hostC.Pos) > Moved) ? hostOwn.Pos : null,
                            Readings = new Dictionary<string, object?> { ["attribution"] = att, ["label"] = rc.Label, ["hostKind"] = rc.HostHow,
                                ["distanceOwn"] = rc.DistOwn, ["distanceCombined"] = rc.DistHere, ["unknown"] = rc.Unknown, ["moved"] = moved,
                                ["hits"] = lhits, ["fixtureClause"] = att == NeedsBoth ? clause : null },
                        });
                    }
                }
            }
            if (lc is null || !lc.Enabled) continue;

            // a light behind a wall; a light that lights nothing
            counts[LightBehindWall].Subjects++;
            counts[LightLightsNothing].Subjects++;
            string? lightRefusal = refusal ?? (gaps is null ? null : $"Not judged: the ground around {who} could not be read ({gaps}); check the cell's landscape in xEdit.");
            var fu = BehindWall(lc);
            string attW = lightRefusal is not null ? NotJudged : Attribute(fu, singles.Where(a => a.Lights.ContainsKey(k)).Select(a => BehindWall(a.Lights[k])));
            Tally(LightBehindWall, attW);
            var f = lc.Facing;
            int hits = f is null ? 0 : f.Front + f.Back;
            var facing = f is null ? null : new Dictionary<string, int> { ["rays"] = f.Rays, ["front"] = f.Front, ["back"] = f.Back, ["none"] = f.None };
            if (attW is NeedsBoth or NotJudged)
            {
                LightCheckMod? other = null;
                foreach (var (owner, n) in lc.BackByRef.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal))
                    if (ChangedBy(c.Refs.GetValueOrDefault(owner), own) is { } p) { other = Mod(p); break; }
                other ??= ModOrNull(ChangedBy(c.Refs.GetValueOrDefault(k), own));
                string summary = attW == NotJudged
                    ? lightRefusal ?? (f is null ? $"Not judged: {who} was not measured; run the check with its geometry reading."
                        : hits < MinFacingHits ? $"Not judged: too few surfaces around {who} to tell which side of a wall it is on ({hits} of {f.Rays} checks hit something); look at it in game."
                        : $"Not judged: a model near {who} could not be read ({string.Join(", ", lc.Incomplete)}); check that its mesh is installed and readable.")
                    : $"{who} now sits behind {Name(other)}'s wall: most of what it reaches is the back of a surface ({f!.Back} of {hits} hits)."
                      + (lc.Shadow ? " It casts shadows, which may hide it." : "");
                rows.Add(new LightCheckRow(LightBehindWall, attW == NeedsBoth ? "listed" : "not judged", scene, k)
                {
                    SubjectEdid = lc.Edid, SubjectMod = lightMod, OtherMods = other is null ? Array.Empty<LightCheckMod>() : new[] { other },
                    Summary = summary, Reason = attW == NotJudged ? summary : null, At = lc.Pos, Shadow = lc.Shadow,
                    Readings = new Dictionary<string, object?> { ["attribution"] = attW, ["facing"] = facing, ["incomplete"] = lc.Incomplete },
                });
            }
            var vu = LightsNothing(lc);
            string attV = lightRefusal is not null ? NotJudged
                : Attribute(vu, singles.Where(a => a.Lights.GetValueOrDefault(k) is { Enabled: true }).Select(a => (bool?)LightsNothing(a.Lights[k])));
            Tally(LightLightsNothing, attV);
            if (attV is NeedsBoth)   // a not-judged reading here comes only from the refusal above, whose row the wall class carries
            {
                LightCheckMod? other = null;
                var lOwnRead = lo;
                if (lOwnRead is not null)
                    foreach (var (owner, _) in lOwnRead.LitByRef.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal))
                        if (ChangedBy(c.Refs.GetValueOrDefault(owner), own) is { } p) { other = Mod(p); break; }
                other ??= ModOrNull(ChangedBy(c.Refs.GetValueOrDefault(k), own));
                string summary = $"{who} lights nothing with {Name(other)} installed: no surface is in its reach. Harmless to look at; safe to disable.";
                var nothingReadings = new Dictionary<string, object?> { ["attribution"] = attV, ["facing"] = facing, ["lit"] = lc.Lit, ["leak"] = lc.Leak };
                // finding 4: the same light listed as left with no fixture: its stranded row is marked, counted, and this row carries the
                // fixture fact, so the app can list the light once (the registry's class entry says whether it does)
                int ix = rows.FindLastIndex(x => x.Class == LightNoFixture && x.Subject == k && x.Verdict == "listed");
                LightCheckRow? strandedRow = ix >= 0 ? rows[ix] : null;
                if (strandedRow is not null)
                {
                    rows[ix] = strandedRow with { Readings = new Dictionary<string, object?>(strandedRow.Readings) { ["alsoLightsNothing"] = true } };
                    counts[LightNoFixture].AlsoLightsNothing++;
                    nothingReadings["strandedSummary"] = $"{strandedRow.Readings.GetValueOrDefault("fixtureClause")}; with {Name(other)} installed it lights nothing: no surface is in its reach. Harmless to look at; safe to disable.";
                    nothingReadings["strandedOtherMods"] = strandedRow.OtherMods;
                }
                rows.Add(new LightCheckRow(LightLightsNothing, "listed", scene, k)
                {
                    SubjectEdid = lc.Edid, SubjectMod = lightMod, OtherMods = other is null ? Array.Empty<LightCheckMod>() : new[] { other },
                    Summary = summary, At = lc.Pos, Shadow = lc.Shadow,
                    Fixture = strandedRow?.Fixture, FixtureEdid = strandedRow?.FixtureEdid, FixtureAt = strandedRow?.FixtureAt,
                    Readings = nothingReadings,
                });
            }
        }

        // ---- fixtures
        var fixtureKeys = c.Refs.Values.Where(x => IsEmitting(x, o.EmitThreshold)).Select(x => x.Key)
            .Concat(singles.SelectMany(a => a.Refs.Values.Where(x => IsEmitting(x, o.EmitThreshold)).Select(x => x.Key)))
            .Distinct().OrderBy(k => k, StringComparer.Ordinal).ToList();
        foreach (var fk in fixtureKeys)
        {
            if (!Subject(fk, a => a.Refs.GetValueOrDefault(fk), x => x.Subject)) continue;
            var own = Own(fk);
            var fo = own.Refs.GetValueOrDefault(fk);
            var fc = c.Refs.GetValueOrDefault(fk);
            var anyF = fc ?? fo ?? singles.Select(a => a.Refs.GetValueOrDefault(fk)).First(x => x is not null)!;
            var fixtureMod = Mod(PluginOf(fk));
            string fname = $"{Name(fixtureMod)}'s {anyF.Edid ?? fk}";

            // a fixture left with no light
            var b = fc is not null ? Unlit(fk, own, c, o) : new LightReading(false, false);
            if (b.Applies)
            {
                counts[FixtureNoLight].Subjects++;
                string att = Attribute(b.Positive, singles.Where(a => a != own).Select(a => a.Refs.ContainsKey(fk) ? Unlit(fk, own, a, o).Positive : null));
                Tally(FixtureNoLight, att);
                if (att is NeedsBoth or NotJudged)
                {
                    LightCheckMod? other = null;
                    foreach (var lk in b.LitOwn)
                        if (ChangedBy(c.Refs.GetValueOrDefault(lk), own) is { } p) { other = Mod(p); break; }
                    var lightMods = b.LitOwn.Select(lk => Name(Mod(PluginOf(lk)))).Distinct().ToList();
                    string summary = att == NotJudged
                        ? $"Not judged: the burning parts of {fname} could not be read with both sides installed ({b.Label}); check that its mesh is installed and readable."
                        : $"{fname} is lit by {string.Join(" and ", lightMods)}'s light in its own setup, and {Name(other)} disables, removes or moves that light; it now burns without lighting anything.";
                    rows.Add(new LightCheckRow(FixtureNoLight, att == NeedsBoth ? "listed" : "not judged", scene, fk)
                    {
                        SubjectEdid = anyF.Edid, Fixture = fk, FixtureEdid = anyF.Edid, SubjectMod = fixtureMod,
                        OtherMods = other is null ? Array.Empty<LightCheckMod>() : new[] { other }, Summary = summary, Reason = att == NotJudged ? summary : null,
                        At = (fc ?? anyF).Pos, Readings = new Dictionary<string, object?> { ["attribution"] = att, ["litOwn"] = b.LitOwn },
                    });
                }
            }
            // a fixture lit twice
            if (fc is not null && IsEmitting(fc, o.EmitThreshold))
            {
                counts[FixtureLitTwice].Subjects++;
                var d3 = Doubled(fk, c, sides, o);
                string att = Attribute(d3.Positive, singles.Select(a => a.Refs.ContainsKey(fk) ? Doubled(fk, a, sides, o).Positive : null));
                Tally(FixtureLitTwice, att);
                if (att == NeedsBoth)
                {
                    var bySide = new List<LightCheckMod>();
                    foreach (var side in new[] { LightSide.Selection, LightSide.Rest })
                    {
                        var lk = d3.Lights.FirstOrDefault(x => SideOfLight(x, c, sides) == side);
                        if (lk is null) continue;
                        var pl = sides.Of(PluginOf(lk)) == side ? PluginOf(lk) : c.Refs.GetValueOrDefault(lk)?.Responsible ?? PluginOf(lk);
                        bySide.Add(Mod(pl));
                    }
                    rows.Add(new LightCheckRow(FixtureLitTwice, "listed", scene, fk)
                    {
                        SubjectEdid = fc.Edid, Fixture = fk, FixtureEdid = fc.Edid, SubjectMod = fixtureMod, OtherMods = bySide,
                        Summary = $"{fname} is lit by a light from both {string.Join(" and ", bySide.Select(m => m.Mod))}; it may look twice as bright.",
                        At = fc.Pos, Readings = new Dictionary<string, object?> { ["attribution"] = att, ["lights"] = d3.Lights },
                    });
                }
            }
        }

        // ---- lit shapes: over the light limit (a count, never a clash)
        foreach (var (sk, sh) in c.Shapes.OrderBy(kv => kv.Key.Ref, StringComparer.Ordinal).ThenBy(kv => kv.Key.Index))
        {
            if (!sh.Subject) continue;
            counts[OverLightLimit].Subjects++;
            bool llf = c.LlfInstalled;
            string att = Attribute(OverLimit(sh, llf), singles.Select(a => a.Shapes.TryGetValue(sk, out var x) ? OverLimit(x, llf) : (bool?)null));
            Tally(OverLightLimit, att);
            if (att != NeedsBoth) continue;
            var shapeMod = Mod(PluginOf(sh.Ref));
            var lightMods = sh.Lights.Select(lk => Mod(PluginOf(lk))).DistinctBy(m => m.Plugin).ToList();
            var owner = c.Refs.GetValueOrDefault(sh.Ref);
            string limit = llf ? "the game shows at most 7 portal-strict lights on one surface with Light Limit Fix" : "the game shows at most 7, or 4 with shadows, on one surface";
            rows.Add(new LightCheckRow(OverLightLimit, "cannot tell", scene, sh.Ref)
            {
                SubjectEdid = owner?.Edid, ShapeIndex = sh.Index, ShapeName = sh.Name, SubjectMod = shapeMod, OtherMods = lightMods,
                Summary = $"Cannot tell: {sh.Count} lights from {string.Join(" and ", lightMods.Select(m => m.Mod).Distinct())} reach {Name(shapeMod)}'s {sh.Name ?? "shape " + sh.Index} ({limit}). Whether this shows in game is not yet known.",
                At = owner?.Pos ?? Vector3.Zero,
                Readings = new Dictionary<string, object?> { ["attribution"] = att, ["count"] = sh.Count, ["shadow"] = sh.Shadow, ["strict"] = sh.Strict,
                    ["reading"] = llf ? "Light Limit Fix" : "vanilla", ["lights"] = sh.Lights,
                    ["countSingles"] = new Dictionary<string, int?> { ["dependencies"] = d.Shapes.GetValueOrDefault(sk)?.Count, ["selection"] = s.Shapes.GetValueOrDefault(sk)?.Count, ["rest"] = r.Shapes.GetValueOrDefault(sk)?.Count } },
            });
        }
        return new LightCheckSceneResult(scene, rows, counts);
    }
}
