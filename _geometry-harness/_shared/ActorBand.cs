using System;
using System.Collections.Generic;
using System.Linq;
using HarnessShared;
using HousecarlCore;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

// The top is derived from placed actors' race height and NPC height. Reference scale contributes only for a
// generated controller, which the engine builds at placement scale on cell load. Custom skeleton phantoms ignore it.
// ResolveControllers reads the winning skeleton, per sex; no race-name allowlist. An unclassified scaled actor makes
// the cell unjudgeable. Template inheritance and the absolute controller dimensions remain outside this derivation.
// The bottom and radius remain the measured humanoid model; this fold changes the scale term, not creature locomotion.

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

    /// <summary>The controller's top ABOVE THE NAVMESH at the measuring station: the derived nominal top scaled to the
    /// controller's. What the route rule's band top was until Q24; still the record of the measurement.</summary>
    public static float ControllerTop(float nominalTop) => nominalTop * ControllerFactor;

    /// <summary>The measuring station's navmesh-to-floor offset (<see cref="DetectorModel.StationOffset"/>, 10.4 u) and the
    /// band's ends above the walker's FEET (Q24, 2026-09-05): the measured rungs plus that offset.</summary>
    public const float StationOffset = DetectorModel.StationOffset;
    public const float PhysicalStep = DetectorModel.PhysicalStep;
    public const float PhysicalStepBandTop = DetectorModel.PhysicalStepBandTop;

    /// <summary>The floor-bottom term (Q26): an object whose lowest material within the capsule's footprint stands this far or
    /// more above the floor along the cut is a wall whatever its rise — <see cref="DetectorModel.FloatWall"/>.</summary>
    public const float FloatWall = DetectorModel.FloatWall;

    /// <summary>The controller's top above the FEET: the navmesh-relative controller top plus the station's offset —
    /// 122.5 u for the 1.03 Nord, inside the (110.4, 130.4) u the overhead ladder measured above its feet (stopped by an
    /// underside 100 u above the navmesh, under one at 120); 97.4 u for the child race, inside its (95.4, 110.4). Additive
    /// rather than a refitted factor: the same measured interval, restated from the floor the walker stands on (a
    /// multiplied 0.92 would put the child's top below its measured interval). The route rule's band top once it reads
    /// the physical floor (RouteConnectivity.Measure with floorAt).</summary>
    public static float PhysicalControllerTop(float nominalTop) => ControllerTop(nominalTop) + StationOffset;

    /// <param name="Templated">The NPC has a template at all (any template flag): what the band count reports.</param>
    /// <param name="Template">Its template (an NPC or a leveled NPC list), FormKey.Null when it has none.</param>
    /// <param name="TraitsTemplated">Its traits (race, sex, height) come from the template: core's
    /// <see cref="FaceGenCheck.InheritsAppearance"/>, the Traits template flag.</param>
    public sealed record NpcFacts(FormKey Race, bool Female, float Height, bool Templated, FormKey Template = default,
                                  bool TraitsTemplated = false);

    /// <summary>The facts that set an NPC's body (race, sex, height): its own, or, while the Traits template flag is set,
    /// its template's, to the end of the chain. Null with the reason when the chain reaches a leveled list or a record
    /// not read (the race varies or is not known) or loops.</summary>
    public static NpcFacts? TraitsSource(FormKey npc, IReadOnlyDictionary<FormKey, NpcFacts> npcs, out string why)
    {
        why = "";
        if (!npcs.TryGetValue(npc, out var n)) { why = "its base is not an NPC record (a leveled actor or unread)"; return null; }
        var seen = new HashSet<FormKey> { npc };
        while (n.TraitsTemplated)
        {
            if (!npcs.TryGetValue(n.Template, out var next))
            {
                why = $"a templated NPC whose race comes from {n.Template}, a leveled list or a record not read: its race varies or is not known";
                return null;
            }
            if (!seen.Add(n.Template)) { why = $"a templated NPC whose template chain loops at {n.Template}"; return null; }
            n = next;
        }
        return n;
    }
    public sealed record RaceFacts(float Male, float Female, bool Playable, string? EditorId,
        string? MaleSkeleton = null, string? FemaleSkeleton = null,
        ControllerShapeFact? MaleController = null, ControllerShapeFact? FemaleController = null);

    /// <summary>Resolve the census from the winning skeleton assets, per sex, after collecting the winning races.
    /// Cache by path within this order only; an overridden skeleton can change a race's controller without a RACE edit.</summary>
    public static void ResolveControllers(Dictionary<FormKey, RaceFacts> races, AssetResolver assets)
    {
        var cache = new Dictionary<string, ControllerShapeFact>(StringComparer.OrdinalIgnoreCase);
        ControllerShapeFact Read(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return new(null, "race has no skeleton path");
            path = path.Replace('\\', '/');
            if (!path.StartsWith("meshes/", StringComparison.OrdinalIgnoreCase)) path = "meshes/" + path;
            if (cache.TryGetValue(path, out var hit)) return hit;
            var resolved = assets.ResolveForPlacement(path);
            ControllerShapeFact fact;
            if (resolved.Sources.Count == 0) fact = new(null, $"skeleton not resolved: {path}");
            else
            {
                var (bytes, error) = AssetResolver.ReadPlacementSource(resolved.Sources[0]);
                fact = bytes is null ? new(null, $"skeleton unreadable: {path}: {error}") : SkeletonController.Inspect(bytes);
            }
            return cache[path] = fact;
        }
        foreach (var (key, race) in races.ToArray())
            races[key] = race with { MaleController = Read(race.MaleSkeleton), FemaleController = Read(race.FemaleSkeleton) };
    }

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
            npcs[npc.FormKey] = new NpcFacts(npc.Race.FormKey, female, h, templated,
                                             templated ? npc.Template.FormKey : FormKey.Null, FaceGenCheck.InheritsAppearance(npc));
        }
        else if (rec is IRaceGetter race)
        {
            float m = race.Height.Male, f = race.Height.Female;
            races[race.FormKey] = new RaceFacts(float.IsFinite(m) && m > 0 ? m : 1f, float.IsFinite(f) && f > 0 ? f : 1f,
                                                race.Flags.HasFlag(Race.Flag.Playable), race.EditorID, race.SkeletalModel?.Male?.File.GivenPath, race.SkeletalModel?.Female?.File.GivenPath);
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

    /// <summary>The band top for one cell: the tallest placed actor's head height, else the fallback.
    /// <paramref name="enabled"/>: the run's population (<c>EnablePopulation.Predicate</c>) — the load-time enable chain
    /// by default since Q27, a state arm's under <c>--state</c>. NULL is the flag population, what every run before then
    /// judged. It matters: an actor placed as a switch child is in the room only in the states its marker is on for, and
    /// before Q27 the band was on the flag even inside a state arm.</summary>
    public static (float Top, int Actors, int Templated, string Source) ForCell(
        IEnumerable<PlacedRef> refs, Dictionary<FormKey, NpcFacts> npcs, Dictionary<FormKey, RaceFacts> races,
        float fallbackTop, string fallbackSource, Func<FormKey, bool>? enabled = null)
    {
        float best = 0f; int actors = 0, templated = 0; string who = "";
        foreach (var r in refs)
        {
            if (r.RecordType != "PlacedNpc" || r.Base is not { } bk || !npcs.TryGetValue(bk, out var n)) continue;
            if (r.Deleted) continue;
            if (enabled is null ? r.InitiallyDisabled : !enabled(r.FormKey)) continue;
            actors++;
            if (n.Templated) templated++;
            if (!races.TryGetValue(n.Race, out var rc))
                return (float.NaN, actors, templated, $"actor {r.FormKey}: race {n.Race} unresolved; band unjudgeable");
            float raceH = n.Female ? rc.Female : rc.Male;
            var controller = n.Female ? rc.FemaleController : rc.MaleController;
            bool scaled = MathF.Abs(r.Scale - 1f) > 1e-6f;
            if (!float.IsFinite(r.Scale) || r.Scale <= 0f)
                return (float.NaN, actors, templated, $"actor {r.FormKey}: invalid placement scale {r.Scale}; band unjudgeable");
            if (scaled && controller?.ControllerCustom is null)
                return (float.NaN, actors, templated, $"actor {r.FormKey}, race {rc.EditorId ?? n.Race.ToString()}: controller unclassified ({controller?.Why ?? "census not resolved"}), placement scale {r.Scale:0.###}; band unjudgeable");
            float placementScale = controller?.ControllerCustom == false ? r.Scale : 1f;
            float top = ScaleOneHeight * raceH * n.Height * placementScale;
            if (top > best)
            {
                best = top;
                who = $"{r.FormKey}, race {rc.EditorId ?? n.Race.ToString()} x{raceH * n.Height:0.###}"
                    + (controller?.ControllerCustom == false
                        ? $" (generated controller; placement scale {r.Scale:0.###} counted at 3D build)"
                        : controller?.ControllerCustom == true
                            ? $" (custom controller; placement scale {r.Scale:0.###} not counted)"
                            : $" (controller unclassified: {controller?.Why ?? "census not resolved"}; unit placement scale has no effect)");
            }
        }
        if (actors == 0 || best <= 0f) return (fallbackTop, actors, templated, $"no actors in cell: {fallbackSource}");
        return (best, actors, templated, $"tallest placed actor {who} = {best:0} u");
    }
}
