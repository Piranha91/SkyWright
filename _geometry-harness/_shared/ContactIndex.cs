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
                                 IReadOnlyList<(string Layer, string MotionSystem)> SolidBodies, bool PhantomOnly,
                                 TriangleSoup? HostShell = null, TriangleSoup? DepShell = null)
{
    public bool HasGeometry => !Box.IsEmpty && (HostSrc != '-' || DepSrc != '-');
    public bool CanHost => HasGeometry && HostUsable && HostSoup.TriangleCount > 0;
    public bool CanDepend => HasGeometry && DepSoup.TriangleCount > 0;
    /// <summary>The largest per-triangle Havok radius on each role's core soup (model units; 0 when none recorded) — how far the grown
    /// surface can reach past the core, for the broad phase.</summary>
    public float HostRadiusMax { get; } = HostSoup.TriangleRadii is { Length: > 0 } hr ? hr.Max() : 0f;
    public float DepRadiusMax { get; } = DepSoup.TriangleRadii is { Length: > 0 } dr ? dr.Max() : 0f;
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
                                float RawDistance = float.NaN, FormKey RawAgainst = default)
{
    /// <summary>The facing gap between the CORE surfaces, kept beside <see cref="SoupContact.FacingDistance"/> when that is read with
    /// the Havok radius (<see cref="ContactIndex.ReadRadius"/>), so a row can say what the radius changed; equal to it otherwise.</summary>
    public float CoreGap { get; init; } = float.NaN;
    /// <summary>The differential Sunk rule (opt-in, <see cref="ContactIndex.BestContact"/>): how deep the dependent already sat in
    /// <see cref="ArmContact.Against"/> as authored (0 when not measured), and whether this contact is that pre-existing overlap
    /// standing alone — nothing else within ε.</summary>
    public float PreexistingDepth { get; init; }
    public bool PreexistingOnly { get; init; }
    /// <summary>Separate, non-certified directional coverage evidence; never substituted for penetration.</summary>
    public DirectionalCoverageResult? Coverage { get; init; }
    public DirectionalCoverageResult? AuthoredCoverage { get; init; }
    public bool CoverageInputsExactlyUnchanged { get; init; }
    public IReadOnlyList<CoverageNeighbour>? CoverageNeighbours { get; init; }
    /// <summary>Diagnostic only (never read by the selection or the rule): every neighbour the dependent penetrated deeper than Îµ,
    /// with that depth and its measured authored depth — so a report can attribute a changed row to the differential rule.</summary>
    public IReadOnlyList<(FormKey Against, float Depth, float Authored)>? DeepNeighbours { get; init; }
}

/// <summary>One (dependent, moved host) pair, judged. <paramref name="DepAuthor"/> is the plugin that set the dependent's
/// placement in the arm without the mover; <paramref name="Seen"/> says that plugin is in the mover's master closure
/// (the arrangement was in the mover's editor); <paramref name="AuthorAfterMover"/> says it loads after the mover
/// (neither side could have seen the other). <paramref name="DepTouchedByMoverSide"/>: the mover's side carries an
/// override of the dependent's record without having re-placed it — awareness evidence the rule does not fold.</summary>
public sealed record CoverageNeighbour(FormKey Against, DirectionalCoverageResult Winner, DirectionalCoverageResult? Authored)
{ public CoverageInputComparison? InputComparison { get; init; } }

// Insert after CoverageNeighbour in ContactIndex.cs. Geometry-bearing frames remain local, not serialized.
public sealed record CoverageInputs(FormKey DependentBase, FormKey HostBase, char DependentSource, char HostSource,
    Snap DependentPose, Snap HostPose, EnableResolution DependentEnable, EnableResolution HostEnable,
    TriangleSoup? Dependent, TriangleGrid? Host, Vector3 Direction, float MaxGap, float FaceCos,
    int MaxTrianglePairs = 2_000_000, int MaxPieces = 4096, double Tolerance = 1e-5);

public sealed record AuthoredCoverageReading(DirectionalCoverageResult Result, CoverageInputs? Inputs = null,
    string? KnownAbsence = null);

public sealed record CoverageInputComparison(string Status, string? Reason, long ElementsCompared)
{
    public bool ExactlyEqual => Status == "identical";
    public static CoverageInputComparison Unknown(string reason) => new("unknown", reason, 0);
}

/// <summary>Exact equality of the complete modeled inputs consumed by the directional measure. Never an area,
/// gap, tolerance, source-hash or changed-history approximation. Equal geometry alone does not erase a work refusal.
/// Host comparison enumerates ordered consumed triangle vertices through the readonly grid accessor.</summary>
public static class CoverageInputComparisonReader
{
    // RED_GATE_METHOD_BEGIN
    public static CoverageInputComparison CompareExact(CoverageInputs? winner, CoverageInputs? authored,
        long maxElements = 2_000_000, CancellationToken cancellationToken = default)
    {
        CoverageInputComparison Different(string reason, long count = 0) => new("different", reason, count);
        CoverageInputComparison Unknown(string reason, long count = 0) => new("unknown", reason, count);
        if (cancellationToken.IsCancellationRequested) return Unknown("input_comparison_cancelled");
        if (winner is null || authored is null) return Unknown("input_frame_missing");
        if (winner.Dependent is null || authored.Dependent is null || winner.Host is null || authored.Host is null)
            return Unknown("input_geometry_missing");
        if (!winner.DependentEnable.Resolved || !winner.HostEnable.Resolved
            || !authored.DependentEnable.Resolved || !authored.HostEnable.Resolved)
            return Unknown("input_enable_unresolved");
        if (!winner.DependentEnable.Enabled || !winner.HostEnable.Enabled
            || !authored.DependentEnable.Enabled || !authored.HostEnable.Enabled)
            return Different("input_known_runtime_absence");
        if (!Finite(winner) || !Finite(authored)) return Unknown("input_parameters_nonfinite");
        if (maxElements < 0) return Unknown("input_comparison_invalid_budget");
        if (winner.DependentBase != authored.DependentBase || winner.HostBase != authored.HostBase)
            return Different("input_base_changed");
        if (winner.DependentSource != authored.DependentSource || winner.HostSource != authored.HostSource)
            return Different("input_role_source_changed");
        if (!Pose(winner.DependentPose, authored.DependentPose) || !Pose(winner.HostPose, authored.HostPose))
            return Different("input_pose_changed");
        if (winner.DependentEnable != authored.DependentEnable || winner.HostEnable != authored.HostEnable)
            return Different("input_enable_changed");
        if (!V(winner.Direction, authored.Direction) || !F(winner.MaxGap, authored.MaxGap)
            || !F(winner.FaceCos, authored.FaceCos) || !D(winner.Tolerance, authored.Tolerance)
            || winner.MaxTrianglePairs != authored.MaxTrianglePairs || winner.MaxPieces != authored.MaxPieces)
            return Different("input_measurement_parameters_changed");
        var a = winner.Dependent; var b = authored.Dependent;
        if (a.Indices.Length % 3 != 0 || b.Indices.Length % 3 != 0) return Unknown("input_indices_invalid");
        if (a.Vertices.Length != b.Vertices.Length || a.Indices.Length != b.Indices.Length
            || winner.Host.TriangleCount != authored.Host.TriangleCount)
            return Different("input_geometry_count_changed");
        long required = (long)a.Vertices.Length + a.Indices.Length + 3L * winner.Host.TriangleCount;
        if (required > maxElements) return Unknown("input_comparison_element_budget");
        long compared = 0;
        for (int i = 0; i < a.Vertices.Length; i++)
        {
            if (cancellationToken.IsCancellationRequested) return Unknown("input_comparison_cancelled", compared);
            if (!Finite(a.Vertices[i]) || !Finite(b.Vertices[i])) return Unknown("input_geometry_nonfinite", compared);
            compared++;
            if (!V(a.Vertices[i], b.Vertices[i])) return Different("input_dependent_vertices_changed", compared);
        }
        for (int i = 0; i < a.Indices.Length; i++)
        {
            if (cancellationToken.IsCancellationRequested) return Unknown("input_comparison_cancelled", compared);
            if ((uint)a.Indices[i] >= (uint)a.Vertices.Length || (uint)b.Indices[i] >= (uint)b.Vertices.Length)
                return Unknown("input_indices_invalid", compared);
            compared++;
            if (a.Indices[i] != b.Indices[i]) return Different("input_dependent_indices_changed", compared);
        }
        for (int i = 0; i < winner.Host.TriangleCount; i++)
        {
            if (cancellationToken.IsCancellationRequested) return Unknown("input_comparison_cancelled", compared);
            var x = winner.Host.GetTriangle(i); var y = authored.Host.GetTriangle(i);
            if (!Finite(x.A) || !Finite(x.B) || !Finite(x.C) || !Finite(y.A) || !Finite(y.B) || !Finite(y.C))
                return Unknown("input_geometry_nonfinite", compared);
            compared += 3;
            if (!V(x.A, y.A) || !V(x.B, y.B) || !V(x.C, y.C)) return Different("input_host_triangles_changed", compared);
        }
        return new("identical", null, compared);
    }
    // RED_GATE_METHOD_END

