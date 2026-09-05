using System;
using System.Collections.Generic;
using System.Linq;
using HarnessShared;
using HousecarlCore;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

// ======================================================================
//  ActorBand — the actor column's top and bottom, DERIVED, not assumed.
//
//  The surface-crossing and severance tests judge only the band from
//  `clearance` (step height) to `height` (head height) above each navmesh
//  triangle, so an archway or a bridge over the navmesh is never a cut.
//  Until 2026-09-02 the top was a constant, 128 u — a scale-1 humanoid —
//  for everyone. Piranha's refinement (roadmap 4.7, Part B): derive it from
//  the tallest humanoid that can stand in the cell — the race heights and
//  scales of the NPCs placed there, or the load order's tallest playable
//  race when the cell holds none — and PRINT what was derived.
//
//  The bottom was the assumed 32 u from milestone 4c to 2026-09-05 and is now
//  MEASURED (Q23 Part A: five fresh games, ~230 arms, the Ratway corridor):
//  a package-free humanoid clone walking at its own pace got up a 48 u beam
//  in every repeat at SetScale 0.7 / 0.78 / 1.0 / 1.3 and as the child race
//  (race Height 0.8); 52 u in every repeat as an adult (half the time stalling
//  on top of it), two of three as the child race, one of three at 0.7; 55 u
//  only at scale 1.3 (3 of 4) or running (4 of 4 at 240 u/s); 60 u only
//  running (1 of 4). The step barely follows the actor's size — proportional
//  scaling would have put the 0.7 body near 35 u and the 1.3 body near 70 — so
//  the bottom is a CONSTANT for humanoids: 48 u, the highest rung every walking
//  humanoid cleared every time, with a band (48, 60] above it where pace and
//  size decide, which the route-cut verdict names. The shortest-actor
//  derivation the handoff proposed is moot within one rung. Creatures are
//  another class (a goat is stopped by a 22 u beam; a dog would not path the
//  corridor at all) and are catalogued, not folded. Every height is ABOVE THE
//  NAVMESH, which is what the route rule measures against; at that station
//  the navmesh floats 10 u above the physical floor, so the walker's physical
//  step is ~62 u and the transfer to a cell with another offset is the open
//  item. Research/ACTOR-BAND-BOTTOM-2026-09-05.md.
//
//  Height arithmetic follows viewprobe's NpcResolve for the race height (for
//  the NPC's sex) × the NPC's own Height, on a 128 u scale-1 figure — and NOT
//  the placement's scale any more (2026-09-05, in ForCell). Templated NPCs are
//  read from their own record, not their template — counted, so a cell whose
//  only actors are leveled spawns is known to fall back.
// ======================================================================

internal static class ActorBand
{
    /// <summary>Head height of a scale-1 humanoid above the ground — the constant the band top used to be.</summary>
    public const float ScaleOneHeight = 128f;
    /// <summary>Step height — the band's bottom, above the navmesh. MEASURED 2026-09-05 (header): 48 u. The constant's
    /// home is <see cref="DetectorModel.StepHeight"/> (DetectorModel is compiled into every probe; this file is not);
    /// this forwards so the band's callers keep one name.</summary>
    public const float StepHeight = DetectorModel.StepHeight;

    /// <summary>The top of the step band (48, 60] the route-cut verdict names — <see cref="DetectorModel.StepBandTop"/>.</summary>
    public const float StepBandTop = DetectorModel.StepBandTop;

    /// <summary>What the character controller actually ducks under, as a fraction of the nominal head height the
    /// records give. MEASURED 2026-09-02 (roadmap 4.7 Part C, the overhead ladder): a scale-1 Nord — 128 × 1.03 =
    /// 132 u nominal — stopped at a beam whose underside was 100 u above the floor and walked under one at 120 u,
    /// so its controller's top lies in (100, 120) u: a factor in (0.76, 0.91). 0.85 is the middle of that interval
    /// (112 u for the Nord). One walker, one race, one arm per rung; a taller or shorter race was not measured, and
    /// the factor is applied to every derived top on the assumption the controller scales with the model.</summary>
    public const float ControllerFactor = 0.85f;

    /// <summary>The band top the route-cut rule uses: the derived nominal top scaled to the controller's.</summary>
    public static float ControllerTop(float nominalTop) => nominalTop * ControllerFactor;

    public sealed record NpcFacts(FormKey Race, bool Female, float Height, bool Templated);
    public sealed record RaceFacts(float Male, float Female, bool Playable, string? EditorId);

