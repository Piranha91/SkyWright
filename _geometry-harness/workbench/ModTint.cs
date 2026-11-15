using Mutagen.Bethesda.Plugins;

namespace Workbench;

/// <summary>"Make one mod see-through": which plugins a mod folder provides in the arm's view, and which drawn
/// references those plugins win.</summary>
internal static class ModTint
{
    /// <summary>The plugins of the order that <paramref name="modFolder"/> provides, by the layer map's winning copy
    /// (<c>HarnessOrderCli.PluginLayerMap</c>), or one sentence when it provides none.</summary>
    internal static (IReadOnlyList<string> Plugins, string? Refusal) PluginsOf(string modFolder, IReadOnlyDictionary<string, string> layerMap,
                                                                             string modsDir, IReadOnlyList<string> orderPlugins)
        => (Array.Empty<string>(), null);

    /// <summary>The drawn references whose winning plugin is one of <paramref name="plugins"/>.</summary>
    internal static IReadOnlyList<FormKey> Select(IEnumerable<FormKey> drawn, IReadOnlyDictionary<FormKey, string> winners, IReadOnlyCollection<string> plugins)
        => Array.Empty<FormKey>();
}
