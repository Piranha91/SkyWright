using System.Numerics;
using Mutagen.Bethesda.Plugins;
using SkyRender;
using ViewProbe;

namespace Workbench;

/// <summary>Everything the view changes and a commit never sees: originals shown in place of edits, the ghost of each
/// edited reference's load-order place, hidden references and parts, see-through references and isolation. One
/// "Restore visibility" clears it. It never stages, unstages or writes anything: it hides meshes through SkyRender's
/// host switch (<see cref="GlMesh.IsHiddenByHost"/>, which viewprobe's own culls do not reset) and re-places edited
/// references only through <see cref="EditSession.Preview"/>.</summary>
internal sealed class ViewState
{
    internal const int SpaceKey = 0x20;

    readonly EditSession _session;
    readonly Func<IEnumerable<RefOcclusion>> _drawn;
    readonly HashSet<FormKey> _hidden = new();
    readonly HashSet<GlMesh> _hiddenParts = new();
    readonly List<FormKey> _seeThrough = new();
    readonly HashSet<GlMesh> _meshesHidden = new();          // every mesh this state switched off, so restore switches back only those
    List<float>? _tint;

    /// <summary>Bumped on every change the overlays draw from, so a drawer rebuilds only when it changed.</summary>
    internal int Version { get; private set; }
    internal event Action<string>? Changed;

    readonly Gizmo? _handle;

    internal ViewState(EditSession session, Func<IEnumerable<RefOcclusion>> drawn, Gizmo? handle = null)
    {
        _session = session; _drawn = drawn; _handle = handle;
        // One rule for every edit path: before anything re-places a reference, the edits go back on screen.
        _session.Replacing += () => { if (ShowingOriginal) ShowOriginal(false); };
        // an edit moves a see-through reference's tint and can add a ghost
        _session.Changed += _ => { _tint = null; Version++; };
    }

    void Say(string s) { Version++; Changed?.Invoke(s); }

    /// <summary>Every edited reference is drawn where the load order puts it (the hold key or the Original button).</summary>
    internal bool ShowingOriginal { get; private set; }

    internal string ShowOriginal(bool on)
    {
        var edited = _session.Edited();
        ShowingOriginal = on;
        foreach (var k in edited)
            _session.Preview(k, on ? RefPlacementEdit.BakedPose(_session.RefOf(k).Ref) : _session.Current(k));
        var said = edited.Count == 0 ? (on ? "nothing is edited, so the originals are what the view shows" : "showing the edits")
                 : on ? $"showing {edited.Count} edited reference(s) where the load order places them; release to see the edits"
                      : $"showing the edits of {edited.Count} reference(s)";
        Say(said);
        return said;
    }

    /// <summary>The window's key handler: Space down shows originals, up shows edits; a repeat is ignored. True when used.</summary>
    internal bool OnKey(int vk, bool down, bool repeat)
    {
        if (vk != SpaceKey) return false;
        if (down) { if (!repeat && !ShowingOriginal) ShowOriginal(true); }
        else if (ShowingOriginal) ShowOriginal(false);
        return true;
    }

    /// <summary>The view lost the keyboard: a held key's release will not arrive, so originals stop showing.</summary>
    internal void OnFocusLost() { if (ShowingOriginal) ShowOriginal(false); }

    bool _ghost;
    internal bool Ghost { get => _ghost; set { _ghost = value; Say(value ? "ghost of the load-order place shown" : "ghost hidden"); } }

    static void AddTriangles(List<float> into, IReadOnlyList<Vector3> v, int[] tris)
    {
        for (int t = 0; t + 2 < tris.Length; t += 3)
        {
            Vector3 a = v[tris[t]], b = v[tris[t + 1]], c = v[tris[t + 2]];
            var n = Vector3.Cross(b - a, c - a);
            n = n.LengthSquared() > 0 ? Vector3.Normalize(n) : Vector3.UnitY;
            foreach (var p in new[] { a, b, c }) { into.Add(p.X); into.Add(p.Y); into.Add(p.Z); into.Add(n.X); into.Add(n.Y); into.Add(n.Z); }
        }
    }