    static bool F(float a, float b) => BitConverter.SingleToInt32Bits(a) == BitConverter.SingleToInt32Bits(b);
    static bool D(double a, double b) => BitConverter.DoubleToInt64Bits(a) == BitConverter.DoubleToInt64Bits(b);
    static bool V(Vector3 a, Vector3 b) => F(a.X,b.X) && F(a.Y,b.Y) && F(a.Z,b.Z);
    static bool V(Vec3 a, Vec3 b) => F(a.X,b.X) && F(a.Y,b.Y) && F(a.Z,b.Z);
    static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
    static bool Finite(Vec3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
    static bool Pose(Snap a, Snap b) => a.Cell == b.Cell && a.Worldspace == b.Worldspace && a.Disabled == b.Disabled
        && a.Deleted == b.Deleted && V(a.Pos,b.Pos) && V(a.Rot,b.Rot) && F(a.Scale,b.Scale);
    static bool Finite(CoverageInputs f) => Finite(f.Direction) && Finite(f.DependentPose.Pos) && Finite(f.DependentPose.Rot)
        && Finite(f.HostPose.Pos) && Finite(f.HostPose.Rot) && float.IsFinite(f.DependentPose.Scale)
        && float.IsFinite(f.HostPose.Scale) && float.IsFinite(f.MaxGap) && float.IsFinite(f.FaceCos) && double.IsFinite(f.Tolerance);
}


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
    /// <summary>Opt-in per-object trace (src/housecarl-core/ProbeTrace.cs). Null by default; attaching it never changes a verdict.
    /// Called at every exit of <see cref="MovedHosts"/>, <see cref="DependentSnaps"/>, <see cref="PhysicalWinners"/>,
    /// <see cref="PhysicalWithout"/> and <see cref="Judge"/>; each counted exit emits under the coverage key it counts.</summary>
    public IProbeTraceSink? Trace { get; set; }

    /// <summary>Whether the attached sink wants events for this key (false with no sink — the untraced path does no work).</summary>
    bool Tw(FormKey key) => Trace is { } t && t.Wants(key);
    void Te(FormKey key, string role, string outcome, string rule, FormKey other = default,
            IReadOnlyDictionary<string, double>? values = null, string? detail = null)
        => Trace!.Emit(new TraceEvent(key, role, outcome, rule, other, values, detail));
    static Dictionary<string, double> Tv(string n1, double v1, string n2, double v2) => new() { [n1] = v1, [n2] = v2 };
    readonly Func<FormKey, RefGeometry?> _geomOf;
    readonly Dictionary<FormKey, RefGeometry?> _geomCache = new();
    /// <summary>The byte budget the per-placement caches share (host grids, dependent soups, radius shells): least-recently-used,
    /// evicted by bytes (_shared/PlacementCache.cs). Set at construction; the default takes the automatic share of the memory the
    /// runtime may use. Replaces the entry-count bounds that let the host grids reach 11.6 GB on an exterior block (2026-10-05).</summary>
    public CacheBudget Budget { get; }
    readonly PlacementCache<TriangleGrid> _hostGrids;
    readonly PlacementCache<TriangleSoup> _depSoups;
    /// <summary>The per-arm enable chain (_shared/EnableArms.cs — lifted out of this file in Q27 so the overlap pass's
    /// without-plugin arm resolves the same way, and shared by construction rather than by copy).</summary>
    public EnableArms Arms { get; }
    /// <summary>The world-state arm (roadmap 4.11): a set of switches flipped from their baseline. Set BEFORE any enable
    /// state is read — the memos are cleared when it changes. Null is the load-time state, every run before 2026-09-04.</summary>
    public StateArm? State
    {
        get => Arms.State;
        set => Arms.State = value;
    }

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
    /// gets its winner arm measured, so the report can re-judge every row at a larger Îµ (sensitivity) from stored
    /// facts rather than a re-run. Twice Îµ by default.</summary>
    public float NearBand { get; init; } = 8f;
    /// <summary>The largest box gap that still gets a surface test — the near band plus a little, so a pair the rule
    /// could call touching at any Îµ under test is never rejected by the box lower bound.</summary>
    public float BoxBand { get; init; } = 10f;
    public float SupportCos { get; init; } = 0.866f;
    public float AttachCos { get; init; } = 0.5f;
    public float MoveThreshold { get; init; } = PlacementHistory.MoveThreshold;
    /// <summary>Read the Havok radius for the gap the rule reads (contactprobe <c>--radius on</c>): the gap between the soups with every
    /// radius grown (<see cref="RefGeometry.HostShell"/> / <see cref="RefGeometry.DepShell"/>); normal, depth and extent stay the core's.
    /// The pair's broad phase widens by both shapes' largest radius (x scale) so a pair only the grown surfaces bring together is tested.
    /// Chosen over reading everything on the grown soups (whose summed normal tilts and whose depth reads crossings at open faces) and
    /// over a lower Îµ, by the in-game placement-contact acceptance set; Îµ is unchanged.</summary>
    public bool ReadRadius { get; init; }
    float _maxHostRadiusW;                                                     // the largest host radius x scale hashed so far
    float HostPadW(FormKey baseKey, Snap s)
    {
        if (!ReadRadius) return 0f;
        float r = GeomOf(baseKey) is { } g ? g.HostRadiusMax * s.Scale : 0f;
        if (r > _maxHostRadiusW) _maxHostRadiusW = r;
        return r;
    }
    float DepPadW(FormKey baseKey, Snap s) => ReadRadius && GeomOf(baseKey) is { } g ? g.DepRadiusMax * s.Scale : 0f;
    readonly PlacementCache<TriangleGrid> _shellGrids;
    readonly PlacementCache<TriangleSoup> _shellDeps;

