using System.Numerics;
using HousecarlCore;
using Mutagen.Bethesda.Plugins;

namespace HarnessShared;

// TERRAIN SUPPORT — the pairing (Research/TERRAIN-SUPPORT-DESIGN-2026-10-18.md §4–§6; Gate A 2026-10-20).
//
// A Fixed exterior reference that rested on the ground in its placement owner's view floats over, or is buried in, the ground
// the game loads, because a plugin the owner could not see replaced the LAND under it. Per reference: the owner (the plugin
// that set the position the game loads), the deleted-reference gate, the load-time enable state, the body (the dependent soup
// contactprobe already chooses and labels: collision, else render), the footprint cells, and in each cell the authored LAND
// (the last statement the owner or its masters made) and the winning LAND. Where the two agree within one heightmap unit over
// the footprint there is no row. Otherwise the body's signed vertical clearance against each view's terrain, under both of the
// heightmap's possible triangulations, and the burial (the increase in covered depth), feed DetectorModel.TerrainSupport with
// the winning arm's reference supports (collision only; render never supports). Sight and population per changed cell are
// DetectorModel.TerrainProvenance / TerrainPopulationOf; the worst changed cell names the row.
//
// Geometry here, not in core: the clearance between a triangle soup and a heightmap is exact for two piecewise-linear surfaces
// at the critical points — body vertices, heightmap vertices under a body triangle, and body edges crossing the heightmap's
// grid lines and the chosen diagonal. Burial reads the same points; where the ground meets the body inside a triangle the
// kink is not added, so burial is exact for a uniform shift and a lower bound otherwise (stated on the mode's limits line).

/// <summary>What became of one exterior reference in the terrain pass.</summary>
public enum TerrainStatus
{
    NotObject, DeletedAtLoad, DeletedWithPlacement, DeletedInOwnerView, EnableUnresolved, DisabledAtLoad, Marker, NoBody,
    DuplicateGrid, TerrainAgrees, NoAuthoredGround, NotRestingAsAuthored, AuthoredCannotTell, StillResting, Row, Refused, NotBuilt,
}

/// <summary>One footprint cell: who the owner saw there, who won, how far the two terrains differ under the body, and the cell's
/// sight and population.</summary>
public sealed record TerrainCellReading(int GX, int GY, int AuthoredPlugin, int WinnerPlugin, string AuthoredKind, string WinnerKind,
                                        float MaxDelta, bool Changed, TerrainSight Sight, TerrainPopulation Population, FormKey Cell = default);

/// <summary>One reference judged. <c>Gap</c>/<c>Burial</c> are the folded winning readings; the per-diagonal clearances ride along.</summary>
public sealed record TerrainRow(FormKey Key, FormKey Base, FormKey Worldspace, Snap Pose, int StepPlugin, int Owner, TerrainStatus Status, string Why,
                                char BodySrc = '-', DependentMotion Motion = DependentMotion.Unknown,
                                TerrainAuthored Authored = TerrainAuthored.NotResting, TerrainVerdict Verdict = TerrainVerdict.Refused,
                                float Gap = float.NaN, float Burial = float.NaN, float Saddle = float.NaN,
                                TerrainSight Sight = TerrainSight.Unchanged, TerrainPopulation Population = TerrainPopulation.Mod, int Winner = -1,
                                IReadOnlyList<TerrainCellReading>? Cells = null, FormKey? Support = null,
                                float AuthoredD0 = float.NaN, float AuthoredD1 = float.NaN, float WinningD0 = float.NaN, float WinningD1 = float.NaN,
                                float BurialD0 = float.NaN, float BurialD1 = float.NaN, float CoreGap = float.NaN, float? Landing = null,
                                DetectorModel.TerrainSupportFacts? Facts = null);

public sealed class TerrainContactIndex
{
    readonly ContactIndex _cidx;
    readonly CellBoundaryIndex _land;
    readonly IReadOnlyList<HashSet<int>> _closure;
    BoxHash<FormKey>? _supports;
    HashSet<(FormKey Ws, int X, int Y)>? _candidates;

    public float Epsilon { get; init; } = 4f;
    public float FloatThreshold { get; init; } = 8f;
    public float BurialThreshold { get; init; } = 8f;
    /// <summary>How far from the body a reference support is looked for (u).</summary>
    public float Reach { get; init; } = 256f;
    /// <summary>The cos of the steepest contact normal that still holds a body up (60°).</summary>
    public float HoldCos { get; init; } = 0.5f;
    public Dictionary<string, long> Counts { get; } = new();
    /// <summary>Called every 1,000 references judged, with the pass's name and the count, and once when the support index is
    /// built (a progress line; never a verdict).</summary>
    public Action<string, int>? Progress { get; set; }

