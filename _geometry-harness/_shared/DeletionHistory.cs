using Mutagen.Bethesda.Plugins;

namespace HarnessShared;

// THE DELETION HISTORY — which plugins state that a placed reference's record is deleted, in load
// order, selectable per VIEW (Q34, 2026-09-06).
//
// Why it exists. Every detector used to keep a `DeletedBy` map: reference -> the LAST plugin whose
// record carried the Deleted flag. It was then compared against the last plugin to CHANGE the
// placement, and if the deletion came at or after that step the reference was called deleted. Three
// different identities were being conflated by that one comparison:
//
//   - the last plugin to change a PLACEMENT (PlacementHistory's step, which drops an override that
//     forwards a position untouched, and never sees a record with no placement subrecord at all);
//   - the last plugin to DELETE (all the old map remembered);
//   - the plugin whose RECORD the view actually selects, which is neither of the first two.
//
// A deletion normally strips the placement subrecord, so it is invisible to the placement history; a
// later plugin that restores the reference at a position already in that history leaves no step
// either. The reference was then reported deleted for good, though the winning record says otherwise
// — and, in the other direction, a restoration that happened to move the reference hid the bug by
// adding a step. The fix is to select the record FIRST, per view, and read its Deleted flag second.
//
// Shape. Two structures, and the split is what keeps it small — the same one DeclaredEnableHistory
// uses for the enable facts, which is why that class now delegates its record-presence half here.
// PRESENCE is the owner's existing toucher list (every plugin whose record of the reference carried
// placement data) plus a short per-reference list of the records that carried none, which is how a
// deletion stays visible at all. VALUES are a CHANGE LOG: an entry only when a plugin's statement
// differs from the last logged one, with "no entry" meaning not deleted. A reference nobody ever
// deletes costs nothing, and the value at any plugin index is the last entry at or before it —
// correct because a contributing plugin whose statement differed would have logged one.
//
// The views this answers, and nothing else decides which one a caller wants:
//   WINNER        the last record in the evaluated plugin order.
//   AT p          the record in force at load position p.
//   IN VIEW v     the last record v or one of v's transitive masters provides — an earlier unrelated
//                 plugin is not eligible, and a plugin that forwarded a statement unchanged still
//                 resolves to the earlier value that statement carried.
//   WITHOUT m     the last record not by m and not by a plugin downstream of m — the counterfactual
//                 arm the contact and overlap passes rebuild.
//
// A runtime layer (Base Object Swapper) states nothing here on purpose: a flag or position override
// is not a record, so it can neither delete a reference nor restore one.

/// <summary>Per-reference deletion statements, selectable per view. Constructed with the owning index's
/// ref -> contributing-plugins map, which is the presence half of every view.</summary>
public sealed class DeletionHistory
{
    readonly IReadOnlyDictionary<FormKey, List<int>> _placed;
    readonly Dictionary<FormKey, List<int>> _recordOnly = new();
    readonly Dictionary<FormKey, List<(int Plugin, bool Deleted)>> _log = new();
    readonly HashSet<FormKey> _ever = new();

    /// <param name="placedTouchers">the owning index's ref -> contributing plugins map, in load order (every plugin
    /// whose record of the reference carried placement data).</param>
    public DeletionHistory(IReadOnlyDictionary<FormKey, List<int>> placedTouchers) => _placed = placedTouchers;

    /// <summary>References whose record carried the Deleted flag at SOME point in the order, restored or not. This is
    /// the historical count the probes have always printed as "refs deleted somewhere"; it is deliberately NOT the
    /// number deleted in any particular view.</summary>
    public int EverCount => _ever.Count;
    /// <summary>…and the same question for one reference.</summary>
    public bool Ever(FormKey key) => _ever.Contains(key);
    /// <summary>Records with no placement subrecord that were noted for presence (deletions, almost always).</summary>
    public int RecordOnly => _recordOnly.Count;
    /// <summary>How many references carry a value entry at all — the change log's size, not the order's.</summary>
    public int Tracked => _log.Count;

    /// <summary>Note one plugin's record of a reference, in load order. <paramref name="hasPlacement"/> false means the
    /// record carried no placement subrecord — <c>PlacementExtract</c> drops it, so the toucher list will not have it and
    /// this is the only thing that keeps it visible. Returns true when the statement CHANGED the log, so an owner can
    /// invalidate whatever it memoised from the old facts.</summary>
    public bool Note(int plugin, FormKey key, bool deleted, bool hasPlacement)
    {
        if (!hasPlacement)
        {
            if (!_recordOnly.TryGetValue(key, out var seen)) _recordOnly[key] = seen = new List<int>(1);
            if (seen.Count == 0 || seen[^1] != plugin) seen.Add(plugin);
        }
        if (deleted) _ever.Add(key);
        _log.TryGetValue(key, out var entries);
        // The same plugin can be noted twice for one reference — once from the placement extraction and once from the
        // major-record walk. The second note replaces the first rather than appending a step that no plugin took.
        if (entries is { Count: > 0 } && entries[^1].Plugin == plugin)
        {
            if (entries[^1].Deleted == deleted) return false;
            bool before = entries.Count >= 2 && entries[^2].Deleted;
            if (before == deleted) entries.RemoveAt(entries.Count - 1); else entries[^1] = (plugin, deleted);
            return true;
        }
        bool inForce = entries is { Count: > 0 } && entries[^1].Deleted;
        if (inForce == deleted) return false;                     // no entry at all means "not deleted"
        if (entries is null) _log[key] = entries = new List<(int Plugin, bool Deleted)>(1);
        entries.Add((plugin, deleted));
        return true;
    }

