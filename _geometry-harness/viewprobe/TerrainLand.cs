using System.Collections;
using HousecarlCore;
using Mutagen.Bethesda.Skyrim;

namespace ViewProbe;

// THE TERRAIN WALK'S RULE FOR ONE CELL COPY'S LAND, in load order (Program.cs's terrain walk; tested by
// --moved-ref-selftest, T1). The classification is the core's one rule (HousecarlCore.LandHeight.Classify), shared with
// PlacementExtract and so with every floor filing:
//   - a cell copy with no LAND leaves the square's earlier terrain as it is;
//   - a LAND that DECLARES no heights removes the square's earlier terrain from every map the walk keeps, so nothing is
//     drawn there: the engine draws no ground over such a square (a hole) and has no terrain collision (manual lab
//     playtest, 2026-09-27);
//   - a LAND that says it has heights and gives none is refused (TerrainUnreadable): drawing an earlier plugin's heights
//     in its place would be a silent wrong picture;
//   - a readable LAND is returned, for the walk to take.
internal static class TerrainLand
{
    /// <summary>The heights to take for grid square <paramref name="g"/>, or null when this cell copy leaves nothing to
    /// take (no LAND: the earlier terrain stays; a LAND that declares none: the earlier terrain has been removed from every
    /// map in <paramref name="maps"/>, each keyed by the grid square). Throws TerrainUnreadable for an unreadable LAND.</summary>
    internal static LandHeight.LandClass? Step(ILandscapeGetter? ls, string plugin, (int X, int Y) g, params IDictionary[] maps)
    {
        if (ls is null) return null;
        var lc = LandHeight.Classify(ls);
        switch (lc.Kind)
        {
            case LandHeight.LandKind.Heights:
                return lc;
            case LandHeight.LandKind.DeclaresNone:
                foreach (var m in maps) m.Remove(g);
                return null;
            default:
                throw new TerrainUnreadable(plugin, g.X, g.Y, lc.Error ?? new InvalidDataException(lc.Reason));
        }
    }
}
