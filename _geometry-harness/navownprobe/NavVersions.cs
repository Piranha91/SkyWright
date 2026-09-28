using HousecarlCore;
using Mutagen.Bethesda.Plugins;

// THE NAVMESH VERSION HISTORY navownprobe's detectors read: every plugin that supplies each navmesh, in load order, with
// each version's size, content hash and deleted flag, and the cell of the last version seen. Held in its own class so
// --moved-ref-selftest runs exactly the code the main walk runs.
sealed class NavVersions
{
    public readonly Dictionary<FormKey, List<int>> Providers = new();                      // NAVM -> plugins, in order
    public readonly Dictionary<FormKey, FormKey> Cell = new();                             // NAVM -> its cell
    // (NAVM, plugin) -> geometry size AND a content hash over every vertex, triangle index and flag.
    // Counts alone were the "material" test until 2026-08-27; a re-cut that moves vertices without
    // changing their number was invisible, and the 20 "forwarded unchanged" records rested on an
    // "almost always". The hash decides it: same counts + same hash IS a byte-identical forward.
    public readonly Dictionary<(FormKey, int), (int V, int T, int H)> Size = new();
    public readonly Dictionary<(FormKey, int), bool> Deleted = new();
    public readonly Dictionary<FormKey, SortedSet<int>> CellPlugins = new();               // cell -> plugins supplying navmesh
    /// <summary>NAVM -> its last sighting in load order: the plugin, and whether that sighting carried no geometry
    /// (CellPlacements.DeletedNavmeshes). A sighting with no geometry is not a version (it has no size to compare), but when
    /// it is the last one it is the navmesh's winning state.</summary>
    public readonly Dictionary<FormKey, (int Plugin, bool NoGeometry, FormKey Cell)> Last = new();

    /// <summary>One cell of plugin <paramref name="p"/>, in load order. <paramref name="deletedHere"/>: the navmeshes the
    /// plugin flags deleted (read from the records).</summary>
    public void Note(CellPlacements cp, int p, ISet<FormKey> deletedHere, Func<NavmeshGeometry, int> hash)
    {
        foreach (var nav in cp.Navmeshes)
        {
            if (!Providers.TryGetValue(nav.FormKey, out var l)) Providers[nav.FormKey] = l = new List<int>();
            if (l.Count == 0 || l[^1] != p) l.Add(p);
            Cell[nav.FormKey] = cp.Cell;
            Size[(nav.FormKey, p)] = (nav.Vertices.Count, nav.Triangles.Count, hash(nav));
            Deleted[(nav.FormKey, p)] = deletedHere.Contains(nav.FormKey);
            if (!CellPlugins.TryGetValue(cp.Cell, out var s)) CellPlugins[cp.Cell] = s = new SortedSet<int>();
            s.Add(p);
            Last[nav.FormKey] = (p, false, cp.Cell);
        }
        if (cp.DeletedNavmeshes is { Count: > 0 } gone)
            foreach (var d in gone) Last[d.FormKey] = (p, true, cp.Cell);
    }

    /// <summary>The navmeshes whose winning state is a deletion, with the plugin that deleted them: the last sighting is a
    /// version flagged deleted, or a record that carries no geometry (flagged deleted or not: nothing to walk on).</summary>
    public Dictionary<FormKey, int> DeletedAtWinner()
    {
        var d = new Dictionary<FormKey, int>();
        foreach (var (nav, last) in Last)
            if (last.NoGeometry || Deleted.GetValueOrDefault((nav, last.Plugin))) d[nav] = last.Plugin;
        return d;
    }
}
