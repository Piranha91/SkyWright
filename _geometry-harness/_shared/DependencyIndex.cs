using HousecarlCore;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace HarnessShared;

// THE DECLARED-HOST INDEX — the I/O half of the placement-dependency pass (roadmap 4.5/4.6, the
// records layer). A placed reference can DECLARE that it belongs with another one: an enable parent
// (XESP), a linked reference (XLKR), an activate parent (XAPR). That edge is an authored statement,
// and when the host moves and the dependent does not, a records pass can say so without a mesh.
//
// Everything here is bookkeeping over the load order — placements through PlacementHistory, the
// edges each override carries, who set each edge, which refs are deleted, what the bases are. The
// DECISION is DetectorModel.PlacementDependency, owned by proofprobe; this class only assembles the
// facts it takes. Shared between depprobe (the real order) and proofprobe (a synthetic order written
// with Mutagen), so the proof exercises the same extraction the real run uses.
//
// The edge's AUTHOR is the last plugin to SET it: an override that forwards an edge unchanged keeps
// the original author; one that drops it removes the edge; one that re-adds it becomes the author.
// That is the plugin whose authoring view the rule compares against.
//
// Since Q32 (2026-09-06) the index also keeps a DECLARED ENABLE HISTORY (below), because whether the
// host is THERE when the cell loads is not its own initially-disabled checkbox: a reference with an
// XESP enable parent takes the parent's state, and the engine ignores its own flag. The rule needs
// that answer in each of the two configurations it compares, so the history is resolved per view.

/// <summary>One declared edge: <paramref name="Dependent"/> names <paramref name="Host"/> through
/// <paramref name="Kind"/>, last set by plugin <paramref name="Author"/>.</summary>
public readonly record struct HostEdge(FormKey Dependent, HostEdgeKind Kind, FormKey Host, int Author);

/// <summary>One plugin's statement of the enable-relevant facts of one placed reference: its own
/// initially-disabled flag, its XESP enable parent with the opposite-of-parent flag, and whether the
/// record carries the Deleted flag. A plugin that carries a record of a reference states all four,
/// whether or not it changed any of them.</summary>
public readonly record struct EnableStatement(int Plugin, bool Disabled, FormKey? Parent, bool Opposite, bool Deleted);

/// <summary>THE DECLARED ENABLE HISTORY — enough of every reference's enable facts, in load order, to answer
/// "was this thing there when the cell loaded" in an ARBITRARY view of the order, not just the winning one.
///
/// <para>Why it cannot come from <see cref="PlacementHistory"/>: that history records a plugin only when it
/// CHANGED a placement, and <see cref="Snap.SameAs"/> covers position, cell, the disabled flag and deletion —
/// but not the XESP parent. A parent-only or opposite-only override therefore leaves no step at all, and an
/// override that forwards a placement unchanged is dropped, which is exactly the version a sparse master
/// closure may be able to see when the original provider's is out of view.</para>
///
/// <para>Two structures, and the split is what keeps it small. PRESENCE — which plugins carry a record of a
/// reference at all — is the caller's existing toucher list (every plugin with placement data) plus a short
/// per-reference list of the records <c>PlacementExtract</c> drops for having no placement subrecord, which
/// is how a deleted parent stays visible. VALUES are a CHANGE LOG: an entry is appended only when a plugin's
/// statement differs from the last logged one, with "no entry" meaning the plain default (enabled, no parent,
/// not deleted). A reference nobody ever disables, parents or deletes costs nothing at all, and the value at
/// any plugin index is the last entry at or before it — correct because a contributing plugin whose statement
/// differed would have logged one.</para>
///
/// <para>Two views, two memo sets, and they must not share. WINNER: the last statement of each chain element,
/// including the Base Object Swapper pseudo-plugin's re-flags. AS-SEEN-BY p: for each chain element, the value
/// at the last plugin p or one of p's transitive masters contributed — the same eligibility set as
/// <see cref="PlacementHistory.AsSeenBy"/>, so the two views of one row agree about what its author could see.
/// Different authors get different memos even when they share a host.</para>
///
/// <para>The engine rule itself is <see cref="EnableState.Resolve"/> and is NOT restated here; this class only
/// decides which facts each view hands it. A reference the view does not place, one deleted in that view, and
/// a cycle all come back through <see cref="EnableResolution.Resolved"/> = false, which the caller counts
/// rather than trusts.</para></summary>
public sealed class DeclaredEnableHistory
{
    readonly IReadOnlyDictionary<FormKey, List<int>> _placed;
    readonly Dictionary<FormKey, List<EnableStatement>> _log = new();
    readonly Dictionary<FormKey, List<int>> _recordOnly = new();
    readonly Dictionary<FormKey, EnableResolution> _winnerMemo = new();
    readonly Dictionary<int, Dictionary<FormKey, EnableResolution>> _viewMemo = new();
    readonly Dictionary<int, Func<FormKey, EnableFacts?>> _viewFacts = new();
    IReadOnlyList<HashSet<int>>? _closure;

