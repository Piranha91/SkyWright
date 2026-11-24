using HousecarlCore;

namespace SkyWright.Engine;

/// <summary>
/// The cell lists of a navmesh's spatial grid, out of the raw bytes Mutagen hands over.
///
/// MUTAGEN LAGS XEDIT HERE: Mutagen 0.54.4 models the grid's header (<c>NavmeshGridDivisor</c>, <c>MaxDistanceX/Y</c>,
/// <c>Min</c>, <c>Max</c>) as fields but the cells only as <c>NavmeshGrid</c> bytes. xEdit names them: divisor² cells, row by
/// row, each a uint32 count and that many uint16 triangle indices. Decoding happens here and nowhere else; a caller that
/// needs the cells reads them through this function.
/// </summary>
public static class NavmeshGridDecode
{
    /// <summary>The divisor² cells, in stored order (cell k is row k / divisor, column k % divisor), or null with
    /// <paramref name="why"/> when the bytes do not hold exactly that many cells.</summary>
    public static ushort[][]? Cells(uint divisor, ReadOnlySpan<byte> grid, out string? why)
    {
        why = null;
        if (divisor == 0 || divisor > 1024) { why = $"divisor {divisor} is outside 1..1024"; return null; }
        int n = checked((int)(divisor * divisor)), at = 0;
        var cells = new ushort[n][];
        for (int k = 0; k < n; k++)
        {
            if (at + 4 > grid.Length) { why = $"the grid ends at cell {k} of {n}"; return null; }
            int count = (int)BitConverter.ToUInt32(grid.Slice(at, 4)); at += 4;
            if (count < 0 || at + 2L * count > grid.Length) { why = $"cell {k} says {count} triangles, more than the bytes left"; return null; }
            var c = new ushort[count];
            for (int i = 0; i < count; i++) c[i] = BitConverter.ToUInt16(grid.Slice(at + 2 * i, 2));
            at += 2 * count;
            cells[k] = c;
        }
        if (at != grid.Length) { why = $"{grid.Length - at} bytes are left after {n} cells"; return null; }
        return cells;
    }
}
