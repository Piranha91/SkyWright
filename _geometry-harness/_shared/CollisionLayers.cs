using System;
using System.Collections.Generic;

// ======================================================================
//  CollisionLayers — which Havok collision layers an ACTOR can meet.
//
//  DATA, and an ASSUMPTION, stated here in one place so every probe that
//  builds a "what stops an NPC" soup uses the same set and reports the
//  same thing. The decoder (HavokCollision) reports every body's layer by
//  the game's own name and judges nothing; this is the judgement.
//
//  Provenance (2026-09-02): the layer names are Skyrim's (nif.xml's
//  SkyrimLayer enum, as Nifly 1.1.0 exposes it); the split below is drawn
//  from what the names mean and from how the vanilla meshes use them
//  (collprobe's layer-by-record-type table: statics overwhelmingly STATIC,
//  flora spread over NONCOLLIDABLE / CLUTTER / STATIC, trunks on TREES, the
//  invisible wall-top boxes on TRANSPARENT). It is NOT read from the
//  engine's collision matrix, which lives in the executable and not in any
//  file this harness can open, and the Creation Kit wiki page that
//  describes it was unreachable when this was written. Two layers were
//  tested in game the same day (Part C of the 4.7 handoff): a fence whose
//  only body is on TRANSPARENT stopped a commanded walker dead — solid,
//  as assumed; an urn whose only body is on CLUTTER and which is a
//  MoveableStatic was shoved aside — so for a MOVEABLE object the layer
//  is not what decides, the record type is. CLUTTER stays in the solid
//  set because a CLUTTER body on a Static cannot move (untested); a caller
//  that wants "what an actor cannot push through" should also drop
//  MoveableStatic bodies, and say so. Change the set here, re-run, and say
//  so in the note.
// ======================================================================

internal static class CollisionLayers
{
    /// <summary>Layers an actor's character controller collides with — the bodies that can block a route.</summary>
    public static readonly HashSet<string> ActorSolid = new(StringComparer.Ordinal)
    {
        "SKYL_STATIC", "SKYL_ANIMSTATIC", "SKYL_TRANSPARENT", "SKYL_CLUTTER", "SKYL_TREES", "SKYL_PROPS",
        "SKYL_TERRAIN", "SKYL_GROUND", "SKYL_INVISIBLE_WALL", "SKYL_STAIRHELPER", "SKYL_TRAP",
        "SKYL_DEBRIS_LARGE", "SKYL_UNIDENTIFIED",
    };

    /// <summary>Everything else is treated as passable: triggers, picks, zones, spells, projectiles, small debris,
    /// weapons lying about, water, actor layers, and NONCOLLIDABLE. A layer name this file has never seen is
    /// passable too — and counted by the caller, so an unknown layer is visible rather than silently solid.</summary>
    public static bool IsActorSolid(string layer) => ActorSolid.Contains(layer);

    /// <summary>A decoded mesh's two contact roles. The HOST surface: the solid bodies on the layers an actor (and a dropped item)
    /// meets. The DEPENDENT solid: every solid body but NONCOLLIDABLE. Either falls back to the render soup ('r') when the collision
    /// is incomplete or empty; '-' when there is neither. 'c' marks collision.</summary>
    public static (HousecarlCore.TriangleSoup Host, char HostSrc, HousecarlCore.TriangleSoup Dep, char DepSrc) RoleSoups(HousecarlCore.NifGeometryModel m)
    {
        var hc = m.CollisionSoupFor(b => b.Solid && IsActorSolid(b.Layer));
        var dc = m.CollisionSoupFor(b => b.Solid && b.Layer != "SKYL_NONCOLLIDABLE");
        (HousecarlCore.TriangleSoup, char) Pick(HousecarlCore.TriangleSoup? c) => m.CollisionComplete && c is { TriangleCount: > 0 } ? (c, 'c')
            : m.Soup is { TriangleCount: > 0 } ? (m.Soup, 'r') : (HousecarlCore.TriangleSoup.Empty, '-');
        var (h, hs) = Pick(hc);
        var (d, ds) = Pick(dc);
        return (h, hs, d, ds);
    }
}
