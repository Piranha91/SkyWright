using System.Numerics;
using HarnessShared;
using HousecarlCore;
using SkyWright.Engine;
using Mutagen.Bethesda.Plugins;

namespace LeftBehind;

// ONE ARM — the winning state of one load order, as the left-behind rule reads it.
//
// The base arm and the city arm are two instances, each fed its own order in load order. Each is the order's WINNER
// and nothing else, so no "arm without a mover" reconstruction is needed and a rotation-only or scale-only override
// is honoured (the pose is OverlapIndex.WinningGeometryPose, not the de-duplicated placement history).
//
// What an arm answers:
//   - a reference's state: winning base, deletion, pose, and its LOAD-TIME enable state through the XESP chain
//     (EnableArms.Winner -> SkyWright.Engine.EnableState, opposite-state honoured; an unresolved chain is Unknown);
//   - whether a base can back a panel (an occluder type): not marker-flagged, Physical by ClassifyDependent (so not a
//     trigger or FX), and one of the backing base types;
//   - its geometry in the world (through a caller-supplied per-base provider), and the occluders near a box;
//   - the terrain height at a point from the arm's winning LAND.
// Everything is read after the arm has been fed; the caches assume the records no longer change.

public sealed class LeftBehindArm
{
    /// <summary>The base record types that can back a panel. Trees, lights, flora and items are not backing.</summary>
    public static readonly string[] BackingBaseTypes = { "Static", "MoveableStatic", "Door", "Activator", "Furniture", "Container" };
    /// <summary>A backing reference's world box has at least two extents of this size (architecture, not clutter).</summary>
    public const float TwoExtentMinU = 64f;
    /// <summary>Cell size of the per-reference triangle grid. Coarse enough that a large rock's triangles are indexed
    /// (the grid skips a triangle spanning more than 32,768 cells), fine enough for a 128 u reach.</summary>
    public const float GridCell = 64f;

    public string Name { get; }
    public OverlapIndex Index { get; } = new();
    public EnableArms Enable { get; }
    /// <summary>Plugin file name per load-order index, as this arm was fed.</summary>
    public List<string> Plugins { get; } = new();
    /// <summary>Worldspace editor id by key.</summary>
    public Dictionary<FormKey, string> WorldspaceName { get; } = new();
    /// <summary>Winning LAND heights per (worldspace, grid), world units, [x, y] over 33 x 33 vertices. A null value is a
    /// LAND record that could not be decoded.</summary>
    public Dictionary<(FormKey Ws, int X, int Y), float[,]?> Land { get; } = new();
    /// <summary>The per-base geometry provider for THIS arm's asset view. Null answer: no readable geometry.</summary>
    public Func<FormKey, ArmGeometry?> Geometry { get; set; } = _ => null;

    readonly Dictionary<FormKey, ArmGeometry?> _geom = new();
    readonly Dictionary<FormKey, Placed?> _placed = new();

    /// <summary>How many placed triangles the world-geometry cache may hold before it is emptied. Placed geometry is
    /// rebuilt on demand, so the budget trades time for memory and never changes an answer.</summary>
    public long TriangleBudget { get; set; } = 6_000_000;
    public long CachedTriangles { get; private set; }
    public int CacheClears { get; private set; }

    /// <summary>Empty the placed-geometry cache (answers are unchanged; geometry is rebuilt on demand).</summary>
    public void ClearPlaced() { _placed.Clear(); _geom.Clear(); CachedTriangles = 0; CacheClears++; }
    readonly Dictionary<FormKey, (BoxHash<FormKey> Solid, List<(FormKey Key, Vector3 Origin)> Blind)> _byWs = new();

    sealed record Placed(Aabb World, TriangleGrid Surface, MeshColumnIndex? Columns, char Src);

    public LeftBehindArm(string name) { Name = name; Enable = new EnableArms(Index); }

