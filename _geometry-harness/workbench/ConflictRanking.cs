using System.Text.Json;
using HarnessShared;

namespace Workbench;

/// <summary>The severity weights (ranking key 5): app data, <c>data/workbench-severity.json</c>,
/// keyed by (Kind, base type) with '*' for any kind.</summary>
internal sealed class SeverityTable
{
    readonly Dictionary<string, Dictionary<string, int>> _byKind = new(StringComparer.OrdinalIgnoreCase);
    internal int Unread { get; private init; }
    internal int Default { get; private init; }
    internal string Status { get; private init; } = "";
    internal string Source { get; private init; } = "";

    internal static SeverityTable Load(string path)
    {
        if (!File.Exists(path)) throw new InvalidOperationException($"the severity table is not at '{path}'; it ships in SkyWright's data folder.");
        using var d = JsonDocument.Parse(File.ReadAllText(path));
        var r = d.RootElement;
        var t = new SeverityTable { Unread = r.GetProperty("unread").GetInt32(), Default = r.GetProperty("default").GetInt32(),
                                    Status = r.TryGetProperty("status", out var s) ? s.GetString() ?? "" : "", Source = path };
        foreach (var k in r.GetProperty("weights").EnumerateObject())
            t._byKind[k.Name] = k.Value.EnumerateObject().ToDictionary(x => x.Name, x => x.Value.GetInt32(), StringComparer.OrdinalIgnoreCase);
        return t;
    }

    /// <summary>The weight of one base type under a kind, and the words saying where it came from.</summary>
    internal (int Weight, string Why) Of(string kind, string? baseType)
    {
        if (baseType is null) return (Unread, "base not read by the bundle's names enrichment");
        if (_byKind.TryGetValue(kind, out var m) && m.TryGetValue(baseType, out var w)) return (w, $"{baseType} under {kind}");
        if (_byKind.TryGetValue("*", out var any) && any.TryGetValue(baseType, out var w2)) return (w2, baseType);
        return (Default, $"{baseType} (no weight listed; default)");
    }
}

/// <summary>A ranked row: the row and the value of each sort key, as the Conflicts tab shows them.</summary>
internal sealed record RankedRow(int Rank, ConflictRow Row, bool InvolvesTarget, int PhysicalGrade, string PhysicalLabel,
                                 int EvidenceGrade, string EvidenceLabel, int NewGrade, string NewLabel, int Severity, string SeverityLabel);

/// <summary>DESIGN.md section 3's sort: first difference wins. 1 involves a plan target; 2 the physical half Holds
/// before Unmeasured/unknown before anything else; 3 the check's evidence grade (the same for every row of a check);
/// 4 new on this arm; 5 severity, from the skill's table; 6 the row identity, for a stable order.</summary>
internal static class ConflictRanking
{
    static readonly string[] Grades = { "record-fact", "calibrated-inference", "engine-observation", "supplied-media" };

    internal static IReadOnlyList<RankedRow> Rank(IEnumerable<ConflictRow> rows, IReadOnlyCollection<string> targets, SeverityTable sev)
    {
        var t = new HashSet<string>(targets, StringComparer.OrdinalIgnoreCase);
        var keyed = rows.Select(r =>
        {
            bool target = t.Contains(r.A) || t.Contains(r.B);
            var (pg, pl) = r.Physical switch
            {
                PhysicalState.Holds => (0, "holds"),
                PhysicalState.Unmeasured => (1, "unmeasured"),
                null => (1, "not recorded (read as unmeasured)"),
                _ => (2, r.Physical.ToString()!.ToLowerInvariant()),
            };
            int eg = Array.IndexOf(Grades, r.EvidenceStrength); if (eg < 0) eg = Grades.Length;
            var (ng, nl) = r.NewOnArm switch { true => (0, "new on this arm"), false => (1, "not new"), null => (1, "not compared") };
            var a = sev.Of(r.Kind, r.RefBaseType); var b = sev.Of(r.Kind, r.OtherBaseType);
            var s = a.Weight >= b.Weight ? a : b;
            return (r, target, pg, pl, eg, ng, nl, s);
        })
        .OrderByDescending(x => x.target).ThenBy(x => x.pg).ThenBy(x => x.eg).ThenBy(x => x.ng)
        .ThenByDescending(x => x.s.Weight).ThenBy(x => x.r.Identity, StringComparer.Ordinal)
        .ToList();
        return keyed.Select((x, i) => new RankedRow(i + 1, x.r, x.target, x.pg, x.pl, x.eg, x.r.EvidenceStrength, x.ng, x.nl, x.s.Weight, x.s.Why)).ToList();
    }
}
