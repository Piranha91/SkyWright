using System.Numerics;
using CharacterViewer.Rendering;

namespace ViewProbe;

// THE WORLD STATE in the picture — what the renderer shows of a switch flipped (roadmap 4.11, --state).
//
// The scene is built for the STATE named on the command line: every ref's existence is its runtime
// enable state (EnableState, the R1 rule) with the named switches flipped from their load-time state
// (_shared/WorldState.cs, StateArm.Wrap). `--state-view on` draws that state and nothing else; `diff`
// draws the state AND the refs the flip took out, so the eye can tell what arrived (tinted GREEN) from
// what departed (tinted RED, still drawn where it stood). The two views share one scene when the scene
// was built `diff` (the departed refs are resident); the window can switch on <-> diff live (the T key)
// by hiding and showing them. A scene built `on` holds no departed ref and says so rather than
// pretending to toggle.
//
// The tint is the occlusion preview's mechanism (an emissive wash the mesh keeps its texture under)
// with its own saved state, as the BOS paint is (BosView.cs). A mesh two paints touch takes the last
// one applied; the caption says which are on. Hiding writes the same visibility flag the height clip
// and the occlusion preview write (PickShape.StateHidden folds into the clip's decision), so no knife
// can undo another.

static class StatePalette
{
    public static readonly Vector3 ArrivedTint = new(0.20f, 0.95f, 0.30f);
    public static readonly Vector3 DepartedTint = new(0.95f, 0.25f, 0.20f);
}

/// <summary>The refs the flip changed, with their meshes, and the view they are painted in.</summary>
sealed class StateViewState
{
    public readonly List<RefOcclusion> Arrived = new();
    public readonly List<RefOcclusion> Departed = new();
    public int Count => Arrived.Count + Departed.Count;
    /// <summary>What the scene was built with: on (no departed refs resident) | diff (departed refs resident, hidden in on).</summary>
    public string BuiltMode { get; }
    public string Mode { get; private set; }
    public string StateName { get; }
    readonly Dictionary<GlMesh, (bool Emissive, bool GlowMap, bool Enabled, Vector3 Colour, float Multiple)> _saved = new();

    public StateViewState(string builtMode, string stateName) { BuiltMode = builtMode; Mode = builtMode; StateName = stateName; }

    /// <summary>Paint for <paramref name="mode"/>: diff tints arrivals green and shows departures tinted red; on shows
    /// arrivals plain and hides departures. False when there is nothing to show or the mode is not one this scene takes.</summary>
    public bool Apply(string mode)
    {
        if (Count == 0 || mode is not ("on" or "diff")) return false;
        if (mode == "diff" && BuiltMode == "on") return false;          // no departed ref is resident
        Mode = mode;
        bool diff = mode == "diff";
        foreach (var r in Arrived) foreach (var m in r.Meshes) SetTint(m, diff ? StatePalette.ArrivedTint : null);
        foreach (var r in Departed)
        {
            foreach (var m in r.Meshes) { SetTint(m, diff ? StatePalette.DepartedTint : null); m.IsRendering = diff; }
            foreach (var s in r.Shapes) s.StateHidden = !diff;
        }
        return true;
    }

    void SetTint(GlMesh m, Vector3? colour)
    {
        if (colour is { } c)
        {
            if (!_saved.ContainsKey(m)) _saved[m] = (m.HasEmissive, m.HasGlowMap, m.EmissiveEnabled, m.EmissiveColor, m.EmissiveMultiple);
            m.HasEmissive = true; m.HasGlowMap = false; m.EmissiveEnabled = true;
            m.EmissiveColor = c; m.EmissiveMultiple = 0.9f;
        }
        else if (_saved.Remove(m, out var g))
        {
            m.HasEmissive = g.Emissive; m.HasGlowMap = g.GlowMap; m.EmissiveEnabled = g.Enabled;
            m.EmissiveColor = g.Colour; m.EmissiveMultiple = g.Multiple;
        }
    }

    public string Summary() => Count == 0
        ? $"STATE {StateName}: no drawn ref changes with this flip"
        : $"STATE {StateName} {Mode}: {Arrived.Count:N0} drawn ref(s) arrive with the flip{(Mode == "diff" ? " (GREEN)" : "")}, {Departed.Count:N0} depart{(Mode == "diff" ? " (drawn RED)" : BuiltMode == "diff" ? " (hidden)" : " (not drawn)")}";
}
