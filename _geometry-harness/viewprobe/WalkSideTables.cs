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
    internal static void Observe(CellPlacements cp, bool stationsAsked, FormKey targetWorldspace, (int X, int Y)? targetGrid, int radius,
                                 Dictionary<(int, int), Dictionary<FormKey, PlacedRef>> refsByGrid, Dictionary<(int, int), float[,]> landByGrid)
    {
        if (stationsAsked && !cp.Interior && cp.Worldspace == targetWorldspace
            && (cp.Grid is not { } cgS || targetGrid is not { } tgS
                || (Math.Abs(cgS.X - tgS.X) <= radius
                    && Math.Abs(cgS.Y - tgS.Y) <= radius)))
            FloorFeed.FileByGrid(cp, refsByGrid, landByGrid);
    }

    /// <summary>HOUSECARL_WINSTATE_DUMP: the station floor filing after the walk, for an independent reader.</summary>
    internal static void Dump(FormKey targetWorldspace, (int X, int Y)? targetGrid, int radius,
                              Dictionary<(int, int), Dictionary<FormKey, PlacedRef>> refsByGrid, Dictionary<(int, int), float[,]> landByGrid)
    {
        if (!WinStateDump.Asked) return;
        WinStateDump.Write("viewprobe-stations", new
        {
            worldspace = targetWorldspace.ToString(), targetX = targetGrid?.X, targetY = targetGrid?.Y, radius,
            floor = WinStateDump.Floor(targetWorldspace, refsByGrid, landByGrid),
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
