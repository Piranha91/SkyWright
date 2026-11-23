using V3 = System.Numerics.Vector3;
using SkyRender;

namespace ViewProbe;

/// <summary>
/// The live scene: what an interactive host keeps between builds so that cells can be added to and
/// dropped from the scene it is showing, instead of the scene being thrown away and built again.
///
/// <para>The first build of an interactive scene makes the renderer, the texture manager, the asset
/// resolver with its decoded-pixel cache, and the mesh builder, exactly as a one-shot build does, and
/// hands them to the host here (<see cref="InteractiveOptions.Live"/>) just before the host's loop
/// starts. A later cell is built by the same <see cref="TexturedScene.RenderToPng"/> in slice mode: it
/// reads through these owners (so a texture or a mesh the scene already holds is not read again),
/// adds its meshes to the live renderer, and stops after the grass, before anything the scene draws
/// once for its target cell (lights, LOD, the navmesh, the overlays).</para>
///
/// <para>The owners belong to the first build's frame: they are disposed when the host's loop
/// returns, with the rest of that build. A slice built after that would draw into a dead renderer, so
/// a host never keeps a <see cref="LiveCell"/> past its own loop.</para>
/// </summary>
internal sealed class LiveSession
{
    internal required GlRenderer Renderer { get; init; }
    internal required GlTextureManager Textures { get; init; }
    internal required CellAssets Assets { get; init; }
    internal required MeshState Mesh { get; init; }

    // the 1x1 orange a highlighted reference is drawn with; one for the session, deleted with its context
    int _orange;
    internal int Orange(Func<int> create) => _orange != 0 ? _orange : _orange = create();
}

/// <summary>One exterior cell built into the live scene: everything it added, so it can be dropped again.
/// Its references are those placed in it (by position: a reference belongs to the cell its position falls in),
/// its terrain, its water and its grass.</summary>
internal sealed class LiveCell
{
    internal required (int X, int Y) Grid { get; init; }
    /// <summary>The meshes added to the live renderer: removed and disposed when the cell is dropped.</summary>
    internal List<GlMesh> Meshes { get; } = new();
    internal List<PickShape> PickShapes { get; init; } = new();
    internal List<RefOcclusion> Refs { get; init; } = new();
    /// <summary>"terrain", "water", "grass": this cell's meshes of each layer.</summary>
    internal Dictionary<string, List<GlMesh>> LayerMeshes { get; init; } = new();
    internal bool HasLand { get; init; }
    /// <summary>Only the cell's large references (the far grid beyond the loaded cells): no terrain, water or grass.</summary>
    internal bool LargeOnly { get; init; }
    internal double Seconds { get; set; }

    /// <summary>Take the cell out of the renderer and free its meshes. The textures stay in the session's cache.</summary>
    internal void Drop(GlRenderer renderer)
    {
        foreach (var m in Meshes) { renderer.RemoveMesh(m); m.Dispose(); }
        Meshes.Clear();
    }
}

/// <summary>The walk's prepared inputs split by cell, so any set of cells of the prepared square can be built on its own.
/// A reference belongs to the cell its position falls in; a terrain or water triangle to the cell its centre falls in;
/// a water plane to the cell its middle falls in.</summary>
internal sealed class LiveSlicer
{
    readonly Dictionary<(int X, int Y), List<SceneRef>> _refs = new();
    readonly Dictionary<(int X, int Y), Dictionary<string, List<(V3, V3, V3)>>> _patches = new();
    readonly Dictionary<(int X, int Y), List<(string Diffuse, List<(V3 A, V3 B, V3 C, float Oa, float Ob, float Oc)> Tris)>> _layers = new();
    readonly Dictionary<(int X, int Y), List<WaterPlane>> _water = new();
    readonly HashSet<Mutagen.Bethesda.Plugins.FormKey> _large;

    internal static (int X, int Y) CellOf(float x, float y) => ((int)MathF.Floor(x / 4096f), (int)MathF.Floor(y / 4096f));

    internal LiveSlicer(IReadOnlyList<SceneRef> refs,
        IReadOnlyDictionary<string, List<(V3, V3, V3)>> patches,
        IReadOnlyList<(string Diffuse, List<(V3 A, V3 B, V3 C, float Oa, float Ob, float Oc)> Tris)> layers,
        IReadOnlyList<WaterPlane> water, IReadOnlySet<Mutagen.Bethesda.Plugins.FormKey>? large = null)
    {
        _large = large is null ? new() : new(large);
        foreach (var r in refs) Bucket(_refs, CellOf(r.Position.X, r.Position.Y)).Add(r);
        foreach (var (diffuse, tris) in patches)
            foreach (var t in tris)
            {
                var c = CellOf((t.Item1.X + t.Item2.X + t.Item3.X) / 3f, (t.Item1.Y + t.Item2.Y + t.Item3.Y) / 3f);
                var byTex = _patches.TryGetValue(c, out var d) ? d : _patches[c] = new(StringComparer.OrdinalIgnoreCase);
                Bucket(byTex, diffuse).Add(t);
            }
        foreach (var (diffuse, tris) in layers)
        {
            var split = new Dictionary<(int X, int Y), List<(V3 A, V3 B, V3 C, float Oa, float Ob, float Oc)>>();
            foreach (var t in tris) Bucket(split, CellOf((t.A.X + t.B.X + t.C.X) / 3f, (t.A.Y + t.B.Y + t.C.Y) / 3f)).Add(t);
            foreach (var (c, part) in split) Bucket(_layers, c).Add((diffuse, part));   // the draw order of layers is kept per cell
        }
        foreach (var w in water) Bucket(_water, CellOf((w.X0 + w.X1) / 2f, (w.Y0 + w.Y1) / 2f)).Add(w);
    }

