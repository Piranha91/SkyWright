namespace SkyWright.Engine;

// STRANDED BY THE OTHER MOD (the geometry.stranded-object check; houseCARL lane S's rule, read as the placing plugin's own
// view by the 2026-11-26 Gate A review, ruling 1). STUB: the rule is not written yet; every reference reads not judged.

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
    public const double Reach = 150, Tolerance = 12;
    public const float BandLo = 0, BandHi = 128;

    public const string Outdoors = "outdoors: there is no room for another mod to take away";
    public const string DisabledAtLoad = "not there when the area loads";
    public const string SameNavmesh = "the navmesh that wins here is the one its own plugin was made with";
    public const string AuthorHadIt = "the plugin whose navmesh wins here has this object's plugin among its masters";

    /// <summary>Why a placed reference is outside the population (a no-row reason), or null when it is judged.</summary>
    public static string? OutOfPopulation(bool interior, bool enabledAtLoad) => null;

    public static bool IsTrigger(string? baseType, bool hasModel, string? primitiveType) => false;

    /// <summary>True when the winning navmesh of a room equals its own view's: the same records, each from the same plugin.</summary>
    public static bool SameView(IReadOnlyDictionary<string, int> own, IReadOnlyDictionary<string, int> winning) => false;

    /// <summary>True when the plugin whose navmesh wins (its master closure given) had the object's plugin in the editor.</summary>
    public static bool AuthorHad(IReadOnlySet<int> navmeshPluginClosure, int placer) => false;

    /// <summary>True when two plugins are the same mod (their families, as the probe reads them, are equal).</summary>
    public static bool SameMod(string familyA, string familyB) => false;

    /// <summary>Whether a primitive blocks, from its PrimitiveSolidity status: true (a body), false (not a body), null (not
    /// measured: a bound only).</summary>
    public static bool? PrimitiveBlocks(string solidityStatus) => null;

    public static string ClassOf(string? baseType, string? baseEditorId, bool scripted) => "the rule is not written yet";

    public static StrandedVerdict Decide(StrandedFacts f) => new(StrandedState.NotJudged, "the rule is not written yet");
}
