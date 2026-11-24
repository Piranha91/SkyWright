using System;
using System.Collections.Generic;
using System.Numerics;
using HarnessShared;
using HousecarlCore;
using SkyWright.Engine;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

// ======================================================================
//  FloorFeed — one cell's FloorField, built the way every probe builds it.
//
//  The floor rule is core's (SkyWright.Engine.NavmeshFloor; the field that
//  answers it point by point is FloorField). This is the harness's plumbing
//  so that navprobe, blockprobe, corridorprobe, stateprobe and floorgapprobe
//  stand the route rule on ONE floor: the refs that can be a floor (a cell's
//  own for an interior; the 3×3 neighbourhood BY POSITION for an exterior,
//  because a floor mesh placed in the next cell reaches under this one and
//  a persistent ref lives in the worldspace's persistent cell), each with its
//  world box and a column index built on first use from the same actor-solid
//  collision soup the obstacles are read from (CollisionLayers), and the
//  terrain (CellPlacements.Land) by grid cell.
//
//  Provenance (Q24, 2026-09-05): read from a cell's own refs alone, the floor
//  under 3.7 % of exterior triangles is lost and another 4 % falls to the
//  terrain under a neighbour's street mesh — so a caller that can reach the
//  neighbourhood should. Deleted refs are never floors, and a ref that is not
//  there when the cell loads is not one either — WHICH refs those are is the
//  caller's population (_shared/EnablePopulation.cs): the load-time enable
//  chain by default since Q27, the record's own flag under --flags.
//  The Base Object Swapper layer is not applied to floor candidates: the
//  floor is the authored floor against the authored navmesh, and a swap
//  changes obstacles far more often than floors (stated, not hidden).
//
//  Reach: the game loads more than the 3x3 (RingReach): every reference anchored up to two cells out, and the large
//  references a worldspace's lists name out to five. A large mesh anchored two cells away (a mountain or cliff) can lay
//  an actor-solid floor over a square, which a filing by position alone never offers. FloorIndex therefore also keeps
//  those references (FloorReach), and NeighbourhoodOf admits each one whose placed OBND box enters the 3x3, by
//  viewprobe's own test (RingReach.EntersSquare). A base with no usable OBND is not admitted from beyond the 3x3.
//  Whether a large reference at rings 3-5 has actor collision in game is NOT read (ReachLarge); the by-position filing
//  above is unchanged.
// ======================================================================

internal static class FloorFeed
{
    /// <summary>Reach admission of references anchored at ring 2 (RingReach.EdgeRadius).</summary>
    internal const bool ReachRing2 = true;
    /// <summary>Reach admission of listed large references anchored at rings 3 to RingReach.LargeRadius. Admitted by
    /// record: their actor collision where they enter the 3x3 is not read in game.</summary>
    internal const bool ReachLarge = true;
    /// <summary>A child worldspace takes its own lists' union (viewprobe's default --large-refs-child union).</summary>
    internal const bool ReachChildUnion = true;

    /// <summary>The caller's geometry lookup: a ref's actor-solid soup and its model-space box, or null when the ref has
    /// no usable solid (no model, no actor-solid body, unreadable).</summary>
    public delegate (Aabb Box, TriangleSoup Soup)? GeometryOf(PlacedRef r);

    /// <summary>Build a cell's field from the refs that can be a floor under it and the terrain those refs stand on.
    /// <paramref name="enabled"/>: the caller's runtime enable resolution — <c>EnablePopulation.Predicate</c>, which is
    /// the load-time chain (<see cref="EnableState"/>) by default and a state arm's under <c>--state</c>. NULL is the
    /// flag population: the record's own initially-disabled flag, what every run before Q27 judged and what <c>--flags</c>
    /// still asks for. Q25 (2026-09-05): on the flag alone a room variant switched off at load — Obscure's council dais,
    /// enable-parented to an initially-disabled marker — stood as the floor 48 u over a whole room's navmesh and moved
    /// verdicts, while the obstacles of the same run had already gone through the resolver.</summary>
    public static FloorField Build(IEnumerable<PlacedRef> refs, GeometryOf geometry,
                                   IEnumerable<((int X, int Y) Grid, float[,] Heights)>? land,
                                   float fallbackOffset = DetectorModel.FallbackOffset,
                                   float maxDepth = NavmeshFloor.DefaultMaxDepth, float upReach = NavmeshFloor.DefaultUpReach,
                                   Func<FormKey, bool>? enabled = null)
    {
        var f = new FloorField(maxDepth, upReach, fallbackOffset);
        if (land is not null) foreach (var (g, h) in land) f.AddLand(g, h);
        foreach (var r in refs)
        {
            if (r.Deleted || r.Base is null) continue;
            if (enabled is null ? r.InitiallyDisabled : !enabled(r.FormKey)) { f.SkippedDisabled++; continue; }
            if (geometry(r) is not { } g || g.Box.IsEmpty || g.Soup.TriangleCount == 0) continue;
            var world = SpatialOverlap.ToWorld(g.Box, r.Position, r.Rotation, r.Scale);
            var soup = g.Soup; var pos = r.Position; var rot = r.Rotation; float scale = r.Scale;
            f.Add(r.FormKey, world, () => Columns(soup, pos, rot, scale));
        }
        return f;
    }

