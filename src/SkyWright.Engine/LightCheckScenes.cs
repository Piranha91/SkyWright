using System.Numerics;
using HousecarlCore;

namespace SkyWright.Engine;

// THE LIGHTING CHECK's population (2026-11-26, Gate B): which plugins are on which side (read from the arms), which scenes both sides
// touch, and which references one arrangement reads for a scene. A scene is an interior cell, or an exterior cell (the centre) read with
// its ring of eight neighbours: its subjects are the centre's lights, fixtures and shapes; every reference whose bounds reach the
// three-by-three square is a surface or a context light; the terrain of the nine cells is a surface.

/// <summary>One placed reference as the population sees it: its interior cell or worldspace, where it stands in this arrangement, how
/// far its bounds reach, the plugins carrying a record of it, and whether its base is a light.</summary>
public sealed record LightCheckPlacement(string Key, string? Cell, string? Worldspace, Vector3 Pos, float Reach, IReadOnlyList<string> Touchers, bool IsLight);

/// <summary>A scene: an interior cell (<see cref="Space"/> its key), or an exterior centre cell of a worldspace at grid X, Y.</summary>
public sealed record LightCheckSceneKey(bool Exterior, string Space, int X = 0, int Y = 0)
{
    public string Id => Exterior ? $"{Space}@{X},{Y}" : Space;
}

public sealed class LightCheckSceneIndex
{
    /// <summary>World units per exterior cell.</summary>
    public const float Cell = 4096f;
    readonly Dictionary<string, List<LightCheckPlacement>> _interior = new(StringComparer.Ordinal);
    readonly Dictionary<(string Ws, int X, int Y), List<LightCheckPlacement>> _grid = new();
    readonly Dictionary<string, List<LightCheckPlacement>> _large = new(StringComparer.Ordinal);

    public LightCheckSceneIndex(IEnumerable<LightCheckPlacement> placements)
    {
        foreach (var p in placements)
        {
            if (p.Worldspace is null)
            {
                if (p.Cell is null) continue;
                (_interior.TryGetValue(p.Cell, out var l) ? l : _interior[p.Cell] = new()).Add(p);
                continue;
            }
            var g = (p.Worldspace, Grid(p.Pos.X), Grid(p.Pos.Y));
            (_grid.TryGetValue(g, out var gl) ? gl : _grid[g] = new()).Add(p);
            if (p.Reach >= Cell) (_large.TryGetValue(p.Worldspace, out var ll) ? ll : _large[p.Worldspace] = new()).Add(p);
        }
    }

    public static int Grid(float v) => (int)Math.Floor(v / Cell);

    /// <summary>The nine cells an exterior scene reads (its centre and the ring of eight around it).</summary>
    public static IEnumerable<(int X, int Y)> Ring(LightCheckSceneKey scene)
    {
        for (int dx = -1; dx <= 1; dx++)
            for (int dy = -1; dy <= 1; dy++) yield return (scene.X + dx, scene.Y + dy);
    }

    /// <summary>The cells whose terrain an exterior scene reads as a surface (none for an interior).</summary>
    public static IEnumerable<(int X, int Y)> TerrainGrids(LightCheckSceneKey scene) => scene.Exterior ? Ring(scene) : Enumerable.Empty<(int, int)>();

    /// <summary>Does a reference's bound sphere reach the scene's three-by-three square (XY)?</summary>
    public static bool ReachesSquare(LightCheckPlacement p, LightCheckSceneKey s)
    {
        double x0 = (s.X - 1) * (double)Cell, x1 = (s.X + 2) * (double)Cell, y0 = (s.Y - 1) * (double)Cell, y1 = (s.Y + 2) * (double)Cell;
        double dx = Math.Max(0, Math.Max(x0 - p.Pos.X, p.Pos.X - x1)), dy = Math.Max(0, Math.Max(y0 - p.Pos.Y, p.Pos.Y - y1));
        return dx * dx + dy * dy <= (double)p.Reach * p.Reach;
    }

    /// <summary>The references one arrangement reads for a scene and whether each is a subject. An interior scene is its own cell and
    /// nothing else; an exterior scene is every reference of the worldspace whose bounds reach the three-by-three square, the subjects
    /// being those standing in the centre cell.</summary>
    public IReadOnlyList<(LightCheckPlacement P, bool Subject)> Members(LightCheckSceneKey scene)
    {
        var o = new List<(LightCheckPlacement, bool)>();
        if (!scene.Exterior)
        {
            if (_interior.TryGetValue(scene.Space, out var l)) o.AddRange(l.Select(p => (p, true)));
            return o;
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int dx = -2; dx <= 2; dx++)
            for (int dy = -2; dy <= 2; dy++)
            {
                if (!_grid.TryGetValue((scene.Space, scene.X + dx, scene.Y + dy), out var g)) continue;
                bool inner = Math.Abs(dx) <= 1 && Math.Abs(dy) <= 1;
                foreach (var p in g)
                    if ((inner || ReachesSquare(p, scene)) && seen.Add(p.Key)) o.Add((p, dx == 0 && dy == 0));
            }
        if (_large.TryGetValue(scene.Space, out var big))
            foreach (var p in big)
                if (ReachesSquare(p, scene) && seen.Add(p.Key)) o.Add((p, Grid(p.Pos.X) == scene.X && Grid(p.Pos.Y) == scene.Y));
        return o;
    }

