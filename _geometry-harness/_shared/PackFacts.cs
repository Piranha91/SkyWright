using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace HarnessShared;

/// <summary>One package's POSITIONAL facts, flattened off the overlay (which is disposed with its
/// plugin). A package positions an actor through its location slots — a bag keyed by slot id whose
/// LocationTargetRadius carries a target arm and a radius. The arm is the discriminator this whole
/// filter turns on: LocationFallback with Type NearEditorLocation means "wherever this actor was
/// PLACED, plus radius"; every other arm means somewhere else entirely (a named reference, a cell,
/// a keyword, an object type — including the player, who moves).
/// <para>Shared extraction (moved out of driftprobe 2026-09-02): the scenario-consistency reach
/// filter and the navmesh needs-witness join read packages the same way, and the rule against
/// growing one probe with another's domain applies to the extraction as much as to the driver.
/// The DECISIONS that read these facts stay where they were: <c>DetectorModel.ClassifyReach</c>
/// and <c>DetectorModel.NeedsWitness</c>, both owned by proofprobe.</para></summary>
public sealed record PackFacts(string? Edid, FormKey Template, int ScheduleHour, int ScheduleMinutes,
                               int Conditions, IReadOnlyList<(string Arm, uint Radius, FormKey Link)> Locations)
{
    public bool Scheduled => ScheduleHour >= 0;
    public IEnumerable<uint> EditorRadii => Locations.Where(l => l.Arm == EditorArm).Select(l => l.Radius);
    public bool AnchorsElsewhere => Locations.Any(l => l.Arm != EditorArm && l.Arm != SelfArm);
    public const string EditorArm = "editor-location";
    public const string SelfArm = "where-it-stands";
    public const string RefArm = "reference";

    public static PackFacts From(IPackageGetter pk)
    {
        var locs = new List<(string, uint, FormKey)>();
        foreach (var d in pk.Data.Values.OfType<IPackageDataLocationGetter>())
        {
            if (d.Location is not { } ltr || ltr.Target is not { } tgt) continue;
            locs.Add((tgt switch
            {
                // NearSelf and NearPackageStart do not RELOCATE an actor — they keep it where it
                // already is — so for "does the placement predict position" they read the same as
                // the editor location. Every other fallback type names something else entirely.
                ILocationFallbackGetter f => f.Type switch
                {
                    LocationTargetRadius.LocationType.NearEditorLocation => EditorArm,
                    LocationTargetRadius.LocationType.NearSelf => SelfArm,
                    LocationTargetRadius.LocationType.NearPackageStart => SelfArm,
                    _ => "fallback:" + f.Type,
                },
                ILocationTargetGetter => RefArm,
                ILocationCellGetter => "cell",
                ILocationKeywordGetter => "keyword",
                ILocationObjectIdGetter => "object-id",
                ILocationObjectTypeGetter => "object-type",
                _ => "arm:" + tgt.GetType().Name.Replace("BinaryOverlay", ""),
            }, ltr.Radius, tgt is ILocationTargetGetter lt ? lt.Link.FormKey
                         : tgt is ILocationCellGetter lc ? lc.Link.FormKey
                         : tgt is ILocationKeywordGetter lk ? lk.Link.FormKey
                         : tgt is ILocationObjectIdGetter lo ? lo.Link.FormKey : FormKey.Null));
        }
        return new PackFacts(pk.EditorID, pk.PackageTemplate.FormKey, pk.ScheduleHour,
                             pk.ScheduleDurationInMinutes, pk.Conditions.Count, locs);
    }
}
