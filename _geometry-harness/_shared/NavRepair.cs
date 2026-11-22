using System.Globalization;
using HousecarlCore;
using Mutagen.Bethesda.Plugins;

namespace HarnessShared;

// THE NAVMESH REPAIR PLAN (task 2026-11-16-navmesh-repair, classes 2 and 3). Pure: it reads the winning navmeshes' geometry
// and the detector's broken rows, and says what to change; it reads no plugin and writes nothing. The detector
// (navownprobe) owns the rows, the write path (housecarl_apply) owns the write, NavRepairReadBack owns the check of what
// was written against this plan.
//
//   RELINK (class 2). A broken border link (EDGE-IDX, EDGE-NAV, EDGE-NOMATCH) takes the one open edge of another navmesh
//   that lies along it within the tolerance (NavBorderMatch, OpenEdgeIndex). Two or more such edges: refused by name,
//   never the nearest (Gate A review finding 2). None: the link is cut (its edge field, flag bit and table entry go) and
//   the plan says so, unless an edge lies within the unproven band, when it is refused (Gate B review finding 2). Every
//   link it makes is written both ways. An EDGE-ONEWAY link gains its link back, or is said resolved by another repair;
//   one whose match lies only in the unproven band is refused (2026-11-22: such a row is a finding, but no link is
//   written across a gap not yet walked both ways).
//   CLASS 3, FOLLOW THE GROUND. A NAV-GROUND navmesh's vertices that sat on the ground its author saw move up or down by
//   what the ground moved under each; the box's z is recomputed from the used vertices; x and y never move, so the grid
//   stays valid (Gate A, Q2).
//
// Before anything is written the plan checks its own result: no vertex or triangle added or removed, every link resolves
// in the planned state, every link it made has its link back, a link that matched still matches after a height move, the
// box is the used vertices' extent, and no edit touches the grid. A navmesh that fails one is refused in one sentence, and
// so is every navmesh whose planned edit depends on it.

/// <summary>A broken link as the detector reports it. <paramref name="SrcTri"/> and <paramref name="Edge"/> name one source
/// edge (EDGE-NOMATCH, EDGE-ONEWAY); null means every source edge whose table entry names the target triangle.</summary>
public sealed record BrokenLink(string Kind, FormKey Src, FormKey Target, int TargetTri, int? SrcTri = null, int? Edge = null);

/// <summary>One used vertex of a NAV-GROUND navmesh: the author's and the winning ground under it, or that it cannot be told.</summary>
public sealed record GroundVertex(int Index, float Authored, float Winning, bool CannotTell);

/// <summary>A NAV-GROUND row: the navmesh and its used vertices' ground facts, as the detector measured them.
/// <paramref name="Mover"/> is set when a plugin that moved the ground ships its own re-cut of the navmesh (the detector's
/// NavGround.ChooseMover: its own vertices, not the winner's with new flags), which a later plugin overrides.</summary>
public sealed record GroundRow(FormKey Nav, IReadOnlyList<GroundVertex> Vertices, MoverVersion? Mover = null);

/// <summary>A ground mover's own re-cut of a navmesh, overridden by <paramref name="Winner"/>; <paramref name="MoverTris"/> and
/// <paramref name="WinnerTris"/> are the two versions' triangle counts. <paramref name="Clobbered"/> is the detector's emitted
/// CLOBBERED row for the same pair ("mover &gt; winner"), the same problem seen by that class, or null when it has none.
/// <paramref name="OtherMovers"/> are the other plugins that moved ground under the navmesh, whose ground the re-cut may not fit.</summary>
public sealed record MoverVersion(string Mover, string Winner, int MoverTris, int WinnerTris, string? Clobbered = null,
                                  IReadOnlyList<string>? OtherMovers = null);

/// <summary>One housecarl_apply op. <see cref="Compose"/> is an EdgeLink element for an Add; <see cref="ComposeNode"/> a whole
/// compose (a NAVI entry with its nested sets); <see cref="FromPlugin"/> the plugin a CopyFrom copies the field from (navedit's
/// use_version carries a navmesh's Data from the plugin named).</summary>
public sealed record RepairOp(string FormId, string FieldPath, string Op, string? Key = null, string? Value = null,
                              IReadOnlyDictionary<string, string>? Compose = null, System.Text.Json.Nodes.JsonNode? ComposeNode = null,
                              string? FromPlugin = null);

/// <summary>What happened to one link. Status: relinked, cut, refused, linked-back, resolved. <paramref name="Src"/>,
/// <paramref name="SrcTri"/> and <paramref name="Edge"/> are the edge the plan edits (or would); the detector row it answers is
/// (<paramref name="Kind"/>, <paramref name="RowSrc"/> or else <paramref name="Src"/>, <paramref name="Target"/>,
/// <paramref name="TargetTri"/>). A LINK-BACK answers no row: it is the far half of a relink.</summary>
public sealed record LinkRepair(string Kind, FormKey Src, int SrcTri, int Edge, FormKey Target, int TargetTri, string Status, string Why,
                                FormKey? To = null, int? ToTri = null, FormKey? RowSrc = null)
{
    public FormKey RowSource => RowSrc ?? Src;
}

/// <summary>One planned invariant and whether the planned state keeps it.</summary>
public sealed record PlanInvariant(string Name, bool Holds, string Detail);

