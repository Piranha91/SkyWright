using Mutagen.Bethesda.Plugins;
using ViewProbe;

namespace Workbench;

/// <summary>A staged edit as saved: the reference and its pose before and after.</summary>
internal sealed record SavedEdit(string Ref, RefPlacementEdit.Pose Before, RefPlacementEdit.Pose After);

/// <summary>A written edit as saved: the reference and the patch (and its folder) that holds it.</summary>
internal sealed record SavedWritten(string Ref, string Patch, string PatchFolder);

/// <summary>One review's session: selection, open row, camera, staged and written edits.</summary>
internal sealed record SessionData(string PlanId, string Arm, string? Selected, int? OpenConflict, float[]? Camera,
                                   IReadOnlyList<SavedEdit> Staged, IReadOnlyList<SavedWritten> Written);

/// <summary>The session file under the Work Folder, written on every change and read when the same bundle is opened.</summary>
internal static class SessionFile
{
    /// <summary>The session file for a bundle and arm, or one sentence when there is no Work Folder to keep it in.</summary>
    internal static (string? Path, string? Refusal) PathFor(string? workFolder, string planId, string arm) => (null, null);

    internal static void Write(string path, SessionData data) { }

    internal static SessionData? Read(string path) => null;

    /// <summary>Re-stage the saved edits whose reference still sits where it did, redraw written ones from their patch,
    /// and say, one sentence each, what was not restored.</summary>
    internal static IReadOnlyList<string> Restore(EditSession session, SessionData data,
                                                  Func<FormKey, SavedWritten, RefPlacementEdit.Pose?> readWritten) => Array.Empty<string>();
}
