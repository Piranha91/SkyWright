using System;
using System.Collections.Generic;
using System.Linq;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using SkyRender.World;

namespace ViewProbe;

/// <summary>
/// --lod (opt-in, like --ring visible): the worldspace's distant LOD -- terrain (.btr), objects (.bto),
/// trees (.btt) and LOD water (the .btr WATER quads) -- drawn beyond the loaded cells, through SkyRender.World's DistantLodLoader and
/// GlWorldFactory.CreateDistantLod. viewprobe's job is local clashes, so its default frame and load
/// time do not change: without --lod nothing here runs.
///
/// Which LOD files: the target worldspace's EditorID names them, or its parent's when the WRLD uses its
/// parent's LOD data (PNAM flag "Use LOD Data"); the walk notes every WRLD it sees (the last plugin
/// wins). The files resolve through the same load-order VFS and archives as every other asset. The
/// loaded cells (what LOD is cut away under) are the cells whose terrain the frame draws, plus the
/// loaded set the walk chose (--ring). Blocks and distances follow the engine's [TerrainManager]
/// settings (SkyRender's LodDistances defaults; the lab profile's SkyrimPrefs.ini carries the same
/// values), out to <see cref="FarPlane"/>, which also becomes the camera's far plane.
/// LOD water takes the worldspace's LOD water type (WRLD NAM3, the parent's when the WRLD uses its parent's
/// LOD data) and is not drawn when the WRLD has the "No LOD Water" flag, the WATR is missing, or --no-water.
/// VP_LOD_KINDS=terrain,objects,trees,water picks kinds (diagnostic).
/// </summary>
internal static class DistantLodLayer
{
    /// <summary>--lod.</summary>
    internal static bool Requested;
    /// <summary>The camera's far plane and the LOD reach with --lod: about 100 cells, the render
    /// distance the DynDOLOD documentation gives for everything in the game.</summary>
    internal const float FarPlane = 409600f;

    /// <summary>The target worldspace (set once the target cell is found).</summary>
    internal static FormKey Target = FormKey.Null;
    /// <summary>The loaded exterior cells the walk chose (--ring), if any.</summary>
    internal static IReadOnlyCollection<(int X, int Y)>? WalkCells;

    private static readonly Dictionary<FormKey, (string? EditorId, FormKey Parent, bool UsesParentLod, FormKey LodWater, bool NoLodWater)> Seen = new();

    /// <summary>The LOD water type as a water plane's material fields (footprint unused), set by
    /// Program.cs once the walk has seen the WATR records; null when LOD water is not drawn.</summary>
    internal static WaterPlane? LodWater;
    /// <summary>Why <see cref="LodWater"/> is null, for the console.</summary>
    internal static string LodWaterWhy = "";

    /// <summary>The walk's sighting of a WRLD record (later plugins replace earlier ones).</summary>
    internal static void Note(IWorldspaceGetter w)
    {
        if (!Requested) return;
        var parent = w.Parent;
        bool useParent = parent is not null && parent.Flags.HasFlag(WorldspaceParent.Flag.UseLodData)
                         && !parent.Worldspace.IsNull;
        Seen[w.FormKey] = (w.EditorID, useParent ? parent!.Worldspace.FormKey : FormKey.Null, useParent,
                           w.LodWater.FormKeyNullable ?? FormKey.Null, w.Flags.HasFlag(Worldspace.Flag.NoLodWater));
    }

    /// <summary>The target's LOD water type (NAM3), following "Use LOD Data" to the parent like the LOD
    /// files do; null with the reason when there is none or the target's "No LOD Water" flag is set.</summary>
    internal static FormKey? LodWaterKey(out string why)
    {
        why = "";
        var key = Target;
        if (key.IsNull || !Seen.TryGetValue(key, out var t)) { why = "no target worldspace"; return null; }
        if (t.NoLodWater) { why = "the worldspace has the No LOD Water flag"; return null; }
        for (int hops = 0; hops < 4; hops++)
        {
            if (!Seen.TryGetValue(key, out var w)) { why = $"the walk did not see WRLD {key}"; return null; }
            if (w.UsesParentLod) { key = w.Parent; continue; }
            if (w.LodWater.IsNull) { why = $"WRLD {key} names no LOD water type (NAM3)"; return null; }
            return w.LodWater;
        }
        why = "parent LOD chain too deep";
        return null;
    }

    /// <summary>The LOD file name for the target, or null with the reason.</summary>
    internal static string? LodWorldspace(out string why)
    {
        why = "";
        var key = Target;
        for (int hops = 0; hops < 4; hops++)
        {
            if (key.IsNull) { why = "no target worldspace (interior?)"; return null; }
            if (!Seen.TryGetValue(key, out var w))
            { why = $"the walk did not see WRLD {key} (a retained walk skips records; run without the reuse options)"; return null; }
            if (w.UsesParentLod) { key = w.Parent; continue; }
            if (string.IsNullOrEmpty(w.EditorId)) { why = $"WRLD {key} has no EditorID"; return null; }
            why = key == Target ? "" : $" (the parent's LOD: the target uses its parent's LOD data)";
            return w.EditorId;
        }
        why = "parent LOD chain too deep";
        return null;
    }

    internal static LodKinds Kinds()
    {
        var env = Environment.GetEnvironmentVariable("VP_LOD_KINDS");
        if (string.IsNullOrWhiteSpace(env)) return LodKinds.All;
        var k = LodKinds.None;
        foreach (var part in env.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            k |= part.ToLowerInvariant() switch
            {
                "terrain" => LodKinds.Terrain,
                "objects" => LodKinds.Objects,
                "trees" => LodKinds.Trees,
                "water" => LodKinds.Water,
                _ => LodKinds.None,
            };
        return k;
    }

    /// <summary>One console line per kind: blocks picked / found, meshes, triangles, what was cut.</summary>
    internal static IEnumerable<string> Describe(DistantLodData d)
    {
        foreach (var kind in new[] { LodKind.Terrain, LodKind.Water, LodKind.Objects, LodKind.Trees })
        {
            if (!d.BlocksSelected.TryGetValue(kind, out int sel)) continue;
            var parts = d.Parts.Where(p => p.Kind == kind).ToList();
            var levels = string.Join(" ", parts.GroupBy(p => p.Block.Level).OrderBy(g => g.Key).Select(g => $"L{g.Key}:{g.Count()}"));
            string cut = kind switch
            {
                LodKind.Terrain => $"{parts.Sum(p => p.Cut):N0} triangle(s) under the loaded cells dropped, {parts.Sum(p => p.Sunk):N0} seam vertex(es) lowered",
                LodKind.Objects => $"{parts.Sum(p => p.Cut):N0} triangle(s) of loaded cells' objects dropped"
                                   + (parts.Count(p => p.HasVertexColors) is > 0 and var vc ? $"; {vc} with vertex colours" : ""),
                LodKind.Water => $"{parts.Sum(p => p.Cut):N0} triangle(s) of loaded cells' water dropped; "
                                 + (LodWater is { } lw ? $"LOD water type {lw.Label}" : $"not drawn - {LodWaterWhy}"),
                _ => $"{parts.Sum(p => p.Cut):N0} tree(s) in loaded cells dropped",
            };
            string onDisk = kind == LodKind.Water ? "with water quads" : "on disk";
            yield return $"   lod {kind.ToString().ToLowerInvariant()}: {sel} block(s) picked, {d.BlocksFound.GetValueOrDefault(kind)} {onDisk}, "
                       + $"{parts.Count} mesh(es) [{levels}], {parts.Sum(p => p.Data.Indices.Length / 3):N0} triangles; {cut}";
        }
    }
}