    /// <summary>The scenes both sides touch: an interior cell in which a plugin of each side carries a record of a reference, and which
    /// holds a light; an exterior centre cell whose three-by-three square holds a record of each side and a light. One side's scenes cannot
    /// make a row (every subject there reads the same in the combination as in that side alone) and are counted, not read.</summary>
    public IReadOnlyList<LightCheckSceneKey> TwoSide(LightCheckSides sides, out int oneSide, out int noLight)
    {
        oneSide = 0; noLight = 0;
        int Bits(IEnumerable<LightCheckPlacement> ps)
        {
            int b = 0;
            foreach (var p in ps)
                foreach (var t in p.Touchers)
                    b |= sides.Of(t) switch { LightSide.Selection => 1, LightSide.Rest => 2, _ => 0 };
            return b;
        }
        var o = new List<LightCheckSceneKey>();
        foreach (var (cell, ps) in _interior.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            int b = Bits(ps);
            if (b != 3) { if (b != 0) oneSide++; continue; }
            if (!ps.Any(p => p.IsLight)) { noLight++; continue; }
            o.Add(new LightCheckSceneKey(false, cell));
        }
        var gridBits = _grid.ToDictionary(kv => kv.Key, kv => (Bits: Bits(kv.Value), Light: kv.Value.Any(p => p.IsLight)));
        foreach (var g in _grid.Keys.OrderBy(k => k.Ws, StringComparer.Ordinal).ThenBy(k => k.X).ThenBy(k => k.Y))
        {
            int b = 0; bool light = false;
            foreach (var (x, y) in Ring(new LightCheckSceneKey(true, g.Ws, g.X, g.Y)))
                if (gridBits.TryGetValue((g.Ws, x, y), out var v)) { b |= v.Bits; light |= v.Light; }
            if (b != 3) { if (b != 0) oneSide++; continue; }
            if (!light) { noLight++; continue; }
            o.Add(new LightCheckSceneKey(true, g.Ws, g.X, g.Y));
        }
        return o;
    }

    /// <summary>One exterior cell's terrain as a surface: 32 by 32 quads at 128 u, two triangles each, facing up (front face upward).</summary>
    public static TriangleSoup TerrainSoup(int gx, int gy, float[,] heights)
    {
        int n = LandHeight.Side;
        var v = new Vector3[n * n];
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
                v[y * n + x] = new Vector3(gx * Cell + x * LandHeight.VertexSpacing, gy * Cell + y * LandHeight.VertexSpacing, heights[x, y]);
        var idx = new List<int>((n - 1) * (n - 1) * 6);
        for (int y = 0; y < n - 1; y++)
            for (int x = 0; x < n - 1; x++)
            {
                int a = y * n + x, b = a + 1, c = a + n + 1, d = a + n;
                idx.AddRange(new[] { a, b, c, a, c, d });
            }
        return new TriangleSoup(v, idx.ToArray());
    }

    /// <summary>The sides, read from the arms: the selection is what the selection arm adds to the dependencies, the rest what the rest arm
    /// adds; mods are named by the providers given. Null with a one-sentence refusal when the arms do not describe two sides of one order.</summary>
    public static LightCheckSides? Sides(IReadOnlyList<string> dependencies, IReadOnlyList<string> selection, IReadOnlyList<string> rest,
                                         IReadOnlyList<string> combined, IReadOnlyDictionary<string, string> modByPlugin, out string? refusal)
    {
        var cmp = StringComparer.OrdinalIgnoreCase;
        var d = new HashSet<string>(dependencies, cmp); var c = new HashSet<string>(combined, cmp);
        var sel = selection.Where(p => !d.Contains(p)).ToList();
        var rst = rest.Where(p => !d.Contains(p)).ToList();
        refusal = null;
        foreach (var (role, list) in new[] { ("dependencies", dependencies), ("selection", selection), ("rest", rest) })
            if (list.FirstOrDefault(p => !c.Contains(p)) is { } miss)
            { refusal = $"The {role} arrangement loads {miss}, which the combined arrangement does not; rebuild the four arrangements from one load order."; return null; }
        foreach (var (role, list) in new[] { ("selection", selection), ("rest", rest) })
            if (dependencies.FirstOrDefault(p => !list.Contains(p, cmp)) is { } miss)
            { refusal = $"The {role} arrangement leaves out {miss}, which the dependencies load; rebuild the four arrangements from one load order."; return null; }
        if (sel.FirstOrDefault(p => rst.Contains(p, cmp)) is { } both)
        { refusal = $"{both} is in both the selection and the rest arrangements; rebuild them so each plugin is on one side."; return null; }
        if (sel.Count == 0) { refusal = "The selection adds no plugin to the dependencies, so there is nothing to check; select at least one mod with a plugin."; return null; }
        if (rst.Count == 0) { refusal = "The rest of the load order adds no plugin to the dependencies, so nothing can clash with the selection; check the rest arrangement."; return null; }
        if (combined.FirstOrDefault(p => !d.Contains(p) && !sel.Contains(p, cmp) && !rst.Contains(p, cmp)) is { } stray)
        { refusal = $"The combined arrangement loads {stray}, which no other arrangement does; rebuild the four arrangements from one load order."; return null; }
        var side = new Dictionary<string, LightSide>(cmp);
        foreach (var p in d) side[p] = LightSide.Base;
        foreach (var p in sel) side[p] = LightSide.Selection;
        foreach (var p in rst) side[p] = LightSide.Rest;
        return new LightCheckSides(side, modByPlugin);
    }
}
