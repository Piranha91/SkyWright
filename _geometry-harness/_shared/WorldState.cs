using System.Numerics;
using HousecarlCore;
using Mutagen.Bethesda.Plugins;

namespace HarnessShared;

// WORLD STATE — the enable-marker SWITCHES, their sets, and the arm that flips them (roadmap 4.11,
// handoff 2026-09-20).
//
// The suite judges the load-time state: EnableState resolves each reference's initially-disabled flag
// and its XESP parent chain, and every detector reads that. Nothing modelled a later flip — a quest
// stage, a scene, a trigger volume calling Enable() or Disable() on a marker whose children then swap
// in one instant. This file is the records-side model of that:
//
//   - a SWITCH is any reference that is the winning XESP parent of at least one other reference (the
//     player, ID 0x14, is never a switch: parent = player + opposite is the permanently-disabled idiom).
//     Its children split into the ON-SET (opposite flag clear: same state as the parent) and the
//     OFF-SET (opposite flag set). A child that is itself a switch chains; descendants carry the XOR of
//     the flags along the path (PARITY). A cycle is an authoring error, counted and not walked;
//   - the BASELINE is EnableState over the winner (the R1 rule the contact pass and viewprobe share);
//   - a FLIP of switch S sets S's own runtime state to the opposite of its baseline and cuts S's own
//     parent link for the arm — Enable()/Disable() on the marker itself — and every descendant follows
//     through the unchanged chain rule. A joint flip applies several at once (a quest stage). Nothing
//     else changes: no placement, no base, no navmesh;
//   - a NAVCUT is a placed object whose XTRI collision layer is a COLL record flagged NavmeshObstacle.
//     Its obstacle is its XPRM box (full extents about the reference) at the reference's transform.
//
// Everything here is bookkeeping over OverlapIndex (the history, the XESP change log, the collision
// layers and primitives it now carries). The DECISION about a row across a flip is
// DetectorModel.StateDifferential, owned by proofprobe; the class driver is stateprobe, the census
// switchprobe, and every probe with a --state flag parses it through StateArm.

/// <summary>One reference under a switch. <paramref name="Opposite"/> is the child's own XESP flag; <paramref name="Parity"/>
/// is the flag XOR'd along the chain from the ROOT switch (false = on-set of the root: it takes the root's state;
/// true = off-set: it takes the opposite). <paramref name="Depth"/> is 1 for a direct child. <paramref name="Placer"/> is
/// the plugin that defined the reference, <paramref name="Attacher"/> the plugin that last set its XESP.</summary>
public sealed record SetMember(FormKey Key, bool Opposite, bool Parity, int Depth, int Placer, int Attacher,
                               FormKey Cell, FormKey Space, bool Navcut);

/// <summary>One switch: the marker, who placed it, where, its baseline state, its own parent (a chained switch), and
/// its sets. <paramref name="Children"/> are the direct children; <paramref name="Descendants"/> the transitive closure
/// with parity (the direct children included, depth 1).</summary>
public sealed class SwitchInfo
{
    public FormKey Key;
    public string? EditorId;
    public string? BaseEditorId;
    public string? BaseType;
    public bool BaseIsMarker;
    public int Owner;
    public FormKey Cell;
    public FormKey Space;
    public bool Interior;
    public Vec3 Position;
    public EnableResolution Baseline;
    public FormKey? Parent;
    public bool ParentOpposite;
    public readonly List<SetMember> Children = new();
    public readonly List<SetMember> Descendants = new();
    public bool ChainCycle;

    public IEnumerable<SetMember> OnSet => Descendants.Where(m => !m.Parity);
    public IEnumerable<SetMember> OffSet => Descendants.Where(m => m.Parity);
    public int OnCount => Descendants.Count(m => !m.Parity);
    public int OffCount => Descendants.Count(m => m.Parity);
    public int NavcutCount => Descendants.Count(m => m.Navcut);
    /// <summary>The cells the sets touch (the switch's own cell included when a member stands there).</summary>
    public IEnumerable<FormKey> Cells => Descendants.Select(m => m.Cell).Distinct();
    public string Label => EditorId ?? Key.ToString();
}