/// <summary>The semantic change to one navmesh, what the read-back holds the written record to: vertex positions by index, the
/// box, table entries re-pointed (by original index), removed (original indices) and appended, and the final edge fields and
/// flag bits of every edge that changes.</summary>
public sealed class NavChange
{
    public readonly SortedDictionary<int, Vec3> Vertices = new();
    public Vec3? Min, Max;
    public readonly SortedDictionary<int, (FormKey Mesh, short Tri)> SetEntries = new();
    public readonly SortedSet<int> RemovedEntries = new();
    public readonly List<NavEdgeLink> AddedEntries = new();
    public readonly SortedDictionary<(int Tri, int Edge), short> EdgeFields = new();
    public readonly SortedDictionary<(int Tri, int Edge), bool> FlagBits = new();
    public bool Empty => Vertices.Count == 0 && Min is null && Max is null && SetEntries.Count == 0 && RemovedEntries.Count == 0
                         && AddedEntries.Count == 0 && EdgeFields.Count == 0 && FlagBits.Count == 0;
}

/// <summary>The plan for one navmesh: the edits a patch makes to it (status planned or cut) or the reason it is left
/// (refused).</summary>
public sealed class NavPlan
{
    public required FormKey Nav { get; init; }
    public required string Class { get; init; }
    public string Status { get; set; } = "planned";
    public readonly List<string> Why = new();
    public bool AdjacencyChange { get; set; }
    public readonly List<LinkRepair> Links = new();
    public NavChange Change { get; set; } = new();
    public List<RepairOp> Edits { get; set; } = new();
    public readonly List<PlanInvariant> Invariants = new();
}

public sealed class NavRepairPlan
{
    public readonly List<NavPlan> Plans = new();
    public IEnumerable<RepairOp> Ops => Plans.Where(p => p.Status != "refused").SelectMany(p => p.Edits);
}

public static class NavRepair
{
    static readonly string[] EdgeName = { "EdgeLink_0_1", "EdgeLink_1_2", "EdgeLink_2_0" };
    static string F(float v) => v.ToString("R", CultureInfo.InvariantCulture);
    static string V(Vec3 v) => $"{F(v.X)},{F(v.Y)},{F(v.Z)}";

    /// <summary>A navmesh's link state as the plan changes it: the table (original index or -1 for an appended entry) and
    /// every triangle edge's field and flag bit.</summary>
    sealed class Work
    {
        public required NavmeshGeometry G;
        public readonly List<(FormKey Mesh, short Tri, int Type, int Orig, bool Removed)> Table = new();
        public short[,] Field = new short[0, 3];
        public bool[,] Flag = new bool[0, 3];
        public static Work Of(NavmeshGeometry g)
        {
            var w = new Work { G = g, Field = new short[g.Triangles.Count, 3], Flag = new bool[g.Triangles.Count, 3] };
            var links = g.EdgeLinks ?? Array.Empty<NavEdgeLink>();
            for (int k = 0; k < links.Count; k++) w.Table.Add((links[k].Mesh, links[k].TriangleIndex, links[k].Type, k, false));
            for (int t = 0; t < g.Triangles.Count; t++)
                for (int e = 0; e < 3; e++) { w.Field[t, e] = NavBorderMatch.EdgeLink(g.Triangles[t], e); w.Flag[t, e] = NavBorderMatch.EdgeIsLink(g.Triangles[t], e); }
            return w;
        }
        public int Users(int k) { int n = 0; for (int t = 0; t < Field.GetLength(0); t++) for (int e = 0; e < 3; e++) if (Flag[t, e] && Field[t, e] == k) n++; return n; }
        public (FormKey Mesh, int Tri)? Link(int t, int e)
        {
            if (!Flag[t, e]) return null;
            int k = Field[t, e];
            return k >= 0 && k < Table.Count && !Table[k].Removed ? (Table[k].Mesh, Table[k].Tri) : null;
        }
        public void Cut(int t, int e)
        {
            int k = Field[t, e];
            Flag[t, e] = false; Field[t, e] = -1;
            if (k >= 0 && k < Table.Count && Users(k) == 0) Table[k] = Table[k] with { Removed = true };
        }
        public void LinkTo(int t, int e, FormKey mesh, int tri)
        {
            int k = Field[t, e];
            if (Flag[t, e] && k >= 0 && k < Table.Count && Users(k) == 1) { Table[k] = Table[k] with { Mesh = mesh, Tri = (short)tri, Removed = false }; return; }
            Table.Add((mesh, (short)tri, 0, -1, false));
            Field[t, e] = (short)(Table.Count - 1); Flag[t, e] = true;
        }
    }

