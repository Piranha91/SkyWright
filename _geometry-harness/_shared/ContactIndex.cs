using System.Numerics;
using HousecarlCore;
using Mutagen.Bethesda.Plugins;

namespace HarnessShared;

// THE CONTACT INDEX — the I/O half of the placement-contact pass (roadmap 4.5 / 4.6, the geometry half;
// charter milestone 8 over milestone 6's primitive).
//
// Everything here is bookkeeping and geometry plumbing over OverlapIndex's history: which references
// can be hosts (physical, furniture- or architecture-scale, and MOVED — or removed — by the last plugin
// to touch their placement), which can be dependents (physical, clutter- or furniture-scale, not a
// marker, not FX), the world boxes at each snap, the two spatial hashes (every dependent snap for the
// authoring arm; every physical winner for the winner arm), the world soups and host grids, and the
// facts for one (dependent, host) pair in both arms. The DECISION is DetectorModel.PlacementContact,
// owned by proofprobe; the contact test is SpatialOverlap.Contact, owned by crossprobe. Shared between
// contactprobe (the real order, meshes through the VFS) and proofprobe (a synthetic order written with
// Mutagen, OBND boxes turned into box soups), so the proof exercises the same extraction, the same arm
// construction and the same primitive the live run uses.
//
// The game boundary talked back on 2026-09-03 (the in-game arm on the lab's JK's–OCW pair) and four
// folds followed, each measured against that arm's verdicts:
//   R1  a reference EXISTS in an arm when its runtime enable state resolves enabled there — its own
//       initially-disabled flag when it has no XESP parent, the parent chain's state otherwise
//       (EnableState.Resolve, shared with viewprobe), resolved PER ARM: the winner's flags and parents,
//       or those of the arm without a mover. Hosts, dependents and the winner-arm population all read
//       it; a host can leave the world through a parent a later plugin disabled (the second Removed).
//   R2  a base is a marker — neither host, dependent, re-supporter nor Sunk target — by the record's
//       Is-Marker header flag, the engine's own rule; the editor-id and mesh-name heuristics are counted
//       against it (AuditMarkers) and no longer used.
//   R3  the volume cap gates what can be a MOVED host; anything with solid collision can re-support.
//   R4  the winner arm reports how wide the thing a penetration was measured against is, along the
//       contact normal, so the rule can refuse a depth no interior point could have.
//
// Standing rules applied: measured geometry first, OBND as a LABELLED fallback, no geometry = not judged,
// counted (rule 6); the geometry CHOICE (collision or render) labelled on every row; the floor measured
// before any count is read; nothing verified in game beyond the four folds' acceptance set.

/// <summary>One base record's geometry as the contact pass uses it. <paramref name="HostSoup"/> is the surface a thing
/// rests on or against — the solid collision bodies on the layers an actor (and a dropped item) meets; <paramref name="DepSoup"/>
/// is the dependent's own solid — every solid body but NONCOLLIDABLE; a dependent falls back to the render soup,
/// labelled. <paramref name="HostSrc"/> / <paramref name="DepSrc"/>: 'c' collision, 'r' render (the fallback), 'o' a
/// box built from OBND, '-' nothing. <paramref name="HostUsable"/>: this geometry may HOLD something — true only for a
/// closed, outward-wound solid (real collision; the proof's box soups). A render soup or an OBND box never hosts: in
/// the engine nothing rests on a mesh without collision, and the inside test needs a solid to be inside of (the
/// labelled set measured render and box hosts reading "sunk" 85–87 % of the time — the winding limit, in numbers).
/// <paramref name="SolidBodies"/> carries each solid body's layer and motion system for the movable split;
/// <paramref name="PhantomOnly"/> says the mesh has collision but none of it is solid (a trigger volume).</summary>
public sealed record RefGeometry(Aabb Box, TriangleSoup HostSoup, TriangleSoup DepSoup, char HostSrc, char DepSrc, bool HostUsable,
                                 IReadOnlyList<(string Layer, string MotionSystem)> SolidBodies, bool PhantomOnly)
{
    public bool HasGeometry => !Box.IsEmpty && (HostSrc != '-' || DepSrc != '-');
    public bool CanHost => HasGeometry && HostUsable && HostSoup.TriangleCount > 0;
    public bool CanDepend => HasGeometry && DepSoup.TriangleCount > 0;
}

/// <summary>Why a host's placement changed under its last plugin: moved within its space, relocated to another, or
/// removed (disabled or deleted after having been placed — by its own record, or through an enable parent a later
/// plugin disabled).</summary>
public enum HostChange { Moved, Relocated, Removed }

/// <summary>A host whose last placement step is a move or a removal. <paramref name="Previous"/> is the snap before the
/// mover's — the host's placement in the order without the mover (<paramref name="PrevIndex"/> its index in the
/// history); <paramref name="PrevBase"/> the base record it had then (an override that swapped the mesh at the same
/// time as moving is rebuilt with the right mesh). <paramref name="RemovedViaParent"/>: the host left the world through
/// its enable-parent chain, and the mover is the plugin whose step on that chain disabled it (R1).</summary>
public sealed record MovedHost(FormKey Key, Snap Previous, int PrevIndex, Snap Winner, HostChange Change, int Mover, FormKey PrevBase, FormKey WinBase,
                               bool RemovedViaParent = false);

