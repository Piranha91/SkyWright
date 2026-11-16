using System.Text.Json;

namespace Workbench;

/// <summary>The reporting skill's grouping data (scene-policy.json <c>vignettes</c>): the group key's names, the check id to
/// clash type map and the type a check outside the map takes. The workbench folds rows by this data, as the report does.</summary>
internal sealed record SceneKeyPolicy(IReadOnlyList<string> Keys, IReadOnlyDictionary<string, string> Types, string OtherType)
{
    /// <summary>The key names a Conflicts row has a value for.</summary>
    internal static readonly string[] Known = { "type", "pair", "cell", "field" };

    /// <summary>The skill's file, relative to a checkout or a plugin root.</summary>
    internal static readonly string RelativePath = Path.Combine(".claude", "skills", "evaluation-reporting", "references", "scene-policy.json");

    /// <summary>Read the skill's scene policy; a key name the workbench does not map is refused.</summary>
    internal static SceneKeyPolicy Load(string path)
    {
        if (!File.Exists(path)) throw new InvalidOperationException($"the reporting skill's scene policy is not at '{path}', so rows are not grouped into scenes.");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        if (!doc.RootElement.TryGetProperty("vignettes", out var v) || v.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException($"'{path}' has no vignettes section, so rows are not grouped into scenes.");
        var keys = v.TryGetProperty("groupKey", out var gk) && gk.ValueKind == JsonValueKind.Array
            ? gk.EnumerateArray().Select(e => e.GetString() ?? "").ToList()
            : throw new InvalidOperationException($"'{path}' names no vignettes.groupKey, so rows are not grouped into scenes.");
        if (keys.FirstOrDefault(k => !Known.Contains(k)) is { } bad)
            throw new InvalidOperationException($"the reporting skill's group key names '{bad}', which a Conflicts row has no value for; the workbench maps {string.Join(", ", Known)}.");
        var types = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (v.TryGetProperty("types", out var t) && t.ValueKind == JsonValueKind.Object)
            foreach (var p in t.EnumerateObject()) if (p.Value.GetString() is { } s) types[p.Name] = s;
        var other = v.TryGetProperty("otherType", out var o) && o.GetString() is { } os ? os : "other";
        return new SceneKeyPolicy(keys, types, other);
    }

    /// <summary>The clash type of a check id, by the skill's map.</summary>
    internal string TypeOf(string checkId) => Types.TryGetValue(checkId, out var t) ? t : OtherType;

    /// <summary>A Conflicts row's group-key values, in the key's order. A geometry row has no field.</summary>
    internal IReadOnlyList<string> KeyOf(ConflictRow r) => Keys.Select(k => k switch
    {
        "type" => TypeOf(r.CheckId),
        "pair" => string.Join(" / ", new[] { r.A, r.B }.Select(x => x.ToLowerInvariant()).OrderBy(x => x, StringComparer.Ordinal)),
        // the row's cell FormKey; the report reads the same cell from the summary's " in X (ext)"
        "cell" => r.Cell ?? "",
        _ => "",
    }).ToList();
}

/// <summary>Rows of one cluster with equal group-key values: instances of one problem.</summary>
internal sealed record SceneGroup(IReadOnlyList<string> Key, IReadOnlyList<int> Ranks);

/// <summary>Rows of one cell joined by naming a common reference.</summary>
internal sealed record SceneCluster(IReadOnlyList<int> Ranks, IReadOnlyList<SceneGroup> Groups);

/// <summary>One cell's rows: its Conflicts rows in clusters, and its clickable Findings rows listed with them.</summary>
internal sealed record Scene(int Number, string Cell, int? BestRank, IReadOnlyList<SceneCluster> Clusters, IReadOnlyList<int> FindingRanks);

/// <summary>The scenes, and the Findings rows no scene could place (no reference or no cell).</summary>
internal sealed record SceneList(IReadOnlyList<Scene> Scenes, int UnplacedFindings, int FindingsOnlyCells = 0, int FindingsOnlyRows = 0);

/// <summary>Item 7: the scene is the cell; inside it, rows naming a common reference are one cluster, and inside a cluster
/// rows with equal group-key values are one group. No distance threshold.</summary>
internal static class Scenes
{
    internal static SceneList Build(IReadOnlyList<RankedRow> rows, IReadOnlyList<RankedFinding> findings, SceneKeyPolicy policy, bool includeFindingsOnly = false)
    {
        static string Norm(string? k) => (k ?? "").ToLowerInvariant();
        var byCell = rows.GroupBy(r => r.Row.Cell ?? "", StringComparer.OrdinalIgnoreCase).ToList();
        var placed = findings.Where(f => f.Row.Clickable).GroupBy(f => f.Row.Cell!, StringComparer.OrdinalIgnoreCase)
                             .ToDictionary(g => g.Key, g => g.Select(f => f.Rank).OrderBy(x => x).ToList(), StringComparer.OrdinalIgnoreCase);
        var made = new List<(string Cell, int? Best, int BestFinding, List<SceneCluster> Clusters, List<int> Findings)>();
        foreach (var cell in byCell)
        {
            var list = cell.OrderBy(r => r.Rank).ToList();
            // clusters: the connected components of "names a common reference"
            var parent = Enumerable.Range(0, list.Count).ToArray();
            int Find(int i) { while (parent[i] != i) i = parent[i] = parent[parent[i]]; return i; }
            var firstOf = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < list.Count; i++)
                foreach (var k in new[] { list[i].Row.RefKey, list[i].Row.OtherKey }.Where(k => !string.IsNullOrEmpty(k)).Select(Norm))
                    if (firstOf.TryGetValue(k, out var j)) parent[Find(i)] = Find(j); else firstOf[k] = i;
            var clusters = Enumerable.Range(0, list.Count).GroupBy(Find)
                .Select(g => g.Select(i => list[i]).ToList())
                .OrderBy(c => c.Min(r => r.Rank))
                .Select(c => new SceneCluster(c.Select(r => r.Rank).ToList(),
                    c.GroupBy(r => string.Join("\u001f", policy.KeyOf(r.Row)), StringComparer.Ordinal)
                     .OrderBy(g => g.Min(r => r.Rank))
                     .Select(g => new SceneGroup(policy.KeyOf(g.First().Row), g.Select(r => r.Rank).ToList())).ToList()))
                .ToList();
            var f = placed.TryGetValue(cell.Key, out var fr) ? fr : new List<int>();
            placed.Remove(cell.Key);
            made.Add((cell.Key, list[0].Rank, f.Count > 0 ? f[0] : int.MaxValue, clusters, f));
        }
        // a cell holding only Findings rows is a scene of its own, after those holding Conflicts rows
        foreach (var (cell, f) in placed) made.Add((cell, null, f[0], new List<SceneCluster>(), f));
        var ordered = made.OrderBy(m => m.Best is null ? 1 : 0).ThenBy(m => m.Best ?? m.BestFinding).ToList();
        var scenes = ordered.Select((m, i) => new Scene(i + 1, m.Cell, m.Best, m.Clusters, m.Findings)).ToList();
        return new SceneList(scenes, findings.Count(f => !f.Row.Clickable));
    }
}
