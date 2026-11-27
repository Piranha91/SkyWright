using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SkyWright.Engine;

// WHAT A LOST-EDIT CHECK COMPARES, AND WHEN TWO VALUES ARE THE SAME IN GAME: the one owner in SkyWright (record checks
// Gate A, section 4). The data is data/record-field-scope.json, SkyWright's own file, built into this assembly so the
// scope a scan uses is the one the build was made with; the rules over it are here: which fields are left out (file,
// struct and group bookkeeping, runtime-merged fields and records, generated bookkeeping, child records), record flags
// compared without their storage bits, integer fields compared under their mask, numbers at four decimals with -0 equal
// to 0, and which lists the game reads in order. Ported from the module's evalrecords (FieldScope, same_value,
// ORDER_MATTERS), which go at Gate D.

public sealed class RecordFieldScope
{
    /// <summary>Child-record groups: another record's business, never a field of this one.</summary>
    static readonly HashSet<string> ChildGroups = new(StringComparer.Ordinal)
    { "Persistent", "Temporary", "NavigationMeshes", "Landscape", "SubCells", "TopCell", "Responses", "LargeReferences" };

    /// <summary>Lists whose order the game reads or may read, by list name on any record: a condition chain's OR grouping,
    /// an actor's package stack, a response's spoken lines, a navmesh's tables that triangles and search name by position, a
    /// record's script list. By record type: a form list's items (scripts read them by index), a leveled list's entries,
    /// a story-manager node's quests, a response's topic links. A reorder of any other list is the same contents.</summary>
    static readonly HashSet<string> OrderMatters = new(StringComparer.Ordinal)
    { "Conditions", "Packages", "Responses", "EdgeLinks", "Vertices", "DoorTriangles", "NavmeshTree", "Scripts" };
    static readonly Dictionary<string, string[]> OrderMattersByType = new(StringComparer.Ordinal)
    {
        ["FormList"] = new[] { "Items" }, ["LeveledItem"] = new[] { "Entries" }, ["LeveledNpc"] = new[] { "Entries" }, ["LeveledSpell"] = new[] { "Entries" },
        ["StoryManagerQuestNode"] = new[] { "Quests" }, ["DialogResponses"] = new[] { "LinkTo" },
    };

    static readonly Regex Number = new(@"-?\d+(?:\.\d+)?(?:[eE][-+]?\d+)?", RegexOptions.CultureInvariant);

    readonly HashSet<string> _file;
    readonly Dictionary<string, HashSet<string>> _group, _merged, _generated;
    readonly HashSet<string> _mergedRecords, _angles;
    readonly Dictionary<string, Dictionary<string, long>> _masks;
    readonly Dictionary<string, Dictionary<string, Dictionary<string, long>>> _enumNames;
    readonly Dictionary<string, long> _flagBits;
    readonly long _storageBits;

    public string? FlagsField { get; }
    public IReadOnlySet<string> FlagsViews { get; }

    static readonly Lazy<RecordFieldScope> _shipped = new(() =>
    {
        using var s = typeof(RecordFieldScope).Assembly.GetManifestResourceStream("record-field-scope.json")
                      ?? throw new InvalidOperationException("SkyWright's field scope is not in its engine, so no record field can be compared; reinstall SkyWright.");
        return Parse(new StreamReader(s).ReadToEnd());
    });

    /// <summary>The scope this build carries (data/record-field-scope.json).</summary>
    public static RecordFieldScope Shipped => _shipped.Value;