    /// <summary>The ghost overlay's triangles: each edited reference at its load-order place, 6 floats a vertex (Y-up),
    /// from the copy of the build's vertices the placement edit keeps.</summary>
    internal List<float> GhostTriangles()
    {
        var list = new List<float>();
        if (!_ghost) return list;
        foreach (var k in _session.Edited())
            foreach (var s in _session.RefOf(k).Shapes) AddTriangles(list, RefPlacementEdit.BakedVerts(s), s.Tris);
        return list;
    }

    void HideMeshes(IEnumerable<GlMesh> meshes) { foreach (var m in meshes) { if (!m.IsHiddenByHost) { m.IsHiddenByHost = true; _meshesHidden.Add(m); } } }

    RefOcclusion Drawn(FormKey key) => _session.IsDrawn(key) ? _session.RefOf(key)
        : _drawn().FirstOrDefault(r => r.Ref.RefKey == key && r.Meshes.Count > 0) ?? throw new InvalidOperationException($"{key} is not a drawn reference in this scene, so there is nothing of it to hide.");

    internal string Hide(FormKey key)
    {
        var ro = Drawn(key);
        HideMeshes(ro.Meshes);
        _hidden.Add(key);
        var said = $"hid {key} ({ro.Ref.BaseEditorId ?? "no editor id"}); the view only, nothing is staged";
        Say(said);
        return said;
    }

    /// <summary>Hide one part (one drawn mesh) of a reference, such as a roof that is a shape of the building's model.</summary>
    internal string HidePart(PickShape shape)
    {
        if (shape.Mesh is not { } m) throw new InvalidOperationException("that part has no drawn mesh to hide.");
        HideMeshes(new[] { m });
        _hiddenParts.Add(m);
        var said = $"hid one part of {shape.Ref.RefKey}; the rest of it stays drawn";
        Say(said);
        return said;
    }

    /// <summary>Hide whatever the centre pick hits first, and pick again, until the subject is hit; terrain is named,
    /// not hidden. When the ray never reaches the subject, what this call hid is shown again and that is said.</summary>
    internal string HideInFront(FormKey subject, Func<PickHit?> pick)
    {
        var hidNow = new List<FormKey>();
        for (int i = 0; i < 256; i++)
        {
            var hit = pick();
            if (hit is null)
            {
                foreach (var k in hidNow) { foreach (var m in Drawn(k).Meshes) { m.IsHiddenByHost = false; _meshesHidden.Remove(m); } _hidden.Remove(k); }
                var miss = $"the centre ray does not reach {subject}, so nothing was hidden; point the view at it first";
                Say(miss);
                return miss;
            }
            if (hit.Shape.Kind == PickKind.Terrain)
            {
                var t = $"terrain is in front of {subject}, and terrain is not hidden" + (hidNow.Count > 0 ? $"; hid {Count(hidNow.Count)} before it" : "")
                        + "; move the camera above the ground";
                Say(t);
                return t;
            }
            var key = hit.Shape.Ref.RefKey;
            if (key == subject)
            {
                var ok = hidNow.Count == 0 ? $"nothing is in front of {subject}" : $"hid {Count(hidNow.Count)} in front of {subject}";
                Say(ok);
                return ok;
            }
            Hide(key);
            hidNow.Add(key);
        }
        return $"hid {Count(hidNow.Count)} and still did not reach {subject}";
    }

    static string Count(int n) => n == 1 ? "1 reference" : $"{n} references";

    /// <summary>Draw references as a see-through flat tint: their meshes hidden, their triangles tinted where drawn.</summary>
    internal string SeeThrough(IReadOnlyCollection<FormKey> keys, string? what = null)
    {
        int n = 0;
        foreach (var k in keys)
        {
            var ro = Drawn(k);
            HideMeshes(ro.Meshes);
            if (!_seeThrough.Contains(k)) _seeThrough.Add(k);
            n++;
        }
        _tint = null;
        var said = $"drew {Count(n)}{(what is null ? "" : " of " + what)} see-through, as a flat tint (textures are not shown)";
        Say(said);
        return said;
    }

