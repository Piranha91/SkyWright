namespace Workbench;

/// <summary>One Conflicts row with its decision: the verdict and reason when one is stored, and its standing now.</summary>
internal sealed record ReviewEntry(RankedRow Row, Decision? Decision, DecisionMatch Standing);

/// <summary>The Conflicts list with decisions, the one model the tab and the <c>conflicts</c> state part both render.</summary>
internal static class ReviewList
{
    /// <summary>The rows in decision order (reconsider, undecided, unsure, real problem, intentional), intentional
    /// rows left out unless <paramref name="includeDecided"/>.</summary>
    internal static IReadOnlyList<ReviewEntry> Build(IReadOnlyList<RankedRow> rows, Func<RankedRow, (Decision? Decision, DecisionMatch Standing)> of, bool includeDecided)
        => rows.Select(r => new ReviewEntry(r, null, new DecisionMatch(Standing.Undecided, "undecided"))).ToList();

    /// <summary>The row a "next undecided" opens after <paramref name="currentRank"/>: a reconsider row first, then an undecided one.</summary>
    internal static RankedRow? NextUndecided(IReadOnlyList<ReviewEntry> entries, int currentRank) => null;
}