    /// <summary>A base's geometry in this arm. Kept in a cache that <see cref="ClearPlaced"/> empties; the source id,
    /// label and model box are kept for the whole run (they are small), so dropping the soups changes no answer.</summary>
    public ArmGeometry? GeometryOf(FormKey baseKey)
    {
        if (_geom.TryGetValue(baseKey, out var g)) return g;
        // A base with no model is never drawn (a trigger activator, a marker volume): it has no geometry, whatever
        // bounds it carries, and so it can neither back a panel nor hide a loss.
        g = HasModel(baseKey) ? Geometry(baseKey) : null;
        _source[baseKey] = g?.SourceId;
        _src[baseKey] = g?.Src ?? '-';
        return _geom[baseKey] = g;
    }
    readonly Dictionary<FormKey, string?> _source = new();

    /// <summary>The base names a model file (it is drawn). Without one there is nothing to back a panel with.</summary>
    public bool HasModel(FormKey baseKey) => Index.BaseMesh.ContainsKey(baseKey);
    readonly Dictionary<FormKey, char> _src = new();
    /// <summary>Per base looked up in this arm, the geometry source letter (m, r, c, o, or '-' for none).</summary>
    public IReadOnlyDictionary<FormKey, char> SourceLetters => _src;

    /// <summary>The reference's state in this arm, or null when the order never places it.</summary>
    public RefState? StateOf(FormKey key)
    {
        if (Index.History.Winning(key) is not { } win) return null;
        var pose = Index.WinningGeometryPose(key) ?? win;
        bool deleted = win.Deleted || Index.IsDeletedNow(key);
        var baseKey = Index.RefBase.TryGetValue(key, out var b) ? b : FormKey.Null;
        var en = deleted ? new EnableResolution(false, false, true) : Enable.Winner(key);
        return new RefState(key, baseKey, deleted, en, win.Disabled, pose.Pos, pose.Rot, pose.Scale);
    }

    /// <summary>Can a reference of this base back a panel.</summary>
    public bool IsOccluderBase(FormKey baseKey)
    {
        if (baseKey.IsNull || Index.BaseIsMarker.Contains(baseKey)) return false;
        if (!Index.BaseInfo.TryGetValue(baseKey, out var bi)) return false;
        if (Array.IndexOf(BackingBaseTypes, bi.Type) < 0) return false;
        return DetectorModel.ClassifyDependent("PlacedObject", bi.Type, bi.Edid, false) == DependentClass.Physical;
    }

    /// <summary>The geometry label the output contract uses for a base in this arm.</summary>
    public string GeometryLabel(FormKey baseKey) => (_src.TryGetValue(baseKey, out var c) ? c : GeometryOf(baseKey)?.Src ?? '-') switch
    {
        '-' => "none",
        'o' => "obnd",
        'c' => "collision",
        _ => "mesh",
    };

    /// <summary>The model-space box of a base's geometry (surface and closed solid together), cached.</summary>
    Aabb ModelBox(FormKey baseKey)
    {
        if (_modelBox.TryGetValue(baseKey, out var b)) return b;
        b = Aabb.Empty;
        if (GeometryOf(baseKey) is { } g)
        {
            b = SpatialOverlap.SoupBounds(g.Surface);
            if (g.ClosedSolid is { TriangleCount: > 0 } cs) b = b.Union(SpatialOverlap.SoupBounds(cs));
        }
        return _modelBox[baseKey] = b;
    }
    readonly Dictionary<FormKey, Aabb> _modelBox = new();

    /// <summary>The reference's world box from its base's model box and its pose (cheap: no triangle is placed).</summary>
    Aabb WorldBoxOf(RefState s)
    {
        var mb = ModelBox(s.Base);
        return mb.IsEmpty ? mb : SpatialOverlap.ToWorld(mb, s.Position, s.Rotation, s.Scale);
    }

    /// <summary>The reference's geometry placed in the world, built on first use and kept.</summary>
    Placed? PlacedOf(FormKey key, RefState s)
    {
        if (_placed.TryGetValue(key, out var p)) return p;
        p = null;
        if (GeometryOf(s.Base) is { } g && g.Surface.TriangleCount > 0)
        {
            var world = SpatialOverlap.TransformSoup(g.Surface, s.Position, s.Rotation, s.Scale);
            MeshColumnIndex? cols = g.ClosedSolid is { TriangleCount: > 0 } cs ? FloorFeed.Columns(cs, s.Position, s.Rotation, s.Scale) : null;
            if (CachedTriangles + world.TriangleCount > TriangleBudget) ClearPlaced();
            CachedTriangles += world.TriangleCount;
            p = new Placed(WorldBoxOf(s), TriangleGrid.Build(world, GridCell), cols, g.Src);
        }
        return _placed[key] = p;
    }

