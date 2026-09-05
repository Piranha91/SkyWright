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

/// <summary>One declared edge: <paramref name="Dependent"/> names <paramref name="Host"/> through
/// <paramref name="Kind"/>, last set by plugin <paramref name="Author"/>.</summary>
public readonly record struct HostEdge(FormKey Dependent, HostEdgeKind Kind, FormKey Host, int Author);

/// <summary>An edge with the facts assembled for the rule and the snaps behind them, for the report.
/// Mover is the plugin that last set the host's placement (-1 when the host has none). The last three are
/// report-side facts the rule does not read: Dependent is what the dependent IS (a row is support-shaped only
/// when host and dependent are both Physical); AuthoredOffset is how far apart host and dependent stood in
/// the author's view (NaN without one) — an object resting on another is within its footprint, so a declared
/// edge over a long offset is an AI or control link whatever the record types say; and
/// DependentTouchedByMoverSide says the mover or a plugin downstream of it carries an override of the
/// dependent's record AT ALL, moved or not — the rule credits only a re-placement (the handoff's rule 3), so
/// this is reported beside it, as awareness evidence the frozen rule does not fold.</summary>
public sealed record DependencyRow(HostEdge Edge, DetectorModel.DependencyFacts Facts,
                                   Snap? HostThen, Snap? HostNow, Snap? DepThen, Snap? DepNow,
                                   int Mover, FormKey? HostBase, DependentClass Dependent,
                                   float AuthoredOffset, bool DependentTouchedByMoverSide);

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
                continue;
            }
            if (rec is INpcGetter npc) BaseInfo[npc.FormKey] = ("Npc", npc.EditorID);
            else if (rec is IModeledGetter && rec is ISkyrimMajorRecordGetter sm)
                BaseInfo[sm.FormKey] = (rec.GetType().Name.Replace("BinaryOverlay", ""), sm.EditorID);
        }
    }

    /// <summary>One placed record from one plugin. <paramref name="worldspace"/> is FormKey.Null for an interior.
    /// Exposed so a proof can feed extracted refs directly.</summary>
    public void Record(int plugin, FormKey cell, FormKey worldspace, PlacedRef r)
    {
        Placements++;
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
        bool hostDisabled = views && (hostThen!.Value.Disabled || hostNow!.Value.Disabled);
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

        var facts = new DetectorModel.DependencyFacts(e.Kind, hostIsPlayer, hostResolved, hostDeleted, hostDisabled,
            sameSpaceThen, hostChangedSpace, hostMoved, depDeleted, depRePlaced, byMoverSide, hostClass);
        return new DependencyRow(e, facts, hostThen, hostNow, depThen, depNow, mover, hostBase, depClass, authoredOffset, depTouched);
    }
}
