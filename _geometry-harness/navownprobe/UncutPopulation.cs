using HarnessShared;
using HousecarlCore;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

// THE UNCUT POPULATION'S INPUT: the obstructing references each plugin INTRODUCES into each cell, collected during the
// main walk and filtered once every plugin has been read. Held in its own class, with the walk's cell enumeration, so
// --moved-ref-selftest runs exactly the code a real run runs.
sealed class UncutPopulation
{
    /// <summary>The main walk's cells of one plugin (Program.cs iterates exactly this).</summary>
    public static IEnumerable<CellPlacements> Cells(ISkyrimModGetter mod) => PlacementExtract.FromMod(mod);

    // The UNCUT candidates BEFORE any enable filter — the winning enable state of a reference is not known until every
    // plugin has been read, so the filter is applied after the walk rather than during it (Q27).
    public readonly List<(FormKey Cell, int Plugin, FormKey Ref, FormKey Base)> Candidates = new();
    /// <summary>The candidates Resolve counted.</summary>
    public readonly List<(FormKey Cell, int Plugin, FormKey Ref)> Counted = new();

    /// <summary>One cell of plugin <paramref name="p"/> (whose own ModKey is <paramref name="modKey"/>), in load order.</summary>
    public void Note(CellPlacements cp, int p, ModKey modKey)
    {
        foreach (var r in cp.Refs)
        {
            if (r.Deleted) continue;
            if (r.FormKey.ModKey != modKey) continue;                  // an override is not an introduction
            if (r.Base is not { } bk) continue;
            Candidates.Add((cp.Cell, p, r.FormKey, bk));
        }
    }

    /// <summary>The population, once every plugin has spoken: an obstructing base, and the reference present at load under
    /// the run's population (Q27). The base type could not be tested during the walk either — a later plugin can define
    /// the base. Fills <paramref name="cellNewRefs"/>.</summary>
    public void Resolve(Func<FormKey, string?> baseTypeOf, ISet<string> obstructing, Func<FormKey, bool> enabled,
                        Dictionary<(FormKey Cell, int Plugin), List<FormKey>> cellNewRefs, out long skippedAbsent, out long skippedType)
    {
        skippedAbsent = 0; skippedType = 0;
        foreach (var (cell, plugin, refKey, baseKey) in Candidates)
        {
            if (baseTypeOf(baseKey) is not { } bt || !obstructing.Contains(bt)) { skippedType++; continue; }
            if (!enabled(refKey)) { skippedAbsent++; continue; }
            if (!cellNewRefs.TryGetValue((cell, plugin), out var bag)) cellNewRefs[(cell, plugin)] = bag = new List<FormKey>();
            bag.Add(baseKey);
            Counted.Add((cell, plugin, refKey));
        }
    }

    /// <summary>HOUSECARL_WINSTATE_DUMP: the candidates that were counted, for an independent reader.</summary>
    public void Dump()
    {
        if (!WinStateDump.Asked) return;
        WinStateDump.Write("navownprobe-uncut", new
        {
            candidates = Candidates.Select(c => new { cell = c.Cell.ToString(), plugin = c.Plugin, formKey = c.Ref.ToString(), @base = c.Base.ToString() }).ToList(),
            counted = Counted.Select(c => new { cell = c.Cell.ToString(), plugin = c.Plugin, formKey = c.Ref.ToString() }).ToList(),
        });
    }
}
