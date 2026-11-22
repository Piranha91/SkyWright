using System.Diagnostics;
using HousecarlCore;

namespace HarnessShared;

// THE ONE READER EVERY PROBE USES TO DECIDE WHAT IT IS EVALUATING (roadmap 4.20, 2026-09-11).
//
// Before this, each probe carried its own copy of the same twenty lines: read the composition, build the
// real order OR a --enable/--disable synthetic one, and — separately, unconditionally, from the REAL
// profile — build the asset view. Two consequences, both silent:
//
//   1. The asset view never followed the order. A synthetic arm read the profile's mods and BSAs whatever
//      it did to the plugins, so a geometry answer was computed over an asset set that belonged to a
//      different configuration. SyntheticOrder says so in its own header ("does NOT model the asset VFS");
//      nothing downstream repeated it, and the emit did not record it at all.
//   2. The emit could not say what it had consumed. ConflictFile named the probe, the instance, the
//      population and the state — nothing about which plugins loaded, in what order, with what content, or
//      which mod folders supplied the meshes. Two emits of two different orders were indistinguishable by
//      their own contents, so any reuse or comparison keyed on them was keyed on less than the answer
//      depends on.
//
// This reader is the single place all of that is decided, and it produces a ConfigurationIdentity for
// every source — the real profile, a --enable/--disable what-if, and a --order candidate file — so an emit
// can never be silent about what it read.
//
// WHAT IT REFUSES, and why refusing beats approximating. A candidate is names, not paths; resolving them
// means picking a provider per name, and a name nothing provides has no honest resolution. Neither does an
// order the engine would not load: a plugin ahead of its master, a master absent from the loaded set, a
// non-master plugin ahead of a master-flagged one. Every finding computed from such an order describes a
// game that cannot start (SyntheticOrder learned this on 2026-08-27, when enabling a patch with an absent
// master "repaired" nine records). So those are refusals, named per plugin, not warnings.
//
// A MISSING MASTER IS REFUSED FROM EVERY SOURCE (ruling 12, 2026-10-15): the profile, a --enable/--disable what-if
// (including a --enable that SyntheticOrder had to leave off) and a candidate. The reason is OrderMasters' one sentence,
// starting "MISSING MASTER"; the caller prints it and exits 2 with nothing written.
//
// SO IS A REQUEST THIS READER CANNOT BUILD AS ASKED (2026-10-24), in the same shape: a --enable that loadorder.txt does
// not list or that no layer provides ("CANNOT ENABLE"), and a --order candidate whose BaseProfile is not --profile
// ("PROFILE MISMATCH"). Each used to be answered with a different order: without the plugin, or through the other
// profile's lists.
//
// WHAT IT ALLOWS AND RECORDS INSTEAD (ConfigurationIdentity.Limits — read before believing a geometry or
// script result computed under one):
//   - a plugin whose winning provider is a mod folder that is NOT in the asset view (a disabled folder, or
//     one the candidate's own Mods list drops): the RECORDS load, the folder's meshes and textures do not;
//   - a candidate with no Mods list: the plugin order is the candidate's, the asset priority is still the
//     base profile's — plugin order and MO2 asset priority are separate inputs, and saying one changed does
//     not make the other change;
//   - a name the base profile's loadorder.txt never lists: allowed under --order because the candidate
//     GIVES the position (nothing is invented), recorded because MO2 has no position of its own for it.
//
// The ordering RULES (what a legal order is beyond what the engine will load) and the bounded search over
// candidates are OrderRules.cs / OrderSearch.cs; the identity and candidate SHAPES are Configuration.cs.
// This file is only the resolution: names -> paths -> the asset view that follows them -> the identity.

