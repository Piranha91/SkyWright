using HarnessShared;
using HousecarlCore;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

// CHECK 1's QUESTION — is the reference an alias is forced onto still there under the winning state? — together with
// the part of the walk that answers it (the placement history's records, and the plugin's major records). Held in its
// own class so --moved-ref-selftest runs exactly the code a real run runs; Program.cs keeps every other job of the walk.
sealed class AliasTargets
{
    public readonly PlacementHistory History;

    public AliasTargets(PlacementHistory history) { History = history; }

    /// <summary>The walk's cells of one plugin (Program.cs iterates exactly this).</summary>
    public static IEnumerable<CellPlacements> Cells(ISkyrimModGetter mod) => PlacementExtract.FromMod(mod);

    /// <summary>One placed reference of plugin <paramref name="p"/>, in load order.</summary>
    public void Placed(int p, CellPlacements cp, PlacedRef r) =>
        History.Record(r.FormKey, new Snap(p, r.Position, cp.Cell, r.InitiallyDisabled, r.Deleted, cp.Worldspace, r.Rotation, r.Scale));

    // Every placed record's own deleted flag, by the last plugin that has a record of it. A deletion usually carries no
    // placement data, so the placement history never sees it; the record pass does (the last-sighting rule,
    // _shared/LastSighting.cs: a deletion is a sighting that takes the reference out).
    readonly Dictionary<FormKey, (int Plugin, bool Deleted)> _lastRecord = new();

    /// <summary>One major record of plugin <paramref name="p"/>, in load order (the walk's record pass).</summary>
    public void Record(int p, IMajorRecordGetter rec)
    {
        if (rec is IPlacedGetter pg) _lastRecord[pg.FormKey] = (p, pg.IsDeleted);
    }

    /// <summary>MISSING when no plugin places the reference, DELETED (with the plugin) when the winning state deletes it,
    /// null when it stands.</summary>
    public (string Kind, int Plugin)? Break(FormKey refKey)
    {
        var w = History.Winning(refKey);
        // The last plugin with a record of it deleted it, and no later placement (the history's winner is not newer):
        // deleted, whether or not that record kept its placement.
        if (_lastRecord.TryGetValue(refKey, out var lr) && lr.Deleted && (w is null || lr.Plugin >= w.Value.Plugin))
            return ("DELETED", lr.Plugin);
        if (w is null) return ("MISSING", -1);
        if (w.Value.Deleted) return ("DELETED", w.Value.Plugin);
        return null;
    }

    /// <summary>HOUSECARL_WINSTATE_DUMP: every reference CHECK 1 would call deleted, for an independent reader.</summary>
    public void Dump(Func<int, string> pluginName)
    {
        if (!WinStateDump.Asked) return;
        var keys = History.Refs.Select(kv => kv.Key).Union(_lastRecord.Keys).ToList();
        WinStateDump.Write("driftprobe-check1", new
        {
            deleted = keys.Select(k => (k, b: Break(k))).Where(x => x.b is { Kind: "DELETED" })
                          .Select(x => new { formKey = x.k.ToString(), by = pluginName(x.b!.Value.Plugin) })
                          .OrderBy(x => x.formKey, StringComparer.Ordinal).ToList(),
        });
    }
}
