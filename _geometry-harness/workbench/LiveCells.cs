using ViewProbe;

namespace Workbench;

/// <summary>The cells the 3D view holds around its scene's cell, added and dropped live (viewprobe's LiveScene.cs): the
/// loaded square is the game's uGridsToLoad, and changing it builds only the cells that come in and frees only the cells
/// that go out. Nothing already drawn is built again.</summary>
internal sealed class LiveCells
{
    readonly InteractiveOptions _opt;
    readonly Dictionary<(int X, int Y), LiveCell> _cells = new();

    internal LiveCells(InteractiveOptions opt) { _opt = opt; Grids = opt.GridsToLoad; }

    /// <summary>The scene can add and drop cells: an exterior built with a prepared square.</summary>
    internal bool Available => _opt.BuildCell is not null && _opt.Grid is not null && _opt.Reach.Count > 0;
    /// <summary>The width of the loaded square, in cells (odd).</summary>
    internal int Grids { get; private set; }
    internal IReadOnlyCollection<(int X, int Y)> Held => _cells.Keys;
    internal bool Holds((int X, int Y) cell) => _cells.ContainsKey(cell);
    internal IEnumerable<LiveCell> All => _cells.Values;

    /// <summary>The widest square the prepared cells allow around the scene's cell.</summary>
    internal int MaxGrids => _opt.Grid is { } g ? MaxSquare(g, _opt.Reach) : 1;

    internal static int MaxSquare((int X, int Y) centre, IReadOnlySet<(int X, int Y)> reach)
    {
        int half = 0;
        while (Square(centre, 2 * (half + 1) + 1).All(reach.Contains)) half++;
        return 2 * half + 1;
    }

    /// <summary>The cells of a square <paramref name="grids"/> wide around <paramref name="centre"/>, nearest first.</summary>
    internal static List<(int X, int Y)> Square((int X, int Y) centre, int grids)
    {
        int half = grids / 2;
        var cells = new List<(int X, int Y)>();
        for (int dx = -half; dx <= half; dx++)
            for (int dy = -half; dy <= half; dy++) cells.Add((centre.X + dx, centre.Y + dy));
        return cells.OrderBy(c => Math.Max(Math.Abs(c.X - centre.X), Math.Abs(c.Y - centre.Y))).ThenBy(c => c.X).ThenBy(c => c.Y).ToList();
    }

    /// <summary>What a change of the loaded square does: the cells to build, nearest first, and the cells to free.</summary>
    internal static (List<(int X, int Y)> Add, List<(int X, int Y)> Drop) Plan((int X, int Y) centre, int grids,
        IReadOnlyCollection<(int X, int Y)> held, IReadOnlySet<(int X, int Y)> reach)
    {
        var want = Square(centre, grids).Where(reach.Contains).ToList();
        var wantSet = want.ToHashSet();
        return (want.Where(c => !held.Contains(c)).ToList(), held.Where(c => !wantSet.Contains(c)).OrderBy(c => c.X).ThenBy(c => c.Y).ToList());
    }

    /// <summary>The refusal for a square width, or null when it can be loaded.</summary>
    internal string? Refuse(int grids) =>
        !Available ? "this scene cannot load more cells: " + (_opt.Interior ? "an interior has no cell grid." : "it was built without the cells around it.")
        : grids < 1 || grids % 2 == 0 ? $"the loaded square is an odd number of cells wide (1, 3, 5, ...), not {grids}."
        : grids > MaxGrids ? $"this scene prepared the cells for a square up to {MaxGrids} wide; {grids} needs the cell opened again." : null;

    /// <summary>Build one cell into the scene and add what it drew to the scene's lists. Null when it cannot be built.</summary>
    internal LiveCell? Add((int X, int Y) cell)
    {
        if (_cells.ContainsKey(cell) || _opt.BuildCell?.Invoke(cell) is not { } lc) return null;
        _cells[cell] = lc;
        _opt.PickShapes.AddRange(lc.PickShapes);
        _opt.Occlusion?.Refs.AddRange(lc.Refs);
        foreach (var (layer, meshes) in lc.LayerMeshes)
            (_opt.LayerMeshes.TryGetValue(layer, out var l) ? l : _opt.LayerMeshes[layer] = new()).AddRange(meshes);
        if (lc.HasLand) _opt.LandCells = _opt.LandCells.Append(cell).Distinct().ToList();
        return lc;
    }

    /// <summary>Take a cell out of the scene's lists and free its meshes.</summary>
    internal LiveCell? Drop((int X, int Y) cell, SkyRender.GlRenderer renderer)
    {
        if (!_cells.Remove(cell, out var lc)) return null;
        var shapes = lc.PickShapes.ToHashSet();
        _opt.PickShapes.RemoveAll(shapes.Contains);
        var refs = lc.Refs.ToHashSet();
        _opt.Occlusion?.Refs.RemoveAll(refs.Contains);
        foreach (var (layer, meshes) in lc.LayerMeshes)
            if (_opt.LayerMeshes.TryGetValue(layer, out var l)) { var gone = meshes.ToHashSet(); l.RemoveAll(gone.Contains); }
        _opt.LandCells = _opt.LandCells.Where(c => c != cell).ToList();
        lc.Drop(renderer);
        return lc;
    }

    internal void SetGrids(int grids) => Grids = grids;
}