    public TerrainContactIndex(ContactIndex cidx, CellBoundaryIndex land, IReadOnlyList<HashSet<int>> closure)
    { _cidx = cidx; _land = land; _closure = closure; }

    OverlapIndex Idx => _cidx.Index;
    void Count(string k, long n = 1) => Counts[k] = Counts.GetValueOrDefault(k) + n;

    // ---- the owner ------------------------------------------------------------------------------------------------------

    /// <summary>The placement owner: the plugin that set the position the game loads. The changed-only placement history drops a
    /// record that repeats the previous position whatever its masters say (PlacementHistory.Record), so a later record that
    /// restates a step its plugin could not see is a statement of its own: the latest such toucher owns the position (Gate A
    /// review F1, K15-b). A later toucher that could see the step is a carrier, not an owner.</summary>
    public static int PlacementOwner(int stepPlugin, IReadOnlyList<int>? touchers, IReadOnlyList<HashSet<int>> closure)
    {
        if (touchers is not null)
            for (int i = touchers.Count - 1; i >= 0; i--)
            {
                int t = touchers[i];
                if (t <= stepPlugin) break;
                if (!closure[t].Contains(stepPlugin)) return t;
            }
        return stepPlugin;
    }

    // ---- the LAND views ---------------------------------------------------------------------------------------------------

    /// <summary>One view of a cell's ground: the plugin whose statement it is (−1: no statement at all), what it says, its heights.</summary>
    sealed record View(int Plugin, LandHeight.LandKind Kind, float[,]? Heights, string? Reason)
    {
        public bool Ground => Heights is not null;
        public string Label => Plugin < 0 ? "no LAND" : Kind.ToString();
    }
    static readonly View NoStatement = new(-1, LandHeight.LandKind.DeclaresNone, null, null);

    /// <summary>The ground the owner saw: the last LAND statement by the owner or a plugin in its closure; heights from the
    /// changed-only history's authoring view (CellBoundaryIndex.LandAsSeenBy).</summary>
    View Authored(CellInfo c, int owner)
    {
        for (int i = c.LandStatements.Count - 1; i >= 0; i--)
        {
            var st = c.LandStatements[i];
            if (st.Plugin != owner && !_closure[owner].Contains(st.Plugin)) continue;
            if (st.Kind == LandHeight.LandKind.DeclaresNone) return new View(st.Plugin, st.Kind, null, null);
            if (st.Kind == LandHeight.LandKind.Unreadable) return new View(st.Plugin, st.Kind, null, st.Reason);
            var snap = CellBoundaryIndex.LandAsSeenBy(c.Land, owner, _closure);
            return snap is null ? NoStatement : new View(snap.Plugin, LandHeight.LandKind.Heights, snap.Heights, null);
        }
        return NoStatement;
    }

    /// <summary>The ground the game loads: the last LAND statement; heights from the changed-only history's last snap, whose plugin
    /// is the one that set them (a repeat of its own masters' heights is not a step).</summary>
    View Winning(CellInfo c)
    {
        if (c.LandStatements.Count == 0) return NoStatement;
        var st = c.LandStatements[^1];
        if (st.Kind == LandHeight.LandKind.DeclaresNone) return new View(st.Plugin, st.Kind, null, null);
        if (st.Kind == LandHeight.LandKind.Unreadable) return new View(st.Plugin, st.Kind, null, st.Reason);
        var snap = c.Land[^1];
        return new View(snap.Plugin, LandHeight.LandKind.Heights, snap.Heights, null);
    }

    /// <summary>Cells whose LAND has more than one statement: the only ones that can hold a row (§6 step 2).</summary>
    HashSet<(FormKey, int, int)> Candidates()
    {
        if (_candidates is not null) return _candidates;
        _candidates = new();
        foreach (var c in _land.Cells.Values)
            if (!c.Interior && c.GX != int.MinValue && c.LandStatements.Count >= 2) _candidates.Add((c.Worldspace, c.GX, c.GY));
        return _candidates;
    }

    // ---- reference supports -------------------------------------------------------------------------------------------------