    static int ExtentsAtLeast(Aabb b, float min)
        => (b.Max.X - b.Min.X >= min ? 1 : 0) + (b.Max.Y - b.Min.Y >= min ? 1 : 0) + (b.Max.Z - b.Min.Z >= min ? 1 : 0);

    (BoxHash<FormKey> Solid, List<(FormKey Key, Vector3 Origin)> Blind) SpaceIndex(FormKey ws)
    {
        if (_byWs.TryGetValue(ws, out var idx)) return idx;
        var hash = new BoxHash<FormKey>(512f);
        var blind = new List<(FormKey, Vector3)>();
        foreach (var (key, (_, w)) in Index.RefCell)
        {
            if (w != ws) continue;
            if (StateOf(key) is not { } s || s.Deleted || !IsOccluderBase(s.Base)) continue;
            var world = WorldBoxOf(s);
            if (world.IsEmpty)
            {
                // A drawn base whose geometry cannot be read: remembered whatever its enable state, because a changed
                // reference of that kind hides a loss. A base with no model is not drawn and is not blind.
                if (HasModel(s.Base)) blind.Add((key, new Vector3(s.Position.X, s.Position.Y, s.Position.Z)));
                continue;
            }
            if (!s.Present && !s.Unknown) continue;
            if (ExtentsAtLeast(world, TwoExtentMinU) < 2) continue;
            hash.Add(ws, world, key);
        }
        return _byWs[ws] = (hash, blind);
    }

    /// <summary>Every occluder-type reference, present or Unknown in this arm, whose world geometry comes within
    /// <paramref name="reach"/> of <paramref name="box"/> in worldspace <paramref name="ws"/>.</summary>
    public IEnumerable<FormKey> OccludersNear(FormKey ws, Aabb box, float reach)
        => SpaceIndex(ws).Solid.Near(ws, box, reach).Select(x => x.Item).OrderBy(k => k.ToString(), StringComparer.Ordinal);

    /// <summary>Occluder-type references with no readable geometry whose origin lies within <paramref name="radius"/>
    /// of <paramref name="box"/>, in any enable state.</summary>
    public IEnumerable<FormKey> BlindNear(FormKey ws, Aabb box, float radius)
    {
        foreach (var (k, o) in SpaceIndex(ws).Blind)
            if (SpatialOverlap.BoxDistance(box, Aabb.Empty.Add(o)) <= radius) yield return k;
    }

    /// <summary>Distance from <paramref name="p"/> to the reference's surface within <paramref name="reach"/> (0 when
    /// inside its closed solid); null when farther or when the reference has no readable geometry.</summary>
    public float? DistanceTo(FormKey key, Vector3 p, float reach)
    {
        if (StateOf(key) is not { } s) return null;
        // Cheap first: a sample farther than the reach from the reference's world box needs no triangle placed.
        var wb = WorldBoxOf(s);
        if (wb.IsEmpty || SpatialOverlap.BoxDistance(Aabb.Empty.Add(p), wb) > reach) return null;
        if (PlacedOf(key, s) is not { } pl) return null;
        if (pl.Columns is { } cols && Inside(cols, pl.World, p)) return 0f;
        var (d, _, t) = pl.Surface.NearestPoint(p, reach);
        return t < 0 || !(d <= reach) ? null : d;
    }

    [ThreadStatic] static List<(float Z, bool Up)>? _hits;

