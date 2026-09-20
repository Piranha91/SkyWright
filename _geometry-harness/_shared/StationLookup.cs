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
//  WHAT THIS FILE IS RIGHT NOW: the naive lookup, written the way it comes
//  out when nobody has asked what happens when a read FAILS. Every defect
//  below is reproduced ON PURPOSE so that the cases in
//  proofprobe/StationRuleProof.cs fail on a WRONG ANSWER rather than on a
//  missing member. The corrected behaviour is specified by those cases and
//  lands in a later commit.
//
//  The defects held down by the proofs:
//    L1  the grid square was computed by truncating toward zero, so every
//        station west or south of the origin was attributed to the cell
//        one over - and FloorFeed.GridOf, which files the floor, floors
//        toward minus infinity, so the two disagreed exactly there;
//    L2  a grid square the order does not define answered with an empty
//        cell and NO water at all, which the one resolver then reads as
//        dry - an unread square published as a dry one;
//    L3  a worldspace record that could not be READ was passed on as a
//        null snap, which on an exterior cell whose height defers is read
//        as "no surface": the 2026-09-19 water defect in a new place;
//    L4  the same for a parent link that could not be read, and the parent
//        chain was not followed at all - a city worldspace that defers to
//        Tamriel resolved to nothing.
//
//  The water itself is decided by ONE resolver (CellBoundaryIndex.Resolve,
//  through StationRule.Classify). This file does not decide it a second
//  time; it decides only WHICH cell is asked, and refuses rather than
//  guessing when the records behind the question could not be read.
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

    /// <summary>The exterior grid square an XY falls in.
    /// DEFECT L1: truncation toward zero, which sends every negative coordinate one square too far
    /// east or north and disagrees with <c>FloorFeed.GridOf</c> for exactly those stations.</summary>
    public static (int X, int Y) GridOf(float x, float y)
        => ((int)(x / GridSize), (int)(y / GridSize));

    /// <summary>The surface the station stands over, decided for the cell the station's OWN XY falls
    /// in. <paramref name="cellAt"/> answers null for a grid square the order does not define;
    /// <paramref name="worldAt"/> answers null for a worldspace whose record could not be read.
    /// DEFECTS L2, L3, L4: both failures are passed through as though they were answers.</summary>
    public static StationPlace At(float x, float y,
                                  Func<(int X, int Y), GridCellFacts?> cellAt,
                                  Func<string, WorldFacts?> worldAt,
                                  string fallbackWorldspace,
                                  int maxHops = 8)
    {
        var g = GridOf(x, y);
        if (cellAt(g) is not { } f)
            return new StationPlace("", fallbackWorldspace, g,
                StationRule.Classify(DetectorModel.WaterTuple.None, null, exterior: true,
                                     fromWorldspace: fallbackWorldspace));

        string ws = string.IsNullOrEmpty(f.Worldspace) ? fallbackWorldspace : f.Worldspace;
        var own = worldAt(ws);
        WorldWaterSnap? snap = own is { } w
            ? new WorldWaterSnap(0, null, null, null, w.DefaultWaterHeight, false)
            : null;
        return new StationPlace(f.Cell, ws, g,
            StationRule.Classify(f.Water, snap, exterior: true, fromWorldspace: ws));
    }

    /// <summary>A refusal built here rather than by the resolver: the records the question needed
    /// could not be read, so there is nothing to resolve. Never "dry".</summary>
    internal static WaterResolution Unresolved(DetectorModel.WaterTuple t, float? worldDefault,
                                               string worldspace, string how)
        => new(StationWaterVerdict.Unresolved, null, t.Height, DetectorModel.HeightKind(t.Height),
               worldDefault, worldspace, how);
}
