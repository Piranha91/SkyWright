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
// measures a mesh is not, and must say so. Since 2026-09-11 the caller says it for itself: the run's
// ConfigurationIdentity (HarnessOrderCli / _shared/Configuration.cs) records each such plugin in its
// Limits, so an emit computed over one carries the caveat instead of relying on a reader's memory.

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

        // Two-phase: decide every plugin's loaded state first, THEN check masters against that
        // state, THEN build the paths. Position alone is not enough for an honest what-if — a
        // plugin whose master is absent cannot be loaded by the game at all, so an order
        // containing it describes an IMPOSSIBLE configuration and every finding computed from it
        // is fiction. (2026-08-27: the review demonstrated exactly that — enabling a patch whose
        // master is nowhere in the order "repaired" 9 records; the game would have refused to
        // start. The published negative control had the same defect, unnoticed.)
        var state = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in comp.OrderedPluginNames) state[name] = Loaded(name);
        var loadedNames = new HashSet<string>(state.Where(kv => kv.Value).Select(kv => kv.Key),
                                              StringComparer.OrdinalIgnoreCase);

        // A plugin this call is switching ON with a master absent from the result is REFUSED, the
        // same way an unlisted plugin is: the alternative is inventing an impossible game state.
        foreach (var name in comp.OrderedPluginNames)
        {
            if (!state[name] || !enableSet.Contains(name) || !map.TryGetValue(name, out var path)) continue;
            var masters = TryReadMasters(path);
            if (masters is null) { warnings.Add($"'{name}': master list unreadable, missing-master check skipped for it."); continue; }
            var missing = masters.Where(m => !loadedNames.Contains(m)).ToList();
            if (missing.Count == 0) continue;
            state[name] = false;
            loadedNames.Remove(name);
            warnings.Add(
                $"REFUSED to enable '{name}': it masters {string.Join(", ", missing.Select(m => $"'{m}'"))}, " +
                "absent from the synthetic order. The game cannot load that state, so any finding computed " +
                "from it would describe an impossible configuration.");
        }

        // Everything else that would be broken in the result — a --disable orphaning dependents, or
        // a pre-existing gap — is warned about loudly rather than refused: the caller asked for
        // this state, and the honest what-if answer includes "the game will not load it".
        foreach (var name in comp.OrderedPluginNames)
        {
            if (!state[name] || !map.TryGetValue(name, out var path)) continue;
            var masters = TryReadMasters(path);
            if (masters is null) continue;
            foreach (var m in masters.Where(m => !loadedNames.Contains(m)))
                warnings.Add((disableSet.Contains(m)
                    ? $"MISSING MASTER: disabling '{m}' orphans '{name}', which masters it"
                    : $"MISSING MASTER: '{name}' masters '{m}', which is not loaded")
                    + " — the game cannot load this order.");
        }

        var orderedPaths = new List<string>(comp.OrderedPluginNames.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int loaded = 0;

        foreach (var name in comp.OrderedPluginNames)
        {
            seen.Add(name);
            bool wasLoaded = !inactive.Contains(name);
            bool nowLoaded = state[name];
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
    /// modlist priority (top = highest, first seen wins), then game Data — and only THEN, below
    /// everything the game can actually see, the DISABLED mod folders.
    /// <para>The disabled layer is what <see cref="Mo2LoadOrder.Build"/>'s map deliberately omits and
    /// what this needs, but for one job only: supplying a plugin that NO enabled folder and NOT Data
    /// provides — the what-if of switching on a plugin whose only copy sits in a folder that is off.
    /// A plugin Data already provides is served by Data, because that is the copy the game loads;
    /// MO2 puts nothing from a switched-off folder into the game's view.</para>
    /// <para>It sat ABOVE Data until 2026-09-11, and that was a defect with measured consequences. On
    /// the SE conflict lab's profile, 21 Creation Club plugins exist both in the game's Data folder and
    /// in a switched-off "Creation Club Content" mod folder, with DIFFERENT bytes —
    /// ccbgssse018-shadowrend.esl is 299,516 B in Data and 253,552 B in the folder. The baseline arm
    /// (<see cref="Mo2LoadOrder.Build"/>) read Data; every <c>--enable</c> arm read the folder. So every
    /// differential run on that profile was comparing two configurations that differed in 21 plugins'
    /// content as well as in the plugin under test, and nothing said so. The configuration identity
    /// (roadmap 4.20) is what surfaced it, on its first real run.</para>
    /// <para>Public because orderprobe resolves the SAME baseline this builds: a divergent copy of the precedence
    /// rules there would silently disagree with a synthetic what-if about which file a name means.</para></summary>
    public static Dictionary<string, string> BuildFilenameMap(
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
        Layer(dataDir);                             // the game's own copy beats any switched-off folder's
        foreach (var mod in comp.DisabledMods) Layer(Path.Combine(modsDir, mod));
        return map;
    }

    static bool IsPlugin(string p)
    {
        var e = Path.GetExtension(p);
        return e.Equals(".esp", StringComparison.OrdinalIgnoreCase)
            || e.Equals(".esm", StringComparison.OrdinalIgnoreCase)
            || e.Equals(".esl", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The plugin's declared masters, read straight off the TES4 header (MAST subrecords).
    /// Deliberately dependency-free — _shared must not pull Mutagen in — and header-only, so 490
    /// plugins cost milliseconds. Returns null when the file is not a readable plugin (an empty or
    /// truncated file makes no claim, and the caller must not invent one).</summary>
    public static IReadOnlyList<string>? TryReadMasters(string path)
    {
        try
        {
            using var f = File.OpenRead(path);
            Span<byte> hdr = stackalloc byte[24];
            if (f.Read(hdr) != 24 || hdr[0] != (byte)'T' || hdr[1] != (byte)'E' || hdr[2] != (byte)'S' || hdr[3] != (byte)'4')
                return null;
            int size = BitConverter.ToInt32(hdr.Slice(4, 4));
            if (size < 0 || size > 1 << 24) return null;
            var body = new byte[size];
            if (f.Read(body, 0, size) != size) return null;

            var masters = new List<string>();
            int i = 0;
            while (i + 6 <= body.Length)
            {
                ushort len = BitConverter.ToUInt16(body, i + 4);
                if (i + 6 + len > body.Length) break;
                if (body[i] == (byte)'M' && body[i + 1] == (byte)'A' && body[i + 2] == (byte)'S' && body[i + 3] == (byte)'T')
                {
                    int end = i + 6 + len;
                    int stop = end;
                    while (stop > i + 6 && body[stop - 1] == 0) stop--;          // trailing NUL
                    // Latin1, not GetEncoding(1252): code-page encodings need a provider registered
                    // on .NET Core, and the two differ only in 0x80–0x9F, absent from plugin names.
                    masters.Add(System.Text.Encoding.Latin1.GetString(body, i + 6, stop - (i + 6)));
                }
                i += 6 + len;
            }
            return masters;
        }
        catch { return null; }
    }
}