    /// <summary>Inside a closed solid: the vertical line through p crosses the surface an odd number of times above p.</summary>
    static bool Inside(MeshColumnIndex cols, Aabb world, Vector3 p)
    {
        if (p.X < world.Min.X || p.X > world.Max.X || p.Y < world.Min.Y || p.Y > world.Max.Y || p.Z < world.Min.Z || p.Z > world.Max.Z)
            return false;
        var hits = _hits ??= new List<(float, bool)>();
        hits.Clear();
        cols.SurfacesAt(p.X, p.Y, hits);
        // A line through a shared edge (a quad's diagonal) meets both triangles at the same height: one crossing, not two.
        var zs = new List<float>();
        foreach (var (z, _) in hits) if (z > p.Z) zs.Add(z);
        zs.Sort();
        int above = 0; float last = float.NegativeInfinity;
        foreach (var z in zs) { if (z - last > 1e-3f) above++; last = z; }
        return (above & 1) == 1;
    }

    /// <summary>Terrain height under (x, y) in worldspace <paramref name="ws"/>, null where the arm has no decoded LAND.
    /// Bilinear between the 33 x 33 vertices (exact at the vertices; the engine's triangle split is not modelled).</summary>
    public float? TerrainZ(FormKey ws, float x, float y)
    {
        int gx = (int)MathF.Floor(x / 4096f), gy = (int)MathF.Floor(y / 4096f);
        if (!Land.TryGetValue((ws, gx, gy), out var h) || h is null) return null;
        float fx = (x - gx * 4096f) / LandHeight.VertexSpacing, fy = (y - gy * 4096f) / LandHeight.VertexSpacing;
        int ix = Math.Clamp((int)MathF.Floor(fx), 0, 31), iy = Math.Clamp((int)MathF.Floor(fy), 0, 31);
        float tx = Math.Clamp(fx - ix, 0f, 1f), ty = Math.Clamp(fy - iy, 0f, 1f);
        float a = h[ix, iy] * (1 - tx) + h[ix + 1, iy] * tx;
        float b = h[ix, iy + 1] * (1 - tx) + h[ix + 1, iy + 1] * tx;
        return a * (1 - ty) + b * ty;
    }

    /// <summary>Every panel: a placed reference carrying an XPRM primitive that <paramref name="panelPlugin"/> defines
    /// or overrides, as this arm's winner places it.</summary>
    public List<Panel> PanelsOf(string panelPlugin)
    {
        int pi = Plugins.FindIndex(n => string.Equals(n, panelPlugin, StringComparison.OrdinalIgnoreCase));
        var list = new List<Panel>();
        if (pi < 0) return list;
        foreach (var (key, prim) in Index.RefPrimitive)
        {
            if (!Index.Touchers.TryGetValue(key, out var t) || !t.Contains(pi)) continue;
            if (PanelOf(key) is { } p) list.Add(p);
        }
        return list.OrderBy(p => p.Key.ToString(), StringComparer.Ordinal).ToList();
    }

    /// <summary>One panel as this arm's winner places it; null when the arm does not place it with a primitive.</summary>
    public Panel? PanelOf(FormKey key)
    {
        if (!Index.RefPrimitive.TryGetValue(key, out var prim) || StateOf(key) is not { } s) return null;
        var (cell, ws) = Index.RefCell[key];
        var space = ws.IsNull ? cell : ws;
        string name = ws.IsNull ? $"interior:{cell}" : WorldspaceName.TryGetValue(ws, out var n) ? n : ws.ToString();
        return new Panel(key, space, name, prim.Type, new Vector3(s.Position.X, s.Position.Y, s.Position.Z),
                         new Vector3(s.Rotation.X, s.Rotation.Y, s.Rotation.Z),
                         new Vector3(prim.Bounds.X, prim.Bounds.Y, prim.Bounds.Z) * s.Scale);
    }

    /// <summary>The geometry source identity of a base in this arm (to tell a replaced mesh from an unchanged one).</summary>
    public string? SourceOf(FormKey baseKey) => _source.TryGetValue(baseKey, out var s) ? s : GeometryOf(baseKey)?.SourceId;

    /// <summary>The LAND grids a box's footprint covers.</summary>
    public static IEnumerable<(int X, int Y)> GridsUnder(Aabb box)
    {
        for (int x = (int)MathF.Floor(box.Min.X / 4096f); x <= (int)MathF.Floor(box.Max.X / 4096f); x++)
            for (int y = (int)MathF.Floor(box.Min.Y / 4096f); y <= (int)MathF.Floor(box.Max.Y / 4096f); y++)
                yield return (x, y);
    }
}
