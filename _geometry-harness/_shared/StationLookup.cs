using HousecarlCore;

namespace HarnessShared;

// ======================================================================
//  StationLookup - the XY -> cell -> water lookup a station's environment
//  is built from, as a FUNCTION with proof cases rather than as inline
//  code inside a probe's main path.
//
//  It exists because one ladder's stations reach 3,000 u either side of a
//  primitive while an exterior cell is 4,096 u across: a station over the
//  border stands in the NEIGHBOUR's cell, and it is that cell's water, and
//  that cell's name, which belong on its pose. Asking per station is the
//  whole point; asking once per primitive is the defect this replaces.
//
//  A FAILED READ IS NOT AN ANSWER. That is the whole of this file's rule,
//  and it is the 2026-09-19 defect stated in its general form: a cell whose
//  water could not be read was published as a cell with no water. So every
//  way the question can fail to be answerable - a grid square the order
//  defines no cell for, a worldspace record that could not be read, a
//  parent link that leads to a record that could not be read - resolves to
//  Unresolved with a sentence naming what failed, and the station is
//  REFUSED (WaterUnknown) rather than stood on a surface nobody measured.
//  The defects this replaces are held down by proofprobe's L1-L7 cases,
//  recorded red at 34c5261d before this behaviour existed:
//    L1  the grid square truncated toward zero, so every station west or
//        south of the origin was attributed to the next cell - and
//        disagreed with the square FloorFeed files the floor by;
//    L2  a border station took the primitive's cell rather than its own;
//    L3  an unreadable worldspace was passed on as a null snap, which the
//        resolver reads as "no surface";
//    L4  the parent chain was not followed, so a city worldspace that
//        defers to Tamriel resolved to nothing;
//    L5  a parent link to an unreadable record was read as dry;
//    L7  an undefined grid square answered "no water at all".
//
//  The water itself is decided by ONE resolver (CellBoundaryIndex.Resolve,
//  through StationRule.Classify). This file does not decide it a second
//  time; it decides only WHICH cell is asked, and refuses rather than
//  guessing when the records behind the question could not be read.
//
//  WHAT IT DOES NOT DO: interiors. The lookup is the exterior grid lookup -
//  it turns an XY into a 4,096 u square - and an interior cell has no grid.
//  A caller with an interior target refuses it rather than running an
//  untested path through here.
// ======================================================================

/// <summary>What the load order says about one exterior grid square: the cell that occupies it, the
/// worldspace it belongs to, and that cell's own water fields as read.</summary>
public readonly record struct GridCellFacts(string Cell, string Worldspace, DetectorModel.WaterTuple Water);

/// <summary>A worldspace record as the order left it. The ABSENCE of the record is a null
/// <c>WorldFacts?</c> from the caller's lookup, which is NOT the same thing as a worldspace that
/// states no usable default: the first is a failed read and the second is an answer.</summary>
public readonly record struct WorldFacts(float? DefaultWaterHeight, string? Parent);

/// <summary>One station's own place: the cell it stands in, the worldspace, the grid square, and
/// that cell's water as the one resolver answered it.</summary>
public readonly record struct StationPlace(string Cell, string Worldspace, (int X, int Y) Grid,
                                           WaterResolution Water);

public static class StationLookup
{
    /// <summary>An exterior cell's side, in units. The same number FloorFeed.GridOf uses.</summary>
    public const float GridSize = 4096f;

    /// <summary>The exterior grid square an XY falls in. Floors toward minus infinity, which is
    /// what <c>FloorFeed.GridOf</c> does: the square a station's water is read from and the square
    /// its floor is filed under have to be the same square, and truncation toward zero makes them
    /// differ for every station west or south of the origin.</summary>
    public static (int X, int Y) GridOf(float x, float y)
        => ((int)MathF.Floor(x / GridSize), (int)MathF.Floor(y / GridSize));

    /// <summary>The surface the station stands over, decided for the cell the station's OWN XY falls
    /// in. <paramref name="cellAt"/> answers null for a grid square the order does not define;
    /// <paramref name="worldAt"/> answers null for a worldspace whose record could not be read.
    /// Neither null is passed on as an answer.</summary>
    public static StationPlace At(float x, float y,
                                  Func<(int X, int Y), GridCellFacts?> cellAt,
                                  Func<string, WorldFacts?> worldAt,
                                  string fallbackWorldspace,
                                  int maxHops = 8)
    {
        var g = GridOf(x, y);
        if (cellAt(g) is not { } f)
            return new StationPlace("", fallbackWorldspace, g,
                Unresolved(DetectorModel.WaterTuple.None, null, fallbackWorldspace,
                           $"the load order defines no cell at grid {g.X},{g.Y} of "
                           + $"{fallbackWorldspace}, so this station's own cell could not be read"));

        string ws = string.IsNullOrEmpty(f.Worldspace) ? fallbackWorldspace : f.Worldspace;
        var kind = DetectorModel.HeightKind(f.Water.Height);
        float? own = worldAt(ws)?.DefaultWaterHeight;

        // The worldspace statement decides ONLY for the two height kinds that defer to it (the
        // engine's two int-max sentinels). For any other kind the cell's own height settles the
        // surface, so a worldspace that could not be read cannot change the answer and must NOT
        // refuse the station - the other arm of this condition, and a case of its own.
        if (kind is not (WaterHeightKind.Unset or WaterHeightKind.UnsignedWrapped))
            return new StationPlace(f.Cell, ws, g,
                StationRule.Classify(f.Water, Snap(own), exterior: true, fromWorldspace: ws));

        // It defers, so the chain is walked - once, by StationRule.ResolveWorldDefault, which is
        // the one walk. A record the lookup could not READ is recorded here and refuses; a chain
        // that was read end to end and states no usable default is an ANSWER, and goes to the one
        // resolver to be read as it reads it.
        string? unreadable = null;
        var chain = StationRule.ResolveWorldDefault(ws, name =>
        {
            if (worldAt(name) is { } wf) return (wf.DefaultWaterHeight, wf.Parent);
            unreadable ??= name;
            return (null, null);
        }, maxHops);
        if (unreadable is { } bad)
            return new StationPlace(f.Cell, ws, g,
                Unresolved(f.Water, own, ws,
                           $"this cell defers to its worldspace, and {bad}'s record could not be "
                           + $"read (from {ws}, {chain.Hops} hop(s)); the surface is unknown, not absent"));

        return new StationPlace(f.Cell, ws, g,
            StationRule.Classify(f.Water, Snap(chain.Value ?? own), exterior: true,
                                 fromWorldspace: chain.Value is null ? ws : chain.From));
    }

    static WorldWaterSnap? Snap(float? defaultHeight)
        => defaultHeight is null ? null : new WorldWaterSnap(0, null, null, null, defaultHeight, false);

    /// <summary>A refusal built here rather than by the resolver: the records the question needed
    /// could not be read, so there is nothing to resolve. Never "dry".</summary>
    internal static WaterResolution Unresolved(DetectorModel.WaterTuple t, float? worldDefault,
                                               string worldspace, string how)
        => new(StationWaterVerdict.Unresolved, null, t.Height, DetectorModel.HeightKind(t.Height),
               worldDefault, worldspace, how);
}
