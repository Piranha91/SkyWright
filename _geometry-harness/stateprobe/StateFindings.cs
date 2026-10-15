using HarnessShared;

// THE CLASS DECISIONS over the evaluated state rows (Program.cs): which are the class (StateConditional, not the set against
// its own members, not a mod's set against its own geometry), which are reported as resolved, which are the same-author
// floor, and which class rows the emit carries (the Bethesda floor withheld). Held in their own class so --self-test runs
// exactly the decisions a real run makes; the listings and the emit writing stay in Program.cs.
static class StateFindings
{
    /// <summary>The rows split by verdict, same-set and same-author. Every list keeps the rows' order.</summary>
    public sealed record Split(List<StateRow> Class, List<StateRow> Resolved, List<StateRow> OwnFloor);

    public static Split Of(IReadOnlyList<StateRow> rows)
    {
        var cls = rows.Where(r => r.Verdict == StateVerdict.StateConditional && !r.SameSet && !r.SameAuthor).ToList();
        var resolved = rows.Where(r => r.Verdict == StateVerdict.StateResolved && !r.SameSet && !r.SameAuthor).ToList();
        var ownFloor = rows.Where(r => r.Verdict == StateVerdict.StateConditional && !r.SameSet && r.SameAuthor).ToList();
        return new(cls, resolved, ownFloor);
    }

    /// <summary>The class rows the emit carries: the Bethesda floor is withheld.</summary>
    public static IEnumerable<StateRow> Emitted(IEnumerable<StateRow> cls) => cls.Where(r => r.Population != StatePopulation.BethesdaFloor);
}