    /// <summary>A soup placed into the world by a reference's transform, as a column index (navprobe's BuildColumns).</summary>
    public static MeshColumnIndex? Columns(TriangleSoup soup, Vec3 pos, Vec3 rot, float scale)
    {
        if (soup.TriangleCount == 0) return null;
        var m = SpatialOverlap.RefrRotation(rot);
        var t = new Vector3(pos.X, pos.Y, pos.Z);
        var w = new Vector3[soup.Vertices.Length];
        for (int i = 0; i < w.Length; i++) w[i] = Vector3.Transform(soup.Vertices[i] * scale, m) + t;
        return MeshColumnIndex.Build(w, soup.Indices);
    }

    /// <summary>One plugin's floor filing, the way the single-plugin probes (navprobe, corridorprobe) build it: the reach's
    /// lists and base boxes first (FloorIndex.ObserveMod), then every cell in order (LastSighting.Cells), each handed to
    /// <paramref name="perCell"/> before it is filed. <paramref name="name"/> names an unreadable LAND (the plugin's file
    /// name when null). Their --moved-ref-selftest runs this same function on FloorReachFixture.</summary>
    public static FloorIndex FileOnePlugin(ISkyrimModGetter mod, Func<FormKey, (int X, int Y), bool>? wantGrid = null,
                                           Action<CellPlacements>? perCell = null, string? name = null)
    {
        var index = new FloorIndex(wantGrid);
        index.ObserveMod(mod);
        foreach (var cp in LastSighting.Cells(mod))
        {
            perCell?.Invoke(cp);
            index.Observe(cp, name ?? mod.ModKey.FileName.String);
        }
        return index;
    }

    /// <summary>The exterior grid cell a position falls in.</summary>
    public static (int X, int Y) GridOf(Vec3 p) => ((int)MathF.Floor(p.X / 4096f), (int)MathF.Floor(p.Y / 4096f));

    /// <summary>
    /// The floor filing under the last-sighting rule (_shared/LastSighting.cs): per worldspace, the references that can be
    /// a floor filed by the grid cell their POSITION falls in, and the LAND by grid.
    ///
    /// Call <see cref="Observe"/> for EVERY cell of EVERY plugin in load order (LastSighting.Cells), interiors and other
    /// worldspaces included. Each reference is held at most once, at its last sighting: a later sighting anywhere else
    /// (another grid, cell, worldspace, an interior, a deletion with or without placement data, a record with no base)
    /// takes the earlier filing out. A reference is filed when its last sighting is exterior, not deleted, has a base, and
    /// its position's grid passes the caller's wantGrid (all grids when null) -- by POSITION, so a worldspace's persistent
    /// refs, and a ref filed in a far cell's record but standing here, are filed where they stand. The ENABLE STATE is not
    /// decided here (Q27): a ref whose own flag is set may still be there through an enable parent; the filtering happens
    /// once, in Build, against the run's population.
    ///
    /// LAND: the last LAND record at a grid wins. A later cell copy with no LAND leaves the earlier heights; a later LAND
    /// that says it has heights and gives none (CellPlacements.LandUnreadableReason) removes them and names the grid in
    /// UnreadableLandWhy, which a caller refuses or counts on; a later LAND that DECLARES no heights
    /// (CellPlacements.LandDeclaresNoHeights: the engine draws no ground there and has no terrain collision) removes them
    /// and clears any name, so the grid reads as having no terrain; a later readable LAND takes the grid and clears the name.
    ///
    /// RefsByWorld and LandByWorld are the maps Neighbourhood reads, kept current as cells are observed.
    /// </summary>
    public sealed class FloorIndex
    {
        public readonly Dictionary<FormKey, Dictionary<(int, int), Dictionary<FormKey, PlacedRef>>> RefsByWorld = new();
        public readonly Dictionary<FormKey, Dictionary<(int, int), float[,]>> LandByWorld = new();
        /// <summary>Grids whose last LAND gives no heights, with the reason and the plugin.</summary>
        public readonly Dictionary<(FormKey Ws, int X, int Y), string> UnreadableLandWhy = new();
        public IEnumerable<(FormKey Ws, int X, int Y)> UnreadableLand => UnreadableLandWhy.Keys;
        readonly Func<FormKey, (int X, int Y), bool>? _wantGrid;
        readonly Dictionary<FormKey, (FormKey Ws, (int, int) Grid)> _where = new();
        // Reach (the header): per worldspace, the references by anchor, the lists, and the bases' OBND boxes.
        readonly Func<FormKey, (int X, int Y), bool>? _wantAnchor;
        readonly Dictionary<FormKey, FloorReach> _reach = new();
        readonly Dictionary<FormKey, (FormKey Ws, (int, int) Anchor)> _reachWhere = new();
        readonly Dictionary<FormKey, (Vec3 Min, Vec3 Max)> _bounds = new();