    /// <summary>The relink plan for <paramref name="broken"/> over the winning navmeshes <paramref name="nav"/>.
    /// <paramref name="unproven"/> is the wider tolerance whose band the lab has not settled. <paramref name="scope"/>, when
    /// given, keeps only rows whose source navmesh is in it. <paramref name="lower"/>: the versions of a navmesh below its
    /// winner (plugin, geometry), latest first, for restoring a link back one of them authored.</summary>
    public static NavRepairPlan Relink(IReadOnlyDictionary<FormKey, WinningNav> nav, IReadOnlyList<BrokenLink> broken, EdgeTolerance tol,
                                       EdgeTolerance unproven, ISet<FormKey>? scope = null, OpenEdgeIndex? index = null,
                                       IReadOnlyDictionary<FormKey, IReadOnlyList<(string Plugin, NavmeshGeometry Geom)>>? lower = null)
    {
        var idx = index ?? new OpenEdgeIndex(nav);
        var work = new Dictionary<FormKey, Work>();
        Work W(FormKey k) => work.TryGetValue(k, out var w) ? w : work[k] = Work.Of(nav[k].Geom);
        var plans = new Dictionary<FormKey, NavPlan>();
        NavPlan P(FormKey k) => plans.TryGetValue(k, out var p) ? p : plans[k] = new NavPlan { Nav = k, Class = "relink" };
        var refusedEdges = new Dictionary<(FormKey, int, int), string>();
        var relinked = new List<(FormKey S, int T, int E, FormKey M, int J, int Fe, (FormKey, short, int)? Before)>();

        // the source edges, one per (navmesh, triangle, edge)
        var seen = new HashSet<(FormKey, int, int)>();
        var outgoing = new List<(BrokenLink Row, int T, int E)>();
        var oneway = new List<(BrokenLink Row, int T, int E)>();
        foreach (var r in broken)
        {
            if (scope is not null && !scope.Contains(r.Src)) continue;
            if (!nav.TryGetValue(r.Src, out var sw)) continue;
            var g = sw.Geom; var links = g.EdgeLinks ?? Array.Empty<NavEdgeLink>();
            var edges = new List<(int, int)>();
            if (r.SrcTri is { } st && r.Edge is { } se) edges.Add((st, se));
            else
                for (int t = 0; t < g.Triangles.Count; t++)
                    for (int e = 0; e < 3; e++)
                    {
                        var tr = g.Triangles[t]; int k = NavBorderMatch.EdgeLink(tr, e);
                        if (NavBorderMatch.EdgeIsLink(tr, e) && k >= 0 && k < links.Count && links[k].Mesh == r.Target && links[k].TriangleIndex == r.TargetTri) edges.Add((t, e));
                    }
            foreach (var (t, e) in edges)
                if (seen.Add((r.Src, t, e))) (r.Kind == "EDGE-ONEWAY" ? oneway : outgoing).Add((r, t, e));
        }

        (Vec3 A, Vec3 B) Ends(FormKey s, int t, int e) { var g = nav[s].Geom; var (i, j) = NavBorderMatch.EdgeVerts(g.Triangles[t], e); return (g.Vertices[i], g.Vertices[j]); }
        string Names(IEnumerable<(FormKey Nav, int Tri, int Edge, EdgeGap Gap)> c) => string.Join(" and ", c.Select(x => $"{x.Nav} triangle {x.Tri}"));

        // 1. every broken outgoing link: relink, refuse or cut
        foreach (var (r, t, e) in outgoing)
        {
            var (a, b) = Ends(r.Src, t, e);
            var cands = idx.Candidates(nav[r.Src].Space, a, b, tol, r.Src);
            var w = W(r.Src);
            int k = w.Field[t, e];
            if (cands.Count > 1)
            {
                string why = $"triangle {t}'s link is refused as ambiguous: {Names(cands)} both match within the tolerance; neither is taken";
                refusedEdges[(r.Src, t, e)] = why;
                P(r.Src).Links.Add(new LinkRepair(r.Kind, r.Src, t, e, r.Target, r.TargetTri, "refused", why));
            }
            else if (cands.Count == 1)
            {
                var c = cands[0];
                if (k >= 0 && k < w.Table.Count && w.Users(k) > 1)
                {
                    string why = $"triangle {t}'s link is refused: its table entry {k} is shared with another edge, so re-pointing it would move that link too";
                    refusedEdges[(r.Src, t, e)] = why;
                    P(r.Src).Links.Add(new LinkRepair(r.Kind, r.Src, t, e, r.Target, r.TargetTri, "refused", why));
                    continue;
                }
                var before = k >= 0 && k < w.Table.Count ? (w.Table[k].Mesh, w.Table[k].Tri, k) : ((FormKey, short, int)?)null;
                w.LinkTo(t, e, c.Nav, c.Tri);
                bool moves = c.Nav != r.Target;
                var p = P(r.Src);
                p.AdjacencyChange |= moves;
                p.Links.Add(new LinkRepair(r.Kind, r.Src, t, e, r.Target, r.TargetTri, "relinked",
                    $"triangle {t}'s link is re-pointed from {r.Target} triangle {r.TargetTri} to {c.Nav} triangle {c.Tri}" + (moves ? ", in another navmesh, so the navmeshes it borders change" : ""), c.Nav, c.Tri));
                relinked.Add((r.Src, t, e, c.Nav, c.Tri, c.Edge, before));
            }
            else
            {
                var wide = idx.Candidates(nav[r.Src].Space, a, b, unproven, r.Src);
                if (wide.Count > 0)
                {
                    string why = $"triangle {t}'s link is refused: {Names(wide)} lies along it only within the band the lab has not yet walked ({tol} to {unproven}), so it is neither relinked nor cut";
                    refusedEdges[(r.Src, t, e)] = why;
                    P(r.Src).Links.Add(new LinkRepair(r.Kind, r.Src, t, e, r.Target, r.TargetTri, "refused", why));
                }
                else
                {
                    w.Cut(t, e);
                    P(r.Src).Links.Add(new LinkRepair(r.Kind, r.Src, t, e, r.Target, r.TargetTri, "cut",
                        $"triangle {t}'s link to {r.Target} triangle {r.TargetTri} is cut: no edge across the border lies along it"));
                }
            }
        }

        // 2. every link made is written both ways; a far edge that already links elsewhere refuses the relink
        foreach (var x in relinked)
        {
            var fw = W(x.M); var back = fw.Link(x.J, x.Fe);
            if (back == (x.S, x.T)) continue;
            bool open = !fw.Flag[x.J, x.Fe] && fw.Field[x.J, x.Fe] < 0;
            bool repointable = fw.Flag[x.J, x.Fe] && (back is null || back.Value.Mesh == x.S || !nav.ContainsKey(back.Value.Mesh)
                                                        || back.Value.Tri >= nav[back.Value.Mesh].Geom.Triangles.Count);
            if (open || repointable)
            {
                fw.LinkTo(x.J, x.Fe, x.S, x.T);
                P(x.M).Links.Add(new LinkRepair("LINK-BACK", x.M, x.J, x.Fe, x.S, x.T, "linked-back",
                    $"triangle {x.J} gains its link back to {x.S} triangle {x.T}", x.S, x.T));
                continue;
            }
            // undo the relink: the far edge links a live triangle elsewhere, and is not overwritten
            var sw = W(x.S);
            if (x.Before is { } bf) sw.Table[bf.Item3] = sw.Table[bf.Item3] with { Mesh = bf.Item1, Tri = bf.Item2, Removed = false };
            var p = P(x.S);
            int i = p.Links.FindIndex(l => l.SrcTri == x.T && l.Edge == x.E && l.Status == "relinked");
            string why = $"triangle {x.T}'s link is refused: its match {x.M} triangle {x.J} already links {back?.Mesh} triangle {back?.Tri}, which is not overwritten";
            p.Links[i] = p.Links[i] with { Status = "refused", Why = why, To = null, ToTri = null };
            refusedEdges[(x.S, x.T, x.E)] = why;
        }

        // 3. a one-way link: resolved by another repair, given its link back, or refused
        foreach (var (r, t, e) in oneway)
        {
            if (!nav.TryGetValue(r.Target, out var far) || r.TargetTri < 0 || r.TargetTri >= far.Geom.Triangles.Count) continue;
            var (a, b) = Ends(r.Src, t, e);
            var (fe, gap) = NavBorderMatch.BestEdge(a, b, far.Geom, r.TargetTri);
            if (fe < 0) continue;
            var fw = W(r.Target); var back = fw.Link(r.TargetTri, fe);
            if (!gap.Within(tol) && Authored(r.Src, t, a, b, lower?.GetValueOrDefault(r.Target), tol, unproven) is { } author
                && !fw.Flag[r.TargetTri, fe] && fw.Field[r.TargetTri, fe] < 0)
            {
                // houseCARL 2026-11-22: a link back a lower version of this navmesh authored is restored, even across the
                // band: restoring what the losing version had is not inventing a link (the RV station's 0FFE80: Dawnguard's
                // version links back to 079A55 t59, Moon and Star's dropped it)
                fw.LinkTo(r.TargetTri, fe, r.Src, t);
                P(r.Target).Links.Add(On("linked-back", FormattableString.Invariant(
                    $"triangle {r.TargetTri} gains its link back to {r.Src} triangle {t}, restored from {author.Plugin}, in the band (its triangle {author.Tri} linked back there{(author.InTol ? "" : ", from an edge in the band too")}); the winner's edge lies {gap.Plan:0.#} u off in plan and {gap.Height:0.#} u in height, not yet walked both ways"), r.Src, t));
                continue;
            }
            if (!gap.Within(tol))
            {
                // the match lies only in the unproven band (an EDGE-ONEWAY found there, 2026-11-22): the row is a finding,
                // but no link back is written across a gap no walker has been seen to take both ways
                P(r.Target).Links.Add(new LinkRepair(r.Kind, r.Target, r.TargetTri, fe, r.Target, r.TargetTri, "refused",
                    FormattableString.Invariant($"{r.Src} triangle {t}'s one-way link is refused: its match, triangle {r.TargetTri}, lies {gap.Plan:0.#} u off in plan and {gap.Height:0.#} u in height, in the band not yet walked both ways, so no link back is written across it"),
                    null, null, RowSrc: r.Src));
                continue;
            }
            LinkRepair On(string status, string why, FormKey? to = null, int? toTri = null)
                => new(r.Kind, r.Target, r.TargetTri, fe, r.Target, r.TargetTri, status, why, to, toTri, RowSrc: r.Src);
            if (back == (r.Src, t))
            {
                P(r.Target).Links.Add(On("resolved", $"triangle {r.TargetTri} now links back to {r.Src} triangle {t}, by the repair above"));
                continue;
            }
            if (refusedEdges.TryGetValue((r.Target, r.TargetTri, fe), out var because))
            {
                P(r.Target).Links.Add(On("refused", $"{r.Src} triangle {t}'s one-way link is refused with it: {because}"));
                continue;
            }
            if (!fw.Flag[r.TargetTri, fe] && fw.Field[r.TargetTri, fe] < 0)
            {
                fw.LinkTo(r.TargetTri, fe, r.Src, t);
                P(r.Target).Links.Add(On("linked-back", $"triangle {r.TargetTri} gains its link back to {r.Src} triangle {t}", r.Src, t));
                continue;
            }
            string holds = back is { } bk ? $"links {bk.Mesh} triangle {bk.Tri}"
                         : fw.Flag[r.TargetTri, fe] ? $"names edge-link entry {fw.Field[r.TargetTri, fe]}, which is not in its table"
                         : $"joins its own triangle {fw.Field[r.TargetTri, fe]}";
            P(r.Target).Links.Add(On("refused",
                $"{r.Src} triangle {t}'s one-way link is refused: its match, triangle {r.TargetTri}, {holds}, which is not overwritten"));
        }

        var plan = new NavRepairPlan();
        foreach (var (k, p) in plans) { p.Change = ChangeOf(work.GetValueOrDefault(k)); plan.Plans.Add(p); }
        Check(plan, nav, tol, work.ToDictionary(kv => kv.Key, kv => kv.Value), null);
        return plan;
    }

