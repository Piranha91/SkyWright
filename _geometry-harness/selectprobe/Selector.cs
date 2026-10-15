using System.Diagnostics;
using HarnessShared;
using HousecarlCore;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

// The order index and the per-newcomer selection. One Selector holds one resolved order; every newcomer is selected
// against it through SelectionRule.Judge, which is pairwise, so a batch and a single run give the same selection.

sealed class NewcomerSpec
{
    public string Id = "";
    public string? FolderPath;                          // null: plugin files were named
    public List<string> Companions = new();             // further folders of the same mod: assets only
    public List<string> PluginPaths = new();
    public List<SelNamed> Packaged = new();
    public string? Refused;                              // set: nothing else is meaningful
    public NewcomerFacts? Facts;
    public HashSet<string>? Assets;                     // read up front: the asset index is built for every newcomer's paths
    public List<string> ArchivesRead = new();
    public int Loose, InArchives;
    public List<string> RuntimeFiles = new();
    public int ScriptFiles;
    public List<string> Notes = new();
}

sealed class Selector
{
    readonly ResolvedConfiguration _cfg;
    readonly string _modsDir, _dataDir, _overwriteDir;
    readonly bool _reverseLinks;
    readonly Dictionary<string, ConfigPlugin> _byName = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _base = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, List<string>> _folderPlugins = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, Footprint> _fp = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, HashSet<string>> _looseHits = new(StringComparer.OrdinalIgnoreCase);      // folder -> wanted paths
    readonly Dictionary<string, HashSet<string>> _archiveHits = new(StringComparer.OrdinalIgnoreCase);    // owning plugin -> wanted paths
    readonly List<string> _assetFailures = new();
    public double FootprintSeconds, AssetSeconds;
    public int FootprintsRead;

    public Selector(ResolvedConfiguration cfg, string modsDir, string dataDir, string overwriteDir, bool reverseLinks)
    {
        _cfg = cfg; _modsDir = modsDir; _dataDir = dataDir; _overwriteDir = overwriteDir; _reverseLinks = reverseLinks;
        foreach (var p in cfg.Plugins)
        {
            _byName[p.Name] = p;
            if (!_folderPlugins.TryGetValue(p.Provider, out var l)) _folderPlugins[p.Provider] = l = new();
            l.Add(p.Name);
        }
        // Base: what the game's own Data folder provides — read off the folder, never a list of names.
        if (Directory.Exists(dataDir))
            foreach (var f in Directory.EnumerateFiles(dataDir))
                if (IsPlugin(f)) _base.Add(Path.GetFileName(f));
    }

    public static bool IsPlugin(string f)
    {
        var e = Path.GetExtension(f);
        return e.Equals(".esp", StringComparison.OrdinalIgnoreCase) || e.Equals(".esm", StringComparison.OrdinalIgnoreCase)
            || e.Equals(".esl", StringComparison.OrdinalIgnoreCase);
    }

