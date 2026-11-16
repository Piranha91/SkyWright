using Mutagen.Bethesda.Plugins;

namespace Workbench;

/// <summary>The per-row comparison of a stored decision, in one place: the decisions state part calls it, and so does a
/// scene's arrival for the decided rows that scene draws, so the window's list is compared without a client.</summary>
internal static class DecisionCompare
{
    /// <summary>One decided row's standing: the row half first (no scene, no server); then, while it holds, the stored full
    /// comparison, or a fresh one against the open scene. Any failure to read the row's objects is that row's sentence.</summary>
    internal static async Task<DecisionMatch> RowAsync(IWorkbenchSurface s, Decision d, RankedRow r, Dictionary<string, DecisionMatch> cache, bool fresh = false)
    {
        var key = Decisions.RowKey(r.Row);
        var m = Decisions.CompareRow(d, s.RowStateOf(r));
        if (m.Standing == Standing.Reconsider) return m;
        if (!fresh && cache.TryGetValue(key, out var cached)) return cached;
        if (s.Scene is not { } sc) return m;
        try
        {
            var (row, objects, build) = await sc.DecisionInputsAsync(r);
            var full = Decisions.Compare(d, row, objects, build);
            cache[key] = full;
            return full;
        }
        catch (Exception ex) { cache.Remove(key); return new DecisionMatch(m.Standing, m.Sentence.TrimEnd('.') + " (" + ex.Message.TrimEnd('.') + ")."); }
    }

    /// <summary>Compare the decided rows whose reference the open scene draws, store their standings, and tell the list.</summary>
    internal static async Task SceneOpenedAsync(IWorkbenchSurface s, Dictionary<string, DecisionMatch> cache)
    {
        if (s.Scene is not { } sc || DecisionStore.For(s.WorkFolder) is not { } store || s.Binding is not { } b) return;
        bool any = false;
        foreach (var r in s.Listed)
        {
            if (!FormKey.TryFactory(r.Row.RefKey, out var fk) || !sc.Session.IsDrawn(fk)) continue;
            if (store.Find(b.Instance, b.Profile, Decisions.RowKey(r.Row)) is not { } d) continue;
            await RowAsync(s, d, r, cache, fresh: true);
            any = true;
        }
        if (any) ReviewList.RaiseChanged();
    }
}