    /// <summary>A lower version of the far navmesh that authored the link back: one of its triangles has an edge along the
    /// source edge (a, b) within the unproven band that links back to <paramref name="src"/> triangle <paramref name="t"/>.
    /// An author's link is evidence whatever its gap (houseCARL, 2026-11-22); whether the game walks it is the lab's question,
    /// and the row says so. Beyond the band the edge does not lie along the source, so it is not taken. The latest such
    /// version, or null; InTol says whether its edge was within the fitted tolerance.</summary>
    static (string Plugin, int Tri, bool InTol)? Authored(FormKey src, int t, Vec3 a, Vec3 b, IReadOnlyList<(string Plugin, NavmeshGeometry Geom)>? versions,
                                                         EdgeTolerance tol, EdgeTolerance wide)
    {
        foreach (var (plugin, g) in versions ?? Array.Empty<(string, NavmeshGeometry)>())
        {
            var links = g.EdgeLinks ?? Array.Empty<NavEdgeLink>();
            for (int i = 0; i < g.Triangles.Count; i++)
                for (int e = 0; e < 3; e++)
                {
                    var tr = g.Triangles[i];
                    if (!NavBorderMatch.EdgeIsLink(tr, e)) continue;
                    int k = NavBorderMatch.EdgeLink(tr, e);
                    if (k < 0 || k >= links.Count || links[k].Mesh != src || links[k].TriangleIndex != t) continue;
                    var (fe, gap) = NavBorderMatch.BestEdge(a, b, g, i);
                    if (fe == e && gap.Within(wide)) return (plugin, i, gap.Within(tol));
                }
        }
        return null;
    }

