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
//  No constant is chosen here and NavmeshFloor's defaults are untouched;
//  the step reach is only read, to say whether a burial is deep enough to
//  be worth counting.
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
    /// read at this XY, taking the height to anchor the band on; <paramref name="stepReach"/> is
    /// only used to say whether the burial is deep enough to count, never to move the band.</summary>
    public static GroundReading Read(Func<float, FloorReading> readAt, float navZ, float? terrainZ,
                                     float stepReach = NavmeshFloor.DefaultUpReach)
    {
        float burial = terrainZ is { } t && t > navZ ? t - navZ : 0f;
        bool onTerrain = burial > 0f;
        float anchorZ = onTerrain ? terrainZ!.Value : navZ;
        return new(readAt(anchorZ),
                   onTerrain ? FloorAnchor.Terrain : FloorAnchor.Navmesh,
                   anchorZ, navZ, terrainZ, burial, burial > stepReach);
    }
}
