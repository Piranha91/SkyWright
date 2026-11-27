namespace SkyWright.Engine;

// STRANDED BY THE OTHER MOD (the geometry.stranded-object check): houseCARL lane S's rule (s10_result.stranded_record,
// 2026-10-12, with the 2026-10-14 primitive bodies and triggers and the 2026-11-08 bodies), read as the placing plugin's
// OWN VIEW (Gate A review ruling 1, 2026-11-26): the navmesh as the object's winning plugin and its declared masters resolve
// it, in place of lane S's single-mod orders. This class owns every threshold, the population, the cuts and every reason;
// strandprobe measures and asks.
//   population   an interior reference that is there when the area loads (outdoors there is no room to take away);
//   the cuts     no row when the winning navmesh is its own view's (the same records from the same plugins), or when the
//                plugin whose navmesh wins has the object's plugin among its masters (that author had it in the editor:
//                no evidence of a clash between mods, ruling 2);
//   the body     an object whose model could not be read is not judged (Gate A review finding 2);
//   the distance a refused distance is not judged where the measured one still meets the form, else no row (C8); a trigger
//                (a model-less activator with a box) is judged by contact with the walkable band, reach 0 (C4, C7);
//   the form     beyond the reach with the winning navmesh and within it in the own view, else no row (C9);
//   clearance    no solid object of another mod in the room: not judged (C14); touching one: not judged (C15, before the
//                tolerance); an object of another mod whose body is only a bound, nearer than the nearest known body: not
//                judged (C16); a primitive blocks as PrimitiveSolidity measures it (ruling 4);
//   tolerance    within 12 u of the reach on either side (the navmesh-to-floor spread, Q24): not judged (C17);
//   reported     with its class (C19): quest or scripted objects first, then AI markers, then lights and decoration.

/// <summary>What the rule says about one reference: a row that is reported, a row that is not judged (with its reason), or
/// no row (with the reason it is counted under).</summary>
public enum StrandedState { Reported, NotJudged, NoRow }

public sealed record StrandedVerdict(StrandedState State, string Reason, string? Class = null);

/// <summary>The clearance facts of a reference in its room: whether any solid object of another mod stands there, the
/// nearest such body (box distance, its name), and the nearest object of another mod whose body could not be settled (a
/// bound: an unread model, a primitive with no measurement).</summary>
public sealed record StrandedClearance(bool AnyOther, double? Nearest, string? NearestName, double? UnreadNearest, string? UnreadName);

/// <summary>Everything the rule reads about one reference once it is measured: whether it is a trigger, its distance to the
/// winning navmesh and to its own view's, its clearance, why its body was not read (null when read), and its base.</summary>
public sealed record StrandedFacts(bool Trigger, NavDistance Winning, NavDistance Own, StrandedClearance Clearance, string? BodyUnread,
                                   string? BaseType, string? BaseEditorId, bool Scripted);

public static class StrandedRule
{
    /// <summary>The activation reach (u): s8_place.ACTIVATE, fActivatePickLength's default.</summary>
    public const double Reach = 150;
    /// <summary>The tolerance (u) on either side of the reach: the walkable floor lies -9..+12 u from the navmesh (Q24).</summary>
    public const double Tolerance = 12;
    /// <summary>The walkable band a trigger must meet: the navmesh swept up from +0 to +128 u (a standing body).</summary>
    public const float BandLo = 0, BandHi = 128;

    public const string Outdoors = "outdoors: there is no room for another mod to take away";
    public const string DisabledAtLoad = "not there when the area loads";
    public const string SameNavmesh = "the navmesh that wins here is the one its own plugin was made with";
    public const string AuthorHadIt = "the plugin whose navmesh wins here has this object's plugin among its masters";
    public const string WithinReach = "within reach of walkable ground";
    public const string OutOfReachAlone = "out of reach with its own plugin's navmesh too";
    public const string RefusedNoForm = "a distance could not be measured, and the one measured does not meet the form";

    public static string? OutOfPopulation(bool interior, bool enabledAtLoad) => !interior ? Outdoors : !enabledAtLoad ? DisabledAtLoad : null;