    /// <summary>Every reference that can hold something in the winning arm: placed objects enabled at load, not markers, whose
    /// base has real collision to rest on (CanHost: collision, closed — never a render soup or an OBND box), not Movable.</summary>
    BoxHash<FormKey> SupportHash()
    {
        if (_supports is not null) return _supports;
        _supports = new BoxHash<FormKey>();
        foreach (var (key, snaps) in Idx.History.Refs)
        {
            var w = snaps[^1];
            if (w.Interior || w.Deleted || Idx.IsDeletedNow(key)) continue;
            if (Idx.RefArm.TryGetValue(key, out var arm) && arm != "PlacedObject") continue;
            var b = _cidx.WinnerBase(key);
            if (b.IsNull || _cidx.IsMarker(b)) continue;
            if (!SupportGeometry(b)) continue;
            if (_cidx.MotionOf(b) == DependentMotion.Movable) continue;
            var en = _cidx.WinnerEnable(key);
            if (!en.Resolved || !en.Enabled) continue;
            var pose = Idx.WinningGeometryPose(key) ?? w;
            var box = _cidx.WorldBox(b, pose);
            if (!box.IsEmpty) _supports.Add(w.Space, box, key);
        }
        Progress?.Invoke("support index built, references", _supports.Count);
        return _supports;
    }

    bool SupportGeometry(FormKey b) => _cidx.Summary(b) is { CanHost: true };

    /// <summary>The winning arm's reference supports near a body: held by one (a face within ε under it pointing up within 60°, or
    /// the drop reading pushes it up out of an overlap), and the least downward gap to any (PhysicsReach.Contact, +∞ when none below).</summary>
    (bool Held, FormKey? By, float Gap) ReferenceSupport(FormKey self, FormKey space, TriangleSoup body, Aabb bodyBox)
    {
        bool held = false; FormKey? by = null; float gap = float.PositiveInfinity;
        foreach (var (other, otherBox, _) in SupportHash().Near(space, bodyBox, Reach))
        {
            if (other == self) continue;
            if (otherBox.Min.Z >= bodyBox.Min.Z) continue;   // a support reaches below the body's lowest point; what sits on it holds nothing up
            var b = _cidx.WinnerBase(other);
            if (_cidx.GeomOf(b) is not { } g) continue;
            var pose = Idx.WinningGeometryPose(other) ?? Idx.History.Winning(other)!.Value;
            var soup = SpatialOverlap.TransformSoup(g.HostShell ?? g.HostSoup, pose.Pos, pose.Rot, pose.Scale);
            var c = SpatialOverlap.Contact(body, soup, Reach, Epsilon);
            if (c.FacingDistance <= Epsilon && c.NormalUp >= HoldCos) { if (!held) { held = true; by = other; } continue; }
            // overlapping or below: the drop reading (PhysicsReach.Contact) — held when it pushes the body up, else its first contact
            var drop = PhysicsReach.Contact(body, soup);
            if (drop.Held || drop.D == 0) { if (!held) { held = true; by = other; } continue; }
            if (drop.D is { } d && d < gap) { gap = (float)d; if (!held) by = other; }
        }
        return (held, by, gap);
    }

    // ---- the pass -------------------------------------------------------------------------------------------------------------

    /// <summary>The admission rule (show only inter-mod clashes): a Fixed Blind Mod-population row that floats or is buried.</summary>
    public static bool Emitted(TerrainRow r) =>
        r.Status == TerrainStatus.Row && r.Motion == DependentMotion.Fixed && r.Sight == TerrainSight.Blind
        && r.Population == TerrainPopulation.Mod && r.Verdict is TerrainVerdict.Floating or TerrainVerdict.Buried;

    /// <summary>Every exterior placed reference whose footprint touches a cell with more than one LAND statement; rows of every
    /// status when <paramref name="keepAll"/>, else only Row and Refused. Counts every status in <see cref="Counts"/>.</summary>
    public List<TerrainRow> Judge(bool keepAll, Func<FormKey, bool>? inScope = null)
    {
        var rows = new List<TerrainRow>();
        var cand = Candidates();
        int seen = 0;
        foreach (var (key, snaps) in Idx.History.Refs)
        {
            var w = snaps[^1];
            if (w.Interior) continue;
            if (inScope is not null && !inScope(key)) continue;
            if (!TouchesCandidate(key, w, cand)) { Count("footprint outside a cell with 2+ LAND statements"); continue; }
            var r = Explain(key);
            if (++seen % 1000 == 0) Progress?.Invoke("differential pass", seen);
            Count(r.Status.ToString());
            if (keepAll || r.Status is TerrainStatus.Row or TerrainStatus.Refused) rows.Add(r);
        }
        return rows;
    }

    /// <summary>The absolute floor (§7): every exterior placed reference against the winning ground as shipped, Row status only;
    /// <paramref name="inSample"/> narrows it (a deterministic sample by FormKey, stated by the caller).</summary>
    public List<TerrainRow> JudgeAbsolute(Func<FormKey, bool>? inSample = null)
    {
        var rows = new List<TerrainRow>();
        int seen = 0;
        foreach (var (key, snaps) in Idx.History.Refs)
        {
            if (snaps[^1].Interior) continue;
            if (inSample is not null && !inSample(key)) { Count("absolute: outside the sample"); continue; }
            var r = Explain(key, absolute: true);
            Count("absolute: " + r.Status);
            if (++seen % 1000 == 0) Progress?.Invoke("absolute floor", seen);
            if (r.Status == TerrainStatus.Row) rows.Add(r with { Cells = null });
        }
        return rows;
    }

