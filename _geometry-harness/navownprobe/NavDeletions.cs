using Mutagen.Bethesda.Plugins;

// THE DELETED / EMPTIED SECTION of navownprobe's report: a winning navmesh record that is deleted, or that arrives empty where
// an earlier provider had geometry. Held in its own class so --moved-ref-selftest runs exactly the lines the main walk prints.
//
// A navmesh a Bethesda master deletes is the shipped game (the DLCs delete Skyrim.esm navmeshes they replace), not a clash
// between mods: it is counted on a line of its own and never a finding (2026-09-28). --list-master-deletions lists those
// deletions after the findings, each labelled as a shipped-game deletion; the findings and the count do not change.
static class NavDeletions
{
    public const string ShippedHeading = "   shipped-game deletions (--list-master-deletions; not findings):";
    public const string ShippedLabel = "a shipped-game deletion (Bethesda master), not a mod clash";

    /// <summary>The section's lines, exactly as printed (the header keeps its leading "\n"). <paramref name="shippedGame"/>
    /// says whether a plugin index is Bethesda's shipped content; <paramref name="wanted"/> is the --plugin filter.</summary>
    public static List<string> Report(NavVersions v, Func<int, bool> wanted, Func<int, bool> shippedGame, Func<int, string> pluginName,
                                      Func<FormKey, string> cellName, int listN, bool listShippedGame)
    {
        var broken = new List<string>();
        var shipped = new List<string>();
        var deletedAtWinner = v.DeletedAtWinner();
        int vanillaDeleted = 0;
        bool VanillaDeletion(FormKey nav) => deletedAtWinner.TryGetValue(nav, out var d) && shippedGame(d);
        string Shipped(FormKey nav, int by, string note) => $"   SHIPPED  {nav} in {cellName(v.Last[nav].Cell)} by {pluginName(by)}{note} - {ShippedLabel}";
        foreach (var (nav, provs) in v.Providers)
        {
            int winner = deletedAtWinner.TryGetValue(nav, out var deleter) ? deleter : provs[^1];
            if (!wanted(winner)) continue;
            if (VanillaDeletion(nav))
            {
                vanillaDeleted++;
                shipped.Add(Shipped(nav, winner, v.Last[nav].NoGeometry ? " (no geometry left)" : ""));
                continue;
            }
            if (deletedAtWinner.ContainsKey(nav))
                broken.Add($"   DELETED  {nav} in {cellName(v.Last[nav].Cell)} by {pluginName(winner)}{(v.Last[nav].NoGeometry ? " (no geometry left)" : "")}");
            else if (provs.Count > 1)
            {
                var w = v.Size.GetValueOrDefault((nav, winner));
                var first = v.Size.GetValueOrDefault((nav, provs[0]));
                if (w.T == 0 && first.T > 0)
                    broken.Add($"   EMPTIED  {nav} in {cellName(v.Cell.GetValueOrDefault(nav))} by {pluginName(winner)} ({first.T} tris -> 0)");
            }
        }
        // A navmesh no plugin ever gave geometry to, whose last record carries none: nothing to walk on either.
        foreach (var (nav, deleter) in deletedAtWinner)
            if (!v.Providers.ContainsKey(nav) && wanted(deleter) && VanillaDeletion(nav))
            {
                vanillaDeleted++;
                shipped.Add(Shipped(nav, deleter, " (no version with geometry in the order)"));
            }
            else if (!v.Providers.ContainsKey(nav) && wanted(deleter))
                broken.Add($"   DELETED  {nav} in {cellName(v.Last[nav].Cell)} by {pluginName(deleter)} (no version with geometry in the order)");

        var lines = new List<string>
        {
            "\n==== DELETED or EMPTIED navmesh (hard break) ====",
            $"   findings: {broken.Count:N0}",
            $"   deleted by a Bethesda master (the shipped game; not findings): {vanillaDeleted:N0}",
        };
        lines.AddRange(broken.Take(listN));
        if (listShippedGame && shipped.Count > 0)
        {
            lines.Add(ShippedHeading);
            lines.AddRange(shipped);
        }
        return lines;
    }
}
