using Mutagen.Bethesda.Plugins;

namespace Workbench;

/// <summary>One node of the Records tree (worldspace, cell or placed reference; the shape of SharpKit's Cell View, as an
/// idea only). A reference node names its FormKey and base EditorID; <see cref="Conflict"/> marks a reference a Conflicts
/// row names.</summary>
internal sealed record TreeNode(string Kind, string Key, string Label, bool Conflict, bool Loaded, IReadOnlyList<TreeNode> Children)
{
    /// <summary>On a reference node: why the scene did not draw it (one word), or null when drawn.</summary>
    public string? NotDrawn { get; init; }
}

/// <summary>A placed reference the walk read: its FormKey, its cell, its base's EditorID, and why the scene did not draw it
/// (one word), or null when it is drawn.</summary>
internal sealed record SceneRefEntry(FormKey Ref, FormKey? Cell, string? BaseEditorId, string? NotDrawn = null);

/// <summary>The Records tree: the loaded scene's drawn references by worldspace and cell, then the conflict cells that are
/// not loaded with only their conflict references (a click on one loads that cell).</summary>
internal static class RecordsTree
{
    /// <summary>Every placed reference the walk read, drawn or not (stage-3 review M-2): the walk's per-reference outcome
    /// from viewprobe, and the references whose meshes the scene holds. A reference the build placed but whose mesh
    /// never opened (a model path the scene could not resolve or load) reads "unresolved-mesh", distinct from "no-model" (a base with no model). Without the walk's outcomes (an older viewprobe) only the drawn ones are known.</summary>
    internal static IReadOnlyList<SceneRefEntry> Entries(IEnumerable<(FormKey Ref, string? BaseEditorId)> drawn,
        IReadOnlyDictionary<FormKey, (string Outcome, string? BaseEditorId)>? outcomes, IReadOnlyDictionary<FormKey, FormKey> cells)
    {
        FormKey? Cell(FormKey k) => cells.TryGetValue(k, out var c) ? c : null;
        var d = drawn.GroupBy(x => x.Ref).ToDictionary(g => g.Key, g => g.First().BaseEditorId);
        var list = d.Select(x => new SceneRefEntry(x.Key, Cell(x.Key), x.Value)).ToList();
        if (outcomes is not null)
            foreach (var (k, o) in outcomes)
                if (!d.ContainsKey(k) && !k.IsNull)
                    list.Add(new SceneRefEntry(k, Cell(k), o.BaseEditorId, o.Outcome == "drawn" ? "unresolved-mesh" : o.Outcome));
        return list;
    }

    internal static IReadOnlyList<TreeNode> Build(string worldspaceLabel, IEnumerable<SceneRefEntry> drawn,
                                                  Func<FormKey, string> cellLabel, IReadOnlySet<string> conflictRefs,
                                                  IReadOnlyDictionary<string, IReadOnlyList<string>> unloadedConflictCells)
    {
        static string K(FormKey k) => k.ToString();
        var cells = drawn.GroupBy(d => d.Cell)
            .OrderBy(g => g.Key is { } c ? K(c) : "~", StringComparer.Ordinal)
            .Select(g => new TreeNode("cell", g.Key is { } c ? K(c) : "", g.Key is { } c2 ? cellLabel(c2) : "(cell not recorded by the walk)",
                g.Any(d => conflictRefs.Contains(K(d.Ref))), true,
                g.OrderBy(d => K(d.Ref), StringComparer.Ordinal)
                 .Select(d => new TreeNode("ref", K(d.Ref), $"{K(d.Ref)}  {d.BaseEditorId ?? "(base EditorID not recorded)"}" + (d.NotDrawn is { } why ? $"  [not drawn: {why}]" : ""),
                                           conflictRefs.Contains(K(d.Ref)), true, Array.Empty<TreeNode>()) { NotDrawn = d.NotDrawn })
                 .ToList()))
            .ToList();
        var roots = new List<TreeNode> { new("worldspace", worldspaceLabel, worldspaceLabel, cells.Any(c => c.Conflict), true, cells) };
        if (unloadedConflictCells.Count > 0)
            roots.Add(new TreeNode("group", "", "conflict cells not loaded (click a reference to load its cell)", true, false,
                unloadedConflictCells.OrderBy(x => x.Key, StringComparer.Ordinal)
                    .Select(x => new TreeNode("cell", x.Key, x.Key, true, false,
                        x.Value.OrderBy(r => r, StringComparer.Ordinal).Select(r => new TreeNode("ref", r, r, true, false, Array.Empty<TreeNode>())).ToList()))
                    .ToList()));
        return roots;
    }

    /// <summary>The tree with only what a conflict involves: the conflict references, and the cells and worldspaces that
    /// hold one (Build marks a cell or worldspace a conflict when any reference under it is).</summary>
    internal static IReadOnlyList<TreeNode> OnlyConflicts(IEnumerable<TreeNode> nodes) =>
        nodes.Where(n => n.Conflict).Select(n => n with { Children = OnlyConflicts(n.Children) }).ToList();

    /// <summary>Whether the tree comes from a check with conflicts: only then does the conflict filter apply.</summary>
    internal static bool HasConflicts(IEnumerable<TreeNode> nodes) => Walk(nodes).Any(n => n.Kind == "ref" && n.Conflict);

    internal static IEnumerable<TreeNode> Walk(IEnumerable<TreeNode> nodes)
    {
        foreach (var n in nodes) { yield return n; foreach (var c in Walk(n.Children)) yield return c; }
    }
}

/// <summary>What a click on a Conflicts row or a tree reference does.</summary>
internal abstract record ClickPlan
{
    internal sealed record Land(FormKey Ref) : ClickPlan;
    internal sealed record Switch(FormKey Ref, int CellIndex, string Cell) : ClickPlan;
    internal sealed record Refuse(string Sentence) : ClickPlan;
}

internal static class ClickThrough
{
    /// <summary>Land on <paramref name="refKey"/> in this scene when it is drawn here; else rebuild the scene for its cell
    /// (the worklist's cell switch) when that cell is one the worklist holds and nothing is staged; else refuse in one sentence.</summary>
    internal static ClickPlan Plan(string refKey, string? refCell, string? sceneCell, IReadOnlyList<string> worklistCells,
                                   int staged, Func<FormKey, bool> drawnHere)
    {
        if (!FormKey.TryFactory(refKey, out var key)) return new ClickPlan.Refuse($"'{refKey}' is not a FormKey; pick a reference row.");
        if (drawnHere(key)) return new ClickPlan.Land(key);
        if (refCell is null)
            return new ClickPlan.Refuse($"the bundle does not say which cell {refKey} is in (its names enrichment did not read it), so it cannot be loaded.");
        if (string.Equals(refCell, sceneCell, StringComparison.OrdinalIgnoreCase))
            return new ClickPlan.Refuse($"{refKey} is in this cell but the scene did not draw it (no model, a marker, or disabled); nothing to land on.");
        int i = worklistCells.Select((c, n) => (c, n)).FirstOrDefault(x => string.Equals(x.c, refCell, StringComparison.OrdinalIgnoreCase), (null!, -1)).n;
        if (i < 0) return new ClickPlan.Refuse($"{refKey}'s cell {refCell} is not one this session can load; reopen the bundle.");
        if (staged > 0) return new ClickPlan.Refuse($"loading cell {refCell} would drop {staged} staged edit(s); write or undo them first.");
        return new ClickPlan.Switch(key, i, refCell);
    }
}