    /// <summary>The broad filter: the OBND box at the winning pose (else the measured box; else the position), 64 u wider, against the
    /// candidate cells.</summary>
    bool TouchesCandidate(FormKey key, Snap w, HashSet<(FormKey, int, int)> cand)
    {
        var pose = Idx.WinningGeometryPose(key) ?? w;
        var b = _cidx.WinnerBase(key);
        var ob = b.IsNull ? Aabb.Empty : Idx.ObndBox(b);
        // no OBND: the measured geometry's box instead (a decode), never the position alone, which loses a body straddling an edge
        var wb = !ob.IsEmpty ? SpatialOverlap.ToWorld(ob, pose.Pos, pose.Rot, pose.Scale) : b.IsNull ? Aabb.Empty : _cidx.WorldBox(b, pose);
        float x0, x1, y0, y1;
        if (wb.IsEmpty) { x0 = x1 = pose.Pos.X; y0 = y1 = pose.Pos.Y; }
        else { x0 = wb.Min.X - 64; x1 = wb.Max.X + 64; y0 = wb.Min.Y - 64; y1 = wb.Max.Y + 64; }
        for (int gx = Grid(x0); gx <= Grid(x1); gx++)
            for (int gy = Grid(y0); gy <= Grid(y1); gy++)
                if (cand.Contains((w.Worldspace, gx, gy))) return true;
        return false;
    }

    static int Grid(float v) => (int)MathF.Floor(v / 4096f);

