using HarnessShared;
using HousecarlCore;
using Mutagen.Bethesda.Plugins;

// THE NAVCUT BOXES THAT CAN CUT ONE TARGET CELL'S NAVMESH (Program.cs, the navcut pass). Held in its own class so
// --moved-ref-selftest runs exactly the selection a real run makes.
static class NavcutReach
{
    /// <summary>The navcut boxes measured against <paramref name="target"/>'s navmesh: every box held in the cell (as before), and,
    /// for an exterior cell, every box the floor filing places in the 3x3 around it (a box held in the worldspace's persistent cell
    /// or in a neighbour) whose placed box reaches into the cell's square. A box anchored beyond the 3x3 is not read.</summary>
    /// <param name="held">the references the walk holds in the target cell (their last sighting is there)</param>
    /// <param name="floorGrids">the floor filing of the target's worldspace (FloorFeed: every reference by the grid its position
    /// is in, persistent ones included), or null for an interior</param>
    /// <param name="isNavcut">a placed reference whose XTRI names a NavmeshObstacle layer and that carries an XPRM box</param>
    public static List<PlacedRef> ForCell(CellPlacements target, IEnumerable<PlacedRef> held,
                                          IReadOnlyDictionary<(int, int), Dictionary<FormKey, PlacedRef>>? floorGrids,
                                          Func<PlacedRef, bool> isNavcut)
    {
        var picked = held.Where(r => !r.Deleted && r.Primitive is not null && isNavcut(r)).ToList();
        if (floorGrids is null || target.Interior || target.Grid is not { } g) return picked;
        var have = picked.Select(r => r.FormKey).ToHashSet();
        float x0 = g.X * 4096f, y0 = g.Y * 4096f, x1 = x0 + 4096f, y1 = y0 + 4096f;
        for (int dx = -1; dx <= 1; dx++)
            for (int dy = -1; dy <= 1; dy++)
            {
                if (!floorGrids.TryGetValue((g.X + dx, g.Y + dy), out var refs)) continue;
                foreach (var r in refs.Values.OrderBy(r => r.FormKey.ToString(), StringComparer.Ordinal))
                {
                    if (have.Contains(r.FormKey) || r.Deleted || r.Primitive is not { } prim || !isNavcut(r)) continue;
                    var box = SpatialOverlap.ToWorld(NavcutGeometry.ModelBox(prim), r.Position, r.Rotation, r.Scale);
                    if (box.IsEmpty || box.Max.X < x0 || box.Min.X > x1 || box.Max.Y < y0 || box.Min.Y > y1) continue;
                    picked.Add(r); have.Add(r.FormKey);
                }
            }
        return picked;
    }
}