/// <summary>The census: every switch in an ingested OverlapIndex, with the counters the handoff asks for.</summary>
public sealed class SwitchIndex
{
    public OverlapIndex Index { get; }
    public Dictionary<FormKey, SwitchInfo> Switches { get; } = new();
    /// <summary>child -> (winning parent, opposite flag), for every child whose winning parent is a placed, non-player
    /// reference. What a state arm walks.</summary>
    public Dictionary<FormKey, (FormKey Parent, bool Opposite)> ParentOf { get; } = new();
    public Dictionary<string, FormKey> ByEditorId { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Every navcut reference under the winner (XTRI on a NavmeshObstacle layer), whether or not it is under a
    /// switch; <see cref="NavcutUnderSwitch"/> is the subset that is.</summary>
    public HashSet<FormKey> Navcuts { get; } = new();
    public HashSet<FormKey> NavcutUnderSwitch { get; } = new();
    /// <summary>Navcut references whose primitive is missing — counted, never measured.</summary>
    public int NavcutsWithoutPrimitive;
    /// <summary>References whose winning XESP names the player (the permanently-disabled idiom) — never a switch outcome.</summary>
    public int PlayerParentRefs;
    /// <summary>References whose winning XESP names a reference nobody places (or a deleted one) — counted, not a switch.</summary>
    public int UnplacedParentRefs;
    /// <summary>Initially-disabled references with NO parent: disabled until a script names them — an Unknown trigger on
    /// a singleton set, counted apart (the handoff's "what NOT to do").</summary>
    public int DisabledSingletons;
    public int ChainCycles;
    readonly Dictionary<FormKey, EnableResolution> _baseline = new();

    SwitchIndex(OverlapIndex idx) => Index = idx;

    /// <summary>The winner's enable facts for one reference — the same reading ContactIndex.WinnerEnable makes: the
    /// winning snap's flag and the last XESP change. Null for a reference the order does not place or has deleted.</summary>
    public EnableFacts? WinnerFacts(FormKey k)
    {
        var s = Index.History.Winning(k);
        if (s is null || s.Value.Deleted || Index.IsDeletedNow(k)) return null;
        var (p, o) = LastEnableChange(k);
        return new EnableFacts(s.Value.Disabled, p, o);
    }

    (FormKey? Parent, bool Opposite) LastEnableChange(FormKey k)
    {
        if (Index.EnableChanges.TryGetValue(k, out var ch) && ch.Count > 0) return (ch[^1].Parent, ch[^1].Opposite);
        return (null, false);
    }

    /// <summary>The baseline: EnableState over the winner, memoised.</summary>
    public EnableResolution Baseline(FormKey k) => EnableState.Resolve(k, WinnerFacts, _baseline);

    /// <summary>The plugin that defined a reference (its first snap), -1 when unplaced.</summary>
    public int PlacerOf(FormKey k) => Index.History.Of(k) is { Count: > 0 } h ? h[0].Plugin : -1;

    /// <summary>The plugin that last set a reference's XESP (the edge's author), -1 when it never had one.</summary>
    public int AttacherOf(FormKey k) => Index.EnableChanges.TryGetValue(k, out var ch) && ch.Count > 0 ? ch[^1].Plugin : -1;

    public bool IsNavcut(FormKey k) => Index.RefCollisionLayer.TryGetValue(k, out var cl) && Index.CollisionLayersByIndex.TryGetValue(cl, out var l) && l.NavmeshObstacle;

    /// <summary>The name of a reference's collision layer, or the bare index when no COLL record carries it.</summary>
    public string LayerName(FormKey k) => Index.RefCollisionLayer.TryGetValue(k, out var cl)
        ? (Index.CollisionLayersByIndex.TryGetValue(cl, out var l) ? l.Edid ?? l.Key.ToString() : $"layer {cl}") : "-";

    /// <summary>Build the census over an ingested index. Every reference with a winning placement is visited once.</summary>
    public static SwitchIndex Build(OverlapIndex idx)
    {
        var sw = new SwitchIndex(idx);
        // 1. every child's winning parent; the navcuts; the singleton candidates (a disabled ref with no parent — counted
        //    as a singleton only when it is not itself a switch, decided after the switches are known).
        var childrenOf = new Dictionary<FormKey, List<FormKey>>();
        var disabledNoParent = new List<FormKey>();
        foreach (var (key, snaps) in idx.History.Refs)
        {
            var win = snaps[^1];
            if (win.Deleted || idx.IsDeletedNow(key)) continue;
            if (sw.IsNavcut(key))
            {
                sw.Navcuts.Add(key);
                if (!idx.RefPrimitive.ContainsKey(key)) sw.NavcutsWithoutPrimitive++;
            }
            var (parent, opposite) = sw.LastEnableChange(key);
            if (parent is not { } p)
            {
                if (win.Disabled) disabledNoParent.Add(key);
                continue;
            }
            if (p.ID == EnableState.PlayerRefId) { sw.PlayerParentRefs++; continue; }
            var ps = idx.History.Winning(p);
            if (ps is null || ps.Value.Deleted || idx.IsDeletedNow(p)) { sw.UnplacedParentRefs++; continue; }
            sw.ParentOf[key] = (p, opposite);
            if (!childrenOf.TryGetValue(p, out var l)) childrenOf[p] = l = new List<FormKey>();
            l.Add(key);
        }
        // 2. the switches, with their direct children.
        foreach (var (pKey, kids) in childrenOf)
        {
            var ps = idx.History.Winning(pKey)!.Value;
            var info = new SwitchInfo
            {
                Key = pKey, Owner = sw.PlacerOf(pKey), Cell = ps.Cell, Space = ps.Space, Interior = ps.Interior, Position = ps.Pos,
                Baseline = sw.Baseline(pKey),
            };
            if (idx.RefBase.TryGetValue(pKey, out var bk))
            {
                info.BaseIsMarker = idx.BaseIsMarker.Contains(bk);
                if (idx.BaseInfo.TryGetValue(bk, out var bi)) { info.BaseType = bi.Type; info.BaseEditorId = bi.Edid; }
            }
            var (pp, po) = sw.LastEnableChange(pKey);
            if (pp is { } ppk && ppk.ID != EnableState.PlayerRefId && sw.ParentOf.ContainsKey(pKey)) { info.Parent = ppk; info.ParentOpposite = po; }
            foreach (var k in kids)
            {
                var ks = idx.History.Winning(k)!.Value;
                bool opp = sw.ParentOf[k].Opposite;
                info.Children.Add(new SetMember(k, opp, opp, 1, sw.PlacerOf(k), sw.AttacherOf(k), ks.Cell, ks.Space, sw.IsNavcut(k)));
            }
            sw.Switches[pKey] = info;
        }
        foreach (var k in disabledNoParent) if (!sw.Switches.ContainsKey(k)) sw.DisabledSingletons++;
        // 3. editor ids (the marker's own, from RefInfo-shaped data the index does not keep — read from the arm map)
        //    and the descendants with parity.
        foreach (var info in sw.Switches.Values)
        {
            var seen = new HashSet<FormKey> { info.Key };
            var stack = new Stack<(SetMember M, bool Parity)>();
            foreach (var c in info.Children) stack.Push((c, c.Opposite));
            while (stack.Count > 0)
            {
                var (m, parity) = stack.Pop();
                if (!seen.Add(m.Key)) { info.ChainCycle = true; sw.ChainCycles++; continue; }
                var dm = m with { Parity = parity };
                info.Descendants.Add(dm);
                if (dm.Navcut) sw.NavcutUnderSwitch.Add(dm.Key);
                if (sw.Switches.TryGetValue(m.Key, out var child))
                    foreach (var gc in child.Children) stack.Push((gc with { Depth = m.Depth + 1 }, parity ^ gc.Opposite));
            }
        }
        return sw;
    }

    /// <summary>Editor ids for the switches, from a map the probe collects (the index keeps base names, not ref names).</summary>
    public void NameSwitches(IReadOnlyDictionary<FormKey, string> refEditorIds)
    {
        foreach (var info in Switches.Values)
            if (refEditorIds.TryGetValue(info.Key, out var e) && !string.IsNullOrWhiteSpace(e))
            {
                info.EditorId = e;
                ByEditorId.TryAdd(e, info.Key);
            }
    }

    /// <summary>A switch by editor id or FormKey text; null when the order has no such switch.</summary>
    public SwitchInfo? Find(string token)
    {
        if (ByEditorId.TryGetValue(token, out var k) && Switches.TryGetValue(k, out var s)) return s;
        try { var fk = FormKey.Factory(token); return Switches.TryGetValue(fk, out var s2) ? s2 : null; }
        catch { return null; }
    }
}

/// <summary>The census half of the state arm: parsing a <c>--state</c> value against the census, and what a flip changes.</summary>
public sealed partial class StateArm
{
    /// <summary>Parse <c>--state</c>: <c>baseline</c>; <c>&lt;switch&gt;[,&lt;switch&gt;…]</c> by editor id or FormKey;
    /// <c>stage:&lt;quest&gt;:&lt;n&gt;</c> through <paramref name="map"/> (switchprobe's joint flips). A token the order
    /// does not carry is an error, never ignored — a state that silently flipped nothing would read as the baseline.</summary>
    public static StateArm Parse(string spec, SwitchIndex sw, SwitchMap? map = null)
    {
        spec = spec.Trim();
        if (spec.Equals(BaselineName, StringComparison.OrdinalIgnoreCase)) return Baseline();
        if (spec.StartsWith("stage:", StringComparison.OrdinalIgnoreCase))
        {
            if (map is null) throw new ArgumentException($"--state {spec} needs --switch-map <switchprobe --json file> (the joint flips come from the trigger map)");
            var flips = map.StageFlips(spec[6..]);
            if (flips is null) throw new ArgumentException($"--state {spec}: the switch map names no such quest stage (or it flips no switch)");
            var keys = new List<FormKey>();
            foreach (var t in flips)
            {
                var s = sw.Find(t) ?? throw new ArgumentException($"--state {spec}: the map's switch '{t}' is not a switch in this order");
                keys.Add(s.Key);
            }
            return new StateArm(spec, keys);
        }
        var list = new List<FormKey>();
        foreach (var tok in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var s = sw.Find(tok) ?? throw new ArgumentException($"--state: '{tok}' is not a switch in this order (no winning reference names it as an enable parent)");
            if (!list.Contains(s.Key)) list.Add(s.Key);
        }
        if (list.Count == 0) throw new ArgumentException("--state: no switch named");
        return new StateArm(spec, list);
    }