    /// <summary>One pair measured: the core contact (normal, penetration, enclosed depth — the rule's reading), and with
    /// <see cref="ReadRadius"/> its facing gap replaced by the gap between the grown soups. The core gap rides beside it.</summary>
    (SoupContact Contact, float CoreGap) Measure(TriangleSoup soup, TriangleGrid grid, FormKey depKey, int depSnap, FormKey depBase, Snap depS,
                                                 FormKey hostKey, int hostSnap, FormKey hostBase, Snap hostS)
    {
        var c = SpatialOverlap.Contact(soup, grid, Reach, NearBand);
        if (!ReadRadius) return (c, c.FacingDistance);
        var ss = ShellDep(depKey, depSnap, depBase, depS); var sg = ShellGrid(hostKey, hostSnap, hostBase, hostS);
        var cs = ss is not null && sg is not null ? SpatialOverlap.Contact(ss, sg, Reach, NearBand) : c;
        return (c with { FacingDistance = cs.FacingDistance }, c.FacingDistance);
    }

    TriangleGrid? ShellGrid(FormKey key, int snapIndex, FormKey baseKey, Snap s)
    {
        if (_shellGrids.TryGetValue((key, snapIndex, baseKey), out var g)) return g;
        var geom = GeomOf(baseKey);
        TriangleGrid? grid = null;
        if (geom is { CanHost: true })
            grid = TriangleGrid.BuildAdaptive(SpatialOverlap.TransformSoup(geom.HostShell ?? geom.HostSoup, s.Pos, s.Rot, s.Scale));
        return _shellGrids[(key, snapIndex, baseKey)] = grid;
    }

    TriangleSoup? ShellDep(FormKey key, int snapIndex, FormKey baseKey, Snap s)
    {
        if (_shellDeps.TryGetValue((key, snapIndex, baseKey), out var so)) return so;
        var geom = GeomOf(baseKey);
        TriangleSoup? soup = null;
        if (geom is { CanDepend: true })
            soup = SpatialOverlap.TransformSoup(geom.DepShell ?? geom.DepSoup, s.Pos, s.Rot, s.Scale);
        return _shellDeps[(key, snapIndex, baseKey)] = soup;
    }

    /// <param name="geomOf">geometry for a base record — the driver resolves meshes through the VFS, the proof builds
    /// box soups from OBND. Null (or Src '-') when there is none; cached here per base.</param>
    /// <param name="cacheBudgetBytes">the per-placement caches' shared byte budget; null takes <see cref="CacheBudget.Auto()"/>.</param>
    public ContactIndex(Func<FormKey, RefGeometry?> geomOf, long? cacheBudgetBytes = null)
    {
        _geomOf = geomOf; Arms = new EnableArms(Index);
        Budget = new CacheBudget(cacheBudgetBytes ?? CacheBudget.Auto());
        _hostGrids = new(Budget, "hostGrids", g => g.ApproxBytes);
        _depSoups = new(Budget, "depSoups", PlacementCache<TriangleSoup>.SoupBytes);
        _shellGrids = new(Budget, "shellGrids", g => g.ApproxBytes);
        _shellDeps = new(Budget, "shellDeps", PlacementCache<TriangleSoup>.SoupBytes);
        _armGrids = new(Budget, "armGrids", g => g.ApproxBytes);
    }

    /// <summary>Lookups that asked a (reference, snap) for a different base than the previous lookup there, per cache — only
    /// counted with env HC_STALEBASE set (a diagnostic for comparing against the (reference, snap)-keyed build).</summary>
    /// <summary>The largest host grid built this run (reference, snap, base, bytes, triangles) — for the end-of-run report.</summary>
    public ((FormKey Key, int Snap, FormKey Base) Key, long Bytes, int Triangles)? LargestHostGrid => _hostGrids.Largest;

    public IEnumerable<(string Cache, long Count, IReadOnlyList<(FormKey Key, int Snap, FormKey Was, FormKey Now)> Keys)> StaleBase()
    {
        var none = Array.Empty<(FormKey, int, FormKey, FormKey)>();
        yield return ("hostGrids", _hostGrids.StaleBaseLookups, (IReadOnlyList<(FormKey, int, FormKey, FormKey)>?)_hostGrids.StaleBaseKeys ?? none);
        yield return ("depSoups", _depSoups.StaleBaseLookups, (IReadOnlyList<(FormKey, int, FormKey, FormKey)>?)_depSoups.StaleBaseKeys ?? none);
        yield return ("shellGrids", _shellGrids.StaleBaseLookups, (IReadOnlyList<(FormKey, int, FormKey, FormKey)>?)_shellGrids.StaleBaseKeys ?? none);
        yield return ("shellDeps", _shellDeps.StaleBaseLookups, (IReadOnlyList<(FormKey, int, FormKey, FormKey)>?)_shellDeps.StaleBaseKeys ?? none);
    }

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

    /// <summary>A reference's runtime enable state under the winner: its winning flag and winning XESP, the chain
    /// followed through everything else's winning state. A reference the order does not place, or has deleted, is
    /// unknown to the chain (reported unresolved). <see cref="EnableArms.Winner"/>.</summary>
    public EnableResolution WinnerEnable(FormKey key) => Arms.Winner(key);