    /// <param name="placedTouchers">the owning index's ref -> contributing plugins map (every plugin whose record
    /// of the reference carried placement data), used as the presence half of a view.</param>
    public DeclaredEnableHistory(IReadOnlyDictionary<FormKey, List<int>> placedTouchers) => _placed = placedTouchers;

    /// <summary>References whose winning statement a Base Object Swapper property override re-flags WHILE the
    /// reference also carries an XESP parent. The engine ignores a parented reference's own flag, and whether a
    /// runtime flag override is ignored the same way has not been measured — so these resolve UNRESOLVED and are
    /// counted here rather than answered. Empty whenever BOS is off or no such reference exists.</summary>
    public HashSet<FormKey> BosReflaggedUnderParent { get; } = new();

    /// <summary>How many references carry a value entry at all — the change log's size, not the order's.</summary>
    public int Tracked => _log.Count;
    /// <summary>Records with no placement subrecord that were noted for presence (deletions, almost always).</summary>
    public int RecordOnly => _recordOnly.Count;

    /// <summary>Winner-view references the chain could not be read to the end for: a parent nothing places, a
    /// parent deleted at the winner, a cycle, or a BOS re-flag under a parent. Derived from the memo, so counting
    /// costs nothing and cannot change an answer.</summary>
    public int WinnerUnresolved { get { int n = 0; foreach (var r in _winnerMemo.Values) if (!r.Resolved) n++; return n; } }
    /// <summary>Winner-view references the chain resolves as absent when the cell loads.</summary>
    public int WinnerAbsent { get { int n = 0; foreach (var r in _winnerMemo.Values) if (!r.Enabled) n++; return n; } }
    /// <summary>How many references the winner view was asked about, chain elements included — the denominator
    /// the two counts above belong to.</summary>
    public int WinnerQueried => _winnerMemo.Count;
    /// <summary>How many distinct authoring views were built (one per edge author that reached the chain).</summary>
    public int ViewsBuilt => _viewMemo.Count;
    /// <summary>References asked about across every authoring view, chain elements included and counted once per view.</summary>
    public int ViewQueried { get { int n = 0; foreach (var m in _viewMemo.Values) n += m.Count; return n; } }
    /// <summary>…of those, how many the chain could not be read to the end for.</summary>
    public int ViewUnresolved { get { int n = 0; foreach (var m in _viewMemo.Values) foreach (var r in m.Values) if (!r.Resolved) n++; return n; } }
    /// <summary>…and how many resolve as absent in the view that asked.</summary>
    public int ViewAbsent { get { int n = 0; foreach (var m in _viewMemo.Values) foreach (var r in m.Values) if (!r.Enabled) n++; return n; } }