/// <summary>One arm's contact: the primitive's result, what it was against, and the geometry sources on both sides.
/// <paramref name="AgainstExtent"/>: how wide the against-ref's surface is along the contact normal (its largest box
/// dimension when the normal cannot be read) — the bound a penetration has to respect (R4); NaN when unknown.
/// <paramref name="AgainstMaxDim"/>: the against-ref's largest world-box dimension, so a re-supporter over the old
/// volume cap can be counted (R3). <paramref name="RawDistance"/> / <paramref name="RawAgainst"/>: the least RAW surface
/// distance (winding- and facing-free) to anything in the population, and to what — reported so a row the facing rule
/// calls "touches nothing" can say how far the nearest surface really is (the symbols a unit behind a wall's collision
/// face, 2026-09-03); the verdict never reads it.</summary>
public sealed record ArmContact(SoupContact Contact, FormKey Against, char DepSrc, char HostSrc,
                                float AgainstExtent = float.NaN, float AgainstMaxDim = float.NaN,
                                float RawDistance = float.NaN, FormKey RawAgainst = default);

/// <summary>One (dependent, moved host) pair, judged. <paramref name="DepAuthor"/> is the plugin that set the dependent's
/// placement in the arm without the mover; <paramref name="Seen"/> says that plugin is in the mover's master closure
/// (the arrangement was in the mover's editor); <paramref name="AuthorAfterMover"/> says it loads after the mover
/// (neither side could have seen the other). <paramref name="DepTouchedByMoverSide"/>: the mover's side carries an
/// override of the dependent's record without having re-placed it — awareness evidence the rule does not fold.</summary>
public sealed record ContactRow(MovedHost Host, FormKey Dependent, Snap DepAuthoring, Snap DepWinner, FormKey DepBase, int DepAuthor,
                                bool Seen, bool AuthorAfterMover, bool DepTouchedByMoverSide,
                                OverlapScale DepScale, OverlapScale HostScale, DependentMotion Motion,
                                ArmContact? Authoring, ArmContact? Winner,
                                DetectorModel.ContactFacts Facts, ContactRelation Relation, ContactVerdict Verdict, ContactBlocker Reason, string Why);

/// <summary>What the pairing set aside, counted per rule 6. The runtime-state counters are R1's: a host or dependent
/// that does not exist in the arm under test (disabled at runtime through its own flag or its parent chain), or whose
/// chain the order cannot follow; and the winner-arm population's exclusions on the same grounds.</summary>
public sealed class ContactSkips
{
    public long HostNoGeometry, HostNoCollision, HostVolume, HostTooSmall, DepNoGeometry, DepVolume, DepIntroducedByMover, DepAbsentInArm, DepOtherSpace, BoxesApart;
    public long HostRuntimeDisabled, HostEnableUnresolved, DepEnableUnresolved, WinnerRuntimeDisabled, WinnerEnableUnresolved;
}

/// <summary>A tile hash of world boxes inside a space partition (interior cell | worldspace) — the OverlapIndex pair
/// sweep's grid, kept as an object so two arms can each hold one.</summary>
public sealed class BoxHash<T>
{
    readonly Dictionary<(FormKey Space, int X, int Y), List<(T Item, Aabb Box)>> _grid = new();
    readonly float _tile;
    public BoxHash(float tile = 256f) => _tile = tile;
    public int Count { get; private set; }

    public void Add(FormKey space, Aabb box, T item)
    {
        if (box.IsEmpty) return;
        Count++;
        int x0 = (int)MathF.Floor(box.Min.X / _tile), x1 = (int)MathF.Floor(box.Max.X / _tile);
        int y0 = (int)MathF.Floor(box.Min.Y / _tile), y1 = (int)MathF.Floor(box.Max.Y / _tile);
        for (int x = x0; x <= x1; x++)
            for (int y = y0; y <= y1; y++)
            {
                if (!_grid.TryGetValue((space, x, y), out var l)) _grid[(space, x, y)] = l = new List<(T, Aabb)>();
                l.Add((item, box));
            }
    }

    /// <summary>Every item whose box is within <paramref name="reach"/> of <paramref name="box"/>, each once.</summary>
    public IEnumerable<(T Item, Aabb Box, float Distance)> Near(FormKey space, Aabb box, float reach)
    {
        if (box.IsEmpty) yield break;
        var seen = new HashSet<T>();
        int x0 = (int)MathF.Floor((box.Min.X - reach) / _tile), x1 = (int)MathF.Floor((box.Max.X + reach) / _tile);
        int y0 = (int)MathF.Floor((box.Min.Y - reach) / _tile), y1 = (int)MathF.Floor((box.Max.Y + reach) / _tile);
        for (int x = x0; x <= x1; x++)
            for (int y = y0; y <= y1; y++)
            {
                if (!_grid.TryGetValue((space, x, y), out var l)) continue;
                foreach (var (item, b) in l)
                {
                    if (!seen.Add(item)) continue;
                    float d = SpatialOverlap.BoxDistance(box, b);
                    if (d <= reach) yield return (item, b, d);
                }
            }
    }
}

public sealed class ContactIndex
{
    public OverlapIndex Index { get; } = new();
    readonly Func<FormKey, RefGeometry?> _geomOf;
    readonly Dictionary<FormKey, RefGeometry?> _geomCache = new();
    readonly Dictionary<(FormKey Key, int Snap), TriangleGrid?> _hostGrids = new();
    readonly Dictionary<(FormKey Key, int Snap), TriangleSoup?> _depSoups = new();
    readonly Dictionary<FormKey, EnableResolution> _winnerEnable = new();
    readonly Dictionary<int, Dictionary<FormKey, EnableResolution>> _armEnable = new();