    /// <summary>Every reference whose runtime state the flip changes — the flipped switches and their descendants —
    /// with the state on each side. Read from the census, so a cycle or an unplaced link is already counted there.</summary>
    public IEnumerable<(FormKey Key, bool Before, bool After, SwitchInfo Root, SetMember? Member)> Changes(SwitchIndex sw)
    {
        var facts = Wrap(sw.WinnerFacts, sw.Baseline);
        var memo = new Dictionary<FormKey, EnableResolution>();
        var seen = new HashSet<FormKey>();
        foreach (var f in Flips)
        {
            if (!sw.Switches.TryGetValue(f, out var root)) continue;
            if (seen.Add(f))
            {
                bool before = sw.Baseline(f).Enabled, after = EnableState.Resolve(f, facts, memo).Enabled;
                if (before != after) yield return (f, before, after, root, null);
            }
            foreach (var m in root.Descendants)
            {
                if (!seen.Add(m.Key)) continue;
                bool before = sw.Baseline(m.Key).Enabled, after = EnableState.Resolve(m.Key, facts, memo).Enabled;
                if (before != after) yield return (m.Key, before, after, root, m);
            }
        }
    }
}

/// <summary>A navcut box as an obstacle: the XPRM box (full extents about the reference) closed as twelve outward
/// triangles in the reference's own frame, for the route rule's column index at the reference's transform.</summary>
public static class NavcutGeometry
{
    public static Aabb ModelBox(PlacedPrimitiveInfo prim)
    {
        var h = new Vec3(prim.Bounds.X / 2f, prim.Bounds.Y / 2f, prim.Bounds.Z / 2f);
        if (h.X <= 0 && h.Y <= 0 && h.Z <= 0) return Aabb.Empty;
        return new Aabb(new Vec3(-h.X, -h.Y, -h.Z), h, false);
    }

    public static TriangleSoup Soup(PlacedPrimitiveInfo prim) => ContactIndex.BoxSoup(ModelBox(prim));

    /// <summary>The soup placed in the world with the reference's transform.</summary>
    public static Vector3[] WorldVertices(TriangleSoup soup, Vec3 pos, Vec3 rot, float scale)
    {
        var m = SpatialOverlap.RefrRotation(rot);
        var t = new Vector3(pos.X, pos.Y, pos.Z);
        var w = new Vector3[soup.Vertices.Length];
        for (int i = 0; i < w.Length; i++) w[i] = Vector3.Transform(soup.Vertices[i] * scale, m) + t;
        return w;
    }
}
