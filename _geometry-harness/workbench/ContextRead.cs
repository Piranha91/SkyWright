using System.Numerics;
using HarnessShared;
using HousecarlCore;
using Mutagen.Bethesda.Plugins;
using ViewProbe;

namespace Workbench;

/// <summary>The server's answers item 5 composes, each from its owner: doors near a reference, what names it, a door's
/// other side, a reference's base record type and enable parent, and the commit's placement check (F4).</summary>
internal interface IContextSource
{
    /// <summary>Doors (references whose record has a TeleportDestination) within <paramref name="within"/> units of the reference.</summary>
    Task<IReadOnlyList<string>> DoorsNearAsync(FormKey key, double within);
    /// <summary>Every record that names the reference, with the field that names it.</summary>
    Task<IReadOnlyList<(string Key, string Field)>> NamesAsync(FormKey key);
    /// <summary>A door's other side: the far door, its cell, and the arrival point there.</summary>
    Task<(string? FarDoor, string? FarCell, Vec3? Arrival)> DestinationAsync(string door);
    /// <summary>A reference's base record type, as the server's summary names it.</summary>
    Task<string?> BaseTypeAsync(string key);
    /// <summary>The reference's own enable parent, if it has one.</summary>
    Task<string?> EnableParentAsync(FormKey key);
    /// <summary>Why the server's record for the reference is not the one drawn (F4), or null when it is.</summary>
    Task<string?> MismatchAsync(FormKey key);
}

internal sealed record DoorFact(string Key, string Label, string? FarDoor, string? FarCell, Vec3? Arrival);
internal sealed record NameFact(string Key, string Field);
internal sealed record RestFact(string Key, DependentMotion Motion, string Sentence);
internal sealed record NavFact(string Navmesh, int Triangles);

/// <summary>What item 5 shows for one reference. A section that could not be read, or found nothing, says so in
/// <see cref="Sections"/>. <see cref="Grids"/> and <see cref="Rays"/> count what the geometry half cost, so a test pins it.</summary>
internal sealed record ContextAnswer(string Key, IReadOnlyList<DoorFact> Doors, IReadOnlyList<NameFact> Names, string? EnableParent,
                                     IReadOnlyList<string> Touches, IReadOnlyList<RestFact> Resting, IReadOnlyList<NavFact> Navmesh,
                                     IReadOnlyDictionary<string, string> Sections, string Statement,
                                     long Grids = 0, long Rays = 0);

/// <summary>Item 5: the context of a selected reference, composed from the server, the live scene's contact owner, the motion
/// rule and the scene's navmesh triangles. Read off the UI thread; shown, never judged.</summary>
internal static class ContextRead
{
    /// <summary>The search radius for doors; contact decides the label.</summary>
    internal const double DoorRadius = 256;

    internal static Task<ContextAnswer> ReadAsync(IContextSource src, CaptureViews.Geometry g, string key,
        IEnumerable<(FormKey Key, int Tri, Vector3 A, Vector3 B, Vector3 C)> navTris,
        Func<string, IEnumerable<(string Layer, string MotionSystem)>?> bodiesOf, CancellationToken ct = default)
        => Task.FromResult(new ContextAnswer(key, Array.Empty<DoorFact>(), Array.Empty<NameFact>(), null, Array.Empty<string>(),
            Array.Empty<RestFact>(), Array.Empty<NavFact>(), new Dictionary<string, string>(), ""));

    /// <summary>The navmesh triangles under the reference's axis-aligned bounds (XY, any height), counted by navmesh.</summary>
    internal static IReadOnlyList<NavFact> NavUnder(IEnumerable<(FormKey Key, int Tri, Vector3 A, Vector3 B, Vector3 C)> navTris, Aabb box)
        => Array.Empty<NavFact>();

    /// <summary>The commit's sentence: what moves with the reference and what stays behind.</summary>
    internal static string StaysBehind(ContextAnswer a, IReadOnlyCollection<string> movingWith) => "";
}