    /// <summary>The reach a host looks for dependents within; the size bands — a host is furniture-scale or larger
    /// (<see cref="MinHostDim"/>, 64 u) up to the volume cap, a dependent is furniture-scale or smaller (under
    /// <see cref="MaxDependentDim"/>, 256 u). Parameters, stated; a table is 100–200 u across, a bowl 24 u.
    /// <see cref="MaxHostDim"/> gates MOVED hosts only (a whole shell is not a thing that moves out from under a bowl);
    /// the winner-arm population takes any solid collision whatever its size (R3).</summary>
    public float Reach { get; init; } = 128f;
    public float MinHostDim { get; init; } = 64f;
    public float MaxHostDim { get; init; } = 4096f;
    public float MaxDependentDim { get; init; } = 256f;
    public float Epsilon { get; init; } = 4f;
    /// <summary>A pair further apart than ε as authored has no relationship at ε — but one within the NEAR band still
    /// gets its winner arm measured, so the report can re-judge every row at a larger ε (sensitivity) from stored
    /// facts rather than a re-run. Twice ε by default.</summary>
    public float NearBand { get; init; } = 8f;
    /// <summary>The largest box gap that still gets a surface test — the near band plus a little, so a pair the rule
    /// could call touching at any ε under test is never rejected by the box lower bound.</summary>
    public float BoxBand { get; init; } = 10f;
    public float SupportCos { get; init; } = 0.866f;
    public float AttachCos { get; init; } = 0.5f;
    public float MoveThreshold { get; init; } = PlacementHistory.MoveThreshold;

    /// <param name="geomOf">geometry for a base record — the driver resolves meshes through the VFS, the proof builds
    /// box soups from OBND. Null (or Src '-') when there is none; cached here per base.</param>
    public ContactIndex(Func<FormKey, RefGeometry?> geomOf) => _geomOf = geomOf;

    public RefGeometry? GeomOf(FormKey baseKey)
    {
        if (_geomCache.TryGetValue(baseKey, out var g)) return g;
        return _geomCache[baseKey] = _geomOf(baseKey);
    }

    // ---- roles ---------------------------------------------------------------------------------

    (string Type, string? Edid) BaseInfoOf(FormKey baseKey) => Index.BaseInfo.TryGetValue(baseKey, out var bi) ? bi : ("?", null);

    /// <summary>The engine's own marker rule: the record's Is-Marker header flag (R2). Nothing with it is drawn, so
    /// nothing with it holds, rests, re-supports or swallows anything.</summary>
    public bool IsMarker(FormKey baseKey) => Index.BaseIsMarker.Contains(baseKey);

    /// <summary>Something that can hold or carry something: a Physical host class by ClassifyHost under the flag rule,
    /// not a control object by editor id (an FX cobweb or mist volume is a Static too, and holds nothing).</summary>
    public bool IsHostType(FormKey baseKey)
    {
        var (t, e) = BaseInfoOf(baseKey);
        bool m = IsMarker(baseKey);
        return DetectorModel.ClassifyHost(t, e, m) == HostClass.Physical
            && DetectorModel.ClassifyDependent("PlacedObject", t, e, m) != DependentClass.Control;
    }

    /// <summary>Something that can rest on or hang from something: a Physical dependent class by ClassifyDependent
    /// under the flag rule (statics, furniture, containers, doors, lights, activators, trees, flora, every item type),
    /// not a marker, not FX.</summary>
    public bool IsDependentType(FormKey baseKey)
    {
        var (t, e) = BaseInfoOf(baseKey);
        bool m = IsMarker(baseKey);
        return DetectorModel.ClassifyDependent("PlacedObject", t, e, m) == DependentClass.Physical
            && DetectorModel.ClassifyHost(t, e, m) != HostClass.Marker;
    }

    /// <summary>Any physical thing a dependent could be re-supported by under the winner: hosts and dependents both.</summary>
    public bool IsPhysicalType(FormKey baseKey) => IsHostType(baseKey) || IsDependentType(baseKey);

    /// <summary>The old marker heuristics, read against the flag and reported: "marker" in the editor id, or a mesh
    /// named MarkerX*. Neither is a gate any more; a base on either list is where the two rules disagree.</summary>
    public (List<FormKey> FlagOnly, List<FormKey> NameOnly) AuditMarkers()
    {
        var flagOnly = new List<FormKey>(); var nameOnly = new List<FormKey>();
        foreach (var (key, (_, edid)) in Index.BaseInfo)
        {
            bool byName = (edid is not null && edid.Contains("marker", StringComparison.OrdinalIgnoreCase))
                       || (Index.BaseMesh.TryGetValue(key, out var mesh) && Path.GetFileName(mesh).StartsWith("MarkerX", StringComparison.OrdinalIgnoreCase));
            bool byFlag = IsMarker(key);
            if (byFlag && !byName) flagOnly.Add(key);
            else if (byName && !byFlag) nameOnly.Add(key);
        }
        return (flagOnly, nameOnly);
    }

    public DependentMotion MotionOf(FormKey baseKey)
    {
        var (t, _) = BaseInfoOf(baseKey);
        var g = GeomOf(baseKey);
        return DetectorModel.ClassifyMotion(t == "?" ? null : t, g?.SolidBodies);
    }

    public static OverlapScale ScaleOf(Aabb world) =>
        DetectorModel.ClassifyOverlapScale(MathF.Max(world.Max.X - world.Min.X, MathF.Max(world.Max.Y - world.Min.Y, world.Max.Z - world.Min.Z)));

    static float MaxDim(Aabb b) => MathF.Max(b.Max.X - b.Min.X, MathF.Max(b.Max.Y - b.Min.Y, b.Max.Z - b.Min.Z));

    // ---- runtime enable state, per arm (R1) ------------------------------------------------------

    /// <summary>The XESP a reference carries at plugin <paramref name="plugin"/>'s load position: the last change at or
    /// before it (none when it never had one).</summary>
    (FormKey? Parent, bool Opposite) EnableParentAt(FormKey key, int plugin)
    {
        if (Index.EnableChanges.TryGetValue(key, out var changes))
            for (int i = changes.Count - 1; i >= 0; i--)
                if (changes[i].Plugin <= plugin) return (changes[i].Parent, changes[i].Opposite);
        return (null, false);
    }

