using System.Text.Json;
using HousecarlCore;
using Mutagen.Bethesda.Plugins;

namespace HarnessShared;

// WINNING-STATE DUMP: what a probe's load-order walk ended up holding, written for an independent reader to compare.
//
// Silent unless asked: nothing is written or printed unless HOUSECARL_WINSTATE_DUMP names a file. Each call appends one
// JSON line: the site's name, the filter it applied (wanted cells, target cells, grids), and every entry the walk holds
// (FormKey, cell, position, deleted and persistent flags), plus, for a floor filing, each grid's references and LAND.
// It reads the walk's own structures and changes nothing.
public static class WinStateDump
{
    public const string EnvVar = "HOUSECARL_WINSTATE_DUMP";

    public static bool Asked => Environment.GetEnvironmentVariable(EnvVar) is { Length: > 0 };

    public static object Entry(PlacedRef r, FormKey? cell) => new
    {
        formKey = r.FormKey.ToString(), cell = cell?.ToString(), x = r.Position.X, y = r.Position.Y, z = r.Position.Z,
        deleted = r.Deleted, persistent = r.Persistent, @base = r.Base?.ToString(),
    };

    /// <summary>A floor filing (FloorFeed's per-grid maps) of one worldspace, as entries per grid and a LAND digest per grid
    /// (the height at vertex [16,16] and a sum over the map; enough to tell which plugin's LAND is held).</summary>
    public static object Floor(FormKey worldspace, Dictionary<(int, int), Dictionary<FormKey, PlacedRef>> refs,
                               Dictionary<(int, int), float[,]> land, IEnumerable<(int X, int Y)>? unreadableLand = null) => new
    {
        worldspace = worldspace.ToString(),
        grids = refs.OrderBy(g => g.Key).Select(g => new
        {
            x = g.Key.Item1, y = g.Key.Item2,
            refs = g.Value.Values.OrderBy(r => r.FormKey.ToString(), StringComparer.Ordinal).Select(r => Entry(r, null)).ToList(),
        }).ToList(),
        land = land.OrderBy(g => g.Key).Select(g => new { x = g.Key.Item1, y = g.Key.Item2, h1616 = g.Value[16, 16], sum = Sum(g.Value) }).ToList(),
        unreadableLand = unreadableLand?.OrderBy(g => g).Select(g => new { x = g.X, y = g.Y }).ToList(),
    };

    /// <summary>One navmesh a walk holds: its key, the cell and plugin of the version held (when the walk knows them), and a
    /// digest of that version's geometry (the first vertex's X, the vertex and triangle counts, and the sum of every
    /// vertex coordinate to 0.1): enough to tell which plugin's version is held.</summary>
    public static object Nav(NavmeshGeometry n, FormKey? cell, string? plugin) => new
    {
        formKey = n.FormKey.ToString(), cell = cell?.ToString(), plugin, x0 = n.Vertices.Count > 0 ? n.Vertices[0].X : (float?)null,
        nverts = n.Vertices.Count, ntris = n.Triangles.Count, vsum = Math.Round(n.Vertices.Sum(v => (double)v.X + v.Y + v.Z), 1),
    };

    static double Sum(float[,] h) { double s = 0; foreach (var v in h) s += v; return s; }

    /// <summary>Append one JSON line when asked; otherwise do nothing.</summary>
    public static void Write(string site, object payload)
    {
        if (Environment.GetEnvironmentVariable(EnvVar) is not { Length: > 0 } path) return;
        var line = JsonSerializer.Serialize(new { site, payload });
        File.AppendAllText(path, line + "\n");
    }
}