    public static bool IsTrigger(string? baseType, bool hasModel, string? primitiveType) =>
        baseType == "Activator" && !hasModel && string.Equals(primitiveType, "Box", StringComparison.OrdinalIgnoreCase);

    /// <summary>True when the winning navmesh of a room equals its own view's: the same records, each from the same plugin.</summary>
    public static bool SameView(IReadOnlyDictionary<string, int> own, IReadOnlyDictionary<string, int> winning) =>
        own.Count == winning.Count && own.All(kv => winning.TryGetValue(kv.Key, out var p) && p == kv.Value);

    /// <summary>True when the plugin whose navmesh wins (its master closure given) had the object's plugin in the editor.</summary>
    public static bool AuthorHad(IReadOnlySet<int> navmeshPluginClosure, int placer) => navmeshPluginClosure.Contains(placer);

    /// <summary>True when two plugins are the same mod (their families, as the probe reads them, are equal).</summary>
    public static bool SameMod(string familyA, string familyB) => string.Equals(familyA, familyB, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether a primitive blocks, from its PrimitiveSolidity status: true (a body), false (not a body), null (not
    /// measured: a bound only).</summary>
    public static bool? PrimitiveBlocks(string solidityStatus) => solidityStatus switch
    {
        "certified" or "uncertified" => true,
        "no_geometry" => false,
        _ => null,
    };

    public const string Scripted = "scripted or quest object (demands remediation)";

    /// <summary>The class, from the base type and whether the reference carries a script (lane S C19; quest aliases and
    /// packages are not read).</summary>
    public static string ClassOf(string? baseType, string? baseEditorId, bool scripted)
    {
        if (scripted || baseType is "Door" or "Container" or "Activator" or "Book" or "Npc") return Scripted;
        if (baseType == "IdleMarker" || (baseType == "Furniture" && (baseEditorId ?? "").Contains("marker", StringComparison.OrdinalIgnoreCase))) return "AI marker";
        if (baseType == "Light") return "decoration (light)";
        if (baseType is "Static" or "MoveableStatic") return "decoration";
        return $"object ({baseType ?? "unknown type"})";
    }

    static double? D(NavDistance d) => d.Status == "measured" && d.Distance is { } x ? x : null;

    public static StrandedVerdict Decide(StrandedFacts f)
    {
        if (f.BodyUnread is { } body) return new(StrandedState.NotJudged, $"its shape could not be read: {body}");
        double R = f.Trigger ? 0 : Reach, T = Tolerance;
        double? dw = D(f.Winning), dow = D(f.Own);
        if (dw is null || dow is null)
        {
            string why = dw is null ? $"with the winning navmesh, {f.Winning.Reason}" : $"with its own plugin's navmesh, {f.Own.Reason}";
            if (f.Trigger) return new(StrandedState.NotJudged, $"its distance could not be measured: {why}");
            bool form = (dw is null || dw > R) && (dow is null || dow <= R);
            return form ? new(StrandedState.NotJudged, $"its distance could not be measured: {why}") : new(StrandedState.NoRow, RefusedNoForm);
        }
        if (!(dw > R)) return new(StrandedState.NoRow, WithinReach);
        if (!(dow <= R)) return new(StrandedState.NoRow, OutOfReachAlone);
        var c = f.Clearance;
        if (!c.AnyOther) return new(StrandedState.NotJudged, "no other mod's solid object stands in the room, so whether the room was taken away is not known");
        if (c.Nearest is { } near && near <= 0) return new(StrandedState.NotJudged, $"it touches {c.NearestName}, so it may still stand in that mod's room");
        if (c.UnreadNearest is { } un && (c.Nearest is null || un < c.Nearest))
            return new(StrandedState.NotJudged, $"the shape of {c.UnreadName} near it could not be settled, so whether it touches it is not known");
        if (dw - R <= T || (R > 0 && R - dow <= T))
            return new(StrandedState.NotJudged, $"within 12 u of the {(f.Trigger ? "walkable band" : "150 u reach")} (winning {dw:0.0} u, own {dow:0.0} u): closer than the navmesh can be told from the floor");
        return new(StrandedState.Reported, $"{dw:0.0} u from walkable ground, {dow:0.0} u with its own plugin's navmesh",
                   ClassOf(f.BaseType, f.BaseEditorId, f.Scripted));
    }
}