    /// <summary>The XESP in the arm WITHOUT <paramref name="mover"/>'s side: the last change by a plugin that is neither
    /// the mover nor downstream of it.</summary>
    (FormKey? Parent, bool Opposite) ArmEnableParent(FormKey key, int mover, IReadOnlyList<HashSet<int>> closure)
    {
        if (Index.EnableChanges.TryGetValue(key, out var changes))
            for (int i = changes.Count - 1; i >= 0; i--)
            {
                int p = changes[i].Plugin;
                if (p == mover || closure[p].Contains(mover)) continue;
                return (changes[i].Parent, changes[i].Opposite);
            }
        return (null, false);
    }

    /// <summary>A reference's runtime enable state under the winner: its winning flag and winning XESP, the chain
    /// followed through everything else's winning state. A reference the order does not place, or has deleted, is
    /// unknown to the chain (reported unresolved).</summary>
    public EnableResolution WinnerEnable(FormKey key) => EnableState.Resolve(key, k =>
    {
        var s = Index.History.Winning(k);
        if (s is null || s.Value.Deleted || Index.IsDeletedNow(k)) return null;
        var (p, o) = EnableParentAt(k, int.MaxValue);
        return new EnableFacts(s.Value.Disabled, p, o);
    }, _winnerEnable);

    /// <summary>A reference's runtime enable state in the arm WITHOUT <paramref name="mover"/>'s side: each chain
    /// reference's flag at its authoring snap and its XESP as last set outside the mover's side. Memoised per mover.</summary>
    public EnableResolution ArmEnable(FormKey key, int mover, IReadOnlyList<HashSet<int>> closure)
    {
        if (!_armEnable.TryGetValue(mover, out var memo)) _armEnable[mover] = memo = new Dictionary<FormKey, EnableResolution>();
        return EnableState.Resolve(key, k =>
        {
            var snaps = Index.History.Of(k);
            if (snaps is null) return null;
            int i = AuthoringSnapIndex(snaps, mover, closure);
            if (i < 0 || snaps[i].Deleted) return null;
            var (p, o) = ArmEnableParent(k, mover, closure);
            return new EnableFacts(snaps[i].Disabled, p, o);
        }, memo);
    }

    /// <summary>The plugin whose step on a reference's enable chain last changed its runtime state under the winner —
    /// the mover of a host that left the world through its parent: the latest plugin among every chain element's XESP
    /// changes (past the definer's own) and the root's disabled-flag steps. −1 when the chain has no step after the
    /// definers (the thing was born disabled and nobody changed that).</summary>
    public int ChainMover(FormKey key)
    {
        int best = -1;
        var seen = new HashSet<FormKey>();
        var k = key;
        while (k.ID != EnableState.PlayerRefId && seen.Add(k))
        {
            var snaps = Index.History.Of(k);
            int definer = snaps is { Count: > 0 } ? snaps[0].Plugin : -1;
            if (Index.EnableChanges.TryGetValue(k, out var ch))
                foreach (var c in ch) if (c.Plugin != definer) best = Math.Max(best, c.Plugin);
            var (p, _) = EnableParentAt(k, int.MaxValue);
            if (p is null)
            {
                if (snaps is not null)
                    for (int i = snaps.Count - 1; i >= 1; i--)
                        if (snaps[i].Disabled != snaps[i - 1].Disabled) { best = Math.Max(best, snaps[i].Plugin); break; }
                break;
            }
            k = p.Value;
        }
        return best;
    }

    // ---- placement at a snap --------------------------------------------------------------------

    /// <summary>The base record a reference had at plugin <paramref name="plugin"/>'s load position: the last base change
    /// at or before it, else the winner's. For the HASHES only — a box per snap. An ARM's base is not this: a base
    /// swap is not a position step, so the plugin that set a placement is not the plugin that set the base (OCW turns
    /// a vanilla shell reference into its own floor without moving it; the winner's placement is still Skyrim.esm's).
    /// Use <see cref="WinnerBase"/> for the winner arm and <see cref="ArmBase"/> for the arm without a mover.</summary>
    public FormKey BaseAt(FormKey key, int plugin)
    {
        if (Index.BaseChanges.TryGetValue(key, out var changes))
            for (int i = changes.Count - 1; i >= 0; i--)
                if (changes[i].Plugin <= plugin) return changes[i].Base;
        return Index.RefBase.TryGetValue(key, out var b) ? b : FormKey.Null;
    }

    /// <summary>The base under the winner: the last plugin to set it, whether or not it moved the reference.</summary>
    public FormKey WinnerBase(FormKey key) => Index.RefBase.TryGetValue(key, out var b) ? b : FormKey.Null;

    /// <summary>The base in the arm WITHOUT <paramref name="mover"/>'s side: the last base change by a plugin that is
    /// neither the mover nor downstream of it — every other override still applies in that arm.</summary>
    public FormKey ArmBase(FormKey key, int mover, IReadOnlyList<HashSet<int>> closure)
    {
        if (Index.BaseChanges.TryGetValue(key, out var changes))
            for (int i = changes.Count - 1; i >= 0; i--)
            {
                int p = changes[i].Plugin;
                if (p == mover || closure[p].Contains(mover)) continue;
                return changes[i].Base;
            }
        return FormKey.Null;
    }

    public Aabb WorldBox(FormKey baseKey, Snap s)
    {
        var g = GeomOf(baseKey);
        return g is null || !g.HasGeometry ? Aabb.Empty : SpatialOverlap.ToWorld(g.Box, s.Pos, s.Rot, s.Scale);
    }