    RecordFieldScope(JsonElement d)
    {
        static HashSet<string> Set(JsonElement e) => e.ValueKind == JsonValueKind.Array ? e.EnumerateArray().Select(x => x.GetString() ?? "").ToHashSet(StringComparer.Ordinal) : new(StringComparer.Ordinal);
        static Dictionary<string, HashSet<string>> Map(JsonElement e) => e.ValueKind != JsonValueKind.Object ? new(StringComparer.Ordinal)
            : e.EnumerateObject().Where(p => p.Name != "about").ToDictionary(p => p.Name, p => Set(p.Value), StringComparer.Ordinal);
        JsonElement G(string k) => d.TryGetProperty(k, out var v) ? v : default;
        _file = Set(G("fileBookkeeping")); _file.UnionWith(Set(G("structBookkeeping")));
        _group = Map(G("groupBookkeeping")); _merged = Map(G("runtimeMerged")); _generated = Map(G("generatedBookkeeping"));
        _mergedRecords = Set(G("runtimeMergedRecords"));
        _angles = Set(G("anglesRadians"));
        _masks = G("integerMasks").ValueKind == JsonValueKind.Object
            ? G("integerMasks").EnumerateObject().ToDictionary(t => t.Name, t => t.Value.EnumerateObject().ToDictionary(f => f.Name, f => f.Value.GetInt64(), StringComparer.Ordinal), StringComparer.Ordinal)
            : new(StringComparer.Ordinal);
        _enumNames = G("integerEnumNames").ValueKind == JsonValueKind.Object
            ? G("integerEnumNames").EnumerateObject().ToDictionary(t => t.Name, t => t.Value.EnumerateObject().ToDictionary(f => f.Name,
                f => f.Value.EnumerateObject().ToDictionary(n => n.Name, n => n.Value.GetInt64(), StringComparer.Ordinal), StringComparer.Ordinal), StringComparer.Ordinal)
            : new(StringComparer.Ordinal);
        var rf = G("recordFlags");
        FlagsField = rf.ValueKind == JsonValueKind.Object && rf.TryGetProperty("field", out var f) ? f.GetString() : null;
        FlagsViews = rf.ValueKind == JsonValueKind.Object && rf.TryGetProperty("sameBitsAs", out var sb) ? Set(sb) : new HashSet<string>();
        _flagBits = rf.ValueKind == JsonValueKind.Object && rf.TryGetProperty("bits", out var bits)
            ? bits.EnumerateObject().ToDictionary(b => b.Name, b => b.Value.GetInt64(), StringComparer.Ordinal) : new(StringComparer.Ordinal);
        _storageBits = rf.ValueKind == JsonValueKind.Object && rf.TryGetProperty("storageOnly", out var so)
            ? so.EnumerateArray().Sum(n => _flagBits.GetValueOrDefault(n.GetString() ?? "")) : 0;
    }

