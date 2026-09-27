using Mutagen.Bethesda.Skyrim;

namespace ViewProbe;

// PLUGINS AND RECORDS THAT CANNOT BE READ ARE REFUSED, NEVER SKIPPED.
//
// Every walk over the load order used to open each plugin inside `try { ... } catch { continue; }`. A plugin that failed
// to open then contributed nothing, so an EARLIER plugin's version of every record it overrides stayed in the winning
// state -- drawn and reported with no warning, the same stale-version outcome as the cell-move defect, by another route.
// A plugin that opened but failed while its records were read crashed the process with no name. Now:
//   - a plugin that will not open, or fails while one walk reads it, raises PluginUnreadable naming the plugin, its
//     position in the order and the walk; the run exits 2 with that message;
//   - a LAND whose heightmap cannot be read raises TerrainUnreadable naming the plugin and the grid square (keeping the
//     earlier plugin's heights would be exactly the defect);
//   - a LAND texture layer that cannot be read is NOT refused (it changes paint, not ground or content): it is counted
//     and named on the console and in the report (TerrainLayerFailures).
// --plugin-read-selftest holds each of these to a red and a green case.

/// <summary>A read the render refuses to go on without. The message is the whole explanation.</summary>
internal class ReadRefusal : Exception
{
    internal ReadRefusal(string message, Exception? inner) : base(message, inner) { }
}

internal sealed class PluginUnreadable : ReadRefusal
{
    internal PluginUnreadable(string path, int index, int count, string walk, Exception inner)
        : base((index >= 0 ? $"plugin {index + 1} of {count}, {Path.GetFileName(path)}," : Path.GetFileName(path))
             + $" could not be read by {walk}: "
             + $"{inner.GetType().Name}: {inner.Message}. Refusing: without it, an earlier plugin's version of every "
             + $"record it overrides would be used silently.", inner) { }
}

internal sealed class TerrainUnreadable : ReadRefusal
{
    internal TerrainUnreadable(string plugin, int gx, int gy, Exception inner)
        : base($"the LAND of {plugin} for grid square {gx},{gy} could not be read: {inner.GetType().Name}: {inner.Message}. "
             + $"Refusing: an earlier plugin's heights would be drawn in its place.", inner) { }
}

internal static class PluginRead
{
    /// <summary>Open one plugin of the order, or throw PluginUnreadable.</summary>
    internal static ISkyrimModGetter Open(string path, int index, int count, string walk)
    {
        try { return SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE); }
        catch (Exception ex) { throw new PluginUnreadable(path, index, count, walk, ex); }
    }

    /// <summary>For a catch filter around one plugin's walk: an exception that is not already a refusal becomes one that
    /// names the plugin. The inner exception's type and message are kept in the text, so a defect in the walk itself is
    /// still visible as what it is.</summary>
    internal static bool Wraps(Exception ex) => ex is not ReadRefusal;

    internal static PluginUnreadable Failed(string path, int index, int count, string walk, Exception ex) =>
        new(path, index, count, walk, ex);
}

/// <summary>Terrain texture layers that could not be read, per shot: counted, named on the console as they happen, and
/// written into the planes report as <c>terrainLayersUnreadable</c> (null when the terrain walk ran and found none).</summary>
internal static class TerrainLayerFailures
{
    static readonly List<(string Plugin, int X, int Y, int LayersRead, string Error, string Walk)> _shot = new();

    internal static void Reset() => _shot.Clear();

    internal static void Add(string plugin, int gx, int gy, int layersRead, Exception ex, string walk)
    {
        _shot.Add((plugin, gx, gy, layersRead, $"{ex.GetType().Name}: {ex.Message}", walk));
        Console.WriteLine($"   terrain layer(s) could not be read: {plugin} cell ({gx},{gy}) in {walk}, after {layersRead} "
                        + $"layer(s): {ex.GetType().Name}: {ex.Message}. The layers read before it are used; the rest of "
                        + $"that LAND's paint is missing.");
    }

    internal static int Count => _shot.Count;

    internal static object? ReportBlock() => _shot.Count == 0 ? null : new
    {
        count = _shot.Count,
        note = "terrain texture layers that could not be read; heights and placed content are unaffected",
        layers = _shot.Select(s => new { plugin = s.Plugin, grid = new[] { s.X, s.Y }, layersRead = s.LayersRead, error = s.Error, walk = s.Walk }).ToArray(),
    };
}
