using HousecarlCore;

namespace SkyWright.Engine;

/// <summary>A plugin file on disk that the order does not load: in a disabled mod folder, or unchecked in an enabled
/// one. <paramref name="AtRoot"/>: directly in the folder (a subfolder is an installer option the game never sees).
/// <paramref name="Empty"/>: the structural empty-dummy test (<see cref="EmptyPlugin"/>).</summary>
public sealed record DiskPlugin(string Name, string Folder, bool FolderEnabled, bool AtRoot, IReadOnlyList<string> Masters, bool Empty);

/// <summary>Shape 1 — a patch on disk and not enabled. A question to the person, never a finding: the plugin is
/// asked about when every master it names is loaded and those masters come from two or more distinct non-base
/// providers. A provider is the mod folder that supplies the loaded master — the plugin's own folder included when
/// it supplies one (a patch shipped inside one of the two mods it bridges). The base game's files and Creation Club
/// plugins count as base, not as a provider, whatever folder supplies them (<see cref="IsGameContent"/>).</summary>
public static class InactivePatchLook
{
    public const string Shape = "inactivePatch";

    static readonly HashSet<string> BaseGame = new(StringComparer.OrdinalIgnoreCase)
    { "Skyrim.esm", "Update.esm", "Dawnguard.esm", "HearthFires.esm", "Dragonborn.esm", "SkyrimVR.esm", "_ResourcePack.esl" };

    /// <summary>A master that is game content, not a mod: the base game's files and Creation Club plugins (named "cc…"),
    /// whichever folder supplies them (a cleaned-masters mod, a Creation Club folder). The rule PluginKinds.IsBethesda uses.</summary>
    public static bool IsGameContent(string plugin) => BaseGame.Contains(plugin) || plugin.StartsWith("cc", StringComparison.OrdinalIgnoreCase);

    /// <summary><paramref name="activeProvider"/>: every loaded plugin → the mod folder that supplies it, null for base
    /// (the base masters and anything in the game's Data). <paramref name="onDisk"/>: every plugin filename found on
    /// disk anywhere (any folder, enabled or not). Names compare case-insensitively.</summary>
    public static LookResult Evaluate(IEnumerable<DiskPlugin> inactive, IReadOnlyDictionary<string, string?> activeProvider, IReadOnlySet<string> onDisk,
                                      IReadOnlySet<string>? dummies = null)
    {
        var r = new LookResult(Shape);
        foreach (var p in inactive.OrderBy(p => p.Folder, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
        {
            string ex = $"{p.Name} ({p.Folder})";
            if (!p.AtRoot) { r.SetAside("in a subfolder of its mod (an installer option the game does not see)", ex); continue; }
            if (p.Empty) { r.SetAside("an empty dummy plugin", ex); continue; }
            if (activeProvider.TryGetValue(p.Name, out var copy)) { r.SetAside("a plugin of this name is already loaded from another folder", $"{ex}; loaded from {copy ?? "the game's Data"}"); continue; }

            var providers = new List<(string Master, string Provider)>();
            string? notLoaded = null, absent = null;
            foreach (var m in p.Masters)
            {
                if (activeProvider.TryGetValue(m, out var prov)) { if (prov is not null && !IsGameContent(m)) providers.Add((m, prov)); continue; }
                if (onDisk.Contains(m)) notLoaded ??= m; else absent ??= m;
            }
            if (dummies is not null && p.Masters.FirstOrDefault(dummies.Contains) is { } dm) { r.SetAside("masters an empty dummy plugin (the mod it patches is off)", $"{ex}; {dm}"); continue; }
            if (notLoaded is not null) { r.SetAside("masters a plugin that is on disk but not loaded (a patch for a mod not active)", $"{ex}; needs {notLoaded}"); continue; }
            if (absent is not null) { r.SetAside("masters a plugin that is nowhere on disk", $"{ex}; needs {absent}"); continue; }

            var distinct = providers.Select(x => x.Provider).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (distinct.Count < 2) { r.SetAside("its loaded masters come from fewer than two mods", ex); continue; }

            // Every master is named, game content included, so the person sees what the patch is for.
            string Label(string m) => IsGameContent(m)
                ? (m.StartsWith("cc", StringComparison.OrdinalIgnoreCase) ? "Creation Club" : "base game")
                : activeProvider[m] ?? "the game's Data";
            var shown = p.Masters.GroupBy(Label, StringComparer.OrdinalIgnoreCase)
                                 .Select(g => $"{string.Join(", ", g)} ({g.Key})").ToList();
            var where = p.FolderEnabled ? $"is unchecked in the enabled folder '{p.Folder}'" : $"sits in the disabled folder '{p.Folder}'";
            r.Rows.Add(new LookRow(Shape, p.FolderEnabled ? "unchecked" : "folder-disabled", LookKind.Question,
                distinct.Prepend(p.Folder).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                p.Masters.Prepend(p.Name).ToList(),
                new[] { $"{p.Folder}\\{p.Name}" },
                $"{p.Name} {where} and is not loaded; it masters {string.Join(" and ", shown)}, all loaded. Is it off on purpose?",
                "The game does not load it, so whatever it patches between those mods is not applied.",
                "source: an unloaded plugin is not in the game's load order (the load-order resolver; MO2 exposes only enabled folders)"));
        }
        r.Number();
        return r;
    }
}
