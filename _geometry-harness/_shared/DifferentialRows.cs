namespace HarnessShared;

/// <summary>conflictdiff's per-row credit as a function, so a second reader takes it from the same place: an emit's rows
/// keyed as conflictdiff keys them (one per necessary record, by <see cref="ConflictIo.RowIdentity"/>), and every row of
/// two emits' union credited by <see cref="DetectorModel.Credit"/>, a vanished row's changed-arm predicate taken from the
/// changed emit's <c>Predicates</c> (the probe's <c>--base</c>). conflictdiff prints from it; the workbench's re-check
/// reads the outcome of the row it is fixing from it. The gates (population, state, configuration, coverage) stay in
/// conflictdiff: a reader that skips them is not comparing what conflictdiff compares.</summary>
public static class DifferentialRows
{
    /// <summary>One row: its identity (what the predicate is about), its attribution (the plugin pair) and its predicate
    /// under the emitting arm.</summary>
    public sealed record Row(string Id, string Kind, string A, string B, string FormKey, string? Other, PhysicalState? Physical, string? Detail);

    /// <summary>Both emits' rows by identity, the changed emit's base-row predicates, and each row of the union credited.</summary>
    public sealed record Credited(IReadOnlyDictionary<string, Row> Before, IReadOnlyDictionary<string, Row> After,
                                  IReadOnlyDictionary<string, Predicate> Predicates, IReadOnlyDictionary<string, CreditOutcome> Outcome);

    public static List<Row> Of(ConflictFile f)
    {
        var rows = new List<Row>();
        foreach (var c in f.Conflicts)
        {
            string? other = c.Alternative is { Count: > 0 } alt ? alt[0].FormKey : null;
            int? part = c.Alternative is { Count: > 0 } alt2 ? alt2[0].Triangle : null;   // an EDGE-IDX row's target triangle
            foreach (var r in c.Necessary)
                rows.Add(new Row(ConflictIo.RowIdentity(c.Kind, r.FormKey, other, c.A, part), c.Kind, c.A, c.B, r.FormKey, other, r.Physical, r.PhysicalDetail));
        }
        return rows;
    }

    public static Credited Credit(ConflictFile baseFile, ConflictFile changedFile)
    {
        var before = Of(baseFile).GroupBy(r => r.Id).ToDictionary(g => g.Key, g => g.First());
        var after = Of(changedFile).GroupBy(r => r.Id).ToDictionary(g => g.Key, g => g.First());
        var predicates = (changedFile.Predicates ?? Array.Empty<Predicate>())
            .GroupBy(p => ConflictIo.RowIdentity(p.Kind, p.FormKey, p.Other, p.A, p.OtherTriangle)).ToDictionary(g => g.Key, g => g.First());
        var outcome = new Dictionary<string, CreditOutcome>();
        foreach (var id in before.Keys.Union(after.Keys))
        {
            bool inB = before.TryGetValue(id, out var b), inA = after.TryGetValue(id, out var a);
            PhysicalState? changedPhys = inA ? a!.Physical : predicates.TryGetValue(id, out var p) ? p.Physical : null;
            outcome[id] = DetectorModel.Credit(inB, inB ? b!.Physical : null, inA, changedPhys);
        }
        return new Credited(before, after, predicates, outcome);
    }
}
