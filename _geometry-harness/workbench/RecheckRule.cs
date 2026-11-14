using HarnessShared;

namespace Workbench;

/// <summary>What a re-check says about one conflict row, in the app's word and conflictdiff's.</summary>
internal sealed record RowVerdict(string Id, string Word, string ConflictdiffWord, string Sentence);

/// <summary>The re-check's comparison rule, frozen in stage 4 before it was wired (tests: <c>RecheckRuleTests</c>). It is
/// conflictdiff's credit rule, read per row through <see cref="DifferentialRows"/> (the function conflictdiff prints from),
/// never a second rule: the app only names each outcome for the modder.
/// <list type="bullet">
/// <item><b>resolved</b> = conflictdiff's <i>repaired</i>: the row's physical predicate went Holds -> Gone.</item>
/// <item><b>new</b> = <i>created</i>: a row only in the re-run, entering with its predicate holding.</item>
/// <item><b>reclassified</b>: the row left (or entered) the class without that flip; the defect is not shown gone.</item>
/// <item><b>still there</b> = <i>unchanged</i>: the row is in both runs.</item>
/// <item><b>unmeasured</b>: a predicate the outcome rests on could not be measured; neither resolved nor not.</item>
/// </list></summary>
internal static class RecheckRule
{
    internal static RowVerdict Of(DifferentialRows.Credited c, string id)
    {
        if (!c.Outcome.TryGetValue(id, out var o)) o = CreditOutcome.Absent;
        return o switch
        {
            CreditOutcome.Repaired => new(id, "resolved", "repaired", "resolved: its physical predicate went Holds -> Gone under the patch."),
            CreditOutcome.Created => new(id, "new", "created", "new: it appears under the patch with its physical predicate holding."),
            CreditOutcome.Reclassified => new(id, "reclassified", "reclassified",
                "reclassified: it changed class without its physical predicate going away, so it is not shown resolved."),
            CreditOutcome.Unchanged => new(id, "still there", "unchanged", "still there: the re-run finds it as before."),
            CreditOutcome.Unmeasured => new(id, "unmeasured", "unmeasured",
                "unmeasured: a predicate this rests on could not be measured, so it is neither resolved nor not."),
            _ => new(id, "not in either run", "absent", "not in either run: the re-check's population does not reach it."),
        };
    }

    /// <summary>Every row the patch created: the new conflicts.</summary>
    internal static IReadOnlyList<RowVerdict> NewRows(DifferentialRows.Credited c)
        => c.Outcome.Where(kv => kv.Value == CreditOutcome.Created).OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => Of(c, kv.Key)).ToList();
}