    /// <summary>The WINNER view: is the last record in the evaluated order a deletion?</summary>
    public bool AtWinner(FormKey key) => _log.TryGetValue(key, out var e) && e.Count > 0 && e[^1].Deleted;

    /// <summary>The record in force at load position <paramref name="at"/>: the last entry at or before it.</summary>
    public bool At(FormKey key, int at)
    {
        if (_log.TryGetValue(key, out var e))
            for (int i = e.Count - 1; i >= 0; i--)
                if (e[i].Plugin <= at) return e[i].Deleted;
        return false;
    }

    /// <summary>Which plugin's statement <see cref="At"/> resolved to, −1 when none did. Reported so an output can name
    /// the record provider rather than implying the eligible plugin stated it itself.</summary>
    public int ProviderAt(FormKey key, int at)
    {
        if (_log.TryGetValue(key, out var e))
            for (int i = e.Count - 1; i >= 0; i--)
                if (e[i].Plugin <= at) return e[i].Plugin;
        return -1;
    }

    /// <summary>The plugin whose deletion stands at the winner, −1 when the winning record is not a deletion. What a
    /// removal's mover is, in place of the old map's "last plugin that ever deleted it".</summary>
    public int WinningDeleter(FormKey key) => _log.TryGetValue(key, out var e) && e.Count > 0 && e[^1].Deleted ? e[^1].Plugin : -1;

    /// <summary>The highest plugin index <paramref name="viewer"/> can see a record of the reference at — itself or a
    /// plugin in its transitive master closure. −1 when the view never sees the reference at all.</summary>
    public int LastEligible(FormKey key, int viewer, IReadOnlyList<HashSet<int>> closure)
    {
        int best = -1;
        if (_placed.TryGetValue(key, out var t))
            for (int i = t.Count - 1; i >= 0; i--)
                if (t[i] == viewer || closure[viewer].Contains(t[i])) { best = t[i]; break; }
        if (_recordOnly.TryGetValue(key, out var r))
            for (int i = r.Count - 1; i >= 0; i--)
                if (r[i] == viewer || closure[viewer].Contains(r[i])) { if (r[i] > best) best = r[i]; break; }
        return best;
    }

    /// <summary>The IN VIEW question: is the last record <paramref name="viewer"/> or its transitive masters provide a
    /// deletion? False when the view never sees the reference — a missing record and a deleted one are different
    /// answers, and <see cref="KnownIn"/> is how a caller tells them apart.</summary>
    public bool InView(FormKey key, int viewer, IReadOnlyList<HashSet<int>> closure)
    {
        int at = LastEligible(key, viewer, closure);
        return at >= 0 && At(key, at);
    }

    /// <summary>Does ANY plugin carry a record of this reference — placed or record-only?</summary>
    public bool KnownAnywhere(FormKey key) => _placed.ContainsKey(key) || _recordOnly.ContainsKey(key) || _log.ContainsKey(key);

    /// <summary>Does <paramref name="viewer"/> see a record of this reference at all?</summary>
    public bool KnownIn(FormKey key, int viewer, IReadOnlyList<HashSet<int>> closure) => LastEligible(key, viewer, closure) >= 0;

    /// <summary>The highest plugin index in the arm WITHOUT <paramref name="mover"/>'s side — the last record by a plugin
    /// that is neither the mover nor downstream of it. −1 when every record is the mover's side.</summary>
    public int LastWithout(FormKey key, int mover, IReadOnlyList<HashSet<int>> closure)
    {
        int best = -1;
        if (_placed.TryGetValue(key, out var t))
            for (int i = t.Count - 1; i >= 0; i--)
            {
                int p = t[i];
                if (p == mover || closure[p].Contains(mover)) continue;
                best = p; break;
            }
        if (_recordOnly.TryGetValue(key, out var r))
            for (int i = r.Count - 1; i >= 0; i--)
            {
                int p = r[i];
                if (p == mover || closure[p].Contains(mover)) continue;
                if (p > best) best = p;
                break;
            }
        return best;
    }

    /// <summary>The WITHOUT view: is the last record outside <paramref name="mover"/>'s side a deletion? False when the
    /// arm has no record of the reference at all — the caller decides what an absent reference means there.</summary>
    public bool Without(FormKey key, int mover, IReadOnlyList<HashSet<int>> closure)
    {
        int at = LastWithout(key, mover, closure);
        return at >= 0 && At(key, at);
    }

    /// <summary>References that were deleted at some point and are NOT deleted at the winner — a later record restored
    /// them. The population the correction is about, exposed so a run can report it with a denominator rather than
    /// asserting the count is zero.</summary>
    public IEnumerable<FormKey> RestoredAtWinner()
    {
        foreach (var (key, e) in _log)
            if (e.Count > 0 && !e[^1].Deleted && _ever.Contains(key)) yield return key;
    }

    /// <summary>The deletion statements of one reference, in load order — for a report that must show the history it
    /// selected from rather than only its conclusion.</summary>
    public IReadOnlyList<(int Plugin, bool Deleted)>? Of(FormKey key) => _log.TryGetValue(key, out var e) ? e : null;
}
