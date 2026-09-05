using System.Numerics;
using CharacterViewer.Rendering;

namespace ViewProbe;

// THE BOS LAYER in the picture — what the renderer shows of the Base Object Swapper reader's answer.
//
// The scene is built with the EFFECTIVE bases (the mesh the game loads, at the transform BOS gives it)
// when the run is `--bos on` or `--bos diff`; `--bos off` builds the record bases as every render did
// before 2026-09-04. The two drawn modes share one scene and differ only in paint: `diff` tints every
// swapped ref TEAL so the eye can tell a swap from a record placement, `on` draws them plain. That is
// why the window can cycle on <-> diff live (the B key) but not to off — off is a different scene,
// and a run built without the layer says so rather than pretending to toggle it.
//
// The tint is the occlusion preview's mechanism (an emissive wash the mesh keeps its texture under),
// with its own saved state, so the two previews restore independently. A mesh both previews touch
// takes the last one applied; the caption says which are on.

static class BosPalette
{
    /// <summary>The tint a swapped ref wears in `diff`: teal, distinct from the occlusion preview's red wash and
    /// the highlight's orange rim.</summary>
    public static readonly Vector3 SwappedTint = new(0.10f, 0.90f, 0.85f);
}

/// <summary>The swapped refs' meshes and the mode they are painted in; built by the scene build from the same
/// per-ref mesh lists the occlusion preview keeps.</summary>
sealed class BosViewState
{
    public readonly List<RefOcclusion> Swapped = new();
    public int SwappedCount => Swapped.Count;
    public int RandomCount => Swapped.Count(r => r.Ref.BosRandom);
    /// <summary>What the scene was built with: off | on | diff. Off never changes; on and diff swap live.</summary>
    public string BuiltMode { get; }
    public string Mode { get; private set; }
    readonly Dictionary<GlMesh, (bool Emissive, bool GlowMap, bool Enabled, Vector3 Colour, float Multiple)> _saved = new();

    public BosViewState(string builtMode) { BuiltMode = builtMode; Mode = builtMode; }

    /// <summary>Paint for <paramref name="mode"/>. Returns false when the scene was built without the layer (nothing
    /// to show) or the mode is not one the built scene can take.</summary>
    public bool Apply(string mode)
    {
        if (BuiltMode == "off" || mode is not ("on" or "diff")) return false;
        Mode = mode;
        bool tint = mode == "diff";
        foreach (var r in Swapped) foreach (var m in r.Meshes) SetTint(m, tint);
        return true;
    }

    void SetTint(GlMesh m, bool on)
    {
        if (on)
        {
            if (!_saved.ContainsKey(m)) _saved[m] = (m.HasEmissive, m.HasGlowMap, m.EmissiveEnabled, m.EmissiveColor, m.EmissiveMultiple);
            m.HasEmissive = true; m.HasGlowMap = false; m.EmissiveEnabled = true;
            m.EmissiveColor = BosPalette.SwappedTint; m.EmissiveMultiple = 0.9f;
        }
        else if (_saved.Remove(m, out var g))
        {
            m.HasEmissive = g.Emissive; m.HasGlowMap = g.GlowMap; m.EmissiveEnabled = g.Enabled;
            m.EmissiveColor = g.Colour; m.EmissiveMultiple = g.Multiple;
        }
    }

    public string Summary() => BuiltMode == "off"
        ? "BOS off: record bases (rerun with --bos on|diff for the bases the game loads)"
        : $"BOS {Mode}: {SwappedCount:N0} drawn ref(s) carry a swapped base ({RandomCount:N0} random — the first alternative is drawn)"
          + (Mode == "diff" ? " — tinted TEAL" : "");
}
