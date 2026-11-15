using Mutagen.Bethesda.Plugins;

namespace Workbench;

/// <summary>"Make one mod see-through": which plugins a mod folder provides in the arm's view, and which drawn
/// references those plugins win.</summary>
internal static class ModTint
{
    /// <summary>The plugins of the order that <paramref name="modFolder"/> provides, by the layer map's winning copy
    /// (<c>HarnessOrderCli.PluginLayerMap</c>, built for the arm's own view), in load order; or one sentence when the
    /// folder is not a mod folder of the instance or provides none of the order's plugins.</summary>
    internal static (IReadOnlyList<string> Plugins, string? Refusal) PluginsOf(string modFolder, IReadOnlyDictionary<string, string> layerMap,
                                                                             string modsDir, IReadOnlyList<string> orderPlugins)
    {
        var name = (modFolder ?? "").Trim();
        var dir = Path.GetFullPath(Path.Combine(modsDir, name)).TrimEnd('\\', '/');
        if (name.Length == 0 || !Directory.Exists(dir))
            return (Array.Empty<string>(), $"'{modFolder}' is not a mod folder of this instance; name one as the launcher lists it.");
        var plugins = orderPlugins.Where(p => layerMap.TryGetValue(p, out var path)
                                              && string.Equals(Path.GetFullPath(Path.GetDirectoryName(path)!).TrimEnd('\\', '/'), dir, StringComparison.OrdinalIgnoreCase))
                                  .ToList();
        if (plugins.Count == 0)
            return (plugins, $"the mod folder '{name}' provides no plugin of this order, so none of the references drawn can be its; name a plugin instead, or another folder.");
        return (plugins, null);
    }

    /// <summary>The mod folder's plugins in the arm's view: the layer map built from <paramref name="viewMods"/>, the
    /// arm's own mod list.</summary>
    internal static (IReadOnlyList<string> Plugins, string? Refusal) PluginsOf(string modFolder, string profileDir, IReadOnlyList<string> viewMods,
        string modsDir, string dataDir, string overwriteDir, IReadOnlyList<string> orderPlugins)
        => PluginsOf(modFolder, HarnessShared.HarnessOrderCli.PluginLayerMap(profileDir, viewMods, modsDir, dataDir, overwriteDir), modsDir, orderPlugins);

    /// <summary>The references to tint, and one sentence naming what was left out and why: a Base Object Swapper
    /// reference (drawn with the swap's transform) and one whose placement differs from the server's.</summary>
    internal static (IReadOnlyList<FormKey> Refs, string Note) Pick(IEnumerable<(FormKey Key, bool Bos)> drawn, IReadOnlyDictionary<FormKey, string> winners,
                                                                  IReadOnlyCollection<FormKey> mismatched, IReadOnlyCollection<string> plugins)
        => (Select(drawn.Select(d => d.Key), winners, plugins), "");

    /// <summary>The drawn references whose winning plugin is one of <paramref name="plugins"/>.</summary>
    internal static IReadOnlyList<FormKey> Select(IEnumerable<FormKey> drawn, IReadOnlyDictionary<FormKey, string> winners, IReadOnlyCollection<string> plugins)
    {
        var set = new HashSet<string>(plugins, StringComparer.OrdinalIgnoreCase);
        return drawn.Where(k => winners.TryGetValue(k, out var w) && set.Contains(w)).ToList();
    }
}