    internal string SeeThrough(FormKey key) => SeeThrough(new[] { key });

    /// <summary>The see-through overlay's triangles at the references' current placements, built once per change.</summary>
    internal List<float> TintTriangles()
    {
        if (_tint is not null) return _tint;
        var list = new List<float>();
        foreach (var k in _seeThrough)
            foreach (var s in Drawn(k).Shapes) AddTriangles(list, s.Verts, s.Tris);
        return _tint = list;
    }

    /// <summary>Hide every drawn reference but these.</summary>
    internal string Isolate(FormKey a, FormKey? b)
    {
        int n = 0;
        foreach (var ro in _drawn())
        {
            var k = ro.Ref.RefKey;
            if (k == a || k == b || ro.Meshes.Count == 0 || k.IsNull) continue;
            HideMeshes(ro.Meshes);
            if (_hidden.Add(k)) n++;
        }
        var said = $"isolated {a}{(b is { } bb ? " and " + bb : "")}: hid {Count(n)} around them";
        Say(said);
        return said;
    }

    internal string Restore()
    {
        foreach (var m in _meshesHidden) m.IsHiddenByHost = false;
        int n = _hidden.Count + _seeThrough.Count + _hiddenParts.Count;
        _meshesHidden.Clear(); _hidden.Clear(); _hiddenParts.Clear(); _seeThrough.Clear(); _tint = null;
        var said = n == 0 ? "nothing was hidden or see-through" : "visibility restored: everything is drawn as the scene built it";
        Say(said);
        return said;
    }

    internal IReadOnlyCollection<FormKey> Hidden => _hidden;
    internal IReadOnlyCollection<FormKey> SeeThroughRefs => _seeThrough;
    internal int HiddenParts => _hiddenParts.Count;

    /// <summary>Anything view-only is in effect.</summary>
    internal bool Active => _hidden.Count > 0 || _hiddenParts.Count > 0 || _seeThrough.Count > 0;

    /// <summary>The Restore button's text, e.g. "Restore visibility (3 hidden, 12 see-through)"; empty when nothing is changed.</summary>
    internal string Label
    {
        get
        {
            if (!Active) return "";
            var bits = new List<string>();
            if (_hidden.Count > 0) bits.Add($"{_hidden.Count} hidden");
            if (_hiddenParts.Count > 0) bits.Add($"{_hiddenParts.Count} part{(_hiddenParts.Count == 1 ? "" : "s")} hidden");
            if (_seeThrough.Count > 0) bits.Add($"{_seeThrough.Count} see-through");
            return $"Restore visibility ({string.Join(", ", bits)})";
        }
    }
}

/// <summary>Draws the view state's two tints: the ghost of each edited reference's load-order place (yellow) and the
/// see-through references (blue), depth-tested, by SkyRender's translucent overlay tint.</summary>
internal sealed class ViewTints : IRenderOverlay
{
    internal ViewState? State;
    int _version = -1;
    List<float> _ghost = new(), _tint = new();
    static readonly OpenTK.Mathematics.Vector3 GhostColour = new(1f, 0.85f, 0.2f), TintColour = new(0.35f, 0.7f, 1f);

    public void Draw(GlRenderer renderer, in OverlayContext context)
    {
        if (State is not { } s) return;
        if (s.Version != _version) { _ghost = s.GhostTriangles(); _tint = s.TintTriangles(); _version = s.Version; }
        if (_ghost.Count < 18 && _tint.Count < 18) return;
        if (!renderer.BeginOverlaySurfacePass(context, out var st)) return;
        var none = Array.Empty<(int, int, OpenTK.Mathematics.Vector3)>();
        if (_tint.Count >= 18) renderer.DrawOverlayTranslucentTint(_tint, TintColour, 0.3f, none);
        if (_ghost.Count >= 18) renderer.DrawOverlayTranslucentTint(_ghost, GhostColour, 0.35f, none);
        renderer.EndOverlaySurfacePass(st);
    }
}
