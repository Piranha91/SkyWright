using HousecarlCore;
using SkyWright.Engine;

namespace HarnessShared;

// THE RING RULE the probes share (moved from viewprobe's WinningRefs.cs): which references beyond the
// 3x3 the game has loaded around a square, and whether one's placed box reaches into the 3x3.
//   - EdgeRadius: uGridsToLoad, unset in every lab INI, defaults to 5 (the player's cell +-2). INFERRED, not read: the
//     live GetINIInt returned 0 (2026-10-10).
//   - LargeRadius: a worldspace's large-reference lists (WRLD RNAM) name references the game 3D-loads out to
//     (uLargeRefLODGridSize - 1) / 2 cells: 11 in the lab INI, so 5. MEASURED in game (lane G: Tamriel at one station;
//     lane L: both city worldspaces). Whether such a reference has actor collision where it enters the 3x3 is NOT read in
//     game; viewprobe draws it, FloorFeed offers it as a floor (FloorFeed.ReachLarge).
//   - RootUsesUnion: a root worldspace takes the union of every plugin's list (lane G: the winning Tamriel WRLD lists
//     nothing and the listed references still load).
// viewprobe's merges (WinningRefs.cs) and FloorFeed's reach (FloorFeed.FloorReach) call these and nothing else, so a
// convention fixed here holds for both (lane V's yaw-only sabotage arm edits EntersSquare).
internal static class RingReach
{
    internal const int EdgeRadius = 2;
    internal const int LargeRadius = 5;
    internal const bool RootUsesUnion = true;

    /// <summary>The grid a reference is anchored at: a temporary reference loads with its holding cell, so that cell's
    /// grid; a persistent reference (or one in a cell with no grid) by its position.</summary>
    internal static (int X, int Y) AnchorOf(CellPlacements cp, PlacedRef r) =>
        r.Persistent || cp.Grid is not { } cg
            ? ((int)MathF.Floor(r.Position.X / 4096f), (int)MathF.Floor(r.Position.Y / 4096f))
            : (cg.X, cg.Y);

    /// <summary>Chebyshev grid distance.</summary>
    internal static int Ring((int X, int Y) a, (int X, int Y) g) => Math.Max(Math.Abs(a.X - g.X), Math.Abs(a.Y - g.Y));

    /// <summary>The placed box (scale, the engine's rotation, position) overlaps the 3x3 square around g in XY.</summary>
    internal static bool EntersSquare(PlacedRef r, (Vec3 Min, Vec3 Max) b, (int X, int Y) g)
    {
        float x0 = (g.X - 1) * 4096f, y0 = (g.Y - 1) * 4096f, x1 = (g.X + 2) * 4096f, y1 = (g.Y + 2) * 4096f;
        var rot = SpatialOverlap.RefrRotation(r.Rotation);
        var pos = new System.Numerics.Vector3(r.Position.X, r.Position.Y, r.Position.Z);
        float mx0 = float.MaxValue, my0 = float.MaxValue, mx1 = float.MinValue, my1 = float.MinValue;
        for (int i = 0; i < 8; i++)
        {
            var c = new System.Numerics.Vector3((i & 1) == 0 ? b.Min.X : b.Max.X, (i & 2) == 0 ? b.Min.Y : b.Max.Y,
                                                (i & 4) == 0 ? b.Min.Z : b.Max.Z);
            var w = System.Numerics.Vector3.Transform(c * r.Scale, rot) + pos;
            mx0 = MathF.Min(mx0, w.X); my0 = MathF.Min(my0, w.Y); mx1 = MathF.Max(mx1, w.X); my1 = MathF.Max(my1, w.Y);
        }
        return mx0 < x1 && mx1 > x0 && my0 < y1 && my1 > y0;
    }

    /// <summary>viewprobe's --ring visible: the placed box (the same placement as EntersSquare) overlaps any of
    /// <paramref name="cells"/> in XY. A separate method so EntersSquare, and every caller of it, stays as it was.</summary>
    internal static bool EntersCells(PlacedRef r, (Vec3 Min, Vec3 Max) b, IReadOnlySet<(int X, int Y)> cells)
    {
        var rot = SpatialOverlap.RefrRotation(r.Rotation);
        var pos = new System.Numerics.Vector3(r.Position.X, r.Position.Y, r.Position.Z);
        float mx0 = float.MaxValue, my0 = float.MaxValue, mx1 = float.MinValue, my1 = float.MinValue;
        for (int i = 0; i < 8; i++)
        {
            var c = new System.Numerics.Vector3((i & 1) == 0 ? b.Min.X : b.Max.X, (i & 2) == 0 ? b.Min.Y : b.Max.Y,
                                                (i & 4) == 0 ? b.Min.Z : b.Max.Z);
            var w = System.Numerics.Vector3.Transform(c * r.Scale, rot) + pos;
            mx0 = MathF.Min(mx0, w.X); my0 = MathF.Min(my0, w.Y); mx1 = MathF.Max(mx1, w.X); my1 = MathF.Max(my1, w.Y);
        }
        // Open on the far side, as EntersSquare's strict test is: a box ending exactly on a cell's edge does not enter it.
        int cx0 = (int)MathF.Floor(mx0 / 4096f), cy0 = (int)MathF.Floor(my0 / 4096f);
        int cx1 = (int)MathF.Ceiling(mx1 / 4096f) - 1, cy1 = (int)MathF.Ceiling(my1 / 4096f) - 1;
        for (int x = cx0; x <= cx1; x++)
            for (int y = cy0; y <= cy1; y++)
                if (cells.Contains((x, y))) return true;
        return false;
    }
}