    /// <summary>The follow-the-ground plan for <paramref name="rows"/>: every used vertex within <paramref name="band"/> of its
    /// author's ground moves by what the ground moved under it, and the box's z is recomputed.</summary>
    public static NavRepairPlan Ground(IReadOnlyDictionary<FormKey, WinningNav> nav, IReadOnlyList<GroundRow> rows, float band, EdgeTolerance tol,
                                       ISet<FormKey>? scope = null, ISet<FormKey>? islands = null, string? naviProblem = null)
    {
        var plan = new NavRepairPlan();
        var moved = new Dictionary<FormKey, NavmeshGeometry>();
        foreach (var r in rows)
        {
            if (scope is not null && !scope.Contains(r.Nav)) continue;
            if (!nav.TryGetValue(r.Nav, out var w)) continue;
            var p = new NavPlan { Nav = r.Nav, Class = "ground" };
            plan.Plans.Add(p);
            if (naviProblem is not null)
            {
                p.Status = "refused";
                p.Why.Add($"the navmesh info map cannot be read ({naviProblem}), so whether {r.Nav} is an island is not known; following the ground is refused");
                continue;
            }
            if (islands is not null && islands.Contains(r.Nav))
            {
                // the navmesh info map keeps its own copy of an island's geometry: moving the vertices would leave it stale
                // (houseCARL's sweep, 2026-11-22: 0DEEFB in the Clockwork order); refused until island support (R7)
                p.Status = "refused";
                p.Why.Add($"{r.Nav} is an island in the navmesh info map, which keeps its own copy of the geometry; following the ground is refused until island support (R7)");
                continue;
            }
            if (r.Mover is { } mv)
            {
                // the mover re-cut this navmesh for the ground it built (The Great City of Winterhold's 100F4B, 2026-11-22: its
                // own version has holes where its buildings stand; vanilla's, draped over the new ground, runs under them)
                p.Status = "refused";
                p.Why.Add($"{mv.Mover} moved the ground under {r.Nav} and ships its own re-cut of it "
                        + $"({mv.MoverTris} triangles; {mv.Winner}'s has {mv.WinnerTris}), but {mv.Winner}'s version overrides it; following the ground "
                        + $"would lay {mv.Winner}'s navmesh over what {mv.Mover} built there, so carry {mv.Mover}'s version instead (navedit use_version)"
                        + (mv.Clobbered is { } cl ? $"; this is the CLOBBERED row {cl}, seen from the ground" : "")
                        + (mv.OtherMovers is { Count: > 0 } om ? $"; {string.Join(", ", om)} also moved ground under it, which that version may not fit, so check those parts after carrying it" : ""));
                continue;
            }
            int unclear = r.Vertices.Count(v => v.CannotTell);
            var verts = w.Geom.Vertices.ToArray();
            var ch = new NavChange();
            foreach (var v in r.Vertices)
            {
                if (v.CannotTell || v.Index < 0 || v.Index >= verts.Length) continue;
                float d = v.Winning - v.Authored;
                if (MathF.Abs(verts[v.Index].Z - v.Authored) > band || MathF.Abs(d) < 0.01f) continue;
                verts[v.Index] = verts[v.Index] with { Z = verts[v.Index].Z + d };
                ch.Vertices[v.Index] = verts[v.Index];
            }
            if (ch.Vertices.Count == 0) { p.Status = "refused"; p.Why.Add("no vertex sits on its author's ground where the ground moved"); continue; }
            if (unclear > 0) { p.Status = "refused"; p.Why.Add($"{unclear} of its vertices cannot be told (the terrain's two diagonals disagree), so moving their neighbours could tear it; it is left as it is"); continue; }
            var used = UsedExtent(w.Geom.Triangles, verts);
            ch.Min = w.Geom.Min with { Z = used.Min.Z }; ch.Max = w.Geom.Max with { Z = used.Max.Z };
            p.Change = ch;
            p.Why.Add($"{ch.Vertices.Count} vertices follow the ground the later plugin moved; the box's z becomes {F(used.Min.Z)} to {F(used.Max.Z)}");
            moved[r.Nav] = w.Geom with { Vertices = verts, Min = ch.Min.Value, Max = ch.Max.Value };
        }
        Check(plan, nav, tol, null, moved);
        return plan;
    }

