using System.Text.Json;

namespace Workbench;

/// <summary>One field of a record as the houseCARL server reads it.</summary>
internal sealed record FieldValue(string Path, string Value);

/// <summary>One object a decided row names, as its owners read it when the decision was made: the winning record and its
/// base (fields from the houseCARL server), and the base's model with its winning copy's content hash.</summary>
internal sealed record ObjectState(string FormKey, string Winner, IReadOnlyList<FieldValue> Fields,
                                   string? BaseKey, IReadOnlyList<FieldValue> BaseFields,
                                   string? Model, string? ModelWinner, string? ModelSha256);

/// <summary>The detector's own row when the decision was made: its physical state and summary, and the probe build.</summary>
internal sealed record RowState(string? Physical, string Summary, string ProbeBuild);

/// <summary>A modder's decision on one row of one profile.</summary>
internal sealed record Decision(string Instance, string Profile, string CheckId, string RowKey, string Verdict, string Reason,
                                DateTimeOffset When, RowState Row, IReadOnlyList<ObjectState> Objects, string ServerBuild);

internal enum Standing { Undecided, Applies, Reconsider }

internal sealed record DecisionMatch(Standing Standing, string Sentence);

/// <summary>Real problem / intentional / unsure: what a stored decision means for a row in this run. A decision applies
/// while the detector's row and the objects it names read the same as when it was made; any change a player could meet
/// flags it for reconsideration, naming what changed. A change in the reader or the probe build alone is said, and the
/// decision stands.</summary>
internal static class Decisions
{
    internal static readonly string[] Verdicts = { "real problem", "intentional", "unsure" };

    /// <summary>Fields a re-save changes with nothing a player meets; a change in only these keeps the decision.</summary>
    internal static readonly string[] Bookkeeping = { "FormVersion", "VersionControl", "Version2", "IsCompressed", "MajorRecordFlagsRaw" };

    /// <summary>The base record's fields geometry reads; the rest of the base is not part of the key.</summary>
    internal static readonly string[] BaseGeometryFields = { "Model", "ObjectBounds" };

    static bool IsBookkeeping(string path) => Bookkeeping.Any(b => path == b || path.StartsWith(b + ".", StringComparison.Ordinal));

    /// <summary>A Conflicts row's key: the detector's row identity.</summary>
    internal static string RowKey(ConflictRow r) => r.Identity.ToLowerInvariant();

    /// <summary>A Findings row's key: its check and its own identity, lower case.</summary>
    internal static string FindingKey(FindingRow f) => ("finding|" + f.Identity).ToLowerInvariant();

    static Dictionary<string, string> Map(IEnumerable<FieldValue> fs, bool skipBookkeeping) =>
        fs.Where(f => !skipBookkeeping || !IsBookkeeping(f.Path)).GroupBy(f => f.Path, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Value, StringComparer.Ordinal);

    static List<string> Diff(IEnumerable<FieldValue> a, IEnumerable<FieldValue> b, bool skipBookkeeping)
    {
        var x = Map(a, skipBookkeeping); var y = Map(b, skipBookkeeping);
        return x.Keys.Union(y.Keys).Where(k => !x.TryGetValue(k, out var u) || !y.TryGetValue(k, out var v) || u != v).OrderBy(k => k, StringComparer.Ordinal).ToList();
    }