    static List<T> Bucket<K, T>(Dictionary<K, List<T>> d, K k) where K : notnull => d.TryGetValue(k, out var l) ? l : d[k] = new List<T>();

    /// <summary>A preloaded area's cells (LiveArea.cs) join: only <paramref name="cells"/>, the ones this slicer did not have.</summary>
    internal void Merge(LiveArea area, IReadOnlySet<(int X, int Y)> cells)
    {
        var other = new LiveSlicer(area.Refs, area.Patches, area.Layers, area.Water);
        foreach (var c in cells)
        {
            if (other._refs.TryGetValue(c, out var r)) _refs[c] = r;
            if (other._patches.TryGetValue(c, out var p)) _patches[c] = p;
            if (other._layers.TryGetValue(c, out var l)) _layers[c] = l;
            if (other._water.TryGetValue(c, out var w)) _water[c] = w;
        }
        _large.UnionWith(area.Large);
    }

    /// <summary>Forget the prepared inputs of <paramref name="cells"/> (a preloaded area's memory, once the camera has gone).</summary>
    internal void Forget(IEnumerable<(int X, int Y)> cells)
    {
        foreach (var c in cells) { _refs.Remove(c); _patches.Remove(c); _layers.Remove(c); _water.Remove(c); }
    }

    /// <summary>Which square each walked cell is: where its references stand. A cell is named only when at least 80% of them
    /// stand in one square, which leaves out the worldspace's persistent cell (its references are everywhere) and a cell
    /// placing nothing.</summary>
    internal static Dictionary<Mutagen.Bethesda.Plugins.FormKey, (int X, int Y)> CellGridsOf(
        IReadOnlyDictionary<Mutagen.Bethesda.Plugins.FormKey, Mutagen.Bethesda.Plugins.FormKey> refCells,
        IReadOnlyDictionary<Mutagen.Bethesda.Plugins.FormKey, HousecarlCore.PlacedRef> placed) =>
        refCells.Where(kv => placed.ContainsKey(kv.Key))
            .GroupBy(kv => kv.Value, kv => CellOf(placed[kv.Key].Position.X, placed[kv.Key].Position.Y))
            .Select(g => (Cell: g.Key, Total: g.Count(), Top: g.GroupBy(c => c).OrderByDescending(c => c.Count()).First()))
            .Where(x => x.Top.Count() >= 0.8 * x.Total)
            .ToDictionary(x => x.Cell, x => x.Top.Key);

    /// <summary>The large references placed in <paramref name="cell"/> (the far grid's share of it).</summary>
    internal List<SceneRef> LargeOf((int X, int Y) cell) =>
        _refs.TryGetValue(cell, out var r) ? r.Where(x => _large.Contains(x.RefKey)).ToList() : new List<SceneRef>();

    /// <summary>The inputs of <paramref name="cells"/>, in the shapes <see cref="TexturedScene.RenderToPng"/> takes.</summary>
    internal (List<SceneRef> Refs, Dictionary<string, List<(V3, V3, V3)>> Patches,
              List<(string Diffuse, List<(V3 A, V3 B, V3 C, float Oa, float Ob, float Oc)> Tris)> Layers, List<WaterPlane> Water)
        For(IReadOnlyCollection<(int X, int Y)> cells)
    {
        var refs = new List<SceneRef>(); var patches = new Dictionary<string, List<(V3, V3, V3)>>(StringComparer.OrdinalIgnoreCase);
        var layers = new List<(string Diffuse, List<(V3 A, V3 B, V3 C, float Oa, float Ob, float Oc)> Tris)>(); var water = new List<WaterPlane>();
        foreach (var c in cells)
        {
            if (_refs.TryGetValue(c, out var r)) refs.AddRange(r);
            if (_patches.TryGetValue(c, out var p)) foreach (var (tex, tris) in p) Bucket(patches, tex).AddRange(tris);
            if (_layers.TryGetValue(c, out var l)) layers.AddRange(l);
            if (_water.TryGetValue(c, out var w)) water.AddRange(w);
        }
        return (refs, patches, layers, water);
    }
}