    /// <summary>Note one plugin's record of a reference, in load order. <paramref name="hasPlacement"/> false means
    /// the record carried no placement subrecord (a deletion, normally) and is remembered for presence alone —
    /// <c>PlacementExtract</c> drops it, so the toucher list will not have it.</summary>
    public void Note(int plugin, FormKey key, bool disabled, FormKey? parent, bool opposite, bool deleted, bool hasPlacement)
    {
        if (!hasPlacement)
        {
            if (!_recordOnly.TryGetValue(key, out var seen)) _recordOnly[key] = seen = new List<int>(1);
            if (seen.Count == 0 || seen[^1] != plugin) seen.Add(plugin);
        }
        var last = Latest(key);
        if (last.Disabled == disabled && last.Parent == parent && last.Opposite == opposite && last.Deleted == deleted) return;
        if (!_log.TryGetValue(key, out var entries)) _log[key] = entries = new List<EnableStatement>(1);
        if (entries.Count > 0 && entries[^1].Plugin == plugin) entries[^1] = new EnableStatement(plugin, disabled, parent, opposite, deleted);
        else entries.Add(new EnableStatement(plugin, disabled, parent, opposite, deleted));
        Invalidate();
    }

    /// <summary>Note the Base Object Swapper layer's effect on a reference's own initially-disabled flag, under the
    /// pseudo-plugin index the caller reserved after the last real plugin. It lands in the winner view only: a real
    /// plugin's master closure never contains the pseudo-plugin, so no authoring view can see it.</summary>
    public void NoteBos(int bosPlugin, FormKey key, bool disabled)
    {
        var last = Latest(key);
        if (last.Disabled == disabled) return;                       // the override left the flag alone
        if (last.Parent is not null) BosReflaggedUnderParent.Add(key);
        Note(bosPlugin, key, disabled, last.Parent, last.Opposite, last.Deleted, hasPlacement: true);
    }

    /// <summary>Drop every resolved answer. Called whenever a statement is added, so a value computed during
    /// ingestion or before the runtime layer was applied can never survive into a judgement.</summary>
    public void Invalidate()
    {
        if (_winnerMemo.Count > 0) _winnerMemo.Clear();
        if (_viewMemo.Count > 0) _viewMemo.Clear();
    }

    /// <summary>The last statement anyone made about a reference — the plain default when nobody ever changed it
    /// from one, which is most of the order.</summary>
    public EnableStatement Latest(FormKey key)
        => _log.TryGetValue(key, out var e) && e.Count > 0 ? e[^1] : new EnableStatement(-1, false, null, false, false);

    /// <summary>The statement in force at plugin index <paramref name="at"/>: the last entry at or before it.</summary>
    public EnableStatement StatementAt(FormKey key, int at)
    {
        if (_log.TryGetValue(key, out var e))
            for (int i = e.Count - 1; i >= 0; i--)
                if (e[i].Plugin <= at) return e[i];
        return new EnableStatement(-1, false, null, false, false);
    }

    /// <summary>Does any plugin place or carry a record of this reference at all?</summary>
    public bool KnownAnywhere(FormKey key) => _placed.ContainsKey(key) || _recordOnly.ContainsKey(key) || _log.ContainsKey(key);

    /// <summary>The highest plugin index <paramref name="viewer"/> can see a record of the reference at — itself or a
    /// plugin in its transitive master closure. −1 when the view never sees the reference placed or overridden.</summary>
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

    /// <summary>The winning facts of one reference for <see cref="EnableState"/>: null when nothing places it, when
    /// the winning statement deletes it, or when the BOS/parent interaction above makes the answer unmeasured.</summary>
    public EnableFacts? WinnerFacts(FormKey key)
    {
        if (!KnownAnywhere(key)) return null;
        if (BosReflaggedUnderParent.Contains(key)) return null;
        var s = Latest(key);
        return s.Deleted ? null : new EnableFacts(s.Disabled, s.Parent, s.Opposite);
    }