    /// <summary>A scope from the text of a record-field-scope.json.</summary>
    public static RecordFieldScope Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return new RecordFieldScope(doc.RootElement.Clone());
    }

    /// <summary>Why a field of a record type is not compared, or null when it is.</summary>
    public string? Excluded(string rtype, string field)
    {
        string top = RecordDelta.Top(field), leaf = field.Split('.')[^1];
        if (_mergedRecords.Contains(rtype)) return "runtime-merged record";
        if (_file.Contains(top) || _file.Contains(leaf) || _file.Contains(field)) return "file bookkeeping";
        if (_group.TryGetValue(rtype, out var g) && (g.Contains(top) || g.Contains(field))) return "group bookkeeping";
        if (_merged.TryGetValue(rtype, out var m) && m.Contains(top)) return "runtime-merged field";
        if (_generated.TryGetValue(rtype, out var ge) && ge.Contains(top)) return "generated bookkeeping";
        if (ChildGroups.Contains(top)) return "child records";
        return null;
    }

    /// <summary>A record type the game merges at run time, whose every field is left out.</summary>
    public bool RuntimeMergedRecord(string rtype) => _mergedRecords.Contains(rtype);

    /// <summary>A record-flags value as a number: a number, or on the base field comma-separated names that are all known;
    /// null otherwise (a name not in the table is never guessed).</summary>
    public long? RecordFlags(string field, string? v)
    {
        string s = v?.Trim() ?? "";
        if (Regex.IsMatch(s, @"^-?\d+$") && long.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n)) return n & 0xFFFFFFFFL;
        if (field != FlagsField || s.Length == 0) return null;
        long total = 0;
        foreach (var part in s.Split(','))
        {
            if (!_flagBits.TryGetValue(part.Trim(), out var bit)) return null;
            total |= bit;
        }
        return total;
    }

    /// <summary>Two values of a field that are one value in game: record flags without their storage bits, or an integer
    /// field under the mask of what the game reads (an enum name read as its number first).</summary>
    public bool MaskedEqual(string rtype, string field, string? a, string? b)
    {
        if (FlagsField is not null && (field == FlagsField || FlagsViews.Contains(field)))
        {
            long? x = RecordFlags(field, a), y = RecordFlags(field, b);
            return x is not null && y is not null && (x.Value & ~_storageBits) == (y.Value & ~_storageBits);
        }
        if (!_masks.TryGetValue(rtype, out var ms) || !ms.TryGetValue(field, out var mask)) return false;
        var names = _enumNames.TryGetValue(rtype, out var en) && en.TryGetValue(field, out var fn) ? fn : new Dictionary<string, long>();
        long? Num(string? v)
        {
            string t = (v ?? "None").Trim();
            if (names.TryGetValue(t, out var nv)) return nv;
            return double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && !double.IsNaN(d) && !double.IsInfinity(d) ? (long)Math.Truncate(d) : null;
        }
        long? p = Num(a), q = Num(b);
        return p is not null && q is not null && (p.Value & mask) == (q.Value & mask);
    }

    /// <summary>Two values of an angle field (the scope's anglesRadians) that are one rotation: the same text between their
    /// numbers, and each pair of numbers equal modulo a full turn at four decimals (a patch that writes -1.0614327 as
    /// 5.2217526 has not turned anything). False for any other field.</summary>
    public bool AngleEqual(string field, string? a, string? b)
    {
        if (a is null || b is null || !_angles.Contains(field)) return false;
        var na = Number.Matches(a).Select(m => m.Value).ToList();
        var nb = Number.Matches(b).Select(m => m.Value).ToList();
        if (na.Count == 0 || na.Count != nb.Count || Number.Replace(a, "#") != Number.Replace(b, "#")) return false;
        return na.Zip(nb).All(t =>
        {
            double d = double.Parse(t.First, CultureInfo.InvariantCulture) - double.Parse(t.Second, CultureInfo.InvariantCulture);
            d = Math.IEEERemainder(d, 2 * Math.PI);
            return Math.Abs(d) < 1e-4;
        });
    }

    /// <summary>Two values as the game reads them: their numbers at four decimals, -0 equal to 0, the text between them
    /// exactly; anything without numbers exactly.</summary>
    public static bool SameValue(string? a, string? b)
    {
        if (a is null || b is null) return a is null && b is null;
        var na = Number.Matches(a).Select(m => m.Value).ToList();
        var nb = Number.Matches(b).Select(m => m.Value).ToList();
        if (na.Count > 0 && nb.Count > 0 && na.Count == nb.Count && Number.Replace(a, "#") == Number.Replace(b, "#"))
            return na.Zip(nb).All(t =>
            {
                double x = double.Parse(t.First, CultureInfo.InvariantCulture), y = double.Parse(t.Second, CultureInfo.InvariantCulture);
                return Math.Round(x, 4) == Math.Round(y, 4) || Math.Abs(x - y) < 1e-4;
            });
        return a == b;
    }

    /// <summary>Whether the game reads the order of this list (by its own name, on this record type).</summary>
    public static bool OrderIsRead(string rtype, string root)
    {
        string name = root.Split('.')[^1].Split('[')[0];
        return OrderMatters.Contains(name) || (OrderMattersByType.TryGetValue(rtype, out var l) && l.Contains(name));
    }
}
