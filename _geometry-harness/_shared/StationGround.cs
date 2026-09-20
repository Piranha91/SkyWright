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
//  THE RULE: the band is anchored at max(navmesh, terrain). Where the
//  landscape stands above the navmesh the navmesh is BURIED there, and the
//  body stands on the ground; where it does not, nothing changes. A
//  surface within the step reach ABOVE the terrain - a street mesh, a rock
//  standing on it - still wins, because the band is read upward from the
//  anchor exactly as before.
//
//  A floor below the terrain is then never chosen, and that follows from
//  the anchor rather than being bolted on: a surface under the terrain can
//  only lie inside the band when the terrain lies inside it too, and the
//  read takes the HIGHEST up-facing surface, so the terrain wins. Where
//  the navmesh is more than the band's depth above the landscape - a
//  bridge deck over a gorge - the band's floor is already above the
//  terrain and nothing under it is reachable either. proofprobe's G1 cases
//  pin both arms.
//
//  WHAT THIS FILE IS RIGHT NOW: the anchor with NO BOUND on it - it moves
//  to the terrain however far below the navmesh lies. Measured consequence
//  (the run record's storey diagnosis): a city mod's navmesh override
//  three thousand units under the landscape, in empty space, anchored
//  twenty stations on ground that says nothing about whether a body can
//  stand where the navmesh is. The bound is reproduced here at the STEP
//  REACH so proofprobe's G2 cases fail on a WRONG ANSWER, and the plan's
//  revision 10 states the physical condition that replaces it.
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
    float NavmeshBurial, bool TerrainIsAnotherStorey);

public static class StationGround
{
    /// <summary>How far above the navmesh the landscape may stand and still be ground the navmesh
    /// is merely buried under: a body standing at the navmesh height reaches this high, so above it
    /// the terrain is a different storey and says nothing about standing where the navmesh is.
    /// DEFECT G2: this is the STEP reach - what a walker steps onto - not what a body occupies.</summary>
    public static readonly float StandingHeight = NavmeshFloor.DefaultUpReach;

    /// <summary>Read the floor for a station. <paramref name="readAt"/> is the floor field's own
    /// read at this XY, taking the height to anchor the band on; <paramref name="stepReach"/> is
    /// only used to say whether the burial is deep enough to count, never to move the band.</summary>
    public static GroundReading Read(Func<float, FloorReading> readAt, float navZ, float? terrainZ,
                                     float stepReach = NavmeshFloor.DefaultUpReach)
    {
        float burial = terrainZ is { } t && t > navZ ? t - navZ : 0f;
        // DEFECT G2: no bound. Any burial at all moves the anchor to the terrain.
        bool onTerrain = burial > 0f;
        float anchorZ = onTerrain ? terrainZ!.Value : navZ;
        return new(readAt(anchorZ),
                   onTerrain ? FloorAnchor.Terrain : FloorAnchor.Navmesh,
                   anchorZ, navZ, terrainZ, burial, burial > stepReach);
    }
}