    /// <summary>The facts of one reference as <paramref name="viewer"/> could see them: null when the view never
    /// sees it, or when the version it sees is a deletion.</summary>
    public EnableFacts? ViewFacts(FormKey key, int viewer, IReadOnlyList<HashSet<int>> closure)
    {
        int at = LastEligible(key, viewer, closure);
        if (at < 0) return null;
        var s = StatementAt(key, at);
        return s.Deleted ? null : new EnableFacts(s.Disabled, s.Parent, s.Opposite);
    }

    /// <summary>The reference's runtime enable state in the winning order, chain and all.</summary>
    public EnableResolution Winner(FormKey key) => EnableState.Resolve(key, WinnerFacts, _winnerMemo);

    /// <summary>The reference's runtime enable state as <paramref name="viewer"/> could see it — every hop of the
    /// chain read in the SAME view, never mixed with the winner's. Memoised per viewer.</summary>
    public EnableResolution AsSeenBy(FormKey key, int viewer, IReadOnlyList<HashSet<int>> closure)
    {
        if (!ReferenceEquals(_closure, closure)) { _closure = closure; _viewFacts.Clear(); Invalidate(); }
        if (!_viewMemo.TryGetValue(viewer, out var memo)) _viewMemo[viewer] = memo = new Dictionary<FormKey, EnableResolution>();
        if (!_viewFacts.TryGetValue(viewer, out var facts))
            _viewFacts[viewer] = facts = k => ViewFacts(k, viewer, closure);
        return EnableState.Resolve(key, facts, memo);
    }
}

/// <summary>An edge with the facts assembled for the rule and the snaps behind them, for the report.
/// Mover is the plugin that last set the host's placement (-1 when the host has none). The last three are
/// report-side facts the rule does not read: Dependent is what the dependent IS (a row is support-shaped only
/// when host and dependent are both Physical); AuthoredOffset is how far apart host and dependent stood in
/// the author's view (NaN without one) — an object resting on another is within its footprint, so a declared
/// edge over a long offset is an AI or control link whatever the record types say; and
/// DependentTouchedByMoverSide says the mover or a plugin downstream of it carries an override of the
/// dependent's record AT ALL, moved or not — the rule credits only a re-placement (the handoff's rule 3), so
/// this is reported beside it, as awareness evidence the frozen rule does not fold.</summary>
/// <param name="HostStateThen">the host's runtime enable state as the edge's author could see it (Q32) — the
/// chain resolved over the versions that author or its transitive masters provide.</param>
/// <param name="HostStateNow">the same in the winning order, the Base Object Swapper layer included.</param>
public sealed record DependencyRow(HostEdge Edge, DetectorModel.DependencyFacts Facts,
                                   Snap? HostThen, Snap? HostNow, Snap? DepThen, Snap? DepNow,
                                   int Mover, FormKey? HostBase, DependentClass Dependent,
                                   float AuthoredOffset, bool DependentTouchedByMoverSide,
                                   EnableResolution HostStateThen, EnableResolution HostStateNow);