/// <summary>What one run is evaluating: the order, the asset view THAT order implies, and the parts of its
/// identity. There is no <c>Identity</c> property — only <see cref="Identity(string, string)"/>, which the
/// caller must key with the population and world state it is about to write into the emit, so a placeholder
/// identity cannot exist to leak into one.</summary>
/// <param name="OrderedPaths">Plugin paths, masters first, winner LAST — the shape <c>Mo2LoadOrder.Build</c>
/// returns, so a probe swaps one for the other.</param>
/// <param name="EnabledMods">The asset view's mod folders, MO2 priority, top = highest. Feed straight to
/// <c>AssetResolver.Build</c>.</param>
/// <param name="Archives">The active BSAs of THIS configuration (discovered from its loaded plugin names and
/// its enabled mods, not from the profile).</param>
/// <param name="Refused">True when nothing here is usable: the caller prints <paramref name="RefusedReason"/>
/// and exits 2. <see cref="Identity(string, string)"/> throws rather than answer.</param>
public sealed record ResolvedConfiguration(
    IReadOnlyList<string> OrderedPaths,
    IReadOnlyList<string> EnabledMods,
    ArchiveDiscoveryResult Archives,
    string Source,
    IReadOnlyList<ConfigPlugin> Plugins,
    IReadOnlyList<ConfigProvider> Providers,
    IReadOnlyList<ConfigOverlay> Overlays,
    IReadOnlyList<string> Limits,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Applied,
    bool Refused,
    string? RefusedReason,
    double HashSeconds)
{
    /// <summary>This configuration's identity, keyed with the population and state the emit will carry. Throws on a
    /// refused configuration — a refusal has no identity, and returning a plausible one is the silent wrong answer
    /// the gate downstream exists to stop.</summary>
    public ConfigurationIdentity Identity(string population, string state)
        => Refused
            ? throw new InvalidOperationException($"this configuration was REFUSED and has no identity: {RefusedReason}")
            : ConfigurationIdentity.Compute(Source, Plugins, Providers, Overlays, population, state, Limits);

    /// <summary>Narrow the identity to the plugins a probe actually READ, when it reads fewer than the configuration
    /// offers (contactprobe's <c>--only</c>). Without this the emit would claim the whole order and two runs over two
    /// different subsets would carry the same fingerprint — exactly the confusion the identity exists to remove.</summary>
    public ResolvedConfiguration NarrowedTo(IReadOnlyList<string> keptPaths, string why)
    {
        var kept = new HashSet<string>(keptPaths, StringComparer.OrdinalIgnoreCase);
        return this with
        {
            OrderedPaths = keptPaths,
            Plugins = Plugins.Where(p => kept.Contains(p.Path)).ToList(),
            Limits = Limits.Append($"this run read only part of the configuration: {why}. The identity describes what was "
                                 + "read, so it is not the identity of a full run of the same order.").ToList(),
        };
    }

    /// <summary>The run deliberately read the game's own assets and none of the profile's (contactprobe's
    /// <c>--vanilla-assets</c>): the providers are replaced by Data alone and the overlays dropped, because an identity
    /// that still listed the mods would describe a view this run did not use.</summary>
    public ResolvedConfiguration WithVanillaAssets() => this with
    {
        Providers = new[] { new ConfigProvider("Data", "data") },
        Overlays = Array.Empty<ConfigOverlay>(),
        Limits = Limits.Append("--vanilla-assets: the asset view is the game's own archives and Data folder ONLY — no mod "
                             + "folder, no overwrite layer and no BOS ini was read, whatever the profile enables.").ToList(),
    };
}

public static class HarnessOrderCli
{
    public const string Usage = "[--order FILE] [--enable X.esp | --enable PATH/TO/PATCH.esp] [--disable X.esp]";