    static (Vec3 Min, Vec3 Max) UsedExtent(IReadOnlyList<NavTri> tris, IReadOnlyList<Vec3> v)
    {
        var used = tris.SelectMany(t => new[] { (int)t.A, t.B, t.C }).Where(i => i >= 0 && i < v.Count).Distinct().Select(i => v[i]).ToList();
        if (used.Count == 0) return (default, default);
        return (new Vec3(used.Min(p => p.X), used.Min(p => p.Y), used.Min(p => p.Z)), new Vec3(used.Max(p => p.X), used.Max(p => p.Y), used.Max(p => p.Z)));
    }

    static NavChange ChangeOf(Work? w)
    {
        var c = new NavChange();
        if (w is null) return c;
        var g = w.G; var links = g.EdgeLinks ?? Array.Empty<NavEdgeLink>();
        foreach (var x in w.Table)
        {
            if (x.Orig < 0) { if (!x.Removed) c.AddedEntries.Add(new NavEdgeLink(x.Mesh, x.Tri, x.Type)); continue; }
            if (x.Removed) { c.RemovedEntries.Add(x.Orig); continue; }
            if (x.Mesh != links[x.Orig].Mesh || x.Tri != links[x.Orig].TriangleIndex) c.SetEntries[x.Orig] = (x.Mesh, x.Tri);
        }
        // final indices: the kept entries in order, then the appended ones
        var final = new Dictionary<int, short>(); short n = 0;
        for (int i = 0; i < w.Table.Count; i++) if (!w.Table[i].Removed && w.Table[i].Orig >= 0) final[i] = n++;
        for (int i = 0; i < w.Table.Count; i++) if (!w.Table[i].Removed && w.Table[i].Orig < 0) final[i] = n++;
        for (int t = 0; t < g.Triangles.Count; t++)
            for (int e = 0; e < 3; e++)
            {
                var tr = g.Triangles[t];
                short f = w.Flag[t, e] && final.TryGetValue(w.Field[t, e], out var fi) ? fi : w.Field[t, e];
                if (f != NavBorderMatch.EdgeLink(tr, e)) c.EdgeFields[(t, e)] = f;
                if (w.Flag[t, e] != NavBorderMatch.EdgeIsLink(tr, e)) c.FlagBits[(t, e)] = w.Flag[t, e];
            }
        return c;
    }

    /// <summary>The housecarl_apply ops for one navmesh's change, in an order whose keys stay right as they apply: table entries
    /// re-pointed (original index), then removed (highest index first), then appended; then edge fields and flag bits, then
    /// vertices and the box.</summary>
    public static List<RepairOp> Ops(FormKey nav, NavChange c, IReadOnlyList<NavEdgeLink>? before = null)
    {
        var id = nav.ToString(); var ops = new List<RepairOp>();
        foreach (var (k, (m, t)) in c.SetEntries)
        {
            // only the part that changes: a re-point within the named navmesh sets the triangle alone
            if (before is null || k >= before.Count || before[k].Mesh != m) ops.Add(new RepairOp(id, $"Data.EdgeLinks[{k}].Mesh", "Set", Value: m.ToString()));
            if (before is null || k >= before.Count || before[k].TriangleIndex != t) ops.Add(new RepairOp(id, $"Data.EdgeLinks[{k}].TriangleIndex", "Set", Value: t.ToString(CultureInfo.InvariantCulture)));
        }
        foreach (var k in c.RemovedEntries.Reverse()) ops.Add(new RepairOp(id, "Data.EdgeLinks", "Remove", Key: k.ToString(CultureInfo.InvariantCulture)));
        foreach (var a in c.AddedEntries)
            ops.Add(new RepairOp(id, "Data.EdgeLinks", "Add", Compose: new Dictionary<string, string>
            {
                ["Unknown"] = a.Type.ToString(CultureInfo.InvariantCulture), ["Mesh"] = a.Mesh.ToString(), ["TriangleIndex"] = a.TriangleIndex.ToString(CultureInfo.InvariantCulture),
            }));
        foreach (var ((t, e), f) in c.EdgeFields) ops.Add(new RepairOp(id, $"Data.Triangles[{t}].{EdgeName[e]}", "Set", Value: f.ToString(CultureInfo.InvariantCulture)));
        foreach (var ((t, e), on) in c.FlagBits) ops.Add(new RepairOp(id, $"Data.Triangles[{t}].Flags", on ? "Add" : "Remove", Value: EdgeName[e]));
        foreach (var (i, v) in c.Vertices) ops.Add(new RepairOp(id, "Data.Vertices", "SetAtIndex", Key: i.ToString(CultureInfo.InvariantCulture), Value: V(v)));
        if (c.Min is { } mn) ops.Add(new RepairOp(id, "Data.Min", "Set", Value: V(mn)));
        if (c.Max is { } mx) ops.Add(new RepairOp(id, "Data.Max", "Set", Value: V(mx)));
        return ops;
    }

