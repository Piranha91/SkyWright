using System;
using System.Collections.Generic;
using System.Numerics;
using HarnessShared;
using HousecarlCore;
using Mutagen.Bethesda.Plugins;

// ======================================================================
//  FloorFeed — one cell's FloorField, built the way every probe builds it.
//
//  The floor rule is core's (HousecarlCore.NavmeshFloor; the field that
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
// ======================================================================

internal static class FloorFeed
{
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

    /// <summary>The exterior grid cell a position falls in.</summary>
    public static (int X, int Y) GridOf(Vec3 p) => ((int)MathF.Floor(p.X / 4096f), (int)MathF.Floor(p.Y / 4096f));

    /// <summary>File a cell's refs by the grid cell their POSITION falls in, into a per-worldspace map a neighbourhood
    /// is later read from. A later plugin's ref of the same FormKey replaces an earlier one (call in load order).</summary>
    public static void FileByGrid(CellPlacements cp, Dictionary<(int, int), Dictionary<FormKey, PlacedRef>> refsByGrid,
                                  Dictionary<(int, int), float[,]> landByGrid)
    {
        if (cp.Interior) return;
        if (cp.Land is { } land && cp.Grid is { } g) landByGrid[g] = land;
        foreach (var r in cp.Refs)
        {
            var key = GridOf(r.Position);
            if (!refsByGrid.TryGetValue(key, out var d)) refsByGrid[key] = d = new Dictionary<FormKey, PlacedRef>();
            // A later plugin that deletes the ref, or leaves it without a base, takes it out of the floor; one that moves
            // it re-files it. The ENABLE STATE is NOT decided here (Q27): a ref whose own flag is set may still be there
            // through an enable parent, and dropping it during ingest put it beyond the resolver's reach for good — the
            // filtering happens once, in Build, against the run's population. The winning record is what stays in the map,
            // so the flag population reads the same answer here as it always did.
            if (r.Deleted || r.Base is null) { d.Remove(r.FormKey); continue; }
            d[r.FormKey] = r;
        }
    }

    /// <summary>The 3×3 neighbourhood around an exterior cell, out of maps <see cref="FileByGrid"/> filled.</summary>
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
