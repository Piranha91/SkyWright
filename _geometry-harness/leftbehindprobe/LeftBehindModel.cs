using System.Numerics;
using HousecarlCore;
using Mutagen.Bethesda.Plugins;

namespace LeftBehind;

// THE DATA the left-behind rule reads and writes. No decisions here: the arm (LeftBehindArm.cs) answers what stands
// where in one order, the rule (LeftBehindRule.cs) measures and classifies. Class and cause names are the output
// contract's spellings and are written exactly as below.

/// <summary>The class names, spelled as the output contract spells them.</summary>
public static class PanelClass
{
    public const string Unpaired = "UNPAIRED", LeftBehind = "LEFT-BEHIND", Partial = "PARTIAL",
                        ReBacked = "RE-BACKED", Intact = "INTACT", CannotAssess = "CANNOT-ASSESS";
    public static readonly string[] All = { Unpaired, LeftBehind, Partial, ReBacked, Intact, CannotAssess };
}

/// <summary>What became of a backing reference (or the terrain) in the city arm, spelled as the contract spells it.
/// <see cref="MeshReplaced"/> was admitted to the contract at Gate L1 (2026-09-26).</summary>
public static class PartnerCause
{
    public const string Unchanged = "unchanged", Disabled = "disabled", ParentDisabled = "parent-disabled",
                        Moved = "moved", BaseSwapped = "base-swapped", Deleted = "deleted",
                        UnknownEnable = "unknown-enable", Terrain = "terrain", MeshReplaced = "mesh-replaced";
}

/// <summary>The four thresholds. <see cref="DeltaU"/>: how close (world units) a surface must be to a sample for the
/// sample to be backed. The taus are shares (0..1).</summary>
public readonly record struct Thresholds(float DeltaU, float TauPair, float TauLost, float TauMinor)
{
    public static readonly Thresholds Proposed = new(64f, 0.5f, 0.5f, 0.2f);
}

/// <summary>One occlusion panel as an arm's winning record places it. <paramref name="Type"/> is the XPRM type name as
/// the record states it ("Box", "PortalBox", "Plane", ...). <paramref name="ExtentFull"/> is the XPRM bounds times the
/// reference scale: FULL extents, centred on the position (core's PlacedPrimitiveInfo convention).</summary>
public sealed record Panel(FormKey Key, FormKey WorldspaceKey, string Worldspace, string Type,
                           Vector3 Position, Vector3 RotationRad, Vector3 ExtentFull);

/// <summary>A reference's state in one arm: null from <see cref="LeftBehindArm.StateOf"/> means the order never places
/// it. <paramref name="Enable"/> is the load-time chain (EnableState via EnableArms), never the record's own flag alone.</summary>
public sealed record RefState(FormKey Key, FormKey Base, bool Deleted, EnableResolution Enable, bool OwnFlagDisabled,
                              Vec3 Position, Vec3 Rotation, float Scale)
{
    /// <summary>Present at load: placed, not deleted, and the enable chain resolves enabled.</summary>
    public bool Present => !Deleted && Enable.Resolved && Enable.Enabled;
    /// <summary>The chain could not be read to the end — neither present nor absent.</summary>
    public bool Unknown => !Deleted && !Enable.Resolved;
}

/// <summary>One base's geometry as this probe uses it, in MODEL space. <paramref name="Surface"/>: every triangle the
/// distance term measures against (render and solid collision together). <paramref name="ClosedSolid"/>: a closed solid
/// the inside term may use (a closed collision soup, or an OBND box), null when there is none. <paramref name="Src"/>:
/// 'm' mesh (render + collision), 'r' render only, 'c' collision only, 'o' OBND box fallback. <paramref name="SourceId"/>:
/// where the mesh came from in THIS arm's asset view, so a replaced mesh is visible as a difference between arms.</summary>
public sealed record ArmGeometry(TriangleSoup Surface, TriangleSoup? ClosedSolid, char Src, string SourceId);

/// <summary>One thing that could back a sample in one arm: a reference, or the terrain (Ref = null).
/// <paramref name="Distance"/> 0 when the sample is inside a closed solid. <paramref name="Unknown"/>: the reference's
/// enable chain is unresolved in this arm.</summary>
public readonly record struct Backer(FormKey? Ref, float Distance, bool Unknown);

/// <summary>What the rule measured for one panel: per sample, per arm, every backer within the largest delta.</summary>
public sealed class PanelMeasure
{
    public required Panel Panel { get; init; }
    public required int SampleCount { get; init; }
    /// <summary>Area (u2, a PortalBox's mid-quad) or volume (u3, a Box) one sample stands for.</summary>
    public required float SampleWeight { get; init; }
    public required string WeightUnit { get; init; }
    public required List<Backer>[] BaseBackers { get; init; }
    public required List<Backer>[] CityBackers { get; init; }
    /// <summary>Set when the panel cannot be measured at all (shape not modelled, panel absent or changed).</summary>
    public string? Refusal { get; init; }
    /// <summary>A changed reference near the panel whose geometry could not be read — a loss the rule cannot see.</summary>
    public List<FormKey> BlindChanged { get; } = new();
    public string Terrain { get; set; } = "not compared (unchanged by the city plugin)";
}

/// <summary>One backing partner of a classified panel.
/// <paramref name="EditorId"/> is the BASE record's editor id. <paramref name="Geometry"/>: "mesh", "collision", "obnd",
/// or "terrain" for the LAND partner.</summary>
public sealed record Partner(string Ref, string? Base, string? EditorId, float ShareOfBacking, string Cause, float MoveU,
                             string? ReplacedBy, string Geometry);

/// <summary>A classified panel.</summary>
public sealed record PanelVerdict(Panel Panel, string Class, string? Reason, float BackingBase, float BackingCity,
                                  float LostShare, float LostArea, string LostAreaUnit, IReadOnlyList<Partner> Partners,
                                  string Terrain, int UnknownBackers);
