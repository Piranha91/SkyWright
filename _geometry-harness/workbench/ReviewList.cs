namespace Workbench;

/// <summary>One Conflicts row with its decision: the verdict and reason when one is stored, and its standing now.</summary>
internal sealed record ReviewEntry(RankedRow Row, Decision? Decision, DecisionMatch Standing);

/// <summary>One scene as the tab and the scenes part draw it: its rows in list order, how many are hidden, its header.</summary>
internal sealed record SceneView(Scene Scene, IReadOnlyList<ReviewEntry> Rows, int Hidden, string Header);

/// <summary>The scenes as drawn, and what is left out: cells holding only Findings rows (behind a toggle) and rows no scene placed.</summary>
internal sealed record ScenesView(IReadOnlyList<SceneView> Scenes, int FindingsOnlyCells, int FindingsOnlyRows, int Unplaced);

/// <summary>One line of the Conflicts tab: a scene header, a Conflicts row, a Findings row, a fold ("show K more") or a count.</summary>
internal sealed record ListItem(string Kind, string Text, object? Tag);

/// <summary>A folded Findings run's line: clicking it shows the scene's whole run.</summary>
internal sealed record FoldTag(int Scene);

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
        => ScenesViewFor(s, cache, includeDecided, includeFindingsOnly: true)?.Scenes;

    /// <summary>The scenes view for a surface; findings-only cells counted unless <paramref name="includeFindingsOnly"/>.</summary>
    internal static ScenesView? ScenesViewFor(IWorkbenchSurface s, Dictionary<string, DecisionMatch> cache, bool includeDecided, bool includeFindingsOnly)
        => s.ScenePolicy is { } p ? BuildScenesView(s.Listed, s.Findings, p, StandingOf(s, cache), includeDecided, includeFindingsOnly) : null;

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
        => BuildScenesView(rows, findings, policy, standing, includeDecided, includeFindingsOnly: true).Scenes;

    static readonly System.Text.RegularExpressions.Regex CellName = new(@" in ([A-Za-z0-9_]+) \((?:int|ext)\)");

    /// <summary>A cell's name as its rows' own summaries say it (" in WindhelmExterior02 (ext)"), or null.</summary>
    static string? NameOf(IEnumerable<string?> summaries)
        => summaries.Select(t => t is null ? null : CellName.Match(t)).FirstOrDefault(m => m is { Success: true })?.Groups[1].Value;

    /// <summary>Past this many Findings rows a scene's run folds to a "show" row.</summary>
    internal const int FindingsFoldAt = 20;

    /// <summary>The scenes with what is left out counted: findings-only cells unless asked for, and rows no scene placed.</summary>
    internal static ScenesView BuildScenesView(IReadOnlyList<RankedRow> rows, IReadOnlyList<RankedFinding> findings, SceneKeyPolicy policy,
        Func<RankedRow, (Decision? Decision, DecisionMatch Standing)> standing, bool includeDecided, bool includeFindingsOnly)
    {
        var byRank = rows.GroupBy(r => r.Rank).ToDictionary(g => g.Key, g => g.First());
        var fByRank = findings.GroupBy(f => f.Rank).ToDictionary(g => g.Key, g => g.First());
        var list = Scenes.Build(rows, findings, policy, includeFindingsOnly);
        var views = list.Scenes.Select(sc =>
        {
            var sceneRows = sc.Clusters.SelectMany(c => c.Ranks).OrderBy(r => r).Select(r => byRank[r]).ToList();
            var shown = Build(sceneRows, standing, includeDecided);
            int hidden = sceneRows.Count - shown.Count;
            var name = NameOf(sceneRows.Select(r => (string?)r.Row.Summary).Concat(sc.FindingRanks.Take(5).Select(f => fByRank.TryGetValue(f, out var x) ? x.Row.Summary : null)));
            return new SceneView(sc, shown, hidden, Header(sc, sceneRows.Count, hidden, name));
        }).ToList();
        return new ScenesView(views, list.FindingsOnlyCells, list.FindingsOnlyRows, list.UnplacedFindings);
    }

    /// <summary>The Conflicts tab's lines for these scenes: each header, its rows, its Findings rows up to
    /// <paramref name="foldAt"/> and a fold for the rest unless the scene is expanded, then the count of what is left out.</summary>
    internal static IReadOnlyList<ListItem> Items(ScenesView v, IReadOnlyDictionary<int, RankedFinding> findings, int foldAt, IReadOnlySet<int> expanded)
    {
        static string S(int n, string one, string many) => n == 1 ? one : many;
        var items = new List<ListItem>();
        foreach (var sv in v.Scenes)
        {
            items.Add(new ListItem("header", sv.Header, sv.Scene));
            foreach (var e in sv.Rows) items.Add(new ListItem("row", Shell.RowText(e.Row), e));
            var fr = sv.Scene.FindingRanks;
            int show = expanded.Contains(sv.Scene.Number) ? fr.Count : Math.Min(fr.Count, foldAt);
            for (int i = 0; i < show; i++)
                if (findings.TryGetValue(fr[i], out var f))
                    items.Add(new ListItem("finding", $"   finding {f.Rank}. [{f.Row.CheckId}] {f.Row.Summary}   (not decided here)", f));
            if (show < fr.Count)
                items.Add(new ListItem("fold", $"   … {fr.Count - show} more Findings rows in this cell (click to show them)", new FoldTag(sv.Scene.Number)));
        }
        if (v.FindingsOnlyCells > 0)
            items.Add(new ListItem("count", $"{v.FindingsOnlyCells} more {S(v.FindingsOnlyCells, "cell holds", "cells hold")} only Findings rows ({v.FindingsOnlyRows} {S(v.FindingsOnlyRows, "row", "rows")}); tick \"show cells with only Findings rows\" to list them", null));
        if (v.Unplaced > 0)
            items.Add(new ListItem("count", $"{v.Unplaced} Findings {S(v.Unplaced, "row names", "rows name")} no cell or no reference; the Findings tab lists {S(v.Unplaced, "it", "them")}", null));
        return items;
    }

    /// <summary>A scene's header: its cell, its rows, clusters and groups, the Findings rows listed with it, and how many are hidden.</summary>
    internal static string Header(Scene sc, int rows, int hidden, string? name = null)
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
        return $"scene {sc.Number}, {(name is null ? "cell " + sc.Cell : $"{name} ({sc.Cell})")}: {string.Join(", ", bits)}";
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
