using Mutagen.Bethesda.Plugins;
using SkyRender;
using ViewProbe;

namespace Workbench;

/// <summary>Everything the view changes and a commit never sees: originals shown in place of edits, the ghost of each
/// edited reference's load-order place, hidden references and parts, see-through references and isolation. One
/// "Restore visibility" clears it. It never stages, unstages or writes anything.</summary>
internal sealed class ViewState
{
    internal const int SpaceKey = 0x20;

    internal ViewState(EditSession session, Func<IEnumerable<RefOcclusion>> drawn) { }

    /// <summary>Every edited reference is drawn where the load order puts it (the hold key or the Original button).</summary>
    internal bool ShowingOriginal => false;

    internal string ShowOriginal(bool on) => "";

    /// <summary>The window's key handler: Space down shows originals, up shows edits; a repeat is ignored. True when used.</summary>
    internal bool OnKey(int vk, bool down, bool repeat) => false;

    /// <summary>The view lost the keyboard: a held key's release will not arrive, so originals stop showing.</summary>
    internal void OnFocusLost() { }

    internal bool Ghost { get; set; }

    /// <summary>The ghost overlay's triangles: each edited reference at its load-order place, 6 floats a vertex (Y-up).</summary>
    internal List<float> GhostTriangles() => new();

    internal string Hide(FormKey key) => "";

    /// <summary>Hide one part (one drawn mesh) of a reference, such as a roof that is a shape of the building's model.</summary>
    internal string HidePart(PickShape shape) => "";

    /// <summary>Hide whatever the centre pick hits first, and pick again, until the subject is hit; terrain is named, not hidden.</summary>
    internal string HideInFront(FormKey subject, Func<PickHit?> pick) => "";

    /// <summary>Draw a reference as a see-through flat tint (its meshes hidden, its triangles tinted).</summary>
    internal string SeeThrough(FormKey key) => "";

    internal List<float> TintTriangles() => new();

    /// <summary>Hide every drawn reference but these.</summary>
    internal string Isolate(FormKey a, FormKey? b) => "";

    internal string Restore() => "";

    internal IReadOnlyCollection<FormKey> Hidden => Array.Empty<FormKey>();
    internal IReadOnlyCollection<FormKey> SeeThroughRefs => Array.Empty<FormKey>();
    internal int HiddenParts => 0;

    /// <summary>Anything view-only is in effect.</summary>
    internal bool Active => false;

    /// <summary>The Restore button's text, e.g. "Restore visibility (3 hidden, 12 see-through)"; empty when nothing is changed.</summary>
    internal string Label => "";
}