    /// <summary>One reference, every status. <paramref name="absolute"/>: the absolute floor (§7) — the body against the winning
    /// ground and the winning arm's supports alone, as shipped; no owner view, no gate on change, no sight.</summary>
    public TerrainRow Explain(FormKey key, bool absolute = false)
    {
        var snaps = Idx.History.Of(key);
        if (snaps is null || snaps.Count == 0) return new(key, FormKey.Null, FormKey.Null, default, -1, -1, TerrainStatus.NotObject, "not placed in this order");
        var win = snaps[^1];
        int step = win.Plugin;
        var b = _cidx.WinnerBase(key);
        TerrainRow R(TerrainStatus s, string why, int owner = -1) => new(key, b, win.Worldspace, win, step, owner, s, why);

        if (Idx.RefArm.TryGetValue(key, out var arm) && arm != "PlacedObject") return R(TerrainStatus.NotObject, $"a {arm}: actors and other placed arms are set aside");
        // the deleted-reference gate (Q35): a placement read off a deletion is not a reading
        if (win.Deleted) return R(TerrainStatus.DeletedWithPlacement, "the record that set the winning position is a deletion that kept its placement — that position is not a reading");
        if (Idx.IsDeletedNow(key)) return R(TerrainStatus.DeletedAtLoad, "deleted in the winning order: nothing is placed");
        int owner = PlacementOwner(step, Idx.Touchers.GetValueOrDefault(key), _closure);
        if (Idx.Deletions.InView(key, owner, _closure)) return R(TerrainStatus.DeletedInOwnerView, "the owner's own view selects a deletion: no placement to compare against", owner);
        var en = _cidx.WinnerEnable(key);
        if (!en.Resolved) return R(TerrainStatus.EnableUnresolved, "the enable chain cannot be followed in this order", owner);
        if (!en.Enabled) return R(TerrainStatus.DisabledAtLoad, "disabled at load in this order", owner);
        if (b.IsNull) return R(TerrainStatus.NoBody, "no base", owner);
        if (_cidx.IsMarker(b)) return R(TerrainStatus.Marker, "the base carries the Is-Marker flag: the engine draws nothing", owner);
        var g = _cidx.GeomOf(b);
        if (g is null || !g.CanDepend || g.DepSrc is not ('c' or 'r'))
            return R(TerrainStatus.NoBody, g is null ? "no geometry" : g.DepSrc == 'o' ? "an OBND box only, not a body" : "no body soup", owner);
        var motion = _cidx.MotionOf(b);
        var pose = Idx.WinningGeometryPose(key) ?? win;
        var bodyModel = g.DepShell ?? g.DepSoup;
        var body = SpatialOverlap.TransformSoup(bodyModel, pose.Pos, pose.Rot, pose.Scale);
        var core = g.DepShell is null ? null : SpatialOverlap.TransformSoup(g.DepSoup, pose.Pos, pose.Rot, pose.Scale);
        var bodyBox = SpatialOverlap.SoupBounds(body);
        TerrainRow B(TerrainStatus s, string why) => new(key, b, win.Worldspace, pose, step, owner, s, why, g.DepSrc, motion);

        // the footprint cells, each with its own authored and winning ground (K14)
        var cells = FootprintCells(bodyBox);
        var views = new List<(int GX, int GY, View A, View W)>();
        var cellKeys = new List<FormKey>();
        foreach (var (gx, gy) in cells)
        {
            var at = _land.AtGrid(win.Worldspace, gx, gy);
            if (at is { Count: > 1 }) return B(TerrainStatus.DuplicateGrid, $"more than one CELL record at ({gx},{gy})");
            if (at is not { Count: 1 }) { views.Add((gx, gy, NoStatement, NoStatement)); cellKeys.Add(FormKey.Null); continue; }
            var ci = _land.Cells[at[0]];
            var wv = Winning(ci);
            var a = absolute ? wv : Authored(ci, owner);
            if (a.Kind == LandHeight.LandKind.Unreadable || wv.Kind == LandHeight.LandKind.Unreadable)
            {
                var u = a.Kind == LandHeight.LandKind.Unreadable ? a : wv;
                return B(TerrainStatus.Refused, $"the {(u == a ? "authored" : "winning")} LAND of ({gx},{gy}) by {_land.PluginName(u.Plugin)} is unreadable: {u.Reason}");
            }
            views.Add((gx, gy, a, wv)); cellKeys.Add(ci.Key);
        }

        // §6 step 3: the two terrains agree within one heightmap unit under the footprint → no row
        var readings = new List<TerrainCellReading>();
        bool anyChanged = false;
        for (int k = 0; k < views.Count; k++)
        {
            var (gx, gy, a, wv) = views[k];
            float maxDelta = Delta(a, wv, gx, gy, bodyBox);
            bool changed = maxDelta > LandHeight.Unit;
            anyChanged |= changed;
            readings.Add(new TerrainCellReading(gx, gy, a.Plugin, wv.Plugin, a.Label, wv.Label, maxDelta, changed, TerrainSight.Unchanged, TerrainPopulation.Mod, cellKeys[k]));
        }
        if (!anyChanged && !absolute) return B(TerrainStatus.TerrainAgrees, "the authored and winning ground agree within one heightmap unit under it") with { Cells = readings };

        // the readings, both diagonals
        var r0 = Reading(body, views, 0);
        var r1 = Reading(body, views, 1);
        float coreGap = core is null ? float.NaN : MathF.Min(Reading(core, views, 0).Win.Clearance, Reading(core, views, 1).Win.Clearance);
        bool authGround = views.Any(v => v.A.Ground), winGround = views.Any(v => v.W.Ground);
        var d0 = new DetectorModel.TerrainDiagonal(authGround, r0.Auth.Clearance, winGround, r0.Win.Clearance, r0.Burial);
        var d1 = new DetectorModel.TerrainDiagonal(authGround, r1.Auth.Clearance, winGround, r1.Win.Clearance, r1.Burial);
        var facts0 = new DetectorModel.TerrainSupportFacts(d0, d1, false, float.PositiveInfinity);
        var auth = DetectorModel.TerrainAuthoredRest(facts0, Epsilon);
        TerrainRow Readings(TerrainRow x) => x with
        {
            Authored = auth, Cells = readings, AuthoredD0 = r0.Auth.Clearance, AuthoredD1 = r1.Auth.Clearance,
            WinningD0 = r0.Win.Clearance, WinningD1 = r1.Win.Clearance, BurialD0 = r0.Burial, BurialD1 = r1.Burial, CoreGap = coreGap,
        };

        // the winning arm's reference supports, read only when the ground alone does not hold it
        bool groundHolds = winGround && r0.Win.Clearance <= Epsilon && r1.Win.Clearance <= Epsilon;
        var (held, by, refGap) = groundHolds ? (false, (FormKey?)null, float.PositiveInfinity) : ReferenceSupport(key, win.Space, body, bodyBox);
        var facts = facts0 with { OnReference = held, ReferenceGap = refGap };
        var (verdict, gap, burial, why) = DetectorModel.TerrainSupport(facts, Epsilon, FloatThreshold, BurialThreshold);
        float saddle = Saddle(views, r0.Win);
        var physical = Readings(B(TerrainStatus.Row, why)) with { Verdict = verdict, Gap = gap, Burial = burial, Saddle = saddle, Support = by, Facts = facts };
        if (absolute) return physical with { Why = "as shipped: " + why };

        if (!authGround) return physical with { Status = TerrainStatus.NoAuthoredGround, Why = "no ground in the owner's view: it rested on none" };
        if (auth == TerrainAuthored.NotResting) return physical with { Status = TerrainStatus.NotRestingAsAuthored, Why = $"not resting on the authored ground ({r0.Auth.Clearance:0.#} / {r1.Auth.Clearance:0.#} u by diagonal)" };
        if (auth == TerrainAuthored.CannotTell) return physical with { Status = TerrainStatus.AuthoredCannotTell, Why = $"the diagonals disagree on the authored ground ({r0.Auth.Clearance:0.#} / {r1.Auth.Clearance:0.#} u)" };
        // whose clash: per changed cell, the worst names the row
        var carriers = (Idx.Touchers.GetValueOrDefault(key) ?? new List<int>()).Where(t => t > owner).ToList();
        string ownerName = _land.PluginName(owner);
        TerrainCellReading? worst = null;
        for (int i = 0; i < readings.Count; i++)
        {
            var cr = readings[i];
            if (!cr.Changed) continue;
            int w = cr.WinnerPlugin;
            bool winnerInOwner = w >= 0 && _closure[owner].Contains(w);
            bool ownerInWinner = w >= 0 && w < _closure.Count && _closure[w].Contains(owner);
            bool carrierSaw = w >= 0 && carriers.Any(t => t == w || _closure[t].Contains(w) || _closure[w].Contains(t));
            var sight = DetectorModel.TerrainProvenance(new(owner, w, winnerInOwner, ownerInWinner, carrierSaw));
            string wName = _land.PluginName(w);
            var pop = DetectorModel.TerrainPopulationOf(PluginKinds.IsBethesda(ownerName), PluginKinds.IsBethesda(wName), PluginKinds.IsUssep(ownerName), PluginKinds.IsUssep(wName));
            readings[i] = cr = cr with { Sight = sight, Population = pop };
            if (worst is null || sight > worst.Sight) worst = cr;
        }
        if (verdict == TerrainVerdict.Resting) return physical with { Status = TerrainStatus.StillResting, Sight = worst!.Sight, Population = worst.Population, Winner = worst.WinnerPlugin, Cells = readings };
        float? landing = motion == DependentMotion.Movable ? (verdict == TerrainVerdict.Floating ? gap : 0f) : null;
        return physical with
        {
            Status = TerrainStatus.Row, Sight = worst!.Sight, Population = worst.Population, Winner = worst.WinnerPlugin, Cells = readings,
            Landing = landing, Why = (motion == DependentMotion.Movable ? PhysicsReach.DropLabel + ": " : "") + why,
        };
    }

