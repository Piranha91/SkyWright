using HousecarlCore;

namespace SkyWright.Engine;

/// <summary>Whether a render shape is drawn in game: the one predicate the pair measure, the light reader and the alpha reader
/// share. A hidden shape, a particle system or an unreadable shape is not drawn; neither is editor-marker geometry (a shape named
/// <c>EditorMarker</c>, which the game culls and the renderer skips; confirmed on WRSingleTrigger02.nif in the 2026-10-13 confirmatory
/// launch frames, runs/2026-10-14-g1-cannot-tell/GATE-B.md §4).</summary>
public static class ShapeDrawing
{
    /// <summary>The shape name the editor-marker rule matches (ordinal, ignoring case).</summary>
    public const string EditorMarkerName = "EditorMarker";

    /// <summary>True when a shape of this name is editor-marker geometry.</summary>
    public static bool IsEditorMarker(string? name) => string.Equals(name, EditorMarkerName, StringComparison.OrdinalIgnoreCase);

    /// <summary>Is a render shape drawn in game?</summary>
    public static bool Drawn(string? name, bool hidden, bool particle, bool unread) => !hidden && !particle && !unread && !IsEditorMarker(name);
}
