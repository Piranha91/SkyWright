using Mutagen.Bethesda.Skyrim;

namespace SkyWright.Engine;

/// <summary>
/// The LAND record's vertex heightmap (VHGT) decoded into world-unit heights, once, for every consumer.
///
/// Moved here 2026-09-04 from viewprobe (where it had drawn the terrain since 2026-08-27) so that the
/// landscape-seam detector and the renderer read one function: a seam measured on one decode and drawn
/// on another would be two opinions about where the ground is.
///
/// The format: a float <c>Offset</c> and a 33×33 grid of signed bytes, each a GRADIENT from its
/// neighbour in units of 8 world units. Column 0 accumulates down the rows from the offset (each row's
/// first value is a step from the previous row's first value); every other cell accumulates along its
/// row from the cell to its left. Vertex (x, y) stands at world (cellX·4096 + x·128, cellY·4096 + y·128);
/// vertex 32 of one cell is vertex 0 of the next, which is what makes a shared edge comparable at all.
/// </summary>
public static class LandHeight
{
    /// <summary>Vertices per side of a cell's heightmap.</summary>
    public const int Side = 33;
    /// <summary>World units between adjacent vertices (4096 / 32).</summary>
    public const float VertexSpacing = 128f;
    /// <summary>World units per stored heightmap unit.</summary>
    public const float Unit = 8f;

    /// <summary>Decode <paramref name="gradients"/>[x, y] (33×33, x along the row) and <paramref name="offset"/>
    /// into heights in WORLD units, indexed [x, y].</summary>
    public static float[,] Decode(float offset, sbyte[,] gradients)
    {
        if (gradients.GetLength(0) != Side || gradients.GetLength(1) != Side)
            throw new ArgumentException($"a VHGT grid is {Side}×{Side}, got {gradients.GetLength(0)}×{gradients.GetLength(1)}", nameof(gradients));
        var h = new float[Side, Side];
        float rowStart = offset;
        for (int y = 0; y < Side; y++)
        {
            rowStart += gradients[0, y];
            float acc = rowStart;
            h[0, y] = acc * Unit;
            for (int x = 1; x < Side; x++) { acc += gradients[x, y]; h[x, y] = acc * Unit; }
        }
        return h;
    }

    /// <summary>The raw gradients out of a record, or null when the record carries no usable map (a
    /// truncated LAND throws from inside the overlay; the caller counts it, never guesses at it).</summary>
    public static sbyte[,]? Gradients(ILandscapeVertexHeightMapGetter? vh)
    {
        if (vh is null) return null;
        var g = new sbyte[Side, Side];
        try
        {
            for (int y = 0; y < Side; y++)
                for (int x = 0; x < Side; x++)
                    g[x, y] = vh.HeightMap[x, y];
        }
        catch { return null; }
        return g;
    }

    /// <summary>What a present LAND record says about its square's ground: see <see cref="Classify"/>.</summary>
    public enum LandKind
    {
        /// <summary>A readable heightmap: <see cref="LandClass.Offset"/> and <see cref="LandClass.Gradients"/> hold it.</summary>
        Heights,
        /// <summary>The DATA flag does not say the LAND has heights (VertexNormalsHeightMap clear, or no DATA) and there is no
        /// VHGT: the record declares no terrain on this square. The engine draws no ground there and has no terrain
        /// collision (manual lab playtest, 2026-09-27; CellPlacements.LandDeclaresNoHeights has the detail). A reader
        /// takes the square as having no terrain, never as keeping an earlier plugin's heights.</summary>
        DeclaresNone,
        /// <summary>The LAND says it has heights and gives none: the VHGT is missing although the flag is set, or it (or
        /// the flags) cannot be read. <see cref="LandClass.Reason"/> names why. Not a trustworthy floor: refuse or count.</summary>
        Unreadable,
    }

    /// <summary>The classification of one LAND: the heightmap when <see cref="Kind"/> is Heights, the reason (and the
    /// exception, when one was thrown) when it is Unreadable.</summary>
    public readonly record struct LandClass(LandKind Kind, float Offset = 0f, sbyte[,]? Gradients = null, string? Reason = null,
                                            Exception? Error = null);

    /// <summary>Classify a PRESENT LAND record: the one rule every reader of terrain uses (PlacementExtract.FromCell and
    /// viewprobe's terrain walk). A heightmap Mutagen misreads without throwing (a VHGT cut short in the middle of a
    /// plugin, measured 2026-09-27) classifies as Heights: no reader can see it here.</summary>
    public static LandClass Classify(ILandscapeGetter ls)
    {
        ILandscapeVertexHeightMapGetter? vh;
        try { vh = ls.VertexHeightMap; }
        catch (Exception ex) { return new LandClass(LandKind.Unreadable, Reason: "VHGT unreadable: " + ex.GetType().Name, Error: ex); }
        if (vh is null)
        {
            Landscape.Flag? flags;
            try { flags = ls.Flags; }
            catch (Exception ex) { return new LandClass(LandKind.Unreadable, Reason: "LAND flags unreadable: " + ex.GetType().Name, Error: ex); }
            return (flags ?? 0).HasFlag(Landscape.Flag.VertexNormalsHeightMap)
                ? new LandClass(LandKind.Unreadable, Reason: "LAND has no VHGT, though its DATA flag says it has heights")
                : new LandClass(LandKind.DeclaresNone);
        }
        try
        {
            var g = new sbyte[Side, Side];
            for (int y = 0; y < Side; y++)
                for (int x = 0; x < Side; x++)
                    g[x, y] = vh.HeightMap[x, y];
            return new LandClass(LandKind.Heights, vh.Offset, g);
        }
        catch (Exception ex) { return new LandClass(LandKind.Unreadable, Reason: "VHGT unreadable: " + ex.GetType().Name, Error: ex); }
    }

    /// <summary>Decode straight from the record; null when the map is absent or unreadable.</summary>
    public static float[,]? Decode(ILandscapeVertexHeightMapGetter? vh)
        => vh is null ? null : Gradients(vh) is { } g ? Decode(vh.Offset, g) : null;

    /// <summary>The 33 heights along one edge of a decoded map, in vertex order (south→north for the east and
    /// west edges, west→east for the north and south edges).</summary>
    public static float[] Edge(float[,] heights, CellEdge edge)
    {
        var e = new float[Side];
        for (int i = 0; i < Side; i++)
            e[i] = edge switch
            {
                CellEdge.East => heights[Side - 1, i],
                CellEdge.West => heights[0, i],
                CellEdge.North => heights[i, Side - 1],
                CellEdge.South => heights[i, 0],
                _ => throw new ArgumentOutOfRangeException(nameof(edge)),
            };
        return e;
    }
}

/// <summary>An edge of a cell's heightmap, named by compass direction in grid coordinates (+x east, +y north).</summary>
public enum CellEdge { East, West, North, South }