    /// <summary>A navmesh's geometry with a change applied: vertices, box, table and edge fields.</summary>
    public static NavmeshGeometry Apply(NavmeshGeometry g, NavChange c)
    {
        var verts = g.Vertices.ToArray();
        foreach (var (i, v) in c.Vertices) verts[i] = v;
        var links = (g.EdgeLinks ?? Array.Empty<NavEdgeLink>()).ToList();
        foreach (var (k, (m, t)) in c.SetEntries) links[k] = links[k] with { Mesh = m, TriangleIndex = t };
        foreach (var k in c.RemovedEntries.Reverse()) links.RemoveAt(k);
        links.AddRange(c.AddedEntries);
        var tris = g.Triangles.ToArray();
        foreach (var ((t, e), f) in c.EdgeFields)
            tris[t] = e switch { 0 => tris[t] with { EdgeLink01 = f }, 1 => tris[t] with { EdgeLink12 = f }, _ => tris[t] with { EdgeLink20 = f } };
        foreach (var ((t, e), on) in c.FlagBits) tris[t] = tris[t] with { Flags = on ? tris[t].Flags | (1 << e) : tris[t].Flags & ~(1 << e) };
        return g with { Vertices = verts, Triangles = tris, EdgeLinks = links, Min = c.Min ?? g.Min, Max = c.Max ?? g.Max };
    }

    static readonly string[] Allowed = { "Data.EdgeLinks", "Data.Triangles[", "Data.Vertices", "Data.Min", "Data.Max" };

    /// <summary>The invariants, on the planned state; a navmesh that fails one, and any whose edit depends on it, is refused.</summary>
    static void Check(NavRepairPlan plan, IReadOnlyDictionary<FormKey, WinningNav> nav, EdgeTolerance tol,
                      Dictionary<FormKey, Work>? work, Dictionary<FormKey, NavmeshGeometry>? moved)
    {
        var after = new Dictionary<FormKey, NavmeshGeometry>();
        foreach (var p in plan.Plans) if (p.Status != "refused") after[p.Nav] = Apply(nav[p.Nav].Geom, p.Change);
        NavmeshGeometry G(FormKey k) => after.TryGetValue(k, out var g) ? g : nav[k].Geom;
        bool Has(FormKey k) => after.ContainsKey(k) || nav.ContainsKey(k);
        var failed = new HashSet<FormKey>();
        foreach (var p in plan.Plans.Where(p => p.Status != "refused"))
        {
            var before = nav[p.Nav].Geom; var g = after[p.Nav];
            void Inv(string name, bool holds, string detail) { p.Invariants.Add(new PlanInvariant(name, holds, detail)); if (!holds) { failed.Add(p.Nav); p.Why.Add($"refused: {detail}"); } }
            Inv("counts", g.Vertices.Count == before.Vertices.Count && g.Triangles.Count == before.Triangles.Count,
                $"{g.Vertices.Count} vertices and {g.Triangles.Count} triangles, as before");
            var links = g.EdgeLinks ?? Array.Empty<NavEdgeLink>();
            string? bad = null;
            for (int t = 0; t < g.Triangles.Count && bad is null; t++)
                for (int e = 0; e < 3 && bad is null; e++)
                    if (NavBorderMatch.EdgeIsLink(g.Triangles[t], e) && NavBorderMatch.EdgeLink(g.Triangles[t], e) is var k && (k < 0 || k >= links.Count))
                        bad = $"triangle {t}'s edge names table entry {k} of {links.Count}";
            for (int k = 0; k < links.Count && bad is null; k++)
                if (p.Change.SetEntries.ContainsKey(k) || k >= links.Count - p.Change.AddedEntries.Count)
                    if (!Has(links[k].Mesh) || links[k].TriangleIndex < 0 || links[k].TriangleIndex >= G(links[k].Mesh).Triangles.Count)
                        bad = $"table entry {k} names {links[k].Mesh} triangle {links[k].TriangleIndex}, which the planned state lacks";
            Inv("links resolve", bad is null, bad ?? "every planned link names a triangle that exists");
            string? oneWay = null;
            foreach (var l in p.Links.Where(l => l.Status is "relinked" or "linked-back"))
            {
                if (l.To is not { } m || l.ToTri is not { } j || !Has(m)) continue;
                var ft = G(m); var (a, b) = NavBorderMatch.EdgeVerts(g.Triangles[l.SrcTri], l.Edge);
                var (fe, _) = NavBorderMatch.BestEdge(g.Vertices[a], g.Vertices[b], ft, j);
                var fl = ft.EdgeLinks ?? Array.Empty<NavEdgeLink>();
                int fk = fe >= 0 ? NavBorderMatch.EdgeLink(ft.Triangles[j], fe) : -1;
                if (fe < 0 || !NavBorderMatch.EdgeIsLink(ft.Triangles[j], fe) || fk < 0 || fk >= fl.Count || fl[fk].Mesh != p.Nav || fl[fk].TriangleIndex != l.SrcTri)
                    oneWay ??= $"triangle {l.SrcTri}'s link to {m} triangle {j} has no link back in the planned state";
            }
            Inv("two-way", oneWay is null, oneWay ?? "every link the plan makes is written both ways");
            var (umin, umax) = UsedExtent(g.Triangles, g.Vertices);
            bool boxOk = Near(g.Min, umin) && Near(g.Max, umax);
            var ub = UsedExtent(before.Triangles, before.Vertices);
            bool wasOk = Near(before.Min, ub.Min) && Near(before.Max, ub.Max);
            Inv("box", boxOk || !wasOk,
                boxOk ? "the box is the used vertices' extent" : wasOk ? $"the box {g.Min}-{g.Max} is not the used vertices' extent {umin}-{umax}"
                      : "the winner's box was not the used vertices' extent either; it is left as it was");
            p.Edits = Ops(p.Nav, p.Change, before.EdgeLinks);
            var outside = p.Edits.FirstOrDefault(o => !Allowed.Any(a => o.FieldPath.StartsWith(a, StringComparison.Ordinal))
                                                      || (o.FieldPath.StartsWith("Data.Triangles[") && !o.FieldPath.EndsWith(".Flags") && !o.FieldPath.Contains(".EdgeLink_")));
            Inv("grid and the rest untouched", outside is null, outside is null ? "no edit touches the grid, the cover data or the doors" : $"an edit touches {outside.FieldPath}");
        }
        // a height move must not break a link that matched: both ways, against the planned neighbours
        if (moved is { Count: > 0 })
            foreach (var (k, g) in moved)
            {
                var p = plan.Plans.First(x => x.Nav == k);
                string? broke = null;
                foreach (var (s, w) in nav)
                {
                    var src = G(s); var sb = nav[s].Geom; var sl = sb.EdgeLinks ?? Array.Empty<NavEdgeLink>();
                    if (s != k && !sl.Any(x => x.Mesh == k)) continue;
                    for (int t = 0; t < sb.Triangles.Count && broke is null; t++)
                        for (int e = 0; e < 3 && broke is null; e++)
                        {
                            if (!NavBorderMatch.EdgeIsLink(sb.Triangles[t], e)) continue;
                            int li = NavBorderMatch.EdgeLink(sb.Triangles[t], e);
                            if (li < 0 || li >= sl.Count || sl[li].Type is 1 or 2) continue;
                            var (m, j) = (sl[li].Mesh, sl[li].TriangleIndex);
                            if ((s != k && m != k) || !nav.ContainsKey(m) || j < 0 || j >= nav[m].Geom.Triangles.Count) continue;
                            var (a0, b0) = NavBorderMatch.EdgeVerts(sb.Triangles[t], e);
                            var was = NavBorderMatch.BestEdge(sb.Vertices[a0], sb.Vertices[b0], nav[m].Geom, j);
                            var now = NavBorderMatch.BestEdge(src.Vertices[a0], src.Vertices[b0], G(m), j);
                            if (was.Edge >= 0 && was.Gap.Within(tol) && !(now.Edge >= 0 && now.Gap.Within(tol)))
                                broke = $"moving it would break {s} triangle {t}'s link to {m} triangle {j} (height gap {F(now.Gap.Height)} u after the move)";
                        }
                    if (broke is not null) break;
                }
                p.Invariants.Add(new PlanInvariant("links still meet", broke is null, broke ?? "every link that matched still matches after the move"));
                if (broke is not null) { failed.Add(k); p.Why.Add($"refused: {broke}"); }
            }
        // a refused navmesh takes with it every planned edit that depends on it
        bool grew = true;
        while (grew)
        {
            grew = false;
            foreach (var p in plan.Plans.Where(p => !failed.Contains(p.Nav)))
                if (p.Links.Any(l => l.Status is "relinked" or "linked-back" && l.To is { } m && failed.Contains(m)))
                { failed.Add(p.Nav); p.Why.Add("refused: a navmesh its repair links to is refused"); grew = true; }
        }
        foreach (var p in plan.Plans)
        {
            if (failed.Contains(p.Nav) || p.Change.Empty)
            {
                p.Status = "refused"; p.Edits = new List<RepairOp>(); p.Change = new NavChange();
                p.AdjacencyChange = false;
            }
            else if (p.Class == "relink" && p.Links.All(l => l.Status is "cut" or "refused" or "resolved")) p.Status = "cut";
            else p.Status = "planned";
            if (p.Class == "relink") p.Why.InsertRange(0, p.Links.Select(l => l.Why));
        }
    }