    /// <param name="anySurface">take the host soup whatever its source — a render fallback included — for a test that
    /// asks whether two SURFACES meet (the overlap ranker), not whether one HOLDS the other.</param>
    TriangleGrid? HostGrid(FormKey key, int snapIndex, FormKey baseKey, Snap s, bool anySurface = false)
    {
        if (_hostGrids.TryGetValue((key, snapIndex), out var g) && (g is not null || !anySurface)) return g;
        var geom = GeomOf(baseKey);
        TriangleGrid? grid = null;
        if (geom is { CanHost: true } || (anySurface && geom is { HasGeometry: true } && geom.HostSoup.TriangleCount > 0))
            grid = TriangleGrid.Build(SpatialOverlap.TransformSoup(geom.HostSoup, s.Pos, s.Rot, s.Scale));
        if (_hostGrids.Count > 4096) _hostGrids.Clear();      // a bound, not a policy: exterior worldspaces are large
        return _hostGrids[(key, snapIndex)] = grid;
    }

    TriangleSoup? DepSoup(FormKey key, int snapIndex, FormKey baseKey, Snap s)
    {
        if (_depSoups.TryGetValue((key, snapIndex), out var so)) return so;
        var geom = GeomOf(baseKey);
        TriangleSoup? soup = null;
        if (geom is { CanDepend: true })
            soup = SpatialOverlap.TransformSoup(geom.DepSoup, s.Pos, s.Rot, s.Scale);
        if (_depSoups.Count > 8192) _depSoups.Clear();
        return _depSoups[(key, snapIndex)] = soup;
    }

    // ---- the moved hosts -------------------------------------------------------------------------

    /// <summary>Every host-typed reference whose LAST placement step is a move (within its space, over the threshold),
    /// a relocation (another space), or a removal — enabled at runtime and placed before, deleted or disabled at
    /// runtime now. The mover is the last snap's plugin; for a deletion that stripped the placement (no snap), the
    /// deleting plugin; for a host disabled through its enable-parent chain (R1), the plugin whose step on that chain
    /// disabled it, when that step is later than any placement step. A host must exist at runtime in the arm without
    /// its mover — a host that never existed there is counted, not listed.
    /// A host is furniture-scale or larger by its box at the previous snap; smaller things (a bowl moved with its
    /// table) are dependents, not hosts, and are counted aside. <paramref name="excludeMover"/> sets movers aside
    /// (the base game, when the floor is not wanted).</summary>
    public List<MovedHost> MovedHosts(ContactSkips skips, IReadOnlyList<HashSet<int>> closure, Func<int, bool>? excludeMover = null)
    {
        var list = new List<MovedHost>();
        foreach (var (key, snaps) in Index.History.Refs)
        {
            Snap prev, win; HostChange change; int mover; int prevIndex; bool viaParent = false; bool found = false;
            prev = default; win = default; change = default; mover = -1; prevIndex = -1;
            // A deletion that stripped the placement leaves the history one snap short: the winning snap is the
            // last PLACED one, and the deleting plugin is the mover of a Removed host.
            if (snaps.Count >= 1 && Index.DeletedBy.TryGetValue(key, out var delBy) && delBy > snaps[^1].Plugin && !snaps[^1].Deleted)
            {
                prev = snaps[^1]; prevIndex = snaps.Count - 1;
                win = prev with { Plugin = delBy, Deleted = true }; change = HostChange.Removed; mover = delBy; found = true;
            }
            else if (snaps.Count >= 2)
            {
                win = snaps[^1]; prev = snaps[^2]; prevIndex = snaps.Count - 2; mover = win.Plugin; found = true;
                if (prev.Deleted) found = false;                                          // it was not there before either
                else if (win.Deleted || Index.IsDeletedNow(key) || !WinnerEnable(key).Enabled) change = HostChange.Removed;
                else if (win.Space != prev.Space) change = HostChange.Relocated;
                else if (PlacementHistory.Dist(prev.Pos, win.Pos) > MoveThreshold) change = HostChange.Moved;
                else found = false;
            }
            // The second way a host leaves (R1): disabled at runtime through its parent chain by a plugin whose step
            // is later than any placement step of the host's own.
            if (snaps.Count >= 1 && !snaps[^1].Deleted && !Index.IsDeletedNow(key))
            {
                var we = WinnerEnable(key);
                if (we.Resolved && !we.Enabled && we.ViaParent)
                {
                    int x = ChainMover(key);
                    if (x >= 0 && (!found || x > mover))
                    {
                        int ai = AuthoringSnapIndex(snaps, x, closure);
                        if (ai >= 0 && !snaps[ai].Deleted)
                        {
                            prev = snaps[ai]; prevIndex = ai; win = snaps[^1]; change = HostChange.Removed; mover = x; viaParent = true; found = true;
                        }
                    }
                }
            }
            if (!found) continue;
            if (excludeMover?.Invoke(mover) == true) continue;
            var prevBase = ArmBase(key, mover, closure);
            var winBase = WinnerBase(key);
            if (prevBase.IsNull || !IsHostType(prevBase)) continue;
            var armEn = ArmEnable(key, mover, closure);
            if (!armEn.Resolved) { skips.HostEnableUnresolved++; continue; }
            if (!armEn.Enabled) { skips.HostRuntimeDisabled++; continue; }          // it did not exist before either
            var box = WorldBox(prevBase, prev);
            if (box.IsEmpty) { skips.HostNoGeometry++; continue; }
            if (GeomOf(prevBase) is not { CanHost: true }) { skips.HostNoCollision++; continue; }
            float dim = MaxDim(box);
            if (dim > MaxHostDim) { skips.HostVolume++; continue; }
            if (dim < MinHostDim) { skips.HostTooSmall++; continue; }
            list.Add(new MovedHost(key, prev, prevIndex, win, change, mover, prevBase, winBase, viaParent));
        }
        return list;
    }

