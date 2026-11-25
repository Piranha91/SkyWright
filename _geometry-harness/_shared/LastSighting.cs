using HousecarlCore;
using SkyWright.Engine;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace HarnessShared;

// THE LAST-SIGHTING RULE, for every probe walk that decides which version of a placed reference it holds.
//
//   A reference's state is its LAST sighting in load order. A sighting is a placed record with placement data (in
//   CellPlacements.Refs) or a deletion that carries none (CellPlacements.DeletedRefs), which takes the reference out. A
//   walk admits a reference only if that last sighting passes the walk's own filter (a wanted cell, a target cell, a
//   position inside a bound); a later sighting anywhere else takes the earlier version out, so a move leaves no copy at the
//   old cell, grid or bucket.
//
// NAVMESHES follow the same rule (LastSightingNavSet): a navmesh's state is its last sighting; a sighting is a navmesh
// with geometry (CellPlacements.Navmeshes) or a record that carries none (CellPlacements.DeletedNavmeshes), and a sighting
// flagged deleted takes the navmesh out whether or not it kept its geometry (NavmeshGeometry.Deleted).
//
// A walk passes EVERY cell of EVERY plugin in load order (LastSighting.Cells), not only the cells its filter wants: a later
// sighting in a cell the filter rejects is exactly what must remove the earlier version. viewprobe's WinningRefWalk
// implements the same rule with the render's ring and persistent merge (viewprobe/WinningRefs.cs); FloorFeed.FloorIndex
// implements it for the floor filing.
public static class LastSighting
{
    /// <summary>Every cell of one plugin a last-sighting walk must see: with the deletion-only cells (a later plugin's
    /// deletion must take a reference or a navmesh out) and the cells with neither references nor navmeshes (a cell copy that carries
    /// only its LAND must reach the floor filing).</summary>
    public static IEnumerable<CellPlacements> Cells(ISkyrimModGetter mod) =>
        PlacementExtract.FromMod(mod, includeEmpty: true, includeDeletions: true);
}

/// <summary>The references a walk holds under the last-sighting rule, with the cell each last sighting stands in. The
/// only mutator is <see cref="Observe"/>.</summary>
public sealed class LastSightingSet
{
    /// <summary>ref -> its last sighting, for every reference whose last sighting the filter admitted.</summary>
    public readonly Dictionary<FormKey, PlacedRef> Refs = new();
    /// <summary>ref -> the cell of that sighting.</summary>
    public readonly Dictionary<FormKey, FormKey> CellOf = new();
    readonly HashSet<FormKey> _everAdmitted = new();
    /// <summary>References some sighting admitted and a later sighting took out (moved elsewhere or deleted).</summary>
    public readonly HashSet<FormKey> TakenOut = new();

    /// <summary>One cell of one plugin, in load order: each reference in it is admitted or taken out afresh; each deletion
    /// in it takes its reference out.</summary>
    public void Observe(CellPlacements cp, Func<CellPlacements, PlacedRef, bool> admit)
    {
        foreach (var r in cp.Refs)
        {
            if (admit(cp, r)) { Refs[r.FormKey] = r; CellOf[r.FormKey] = cp.Cell; _everAdmitted.Add(r.FormKey); TakenOut.Remove(r.FormKey); }
            else Out(r.FormKey);
        }
        if (cp.DeletedRefs is { Count: > 0 } deleted)
            foreach (var d in deleted) Out(d.FormKey);
    }

    void Out(FormKey k)
    {
        if (Refs.Remove(k)) CellOf.Remove(k);
        if (_everAdmitted.Contains(k)) TakenOut.Add(k);
    }
}

/// <summary>The navmeshes a walk holds under the last-sighting rule, with the cell each last sighting stands in. The only
/// mutator is <see cref="Observe"/>.</summary>
public sealed class LastSightingNavSet
{
    /// <summary>navmesh -> its last sighting, for every navmesh whose last sighting the filter admitted.</summary>
    public readonly Dictionary<FormKey, NavmeshGeometry> Navs = new();
    /// <summary>navmesh -> the cell of that sighting.</summary>
    public readonly Dictionary<FormKey, FormKey> CellOf = new();
    readonly HashSet<FormKey> _everAdmitted = new();
    /// <summary>Navmeshes some sighting admitted and a later sighting took out (moved elsewhere or deleted).</summary>
    public readonly HashSet<FormKey> TakenOut = new();

    /// <summary>One cell of one plugin, in load order: each navmesh in it is admitted or taken out afresh (a deleted one,
    /// even with its geometry, is taken out); each record in it without geometry takes its navmesh out. Returns the keys
    /// this cell touched, so a caller mirroring the set into its own table updates exactly those.</summary>
    public IEnumerable<FormKey> Observe(CellPlacements cp, Func<CellPlacements, NavmeshGeometry, bool> admit)
    {
        var touched = new List<FormKey>(cp.Navmeshes.Count);
        foreach (var n in cp.Navmeshes)
        {
            if (!n.Deleted && admit(cp, n)) { Navs[n.FormKey] = n; CellOf[n.FormKey] = cp.Cell; _everAdmitted.Add(n.FormKey); TakenOut.Remove(n.FormKey); }
            else Out(n.FormKey);
            touched.Add(n.FormKey);
        }
        if (cp.DeletedNavmeshes is { Count: > 0 } deleted)
            foreach (var d in deleted) { Out(d.FormKey); touched.Add(d.FormKey); }
        return touched;
    }

    void Out(FormKey k)
    {
        if (Navs.Remove(k)) CellOf.Remove(k);
        if (_everAdmitted.Contains(k)) TakenOut.Add(k);
    }
}