    /// <summary>Decide what this run evaluates. Three sources, one shape:
    /// <list type="bullet">
    /// <item>nothing asked for — the real profile (<c>profile:&lt;name&gt;</c>);</item>
    /// <item><paramref name="enable"/>/<paramref name="disable"/> — a what-if through <see cref="SyntheticOrder"/>
    /// (<c>synthetic:&lt;profile&gt;</c>);</item>
    /// <item><paramref name="orderFile"/> — a <see cref="CandidateFile"/> (<c>candidate:&lt;file&gt;</c>).</item>
    /// </list>
    /// The two ways of describing an order are mutually exclusive: given both, this refuses rather than pick one.</summary>
    public static ResolvedConfiguration Resolve(
        string profileName, string profileDir, string modsDir, string dataDir, string overwriteDir, string gamePath,
        string? orderFile, IReadOnlyList<string>? enable, IReadOnlyList<string>? disable)
    {
        bool switches = (enable?.Count ?? 0) > 0 || (disable?.Count ?? 0) > 0;
        if (orderFile is not null && switches)
            return Refuse("--order and --enable/--disable are two descriptions of ONE order: --order spells the loaded "
                        + "set and its positions outright, --enable/--disable edits the profile's. Applying both would "
                        + "mean choosing which description wins, and the choice would be invisible in the result. Pass one.");

        var warnings = new List<string>();
        var applied = new List<string>();
        var limits = new List<string>();
        var comp = Mo2LoadOrder.ReadComposition(profileDir, warnings);

        List<string> paths;
        List<string> names;
        IReadOnlyList<string> viewMods;
        string source;

        if (orderFile is not null)
        {
            CandidateFile cand;
            try { cand = CandidateFile.Read(orderFile); }
            catch (Exception ex) { return Refuse(ex.Message); }
            // A candidate is names resolved through ONE profile's lists: through another profile's, the same names can
            // mean other files and another asset priority, and nothing downstream would say so.
            var mismatch = ProfileMismatch(cand.BaseProfile, orderFile, profileName);
            if (mismatch is not null) return Refuse(mismatch);

            source = $"candidate:{Path.GetFileName(orderFile)}";
            viewMods = cand.Mods ?? comp.EnabledMods;
            if (cand.Mods is null)
                limits.Add("candidate keeps the base profile's asset priority: it re-orders plugins and says nothing about "
                         + "mod folders, so the meshes, textures and BSAs are still the profile's. A geometry or script "
                         + "result under this candidate is a result for the candidate's RECORDS over the profile's ASSETS.");

            var map = BuildLayerMap(comp, viewMods, modsDir, dataDir, overwriteDir);
            var listed = new HashSet<string>(comp.OrderedPluginNames, StringComparer.OrdinalIgnoreCase);
            var refusals = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            paths = new List<string>(cand.Plugins.Count);
            names = new List<string>(cand.Plugins.Count);

            foreach (var n in cand.Plugins)
            {
                if (!seen.Add(n))
                {
                    refusals.Add($"candidate lists '{n}' more than once — a plugin has one position, and picking one of two "
                               + "would invent the order this run is supposed to be testing.");
                    continue;
                }
                if (!map.TryGetValue(n, out var p))
                {
                    refusals.Add($"candidate names '{n}', but no mod folder, the overwrite layer or the game Data folder "
                               + "provides a file by that name. A missing plugin is a DIFFERENT order, not this one.");
                    continue;
                }
                names.Add(n);
                paths.Add(p);
                if (!listed.Contains(n))
                    limits.Add($"candidate names '{n}', which the base profile's loadorder.txt does not list: its position "
                             + "comes from the candidate file and MO2 has no position of its own for it, so this order is "
                             + "not one the profile could be switched into without registering the plugin first.");
            }

            refusals.AddRange(CheckLegality(names, paths, warnings));
            if (refusals.Count > 0)
                return Refuse($"{Path.GetFileName(orderFile)} describes an order this run will not evaluate:\n   - "
                            + string.Join("\n   - ", refusals));
        }
        else if (switches)
        {
            source = $"synthetic:{profileName}";
            viewMods = comp.EnabledMods;
            // A PATCH FILE: an --enable naming a path, not a name (a patch navedit or the Workbench wrote, outside every
            // mod folder). It has no load-order position, and here one is not invented: a patch planned against this
            // order's winners is meant to win, so it loads LAST, after the order the names below build, and the identity
            // says so. A bare name keeps SyntheticOrder's rule (refused without a loadorder.txt position).
            var patchFiles = new List<string>();
            var named = new List<string>();
            foreach (var e in enable ?? Array.Empty<string>())
            {
                if (!IsPatchPath(e)) { named.Add(e); continue; }
                if (!File.Exists(e)) return Refuse($"--enable {e}: no plugin file is there; give the path to a written patch, or a plugin name.");
                patchFiles.Add(Path.GetFullPath(e));
            }
            var syn = SyntheticOrder.Build(profileDir, modsDir, dataDir, overwriteDir, named, disable);
            // A --enable the order could not take would leave the run measuring the order without it.
            if (syn.Unbuildable.Count > 0) return Refuse(EnableRefusal(syn.Unbuildable));
            if (syn.RefusedForMasters.Count > 0)
                return Refuse(OrderMasters.Sentence(syn.RefusedForMasters,
                    g => $"also --enable {OrderMasters.Names(g.Masters)} (install it first if no folder has it) or leave '{g.Plugin}' off"));
            paths = syn.OrderedPaths.ToList();
            foreach (var pf in patchFiles)
            {
                var fn = Path.GetFileName(pf);
                if (paths.Any(x => string.Equals(Path.GetFileName(x), fn, StringComparison.OrdinalIgnoreCase)))
                    return Refuse($"--enable {pf}: a plugin named '{fn}' is already in this order, and the game loads one file of a name; rename the patch.");
                paths.Add(pf);
                applied.Add($"ENABLED  {fn} (patch file, loaded last) from {pf}");
                limits.Add($"'{fn}' is a patch file from outside the MO2 layout, loaded LAST: it has no load-order position of its "
                         + "own, and last is where a patch planned against this order's winners belongs. Registered in MO2 at "
                         + "another position, it could lose records it wins here.");
            }
            names = paths.Select(Path.GetFileName).ToList()!;
            warnings.AddRange(syn.Warnings);
            applied.AddRange(syn.Applied);
            if (syn.Applied.Count == 0 && patchFiles.Count == 0)
                applied.Add("(nothing changed — every named plugin was already in that state)");
        }
        else
        {
            source = $"profile:{profileName}";
            viewMods = comp.EnabledMods;
            var order = Mo2LoadOrder.Build(profileDir, modsDir, dataDir, overwriteDir);
            paths = order.OrderedPaths.ToList();
            warnings.AddRange(order.Warnings);
            // The names the archive discovery keys on are the profile's ACTIVE ordered names — including one whose
            // file no layer provides (Build drops it from the paths and warns). Taking them from the composition
            // rather than from the paths keeps this path identical to what ArchiveDiscovery.Discover computed before.
            var inactive = new HashSet<string>(comp.InactivePluginNames, StringComparer.OrdinalIgnoreCase);
            names = comp.OrderedPluginNames.Where(n => !inactive.Contains(n)).ToList();
        }

        // The identity's plugin list is what was actually LOADED, in order, with its content hash and the layer it
        // came from. Hashing is the only expensive part of this reader, so it is timed and cached.
        var hashClock = Stopwatch.StartNew();
        var plugins = new List<ConfigPlugin>(paths.Count);
        var inView = new HashSet<string>(viewMods, StringComparer.OrdinalIgnoreCase);
        var outOfView = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var loadedMasters = new List<(string Name, IReadOnlyList<string>? Masters)>(paths.Count);
        for (int i = 0; i < paths.Count; i++)
        {
            var path = paths[i];
            var name = i < names.Count && orderFile is not null ? names[i] : Path.GetFileName(path);
            var (sha, bytes) = Hash(path);
            var hdr = Header(path);
            var provider = ProviderOf(path, overwriteDir, modsDir, dataDir, viewMods);
            if (hdr is null)
                warnings.Add($"'{name}': the TES4 header would not read, so its master flag and master list are recorded "
                           + "EMPTY in the identity. That is an unreadable file, not a claim that it has no masters.");
            plugins.Add(new ConfigPlugin(name, path, sha, bytes, provider,
                                         hdr?.MasterFlag ?? false, hdr?.LightFlag ?? false,
                                         hdr?.Masters ?? Array.Empty<string>()));
            loadedMasters.Add((name, hdr?.Masters));
            if (provider is not ("overwrite" or "Data") && !inView.Contains(provider))
            {
                if (!outOfView.TryGetValue(provider, out var list)) outOfView[provider] = list = new List<string>();
                list.Add(name);
            }
        }
        // The game will not start on a missing master (ruling 12): refused here for the profile and a what-if; a candidate
        // was already refused by CheckLegality with the same sentence.
        var gaps = OrderMasters.Gaps(loadedMasters);
        if (gaps.Count > 0)
        {
            var disabled = new HashSet<string>(disable ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            var profileRemedy = OrderMasters.ProfileRemedy(comp, modsDir, dataDir, overwriteDir);
            return Refuse(OrderMasters.Sentence(gaps, g =>
                g.Masters.Where(disabled.Contains).ToList() is { Count: > 0 } off
                    ? $"also --disable '{g.Plugin}' or keep {OrderMasters.Names(off)}"
                    : profileRemedy(g)));
        }
        // One line per offending FOLDER, naming its plugins — not one per plugin. A profile with the Creation Club
        // content installed as a disabled mod puts seventy of them in this bucket at once, and seventy near-identical
        // sentences bury the one that matters.
        foreach (var (folder, list) in outOfView)
            limits.Add($"{list.Count} plugin(s) were resolved from the folder '{folder}', which is NOT in this "
                     + "configuration's asset view: their RECORDS load, but that folder's meshes, textures, scripts and "
                     + "BSAs are not in the view, and the copy read there may not be the copy the game would load. A "
                     + $"geometry or script result that depends on them is not established here. [{string.Join(", ", list)}]");

        var providers = new List<ConfigProvider>(viewMods.Count + 2);
        if (overwriteDir.Length > 0) providers.Add(new ConfigProvider("overwrite", "overwrite"));
        foreach (var m in viewMods) providers.Add(new ConfigProvider(m, "mod"));
        if (dataDir.Length > 0) providers.Add(new ConfigProvider("Data", "data"));

        // The runtime overlays this configuration's VFS exposes. Listed whether or not THIS probe reads them: the
        // identity describes the configuration, and which of it a probe consumed is the probe's own coverage block.
        var overlays = new List<ConfigOverlay>();
        foreach (var ini in BosDiscovery.Discover(LooseRoots(overwriteDir, modsDir, dataDir, viewMods)))
        {
            try { overlays.Add(new ConfigOverlay("bos-ini", ini.Path, ConfigurationIdentity.Sha256File(ini.Path))); }
            catch (Exception ex) { warnings.Add($"BOS ini '{ini.Name}' would not hash ({Concise(ex)}); it is left OUT of the identity, so two runs that differ only in it will look the same — say so if it matters."); }
        }
        hashClock.Stop();

        var archives = ArchiveDiscovery.DiscoverFor(names, viewMods, profileDir, modsDir, dataDir, overwriteDir, gamePath);
        warnings.AddRange(archives.Warnings);

        return new ResolvedConfiguration(paths, viewMods, archives, source, plugins, providers, overlays,
                                         limits, warnings, applied, false, null, hashClock.Elapsed.TotalSeconds);
    }

    /// <summary>The lines a probe prints for the configuration it resolved: what changed (a synthetic arm), what is
    /// NOT modelled (the limits), and the warnings that are worth a reader's eye. The fingerprint is deliberately not
    /// here — it is keyed with the population and state at emit time, and printing an unkeyed one would put a number
    /// on screen that no emit contains.</summary>
    public static void Print(ResolvedConfiguration cfg, TextWriter? w = null)
    {
        w ??= Console.Out;
        w.WriteLine($"configuration: {cfg.Source} — {cfg.OrderedPaths.Count} plugins, {cfg.Providers.Count} asset providers, "
                  + $"{cfg.Overlays.Count} BOS ini, {cfg.Archives.Archives.Count} archives (hashed in {cfg.HashSeconds:0.0}s)");
        foreach (var a in cfg.Applied) w.WriteLine($"   {a}");
        foreach (var l in cfg.Limits) w.WriteLine($"   LIMIT: {l}");
        foreach (var warn in cfg.Warnings.Where(Notable)) w.WriteLine($"   ! {warn}");
    }

    static bool Notable(string w)
        => w.Contains("REFUSED") || w.Contains("MISSING MASTER") || w.Contains("nothing to do")
        || w.Contains("unreadable") || w.Contains("would not read") || w.Contains("would not hash")
        || w.Contains("could not read");

    /// <summary>The loose roots in VFS precedence — overwrite, the view's mods highest-priority-first, Data. The same
    /// rule AssetResolver and BosHook use, restated here so a probe that links neither still resolves overlays.</summary>
    public static IReadOnlyList<(string Name, string Dir)> LooseRoots(
        string overwriteDir, string modsDir, string dataDir, IReadOnlyList<string> viewMods)
    {
        var roots = new List<(string, string)>(viewMods.Count + 2);
        if (overwriteDir.Length > 0) roots.Add(("overwrite", overwriteDir));
        foreach (var m in viewMods) roots.Add((m, Path.Combine(modsDir, m)));
        if (dataDir.Length > 0) roots.Add(("Data", dataDir));
        return roots;
    }

    // ---------------------------------------------------------------------------------------------
    //  Candidate legality — the four orders the engine would not load, each named per plugin
    // ---------------------------------------------------------------------------------------------

    /// <summary>Every reason this exact sequence is not an order the game could load: a plugin ahead of one of its own
    /// masters, a master absent from the loaded set, a non-master plugin ahead of a master-flagged one. A plugin whose
    /// header will not read makes NO claim — it is warned about and skipped, never assumed master-less.</summary>
    static List<string> CheckLegality(IReadOnlyList<string> names, IReadOnlyList<string> paths, List<string> warnings)
    {
        var refusals = new List<string>();
        var pos = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < names.Count; i++) pos[names[i]] = i;

        var headers = new PluginHeader?[names.Count];
        for (int i = 0; i < names.Count; i++)
        {
            headers[i] = Header(paths[i]);
            if (headers[i] is null)
                warnings.Add($"'{names[i]}': the TES4 header would not read, so the master and load-group checks were "
                           + "SKIPPED for it. An unreadable header is not a clean bill of health.");
        }

        var gaps = OrderMasters.Gaps(names.Select((n, i) => (n, headers[i]?.Masters)).ToList());
        if (gaps.Count > 0)
            refusals.Add(OrderMasters.Sentence(gaps,
                g => $"add {OrderMasters.Names(g.Masters)} to the candidate ahead of '{g.Plugin}' or drop '{g.Plugin}'"));

        for (int i = 0; i < names.Count; i++)
        {
            if (headers[i] is not { } h) continue;
            foreach (var m in h.Masters)
            {
                if (!pos.TryGetValue(m, out int j))
                    continue;                                   // the missing-master sentence above names it
                else if (j > i)
                    refusals.Add($"candidate puts '{names[i]}' at position {i} ahead of its master '{m}' at position {j} "
                               + "— the engine loads a plugin's masters before it, so this is not an order it would use.");
            }
        }

        for (int i = 0; i < names.Count; i++)
        {
            if (headers[i] is not { MasterFlag: false }) continue;
            for (int j = i + 1; j < names.Count; j++)
            {
                if (headers[j] is not { MasterFlag: true }) continue;
                refusals.Add($"candidate puts the non-master plugin '{names[i]}' at position {i} ahead of the "
                           + $"master-flagged '{names[j]}' at position {j} — the engine loads every master-flagged "
                           + "plugin before any non-master, so the order it would actually use is not this one.");
                break;   // one pair per offending plugin: the first master after it is the whole finding
            }
        }
        return refusals;
    }

