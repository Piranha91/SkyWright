namespace Workbench;

/// <summary>One Conflicts row with its decision: the verdict and reason when one is stored, and its standing now.</summary>
internal sealed record ReviewEntry(RankedRow Row, Decision? Decision, DecisionMatch Standing);

/// <summary>One scene as the tab and the scenes part draw it: its rows in list order, how many are hidden, its header.</summary>
internal sealed record SceneView(Scene Scene, IReadOnlyList<ReviewEntry> Rows, int Hidden, string Header);

/// <summary>The Conflicts list with decisions, the one model the tab and the <c>conflicts</c> state part both render.</summary>
internal static class ReviewList
{
    /// <summary>A decision was recorded: the window's list redraws.</summary>
    internal static event Action? Changed;
    internal static void RaiseChanged() => Changed?.Invoke();

    /// <summary>The rows in decision order (reconsider, undecided, unsure, real problem, intentional), rows decided
    /// intentional that still apply left out unless <paramref name="includeDecided"/>.</summary>
    internal static IReadOnlyList<ReviewEntry> Build(IReadOnlyList<RankedRow> rows, Func<RankedRow, (Decision? Decision, DecisionMatch Standing)> of, bool includeDecided)
    {
        var entries = rows.Select(r => { var (d, m) = of(r); return new ReviewEntry(r, d, m); }).ToList();
        var ordered = Decisions.Order(entries, e => (e.Standing.Standing, e.Decision?.Verdict));
        return includeDecided ? ordered
            : ordered.Where(e => !(e.Standing.Standing == Standing.Applies && e.Decision?.Verdict == "intentional")).ToList();
    }

    /// <summary>The list for a surface: each row's stored decision, compared on its detector row (no scene, no server),
    /// or the full comparison already made for it in <paramref name="cache"/> when the row half still holds.</summary>
    internal static IReadOnlyList<ReviewEntry> For(IWorkbenchSurface s, IReadOnlyList<RankedRow> rows, Dictionary<string, DecisionMatch> cache, bool includeDecided)
        => Build(rows, StandingOf(s, cache), includeDecided);

    /// <summary>The scenes for a surface, on the same standings as <see cref="For"/>; null when the skill's policy is not found.</summary>
    internal static IReadOnlyList<SceneView>? ScenesFor(IWorkbenchSurface s, Dictionary<string, DecisionMatch> cache, bool includeDecided)
        => s.ScenePolicy is { } p ? BuildScenes(s.Listed, s.Findings, p, StandingOf(s, cache), includeDecided) : null;

    static Func<RankedRow, (Decision?, DecisionMatch)> StandingOf(IWorkbenchSurface s, Dictionary<string, DecisionMatch> cache)
    {
        var store = DecisionStore.For(s.WorkFolder);
        var binding = s.Binding;
        return r =>
        {
            if (store is null || binding is not { } b) return (null, new DecisionMatch(Standing.Undecided, "undecided"));
            var key = Decisions.RowKey(r.Row);
            if (store.Find(b.Instance, b.Profile, key) is not { } d) return (null, new DecisionMatch(Standing.Undecided, "undecided"));
            var m = Decisions.CompareRow(d, s.RowStateOf(r));
            if (m.Standing != Standing.Reconsider && cache.TryGetValue(key, out var full)) m = full;
            return (d, m);
        };
    }

    /// <summary>The scenes the Conflicts tab and the scenes part draw: each scene's rows in the list's order, intentional
    /// rows that still apply hidden unless asked for, and the count hidden.</summary>
    internal static IReadOnlyList<SceneView> BuildScenes(IReadOnlyList<RankedRow> rows, IReadOnlyList<RankedFinding> findings, SceneKeyPolicy policy,
        Func<RankedRow, (Decision? Decision, DecisionMatch Standing)> standing, bool includeDecided)
    {
        var byRank = rows.GroupBy(r => r.Rank).ToDictionary(g => g.Key, g => g.First());
        return Scenes.Build(rows, findings, policy).Scenes.Select(sc =>
        {
            var sceneRows = sc.Clusters.SelectMany(c => c.Ranks).OrderBy(r => r).Select(r => byRank[r]).ToList();
            var shown = Build(sceneRows, standing, includeDecided);
            int hidden = sceneRows.Count - shown.Count;
            return new SceneView(sc, shown, hidden, Header(sc, sceneRows.Count, hidden));
        }).ToList();
    }

    /// <summary>A scene's header: its cell, its rows, clusters and groups, the Findings rows listed with it, and how many are hidden.</summary>
    internal static string Header(Scene sc, int rows, int hidden)
    {
        var bits = new List<string>();
        if (rows > 0)
        {
            int groups = sc.Clusters.Sum(c => c.Groups.Count);
            bits.Add($"{rows} conflict{(rows == 1 ? "" : "s")}" + (sc.Clusters.Count > 1 ? $" in {sc.Clusters.Count} clusters" : "")
                     + (groups < rows ? $", {groups} group{(groups == 1 ? "" : "s")} of one problem" : ""));
        }
        if (sc.FindingRanks.Count > 0) bits.Add($"{sc.FindingRanks.Count} finding{(sc.FindingRanks.Count == 1 ? "" : "s")}");
        if (hidden > 0) bits.Add($"{hidden} hidden (decided intentional)");
        return $"scene {sc.Number}, cell {sc.Cell}: {string.Join(", ", bits)}";
    }

    /// <summary>The row a "next undecided" opens after <paramref name="currentRank"/>: a reconsider row first, then an
    /// undecided one, each in rank order after the current row and then from the top.</summary>
    internal static RankedRow? NextUndecided(IReadOnlyList<ReviewEntry> entries, int currentRank)
    {
        IEnumerable<ReviewEntry> After(Standing st) => entries.Where(e => e.Row.Rank != currentRank && e.Standing.Standing == st)
                                                            .OrderBy(e => e.Row.Rank <= currentRank ? 1 : 0).ThenBy(e => e.Row.Rank);
        return (After(Standing.Reconsider).FirstOrDefault() ?? After(Standing.Undecided).FirstOrDefault())?.Row;
    }
}
