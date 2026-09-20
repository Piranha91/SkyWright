using HousecarlCore;

namespace HarnessShared;

// ======================================================================
//  StationGround - WHERE the floor read is anchored, which is a different
//  question from what the floor rule finds once it is asked.
//
//  The floor rule (HousecarlCore.NavmeshFloor, unchanged and shared with
//  every route detector) reads a band around a height it is given: so far
//  below, so far above. The height it was given was always the navmesh.
//  That is wrong wherever the navmesh is AUTHORED UNDER THE LANDSCAPE - and
//  it is, in places, by more than a body can step. There the band opens
//  under the ground, the terrain the body actually stands on falls outside
//  it, and the rule answers with the highest thing it can see, which can be
//  a placed mesh far BELOW the landscape. Nothing a placed body settles on
//  in an exterior is underground.
//
//  WHAT THIS FILE IS RIGHT NOW: the read as it stood - anchored on the
//  navmesh whatever the terrain does, with no burial reported. The defect
//  is reproduced ON PURPOSE so that proofprobe's G1 cases fail on a WRONG
//  ANSWER rather than on a missing member.
// ======================================================================

/// <summary>Which height a station's floor read was anchored at.</summary>
public enum FloorAnchor : byte
{
    /// <summary>The winning navmesh triangle at the station's XY.</summary>
    Navmesh,
    /// <summary>The landscape, because it stands above the navmesh there.</summary>
    Terrain,
}

/// <summary>One station's ground: the floor the rule read, the height that read was anchored at,
/// and how far the landscape stands over the navmesh underneath it.</summary>
public readonly record struct GroundReading(
    FloorReading Floor, FloorAnchor Anchor, float AnchorZ, float NavZ, float? TerrainZ,
    float NavmeshBurial, bool NavmeshBuried);

public static class StationGround
{
    /// <summary>Read the floor for a station. <paramref name="readAt"/> is the floor field's own
    /// read at this XY, taking the height to anchor the band on.
    /// DEFECT G1: the anchor is the navmesh whatever the landscape does, and the burial is never
    /// measured.</summary>
    public static GroundReading Read(Func<float, FloorReading> readAt, float navZ, float? terrainZ,
                                     float stepReach = NavmeshFloor.DefaultUpReach)
        => new(readAt(navZ), FloorAnchor.Navmesh, navZ, navZ, terrainZ, 0f, false);
}
