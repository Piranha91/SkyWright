using HarnessShared;
using HousecarlCore;
using Mutagen.Bethesda.Plugins;

// THE NAVCUT BOXES THAT CAN CUT ONE TARGET CELL'S NAVMESH (Program.cs, the navcut pass). Held in its own class so
// --moved-ref-selftest runs exactly the selection a real run makes.
static class NavcutReach
{
    /// <summary>The navcut boxes measured against <paramref name="target"/>'s navmesh. STUB at the red commit: the boxes held
    /// in the target cell, as before.</summary>
    /// <param name="held">the references the walk holds in the target cell (their last sighting is there)</param>
    /// <param name="floorGrids">the floor filing of the target's worldspace (FloorFeed: every reference by the grid its position
    /// is in, persistent ones included), or null for an interior</param>
    /// <param name="isNavcut">a placed reference whose XTRI names a NavmeshObstacle layer and that carries an XPRM box</param>
    public static List<PlacedRef> ForCell(CellPlacements target, IEnumerable<PlacedRef> held,
                                          IReadOnlyDictionary<(int, int), Dictionary<FormKey, PlacedRef>>? floorGrids,
                                          Func<PlacedRef, bool> isNavcut)
        => held.Where(r => !r.Deleted && r.Primitive is not null && isNavcut(r)).ToList();
}