    /// <summary>A reference's runtime enable state in the arm WITHOUT <paramref name="mover"/>'s side: each chain
    /// reference's flag at its authoring snap and its XESP as last set outside the mover's side. Memoised per mover.
    /// <see cref="EnableArms.Without"/>.</summary>
    public EnableResolution ArmEnable(FormKey key, int mover, IReadOnlyList<HashSet<int>> closure)
        => Arms.Without(key, mover, closure);

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
            var (p, _) = Arms.EnableParentAt(k, int.MaxValue);
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
        if (_hostGrids.TryGetValue((key, snapIndex, baseKey), out var g) && (g is not null || !anySurface)) return g;
        var geom = GeomOf(baseKey);
        TriangleGrid? grid = null;
        if (geom is { CanHost: true } || (anySurface && geom is { HasGeometry: true } && geom.HostSoup.TriangleCount > 0))
            grid = TriangleGrid.BuildAdaptive(SpatialOverlap.TransformSoup(geom.HostSoup, s.Pos, s.Rot, s.Scale));
        return _hostGrids[(key, snapIndex, baseKey)] = grid;
    }

    /// <summary>A neighbour's host grid at its AUTHORING placement, for the differential Sunk rule. Keyed by base as well as snap: the
    /// arm's base can differ from the winner's at the same snap (a base swap is not a position step). Its own kind in the shared byte
    /// budget (<see cref="Budget"/>), counted and evicted with the other four.</summary>
    TriangleGrid? ArmGrid(FormKey key, int snapIndex, FormKey baseKey, Snap s)
    {
        if (_armGrids.TryGetValue((key, snapIndex, baseKey), out var g)) return g;
        var geom = GeomOf(baseKey);
        TriangleGrid? grid = geom is { CanHost: true } ? TriangleGrid.BuildAdaptive(SpatialOverlap.TransformSoup(geom.HostSoup, s.Pos, s.Rot, s.Scale)) : null;
        return _armGrids[(key, snapIndex, baseKey)] = grid;
    }
    readonly PlacementCache<TriangleGrid> _armGrids;

    TriangleSoup? DepSoup(FormKey key, int snapIndex, FormKey baseKey, Snap s)
    {
        if (_depSoups.TryGetValue((key, snapIndex, baseKey), out var so)) return so;
        var geom = GeomOf(baseKey);
        TriangleSoup? soup = null;
        if (geom is { CanDepend: true })
            soup = SpatialOverlap.TransformSoup(geom.DepSoup, s.Pos, s.Rot, s.Scale);
        return _depSoups[(key, snapIndex, baseKey)] = soup;
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
            string notFound = "NoQualifyingStep"; double moved = double.NaN;               // trace only: why found stays false
            // A deletion that stripped the placement leaves the history one snap short: the winning snap is the
            // last PLACED one, and the deleting plugin is the mover of a Removed host.
            // Q34: the removal is read from the record that WINS, not from the last plugin that ever deleted the
            // reference. A deletion later undone by a restoring record is no removal at all, and the deleting plugin
            // is the mover only while its deletion still stands.
            int delBy = Index.Deletions.WinningDeleter(key);
            if (snaps.Count >= 1 && delBy >= 0 && delBy > snaps[^1].Plugin && !snaps[^1].Deleted)
            {
                prev = snaps[^1]; prevIndex = snaps.Count - 1;
                win = prev with { Plugin = delBy, Deleted = true }; change = HostChange.Removed; mover = delBy; found = true;
            }
            else if (snaps.Count >= 2)
            {
                win = snaps[^1]; prev = snaps[^2]; prevIndex = snaps.Count - 2; mover = win.Plugin; found = true;
                if (prev.Deleted) { found = false; notFound = "NotPresentBefore"; }       // it was not there before either
                else if (win.Deleted || Index.IsDeletedNow(key) || !WinnerEnable(key).Enabled) change = HostChange.Removed;
                else if (win.Space != prev.Space) change = HostChange.Relocated;
                else if (PlacementHistory.Dist(prev.Pos, win.Pos) > MoveThreshold) change = HostChange.Moved;
                else { found = false; notFound = "NotMovedOverThreshold"; moved = PlacementHistory.Dist(prev.Pos, win.Pos); }
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
            bool tw = Tw(key);
            const string MH = TraceRole.MovedHost;
            if (!found)
            {
                if (tw) Te(key, MH, TraceOutcome.Never, notFound, values: notFound == "NotMovedOverThreshold" ? Tv("move", moved, "limit", MoveThreshold) : null,
                           detail: notFound == "NoQualifyingStep" ? $"{snaps.Count} placement snap(s); no later step moved, relocated or removed it" : null);
                continue;
            }
            if (excludeMover?.Invoke(mover) == true) { if (tw) Te(key, MH, TraceOutcome.Never, "MoverExcluded", detail: $"mover plugin {mover}"); continue; }
            var prevBase = ArmBase(key, mover, closure);
            var winBase = WinnerBase(key);
            if (prevBase.IsNull) { if (tw) Te(key, MH, TraceOutcome.Never, "NoArmBase"); continue; }
            if (!IsHostType(prevBase)) { if (tw) Te(key, MH, TraceOutcome.Never, "NotHostType", detail: $"base {prevBase}"); continue; }
            var armEn = ArmEnable(key, mover, closure);
            if (!armEn.Resolved) { skips.HostEnableUnresolved++; if (tw) Te(key, MH, TraceOutcome.Unjudgeable, "EnableChainUnresolved"); continue; }
            if (!armEn.Enabled) { skips.HostRuntimeDisabled++; if (tw) Te(key, MH, TraceOutcome.SetAside, "RuntimeDisabledInArm"); continue; }   // it did not exist before either
            var box = WorldBox(prevBase, prev);
            if (box.IsEmpty) { skips.HostNoGeometry++; if (tw) Te(key, MH, TraceOutcome.Unjudgeable, "NoGeometry"); continue; }
            if (GeomOf(prevBase) is not { CanHost: true }) { skips.HostNoCollision++; if (tw) Te(key, MH, TraceOutcome.SetAside, "NoSolidCollision"); continue; }
            float dim = MaxDim(box);
            if (dim > MaxHostDim) { skips.HostVolume++; if (tw) Te(key, MH, TraceOutcome.SetAside, "VolumeScale", values: Tv("maxDim", dim, "limit", MaxHostDim)); continue; }
            if (dim < MinHostDim) { skips.HostTooSmall++; if (tw) Te(key, MH, TraceOutcome.SetAside, "TooSmallToHost", values: Tv("maxDim", dim, "limit", MinHostDim)); continue; }
            if (tw) Te(key, MH, TraceOutcome.Entered, change.ToString(), detail: $"mover plugin {mover}{(viaParent ? ", through its enable parent" : "")}");
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
            // Trace: one role-level event per key, decided at the LAST snap (the snap the coverage counters count), with the
            // number of snaps hashed in the detail — the authoring arm may still judge an earlier one.
            bool tw = Tw(key);
            int hashed = 0;
            string lastOutcome = TraceOutcome.Entered, lastRule = "Hashed"; IReadOnlyDictionary<string, double>? lastValues = null;
            for (int i = 0; i < snaps.Count; i++)
            {
                var s = snaps[i];
                bool last = i == snaps.Count - 1;
                if (s.Deleted) { if (last) (lastOutcome, lastRule) = (TraceOutcome.Never, "DeletedSnap"); continue; }
                var b = BaseAt(key, s.Plugin);
                if (!IsDependentType(b)) { if (last) (lastOutcome, lastRule) = (TraceOutcome.Never, "NotDependentType"); continue; }
                var box = WorldBox(b, s);
                if (box.IsEmpty) { if (last) { skips.DepNoGeometry++; (lastOutcome, lastRule) = (TraceOutcome.Unjudgeable, "NoGeometry"); } continue; }
                if (MaxDim(box) > MaxDependentDim)
                {
                    if (last) { skips.DepVolume++; (lastOutcome, lastRule) = (TraceOutcome.SetAside, "OverDependentSize"); if (tw) lastValues = Tv("maxDim", MaxDim(box), "limit", MaxDependentDim); }
                    continue;
                }
                h.Add(s.Space, box, (key, i));
                hashed++;
            }
            if (tw) Te(key, TraceRole.Dependent, lastOutcome, lastRule, values: lastValues, detail: $"{hashed} of {snaps.Count} snap(s) hashed");
        }
        return h;
    }

    /// <summary>Every physical reference (host- or dependent-typed, ANY size — R3) that CAN HOLD something — solid
    /// collision — at its winning placement, undeleted and enabled at runtime under the winner: the winner arm's
    /// population. A reference whose enable chain cannot be followed is left out and counted.</summary>
    public BoxHash<FormKey> PhysicalWinners(Func<Snap, bool>? inScope = null, ContactSkips? skips = null)
    {
        var h = new BoxHash<FormKey>();
        const string W = TraceRole.WinnerArm;
        foreach (var (key, snaps) in Index.History.Refs)
        {
            bool tw = Tw(key);
            var s = snaps[^1];
            if (s.Deleted || Index.IsDeletedNow(key)) { if (tw) Te(key, W, TraceOutcome.Never, "DeletedNow"); continue; }
            if (inScope is not null && !inScope(s)) { if (tw) Te(key, W, TraceOutcome.Never, "OutOfScope"); continue; }
            var b = WinnerBase(key);
            if (!IsPhysicalType(b) || GeomOf(b) is not { CanHost: true }) { if (tw) Te(key, W, TraceOutcome.Never, "NotPhysicalCanHost"); continue; }
            var en = WinnerEnable(key);
            if (!en.Resolved) { if (skips is not null) skips.WinnerEnableUnresolved++; if (tw) Te(key, W, TraceOutcome.Unjudgeable, "EnableChainUnresolved"); continue; }
            if (!en.Enabled) { if (skips is not null) skips.WinnerRuntimeDisabled++; if (tw) Te(key, W, TraceOutcome.SetAside, "RuntimeDisabled"); continue; }
            var box = WorldBox(b, s);
            if (box.IsEmpty) { if (tw) Te(key, W, TraceOutcome.Never, "NoWinnerBox"); continue; }
            if (tw) Te(key, W, TraceOutcome.Entered, "Hashed", detail: "winner arm");
            h.Add(s.Space, box, key); HostPadW(b, s);
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
        const string W = TraceRole.WinnerArm; const string Arm = "arm without the labelled mover";
        foreach (var (key, snaps) in Index.History.Refs)
        {
            bool tw = Tw(key);
            int i = AuthoringSnapIndex(snaps, mover, closure);
            if (i < 0) { if (tw) Te(key, W, TraceOutcome.Never, "IntroducedByMover", detail: Arm); continue; }
            var s = snaps[i];
            if (s.Deleted) { if (tw) Te(key, W, TraceOutcome.Never, "DeletedSnap", detail: Arm); continue; }
            if (inScope is not null && !inScope(s)) { if (tw) Te(key, W, TraceOutcome.Never, "OutOfScope", detail: Arm); continue; }
            var b = ArmBase(key, mover, closure);
            if (b.IsNull || !IsPhysicalType(b) || GeomOf(b) is not { CanHost: true }) { if (tw) Te(key, W, TraceOutcome.Never, "NotPhysicalCanHost", detail: Arm); continue; }
            var en = ArmEnable(key, mover, closure);
            if (!en.Resolved) { if (skips is not null) skips.WinnerEnableUnresolved++; if (tw) Te(key, W, TraceOutcome.Unjudgeable, "EnableChainUnresolved", detail: Arm); continue; }
            if (!en.Enabled) { if (skips is not null) skips.WinnerRuntimeDisabled++; if (tw) Te(key, W, TraceOutcome.SetAside, "RuntimeDisabled", detail: Arm); continue; }
            var box = WorldBox(b, s);
            if (box.IsEmpty) { if (tw) Te(key, W, TraceOutcome.Never, "NoWinnerBox", detail: Arm); continue; }
            if (tw) Te(key, W, TraceOutcome.Entered, "Hashed", detail: Arm);
            h.Add(s.Space, box, key); HostPadW(b, s);
            snapIndex[key] = i;
        }
        return h;
    }

    /// <summary>The index of a reference's snap in the order without <paramref name="mover"/>'s side: the last one whose
    /// plugin is neither the mover nor downstream of it. −1 when every snap is the mover's side (it introduced the ref).
    /// The body moved to <see cref="PlacementHistory.AuthoringSnapIndex"/> in Q27 (the enable arms ask it too); this
    /// forwards, so every caller here is unchanged.</summary>
    public static int AuthoringSnapIndex(IReadOnlyList<Snap> snaps, int mover, IReadOnlyList<HashSet<int>> closure)
        => PlacementHistory.AuthoringSnapIndex(snaps, mover, closure);

    // ---- the best contact of one dependent against a population ------------------------------------

    /// <summary>The dependent's best contact against everything in <paramref name="hash"/> (itself excluded) whose box
    /// is within the box band: the deepest penetration if any penetrates, else the least distance. Null when nothing is
    /// within the band, or the dependent has no soup. <paramref name="placementOf"/> gives each neighbour's snap in this
    /// arm; <paramref name="baseOf"/> its base then. The against-ref of the best contact is the one the reported depth
    /// was measured against, and the arm carries how wide that ref is along the contact normal (R4).
    /// <para><paramref name="preexistingDepth"/> (the differential Sunk rule, opt-in; <see cref="Judge"/> passes it): for a neighbour
    /// the dependent penetrates deeper than Îµ, how deep the dependent already sat in that same reference in the authoring arm
    /// (0 when it did not exist there or did not overlap). Neighbours are then ranked by NEW depth (penetration minus that), so a
    /// deep authored overlap cannot mask a shallower new one. A neighbour whose new depth is within Îµ is a PRE-EXISTING overlap:
    /// it is chosen only when nothing else touches within Îµ, and the result then says so (<see cref="ArmContact.PreexistingOnly"/>).
    /// Without the function the selection is exactly the one before the rule.</para></summary>
    public ArmContact? BestContact(FormKey depKey, int depSnapIndex, FormKey depBase, Snap depSnap, Aabb depBox,
                                   BoxHash<FormKey> hash, Func<FormKey, (Snap Snap, int SnapIndex)?> placementOf, Func<FormKey, int, FormKey> baseOf,
                                   out int neighboursTested, Func<FormKey, float>? preexistingDepth = null,
                                   Func<FormKey, DirectionalCoverageResult, CoverageInputs, AuthoredCoverageReading>? authoredCoverage = null,
                                   bool coverageMaySelect = true)
    {
        neighboursTested = 0;
        var soup = DepSoup(depKey, depSnapIndex, depBase, depSnap);
        if (soup is null) return null;
        var depGeom = GeomOf(depBase)!;
        ArmContact? best = null; float bestRank = 0f;          // the best contact that is not a pre-existing overlap, ranked by new depth
        ArmContact? bestPre = null;                            // the deepest pre-existing overlap
        List<(FormKey, float, float)>? deep = null;            // diagnostic: every neighbour deeper than Îµ and its authored depth
        ArmContact? bestCover = null, incompleteCover = null;
        List<CoverageNeighbour>? covers = null;
        bool readCover = ReadRadius && preexistingDepth is not null && authoredCoverage is not null && depBox.Size.Z <= Epsilon;
        float bestRaw = float.PositiveInfinity; FormKey rawAgainst = default;
        float depPad = DepPadW(depBase, depSnap);
        foreach (var (nKey, nBox, _) in hash.Near(depSnap.Space, depBox, BoxBand + depPad + (ReadRadius ? _maxHostRadiusW : 0f)))
        {
            if (nKey == depKey) continue;
            if (placementOf(nKey) is not { } np) continue;
            var nBase = baseOf(nKey, np.Snap.Plugin);
            if (ReadRadius && SpatialOverlap.BoxDistance(depBox, nBox) > BoxBand + depPad + HostPadW(nBase, np.Snap)) continue;
            var grid = HostGrid(nKey, np.SnapIndex, nBase, np.Snap);
            if (grid is null) continue;
            neighboursTested++;
            var (c, coreGap) = Measure(soup, grid, depKey, depSnapIndex, depBase, depSnap, nKey, np.SnapIndex, nBase, np.Snap);
            if (c.Distance < bestRaw) { bestRaw = c.Distance; rawAgainst = nKey; }
            float pen = c.EffectivePenetration, pre = 0f;
            float pd = preexistingDepth is not null && pen > Epsilon ? preexistingDepth(nKey) : float.NaN;
            if (pd > 0f) pre = pd;
            if (!float.IsNaN(pd)) (deep ??= new()).Add((nKey, pen, pd));
            float rank = pen - pre;
            bool isPre = pre > 0f && rank <= Epsilon;
            var hg = GeomOf(nBase)!;
            float extent = c.Normal.LengthSquared() > 1e-12f ? grid.ExtentAlong(Vector3.Normalize(c.Normal)) : MaxDim(nBox);
            var arm = new ArmContact(c, nKey, depGeom.DepSrc, hg.HostSrc, extent, MaxDim(nBox)) { CoreGap = coreGap, PreexistingDepth = pre, PreexistingOnly = isPre };
            if (readCover && PlausibleShallowCover(depBox, grid, Epsilon, SupportCos))
            {
                var input = new CoverageInputs(depBase, nBase, depGeom.DepSrc, hg.HostSrc,
                    depSnap, np.Snap, WinnerEnable(depKey), WinnerEnable(nKey), soup, grid, Vector3.UnitZ, Epsilon, SupportCos);
                var cover = DirectionalCoverage.Measure(soup, grid, input.Direction, input.MaxGap, input.FaceCos,
                    input.MaxTrianglePairs, input.MaxPieces, input.Tolerance);
                AuthoredCoverageReading? reading = null;
                if (WholeCover(cover) || !cover.Complete || cover.FullCoverReading == "numerically_uncertain")
                    reading = authoredCoverage!(nKey, cover, input);
                var authored = reading?.Result;
                CoverageInputComparison? comparison = null;
                if (reading is not null)
                    comparison = !cover.Complete ? CoverageInputComparison.Unknown("winner_coverage_incomplete: " + cover.RefusalReason)
                        : !reading.Result.Complete ? CoverageInputComparison.Unknown("authored_coverage_incomplete: " + reading.Result.RefusalReason)
                        : reading.KnownAbsence is not null ? new("different", reading.KnownAbsence, 0)
                        : CoverageInputComparisonReader.CompareExact(input, reading.Inputs);
                bool unchanged = cover.Complete && authored is { Complete: true } && comparison?.ExactlyEqual == true;
                (covers ??= new()).Add(new(nKey, cover, authored) { InputComparison = comparison });
                var witness = arm with { Coverage = cover, AuthoredCoverage = authored, CoverageInputsExactlyUnchanged = unchanged };
                // Exact complete input identity proves no differential change. Positive remainders and absolute
                // uncertainty stay in the raw readings; incomplete/missing work is never forgiven by this flag.
                if (!unchanged)
                {
                    bool uncertain = !cover.Complete || cover.FullCoverReading == "numerically_uncertain"
                        || authored is { Complete: false } || authored?.FullCoverReading == "numerically_uncertain";
                    if (uncertain) incompleteCover ??= witness;
                    else if (WholeCover(cover) && authored is not null && !WholeCover(authored)
                        && (bestCover is null || cover.MaxGap > bestCover.Coverage!.MaxGap)) bestCover = witness;
                }
            }
            if (isPre ? bestPre is not null && pen <= bestPre.Contact.EffectivePenetration
                      : best is not null && !Better(rank, c.FacingDistance, bestRank, best.Contact.FacingDistance)) continue;
            if (isPre) bestPre = arm; else { best = arm; bestRank = rank; }
        }
        var chosen = best is not null && bestRank > Epsilon ? best
                   : (coverageMaySelect ? bestCover ?? incompleteCover : null) ?? ChooseContact(best, bestRank, bestPre, Epsilon);
        return chosen is null ? null : chosen with { RawDistance = bestRaw, RawAgainst = rawAgainst, DeepNeighbours = deep, CoverageNeighbours = covers };
    }

    static DetectorModel.CoverageFacts? CoverFacts(DirectionalCoverageResult? c) => c is null ? null
        : new(c.ProjectedArea, c.CoveredArea, c.UncoveredArea, c.MinGap, c.MaxGap, c.Complete,
            c.RefusalReason, c.FullCoverReading, c.UncertaintyReason);

    static bool WholeCover(DirectionalCoverageResult c) => c.Complete && c.ProjectedArea > 0
        && c.FullCoverReading == "nominal_whole_cover" && c.UncoveredArea == 0;

    static bool PlausibleShallowCover(Aabb dep, TriangleGrid host, float gap, float faceCos)
    {
        for (int i = 0; i < host.TriangleCount; i++)
        {
            if (host.UnitNormal(i).Z < faceCos) continue;
            var b = host.BoundsOf(i);
            if (b.Max.X < dep.Min.X || b.Min.X > dep.Max.X || b.Max.Y < dep.Min.Y || b.Min.Y > dep.Max.Y) continue;
            if (b.Max.Z > dep.Min.Z && b.Min.Z <= dep.Max.Z + gap) return true;
        }
        return false;
    }

    /// <summary>The differential selection's last step: a pre-existing overlap (<paramref name="bestPre"/>) stands as the contact only
    /// when the best other contact (<paramref name="best"/>, its new depth <paramref name="bestRank"/>) neither sinks nor touches.</summary>
    public static ArmContact? ChooseContact(ArmContact? best, float bestRank, ArmContact? bestPre, float epsilon)
    {
        // "Touches" exactly as the rule reads it: a new depth over Îµ (Sunk) or a facing gap within Îµ (ReSupported). A contact that only
        // grazes (0 < depth <= Îµ) with a facing gap over Îµ reads Stranded/Orphaned, so it must not displace a pre-existing overlap.
        bool bestTouches = best is not null && (bestRank > epsilon || best.Contact.FacingDistance <= epsilon);
        return bestPre is not null && !bestTouches ? bestPre : best;
    }

    /// <summary>Depth first (new depth under the differential rule, raw penetration otherwise), then the facing gap.</summary>
    static bool Better(float depthA, float facingA, float depthB, float facingB)
    {
        if (depthA > 0f || depthB > 0f) return depthA > depthB;
        return facingA < facingB;
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
        var (c, coreGap) = Measure(soup, grid, depKey, depSnapIndex, depBase, depSnap, hostKey, hostSnapIndex, hostBase, hostSnap);
        float extent = c.Normal.LengthSquared() > 1e-12f ? grid.ExtentAlong(Vector3.Normalize(c.Normal)) : float.NaN;
        return new ArmContact(c, hostKey, GeomOf(depBase)!.DepSrc, GeomOf(hostBase)!.HostSrc, extent, RawDistance: c.Distance, RawAgainst: hostKey) { CoreGap = coreGap };
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

        const string DR = TraceRole.Dependent;
        foreach (var ((depKey, si), depBox, _) in depSnaps.Near(host.Previous.Space, hostBox, Reach))
        {
            var snaps = Index.History.Of(depKey)!;
            bool tw = Tw(depKey);
            bool lastSnap = si == snaps.Count - 1;
            if (depKey == host.Key) { if (tw && lastSnap) Te(depKey, DR, TraceOutcome.Never, "SelfPair", host.Key); continue; }
            int ai = AuthoringSnapIndex(snaps, host.Mover, closure);
            if (ai < 0) { if (lastSnap) { skips.DepIntroducedByMover++; if (tw) Te(depKey, DR, TraceOutcome.SetAside, "DepIntroducedByMover", host.Key); } continue; }
            if (ai != si)                                                        // this snap is not the authoring arm's
            {
                // Trace: only when the authoring snap was never hashed (so no other event for this pair can follow).
                if (tw && lastSnap && !HashedAsDependent(depKey, snaps, ai))
                    Te(depKey, DR, TraceOutcome.Never, "NotAuthoringSnap", host.Key, detail: $"authoring snap {ai} was not hashed as a dependent");
                continue;
            }
            var depAuth = snaps[ai];
            if (depAuth.Space != host.Previous.Space) { skips.DepOtherSpace++; if (tw) Te(depKey, DR, TraceOutcome.SetAside, "DepOtherSpace", host.Key); continue; }
            var depArmEn = ArmEnable(depKey, host.Mover, closure);
            if (!depArmEn.Resolved) { skips.DepEnableUnresolved++; if (tw) Te(depKey, DR, TraceOutcome.Unjudgeable, "DepEnableChainUnresolved", host.Key); continue; }
            if (!depArmEn.Enabled) { skips.DepAbsentInArm++; if (tw) Te(depKey, DR, TraceOutcome.SetAside, "DepAbsentInArm", host.Key); continue; }   // never there to rest on anything
            var depBase = ArmBase(depKey, host.Mover, closure);
            if (depBase.IsNull) { skips.DepIntroducedByMover++; if (tw) Te(depKey, DR, TraceOutcome.SetAside, "DepIntroducedByMover", host.Key, detail: "no base in the arm without the mover"); continue; }
            var depArmBox = WorldBox(depBase, depAuth);
            if (depArmBox.IsEmpty) depArmBox = depBox;
            float gap = SpatialOverlap.BoxDistance(depArmBox, hostBox);
            if (gap > BoxBand + DepPadW(depBase, depAuth) + HostPadW(host.PrevBase, host.Previous)) { skips.BoxesApart++; if (tw) Te(depKey, DR, TraceOutcome.SetAside, "BoxesApart", host.Key, Tv("gap", gap, "limit", BoxBand)); continue; }

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
            // re-judgement at a larger Îµ has the facts it needs (the report's sensitivity table).
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
                if (tw) TraceJudged(depKey, host.Key, r0.Relation, r0.Verdict, r0.Reason, false, auth);
                if (r0.Verdict != ContactVerdict.NoRelation || keepNoRelation)
                    rows.Add(new ContactRow(host, depKey, depAuth, depWin, depBase, depAuth.Plugin, seen, authorAfter, touched,
                                            ScaleOf(depArmBox), hostScale, motion, auth, null, f0, r0.Relation, r0.Verdict, r0.Reason, r0.Why));
                continue;
            }

            // The winner arm: the dependent where the winner leaves it, against everything physical near it.
            var winBase = WinnerBase(depKey);
            var winBox = WorldBox(winBase, depWin);
            // The differential Sunk rule: how deep the dependent already sat in a neighbour as authored — both at their authoring
            // placements in the arm without the host's mover (GATE-A §3.3, closed-slabs lane). The dependent's authoring soup is
            // built once per row, and only when some neighbour penetrates deeper than Îµ.
            TriangleSoup? depAuthSoup = null; bool depAuthBuilt = false;
            float Preexisting(FormKey nKey)
            {
                if (!depAuthBuilt) { depAuthBuilt = true; depAuthSoup = depGeom is { CanDepend: true } ? SpatialOverlap.TransformSoup(depGeom.DepSoup, depAuth.Pos, depAuth.Rot, depAuth.Scale) : null; }
                if (depAuthSoup is null) return 0f;
                var ns = Index.History.Of(nKey);
                if (ns is null || ns.Count == 0) return 0f;
                int ni = AuthoringSnapIndex(ns, host.Mover, closure);
                if (ni < 0) return 0f;                                           // the mover's side introduced it
                var nAuth = ns[ni];
                if (nAuth.Deleted || nAuth.Space != depAuth.Space) return 0f;
                var nEn = ArmEnable(nKey, host.Mover, closure);
                if (!nEn.Resolved || !nEn.Enabled) return 0f;                     // not there at runtime as authored
                var nBase = ArmBase(nKey, host.Mover, closure);
                if (nBase.IsNull) return 0f;
                var grid = ArmGrid(nKey, ni, nBase, nAuth);
                if (grid is null) return 0f;
                return SpatialOverlap.Contact(depAuthSoup, grid, Reach, NearBand).EffectivePenetration;   // the core depth, as Measure reads it
            }
            AuthoredCoverageReading AuthoredCover(FormKey nKey, DirectionalCoverageResult winner, CoverageInputs winnerInput)
            {
                AuthoredCoverageReading Absent(string reason) => new(new(winner.ProjectedArea, 0, winner.ProjectedArea,
                    null, null, true, null, 0, "partial", 0, 0), KnownAbsence: reason);
                AuthoredCoverageReading Unknown(string reason) => new(new(winner.ProjectedArea, 0, winner.ProjectedArea,
                    null, null, false, reason, winner.ProjectedArea, "incomplete", 0, 0));
                var ns = Index.History.Of(nKey);
                if (ns is null || ns.Count == 0) return Absent("authored_reference_known_absent");
                int ni = AuthoringSnapIndex(ns, host.Mover, closure);
                if (ni < 0) return Absent("authored_reference_introduced_by_mover");
                var nAuth = ns[ni];
                if (nAuth.Deleted || nAuth.Space != depAuth.Space) return Absent("authored_reference_deleted_or_other_space");
                var en = ArmEnable(nKey, host.Mover, closure);
                if (!en.Resolved) return Unknown("authored_enable_unresolved");
                if (!en.Enabled) return Absent("authored_reference_runtime_absent");
                var nBase = ArmBase(nKey, host.Mover, closure);
                if (nBase.IsNull) return Unknown("authored_base_missing");
                var geom = GeomOf(nBase);
                if (geom is null || !geom.HasGeometry) return Unknown("authored_host_geometry_missing");
                if (!geom.CanHost) return Unknown("authored_host_surface_unavailable");
                if (!depAuthBuilt) { depAuthBuilt = true; depAuthSoup = depGeom is { CanDepend: true }
                    ? SpatialOverlap.TransformSoup(depGeom.DepSoup, depAuth.Pos, depAuth.Rot, depAuth.Scale) : null; }
                if (depAuthSoup is null) return Unknown("authored_dependent_geometry_missing");
                var grid = ArmGrid(nKey, ni, nBase, nAuth);
                if (grid is null) return Unknown("authored_host_grid_missing");
                var input = new CoverageInputs(depBase, nBase, depGeom!.DepSrc, geom.HostSrc, depAuth, nAuth,
                    depArmEn, en, depAuthSoup, grid, winnerInput.Direction, winnerInput.MaxGap, winnerInput.FaceCos,
                    winnerInput.MaxTrianglePairs, winnerInput.MaxPieces, winnerInput.Tolerance);
                return new(DirectionalCoverage.Measure(depAuthSoup, grid, input.Direction, input.MaxGap, input.FaceCos,
                    input.MaxTrianglePairs, input.MaxPieces, input.Tolerance), input);
            }
            var win = winBox.IsEmpty ? null
                    : BestContact(depKey, snaps.Count - 1, winBase, depWin, winBox, winners, WinnerPlacement, (k, _) => WinnerBase(k), out _, Preexisting, ReadRadius ? AuthoredCover : null,
                        coverageMaySelect: r0.Verdict == ContactVerdict.Unjudgeable && r0.Reason == ContactBlocker.WinnerArmMissing);
            bool winMeasured = !winBox.IsEmpty;
            var f = f0 with
            {
                WinnerMeasured = winMeasured,
                WinnerDistance = win?.Contact.FacingDistance ?? Reach,
                WinnerPenetration = win?.Contact.EffectivePenetration ?? 0f,
                WinnerContactIsHost = win is not null && win.Against == host.Key,
                WinnerAgainstExtent = win?.AgainstExtent ?? float.NaN,
                WinnerPreexistingPenetration = win?.PreexistingDepth ?? 0f,
                WinnerPreexistingOnly = win?.PreexistingOnly ?? false,
                WinnerCoverage = CoverFacts(win?.Coverage),
                WinnerAuthoredCoverage = CoverFacts(win?.AuthoredCoverage),
                WinnerCoverageInputsExactlyUnchanged = win?.CoverageInputsExactlyUnchanged ?? false,
            };
            var r = DetectorModel.PlacementContact(f, Epsilon, SupportCos, AttachCos);
            if (tw) TraceJudged(depKey, host.Key, r.Relation, r.Verdict, r.Reason, nearMiss, auth);
            if (r.Verdict == ContactVerdict.NoRelation && !keepNoRelation && !nearMiss) continue;
            rows.Add(new ContactRow(host, depKey, depAuth, depWin, depBase, depAuth.Plugin, seen, authorAfter, touched,
                                    ScaleOf(depArmBox), hostScale, motion, auth, win, f, r.Relation, r.Verdict, r.Reason, r.Why));
        }
        return rows;
    }

    /// <summary>Trace a judged pair (GATE-A §2.1, §12 M2): a clash verdict is <c>judged_relation</c>; a correct non-clash
    /// (MovedTogether, ReSupported, Removed) is <c>judged_relation_not_clash</c>; a near miss (no relation at Îµ, inside the
    /// near band) is <c>judged_below_threshold / NearMiss</c> with the distance, Îµ and the margin; otherwise
    /// <c>judged_no_relation</c>. Unjudgeable names its blocker.</summary>
    void TraceJudged(FormKey dep, FormKey host, ContactRelation rel, ContactVerdict verdict, ContactBlocker reason, bool nearMiss, ArmContact? auth)
    {
        const string DR = TraceRole.Dependent;
        string detail = $"{rel}/{verdict}";
        switch (verdict)
        {
            case ContactVerdict.Unjudgeable:
                Te(dep, DR, TraceOutcome.Unjudgeable, reason.ToString(), host, detail: detail); break;
            case ContactVerdict.NoRelation when nearMiss && auth is { } ac:
                double d = ac.Contact.FacingDistance;
                Te(dep, DR, TraceOutcome.JudgedBelowThreshold, "NearMiss", host,
                   new Dictionary<string, double> { ["distance"] = d, ["penetration"] = ac.Contact.EffectivePenetration, ["epsilon"] = Epsilon,
                                                    ["margin"] = d - Epsilon, ["nearBand"] = NearBand }, detail); break;
            case ContactVerdict.NoRelation:
                Te(dep, DR, TraceOutcome.JudgedNoRelation, "NoRelation", host,
                   auth is { } an ? Tv("distance", an.Contact.FacingDistance, "penetration", an.Contact.EffectivePenetration) : null,
                   auth is null ? detail + " (no authoring contact within reach)" : detail); break;
            case ContactVerdict.MovedTogether or ContactVerdict.ReSupported or ContactVerdict.Removed:
                Te(dep, DR, TraceOutcome.JudgedRelationNotClash, verdict.ToString(), host, detail: detail); break;
            default:
                Te(dep, DR, TraceOutcome.JudgedRelation, verdict.ToString(), host, detail: detail); break;
        }
    }

    /// <summary>Whether <see cref="DependentSnaps"/> hashed snap <paramref name="i"/> of this reference (the same predicate).</summary>
    bool HashedAsDependent(FormKey key, IReadOnlyList<Snap> snaps, int i)
    {
        var s = snaps[i];
        if (s.Deleted) return false;
        var b = BaseAt(key, s.Plugin);
        if (!IsDependentType(b)) return false;
        var box = WorldBox(b, s);
        return !box.IsEmpty && MaxDim(box) <= MaxDependentDim;
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
