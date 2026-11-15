using System.Text.Json;
using HarnessShared;
using Mutagen.Bethesda.Plugins;

namespace Workbench;

/// <summary>One row of the Findings tab: what it says, where it opens the viewer if it names a
/// reference and a cell, and its role: <c>finding</c> (what the probe reports), <c>incomplete</c> (what it could not read
/// or search; the output is not whole) or <c>context</c> (said beside the findings, not one of them).</summary>
internal sealed record FindingRow(string CheckId, string Kind, string A, string B, string Summary, string? RefKey, string? Cell,
                                  string? WhyNot, string Role, string Identity, PhysicalState? Physical = null)
{
    internal bool Clickable => RefKey is not null && Cell is not null;
    internal string? BaseType { get; init; }
    internal string? Arm { get; init; }
    internal string? Source { get; init; }
    internal bool? NewOnArm { get; init; }
    internal string EvidenceStrength { get; init; } = "unknown";
}

/// <summary>One reader per json-output probe (D4, "Expand into rows"), each unpacking its result into one row per entry,
/// on the entry shapes the probes write. A cell given as an editor ID (route-cut names most cells that way) is resolved through
/// <c>cellOfRef</c> (the names enrichment's cell for the row's reference); otherwise the row says it cannot open.</summary>
internal static class FindingReaders
{
    static bool IsKey(string? s) => s is not null && FormKey.TryFactory(s, out _);

    static string? S(JsonElement e, string k) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static IEnumerable<JsonElement> List(JsonElement root, string k) =>
        root.TryGetProperty(k, out var a) && a.ValueKind == JsonValueKind.Array ? a.EnumerateArray() : Enumerable.Empty<JsonElement>();

    static string Text(JsonElement e) => e.ValueKind == JsonValueKind.String ? e.GetString()! : e.GetRawText();

    internal static IReadOnlyList<FindingRow> Read(string checkId, string path) => Read(checkId, path, null);

    internal static IReadOnlyList<FindingRow> Read(string checkId, string path, Func<string, string?>? cellOfRef) => Read(checkId, path, cellOfRef, null);

    /// <summary>With <paramref name="relevance"/>, finalize's record for this check and arm
    /// (disposition.discovery.relevance): a discovery hit is a finding when relevant, context saying why when not,
    /// and, when relevance was not computed, a finding carrying that sentence. Relevance is never re-derived here.</summary>
    internal static IReadOnlyList<FindingRow> Read(string checkId, string path, Func<string, string?>? cellOfRef, JsonElement? relevance)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var r = doc.RootElement;
        var rows = new List<FindingRow>();
        string Id(string part) => checkId + "|" + part;

        FindingRow Placed(string kind, string? refKey, string? cell, string summary, string role, string part)
        {
            string? why = null;
            if (refKey is null || !IsKey(refKey)) { why = "this row names no placed reference"; refKey = null; }
            if (!IsKey(cell))
            {
                var resolved = refKey is null ? null : cellOfRef?.Invoke(refKey);
                if (IsKey(resolved)) cell = resolved;
                else
                {
                    why ??= cell is { Length: > 0 }
                        ? $"the row names its cell by editor ID '{cell}', which the bundle's names do not resolve to a cell, so the viewer cannot open it"
                        : "the row records no cell for its reference, so the viewer cannot open it";
                    cell = null;
                }
            }
            return new FindingRow(checkId, kind, "", "", summary, refKey, cell, cell is null || refKey is null ? why : null, role, Id(part));
        }

        FindingRow Note(string kind, string summary, string role, string why, string part) =>
            new(checkId, kind, "", "", summary, null, null, why, role, Id(part));

        void Unreadable(string key, string why)
        {
            int i = 0;
            foreach (var u in List(r, key)) rows.Add(Note(key, $"{key}: {Text(u)}", "incomplete", why, $"{key}:{i++}"));
        }

        void Incomplete()
        {
            if (r.TryGetProperty("complete", out var c) && c.ValueKind == JsonValueKind.False)
                rows.Add(Note("incomplete", "the probe says its output is incomplete (complete: false)", "incomplete", "a statement about the whole output, not a place", "complete"));
        }

