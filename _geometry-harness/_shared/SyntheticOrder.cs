using HousecarlCore;

namespace HarnessShared;

// SYNTHETIC LOAD ORDER — the real order, plus or minus a set of plugins.
//
// WHY THIS EXISTS, and it is not the obvious reason. "What breaks if I install X?" is the sales
// pitch. The reason it got built is that **nothing in this project has ever been validated against
// an outcome.** Every finding is a statement about what the files say, checked only against other
// statements about what the files say. A detector that is subtly wrong produces exactly the same
// confident output as one that is right.
//
// A differential run is the fix, and it needs no game:
//
//     navownprobe                                        -> N clobbered navmesh records
//     navownprobe --enable "TGC Winterhold - OCW Patch.esp" -> N-k
//
// If k is the number that patch was scored as covering, the detector responds correctly to a known
// intervention. If it is 0, the detector is wrong, and that is the more valuable result.
//
// ⚠️ WHAT MAKES THIS CHEAP: `loadorder.txt` lists EVERY plugin in order, inactive ones included.
// So a synthetic order is that list filtered by a modified active set, and a re-enabled plugin lands
// in the position MO2 already assigned it — no guessing where to insert it. Position decides which
// plugin wins a navmesh, so inventing one would quietly invent the answer too. A plugin that is NOT
// in loadorder.txt has no such position; that case is reported loudly rather than guessed at.
//
// BOUNDARY (Q3 — this claims exactly what it does). It models which plugin RECORDS are loaded and
// in what order. It does NOT model the asset VFS: enabling a plugin that lives in a disabled mod
// folder does not put that folder's meshes and textures in the resolver's path. Record-level probes
// (navownprobe's clobber and dangling-link checks, driftprobe) are unaffected; anything that
// measures a mesh is not, and must say so.

/// <param name="OrderedPaths">Real plugin paths, masters first, highest priority LAST — the same shape
/// <see cref="Mo2LoadOrder.Build"/> returns, so a probe can swap one for the other.</param>
/// <param name="Applied">Plugins whose loaded state this actually changed, with what happened.</param>
public sealed record SyntheticResult(
    IReadOnlyList<string> OrderedPaths,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Applied,
    int LoadedCount);

public static class SyntheticOrder
{
    /// <summary>The real profile order with <paramref name="enable"/> switched on and
    /// <paramref name="disable"/> switched off. Plugin FILENAMES, not mod folder names.</summary>
    public static SyntheticResult Build(
        string profileDir, string modsDir, string dataDir, string overwriteDir,
        IEnumerable<string>? enable = null, IEnumerable<string>? disable = null)
    {
        var warnings = new List<string>();
        var applied = new List<string>();
        var comp = Mo2LoadOrder.ReadComposition(profileDir, warnings);

        var enableSet = new HashSet<string>(enable ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var disableSet = new HashSet<string>(disable ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var inactive = new HashSet<string>(comp.InactivePluginNames, StringComparer.OrdinalIgnoreCase);

        // Mirror Build's rule exactly: loaded == in loadorder.txt and NOT in plugins.txt without a
        // star. The implicit base-game masters are in neither list, so they stay loaded, which is
        // the behaviour the real resolver has.
        bool Loaded(string name)
        {
            if (disableSet.Contains(name)) return false;
            if (enableSet.Contains(name)) return true;
            return !inactive.Contains(name);
        }

        var map = BuildFilenameMap(comp, modsDir, dataDir, overwriteDir);

        var orderedPaths = new List<string>(comp.OrderedPluginNames.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int loaded = 0;

        foreach (var name in comp.OrderedPluginNames)
        {
            seen.Add(name);
            bool wasLoaded = !inactive.Contains(name);
            bool nowLoaded = Loaded(name);
            if (wasLoaded != nowLoaded) applied.Add($"{(nowLoaded ? "ENABLED " : "DISABLED")} {name}");
            if (!nowLoaded) continue;

            loaded++;
            if (map.TryGetValue(name, out var path)) orderedPaths.Add(path);
            else warnings.Add($"'{name}' is in the load order but no mod folder, overwrite or Data provides it.");
        }

        // A name asked for that loadorder.txt never mentions has NO position. Appending it puts it at
        // the highest priority, which is a guess — and for a navmesh clobber the priority IS the
        // answer, so the guess would manufacture the finding. Refused, and named.
        foreach (var name in enableSet)
        {
            if (seen.Contains(name)) continue;
            warnings.Add(
                $"REFUSED to enable '{name}': loadorder.txt does not list it, so it has no load-order " +
                "position. Any position this code picked would be invented, and position decides which " +
                "plugin wins a record. Register it in MO2 first, then re-run.");
        }
        foreach (var name in disableSet)
            if (!seen.Contains(name))
                warnings.Add($"'{name}' was asked to be disabled but is not in loadorder.txt; nothing to do.");

        return new SyntheticResult(orderedPaths, warnings, applied, loaded);
    }

    /// <summary>filename → winning real path, in MO2's own precedence: overwrite, then enabled mods by
    /// modlist priority (top = highest, first seen wins), then DISABLED mods, then game Data.
    /// <para>The disabled layer is what <see cref="Mo2LoadOrder.Build"/>'s map deliberately omits and
    /// what this needs: a plugin worth switching on for a what-if is very often sitting in a folder
    /// that is switched off. It is placed BELOW every enabled mod, so turning one on can never
    /// silently outrank a mod that is genuinely installed.</para></summary>
    static Dictionary<string, string> BuildFilenameMap(
        Mo2Composition comp, string modsDir, string dataDir, string overwriteDir)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        void Layer(string dir)
        {
            if (string.IsNullOrWhiteSpace(dir)) return;
            string[] files;
            try { files = Directory.GetFiles(dir); }
            catch { return; }                       // an unreadable folder contributes nothing, never throws
            foreach (var f in files)
            {
                if (!IsPlugin(f)) continue;
                var fn = Path.GetFileName(f);
                if (!map.ContainsKey(fn)) map[fn] = f;      // first seen wins == highest precedence
            }
        }

        Layer(overwriteDir);
        foreach (var mod in comp.EnabledMods) Layer(Path.Combine(modsDir, mod));
        foreach (var mod in comp.DisabledMods) Layer(Path.Combine(modsDir, mod));
        Layer(dataDir);
        return map;
    }

    static bool IsPlugin(string p)
    {
        var e = Path.GetExtension(p);
        return e.Equals(".esp", StringComparison.OrdinalIgnoreCase)
            || e.Equals(".esm", StringComparison.OrdinalIgnoreCase)
            || e.Equals(".esl", StringComparison.OrdinalIgnoreCase);
    }
}