    /// <summary>filename -> winning path, MO2's precedence with the CANDIDATE'S view on top: overwrite, then the view's
    /// mod folders in their priority, then the game's Data folder — and only BELOW all of that, the folders the view
    /// does not contain (mods the candidate drops, then MO2's disabled ones).
    /// <para>Data before the out-of-view folders, not after, and the ordering is load-bearing: MO2 puts nothing from a
    /// folder outside the view into the game's view, so for a plugin Data provides, the Data copy is the one that
    /// loads. The out-of-view block exists for one job only — supplying a plugin that nothing else provides, the
    /// what-if of switching on a plugin whose only copy sits in a switched-off folder. A plugin resolved there loads
    /// its records and none of its assets, which is why the caller records it in Limits. (SyntheticOrder ranked that
    /// block above Data until 2026-09-11; on a profile whose Creation Club plugins also sit, with different bytes, in a
    /// switched-off folder, that silently fed every <c>--enable</c> arm the other copies. Its header says how.)</para></summary>
    /// <summary>Plugin file name to the copy the probes load for a view, by <see cref="BuildLayerMap"/>'s precedence.</summary>
    public static IReadOnlyDictionary<string, string> PluginLayerMap(
        string profileDir, IReadOnlyList<string> viewMods, string modsDir, string dataDir, string overwriteDir)
        => BuildLayerMap(Mo2LoadOrder.ReadComposition(profileDir), viewMods, modsDir, dataDir, overwriteDir);