        switch (checkId)
        {
            case "geometry.declared-dependencies":
                foreach (var e in List(r, "rows"))
                    rows.Add(Placed(S(e, "verdict") ?? "dependency", S(e, "dependent"), S(e, "cellKey") ?? S(e, "cell"),
                        $"{S(e, "kind")}: {S(e, "dependent")} on host {S(e, "host")} ({S(e, "verdict")}): {S(e, "why")}", "finding", $"{S(e, "host")}|{S(e, "dependent")}|{S(e, "kind")}"));
                foreach (var e in List(r, "withheldRows"))
                    rows.Add(Placed("withheld", S(e, "dependent"), S(e, "cellKey") ?? S(e, "cell"),
                        $"withheld: {S(e, "kind")}: {S(e, "dependent")} on host {S(e, "host")}: {S(e, "why")}", "context", $"withheld|{S(e, "host")}|{S(e, "dependent")}|{S(e, "kind")}"));
                Unreadable("unreadable", "a record the probe could not read names no place");
                break;
            case "geometry.bos-overlay":
                foreach (var e in List(r, "rows"))
                    rows.Add(Placed(S(e, "status") ?? "swap", S(e, "formKey"), S(e, "cell"),
                        $"{S(e, "formKey")}: {S(e, "status")} (record base {S(e, "recordBase")}, effective {S(e, "effectiveBase")})", "finding", S(e, "formKey") ?? ""));
                Unreadable("unreadable", "a record the probe could not read names no place");
                break;
            case "geometry.route-cut":
                foreach (var e in List(r, "Rows"))
                {
                    var v = S(e, "Verdict") ?? "";
                    var role = v is "Blocked" or "LikelyBlocked" or "Clipped" ? "finding" : v == "Unjudgeable" ? "incomplete" : "context";
                    rows.Add(Placed(v, S(e, "FormKey"), S(e, "Cell"), $"{v}: {S(e, "FormKey")} ({S(e, "Mesh")}): {S(e, "Why")}", role, S(e, "FormKey") ?? ""));
                }
                break;
            case "state.switch-census":
                foreach (var e in List(r, "switches"))
                    rows.Add(Placed("switch", S(e, "key"), S(e, "cellKey") ?? S(e, "cell"), $"{S(e, "edid")} ({S(e, "key")}) in {S(e, "cell")}, owned by {S(e, "owner")}", "finding", S(e, "key") ?? ""));
                {
                    int i = 0;
                    foreach (var e in List(r, "jointFlips"))
                        rows.Add(Note("joint flip", $"quest stage {S(e, "stage")} flips {string.Join(", ", List(e, "switches").Select(Text))}", "context",
                                      "a joint flip is a quest stage, not a place", $"flip:{i++}"));
                }
                Unreadable("unreadable", "a record the probe could not read names no place");
                break;
            case "patch.discovery-installed" or "patch.discovery-fomod" or "patch.discovery-pool":
                {
                    bool? computed = relevance is { ValueKind: JsonValueKind.Object } rv && rv.TryGetProperty("computed", out var cv) ? cv.ValueKind == JsonValueKind.True : null;
                    var relevantFiles = computed == true && relevance!.Value.TryGetProperty("relevant", out var rl)
                        ? rl.EnumerateArray().Select(Text).ToHashSet(StringComparer.OrdinalIgnoreCase) : new HashSet<string>();
                    string? notWhy = computed == true && relevance!.Value.TryGetProperty("notRelevant", out var nr) ? S(nr, "why") : null;
                    string? notComputed = computed == false ? S(relevance!.Value, "why") : null;
                    foreach (var e in List(r, "hits"))
                    {
                        var file = S(e, "file") ?? "";
                        var text = $"{file} ({S(e, "kind")}), masters {string.Join(", ", List(e, "masters").Select(Text))}";
                        string role = "finding";
                        if (computed == true && !relevantFiles.Contains(file)) { role = "context"; text += $"; not relevant: {notWhy}"; }
                        else if (computed == false) text += $"; relevance not computed: {notComputed}";
                        rows.Add(Note(S(e, "kind") ?? "hit", text, role, "a patch hit is a file, not a place in the world; open its folder", "hit:" + file));
                    }
                }
                {
                    int i = 0;
                    foreach (var e in List(r, "unjudgeableItems"))
                        rows.Add(Note("unjudgeable", $"{S(e, "what")}: {S(e, "why")}", "incomplete", "an item the search could not judge names no place", $"unjudgeable:{i++}"));
                    i = 0;
                    foreach (var e in List(r, "sourcesNotSearched"))
                        rows.Add(Note("not searched", $"not searched: {S(e, "name") ?? Text(e)} ({S(e, "why")})", "incomplete", "a source the search did not reach names no place", $"notsearched:{i++}"));
                }
                Incomplete();
                break;
            case "loadability.masters-and-order":
                Unreadable("walkFailures", "a walk failure names no record");
                {
                    int i = 0;
                    foreach (var e in List(r, "unreadableRecords"))
                        rows.Add(Note("unreadable record", $"unreadable: {S(e, "FormKey")}: {S(e, "why")}", "incomplete", "no cell is known for a record the probe could not read", $"unreadable:{i++}"));
                }
                Unreadable("limits", "a limit is a statement about the scope");
                {
                    int i = 0;
                    foreach (var e in List(r, "unmeasured"))
                        rows.Add(Note("unmeasured", $"unmeasured {S(e, "extent")}: {S(e, "why")}", "context", "an unmeasured extent is a statement about the scope", $"unmeasured:{i++}"));
                }
                Incomplete();
                break;
            case "patch.author-advice":
                foreach (var k in new[] { "clashes", "rules", "statements", "resolutions", "requirements" })
                {
                    int i = 0;
                    foreach (var e in List(r, k)) rows.Add(Note(k, $"{k}: {Text(e)}", "finding", "author advice is a statement about mods, not a place", $"{k}:{i++}"));
                }
                {
                    int i = 0;
                    foreach (var e in List(r, "warnings")) rows.Add(Note("warning", Text(e), "incomplete", "author advice is a statement about mods, not a place", $"warning:{i++}"));
                }
                break;
            case "geometry.fixture-empty-space":
                foreach (var e in List(r, "Rows"))
                    rows.Add(Placed("empty space", S(e, "FormKey"), S(e, "Cell"), $"empty space at {S(e, "FormKey")}", "finding", S(e, "FormKey") ?? ""));
                break;
            default:
                throw new InvalidOperationException($"no Findings reader reads {checkId}'s json output; its rows cannot be shown.");
        }
        return rows;
    }
}