        /// <summary><paramref name="wantGrid"/> filters the by-position filing (all grids when null). The reach set keeps a
        /// reference whose anchor lies within RingReach.LargeRadius of a grid wantGrid takes (every anchor when wantGrid is
        /// null), unless <paramref name="wantAnchor"/> says otherwise.</summary>
        public FloorIndex(Func<FormKey, (int X, int Y), bool>? wantGrid = null, Func<FormKey, (int X, int Y), bool>? wantAnchor = null)
        {
            _wantGrid = wantGrid;
            if (wantAnchor is not null) _wantAnchor = wantAnchor;
            else if (wantGrid is not null)
                _wantAnchor = (ws, a) =>
                {
                    for (int dx = -RingReach.LargeRadius; dx <= RingReach.LargeRadius; dx++)
                        for (int dy = -RingReach.LargeRadius; dy <= RingReach.LargeRadius; dy++)
                            if (wantGrid(ws, (a.X + dx, a.Y + dy))) return true;
                    return false;
                };
        }

        FloorReach ReachFor(FormKey ws)
        {
            if (!_reach.TryGetValue(ws, out var fr)) _reach[ws] = fr = new FloorReach(_bounds);
            return fr;
        }

        /// <summary>The reach of one worldspace: what NeighbourhoodOf admits from (created empty when nothing is filed).</summary>
        public FloorReach ReachOf(FormKey ws) => ReachFor(ws);

        /// <summary>One plugin, in load order, BEFORE its cells: its worldspace records' large-reference lists.</summary>
        public void ObserveLists(ISkyrimModGetter mod)
        {
            foreach (var ws in mod.Worldspaces.Records) ReachFor(ws.FormKey).Lists.Observe(mod.ModKey.FileName.String, ws);
        }

        /// <summary>One base record, in load order: its OBND box as the record states it (the last one wins; a record that
        /// states none takes an earlier box out), the box reach admission tests.</summary>
        public void NoteBase(Mutagen.Bethesda.Plugins.Records.IMajorRecordGetter rec)
        {
            if (rec is not IObjectBoundedGetter ob) return;
            if (ob.ObjectBounds is { } bnd)
                _bounds[rec.FormKey] = (
                    new Vec3(Math.Min(bnd.First.X, bnd.Second.X), Math.Min(bnd.First.Y, bnd.Second.Y), Math.Min(bnd.First.Z, bnd.Second.Z)),
                    new Vec3(Math.Max(bnd.First.X, bnd.Second.X), Math.Max(bnd.First.Y, bnd.Second.Y), Math.Max(bnd.First.Z, bnd.Second.Z)));
            else _bounds.Remove(rec.FormKey);
        }