    // ---- the two hashes ----------------------------------------------------------------------------

    /// <summary>Every dependent-typed reference at every undeleted snap, hashed by that snap's world box — the authoring
    /// arm's population (which snap applies, and whether the reference EXISTS at runtime in that arm, is decided per
    /// host by its mover in <see cref="Judge"/>; a flag-disabled snap is hashed too, because its parent may enable it).</summary>
    public BoxHash<(FormKey Key, int Snap)> DependentSnaps(ContactSkips skips)
    {
        var h = new BoxHash<(FormKey, int)>();
        foreach (var (key, snaps) in Index.History.Refs)
        {
            for (int i = 0; i < snaps.Count; i++)
            {
                var s = snaps[i];
                if (s.Deleted) continue;
                var b = BaseAt(key, s.Plugin);
                if (!IsDependentType(b)) continue;
                var box = WorldBox(b, s);
                if (box.IsEmpty) { if (i == snaps.Count - 1) skips.DepNoGeometry++; continue; }
                if (MaxDim(box) > MaxDependentDim) { if (i == snaps.Count - 1) skips.DepVolume++; continue; }
                h.Add(s.Space, box, (key, i));
            }
        }
        return h;
    }

    /// <summary>Every physical reference (host- or dependent-typed, ANY size — R3) that CAN HOLD something — solid
    /// collision — at its winning placement, undeleted and enabled at runtime under the winner: the winner arm's
    /// population. A reference whose enable chain cannot be followed is left out and counted.</summary>
    public BoxHash<FormKey> PhysicalWinners(Func<Snap, bool>? inScope = null, ContactSkips? skips = null)
    {
        var h = new BoxHash<FormKey>();
        foreach (var (key, snaps) in Index.History.Refs)
        {
            var s = snaps[^1];
            if (s.Deleted || Index.IsDeletedNow(key)) continue;
            if (inScope is not null && !inScope(s)) continue;
            var b = WinnerBase(key);
            if (!IsPhysicalType(b) || GeomOf(b) is not { CanHost: true }) continue;
            var en = WinnerEnable(key);
            if (!en.Resolved) { if (skips is not null) skips.WinnerEnableUnresolved++; continue; }
            if (!en.Enabled) { if (skips is not null) skips.WinnerRuntimeDisabled++; continue; }
            var box = WorldBox(b, s);
            if (box.IsEmpty) continue;
            h.Add(s.Space, box, key);
        }
        return h;
    }

    /// <summary>The winner arm's placement of a reference: its winning snap when undeleted and enabled at runtime, else null.</summary>
    public (Snap Snap, int SnapIndex)? WinnerPlacement(FormKey key)
    {
        var snaps = Index.History.Of(key);
        if (snaps is null || snaps.Count == 0) return null;
        var w = snaps[^1];
        if (w.Deleted || Index.IsDeletedNow(key)) return null;
        var en = WinnerEnable(key);
        if (en.Resolved && !en.Enabled) return null;
        return (w, snaps.Count - 1);
    }

    /// <summary>Every physical reference at its placement in the arm WITHOUT a given mover side — the last snap not by
    /// the mover or a plugin downstream of it, enabled at runtime there. For a labelled-mover check (USSEP before vs after).</summary>
    public BoxHash<FormKey> PhysicalWithout(int mover, IReadOnlyList<HashSet<int>> closure, Func<Snap, bool>? inScope, out Dictionary<FormKey, int> snapIndex,
                                            ContactSkips? skips = null)
    {
        var h = new BoxHash<FormKey>();
        snapIndex = new Dictionary<FormKey, int>();
        foreach (var (key, snaps) in Index.History.Refs)
        {
            int i = AuthoringSnapIndex(snaps, mover, closure);
            if (i < 0) continue;
            var s = snaps[i];
            if (s.Deleted) continue;
            if (inScope is not null && !inScope(s)) continue;
            var b = ArmBase(key, mover, closure);
            if (b.IsNull || !IsPhysicalType(b) || GeomOf(b) is not { CanHost: true }) continue;
            var en = ArmEnable(key, mover, closure);
            if (!en.Resolved) { if (skips is not null) skips.WinnerEnableUnresolved++; continue; }
            if (!en.Enabled) { if (skips is not null) skips.WinnerRuntimeDisabled++; continue; }
            var box = WorldBox(b, s);
            if (box.IsEmpty) continue;
            h.Add(s.Space, box, key);
            snapIndex[key] = i;
        }
        return h;
    }

    /// <summary>The index of a reference's snap in the order without <paramref name="mover"/>'s side: the last one whose
    /// plugin is neither the mover nor downstream of it. −1 when every snap is the mover's side (it introduced the ref).</summary>
    public static int AuthoringSnapIndex(IReadOnlyList<Snap> snaps, int mover, IReadOnlyList<HashSet<int>> closure)
    {
        for (int i = snaps.Count - 1; i >= 0; i--)
        {
            int p = snaps[i].Plugin;
            if (p == mover || closure[p].Contains(mover)) continue;
            return i;
        }
        return -1;
    }

    // ---- the best contact of one dependent against a population ------------------------------------

