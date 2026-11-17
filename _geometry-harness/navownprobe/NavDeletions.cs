using HarnessShared;
using Mutagen.Bethesda.Plugins;

// THE DELETED / EMPTIED SECTION of navownprobe's report: a winning navmesh record that is deleted, or that arrives empty where
// an earlier provider had geometry. Held in its own class so --moved-ref-selftest runs exactly the lines the main walk prints.
//
// A navmesh a Bethesda master deletes is the shipped game (the DLCs delete Skyrim.esm navmeshes they replace), not a clash
// between mods: it is counted on a line of its own and never a finding (2026-09-28). --list-master-deletions lists those
// deletions after the findings, each labelled as a shipped-game deletion; the findings and the count do not change.
//
// Since 2026-11-16 the findings are also ROWS (Find): the emit carries one DELETED or EMPTIED row per finding, so a bundle,
// a report and the Workbench see what the section prints. Before then they were printed only.
static class NavDeletions
{
    public const string ShippedHeading = "   shipped-game deletions (--list-master-deletions; not findings):";
    public const string ShippedLabel = "a shipped-game deletion (Bethesda master), not a mod clash";

    /// <summary>One finding: DELETED (the winning sighting takes the navmesh out) or EMPTIED (the winning version has no
    /// triangles where an earlier one had some). <paramref name="By"/> is the plugin whose sighting wins;
    /// <paramref name="LastGood"/> the last earlier plugin whose version had triangles (-1: none in the order);
    /// <paramref name="FirstT"/> the first provider's triangle count (EMPTIED's "N tris -> 0").</summary>
    public sealed record Row(string Kind, FormKey Nav, int By, int LastGood, FormKey Cell, bool NoGeometry, bool NoVersion, int FirstT);

    /// <summary>The findings and the shipped-game deletions, under the --plugin filter <paramref name="wanted"/>.</summary>
    public static (List<Row> Findings, List<Row> Shipped) Find(NavVersions v, Func<int, bool> wanted, Func<int, bool> shippedGame)
    {
        var findings = new List<Row>();
        var shipped = new List<Row>();
        var deletedAtWinner = v.DeletedAtWinner();
        bool VanillaDeletion(FormKey nav) => deletedAtWinner.TryGetValue(nav, out var d) && shippedGame(d);
        int LastGood(FormKey nav, int before) => v.Providers.TryGetValue(nav, out var pl)
            ? pl.Where(q => q < before && v.Size.GetValueOrDefault((nav, q)).T > 0 && !v.Deleted.GetValueOrDefault((nav, q))).DefaultIfEmpty(-1).Last()
            : -1;
        foreach (var (nav, provs) in v.Providers)
        {
            int winner = deletedAtWinner.TryGetValue(nav, out var deleter) ? deleter : provs[^1];
            if (!wanted(winner)) continue;
            if (VanillaDeletion(nav))
            {
                shipped.Add(new Row("DELETED", nav, winner, LastGood(nav, winner), v.Last[nav].Cell, v.Last[nav].NoGeometry, false, 0));
                continue;
            }
            if (deletedAtWinner.ContainsKey(nav))
                findings.Add(new Row("DELETED", nav, winner, LastGood(nav, winner), v.Last[nav].Cell, v.Last[nav].NoGeometry, false, 0));
            else if (provs.Count > 1)
            {
                var w = v.Size.GetValueOrDefault((nav, winner));
                var first = v.Size.GetValueOrDefault((nav, provs[0]));
                if (w.T == 0 && first.T > 0)
                    findings.Add(new Row("EMPTIED", nav, winner, LastGood(nav, winner), v.Cell.GetValueOrDefault(nav), false, false, first.T));
            }
        }
        // A navmesh no plugin ever gave geometry to, whose last record carries none: nothing to walk on either.
        foreach (var (nav, deleter) in deletedAtWinner)
            if (!v.Providers.ContainsKey(nav) && wanted(deleter))
                (VanillaDeletion(nav) ? shipped : findings).Add(new Row("DELETED", nav, deleter, -1, v.Last[nav].Cell, v.Last[nav].NoGeometry, true, 0));
        return (findings, shipped);
    }

    /// <summary>A finding's physical half under an arm: Holds while the winning state still takes the navmesh out (DELETED)
    /// or still has no triangles (EMPTIED).</summary>
    public static (PhysicalState State, string Detail) Predicate(string kind, FormKey nav, NavVersions v)
    {
        var deleted = v.DeletedAtWinner();
        if (kind == "DELETED")
            return deleted.ContainsKey(nav) ? (PhysicalState.Holds, "the winning record still deletes the navmesh or carries no geometry")
                 : v.Providers.ContainsKey(nav) ? (PhysicalState.Gone, "a version with geometry wins again")
                 : (PhysicalState.Unmeasured, "no plugin in this order provides the navmesh");
        if (kind == "EMPTIED")
        {
            if (deleted.ContainsKey(nav)) return (PhysicalState.Holds, "the winning record deletes the navmesh");
            if (!v.Providers.TryGetValue(nav, out var pl) || pl.Count == 0) return (PhysicalState.Unmeasured, "no plugin in this order provides the navmesh");
            return v.Size.GetValueOrDefault((nav, pl[^1])).T == 0 ? (PhysicalState.Holds, "the winning version still has no triangles")
                                                                 : (PhysicalState.Gone, "the winning version has triangles again");
        }
        return (PhysicalState.Unmeasured, $"no deletion predicate is defined for kind {kind}");
    }

    /// <summary>The section's lines, exactly as printed (the header keeps its leading "\n"). <paramref name="shippedGame"/>
    /// says whether a plugin index is Bethesda's shipped content; <paramref name="wanted"/> is the --plugin filter.</summary>
    public static List<string> Report(NavVersions v, Func<int, bool> wanted, Func<int, bool> shippedGame, Func<int, string> pluginName,
                                      Func<FormKey, string> cellName, int listN, bool listShippedGame)
    {
        var (findings, shipped) = Find(v, wanted, shippedGame);
        string Line(Row r) => r.Kind == "EMPTIED"
            ? $"   EMPTIED  {r.Nav} in {cellName(r.Cell)} by {pluginName(r.By)} ({r.FirstT} tris -> 0)"
            : $"   DELETED  {r.Nav} in {cellName(r.Cell)} by {pluginName(r.By)}{(r.NoVersion ? " (no version with geometry in the order)" : r.NoGeometry ? " (no geometry left)" : "")}";
        string Shipped(Row r) => $"   SHIPPED  {r.Nav} in {cellName(r.Cell)} by {pluginName(r.By)}"
            + $"{(r.NoVersion ? " (no version with geometry in the order)" : r.NoGeometry ? " (no geometry left)" : "")} - {ShippedLabel}";
        var lines = new List<string>
        {
            "\n==== DELETED or EMPTIED navmesh (hard break) ====",
            $"   findings: {findings.Count:N0}",
            $"   deleted by a Bethesda master (the shipped game; not findings): {shipped.Count:N0}",
        };
        lines.AddRange(findings.Select(Line).Take(listN));
        if (listShippedGame && shipped.Count > 0)
        {
            lines.Add(ShippedHeading);
            lines.AddRange(shipped.Select(Shipped));
        }
        return lines;
    }
}