        /// <summary>One plugin's lists and base boxes (ObserveLists, NoteBase over every record), for a walk that does not
        /// already enumerate the plugin's records. Call BEFORE the plugin's cells.</summary>
        public void ObserveMod(ISkyrimModGetter mod)
        {
            ObserveLists(mod);
            foreach (var rec in mod.EnumerateMajorRecords()) NoteBase(rec);
        }

        /// <summary>The maps of one worldspace, created empty (both together, as callers index one by the other's keys)
        /// when nothing has been filed there.</summary>
        public Dictionary<(int, int), Dictionary<FormKey, PlacedRef>> RefsOf(FormKey ws) { Ensure(ws); return RefsByWorld[ws]; }
        public Dictionary<(int, int), float[,]> LandOf(FormKey ws) { Ensure(ws); return LandByWorld[ws]; }
        void Ensure(FormKey ws)
        {
            if (!RefsByWorld.ContainsKey(ws)) RefsByWorld[ws] = new();
            if (!LandByWorld.ContainsKey(ws)) LandByWorld[ws] = new();
        }

        /// <summary>One cell of one plugin, in load order. <paramref name="plugin"/> only names an unreadable LAND.</summary>
        public void Observe(CellPlacements cp, string? plugin = null)
        {
            foreach (var r in cp.Refs)
            {
                Remove(r.FormKey);
                RemoveReach(r.FormKey);
                if (cp.Interior || cp.Worldspace.IsNull || r.Deleted || r.Base is null) continue;
                var anchor = RingReach.AnchorOf(cp, r);
                if (_wantAnchor is null || _wantAnchor(cp.Worldspace, anchor))
                {
                    ReachFor(cp.Worldspace).File(anchor, r);
                    _reachWhere[r.FormKey] = (cp.Worldspace, anchor);
                }
                var g = GridOf(r.Position);
                if (_wantGrid is not null && !_wantGrid(cp.Worldspace, g)) continue;
                var byGrid = RefsOf(cp.Worldspace);
                if (!byGrid.TryGetValue(g, out var d)) byGrid[g] = d = new Dictionary<FormKey, PlacedRef>();
                d[r.FormKey] = r;
                _where[r.FormKey] = (cp.Worldspace, g);
            }
            if (cp.DeletedRefs is { Count: > 0 } deleted)
                foreach (var d in deleted) { Remove(d.FormKey); RemoveReach(d.FormKey); }

            if (cp.Interior || cp.Worldspace.IsNull || cp.Grid is not { } cg) return;
            if (_wantGrid is not null && !_wantGrid(cp.Worldspace, cg)) return;
            if (cp.LandUnreadableReason is { } why)
            {
                LandOf(cp.Worldspace).Remove(cg);
                UnreadableLandWhy[(cp.Worldspace, cg.X, cg.Y)] = why + (plugin is null ? "" : $" ({plugin})");
            }
            else if (cp.LandDeclaresNoHeights)
            {
                LandOf(cp.Worldspace).Remove(cg);
                UnreadableLandWhy.Remove((cp.Worldspace, cg.X, cg.Y));
            }
            else if (cp.Land is { } land)
            {
                LandOf(cp.Worldspace)[cg] = land;
                UnreadableLandWhy.Remove((cp.Worldspace, cg.X, cg.Y));
            }
        }

        void Remove(FormKey k)
        {
            if (!_where.Remove(k, out var at)) return;
            if (RefsByWorld.TryGetValue(at.Ws, out var byGrid) && byGrid.TryGetValue(at.Grid, out var d))
            {
                d.Remove(k);
                if (d.Count == 0) byGrid.Remove(at.Grid);
            }
        }

        void RemoveReach(FormKey k)
        {
            if (_reachWhere.Remove(k, out var at) && _reach.TryGetValue(at.Ws, out var fr)) fr.Unfile(at.Anchor, k);
        }

        /// <summary>The unreadable-LAND grids inside the 3x3 around <paramref name="grid"/> of <paramref name="ws"/>, with
        /// their reasons: what a caller refuses or counts on before it builds a field there.</summary>
        public List<string> UnreadableNear(FormKey ws, (int X, int Y) grid)
        {
            var list = new List<string>();
            for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                    if (UnreadableLandWhy.TryGetValue((ws, grid.X + dx, grid.Y + dy), out var why))
                        list.Add($"grid {grid.X + dx},{grid.Y + dy}: {why}");
            return list;
        }