    /// <summary>Refuse each named navmesh's plan for its reason, and with it every planned edit that links to a refused one.</summary>
    public static void Refuse(NavRepairPlan plan, IReadOnlyDictionary<FormKey, string> why)
    {
        var failed = new Dictionary<FormKey, string>(why);
        bool grew = true;
        while (grew)
        {
            grew = false;
            foreach (var p in plan.Plans.Where(p => p.Status != "refused" && !failed.ContainsKey(p.Nav)))
                if (p.Links.Any(l => l.Status is "relinked" or "linked-back" && l.To is { } m && failed.ContainsKey(m)))
                { failed[p.Nav] = "a navmesh its repair links to is refused"; grew = true; }
        }
        foreach (var p in plan.Plans.Where(p => p.Status != "refused" && failed.ContainsKey(p.Nav)))
        {
            p.Status = "refused"; p.Edits = new List<RepairOp>(); p.Change = new NavChange(); p.AdjacencyChange = false;
            p.Why.Add($"refused: {failed[p.Nav]}");
        }
    }

    static bool Near(Vec3 a, Vec3 b) => MathF.Abs(a.X - b.X) <= 1 && MathF.Abs(a.Y - b.Y) <= 1 && MathF.Abs(a.Z - b.Z) <= 1;

    /// <summary>A link's re-check verdict: what the plan did to it, against whether its row still holds on the order with the
    /// patch added.</summary>
    public static string Verdict(string status, bool stillHolds) => (status, stillHolds) switch
    {
        ("relinked" or "linked-back" or "planned", false) => "repaired",
        ("cut", false) => "cut",
        ("resolved", false) => "repaired",
        ("refused", true) => "refused",
        ("refused", false) => "refused, and the row is gone: another change resolved it",
        (_, true) => "not repaired: the row still holds with the patch added",
        _ => "not repaired",
    };
}
