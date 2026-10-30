using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using SkyRender.World;

namespace ViewProbe;

/// <summary>
/// Near-field grass (default on; --no-grass turns it off): the GRAS records the landscape textures of the
/// drawn cells list (LTEX GNAM), placed on the drawn terrain by SkyRender.World's GrassPlacer and drawn
/// instanced by GlWorldFactory.CreateGrass, as the game draws grass in loaded exterior cells.
///
/// The walk notes every GRAS and LTEX (winner-takes-all in load order); the terrain walk hands over one
/// TerrainCell per drawn cell with each quadrant's base and alpha layers named by their LTEX, so every
/// layer carries its own grass list (a diffuse path can be shared by textures with different grass).
/// Engine settings come from the profile's Skyrim.ini and SkyrimPrefs.ini [Grass] (SkyrimPrefs wins),
/// else the engine defaults (GrassSettings). Positions follow the GRAS parameters with SkyRender's own
/// seeded placement: the engine's exact positions are not reproducible (RENDERING_PIPELINE.md, "Grass").
/// </summary>
internal static class GrassLayer
{
    /// <summary>On unless --no-grass.</summary>
    internal static bool Requested = true;

    /// <summary>The MO2 profile folder, for its INI files.</summary>
    internal static string? ProfileDir;

    private static readonly Dictionary<FormKey, GrassType> Types = new();
    private static readonly Dictionary<FormKey, List<FormKey>> LtexGrasses = new();

    /// <summary>The cells to grow grass on (set by the terrain walk), and each one's water height.</summary>
    internal static List<TerrainCell> Cells { get; } = new();
    internal static Dictionary<(int X, int Y), float> WaterHeights { get; } = new();
    /// <summary>Why no cells were handed over, for the console (a retained walk, an interior, ...).</summary>
    internal static string? NoCellsWhy;

    internal static void NoteGrass(IGrassGetter g)
    {
        if (!Requested || g.Model?.File.GivenPath is not { Length: > 0 } model) return;
        var rel = model.Replace('/', '\\');
        if (!rel.StartsWith("meshes\\", StringComparison.OrdinalIgnoreCase)) rel = "meshes\\" + rel;
        Types[g.FormKey] = new GrassType
        {
            Id = StableId(g.FormKey),
            ModelPath = rel,
            Density = g.Density,
            MinSlope = g.MinSlope,
            MaxSlope = g.MaxSlope,
            UnitsFromWater = g.UnitsFromWater,
            WaterRule = (GrassWaterRule)(int)g.UnitsFromWaterType,
            PositionRange = g.PositionRange,
            HeightRange = g.HeightRange,
            ColorRange = g.ColorRange,
            WavePeriod = g.WavePeriod,
            Flags = (g.Flags.HasFlag(Grass.Flag.VertexLighting) ? GrassFlags.VertexLighting : 0)
                  | (g.Flags.HasFlag(Grass.Flag.UniformScaling) ? GrassFlags.UniformScaling : 0)
                  | (g.Flags.HasFlag(Grass.Flag.FitToSlope) ? GrassFlags.FitToSlope : 0),
        };
    }

    internal static void NoteLtex(ILandscapeTextureGetter lt)
    {
        if (!Requested) return;
        LtexGrasses[lt.FormKey] = lt.Grasses.Select(l => l.FormKey).Where(k => !k.IsNull).ToList();
    }

    /// <summary>The grasses a landscape texture lists, in record order (unknown GRAS left out).</summary>
    internal static IReadOnlyList<GrassType>? GrassesFor(FormKey? ltex)
    {
        if (ltex is not { } k || k.IsNull || !LtexGrasses.TryGetValue(k, out var list) || list.Count == 0) return null;
        var types = new List<GrassType>(list.Count);
        foreach (var g in list) if (Types.TryGetValue(g, out var t)) types.Add(t);
        return types.Count > 0 ? types : null;
    }

    internal static int TypesSeen => Types.Count;

    /// <summary>A FormKey as a 32-bit seed: its ID mixed with its plugin's name.</summary>
    static uint StableId(FormKey k)
    {
        uint h = 2166136261u;
        foreach (char c in k.ModKey.FileName.String.ToLowerInvariant()) { h ^= c; h *= 16777619u; }
        return h ^ k.ID;
    }

    /// <summary>The engine's [Grass] settings: the profile's Skyrim.ini, then SkyrimPrefs.ini; absent keys keep the
    /// engine defaults.</summary>
    internal static GrassSettings Settings(out string source)
    {
        var s = new GrassSettings();
        var found = new List<string>();
        foreach (var name in new[] { "Skyrim.ini", "SkyrimPrefs.ini" })
        {
            var file = ProfileDir is { } d && Directory.Exists(d)
                ? Directory.EnumerateFiles(d, "*.ini").FirstOrDefault(f => string.Equals(Path.GetFileName(f), name, StringComparison.OrdinalIgnoreCase))
                : null;
            if (file is null) continue;
            bool inGrass = false;
            foreach (var raw in File.ReadLines(file))
            {
                var line = raw.Trim();
                if (line.StartsWith('[')) { inGrass = string.Equals(line, "[Grass]", StringComparison.OrdinalIgnoreCase); continue; }
                int eq = line.IndexOf('=');
                if (!inGrass || eq <= 0) continue;
                string key = line[..eq].Trim(), val = line[(eq + 1)..].Trim();
                bool f = float.TryParse(val, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var fv);
                switch (key.ToLowerInvariant())
                {
                    case "imingrasssize" when f: s = s with { MinGrassSize = (int)fv }; found.Add($"{name} {key}={val}"); break;
                    case "imaxgrasstypespertexure" when f: s = s with { MaxGrassTypesPerTexture = (int)fv }; found.Add($"{name} {key}={val}"); break;
                    case "fgrassstartfadedistance" when f: s = s with { StartFadeDistance = fv }; found.Add($"{name} {key}={val}"); break;
                    case "fgrassfaderange" when f: s = s with { FadeRange = fv }; found.Add($"{name} {key}={val}"); break;
                    case "ftexturepctthreshold" when f: s = s with { TexturePercentThreshold = fv }; found.Add($"{name} {key}={val}"); break;
                }
            }
        }
        source = found.Count > 0 ? string.Join(", ", found) : "engine defaults";
        return s;
    }
}