        /// <summary>The 3x3 neighbourhood of one worldspace (Neighbourhood over this index's maps), plus what its reach
        /// admits (FloorReach.Admit).</summary>
        public (List<PlacedRef> Refs, List<((int X, int Y) Grid, float[,] Heights)> Land) NeighbourhoodOf(FormKey ws, (int X, int Y) grid)
        {
            var nb = Neighbourhood(grid, RefsOf(ws), LandOf(ws));
            ReachOf(ws).Admit(grid, nb.Refs);
            return nb;
        }
    }

    /// <summary>
    /// One worldspace's REACH (the header): every reference whose last sighting is exterior here, not deleted and with a
    /// base, filed by its anchor (RingReach.AnchorOf), the worldspace's large-reference lists, and the bases' OBND boxes
    /// (shared by the index). <see cref="Admit"/> adds to a 3x3 neighbourhood each reference not already in it that is
    /// anchored within ring 2 (ReachRing2: ring 2, and the rare reference held in the 3x3 but standing outside it), or at
    /// rings 3..LargeRadius and named by the selected list (ReachLarge), whose placed box enters the 3x3
    /// (RingReach.EntersSquare). A base with no usable OBND is not admitted (viewprobe's rule); FloorFeed.Build then
    /// decides whether the admitted reference has an actor-solid surface at all. Owned collections only, no callbacks:
    /// viewprobe's retained cell walk carries it as it is (its filing is not changed after the walk; Admit only counts).
    /// </summary>
    public sealed class FloorReach
    {
        readonly Dictionary<(int, int), Dictionary<FormKey, PlacedRef>> _byAnchor = new();
        readonly Dictionary<FormKey, (Vec3 Min, Vec3 Max)> _bounds;
        internal readonly LargeReferenceLists Lists = new();
        /// <summary>How many references Admit added, by kind, over every call.</summary>
        public int AdmittedRing2, AdmittedLarge;

        internal FloorReach(Dictionary<FormKey, (Vec3 Min, Vec3 Max)> bounds) { _bounds = bounds; }

        internal void File((int, int) anchor, PlacedRef r)
        {
            if (!_byAnchor.TryGetValue(anchor, out var d)) _byAnchor[anchor] = d = new Dictionary<FormKey, PlacedRef>();
            d[r.FormKey] = r;
        }

        internal void Unfile((int, int) anchor, FormKey k)
        {
            if (_byAnchor.TryGetValue(anchor, out var d) && d.Remove(k) && d.Count == 0) _byAnchor.Remove(anchor);
        }

        /// <summary>The references filed at an anchor (for a reader and the self-test).</summary>
        public IReadOnlyCollection<PlacedRef> At((int X, int Y) anchor) =>
            _byAnchor.TryGetValue(anchor, out var d) ? d.Values : Array.Empty<PlacedRef>();

        /// <summary>The OBND box of a base, as the walk last saw it, or null.</summary>
        public (Vec3 Min, Vec3 Max)? BoundsOf(FormKey baseKey) => _bounds.TryGetValue(baseKey, out var b) ? b : null;

        /// <summary>Add to <paramref name="refs"/> (the 3x3 neighbourhood of <paramref name="g"/> by position) what the reach
        /// admits, in a stable order. Returns how many were added.</summary>
        public int Admit((int X, int Y) g, List<PlacedRef> refs)
        {
            var have = new HashSet<FormKey>();
            foreach (var r in refs) have.Add(r.FormKey);
            var listed = Lists.Selected(ReachChildUnion, RingReach.RootUsesUnion);
            var add = new List<(PlacedRef Ref, bool Large)>();
            for (int dx = -RingReach.LargeRadius; dx <= RingReach.LargeRadius; dx++)
                for (int dy = -RingReach.LargeRadius; dy <= RingReach.LargeRadius; dy++)
                {
                    var a = (g.X + dx, g.Y + dy);
                    int ring = RingReach.Ring(a, g);
                    bool large = ring > RingReach.EdgeRadius;
                    if (large ? !ReachLarge : !ReachRing2) continue;
                    if (!_byAnchor.TryGetValue(a, out var d)) continue;
                    foreach (var (k, r) in d)
                    {
                        if (have.Contains(k)) continue;
                        if (large && !listed.Contains(k)) continue;
                        if (r.Base is not { } bk || !_bounds.TryGetValue(bk, out var box)) continue;
                        if (!RingReach.EntersSquare(r, box, g)) continue;
                        add.Add((r, large)); have.Add(k);
                    }
                }
            foreach (var (r, large) in add.OrderBy(x => x.Ref.FormKey.ToString(), StringComparer.Ordinal))
            {
                refs.Add(r);
                if (large) AdmittedLarge++; else AdmittedRing2++;
            }
            return add.Count;
        }
    }