    /// <summary>The dependent's best contact against everything in <paramref name="hash"/> (itself excluded) whose box
    /// is within the box band: the deepest penetration if any penetrates, else the least distance. Null when nothing is
    /// within the band, or the dependent has no soup. <paramref name="placementOf"/> gives each neighbour's snap in this
    /// arm; <paramref name="baseOf"/> its base then. The against-ref of the best contact is the one the reported depth
    /// was measured against, and the arm carries how wide that ref is along the contact normal (R4).</summary>
    public ArmContact? BestContact(FormKey depKey, int depSnapIndex, FormKey depBase, Snap depSnap, Aabb depBox,
                                   BoxHash<FormKey> hash, Func<FormKey, (Snap Snap, int SnapIndex)?> placementOf, Func<FormKey, int, FormKey> baseOf,
                                   out int neighboursTested)
    {
        neighboursTested = 0;
        var soup = DepSoup(depKey, depSnapIndex, depBase, depSnap);
        if (soup is null) return null;
        var depGeom = GeomOf(depBase)!;
        ArmContact? best = null;
        float bestRaw = float.PositiveInfinity; FormKey rawAgainst = default;
        foreach (var (nKey, nBox, _) in hash.Near(depSnap.Space, depBox, BoxBand))
        {
            if (nKey == depKey) continue;
            if (placementOf(nKey) is not { } np) continue;
            var nBase = baseOf(nKey, np.Snap.Plugin);
            var grid = HostGrid(nKey, np.SnapIndex, nBase, np.Snap);
            if (grid is null) continue;
            neighboursTested++;
            var c = SpatialOverlap.Contact(soup, grid, Reach, NearBand);
            if (c.Distance < bestRaw) { bestRaw = c.Distance; rawAgainst = nKey; }
            if (best is not null && !Better(c, best.Contact)) continue;
            var hg = GeomOf(nBase)!;
            float extent = c.Normal.LengthSquared() > 1e-12f ? grid.ExtentAlong(Vector3.Normalize(c.Normal)) : MaxDim(nBox);
            best = new ArmContact(c, nKey, depGeom.DepSrc, hg.HostSrc, extent, MaxDim(nBox));
        }
        return best is null ? null : best with { RawDistance = bestRaw, RawAgainst = rawAgainst };
    }

    static bool Better(SoupContact a, SoupContact b)
    {
        if (a.EffectivePenetration > 0f || b.EffectivePenetration > 0f) return a.EffectivePenetration > b.EffectivePenetration;
        return a.FacingDistance < b.FacingDistance;
    }

    /// <summary>One dependent against one host in one arm. <paramref name="anySurface"/>: let a render-only mesh stand as
    /// the surface under test (labelled 'r' on the row) — for the overlap ranker, which asks whether two surfaces meet,
    /// never for the rule, where a host must be able to hold something.</summary>
    public ArmContact? PairContact(FormKey depKey, int depSnapIndex, FormKey depBase, Snap depSnap,
                                   FormKey hostKey, int hostSnapIndex, FormKey hostBase, Snap hostSnap, bool anySurface = false)
    {
        var soup = DepSoup(depKey, depSnapIndex, depBase, depSnap);
        var grid = HostGrid(hostKey, hostSnapIndex, hostBase, hostSnap, anySurface);
        if (soup is null || grid is null) return null;
        var c = SpatialOverlap.Contact(soup, grid, Reach, NearBand);
        float extent = c.Normal.LengthSquared() > 1e-12f ? grid.ExtentAlong(Vector3.Normalize(c.Normal)) : float.NaN;
        return new ArmContact(c, hostKey, GeomOf(depBase)!.DepSrc, GeomOf(hostBase)!.HostSrc, extent, RawDistance: c.Distance, RawAgainst: hostKey);
    }

    // ---- judge one host ------------------------------------------------------------------------------