    /// <summary>Read every NPC and race a mod carries. Call in load order so a later plugin's override wins.</summary>
    public static void Collect(ISkyrimModGetter mod, Dictionary<FormKey, NpcFacts> npcs, Dictionary<FormKey, RaceFacts> races)
    {
        foreach (var rec in mod.EnumerateMajorRecords()) CollectRecord(rec, npcs, races);
    }

    /// <summary>The per-record half of <see cref="Collect"/>, for a caller already walking a mod's records.</summary>
    public static void CollectRecord(IMajorRecordGetter rec, Dictionary<FormKey, NpcFacts> npcs, Dictionary<FormKey, RaceFacts> races)
    {
        if (rec is INpcGetter npc)
        {
            bool female = npc.Configuration.Flags.HasFlag(NpcConfiguration.Flag.Female);
            float h = float.IsFinite(npc.Height) && npc.Height > 0f ? npc.Height : 1f;
            bool templated = npc.Template is { IsNull: false };
            npcs[npc.FormKey] = new NpcFacts(npc.Race.FormKey, female, h, templated);
        }
        else if (rec is IRaceGetter race)
        {
            float m = race.Height.Male, f = race.Height.Female;
            races[race.FormKey] = new RaceFacts(float.IsFinite(m) && m > 0 ? m : 1f, float.IsFinite(f) && f > 0 ? f : 1f,
                                                race.Flags.HasFlag(Race.Flag.Playable), race.EditorID);
        }
    }

    /// <summary>The load order's tallest playable race, as a band top in units, with its name.</summary>
    public static (float Top, string Source) TallestPlayable(Dictionary<FormKey, RaceFacts> races)
    {
        RaceFacts? best = null; float bestH = 0f;
        foreach (var r in races.Values)
        {
            if (!r.Playable) continue;
            float h = MathF.Max(r.Male, r.Female);
            if (h > bestH) { bestH = h; best = r; }
        }
        if (best is null) return (ScaleOneHeight, "no playable race found; 128 u");
        return (ScaleOneHeight * bestH, $"tallest playable race {best.EditorId ?? "?"} x{bestH:0.###} = {ScaleOneHeight * bestH:0} u");
    }

    /// <summary>The band top for one cell: the tallest placed actor's head height, else the fallback.</summary>
    public static (float Top, int Actors, int Templated, string Source) ForCell(
        IEnumerable<PlacedRef> refs, Dictionary<FormKey, NpcFacts> npcs, Dictionary<FormKey, RaceFacts> races,
        float fallbackTop, string fallbackSource)
    {
        float best = 0f; int actors = 0, templated = 0; string who = "";
        foreach (var r in refs)
        {
            if (r.RecordType != "PlacedNpc" || r.Base is not { } bk || !npcs.TryGetValue(bk, out var n)) continue;
            if (r.InitiallyDisabled || r.Deleted) continue;
            actors++;
            if (n.Templated) templated++;
            float raceH = races.TryGetValue(n.Race, out var rc) ? (n.Female ? rc.Female : rc.Male) : 1f;
            // The placement's scale is NOT a factor (measured 2026-09-05, five games): a clone at SetScale 0.7 was stopped
            // by the same overhead beams as the adult and one at 1.3 walked under one the adult's top clears, before and
            // after its 3D was rebuilt at the scaled size — the character controller follows the RACE's Height (a race
            // with Height 0.8 was stopped by an underside at 85 u and walked under 100, where the 1.03 race is stopped by
            // 100 and walks under 120) and ignores the reference scale. The NPC record's own Height is untested (every
            // walker carried 1.0) and stays in as the race's kind of factor. A scaled placement is a bigger picture with
            // the same 22 u radius and the same top; the source string says when one was not counted.
            float top = ScaleOneHeight * raceH * n.Height;
            if (top > best)
            {
                best = top;
                who = $"{(races.TryGetValue(n.Race, out var rr) ? rr.EditorId : n.Race.ToString())} x{raceH * n.Height:0.###}"
                    + (MathF.Abs(r.Scale - 1f) > 0.01f ? $" (placement scale {r.Scale:0.##} not counted)" : "");
            }
        }
        if (actors == 0 || best <= 0f) return (fallbackTop, actors, templated, $"no actors in cell: {fallbackSource}");
        return (best, actors, templated, $"tallest placed actor {who} = {best:0} u");
    }
}
