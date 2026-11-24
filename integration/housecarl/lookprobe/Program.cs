using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HarnessShared;
using HousecarlCore;
using SkyWright.Engine;
using HousecarlCore.Progress;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Pex;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

// LOOKPROBE — the whole-order looks, run over a load order: a patch on disk and not enabled, a swap file pointing at a
// plugin or record the order does not supply, two mods' swaps on one base, a script whose parent or called class is
// absent, and a plugin or config naming an empty dummy plugin (src/housecarl-core/OrderLooks.cs and its shape files).
// It reads the instance's files and writes only --out and --text, which it refuses under the instance or the game.
//
// usage: lookprobe --instance DIR --profile NAME --game DIR [--order FILE] --out looks.json [--text looks.txt]
//        lookprobe --self-test

if (args.Length > 0 && args[0] == "--self-test") return LookSelfTest.Run();
using var __run = RunLog.BeginProcess("lookprobe", args);

string? instance = null, profile = null, game = null, order = null, outFile = null, textFile = null;
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--instance" && i + 1 < args.Length) instance = args[++i];
    else if (args[i] == "--profile" && i + 1 < args.Length) profile = args[++i];
    else if (args[i] == "--game" && i + 1 < args.Length) game = args[++i];
    else if (args[i] == "--order" && i + 1 < args.Length) order = args[++i];
    else if (args[i] == "--out" && i + 1 < args.Length) outFile = args[++i];
    else if (args[i] == "--text" && i + 1 < args.Length) textFile = args[++i];
    else { Console.Error.WriteLine($"unknown option '{args[i]}'; usage: lookprobe --instance DIR --profile NAME --game DIR [--order FILE] --out FILE [--text FILE]"); return 2; }
}
instance = HarnessTarget.Require(instance, "MO2 instance directory", "instance", "--instance");
game = HarnessTarget.Require(game, "game directory", "game", "--game");
profile = HarnessTarget.Require(profile, "MO2 profile name", "profile", "--profile");
if (outFile is null) { Console.Error.WriteLine("--out FILE is required: the looks are written there as JSON."); return 2; }
foreach (var f in new[] { outFile, textFile })
    if (f is not null && (LookProbe.Under(f, instance) || LookProbe.Under(f, game)))
    { Console.Error.WriteLine($"refused: '{f}' is inside the instance or the game folder; lookprobe never writes there. Write it to a run folder."); return 2; }
return LookProbe.Run(instance, profile, game, order, outFile, textFile, Console.Out);