    static Dictionary<string, string> BuildLayerMap(
        Mo2Composition comp, IReadOnlyList<string> viewMods, string modsDir, string dataDir, string overwriteDir)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var layer in Layers(comp, viewMods, modsDir, dataDir, overwriteDir))
            foreach (var f in LayerPlugins(layer.Dir))
            {
                var fn = Path.GetFileName(f);
                if (!map.ContainsKey(fn)) map[fn] = f;      // first seen wins == highest precedence
            }
        return map;
    }

    /// <summary>One folder of the precedence: its name ("overwrite", the mod folder as the view or the profile spells it,
    /// "Data"), its path, and whether the view contains it.</summary>
    public sealed record PluginLayer(string Name, string Dir, bool InView);

    /// <summary>One layer's copy of a plugin file.</summary>
    public sealed record PluginLayerCopy(string Layer, string Path, bool InView);

    // The folders a plugin file is looked for in, highest precedence first. A null profile has no folders outside the view.
    static IEnumerable<PluginLayer> Layers(
        Mo2Composition? comp, IReadOnlyList<string> viewMods, string modsDir, string dataDir, string overwriteDir)
    {
        yield return new PluginLayer("overwrite", overwriteDir, true);
        var inView = new HashSet<string>(viewMods, StringComparer.OrdinalIgnoreCase);
        foreach (var m in viewMods) yield return new PluginLayer(m, Path.Combine(modsDir, m), true);
        yield return new PluginLayer("Data", dataDir, true);   // the game's own copy beats any out-of-view folder's
        if (comp is null) yield break;
        foreach (var m in comp.EnabledMods) if (!inView.Contains(m)) yield return new PluginLayer(m, Path.Combine(modsDir, m), false);
        foreach (var m in comp.DisabledMods) if (!inView.Contains(m)) yield return new PluginLayer(m, Path.Combine(modsDir, m), false);
    }

    // The plugin files at a folder's top level; a blank, absent or unreadable folder has none.
    static IEnumerable<string> LayerPlugins(string dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) yield break;
        string[] files;
        try { files = Directory.GetFiles(dir); }
        catch { yield break; }
        foreach (var f in files)
        {
            var e = Path.GetExtension(f);
            if (e.Equals(".esp", StringComparison.OrdinalIgnoreCase)
             || e.Equals(".esm", StringComparison.OrdinalIgnoreCase)
             || e.Equals(".esl", StringComparison.OrdinalIgnoreCase)) yield return f;
        }
    }

    /// <summary>Plugin file name to every layer's copy of it, in <see cref="BuildLayerMap"/>'s precedence, so the first of
    /// each is the copy the probes load. A folder named twice gives its copy once. A null <paramref name="profileDir"/>
    /// (no profile named) and a blank <paramref name="dataDir"/> (no game named) leave those layers out; a profile
    /// whose modlist.txt is absent has no folders outside the view, as for the map.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<PluginLayerCopy>> PluginLayerCopies(
        string? profileDir, IReadOnlyList<string> viewMods, string modsDir, string dataDir, string overwriteDir)
    {
        var comp = profileDir is null ? null : Mo2LoadOrder.ReadComposition(profileDir);
        var copies = new Dictionary<string, IReadOnlyList<PluginLayerCopy>>(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var layer in Layers(comp, viewMods, modsDir, dataDir, overwriteDir))
            foreach (var f in LayerPlugins(layer.Dir))
            {
                if (!seen.Add(f)) continue;
                var fn = Path.GetFileName(f);
                if (!copies.TryGetValue(fn, out var list)) copies[fn] = list = new List<PluginLayerCopy>();
                ((List<PluginLayerCopy>)list).Add(new PluginLayerCopy(layer.Name, f, layer.InView));
            }
        return copies;
    }

    /// <summary>Which layer a resolved path came from: "overwrite", the mod folder's own name, "Data", or — when it is
    /// under none of the three — the directory itself, said plainly rather than guessed at.</summary>
    /// <summary>Whether an --enable value names a plugin FILE by path (a patch outside the layout) rather than a plugin by
    /// name: it holds a directory separator or is rooted.</summary>
    public static bool IsPatchPath(string enable) =>
        Path.IsPathRooted(enable) || enable.Contains('/') || enable.Contains('\\');

    static string ProviderOf(string path, string overwriteDir, string modsDir, string dataDir, IReadOnlyList<string>? viewMods = null)
    {
        string full = Path.GetFullPath(path);
        if (Under(full, overwriteDir)) return "overwrite";
        // a view entry may name a mod folder by absolute path (a patch in the workbench's output folder): a file under it
        // is provided by that entry, spelled as the view spells it, so the identity does not call it out of view
        foreach (var m in viewMods ?? Array.Empty<string>())
            if (Path.IsPathRooted(m) && Under(full, m)) return m;
        if (Under(full, modsDir))
        {
            var rel = Path.GetRelativePath(Path.GetFullPath(modsDir), full);
            var first = rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
            if (first.Length > 0) return first;
        }
        if (Under(full, dataDir)) return "Data";
        return $"(outside the MO2 layout) {Path.GetDirectoryName(full)}";
    }

    static bool Under(string full, string root)
    {
        if (string.IsNullOrWhiteSpace(root)) return false;
        var r = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
              + Path.DirectorySeparatorChar;
        return full.StartsWith(r, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------------------------------------
    //  Hashing, cached by (path, length, mtime) for the life of the process
    // ---------------------------------------------------------------------------------------------

    static readonly Dictionary<(string Path, long Bytes, long Ticks), (string Sha, long Bytes)> _hashes = new();
    static readonly Dictionary<string, PluginHeader?> _headers = new(StringComparer.OrdinalIgnoreCase);

    static (string Sha, long Bytes) Hash(string path)
    {
        FileInfo fi;
        try { fi = new FileInfo(path); }
        catch { return ("unreadable", 0); }
        var key = (path.ToLowerInvariant(), fi.Exists ? fi.Length : -1, fi.Exists ? fi.LastWriteTimeUtc.Ticks : 0);
        if (_hashes.TryGetValue(key, out var hit)) return hit;
        (string, long) v;
        // A file that will not hash is recorded as "unreadable", which is NOT a content hash: two unreadable files
        // compare equal, and the caller's warning is what says so. Silently hashing empty would be worse.
        try { v = (ConfigurationIdentity.Sha256File(path), fi.Length); }
        catch { v = ("unreadable", fi.Exists ? fi.Length : 0); }
        _hashes[key] = v;
        return v;
    }

    static PluginHeader? Header(string path)
    {
        if (_headers.TryGetValue(path, out var hit)) return hit;
        return _headers[path] = PluginHeader.TryRead(path);
    }

    /// <summary>The one sentence for a candidate whose BaseProfile is not the profile this run resolves it through (letter
    /// case aside, as MO2 profile folders are on Windows), or null when they match. Starts "PROFILE MISMATCH" so the
    /// controllers' refusal marker finds it; exit 2, nothing written.</summary>
    public static string? ProfileMismatch(string? baseProfile, string candidateFile, string profileName)
    {
        var file = Path.GetFileName(candidateFile);
        if (string.IsNullOrWhiteSpace(baseProfile))
            return $"PROFILE MISMATCH - candidate '{file}' names no BaseProfile, so nothing says which profile its plugin and "
                 + $"mod names belong to; add \"BaseProfile\": \"{profileName}\" if it was built on profile '{profileName}', "
                 + "or rebuild it there, then run again.";
        if (string.Equals(baseProfile, profileName, StringComparison.OrdinalIgnoreCase)) return null;
        return $"PROFILE MISMATCH - candidate BaseProfile '{baseProfile}' does not match --profile '{profileName}' ('{file}'), "
             + $"so its plugins and mod folders would be resolved through the lists of profile '{profileName}', not the "
             + $"profile it was built on; run with --profile '{baseProfile}', or rebuild the candidate on profile "
             + $"'{profileName}', then run again.";
    }

    /// <summary>The one sentence for the --enable requests the order could not take (non-empty, in the order asked):
    /// the first named with its remedy, chosen from whether loadorder.txt lists it and whether any layer has the file;
    /// the rest counted. Starts "CANNOT ENABLE"; exit 2, nothing written.</summary>
    public static string EnableRefusal(IReadOnlyList<EnableGap> gaps)
    {
        var g = gaps[0];
        var p = g.Plugin;
        var more = gaps.Count == 1 ? ""
            : $" (and {gaps.Count - 1} more --enable request(s) cannot be built, first '{gaps[1].Plugin}')";
        const string nowhere = "no mod folder, the overwrite layer or the game Data folder";
        if (g.Listed)
            return $"CANNOT ENABLE - '{p}' is listed in loadorder.txt, but {nowhere} provides it{more}, so this run would "
                 + $"measure the order without it; install the mod that provides '{p}', then run again.";
        var remedy = g.Installed
            ? $"switch '{p}' on once in MO2 so loadorder.txt lists it, or give it a position in an --order candidate"
            : $"install the mod that provides '{p}' ({nowhere} has a file by that name)";
        return $"CANNOT ENABLE - '{p}' has no load-order position: loadorder.txt does not list it{more}, so this run would "
             + $"measure the order without it; {remedy}, then run again.";
    }

    static string Concise(Exception ex) => $"{ex.GetType().Name}: {ex.Message}";

    static ResolvedConfiguration Refuse(string reason)
        => new(Array.Empty<string>(), Array.Empty<string>(),
               new ArchiveDiscoveryResult(Array.Empty<ActiveArchive>(), Array.Empty<string>()),
               "refused", Array.Empty<ConfigPlugin>(), Array.Empty<ConfigProvider>(), Array.Empty<ConfigOverlay>(),
               Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), true, reason, 0);
}
