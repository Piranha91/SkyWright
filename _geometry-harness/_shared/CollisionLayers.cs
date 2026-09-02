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
//  describes it was unreachable when this was written. Two layers are the
//  ones most worth an in-game arm (Part C of the 4.7 handoff): TRANSPARENT
//  (assumed solid — the wall-top boxes read as "camera passes, actors do
//  not") and CLUTTER (assumed solid — a barrel stops an actor even though
//  physics can shove it). Change the set here, re-run, and say so in the note.
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
}
