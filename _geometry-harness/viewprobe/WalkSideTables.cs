using HarnessShared;
using HousecarlCore;
using Mutagen.Bethesda.Plugins;

namespace ViewProbe;

// TWO SIDE TABLES THE RENDER'S LOAD-ORDER WALK FILLS beside the winning state (WinningRefs.cs): the --stations floor
// filing, and the "where is it" tables of watched NPCs (by reference and by base), watched doors and deletions. Held
// here so --moved-ref-selftest runs exactly the code the walk in Program.cs runs.
internal static class StationFloorFiling
{
    /// <summary>The floor neighbourhood, filed IN LOAD ORDER (a later plugin's ref of the same FormKey replaces an earlier
    /// one) — the LAND heightmaps with it, which is what stops a station on bare terrain being refused for want of a floor
    /// that is there. The worldspace's persistent cell has no grid of its own and its refs reach anywhere, so it is filed
    /// whole; a gridded cell is filed within reach of the target. Nothing here runs unless --stations asked for it.</summary>
    /// <summary>The walk's station floor filing: every cell of every plugin is observed, and a floor candidate or a LAND
    /// is filed when it stands in the target's worldspace within <paramref name="radius"/> grids of the target (by
    /// position). Null when --stations was not asked for.</summary>
    internal static FloorFeed.FloorIndex? Begin(bool stationsAsked, FormKey targetWorldspace, (int X, int Y)? targetGrid, int radius) =>
        !stationsAsked ? null : new FloorFeed.FloorIndex((ws, g) => ws == targetWorldspace
            && (targetGrid is not { } t || (Math.Abs(g.X - t.X) <= radius && Math.Abs(g.Y - t.Y) <= radius)));

    /// <summary>After the walk: the target worldspace's filing into the maps the station modes and the retained walk read.</summary>
    internal static void Finish(FloorFeed.FloorIndex? index, FormKey targetWorldspace,
                                Dictionary<(int, int), Dictionary<FormKey, PlacedRef>> refsByGrid, Dictionary<(int, int), float[,]> landByGrid,
                                Dictionary<(int, int), string> unreadableLand)
    {
        if (index is null) return;
        foreach (var (g, d) in index.RefsOf(targetWorldspace)) refsByGrid[g] = d;
        foreach (var (g, h) in index.LandOf(targetWorldspace)) landByGrid[g] = h;
        foreach (var (k, why) in index.UnreadableLandWhy) if (k.Ws == targetWorldspace) unreadableLand[(k.X, k.Y)] = why;
    }

    /// <summary>HOUSECARL_WINSTATE_DUMP: the station floor filing after the walk, for an independent reader.</summary>
    internal static void Dump(FormKey targetWorldspace, (int X, int Y)? targetGrid, int radius,
                              Dictionary<(int, int), Dictionary<FormKey, PlacedRef>> refsByGrid, Dictionary<(int, int), float[,]> landByGrid,
                              Dictionary<(int, int), string>? unreadableLand = null)
    {
        if (!WinStateDump.Asked) return;
        WinStateDump.Write("viewprobe-stations", new
        {
            worldspace = targetWorldspace.ToString(), targetX = targetGrid?.X, targetY = targetGrid?.Y, radius,
            floor = WinStateDump.Floor(targetWorldspace, refsByGrid, landByGrid, unreadableLand?.Keys.Select(k => (k.Item1, k.Item2))),
        });
    }
}

internal static class RefWhere
{
    internal sealed record Tables(
        Dictionary<FormKey, string> DeletedBy,
        Dictionary<FormKey, (FormKey Cell, string? CellEd, PlacedRef Ref)> NpcRef,
        Dictionary<FormKey, Dictionary<FormKey, (FormKey Cell, string? CellEd, PlacedRef Ref)>> NpcBase,
        Dictionary<FormKey, (FormKey Cell, string? CellEd, PlacedRef Ref)> Door);

    /// <summary>One cell of one plugin, in load order.</summary>
    internal static void Observe(CellPlacements cp, string plgName, Tables t, HashSet<FormKey> shotNpcWatch, HashSet<FormKey> doorWatchKeys, ModKey pluginKey)
    {
        // A later sighting undoes an earlier deletion; a deletion takes the ref out of every "where is it" table.
        foreach (var r in cp.Refs) t.DeletedBy.Remove(r.FormKey);
        if (cp.DeletedRefs is { Count: > 0 } deletedHere)
            foreach (var d in deletedHere)
            {
                t.DeletedBy[d.FormKey] = plgName;
                t.NpcRef.Remove(d.FormKey);
                t.Door.Remove(d.FormKey);
                foreach (var perRef in t.NpcBase.Values) perRef.Remove(d.FormKey);
            }
        if (shotNpcWatch.Count > 0)
            foreach (var r in cp.Refs)
            {
                if (r.RecordType != "PlacedNpc") continue;
                // The last sighting decides the base too: a later override that re-bases the reference takes it out of
                // every other base's entry.
                foreach (var (b, per) in t.NpcBase) if (r.Base is not { } rb || b != rb) per.Remove(r.FormKey);
                if (shotNpcWatch.Contains(r.FormKey))
                    t.NpcRef[r.FormKey] = (cp.Cell, cp.CellEditorId, r);         // later plugin wins
                if (r.Base is { } nb && shotNpcWatch.Contains(nb))
                {
                    if (!t.NpcBase.TryGetValue(nb, out var perRef)) t.NpcBase[nb] = perRef = new();
                    perRef[r.FormKey] = (cp.Cell, cp.CellEditorId, r);              // later plugin wins per ref
                }
            }
        if (doorWatchKeys.Count > 0)
            foreach (var r in cp.Refs)
                if (doorWatchKeys.Contains(r.FormKey))
                    { t.Door[r.FormKey] = (cp.Cell, cp.CellEditorId, r);     // later plugin wins
                      if (Environment.GetEnvironmentVariable("VP_DOOR_DEBUG") == "1")
                          Console.WriteLine($"   [debug] door {r.FormKey} in cp: Cell={cp.Cell} Grid={(cp.Grid is { } gg ? $"{gg.X},{gg.Y}" : "null")} Interior={cp.Interior} Ws={cp.Worldspace} plugin={pluginKey}"); }
    }
}

/// <summary>A station floor over a grid whose last LAND gives no heights: refused, never read from an earlier plugin's
/// heights or from none.</summary>
internal sealed class FloorLandUnreadable : ReadRefusal
{
    internal FloorLandUnreadable(int gx, int gy, string why)
        : base($"the station floor needs the LAND of grid square {gx},{gy}, which gives no heights ({why}). Refusing: an "
             + "earlier plugin's heights, or none, would be read in its place.", null) { }
}