    /// <summary>The 3×3 neighbourhood around an exterior cell, out of maps a <see cref="FloorIndex"/> keeps.</summary>
    public static (List<PlacedRef> Refs, List<((int X, int Y) Grid, float[,] Heights)> Land) Neighbourhood(
        (int X, int Y) grid, Dictionary<(int, int), Dictionary<FormKey, PlacedRef>> refsByGrid, Dictionary<(int, int), float[,]> landByGrid)
    {
        var refs = new List<PlacedRef>(); var land = new List<((int X, int Y), float[,])>();
        for (int dx = -1; dx <= 1; dx++)
            for (int dy = -1; dy <= 1; dy++)
            {
                var k = (grid.X + dx, grid.Y + dy);
                if (refsByGrid.TryGetValue(k, out var d)) refs.AddRange(d.Values);
                if (landByGrid.TryGetValue(k, out var h)) land.Add((k, h));
            }
        return (refs, land);
    }

    /// <summary>Totals over every field a run built, for the summary line.</summary>
    public sealed class Tally
    {
        public long Reads, Found, Fallbacks, LandFloors; public int Fields, Candidates, LandCells;
        readonly int[] _hist = new int[129];
        public void Add(FloorField f)
        {
            Fields++; Reads += f.Reads; Found += f.Found; Fallbacks += f.Fallbacks; LandFloors += f.LandFloors; Candidates += f.Candidates; LandCells += f.LandCells;
            for (int b = 0; b < _hist.Length; b++) _hist[b] += f.OffsetHistogram[b];
        }
        public string Line(float fallbackOffset = DetectorModel.FallbackOffset) =>
            $"physical floor: {Reads:N0} raster reads over {Fields:N0} cell field(s) ({Candidates:N0} floor candidates, {LandCells:N0} land cells) — floor found {Found:N0} ({(Reads == 0 ? 0 : 100.0 * Found / Reads):0.0}%; on the terrain {LandFloors:N0}), " +
            $"no floor within reach {Fallbacks:N0} ({(Reads == 0 ? 0 : 100.0 * Fallbacks / Reads):0.0}%: the navmesh − {fallbackOffset:0.#} u, the station's offset, stands in); " +
            $"δ used (navmesh − floor) median {FloorField.Quantile(_hist, 0.5f):+0;-0}, p10 {FloorField.Quantile(_hist, 0.1f):+0;-0}, p90 {FloorField.Quantile(_hist, 0.9f):+0;-0} u";

        /// <summary>One cell's line under the tally.</summary>
        public static string CellLine(string cell, FloorField f) =>
            $"{cell,-36} candidates {f.Candidates,6:N0}  reads {f.Reads,8:N0}  found {(f.Reads == 0 ? 0 : 100.0 * f.Found / f.Reads),5:0.0}%  δ median {f.OffsetQuantile(0.5f),4:+0;-0} p10 {f.OffsetQuantile(0.1f),4:+0;-0} p90 {f.OffsetQuantile(0.9f),4:+0;-0} u";
    }

    /// <summary>The band line a probe prints once it stands on the physical floor.</summary>
    public static string BandLine(bool controllerBand) =>
        $"actor band above the FEET (Q24, 2026-09-05): bottom {ActorBand.PhysicalStep:0.#} u (the measured 48 u above the navmesh + the station's {ActorBand.StationOffset:0.#} u offset; the step band to {ActorBand.PhysicalStepBandTop:0.#} u is named in the verdict); " +
        $"top per cell from placed actors (race Height x NPC Height; placement scale counts for generated controllers, custom skeleton controllers ignore it){(controllerBand ? $" × {ActorBand.ControllerFactor} + {ActorBand.StationOffset:0.#} (the controller's, Part C)" : ", nominal")}; " +
        $"measured from the highest up-facing actor-solid surface (or the terrain) within {NavmeshFloor.DefaultMaxDepth:0} u below / {NavmeshFloor.DefaultUpReach:0} u above the navmesh under each raster cell";
}