    /// <summary>The physical half under this order, for <c>--base</c>: still floating or buried → Holds; resting, or not there at
    /// load → Gone; anything this order cannot read → Unmeasured.</summary>
    public (PhysicalState State, string Detail) Predicate(FormKey key)
    {
        var r = Explain(key);
        return r.Status switch
        {
            TerrainStatus.DeletedAtLoad or TerrainStatus.DisabledAtLoad => (PhysicalState.Gone, r.Why + " — nothing floats"),
            TerrainStatus.TerrainAgrees => (PhysicalState.Gone, "the ground under it is the ground its owner saw"),
            TerrainStatus.Row or TerrainStatus.StillResting or TerrainStatus.NotRestingAsAuthored or TerrainStatus.AuthoredCannotTell or TerrainStatus.NoAuthoredGround
                => r.Verdict switch
                {
                    TerrainVerdict.Floating => (PhysicalState.Holds, $"floats {(float.IsPositiveInfinity(r.Gap) ? "over no ground" : r.Gap.ToString("0.#") + " u")}"),
                    TerrainVerdict.Buried => (PhysicalState.Holds, $"buried {r.Burial:0.#} u deeper than authored"),
                    TerrainVerdict.Resting => (PhysicalState.Gone, r.Support is { } s ? $"held by {s}" : "rests on the ground"),
                    _ => (PhysicalState.Unmeasured, r.Why),
                },
            _ => (PhysicalState.Unmeasured, r.Why),
        };
    }

    // ---- geometry --------------------------------------------------------------------------------------------------------------

    static List<(int, int)> FootprintCells(Aabb box)
    {
        var l = new List<(int, int)>();
        for (int gx = Grid(box.Min.X); gx <= Grid(box.Max.X); gx++)
            for (int gy = Grid(box.Min.Y); gy <= Grid(box.Max.Y); gy++) l.Add((gx, gy));
        return l;
    }

    /// <summary>The largest height difference between the two views over the heightmap vertices under the body (one vertex wider);
    /// ground on one side and none on the other is a change (+∞); none on both is no change.</summary>
    static float Delta(View a, View w, int gx, int gy, Aabb box)
    {
        if (!a.Ground && !w.Ground) return 0f;
        if (a.Ground != w.Ground) return float.PositiveInfinity;
        if (ReferenceEquals(a.Heights, w.Heights)) return 0f;
        var (i0, i1, j0, j1) = VertexRange(gx, gy, box);
        float m = 0f;
        for (int i = i0; i <= i1; i++)
            for (int j = j0; j <= j1; j++) m = MathF.Max(m, MathF.Abs(a.Heights![i, j] - w.Heights![i, j]));
        return m;
    }

    static (int, int, int, int) VertexRange(int gx, int gy, Aabb box)
    {
        float ox = gx * 4096f, oy = gy * 4096f, sp = LandHeight.VertexSpacing;
        int i0 = Math.Clamp((int)MathF.Floor((box.Min.X - ox) / sp) - 1, 0, 32), i1 = Math.Clamp((int)MathF.Ceiling((box.Max.X - ox) / sp) + 1, 0, 32);
        int j0 = Math.Clamp((int)MathF.Floor((box.Min.Y - oy) / sp) - 1, 0, 32), j1 = Math.Clamp((int)MathF.Ceiling((box.Max.Y - oy) / sp) + 1, 0, 32);
        return (i0, i1, j0, j1);
    }

    /// <summary>One view's clearance: the least body-minus-ground height over the samples, and where (cell, world x, y).</summary>
    readonly record struct Side(float Clearance, int GX, int GY, float X, float Y);
    readonly record struct DiagonalReading(Side Auth, Side Win, float Burial);

    /// <summary>Both views under one diagonal (0: the 00–11 cut, 1: the 10–01 cut), over every footprint cell.</summary>
    static DiagonalReading Reading(TriangleSoup body, List<(int GX, int GY, View A, View W)> views, int diag)
    {
        var auth = new Side(float.PositiveInfinity, 0, 0, 0, 0); var win = auth; float burial = 0f;
        foreach (var (gx, gy, a, w) in views)
        {
            if (!a.Ground && !w.Ground) continue;
            float ox = gx * 4096f, oy = gy * 4096f;
            foreach (var (lx, ly, z) in TerrainClearance.Samples(body, ox, oy, diag))
            {
                float ta = a.Ground ? TerrainClearance.HeightAt(a.Heights!, lx, ly, diag) : float.NegativeInfinity;
                float tw = w.Ground ? TerrainClearance.HeightAt(w.Heights!, lx, ly, diag) : float.NegativeInfinity;
                if (a.Ground && z - ta < auth.Clearance) auth = new Side(z - ta, gx, gy, lx + ox, ly + oy);
                if (w.Ground && z - tw < win.Clearance) win = new Side(z - tw, gx, gy, lx + ox, ly + oy);
                burial = MathF.Max(burial, MathF.Max(0f, tw - z) - MathF.Max(0f, ta - z));
            }
        }
        return new DiagonalReading(auth, win, burial);
    }

    static float Saddle(List<(int GX, int GY, View A, View W)> views, Side at)
    {
        foreach (var (gx, gy, _, w) in views)
            if (gx == at.GX && gy == at.GY && w.Ground && float.IsFinite(at.Clearance))
            { NavmeshFloor.LandHeightAt(w.Heights!, gx, gy, at.X, at.Y, out float s); return s; }
        return float.NaN;
    }
}

/// <summary>The body against a heightmap: the critical points where the vertical gap between a triangle soup and the heightmap
/// cut along one diagonal can be least, in one cell's local frame.</summary>
public static class TerrainClearance
{
    const float Side = 4096f, Sp = 128f;

