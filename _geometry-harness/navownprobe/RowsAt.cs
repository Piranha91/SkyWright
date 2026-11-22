using HarnessShared;

// THE RENUMBERING-PROOF ROW KEY (houseCARL's R4, 2026-11-22). A navedit patch renumbers the navmesh it edits: swap-remove
// moves the last triangle into a removed slot, an add appends, a cut replaces triangles. ConflictIo.RowIdentity keys an
// EDGE-IDX or EDGE-NAV row on its target triangle's index, so comparing the base emit with the patched one shows a row
// whose finding did not change as one gone and one new. This key names each triangle by where it stands
// (ConflictRecord.At) instead; a row without triangles keeps its records and kind. Pure: two emits in, counts out.
public static class RowsAt
{
    /// <summary>A row's key: its kind, then each record as FormKey@place (or the bare FormKey when it names no triangle,
    /// or "FormKey#index" when its index names none), necessary records first, the first alternative after.</summary>
    public static string Key(Conflict c)
    {
        static string One(ConflictRecord r) => r.At is { } at ? $"{r.FormKey}@{at}" : r.Triangle is { } t ? $"{r.FormKey}#{t}" : r.FormKey;
        var parts = c.Necessary.Select(One).ToList();
        if (c.Alternative is { Count: > 0 } alt) parts.Add(One(alt[0]));
        return $"{c.Kind}|{string.Join("|", parts)}";
    }

    /// <summary>The rows of <paramref name="emit"/> that name navmesh <paramref name="nav"/> in any record.</summary>
    public static HashSet<string> Of(IEnumerable<Conflict> emit, string nav) =>
        emit.Where(c => c.Necessary.Concat(c.Alternative ?? new List<ConflictRecord>()).Any(r => string.Equals(r.FormKey, nav, StringComparison.OrdinalIgnoreCase)))
            .Select(Key).ToHashSet();

    /// <summary>Navmesh <paramref name="nav"/>'s rows, base against now: gone, new, unchanged.</summary>
    public static (List<string> Gone, List<string> New, List<string> Same) Compare(IEnumerable<Conflict> baseEmit, IEnumerable<Conflict> now, string nav)
    {
        var b = Of(baseEmit, nav); var n = Of(now, nav);
        return (b.Except(n).OrderBy(x => x).ToList(), n.Except(b).OrderBy(x => x).ToList(), b.Intersect(n).OrderBy(x => x).ToList());
    }

    /// <summary>One sentence when the base emit predates places (a row naming a triangle without one), else null: such an
    /// emit cannot be matched by place, and keying it by index would count a renumbered row as gone and new.</summary>
    public static string? Unplaced(IEnumerable<Conflict> baseEmit)
    {
        bool old = baseEmit.Any(c => c.Kind is "EDGE-IDX" or "EDGE-NAV" or "EDGE-NOMATCH" or "EDGE-ONEWAY")
                && !baseEmit.Any(c => c.Necessary.Concat(c.Alternative ?? new List<ConflictRecord>()).Any(r => r.At is not null));
        return old ? "The base emit names triangles but not where they stand (written before 2026-11-22), so its rows cannot be matched across a renumbering; emit it again with this build, then run again." : null;
    }
}