public sealed class DependencyIndex
{
    public PlacementHistory History { get; } = new();
    /// <summary>ref -> its winning base record (winner wins, like the position).</summary>
    public Dictionary<FormKey, FormKey> RefBase { get; } = new();
    /// <summary>ref -> its placed arm ("PlacedObject", "PlacedNpc", …) and editor id, winner wins.</summary>
    public Dictionary<FormKey, (string Type, string? Edid)> RefInfo { get; } = new();
    /// <summary>base record -> its Mutagen type name and editor id, winner wins. Modeled records plus NPCs.</summary>
    public Dictionary<FormKey, (string Type, string? Edid)> BaseInfo { get; } = new();
    public Dictionary<FormKey, (string? Edid, bool Interior)> CellName { get; } = new();
    /// <summary>ref -> the latest plugin whose record of it carries the Deleted flag. A deleted reference usually
    /// loses its placement subrecord, which makes it invisible to PlacementExtract — so deletions are read off the
    /// major-record walk instead, or a deleted host would read as "still where it was".</summary>
    public Dictionary<FormKey, int> DeletedBy { get; } = new();
    /// <summary>ref -> every plugin carrying a record of it, in load order, whether or not the placement
    /// changed (PlacementHistory records changes only). What "the mover's side overrode it" is read from.</summary>
    public Dictionary<FormKey, List<int>> Touchers { get; } = new();
    public long Placements { get; private set; }
    /// <summary>ref -> the cell and worldspace of its winning placement — what the Base Object Swapper reader's
    /// filters read (BosHook).</summary>
    public Dictionary<FormKey, (FormKey Cell, FormKey Worldspace)> RefCell { get; } = new();
    /// <summary>The Base Object Swapper form index, filled during <see cref="Ingest"/> when set before the walk.</summary>
    public BosFormIndex? Bos { get; set; }
    /// <summary>ref -> its RECORD base, kept when BosHook replaced <see cref="RefBase"/>. Empty when BOS was not applied.</summary>
    public Dictionary<FormKey, FormKey> RecordBase { get; } = new();
    public Dictionary<FormKey, BaseObjectSwaps.BosResolution> BosSwaps { get; } = new();
    public int BosPlugin { get; set; } = -1;

    readonly Dictionary<(FormKey Dep, HostEdgeKind Kind, FormKey Host), int> _author = new();
    readonly Dictionary<FormKey, List<(HostEdgeKind Kind, FormKey Host)>> _current = new();

    /// <summary>The enable facts of every reference, per view (Q32) — what <see cref="Judge"/> asks instead of
    /// reading the host's own initially-disabled checkbox, which the engine ignores under an XESP parent.</summary>
    public DeclaredEnableHistory Enable { get; }

    public DependencyIndex() => Enable = new DeclaredEnableHistory(Touchers);

    /// <summary>Every edge the WINNING record of each dependent still carries, with its author.</summary>
    public IEnumerable<HostEdge> Edges => _author.Select(kv => new HostEdge(kv.Key.Dep, kv.Key.Kind, kv.Key.Host, kv.Value));

    public int EdgeCount => _author.Count;

    /// <summary>Read one plugin, in load order: placements and edges through PlacementExtract, then one walk of the
    /// major records for deletions and base-record names.</summary>
    public void Ingest(int plugin, ISkyrimModGetter mod)
    {
        foreach (var cp in PlacementExtract.FromMod(mod))
        {
            CellName[cp.Cell] = (cp.CellEditorId, cp.Interior);
            Bos?.NoteCell(cp.Cell, cp.Worldspace);
            foreach (var r in cp.Refs) Record(plugin, cp.Cell, cp.Worldspace, r);
        }
        foreach (var rec in mod.EnumerateMajorRecords())
        {
            Bos?.Note(rec);
            if (rec is IPlacedGetter pg)
            {
                if (pg.IsDeleted) DeletedBy[pg.FormKey] = plugin;
                // A record with no placement subrecord — a deletion, normally — never reaches Record, so its enable
                // statement is taken here instead. Q32 needs it: a deleted parent must go absent from the views it
                // is deleted in and come back in the ones it is not, and a latest-only deletion map cannot say that.
                if (pg.Placement is null) NoteEnable(plugin, pg, hasPlacement: false);
                continue;
            }
            if (rec is INpcGetter npc) BaseInfo[npc.FormKey] = ("Npc", npc.EditorID);
            else if (rec is IModeledGetter && rec is ISkyrimMajorRecordGetter sm)
                BaseInfo[sm.FormKey] = (rec.GetType().Name.Replace("BinaryOverlay", ""), sm.EditorID);
        }
    }