public static class LookProbe
{
    static readonly string[] PluginExt = { ".esp", ".esm", ".esl" };
    static bool IsPlugin(string f) => PluginExt.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase);

    public static bool Under(string path, string dir)
    {
        var p = Path.GetFullPath(path).TrimEnd('\\', '/') + "\\";
        var d = Path.GetFullPath(dir).TrimEnd('\\', '/') + "\\";
        return p.StartsWith(d, StringComparison.OrdinalIgnoreCase);
    }

    public static int Run(string instance, string profile, string game, string? order, string outFile, string? textFile, TextWriter log)
    {
        var wall = Stopwatch.StartNew();
        string profileDir = Path.Combine(instance, "profiles", profile), modsDir = Path.Combine(instance, "mods");
        string overwrite = Path.Combine(instance, "overwrite"), dataDir = Path.Combine(game, "Data");
        var cfg = HarnessOrderCli.Resolve(profile, profileDir, modsDir, dataDir, overwrite, game, order, null, null);
        if (cfg.Refused) { Console.Error.WriteLine($"REFUSED: {cfg.RefusedReason}"); return 2; }
        var doc = Build(modsDir, dataDir, overwrite, cfg.OrderedPaths, cfg.EnabledMods, cfg.Archives.Archives, log);
        string hash;
        using (var sha = SHA256.Create())
        {
            var bytes = new List<byte>();
            foreach (var f in new[] { "plugins.txt", "modlist.txt" })
                if (File.Exists(Path.Combine(profileDir, f))) bytes.AddRange(File.ReadAllBytes(Path.Combine(profileDir, f)));
            hash = Convert.ToHexString(sha.ComputeHash(bytes.ToArray()));
        }
        doc["source"] = new Dictionary<string, object?> { ["instance"] = instance, ["profile"] = profile, ["configuration"] = cfg.Source,
            ["plugins"] = cfg.OrderedPaths.Count, ["profileSha256"] = hash, ["generatedUtc"] = DateTime.UtcNow.ToString("o") };
        doc["seconds"] = Math.Round(wall.Elapsed.TotalSeconds, 1);
        Write(doc, outFile, textFile);
        log.WriteLine($"lookprobe: wrote {outFile} in {wall.Elapsed.TotalSeconds:0.0}s");
        return 0;
    }

    public static void Write(Dictionary<string, object?> doc, string outFile, string? textFile)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(outFile));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(outFile, JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true }));
        if (textFile is not null) File.WriteAllText(textFile, (string)doc["text"]!);
    }

    /// <summary>Every look over one resolved order. <paramref name="loadedPaths"/>: plugin paths, masters first.</summary>
    public static Dictionary<string, object?> Build(string modsDir, string dataDir, string overwrite, IReadOnlyList<string> loadedPaths,
        IReadOnlyList<string> enabledMods, IReadOnlyList<ActiveArchive> archives, TextWriter log)
    {
        var t = Stopwatch.StartNew();
        string? FolderOf(string path)
        {
            if (Under(path, modsDir)) return Path.GetRelativePath(modsDir, path).Split('\\', '/')[0];
            if (Under(path, overwrite)) return "overwrite";
            return null;
        }
        var loadedName = loadedPaths.Select(Path.GetFileName).ToList();
        var provider = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in loadedPaths) provider[Path.GetFileName(p)] = FolderOf(p);
        var loadedSet = new HashSet<string>(loadedPaths.Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase);
        var enabled = new HashSet<string>(enabledMods, StringComparer.OrdinalIgnoreCase);

        // ---- dummies among the loaded plugins ----
        var dummies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unreadablePlugins = new List<string>();
        foreach (var p in loadedPaths)
        {
            var e = EmptyPlugin.IsEmpty(p);
            if (e is null) unreadablePlugins.Add(Path.GetFileName(p)); else if (e.Value) dummies.Add(Path.GetFileName(p));
        }

        // ---- plugins on disk: every mod folder's root and first-level subfolders, and the game's Data ----
        var onDisk = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var candidates = new List<(string Path, string Folder, bool Root)>();
        foreach (var d in Directory.Exists(modsDir) ? Directory.EnumerateDirectories(modsDir) : Enumerable.Empty<string>())
        {
            var folder = Path.GetFileName(d);
            if (folder.EndsWith("_separator", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                foreach (var f in Directory.EnumerateFiles(d).Where(IsPlugin))
                {
                    onDisk.Add(Path.GetFileName(f));
                    if (!loadedSet.Contains(Path.GetFullPath(f))) candidates.Add((f, folder, true));
                }
                foreach (var sub in Directory.EnumerateDirectories(d))
                {
                    var sn = Path.GetFileName(sub).ToLowerInvariant();
                    if (sn is "meshes" or "textures" or "sound" or "scripts" or "interface" or "seq" or "skse" or "music" or "grass" or "strings" or "video" or "lodsettings") continue;
                    foreach (var f in Directory.EnumerateFiles(sub).Where(IsPlugin)) { onDisk.Add(Path.GetFileName(f)); candidates.Add((f, folder, false)); }
                }
            }
            catch (Exception ex) { unreadablePlugins.Add($"{folder}: {ex.GetType().Name}"); }
        }
        if (Directory.Exists(dataDir)) foreach (var f in Directory.EnumerateFiles(dataDir).Where(IsPlugin)) onDisk.Add(Path.GetFileName(f));
        var disk = new List<DiskPlugin>();
        foreach (var (path, folder, root) in candidates)
        {
            IReadOnlyList<string> mlist = Array.Empty<string>();
            if (root)
            {
                try { using var m = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE); mlist = m.ModHeader.MasterReferences.Select(x => x.Master.FileName.String).ToList(); }
                catch { unreadablePlugins.Add(Path.GetFileName(path)); continue; }
            }
            disk.Add(new DiskPlugin(Path.GetFileName(path), folder, enabled.Contains(folder), root, mlist, root && EmptyPlugin.IsEmpty(path) == true));
        }
        var patches = InactivePatchLook.Evaluate(disk, provider, onDisk, dummies);
        log.WriteLine($"   inactive plugins read: {disk.Count} ({t.Elapsed.TotalSeconds:0.0}s)");

        // ---- one pass over the loaded plugins: the BOS index, masters, script attachments (winners), dummy uses ----
        using var assets = AssetResolver.Build(overwrite, modsDir, dataDir, enabledMods, archives);
        var idx = new OverlapIndex { Bos = new BosFormIndex() };
        var lightness = new List<(ModKey, bool)>();
        var masters = new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.OrdinalIgnoreCase);
        var vmadWinner = new Dictionary<FormKey, string[]>();
        var masterUses = new List<DummyMasterUse>();
        for (int p = 0; p < loadedPaths.Count; p++)
        {
            ISkyrimModGetter mod;
            try { mod = SkyrimMod.CreateFromBinaryOverlay(loadedPaths[p], SkyrimRelease.SkyrimSE); }
            catch { lightness.Add((ModKey.Null, false)); continue; }
            using (mod as IDisposable)
            {
                lightness.Add((mod.ModKey, BosHook.IsLight(mod)));
                var ms = mod.ModHeader.MasterReferences.Select(x => x.Master.FileName.String).ToList();
                masters[mod.ModKey.FileName.String] = ms;
                idx.Ingest(p, mod);
                var dm = ms.Where(dummies.Contains).ToList();
                foreach (var d in dm)
                {
                    int inSpace = 0, links = 0;
                    foreach (var rec in mod.EnumerateMajorRecords())
                    {
                        if (rec.FormKey.ModKey.FileName.String.Equals(d, StringComparison.OrdinalIgnoreCase)) inSpace++;
                        try { links += rec.EnumerateFormLinks().Count(l => l.FormKey.ModKey.FileName.String.Equals(d, StringComparison.OrdinalIgnoreCase)); } catch { }
                    }
                    masterUses.Add(new DummyMasterUse(mod.ModKey.FileName.String, provider.GetValueOrDefault(mod.ModKey.FileName.String), d, inSpace, links));
                }
                foreach (var rec in mod.EnumerateMajorRecords())
                {
                    if (rec is not IHaveVirtualMachineAdapterGetter have) continue;
                    IAVirtualMachineAdapterGetter? vm;
                    try { vm = have.VirtualMachineAdapter; } catch { continue; }
                    if (vm is null) { vmadWinner.Remove(rec.FormKey); continue; }
                    var names = vm.Scripts.Select(s => s.Name).ToList();
                    if (vm is IQuestAdapterGetter qa) foreach (var a in qa.Aliases) names.AddRange(a.Scripts.Select(s => s.Name));
                    vmadWinner[rec.FormKey] = names.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n!.Trim()).ToArray();
                }
            }
        }
        idx.Bos!.SetLoadOrder(lightness);
        log.WriteLine($"   loaded plugins read: {loadedPaths.Count} ({t.Elapsed.TotalSeconds:0.0}s)");

        // ---- BOS ----
        var inis = BosDiscovery.Discover(assets.LooseRoots);
        var layer = BaseObjectSwaps.Load(inis, idx.Bos);
        bool? IsRef(FormKey k) => idx.History.Of(k) is not null || idx.RefBase.ContainsKey(k) ? true : idx.Bos.Exists(k) ? false : null;
        var pointers = SwapPointerLook.Evaluate(inis, idx.Bos, loadedName, onDisk, dummies, IsRef, provider);
        var providerOfIni = inis.ToDictionary(i => i.Name, i => i.Provider, StringComparer.OrdinalIgnoreCase);
        var pluginsOf = provider.Where(kv => kv.Value is not null).GroupBy(kv => kv.Value!, StringComparer.OrdinalIgnoreCase)
                                .ToDictionary(g => g.Key, g => (IReadOnlyCollection<string>)g.Select(kv => kv.Key).ToList(), StringComparer.OrdinalIgnoreCase);
        var contests = SwapContestLook.Evaluate(layer, idx.Bos, providerOfIni,
            m => pluginsOf.TryGetValue(m, out var l) ? l : Array.Empty<string>(),
            p => masters.TryGetValue(p, out var l) ? l : Array.Empty<string>(),
            k => idx.RefBase.TryGetValue(k, out var b) ? b : null,
            k => idx.BaseInfo.TryGetValue(k, out var bi) ? bi.Edid : null);
        log.WriteLine($"   BOS: {inis.Count} inis ({t.Elapsed.TotalSeconds:0.0}s)");

        // ---- scripts: every winning scripts\*.pex ----
        var supplied = new Dictionary<string, (ScriptFacts, string?)>(StringComparer.OrdinalIgnoreCase);
        var pexUnreadable = new List<string>();
        var pexRel = assets.EnumerateUnder("scripts").Where(r => r.EndsWith(".pex", StringComparison.OrdinalIgnoreCase) && r.Count(c => c == '\\') == 1).ToList();
        var byArchive = new Dictionary<string, List<(string Rel, string Entry, string? Mod)>>(StringComparer.OrdinalIgnoreCase);
        void AddPex(byte[] bytes, string rel, string? mod)
        {
            try
            {
                using var ms = new MemoryStream(bytes);
                foreach (var f in ScriptDependencyLook.FactsOf(PexFile.CreateFromStream(ms, GameCategory.Skyrim))) supplied.TryAdd(f.Name, (f, mod));
            }
            catch { pexUnreadable.Add(rel); }
        }
        foreach (var rel in pexRel)
        {
            var place = assets.ResolveForPlacement(rel);
            if (place.Sources.Count == 0) continue;
            var s = place.Sources[0];
            if (s.Kind == AssetKind.Loose && s.LooseFilePath is { } lp)
            {
                try { AddPex(File.ReadAllBytes(lp), rel, FolderOf(lp)); } catch { pexUnreadable.Add(rel); }
            }
            else if (s.ArchivePath is { } ap)
            {
                if (!byArchive.TryGetValue(ap, out var l)) byArchive[ap] = l = new();
                l.Add((rel, s.EntryPath, s.OwningMod ?? FolderOf(ap)));
            }
        }
        foreach (var (ap, entries) in byArchive)
        {
            var got = AssetResolver.TryReadArchiveEntries(ap, entries.Select(e => e.Entry).ToList());
            foreach (var (rel, entry, mod) in entries)
                if (got.TryGetValue(entry, out var b)) AddPex(b, rel, mod); else pexUnreadable.Add(rel);
        }
        var notLoadedClass = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in Directory.Exists(modsDir) ? Directory.EnumerateDirectories(modsDir) : Enumerable.Empty<string>())
        {
            var folder = Path.GetFileName(d);
            if (enabled.Contains(folder)) continue;
            var sd = Path.Combine(d, "scripts");
            if (!Directory.Exists(sd)) continue;
            try { foreach (var f in Directory.EnumerateFiles(sd, "*.pex")) notLoadedClass.TryAdd(Path.GetFileNameWithoutExtension(f), folder); } catch { }
        }
        var attached = vmadWinner.Values.SelectMany(v => v).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var scripts = ScriptDependencyLook.Evaluate(supplied, attached, c => notLoadedClass.GetValueOrDefault(c));
        log.WriteLine($"   scripts: {supplied.Count} classes from {pexRel.Count} pex paths, {attached.Count} attached names ({t.Elapsed.TotalSeconds:0.0}s)");

        // ---- runtime configs naming forms ----
        var refs = new List<ConfigFormRef>(pointers.DummyRefs);
        var configFiles = new List<(string Rel, string Framework)>();
        var rootSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, dir) in assets.LooseRoots)
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var f in Directory.EnumerateFiles(dir, "*.ini"))
            {
                var fn = Path.GetFileName(f);
                string? fw = fn.EndsWith("_DISTR.ini", StringComparison.OrdinalIgnoreCase) ? "SPID" : fn.EndsWith("_KID.ini", StringComparison.OrdinalIgnoreCase) ? "KID" : null;
                if (fw is not null && rootSeen.Add(fn)) configFiles.Add((fn, fw));
            }
        }
        foreach (var rel in assets.EnumerateUnder("SKSE\\Plugins"))
        {
            var ext = Path.GetExtension(rel).ToLowerInvariant();
            if (ext is not (".ini" or ".toml" or ".json" or ".yaml" or ".yml")) continue;
            configFiles.Add((rel, rel.Contains("\\SkyPatcher\\", StringComparison.OrdinalIgnoreCase) ? "SkyPatcher" : "SKSE config"));
        }
        foreach (var rel in assets.EnumerateUnder("meshes", r => r.Contains("\\openanimationreplacer\\", StringComparison.OrdinalIgnoreCase)
                     ? r.EndsWith("\\config.json", StringComparison.OrdinalIgnoreCase) || r.EndsWith("\\user.json", StringComparison.OrdinalIgnoreCase)
                     : r.Contains("\\dynamicanimationreplacer\\", StringComparison.OrdinalIgnoreCase) && r.EndsWith("_conditions.txt", StringComparison.OrdinalIgnoreCase), int.MaxValue, out _))
            configFiles.Add((rel, rel.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? "OAR" : "DAR (read by OAR)"));
        var configUnreadable = new List<string>();
        foreach (var (rel, fw) in configFiles)
        {
            var place = assets.ResolveForPlacement(rel);
            if (place.Sources.Count == 0) { configUnreadable.Add(rel); continue; }
            var (bytes, err) = AssetResolver.ReadPlacementSource(place.Sources[0]);
            if (bytes is null) { configUnreadable.Add($"{rel}: {err}"); continue; }
            var text = Encoding.UTF8.GetString(bytes);
            var prov = place.Sources[0].LooseFilePath is { } lp2 ? FolderOf(lp2) : place.Sources[0].OwningMod;
            if (fw == "OAR") refs.AddRange(OarFormRefs.Extract(rel, prov, text));
            else foreach (var r in SkseConfigReferenceExtractor.Extract(rel, text))
                    if (r.Shape == SkseRefShape.FormToken && r.LocalId is { } id)
                        refs.Add(new ConfigFormRef(rel, prov, r.Line, fw, r.Plugin, id, r.Raw));
        }
        var dummyLook = DummyTargetLook.Evaluate(dummies, masterUses, refs, provider);
        log.WriteLine($"   configs: {configFiles.Count} files, {refs.Count} form refs ({t.Elapsed.TotalSeconds:0.0}s)");

        // ---- the document ----
        var looks = new[] { patches, pointers.Look, contests, scripts, dummyLook };
        object Shape(LookResult r, string established) => new { established, rows = r.Rows, setAside = r.SetAsides };
        var byPlugin = new SortedDictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var byFolder = new SortedDictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in looks.SelectMany(l => l.Rows))
        {
            foreach (var p in r.Plugins) { if (!byPlugin.TryGetValue(p, out var l)) byPlugin[p] = l = new(); if (!l.Contains(r.Id)) l.Add(r.Id); }
            foreach (var m in r.Mods) { if (!byFolder.TryGetValue(m, out var l)) byFolder[m] = l = new(); if (!l.Contains(r.Id)) l.Add(r.Id); }
        }
        var text2 = new StringBuilder();
        foreach (var (title, r) in new[] { ("Patches on disk and not enabled (questions)", patches), ("A mod's runtime file fails on this order", pointers.Look),
                                           ("Two mods' swaps on one base", contests), ("A script's dependency absent", scripts), ("A plugin or config names an empty dummy", dummyLook) })
        {
            text2.AppendLine($"== {title}: {r.Rows.Count} row(s)");
            foreach (var row in r.Rows) text2.AppendLine($"  [{row.Id}] {row.Kind}: {row.Says}\n      -> {row.Consequence}\n      ({row.Established})");
            foreach (var a in r.SetAsides) text2.AppendLine($"  set aside {a.Count}: {a.Reason}");
        }
        return new Dictionary<string, object?>
        {
            ["tool"] = "lookprobe", ["schema"] = 1,
            ["shapes"] = new Dictionary<string, object>
            {
                [InactivePatchLook.Shape] = Shape(patches, "source: an unloaded plugin is not in the game's load order"),
                [SwapPointerLook.Shape] = Shape(pointers.Look, "per row"),
                [SwapContestLook.Shape] = Shape(contests, "per row"),
                [ScriptDependencyLook.Shape] = Shape(scripts, "per row; 4c unasserted"),
                [DummyTargetLook.Shape] = Shape(dummyLook, "per row; per framework"),
            },
            ["index"] = new { byPlugin, byFolder },
            ["swapLines"] = pointers.Table,
            ["conflictBlock"] = layer.ConflictLogLines().ToList(),
            ["inputs"] = new
            {
                loadedPlugins = loadedPaths.Count, dummies = dummies.OrderBy(x => x).ToList(), inactivePluginsRead = disk.Count, bosInis = inis.Select(i => $"{i.Name} <- {i.Provider}").ToList(),
                pexPaths = pexRel.Count, classes = supplied.Count, attachedScriptNames = attached.Count, configFiles = configFiles.Count, configFormRefs = refs.Count,
                unreadable = new { plugins = unreadablePlugins, pex = pexUnreadable.Take(50).ToList(), pexCount = pexUnreadable.Count, configs = configUnreadable.Take(50).ToList(), configCount = configUnreadable.Count },
                notFollowed = new[] { "script fragments (dialogue, quest stage, scene, perk) and scripts started by a DLL or an MCM", "scripts reached only through SPID or SkyPatcher distribution (a distributed spell or perk carries its own VMAD, which is read; a script attached by a distributor alone is not)", "filters on BOS sections naming absent forms", "property overrides from two mods on one id (counted, not assessed)" },
            },
            ["text"] = text2.ToString(),
        };
    }
}