/// <summary>The cells the session can load (the worklist): the Conflicts tab's cells first, its top row's first, then
/// every cell a clickable Findings row names.</summary>
internal static class SessionCells
{
    internal static IReadOnlyList<string> From(IEnumerable<string?> conflictCells, IEnumerable<string?> findingCells)
    {
        var cells = new List<string>();
        foreach (var c in conflictCells.Concat(findingCells))
            if (c is { Length: > 0 } && !cells.Contains(c, StringComparer.OrdinalIgnoreCase)) cells.Add(c);
        return cells;
    }
}

/// <summary>A ranked Findings row.</summary>
internal sealed record RankedFinding(int Rank, FindingRow Row, bool InvolvesTarget, string NewLabel, int Severity);

/// <summary>'s Findings ranking, first difference wins: 1 names a target; 2 new on the review arm before
/// not new before not compared; 3 the check's evidence grade; 4 physical half holds before unmeasured before anything
/// else (conflict rows); 5 severity by the reference's base type; 6 checkId, then identity.</summary>
internal static class FindingRanking
{
    static readonly string[] Grades = { "record-fact", "calibrated-inference", "engine-observation", "supplied-media" };

    internal static IReadOnlyList<RankedFinding> Rank(IEnumerable<FindingRow> rows, IReadOnlyCollection<string> targets, SeverityTable? sev)
    {
        var t = new HashSet<string>(targets, StringComparer.OrdinalIgnoreCase);
        return rows.Select(r =>
        {
            bool target = t.Contains(r.A) || t.Contains(r.B);
            int ng = r.NewOnArm switch { true => 0, false => 1, null => 2 };
            int eg = Array.IndexOf(Grades, r.EvidenceStrength); if (eg < 0) eg = Grades.Length;
            int pg = r.Physical switch { PhysicalState.Holds => 0, PhysicalState.Unmeasured or null => 1, _ => 2 };
            int w = sev?.Of(r.Kind, r.BaseType).Weight ?? 0;
            return (r, target, ng, eg, pg, w);
        })
        .OrderByDescending(x => x.target).ThenBy(x => x.ng).ThenBy(x => x.eg).ThenBy(x => x.pg).ThenByDescending(x => x.w)
        .ThenBy(x => x.r.CheckId, StringComparer.Ordinal).ThenBy(x => x.r.Identity, StringComparer.Ordinal)
        .Select((x, i) => new RankedFinding(i + 1, x.r, x.target, x.ng switch { 0 => "new on this arm", 1 => "not new", _ => "not compared" }, x.w))
        .ToList();
    }
}

/// <summary>New on the review arm (L2). The created rows are the differential's own credit of the arm's emit against the
/// baseline's (<see cref="DifferentialRows"/>, conflictdiff's rule: on the arm, not on the baseline, holding), every one of
/// them, not the receipt's capped examples. How many of those are a new defect, attributed or unmeasured needs the reverse
/// measurement, which ordersweep owns: those counts are read from its diff receipt, never recomputed here.</summary>
internal static class ReviewCredit
{
    internal sealed record Tally(int Created, int NewDefect, int Unmeasured, IReadOnlySet<string> CreatedIds);

    internal static IReadOnlySet<string> CreatedIds(ConflictFile baseFile, ConflictFile reviewFile)
    {
        var c = DifferentialRows.Credit(baseFile, reviewFile);
        return c.Outcome.Where(kv => kv.Value == CreditOutcome.Created).Select(kv => kv.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    internal static Tally Counts(string diffReceipt, string baseEmit, string reviewEmit)
    {
        var ids = CreatedIds(ConflictIo.Read(baseEmit), ConflictIo.Read(reviewEmit));
        using var d = JsonDocument.Parse(File.ReadAllText(diffReceipt));
        var cr = d.RootElement.GetProperty("orderEvaluation").GetProperty("created");
        return new Tally(ids.Count, cr.GetProperty("newDefect").GetInt32(), cr.GetProperty("unmeasured").GetInt32(), ids);
    }
}