    /// <summary>One plugin's statement of a placed record's enable facts, straight off the placed interface — XESP and
    /// the initially-disabled flag live on <see cref="IPlacedGetter"/> itself (every arm), so this reads the same values
    /// <c>PlacementExtract</c> does without going through it.</summary>
    void NoteEnable(int plugin, IPlacedGetter pg, bool hasPlacement)
    {
        bool disabled = pg is ISkyrimMajorRecordGetter smr
                     && smr.SkyrimMajorRecordFlags.HasFlag(SkyrimMajorRecord.SkyrimMajorRecordFlag.InitiallyDisabled);
        FormKey? parent = null;
        bool opposite = false;
        if (pg.EnableParent is { } ep && !ep.Reference.FormKey.IsNull)
        {
            parent = ep.Reference.FormKey;
            opposite = ep.Flags.HasFlag(EnableParent.Flag.SetEnableStateToOppositeOfParent);
        }
        Enable.Note(plugin, pg.FormKey, disabled, parent, opposite, pg.IsDeleted, hasPlacement);
    }

    /// <summary>One placed record from one plugin. <paramref name="worldspace"/> is FormKey.Null for an interior.
    /// Exposed so a proof can feed extracted refs directly.</summary>
    public void Record(int plugin, FormKey cell, FormKey worldspace, PlacedRef r)
    {
        Placements++;
        Enable.Note(plugin, r.FormKey, r.InitiallyDisabled, r.EnableParent, r.EnableParentOpposite, r.Deleted, hasPlacement: true);
        RefCell[r.FormKey] = (cell, worldspace);
        Bos?.NotePlaced(r.FormKey, r.Persistent);
        // Rotation and scale ride along (not part of SameAs, so no step changes) — the BOS hook's rotation/scale-only
        // tally then compares an override against the record's values rather than against zero and one (2026-09-04).
        History.Record(r.FormKey, new Snap(plugin, r.Position, cell, r.InitiallyDisabled, r.Deleted, worldspace, r.Rotation, r.Scale));
        if (!Touchers.TryGetValue(r.FormKey, out var touchers)) Touchers[r.FormKey] = touchers = new List<int>(1);
        if (touchers.Count == 0 || touchers[^1] != plugin) touchers.Add(plugin);
        if (r.Base is { } b) RefBase[r.FormKey] = b;
        RefInfo[r.FormKey] = (r.RecordType, r.EditorId ?? (RefInfo.TryGetValue(r.FormKey, out var prev) ? prev.Edid : null));

        var now = new List<(HostEdgeKind Kind, FormKey Host)>();
        if (r.EnableParent is { } ep) now.Add((HostEdgeKind.EnableParent, ep));
        if (r.LinkedReferences is { } links) foreach (var l in links) now.Add((HostEdgeKind.LinkedRef, l.Reference));
        if (r.ActivateParents is { } aps) foreach (var a in aps) now.Add((HostEdgeKind.ActivateParent, a.Reference));

        _current.TryGetValue(r.FormKey, out var before);
        foreach (var e in now)
            if (before is null || !before.Contains(e)) _author[(r.FormKey, e.Kind, e.Host)] = plugin;
        if (before is not null)
            foreach (var e in before)
                if (!now.Contains(e)) _author.Remove((r.FormKey, e.Kind, e.Host));
        if (now.Count > 0 || before is not null) _current[r.FormKey] = now;
    }

