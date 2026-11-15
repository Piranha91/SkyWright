namespace Workbench;

/// <summary>One Conflicts row with its decision: the verdict and reason when one is stored, and its standing now.</summary>
internal sealed record ReviewEntry(RankedRow Row, Decision? Decision, DecisionMatch Standing);

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
    {
        var store = DecisionStore.For(s.WorkFolder);
        var binding = s.Binding;
        return Build(rows, r =>
        {
            if (store is null || binding is not { } b) return (null, new DecisionMatch(Standing.Undecided, "undecided"));
            var key = Decisions.RowKey(r.Row);
            if (store.Find(b.Instance, b.Profile, key) is not { } d) return (null, new DecisionMatch(Standing.Undecided, "undecided"));
            var m = Decisions.CompareRow(d, s.RowStateOf(r));
            if (m.Standing != Standing.Reconsider && cache.TryGetValue(key, out var full)) m = full;
            return (d, m);
        }, includeDecided);
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