    /// <summary>Does the stored decision still apply to this row as read now?</summary>
    internal static DecisionMatch Compare(Decision stored, RowState row, IReadOnlyList<ObjectState> now, string serverBuild)
    {
        var why = new List<string>();
        var notes = new List<string>();
        if (!string.Equals(stored.Row.Physical, row.Physical, StringComparison.OrdinalIgnoreCase) || stored.Row.Summary != row.Summary)
            why.Add($"the detector reads the row differently now ({stored.Row.Physical ?? "unmeasured"}: \"{stored.Row.Summary}\" became {row.Physical ?? "unmeasured"}: \"{row.Summary}\")"
                    + (stored.Row.ProbeBuild != row.ProbeBuild ? ", and the probe build changed too" : ""));
        bool resaved = false, meshUnknown = false;
        foreach (var o in stored.Objects)
        {
            var n = now.FirstOrDefault(x => string.Equals(x.FormKey, o.FormKey, StringComparison.OrdinalIgnoreCase));
            if (n is null) { why.Add($"{o.FormKey} was not read this time"); continue; }
            if (!string.Equals(o.Winner, n.Winner, StringComparison.OrdinalIgnoreCase)) why.Add($"{o.FormKey}'s winning plugin is {n.Winner} now, not {o.Winner}");
            var changed = Diff(o.Fields, n.Fields, true).Concat(Diff(o.BaseFields, n.BaseFields, true).Select(p => "base " + p)).ToList();
            if (changed.Count > 0) why.Add($"{o.FormKey} changed in {string.Join(", ", changed.Take(6))}{(changed.Count > 6 ? $" and {changed.Count - 6} more" : "")}");
            else if (Diff(o.Fields, n.Fields, false).Count > 0 || Diff(o.BaseFields, n.BaseFields, false).Count > 0) resaved = true;
            if (o.ModelSha256 is null || n.ModelSha256 is null) { if (o.Model is not null || n.Model is not null) meshUnknown = true; }
            else if (!string.Equals(o.ModelSha256, n.ModelSha256, StringComparison.OrdinalIgnoreCase)
                     || !string.Equals(o.Model, n.Model, StringComparison.OrdinalIgnoreCase))
                why.Add($"{o.FormKey}'s mesh {n.Model} changed (its content hash differs)");
        }
        if (why.Count > 0)
        {
            if (stored.ServerBuild != serverBuild) why.Add("the houseCARL reader changed too, so part of this may be how it reads");
            return new(Standing.Reconsider, "reconsider: " + string.Join("; ", why) + ".");
        }
        if (resaved) notes.Add("a record was re-saved with nothing a player meets changed");
        if (stored.Row.ProbeBuild != row.ProbeBuild) notes.Add("the probe build changed and nothing else did");
        if (stored.ServerBuild != serverBuild) notes.Add("the houseCARL reader changed and nothing else did");
        if (meshUnknown) notes.Add("mesh not compared: its content hash was not read");
        return new(Standing.Applies, "applies" + (notes.Count > 0 ? ": " + string.Join("; ", notes) : ", unchanged since " + stored.When.ToString("yyyy-MM-dd")) + ".");
    }

    /// <summary>The detector-row half alone (no scene, no server): reconsider when the row reads differently now, else
    /// applies with the objects not compared.</summary>
    internal static DecisionMatch CompareRow(Decision stored, RowState row) => new(Standing.Undecided, "");

    /// <summary>The list order with decisions: reconsider, undecided, unsure, real problem, then intentional; the
    /// order within each group is kept.</summary>
    internal static IReadOnlyList<T> Order<T>(IReadOnlyList<T> rows, Func<T, (Standing Standing, string? Verdict)> of)
    {
        int Group(T r)
        {
            var (st, v) = of(r);
            if (st == Standing.Reconsider) return 0;
            if (st == Standing.Undecided || v is null) return 1;
            return v switch { "unsure" => 2, "real problem" => 3, _ => 4 };
        }
        return rows.Select((r, i) => (r, i)).OrderBy(x => Group(x.r)).ThenBy(x => x.i).Select(x => x.r).ToList();
    }
}

/// <summary>The decisions file under the Work Folder (<c>review-decisions.json</c>), keyed by instance, profile and
/// row key, all compared without regard to case. Every write rewrites the file whole; nothing is ever dropped.</summary>
internal sealed class DecisionStore
{
    internal const string FileName = "review-decisions.json";
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    readonly string _path;
    readonly List<Decision> _all;

    internal DecisionStore(string path)
    {
        _path = path;
        _all = File.Exists(path)
            ? JsonSerializer.Deserialize<List<Decision>>(File.ReadAllText(path), Json) ?? new List<Decision>()
            : new List<Decision>();
    }

    internal static DecisionStore? For(string? workFolder) => string.IsNullOrWhiteSpace(workFolder) ? null : new DecisionStore(Path.Combine(workFolder, FileName));

    static bool Same(string a, string b) => string.Equals(a.Replace('\\', '/').TrimEnd('/'), b.Replace('\\', '/').TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

    internal void Put(Decision d)
    {
        _all.RemoveAll(x => Same(x.Instance, d.Instance) && Same(x.Profile, d.Profile) && Same(x.RowKey, d.RowKey));
        _all.Add(d);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_all, Json));
        File.Move(tmp, _path, overwrite: true);
    }

    internal Decision? Find(string instance, string profile, string rowKey) =>
        _all.LastOrDefault(x => Same(x.Instance, instance) && Same(x.Profile, profile) && Same(x.RowKey, rowKey));

    internal IReadOnlyList<Decision> All => _all;

    internal IEnumerable<Decision> Of(string instance, string profile) => _all.Where(x => Same(x.Instance, instance) && Same(x.Profile, profile));
}