    /// <summary>Every dependent within reach of <paramref name="host"/> in the authoring arm, judged. The authoring arm
    /// is the order without the host's mover: the host at its previous snap, each dependent at its last snap not on the
    /// mover's side — and existing at runtime there (R1). The winner arm tests the dependent against everything physical
    /// near it under the winner.</summary>
    public List<ContactRow> Judge(MovedHost host, BoxHash<(FormKey Key, int Snap)> depSnaps, BoxHash<FormKey> winners,
                                  IReadOnlyList<HashSet<int>> closure, ContactSkips skips, bool keepNoRelation = false)
    {
        var rows = new List<ContactRow>();
        var hostBox = WorldBox(host.PrevBase, host.Previous);
        if (hostBox.IsEmpty) return rows;                                        // MovedHosts already counted it
        var hostScale = ScaleOf(hostBox);
        int hostPrevIndex = host.PrevIndex;

        foreach (var ((depKey, si), depBox, _) in depSnaps.Near(host.Previous.Space, hostBox, Reach))
        {
            if (depKey == host.Key) continue;
            var snaps = Index.History.Of(depKey)!;
            int ai = AuthoringSnapIndex(snaps, host.Mover, closure);
            if (ai < 0) { if (si == snaps.Count - 1) skips.DepIntroducedByMover++; continue; }
            if (ai != si) continue;                                              // this snap is not the authoring arm's
            var depAuth = snaps[ai];
            if (depAuth.Space != host.Previous.Space) { skips.DepOtherSpace++; continue; }
            var depArmEn = ArmEnable(depKey, host.Mover, closure);
            if (!depArmEn.Resolved) { skips.DepEnableUnresolved++; continue; }
            if (!depArmEn.Enabled) { skips.DepAbsentInArm++; continue; }         // never there to rest on anything
            var depBase = ArmBase(depKey, host.Mover, closure);
            if (depBase.IsNull) { skips.DepIntroducedByMover++; continue; }
            var depArmBox = WorldBox(depBase, depAuth);
            if (depArmBox.IsEmpty) depArmBox = depBox;
            float gap = SpatialOverlap.BoxDistance(depArmBox, hostBox);
            if (gap > BoxBand) { skips.BoxesApart++; continue; }

            var depWin = snaps[^1];
            bool depDeletedNow = Index.IsDeletedNow(depKey);
            var depWinEn = WinnerEnable(depKey);
            bool moverSide = depWin.Plugin == host.Mover || closure[depWin.Plugin].Contains(host.Mover);
            bool rePlacedByMoverSide = ai != snaps.Count - 1 && moverSide;
            bool seen = closure[host.Mover].Contains(depAuth.Plugin) || depAuth.Plugin == host.Mover;
            bool authorAfter = depAuth.Plugin > host.Mover;
            bool touched = Index.Touchers.TryGetValue(depKey, out var tl) && tl.Any(t => t == host.Mover || closure[t].Contains(host.Mover));

            var auth = PairContact(depKey, ai, depBase, depAuth, host.Key, hostPrevIndex, host.PrevBase, host.Previous);
            var depGeom = GeomOf(depBase);
            var hostGeom = GeomOf(host.PrevBase);
            bool depHasGeom = depGeom is { CanDepend: true };
            bool hostHasGeom = hostGeom is { CanHost: true };
            var motion = MotionOf(depBase);

            // The authoring facts first — a pair with no relationship never needs the winner arm measured.
            var f0 = new DetectorModel.ContactFacts(depHasGeom, hostHasGeom, auth is not null,
                auth?.Contact.FacingDistance ?? float.NaN, auth?.Contact.EffectivePenetration ?? float.NaN, auth?.Contact.NormalUp ?? float.NaN,
                depWin.Deleted || depDeletedNow, !depWinEn.Enabled, rePlacedByMoverSide, false, float.NaN, float.NaN, false, motion,
                DependentDisabledViaParent: depWinEn.ViaParent, DependentEnableUnresolved: !depWinEn.Resolved);
            var r0 = DetectorModel.PlacementContact(f0, Epsilon, SupportCos, AttachCos);
            // A near miss — no relationship at ε, but within the near band — still gets its winner arm measured, so a
            // re-judgement at a larger ε has the facts it needs (the report's sensitivity table).
            bool nearMiss = r0.Verdict == ContactVerdict.NoRelation && auth is { } ac
                         && ac.Contact.FacingDistance <= NearBand && ac.Contact.EffectivePenetration <= NearBand && !float.IsNaN(ac.Contact.NormalUp);
            // A re-placed dependent (MovedTogether) keeps its verdict whatever the winner arm says, but the winner arm is
            // measured for it anyway (2026-09-03): the differential's physical predicate — does it still touch nothing? —
            // needs the number, and a sham that nudges a dependent 1 u onto the mover's side IS a MovedTogether row.
            bool settledWithoutWinner = (r0.Verdict == ContactVerdict.NoRelation && !nearMiss)
                                     || r0.Verdict == ContactVerdict.Removed
                                     || (r0.Verdict == ContactVerdict.Unjudgeable && r0.Reason != ContactBlocker.WinnerArmMissing);
            if (settledWithoutWinner)
            {
                if (r0.Verdict != ContactVerdict.NoRelation || keepNoRelation)
                    rows.Add(new ContactRow(host, depKey, depAuth, depWin, depBase, depAuth.Plugin, seen, authorAfter, touched,
                                            ScaleOf(depArmBox), hostScale, motion, auth, null, f0, r0.Relation, r0.Verdict, r0.Reason, r0.Why));
                continue;
            }

            // The winner arm: the dependent where the winner leaves it, against everything physical near it.
            var winBase = WinnerBase(depKey);
            var winBox = WorldBox(winBase, depWin);
            var win = winBox.IsEmpty ? null
                    : BestContact(depKey, snaps.Count - 1, winBase, depWin, winBox, winners, WinnerPlacement, (k, _) => WinnerBase(k), out _);
            bool winMeasured = !winBox.IsEmpty;
            var f = f0 with
            {
                WinnerMeasured = winMeasured,
                WinnerDistance = win?.Contact.FacingDistance ?? Reach,
                WinnerPenetration = win?.Contact.EffectivePenetration ?? 0f,
                WinnerContactIsHost = win is not null && win.Against == host.Key,
                WinnerAgainstExtent = win?.AgainstExtent ?? float.NaN,
            };
            var r = DetectorModel.PlacementContact(f, Epsilon, SupportCos, AttachCos);
            if (r.Verdict == ContactVerdict.NoRelation && !keepNoRelation && !nearMiss) continue;
            rows.Add(new ContactRow(host, depKey, depAuth, depWin, depBase, depAuth.Plugin, seen, authorAfter, touched,
                                    ScaleOf(depArmBox), hostScale, motion, auth, win, f, r.Relation, r.Verdict, r.Reason, r.Why));
        }
        return rows;
    }

    /// <summary>A closed box as twelve outward-facing triangles — the OBND fallback's soup, and the proof's stand-in for a
    /// mesh. Corners in the box's own frame.</summary>
    public static TriangleSoup BoxSoup(Aabb b)
    {
        if (b.IsEmpty) return TriangleSoup.Empty;
        var lo = new Vector3(b.Min.X, b.Min.Y, b.Min.Z); var hi = new Vector3(b.Max.X, b.Max.Y, b.Max.Z);
        var v = new[]
        {
            new Vector3(lo.X, lo.Y, lo.Z), new Vector3(hi.X, lo.Y, lo.Z), new Vector3(hi.X, hi.Y, lo.Z), new Vector3(lo.X, hi.Y, lo.Z),
            new Vector3(lo.X, lo.Y, hi.Z), new Vector3(hi.X, lo.Y, hi.Z), new Vector3(hi.X, hi.Y, hi.Z), new Vector3(lo.X, hi.Y, hi.Z),
        };
        var i = new[] { 0,2,1, 0,3,2, 4,5,6, 4,6,7, 0,1,5, 0,5,4, 2,3,7, 2,7,6, 1,2,6, 1,6,5, 3,0,4, 3,4,7 };
        return new TriangleSoup(v, i);
    }
}