    static List<string> Masters(string path)
    {
        using var mod = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE);
        return mod.ModHeader.MasterReferences.Select(m => m.Master.FileName.String).ToList();
    }

    // ------------------------------------------------------------------------------------------------------------
    //  The newcomer
    // ------------------------------------------------------------------------------------------------------------

    public NewcomerSpec Prepare(string id, IReadOnlyList<string> paths)
    {
        var s = new NewcomerSpec { Id = id };
        try { PrepareInner(s, paths); }
        catch (Exception ex) { s.Refused ??= $"the newcomer could not be read — {Footprint.Concise(ex)}."; }
        return s;
    }

    void PrepareInner(NewcomerSpec s, IReadOnlyList<string> paths)
    {
        if (paths.Count >= 1 && paths.All(Directory.Exists))
        {
            s.FolderPath = Path.GetFullPath(paths[0]);
            // further folders named on the command line: companions, read for assets only
            foreach (var extra in paths.Skip(1)) s.Companions.Add(Path.GetFullPath(extra).TrimEnd('\\', '/'));
            var all = Directory.EnumerateFiles(s.FolderPath).Where(IsPlugin).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
            if (all.Count == 0) { s.Refused = $"the newcomer folder '{s.FolderPath}' holds no plugin; an asset-only mod has no records to select with yet."; return; }
            var inFolder = new HashSet<string>(all.Select(Path.GetFileName)!, StringComparer.OrdinalIgnoreCase);
            var masters = all.ToDictionary(f => Path.GetFileName(f)!, Masters, StringComparer.OrdinalIgnoreCase);
            // roots: plugins that master nothing in the folder; their outside masters are what the mod requires
            var roots = masters.Where(kv => !kv.Value.Any(inFolder.Contains)).Select(kv => kv.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var required = new HashSet<string>(roots.SelectMany(r => masters[r]).Where(m => !inFolder.Contains(m)), StringComparer.OrdinalIgnoreCase);
            // a plugin another plugin of the folder masters is part of the mod (a main .esp under its own .esm, which
            // the folder's own patches master), whatever outside plugins it masters itself
            var masteredInFolder = new HashSet<string>(masters.SelectMany(kv => kv.Value.Where(m => inFolder.Contains(m) && !m.Equals(kv.Key, StringComparison.OrdinalIgnoreCase))),
                                                       StringComparer.OrdinalIgnoreCase);
            var keep = new List<string>();
            foreach (var f in all)
            {
                var n = Path.GetFileName(f)!;
                var outside = masters[n].Where(m => !inFolder.Contains(m) && !_base.Contains(m) && !required.Contains(m)).ToList();
                if (!roots.Contains(n) && !masteredInFolder.Contains(n) && outside.Count > 0)
                    s.Packaged.Add(new SelNamed(n, "a patch packaged in the newcomer's folder: it masters " + string.Join(", ", outside)
                                                + " outside the folder; set aside, never evidence"));
                else keep.Add(f);
            }
            // a folder some of whose plugins the order already loads: the user's choice of those plugins stands
            var active = keep.Where(f => _byName.TryGetValue(Path.GetFileName(f)!, out var cp)
                                         && Path.GetFullPath(cp.Path).Equals(Path.GetFullPath(f), StringComparison.OrdinalIgnoreCase)).ToList();
            if (active.Count > 0 && active.Count < keep.Count)
                s.Notes.Add($"{keep.Count - active.Count} plugin(s) in the folder are not loaded by the order and are left out of the newcomer: "
                          + string.Join(", ", keep.Except(active).Select(Path.GetFileName)));
            s.PluginPaths = active.Count > 0 ? active : keep;
        }
        else
        {
            foreach (var p in paths)
            {
                if (!File.Exists(p) || !IsPlugin(p)) { s.Refused = $"'{p}' is neither a mod folder nor a plugin file."; return; }
                s.PluginPaths.Add(Path.GetFullPath(p));
            }
            s.Notes.Add("the newcomer was named as plugin files, so it has no folder and no assets are compared");
        }
        if (s.PluginPaths.Count == 0) { s.Refused = "every plugin in the newcomer folder is a packaged patch; there is no newcomer plugin left."; return; }

        var names = new HashSet<string>(s.PluginPaths.Select(Path.GetFileName)!, StringComparer.OrdinalIgnoreCase);

        // companions found in the order: an ENABLED folder that ships a losing copy of a newcomer plugin is the same mod
        // spread over two folders (a main folder under a bug-fix or update folder); MO2 merges both into one view
        if (s.FolderPath is not null)
            foreach (var m in _cfg.EnabledMods)
            {
                var dir = Path.GetFullPath(Path.Combine(_modsDir, m)).TrimEnd('\\', '/');
                if (dir.Equals(s.FolderPath.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase) || s.Companions.Contains(dir, StringComparer.OrdinalIgnoreCase)) continue;
                if (names.Any(n => File.Exists(Path.Combine(dir, n)))) s.Companions.Add(dir);
            }
        s.Companions.Sort(StringComparer.OrdinalIgnoreCase);

        // its assets
        var assets = new HashSet<string>(StringComparer.Ordinal);
        if (s.FolderPath is not null)
        {
            foreach (var a in AssetPaths.Loose(s.FolderPath)) { assets.Add(a); s.Loose++; }
            foreach (var c in s.Companions) foreach (var a in AssetPaths.Loose(c)) { if (assets.Add(a)) s.Loose++; }
            // its archives: those at its folder root named for its plugins, and any archive the order loads with one of
            // its plugins from another folder (a mod split over a main folder and an update folder)
            var arcs = AssetPaths.ArchivesFor(s.FolderPath, names).Concat(s.Companions.SelectMany(c => AssetPaths.ArchivesFor(c, names)))
                .Concat(_cfg.Archives.Archives.Where(x => names.Contains(x.OwningPlugin)).Select(x => x.Path))
                .Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var arc in arcs)
            {
                try { foreach (var a in AssetPaths.Archive(arc)) { assets.Add(a); s.InArchives++; } s.ArchivesRead.Add(Path.GetFileName(arc)); }
                catch (Exception ex) { s.Refused = $"the newcomer's archive '{Path.GetFileName(arc)}' could not be read — {Footprint.Concise(ex)}."; return; }
            }
            foreach (var f in Directory.EnumerateFiles(s.FolderPath, "*.ini", SearchOption.AllDirectories)
                                       .Concat(Directory.EnumerateFiles(s.FolderPath, "*.json", SearchOption.AllDirectories)))
            {
                var rel = Path.GetRelativePath(s.FolderPath, f);
                if (rel.StartsWith("fomod", StringComparison.OrdinalIgnoreCase)) continue;
                s.RuntimeFiles.Add(rel.Replace('/', '\\'));
            }
            s.RuntimeFiles.Sort(StringComparer.OrdinalIgnoreCase);
        }
        s.ScriptFiles = assets.Count(a => a.EndsWith(".pex", StringComparison.Ordinal));
        s.Assets = assets;
    }

    /// <summary>Read the newcomer's records, just before its selection: a batch holds one newcomer's records at a time,
    /// so its memory is the order index plus one newcomer, however many newcomers it carries.</summary>
    public void LoadRecords(NewcomerSpec s)
    {
        if (s.Refused is not null) return;
        // read the newcomer itself (with display info and its outgoing links)
        var names = new HashSet<string>(s.PluginPaths.Select(Path.GetFileName)!, StringComparer.OrdinalIgnoreCase);
        var union = new Footprint { Name = string.Join("+", names), Path = s.FolderPath ?? s.PluginPaths[0] };
        union.Info = new(); union.CellNames = new(StringComparer.Ordinal); union.Links = new();
        var nonBaseMasters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in s.PluginPaths)
        {
            var fp = Footprint.Read(p, _dataDir, links: true, info: true);
            if (fp.Error is not null) { s.Refused = $"newcomer plugin '{fp.Name}' {fp.Error}."; return; }
            union.Keys.UnionWith(fp.Keys); union.Containers.UnionWith(fp.Containers); union.Interior.UnionWith(fp.Interior);
            union.Exterior.UnionWith(fp.Exterior); union.PlacedBases.UnionWith(fp.PlacedBases); union.Links.UnionWith(fp.Links!);
            foreach (var kv in fp.Info!) union.Info.TryAdd(kv.Key, kv.Value);
            foreach (var kv in fp.CellNames!) union.CellNames.TryAdd(kv.Key, kv.Value);
            union.NewRecords += fp.NewRecords; union.Overrides += fp.Overrides; union.ScriptCarriers += fp.ScriptCarriers;
            foreach (var m in fp.Masters) if (!_base.Contains(m) && !names.Contains(m)) nonBaseMasters.Add(m);
            _newcomerPlugin[p] = fp;
        }
        union.Masters = nonBaseMasters.ToList();

        s.Facts = new NewcomerFacts(union, s.Assets!, nonBaseMasters, names, s.FolderPath);
    }

    /// <summary>Drop a selected newcomer's records.</summary>
    public void Release(NewcomerSpec s)
    {
        s.Facts = null;
        foreach (var p in s.PluginPaths) _newcomerPlugin.Remove(p);
    }

    readonly Dictionary<string, Footprint> _newcomerPlugin = new(StringComparer.OrdinalIgnoreCase);

    // ------------------------------------------------------------------------------------------------------------
    //  The order index, shared by every newcomer
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>Read every non-base plugin of the order once. Base plugins are never candidates and are not read.</summary>
    public void IndexRecords(Action<int>? progress = null)
    {
        var sw = Stopwatch.StartNew();
        int i = 0;
        foreach (var p in _cfg.Plugins)
        {
            i++;
            if (_base.Contains(p.Name) || _fp.ContainsKey(p.Name)) continue;
            _fp[p.Name] = Footprint.Read(p.Path, _dataDir, links: _reverseLinks, info: false);
            FootprintsRead++;
            progress?.Invoke(i);
        }
        FootprintSeconds = sw.Elapsed.TotalSeconds;
    }

    /// <summary>Find, in every enabled folder and every active archive, which of the wanted paths each provides.</summary>
    public void IndexAssets(HashSet<string> wanted)
    {
        var sw = Stopwatch.StartNew();
        var folders = new List<(string Name, string Dir)>();
        if (_overwriteDir.Length > 0) folders.Add(("overwrite", _overwriteDir));
        foreach (var m in _cfg.EnabledMods) folders.Add((m, Path.Combine(_modsDir, m)));
        foreach (var (name, dir) in folders)
        {
            HashSet<string>? hits = null;
            try
            {
                foreach (var a in AssetPaths.Loose(dir))
                    if (wanted.Contains(a)) (hits ??= new(StringComparer.Ordinal)).Add(a);
            }
            catch (Exception ex) { _assetFailures.Add($"folder '{name}' could not be listed — {Footprint.Concise(ex)}"); }
            if (hits is not null) _looseHits[name] = hits;
        }
        foreach (var arc in _cfg.Archives.Archives)
        {
            if (arc.OwningMod is null || arc.OwningMod.Equals("Data", StringComparison.OrdinalIgnoreCase)) continue;   // the game's own
            try
            {
                foreach (var a in AssetPaths.Archive(arc.Path))
                    if (wanted.Contains(a))
                    {
                        if (!_archiveHits.TryGetValue(arc.OwningPlugin, out var h)) _archiveHits[arc.OwningPlugin] = h = new(StringComparer.Ordinal);
                        h.Add(a);
                    }
            }
            catch (Exception ex) { _assetFailures.Add($"archive '{Path.GetFileName(arc.Path)}' could not be read — {Footprint.Concise(ex)}"); }
        }
        AssetSeconds = sw.Elapsed.TotalSeconds;
    }

    // ------------------------------------------------------------------------------------------------------------
    //  One newcomer's selection
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>What both rules share for one newcomer: the order after removal, the dependencies, every installed
    /// plugin to judge with its facts, and the parts of the output that do not depend on the rule.</summary>
    sealed class Ctx
    {
        public NewcomerFacts Facts = null!;
        public List<SelNamed> Removed = new(), Deps = new(), Unreadable = new();
        public List<SelExcluded> BaseExcluded = new();
        public List<InstalledFacts> Judged = new();
        public List<SelAssetOnly> AssetOnly = new();
        public List<string> Unmeasured = new();
        public SelNewcomer Newcomer = null!;
        public SelOrder Order = null!;
        public bool Incomplete;
    }

    Ctx Prepare(NewcomerSpec s)
    {
        var x = new Ctx();
        var facts = x.Facts = s.Facts!;
        _newcomerDirs = s.FolderPath is null ? new() : new List<string> { s.FolderPath.TrimEnd('\\', '/') }.Concat(s.Companions).ToList();
        var newcomerNames = facts.Plugins;
        var newcomerFolder = s.FolderPath;

        // 1. take the newcomer (and its packaged patches) out of the order, with everything that masters them
        var removed = x.Removed;
        var gone = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in newcomerNames.Concat(s.Packaged.Select(p => p.Plugin)))
            if (_byName.ContainsKey(n) && gone.Add(n))
                removed.Add(new SelNamed(n, newcomerNames.Contains(n) ? "the newcomer itself, already in the order" : "a patch packaged with the newcomer"));
        bool grew = true;
        while (grew)
        {
            grew = false;
            foreach (var p in _cfg.Plugins)
            {
                if (gone.Contains(p.Name)) continue;
                var hit = p.Masters.FirstOrDefault(gone.Contains);
                if (hit is null) continue;
                gone.Add(p.Name); grew = true;
                removed.Add(new SelNamed(p.Name, $"masters {hit}, which leaves with the newcomer"));
            }
        }
        var order = _cfg.Plugins.Where(p => !gone.Contains(p.Name)).ToList();
        var inOrder = new HashSet<string>(order.Select(p => p.Name), StringComparer.OrdinalIgnoreCase);

        // 2. the newcomer must load on what is left
        foreach (var (path, fp) in _newcomerPlugin.Where(kv => s.PluginPaths.Contains(kv.Key, StringComparer.OrdinalIgnoreCase)))
            foreach (var m in fp.Masters)
                if (!inOrder.Contains(m) && !newcomerNames.Contains(m) && !_base.Contains(m))
                    throw new RefusalException($"newcomer plugin '{fp.Name}' masters '{m}', which this order does not load; add or enable it first.");

        // 3. dependencies: the newcomer's masters, transitively, within the order
        var depSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var stack = new Stack<string>(_newcomerPlugin.Where(kv => s.PluginPaths.Contains(kv.Key, StringComparer.OrdinalIgnoreCase))
                                                     .SelectMany(kv => kv.Value.Masters).Where(m => !newcomerNames.Contains(m)));
        while (stack.Count > 0)
        {
            var m = stack.Pop();
            if (!depSet.Add(m)) continue;
            if (_byName.TryGetValue(m, out var cp)) foreach (var mm in cp.Masters) stack.Push(mm);
        }
        foreach (var p in order) if (depSet.Contains(p.Name) && !_base.Contains(p.Name)) x.Deps.Add(new SelNamed(p.Name, "a master of the newcomer (a dependency, not a clash partner)"));

        // 4. every other installed plugin, with the facts a rule judges
        foreach (var p in order)
        {
            string? folder = p.Provider is "Data" ? null : p.Provider;
            if (_base.Contains(p.Name)) { x.BaseExcluded.Add(new SelExcluded(p.Name, folder, "base game (provided by the game's Data folder)", null)); continue; }
            if (depSet.Contains(p.Name)) continue;
            if (!_fp.TryGetValue(p.Name, out var bfp)) { _fp[p.Name] = bfp = Footprint.Read(p.Path, _dataDir, _reverseLinks, false); FootprintsRead++; }
            if (bfp.Error is not null) { x.Unreadable.Add(new SelNamed(p.Name, bfp.Error)); continue; }
            var bAssets = AssetsOf(folder, newcomerFolder, inOrder);
            var nb = new HashSet<string>(bfp.Masters.Where(m => !_base.Contains(m)), StringComparer.OrdinalIgnoreCase);
            x.Judged.Add(new InstalledFacts(p.Name, folder, bfp, bAssets, nb));
        }

        // 5. asset-only folders: enabled folders that ship no plugin the order knows, sharing a path with the newcomer
        foreach (var (folder, hits) in _looseHits)
        {
            if (_folderPlugins.ContainsKey(folder) || IsNewcomerFolder(folder, newcomerFolder)) continue;
            var h = hits.Where(facts.Assets.Contains).OrderBy(v => v, StringComparer.Ordinal).ToList();
            if (h.Count > 0) x.AssetOnly.Add(new SelAssetOnly(folder, h.Count, h.Take(20).ToList()));
        }
        x.AssetOnly.Sort((a, b) => b.Assets != a.Assets ? b.Assets.CompareTo(a.Assets) : string.Compare(a.Folder, b.Folder, StringComparison.OrdinalIgnoreCase));

        x.Unmeasured.AddRange(new[]
        {
            "script logic: scripts are counted (newcomer.scriptFiles, newcomer.scriptCarriers), never read",
            "runtime layers (SKSE plugin configs, SPID, KID, SkyPatcher, BOS, OAR): the newcomer's .ini/.json files are listed (newcomer.runtimeLayerFiles), not interpreted",
            "door teleport destinations, LOD, and exterior cells beyond ring 2",
            "a patch packaged in the newcomer's folder: its loose assets cannot be told from the newcomer's and are counted as the newcomer's",
        });
        if (!_reverseLinks) x.Unmeasured.Add("reverse form links (installed records pointing at the newcomer's) were not read: noted.reverseFormLinks is null");
        x.Unmeasured.AddRange(_assetFailures);
        x.Unmeasured.AddRange(s.Notes);

        var narrowed = _cfg.NarrowedTo(order.Select(p => p.Path).ToList(), $"the newcomer and the {removed.Count} plugin(s) leaving with it are taken out");
        var identity = narrowed.Identity("newcomer-selection", "load-time");
        x.Newcomer = new SelNewcomer(newcomerFolder, s.Companions,
            s.PluginPaths.Select(p => _newcomerPlugin[p]).Select(fp => new SelNewcomerPlugin(fp.Name, fp.Path, ConfigurationIdentity.Sha256File(fp.Path), fp.Masters, fp.NewRecords, fp.Overrides)).ToList(),
            s.Packaged, facts.Fp.Keys.Count, facts.Fp.Interior.Count, facts.Fp.Exterior.Count, facts.Assets.Count, s.Loose, s.InArchives,
            s.ArchivesRead, facts.Fp.ScriptCarriers, s.RuntimeFiles.Take(50).ToList(), s.ScriptFiles);
        x.Order = new SelOrder(_cfg.Source, identity.Short, order.Count, removed);
        x.Incomplete = x.Unreadable.Count > 0 || _assetFailures.Count > 0;
        return x;
    }

    /// <summary>Rule v1 (4.26 part 1), its output unchanged.</summary>
    public Selection Select(NewcomerSpec s)
    {
        var x = Prepare(s);
        var picked = new List<(string, string?, SelEvidence, SelNoted, List<string>)>();
        var excluded = new List<SelExcluded>(x.BaseExcluded);
        foreach (var b in x.Judged)
        {
            var (ev, noted, kinds) = SelectionRule.Judge(x.Facts, b);
            if (kinds.Count > 0) picked.Add((b.Name, b.Folder, ev, noted, kinds));
            else excluded.Add(new SelExcluded(b.Name, b.Folder, SelectionRule.ExcludedReason(noted), noted));
        }
        return new Selection("selectprobe", 1, x.Newcomer, x.Order, x.Deps, SelectionRule.Rank(picked), x.AssetOnly,
            excluded.OrderBy(e => e.Plugin, StringComparer.OrdinalIgnoreCase).ToList(),
            x.Unreadable, x.Incomplete, x.Unmeasured,
            new SelRule(SelectionRule.Ring, SelectionRule.ContainerKinds, SelectionRule.SelectingKinds, SelectionRule.NotedKinds, _reverseLinks, SelectionRule.Ranking),
            null);
    }

    /// <summary>Rule v2 (selection v2): v1's kinds plus form links, features, a score and the control rank R-0.</summary>
    public SelectionV2 SelectV2(NewcomerSpec s)
    {
        var x = Prepare(s);
        var judged = new List<(InstalledFacts B, SelEvidence Ev, SelNoted Noted, List<string> Kinds, PairItems It)>();
        var df = new SelectionRuleV2.Multiplicity();
        foreach (var b in x.Judged)
        {
            var (ev, noted, kinds) = SelectionRule.Judge(x.Facts, b);
            var it = SelectionRuleV2.Items(x.Facts, b);
            df.Add(it);
            judged.Add((b, ev, noted, kinds, it));
        }
        var N = x.Facts.Fp;
        string Rec(FormKey k) => N.Info is not null && N.Info.TryGetValue(k, out var i) ? $"{k} {i.Type}{(i.Edid is null ? "" : " " + i.Edid)}" : k.ToString();
        var picked = new List<(string, string?, List<string>, SelEvidenceV2, Dictionary<string, double>, SelNoted)>();
        var excluded = new List<SelExcluded>(x.BaseExcluded);
        foreach (var (b, ev, noted, kinds, it) in judged)
        {
            var f = SelectionRuleV2.Features(x.Facts, b, it, noted, df);
            var k2 = new List<string>(kinds);
            if (SelectionRuleV2.LinksSelect(f)) k2.Add("formLinks");
            if (k2.Count == 0) { excluded.Add(new SelExcluded(b.Name, b.Folder, SelectionRuleV2.ExcludedReason(noted, it.LinksOut.Count + it.LinksIn.Count), noted)); continue; }
            var linkEx = it.LinksOut.OrderBy(k => k.ToString()).Select(k => "newcomer points at " + k)
                .Concat(it.LinksIn.OrderBy(k => k.ToString()).Select(k => "they point at " + Rec(k))).Take(20).ToList();
            var ev2 = new SelEvidenceV2(ev.Records, ev.RecordExamples, ev.Cells, ev.CellExamples, ev.Neighbours, ev.NeighbourExamples,
                                        ev.Assets, ev.AssetExamples, ev.NewcomerPlacesTheirBase, ev.TheyPlaceNewcomerBase, ev.BaseLinkExamples,
                                        it.LinksOut.Count, it.LinksIn.Count, linkEx);
            picked.Add((b.Name, b.Folder, k2, ev2, f, noted));
        }
        return new SelectionV2("selectprobe", 2, x.Newcomer, x.Order, x.Deps, SelectionRuleV2.Rank(picked), x.AssetOnly,
            excluded.OrderBy(e => e.Plugin, StringComparer.OrdinalIgnoreCase).ToList(),
            x.Unreadable, x.Incomplete, x.Unmeasured,
            new SelRuleV2(SelectionRuleV2.Version, SelectionRule.Ring, SelectionRule.ContainerKinds, SelectionRuleV2.SelectingKinds, SelectionRuleV2.NotedKinds,
                          _reverseLinks, SelectionRuleV2.LinkMin, SelectionRuleV2.Ranking, SelectionRuleV2.ControlRanking),
            null);
    }

    List<string> _newcomerDirs = new();

    /// <summary>Whether a provider is the newcomer's own folder or one of its companions.</summary>
    bool IsNewcomerFolder(string providerName, string? newcomerFolder)
        => providerName != "overwrite"
        && _newcomerDirs.Contains(Path.GetFullPath(Path.Combine(_modsDir, providerName)).TrimEnd('\\', '/'), StringComparer.OrdinalIgnoreCase);

    /// <summary>The wanted paths an installed plugin's folder provides: its loose files, and the archives that load with
    /// a plugin of that folder still in the order. Nothing from the newcomer's own folder.</summary>
    IReadOnlySet<string> AssetsOf(string? folder, string? newcomerFolder, HashSet<string> inOrder)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (folder is null || IsNewcomerFolder(folder, newcomerFolder)) return set;
        if (_looseHits.TryGetValue(folder, out var l)) set.UnionWith(l);
        if (_folderPlugins.TryGetValue(folder, out var plugins))
            foreach (var pl in plugins)
                if (inOrder.Contains(pl) && _archiveHits.TryGetValue(pl, out var a)) set.UnionWith(a);
        return set;
    }
}

sealed class RefusalException(string message) : Exception(message);
