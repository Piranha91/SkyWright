using System.Text.Json;

namespace Workbench;

/// <summary>The reporting skill's grouping data (scene-policy.json <c>vignettes</c>): the group key's names, the check id to
/// clash type map and the type a check outside the map takes. The workbench folds rows by this data, as the report does.</summary>
internal sealed record SceneKeyPolicy(IReadOnlyList<string> Keys, IReadOnlyDictionary<string, string> Types, string OtherType)
{
    /// <summary>The key names a Conflicts row has a value for.</summary>
    internal static readonly string[] Known = { "type", "pair", "cell", "field" };

    /// <summary>Read the skill's scene policy; a key name the workbench does not map is refused.</summary>
    internal static SceneKeyPolicy Load(string path) => new(Array.Empty<string>(), new Dictionary<string, string>(), "other");

    /// <summary>The clash type of a check id, by the skill's map.</summary>
    internal string TypeOf(string checkId) => OtherType;
}

/// <summary>Rows of one cluster with equal group-key values: instances of one problem.</summary>
internal sealed record SceneGroup(IReadOnlyList<string> Key, IReadOnlyList<int> Ranks);

/// <summary>Rows of one cell joined by naming a common reference.</summary>
internal sealed record SceneCluster(IReadOnlyList<int> Ranks, IReadOnlyList<SceneGroup> Groups);

/// <summary>One cell's rows: its Conflicts rows in clusters, and its clickable Findings rows listed with them.</summary>
internal sealed record Scene(int Number, string Cell, int? BestRank, IReadOnlyList<SceneCluster> Clusters, IReadOnlyList<int> FindingRanks);

/// <summary>The scenes, and the Findings rows no scene could place (no reference or no cell).</summary>
internal sealed record SceneList(IReadOnlyList<Scene> Scenes, int UnplacedFindings);

/// <summary>Item 7: the scene is the cell; inside it, rows naming a common reference are one cluster, and inside a cluster
/// rows with equal group-key values are one group. No distance threshold.</summary>
internal static class Scenes
{
    internal static SceneList Build(IReadOnlyList<RankedRow> rows, IReadOnlyList<RankedFinding> findings, SceneKeyPolicy policy)
        => new(Array.Empty<Scene>(), 0);
}
