using HarnessShared;
using HousecarlCore;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

// THE WALK THAT FEEDS floorgapprobe: every cell of every walked plugin, filed for the floor neighbourhood, and under
// --winning the one wanted cell merged over the load order. Held in its own class so --moved-ref-selftest runs exactly
// the code a real run runs; Program.cs keeps the base records and worldspace names in its own loop.
sealed class FloorgapWalk
{
    public readonly bool Winning;
    public readonly string? WantCell;
    public FormKey? WantKey;
    /// <summary>The floor filing (FloorFeed.FloorIndex) over every exterior cell.</summary>
    public readonly FloorFeed.FloorIndex Floor = new();
    public Dictionary<FormKey, Dictionary<(int, int), Dictionary<FormKey, PlacedRef>>> RefsByWorld => Floor.RefsByWorld;
    public Dictionary<FormKey, Dictionary<(int, int), float[,]>> LandByWorld => Floor.LandByWorld;
    public IEnumerable<(FormKey Ws, int X, int Y)> UnreadableLand => Floor.UnreadableLand;
    public readonly List<CellPlacements> Cells = new();
    // --winning: only the named cell is merged (refs and navmeshes by FormKey, a later plugin replacing an earlier one; the
    // meta from the first plugin that carries the cell, the LAND from the last that has one); the exterior neighbourhood is
    // filed from every plugin as the single-plugin path files it from one. A navmesh a later plugin DELETES outright is not
    // removed here (blockprobe's walk has the same simplification).
    public CellPlacements? MergedMeta;
    public float[,]? MergedLand;
    public readonly Dictionary<FormKey, PlacedRef> MergedRefs = new();
    public readonly Dictionary<FormKey, NavmeshGeometry> MergedNavs = new();
    public readonly List<string> PluginsWithCell = new();

    public FloorgapWalk(bool winning, string? wantCell)
    {
        Winning = winning; WantCell = wantCell;
        if (wantCell is not null) { try { WantKey = FormKey.Factory(wantCell); } catch { } }
    }

    /// <summary>Open one plugin. Without --winning an unreadable plugin throws (it is the whole input); with it, the plugin
    /// is skipped with a line on stderr, and null is returned.</summary>
    public ISkyrimModGetter? Open(string path)
    {
        try { return SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE); }
        catch (Exception ex) { if (!Winning) throw; Console.Error.WriteLine($"   skipped {Path.GetFileName(path)}: {ex.Message}"); return null; }
    }

    /// <summary>One plugin's cells, in load order. <paramref name="perCell"/> runs first for every cell.</summary>
    public void Plugin(ISkyrimModGetter mod, string path, Action<CellPlacements> perCell)
    {
        foreach (var cp in LastSighting.Cells(mod))
        {
            perCell(cp);
            Floor.Observe(cp, Path.GetFileName(path));
            // The single-plugin modes sweep the plugin's cells with content, as before: an empty cell copy has nothing to
            // measure.
            if (!Winning) { if (cp.Refs.Count > 0 || cp.Navmeshes.Count > 0) Cells.Add(cp); continue; }
            bool isWanted = (WantKey is { } wk && cp.Cell == wk) || (WantCell is not null && string.Equals(cp.CellEditorId, WantCell, StringComparison.OrdinalIgnoreCase));
            if (!isWanted) continue;
            WantKey ??= cp.Cell;
            MergedMeta ??= cp;
            if (cp.Land is not null) MergedLand = cp.Land;
            foreach (var r in cp.Refs) MergedRefs[r.FormKey] = r;
            foreach (var nv in cp.Navmeshes) MergedNavs[nv.FormKey] = nv;
            PluginsWithCell.Add(Path.GetFileName(path));
        }
    }

    /// <summary>HOUSECARL_WINSTATE_DUMP: the merged cell and the floor filing, for an independent reader.</summary>
    public void Dump()
    {
        if (!WinStateDump.Asked) return;
        WinStateDump.Write("floorgapprobe", new
        {
            winning = Winning, wantCell = WantKey?.ToString() ?? WantCell,
            merged = MergedRefs.Values.OrderBy(r => r.FormKey.ToString(), StringComparer.Ordinal).Select(r => WinStateDump.Entry(r, WantKey)).ToList(),
            mergedLand = MergedLand is { } ml ? new { h1616 = ml[16, 16] } : null,
            floor = RefsByWorld.Select(w => WinStateDump.Floor(w.Key, w.Value, Floor.LandOf(w.Key), Floor.UnreadableLand.Where(u => u.Ws == w.Key).Select(u => (u.X, u.Y)))).ToList(),
        });
    }
}