    /// <summary>Assemble the rule's facts for one edge against <paramref name="closure"/> (DetectorModel.MasterClosure
    /// over the same plugin indices). The verdict is the caller's: DetectorModel.PlacementDependency.</summary>
    public DependencyRow Judge(HostEdge e, IReadOnlyList<HashSet<int>> closure)
    {
        var host = e.Host;
        var dep = e.Dependent;
        var hostNow = History.Winning(host);
        bool hostResolved = hostNow is not null || DeletedBy.ContainsKey(host);
        bool hostDeleted = (hostNow?.Deleted ?? false)
                        || (DeletedBy.TryGetValue(host, out var hd) && hd >= (hostNow?.Plugin ?? -1));
        var hostThen = History.AsSeenBy(host, e.Author, closure);
        var depThen = History.AsSeenBy(dep, e.Author, closure);
        var depNow = History.Winning(dep);

        bool views = hostThen is not null && hostNow is not null && depThen is not null && depNow is not null;
        float hostMoved = views ? PlacementHistory.Dist(hostThen!.Value.Pos, hostNow!.Value.Pos) : float.NaN;
        // SPACE, not cell: the cell for an interior, the worldspace for an exterior. Outdoors a persistent ref
        // sits in the worldspace's persistent cell whatever its coordinates, so the cell test would call a
        // persistent host beside a temporary dependent "different cells".
        bool sameSpaceThen = views && hostThen!.Value.Space == depThen!.Value.Space;
        bool hostChangedSpace = views && hostNow!.Value.Space != hostThen!.Value.Space;
        float authoredOffset = views && sameSpaceThen ? PlacementHistory.Dist(hostThen!.Value.Pos, depThen!.Value.Pos) : float.NaN;
        // Q32: whether the host is THERE when the cell loads, resolved through its XESP chain in EACH compared
        // configuration — the edge author's view and the winning order — rather than read off its own checkbox,
        // which the engine ignores wherever a reference has a parent. Absent in either view withholds the row the
        // way the disabled flag used to; a chain that cannot be read to the end withholds it under its own reason,
        // because the resolver's fallback is a stated default and no verdict may rest on one.
        var stateThen = Enable.AsSeenBy(host, e.Author, closure);
        var stateNow = Enable.Winner(host);
        bool hostAbsent = views && (!stateThen.Enabled || !stateNow.Enabled);
        bool hostStateUnresolved = views && (!stateThen.Resolved || !stateNow.Resolved);
        bool depDeleted = (depNow?.Deleted ?? false)
                       || (DeletedBy.TryGetValue(dep, out var dd) && dd >= (depNow?.Plugin ?? -1));
        int mover = hostNow?.Plugin ?? -1;
        bool depRePlaced = views && !depNow!.Value.SameAs(depThen!.Value);
        bool byMoverSide = views && (depNow!.Value.Plugin == mover || closure[depNow.Value.Plugin].Contains(mover));
        bool depTouched = mover >= 0 && Touchers.TryGetValue(dep, out var tl)
                       && tl.Any(t => t == mover || closure[t].Contains(mover));

        FormKey? hostBase = RefBase.TryGetValue(host, out var hb) ? hb : null;
        var hostClass = hostBase is { } hbk && BaseInfo.TryGetValue(hbk, out var bi)
            ? DetectorModel.ClassifyHost(bi.Type, bi.Edid)
            : DetectorModel.ClassifyHost(null, null);
        var depInfo = RefInfo.TryGetValue(dep, out var ri) ? ri : ("?", null);
        var depBase = RefBase.TryGetValue(dep, out var db) && BaseInfo.TryGetValue(db, out var dbi) ? dbi : ((string?)null, (string?)null);
        var depClass = DetectorModel.ClassifyDependent(depInfo.Item1, depBase.Item1, depBase.Item2);
        // The player reference is an engine constant with no placement record; matched on the raw id, as
        // driftprobe does — a plugin with an unusual master list renders 0x14 against its own ModKey.
        bool hostIsPlayer = host.ID == 0x14;

        var facts = new DetectorModel.DependencyFacts(e.Kind, hostIsPlayer, hostResolved, hostDeleted, hostAbsent, hostStateUnresolved,
            sameSpaceThen, hostChangedSpace, hostMoved, depDeleted, depRePlaced, byMoverSide, hostClass);
        return new DependencyRow(e, facts, hostThen, hostNow, depThen, depNow, mover, hostBase, depClass, authoredOffset, depTouched,
                                 stateThen, stateNow);
    }
}
