using HarnessShared;

// THE RENUMBERING-PROOF ROW KEY (houseCARL's R4, 2026-11-22). A navedit patch renumbers the navmesh it edits: swap-remove
// moves the last triangle into a removed slot, an add appends, a cut replaces triangles. ConflictIo.RowIdentity keys an
// EDGE-IDX or EDGE-NAV row on its target triangle's index, so comparing the base emit with the patched one shows a row
// whose finding did not change as one gone and one new. This key names each triangle by where it stands
// (ConflictRecord.At) instead; a row without triangles keeps its records and kind. Pure: two emits in, counts out.
public static class RowsAt
{
    /// <summary>A row's key as navmesh <paramref name="nav"/> sees it. A link row: its kind, then each record as FormKey@place
    /// (or "FormKey#index" when its index names no triangle, or the bare FormKey), necessary records first, the first
    /// alternative after; its plugins are left out, since a patch becomes the owner of what it wins. A row without triangles
    /// lists every navmesh of its finding (a CLOBBERED row, every navmesh of one plugin pair), so it keys on this navmesh and
    /// what names the finding: the pair for CLOBBERED, the deleter for DELETED or EMPTIED, nothing more for NAV-GROUND.</summary>
    public static string Key(Conflict c, string nav)
    {
        static string One(ConflictRecord r) => r.At is { } at ? $"{r.FormKey}@{at}" : r.Triangle is { } t ? $"{r.FormKey}#{t}" : r.FormKey;
        var all = c.Necessary.Concat(c.Alternative ?? new List<ConflictRecord>());
        if (!all.Any(r => r.Triangle is not null))
            return c.Kind switch
            {
                "CLOBBERED" => $"{c.Kind}|{nav}|{c.A}>{c.B}",
                "DELETED" or "EMPTIED" => $"{c.Kind}|{nav}|{c.A}",
                _ => $"{c.Kind}|{nav}",
            };
        var parts = c.Necessary.Select(One).ToList();
        if (c.Alternative is { Count: > 0 } alt) parts.Add(One(alt[0]));
        return $"{c.Kind}|{string.Join("|", parts)}";
    }

    /// <summary>The rows of <paramref name="emit"/> that name navmesh <paramref name="nav"/> in any record.</summary>
    public static HashSet<string> Of(IEnumerable<Conflict> emit, string nav) =>
        emit.Where(c => c.Necessary.Concat(c.Alternative ?? new List<ConflictRecord>()).Any(r => string.Equals(r.FormKey, nav, StringComparison.OrdinalIgnoreCase)))
            .Select(c => Key(c, nav)).ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Navmesh <paramref name="nav"/>'s rows, base against now: gone, new, unchanged. <paramref name="patch"/>: the
    /// enabled patch's file name when it overrides the navmesh with the previous winner's geometry (its links re-pointed only).</summary>
    public static (List<string> Gone, List<string> New, List<string> Same) Compare(IEnumerable<Conflict> baseEmit, IEnumerable<Conflict> now, string nav, string? patch = null)
    {
        var b = Of(baseEmit, nav); var n = Of(now, nav);
        // the review of Gate C, decision 3: a CLOBBERED row whose winner is the enabled patch (named only when the patch carries
        // the previous winner's geometry, re-pointing links alone) is the base's CLOBBERED row for this navmesh and the same loser
        if (patch is not null)
        {
            string pre = $"CLOBBERED|{nav}|";
            foreach (var k in n.Where(k => k.StartsWith(pre, StringComparison.OrdinalIgnoreCase) && k.EndsWith(">" + patch, StringComparison.OrdinalIgnoreCase)).ToList())
            {
                var loser = k.Substring(pre.Length, k.Length - pre.Length - patch.Length - 1);
                var was = b.FirstOrDefault(x => x.StartsWith($"{pre}{loser}>", StringComparison.OrdinalIgnoreCase) && !n.Contains(x));
                if (was is not null) { n.Remove(k); n.Add(was); }
            }
        }
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