    /// <summary>The ground's height at a local point of a cell under one triangulation: 0 = the quads cut along 00–11 (triangles
    /// 00-10-11 and 00-11-01), 1 = along 10–01 (00-10-01 and 10-11-01) — PhysicsReach.TerrainSupports' two soups.</summary>
    public static float HeightAt(float[,] h, float lx, float ly, int diag)
    {
        float fx = Math.Clamp(lx / Sp, 0f, 32f), fy = Math.Clamp(ly / Sp, 0f, 32f);
        int ix = Math.Min((int)MathF.Floor(fx), 31), iy = Math.Min((int)MathF.Floor(fy), 31);
        float tx = fx - ix, ty = fy - iy;
        float h00 = h[ix, iy], h10 = h[ix + 1, iy], h01 = h[ix, iy + 1], h11 = h[ix + 1, iy + 1];
        if (diag == 0)
            return tx >= ty ? h00 + tx * (h10 - h00) + ty * (h11 - h10) : h00 + ty * (h01 - h00) + tx * (h11 - h01);
        return tx + ty <= 1f ? h00 + tx * (h10 - h00) + ty * (h01 - h00) : h11 + (1f - tx) * (h01 - h11) + (1f - ty) * (h10 - h11);
    }

    /// <summary>The critical points of a world-space soup over one cell (origin <paramref name="ox"/>, <paramref name="oy"/>), in the
    /// cell's local frame with the body's height there: body vertices; heightmap vertices under a body triangle; body edges
    /// crossing the grid lines (x or y a multiple of 128) and the diagonal family (x − y, or x + y, a multiple of 128).</summary>
    public static IEnumerable<(float X, float Y, float Z)> Samples(TriangleSoup body, float ox, float oy, int diag)
    {
        const float tol = 0.01f;
        static bool In(float x, float y) => x >= -tol && y >= -tol && x <= Side + tol && y <= Side + tol;
        var v = body.Vertices;
        foreach (var p in v) { float x = p.X - ox, y = p.Y - oy; if (In(x, y)) yield return (Math.Clamp(x, 0, Side), Math.Clamp(y, 0, Side), p.Z); }
        var idx = body.Indices;
        for (int t = 0; t + 2 < idx.Length; t += 3)
        {
            var a = v[idx[t]] - new Vector3(ox, oy, 0); var b = v[idx[t + 1]] - new Vector3(ox, oy, 0); var c = v[idx[t + 2]] - new Vector3(ox, oy, 0);
            // heightmap vertices under the triangle
            float minX = MathF.Min(a.X, MathF.Min(b.X, c.X)), maxX = MathF.Max(a.X, MathF.Max(b.X, c.X));
            float minY = MathF.Min(a.Y, MathF.Min(b.Y, c.Y)), maxY = MathF.Max(a.Y, MathF.Max(b.Y, c.Y));
            float den = (b.Y - c.Y) * (a.X - c.X) + (c.X - b.X) * (a.Y - c.Y);
            if (MathF.Abs(den) > 1e-6f && maxX >= 0 && maxY >= 0 && minX <= Side && minY <= Side)
                for (int i = Math.Max(0, (int)MathF.Ceiling(minX / Sp)); i <= Math.Min(32, (int)MathF.Floor(maxX / Sp)); i++)
                    for (int j = Math.Max(0, (int)MathF.Ceiling(minY / Sp)); j <= Math.Min(32, (int)MathF.Floor(maxY / Sp)); j++)
                    {
                        float px = i * Sp, py = j * Sp;
                        float l1 = ((b.Y - c.Y) * (px - c.X) + (c.X - b.X) * (py - c.Y)) / den;
                        float l2 = ((c.Y - a.Y) * (px - c.X) + (a.X - c.X) * (py - c.Y)) / den;
                        float l3 = 1f - l1 - l2;
                        if (l1 < -1e-5f || l2 < -1e-5f || l3 < -1e-5f) continue;
                        yield return (px, py, l1 * a.Z + l2 * b.Z + l3 * c.Z);
                    }
            // edges crossing the grid lines and the diagonal family
            foreach (var (p, q) in new[] { (a, b), (b, c), (c, a) })
            {
                foreach (var s in Crossings(p, q, u => u.X)) if (In(s.X, s.Y)) yield return (Math.Clamp(s.X, 0, Side), Math.Clamp(s.Y, 0, Side), s.Z);
                foreach (var s in Crossings(p, q, u => u.Y)) if (In(s.X, s.Y)) yield return (Math.Clamp(s.X, 0, Side), Math.Clamp(s.Y, 0, Side), s.Z);
                foreach (var s in Crossings(p, q, diag == 0 ? u => u.X - u.Y : u => u.X + u.Y)) if (In(s.X, s.Y)) yield return (Math.Clamp(s.X, 0, Side), Math.Clamp(s.Y, 0, Side), s.Z);
            }
        }
    }

    /// <summary>The points of segment p–q where <paramref name="f"/> crosses a multiple of 128, endpoints excluded.</summary>
    static IEnumerable<Vector3> Crossings(Vector3 p, Vector3 q, Func<Vector3, float> f)
    {
        float fp = f(p), fq = f(q);
        if (MathF.Abs(fq - fp) < 1e-6f) yield break;
        float lo = MathF.Min(fp, fq), hi = MathF.Max(fp, fq);
        for (int k = (int)MathF.Floor(lo / Sp) + 1; k * Sp < hi; k++)
        {
            float t = (k * Sp - fp) / (fq - fp);
            if (t <= 0f || t >= 1f) continue;
            yield return p + t * (q - p);
        }
    }
}
