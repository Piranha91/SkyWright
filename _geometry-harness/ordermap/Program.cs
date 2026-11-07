// ORDERMAP - one index over a whole load order: which plugins and mod folders meet, and how.
//
// usage: ordermap --instance DIR --profile NAME --game DIR [--order FILE] --out FILE [--no-hash]
//        ordermap --self-test   (--selftest is the same)
//
// Plugins: shared records, interior and exterior cells, ring-1 neighbouring cells, placed bases, form links; WRLD, persistent CELL and
// NAVI records are containers and make no edge (the selector's rule). Base-game plugins make no edge. Masters are recorded.
// Asset paths: every data path under a folder, loose or in that folder's plugins' archives. Where two folders provide one path the bytes
// are compared, so an edge says identical or different, and who wins by the game's rule: loose over archive, then MO2 priority for loose,
// load order for archives. Runtime files per folder are counted so they can be named as outside this map. Every cell any plugin carries
// is recorded by FormKey with its EditorID, worldspace and grid (an exterior cell often has no EditorID), so a place can be asked by
// either name. Each plugin's size is recorded, so an empty 79-byte placeholder can be told from the map. Read-only.
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using HarnessShared;
using HousecarlCore.Progress;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;

if (args.Length > 0 && (args[0] == "--self-test" || args[0] == "--selftest")) return OrderMapSelfTest.Run();
using var __run = RunLog.BeginProcess("ordermap", args, new RunOptions { StageSource = StageSource.Counted });
string? instance = null, profile = null, game = null, order = null, outFile = null;
bool hash = true;
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--instance") instance = args[++i];
    else if (args[i] == "--profile") profile = args[++i];
    else if (args[i] == "--game") game = args[++i];
    else if (args[i] == "--order") order = args[++i];
    else if (args[i] == "--out") outFile = args[++i];
    else if (args[i] == "--no-hash") hash = false;
    else { Console.Error.WriteLine($"unknown option '{args[i]}'"); return 2; }
}
if (instance is null || profile is null || game is null || outFile is null) { Console.Error.WriteLine("--instance, --profile, --game and --out are required."); return 2; }
return OrderMap.Build(instance, profile, game, order, outFile, hash, Console.Out);

public static class OrderMap
{
    static readonly HashSet<string> DataTops = new(StringComparer.OrdinalIgnoreCase)
    { "meshes", "textures", "scripts", "sound", "music", "seq", "interface", "grass", "lodsettings", "strings", "shadersfx", "dialogueviews", "video", "calientetools" };

    sealed class Edge
    {
        public int Records, Interior, Exterior, Neighbours, BaseLinks, FormLinks;
        public readonly List<string> RecordExamples = new(), CellExamples = new();
    }
    sealed record Provider(int Folder, string? Archive, int ArchiveRank);

    public static int Build(string instance, string profile, string game, string? order, string outFile, bool hash, TextWriter log)
    {
        var wall = Stopwatch.StartNew();
        var run = RunLog.Current;
        run?.Stage("resolve", "resolving the order", 1, "orders");
        string profileDir = Path.Combine(instance, "profiles", profile), modsDir = Path.Combine(instance, "mods");
        string overwrite = Path.Combine(instance, "overwrite"), dataDir = Path.Combine(game, "Data");
        var cfg = HarnessOrderCli.Resolve(profile, profileDir, modsDir, dataDir, overwrite, game, order, null, null);
        if (cfg.Refused) { Console.Error.WriteLine($"REFUSED: {cfg.RefusedReason}"); return 2; }
        double tResolve = wall.Elapsed.TotalSeconds;
        run?.Advance();
        run?.Feature("plugins", cfg.OrderedPaths.Count, "plugins");
        run?.Stage("footprints", "reading every plugin's records and cells", cfg.OrderedPaths.Count, "plugins");
        int read = 0;

        var fps = new Footprint[cfg.OrderedPaths.Count];
        Parallel.For(0, fps.Length, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i => { fps[i] = Footprint.Read(cfg.OrderedPaths[i], dataDir, links: true, info: false, cells: true); run?.Progress(Interlocked.Increment(ref read)); });
        double tFootprints = wall.Elapsed.TotalSeconds;
        var isBase = fps.Select(f => BaseGamePlugins.Names.Contains(f.Name)).ToArray();

        var edges = new Dictionary<(int, int), Edge>();
        Edge E(int a, int b) { var k = a < b ? (a, b) : (b, a); if (!edges.TryGetValue(k, out var e)) edges[k] = e = new Edge(); return e; }

        var byKey = new Dictionary<FormKey, List<int>>();
        var byInterior = new Dictionary<string, List<int>>();
        var byExterior = new Dictionary<(FormKey, int, int), List<int>>();
        for (int i = 0; i < fps.Length; i++)
        {
            if (isBase[i]) continue;
            foreach (var k in fps[i].Keys) if (!fps[i].Containers.Contains(k)) { if (!byKey.TryGetValue(k, out var l)) byKey[k] = l = new(); l.Add(i); }
            foreach (var c in fps[i].Interior) { if (!byInterior.TryGetValue(c, out var l)) byInterior[c] = l = new(); l.Add(i); }
            foreach (var c in fps[i].Exterior) { if (!byExterior.TryGetValue(c, out var l)) byExterior[c] = l = new(); l.Add(i); }
        }
        // a cell record is a record too: an interior both edit is counted under cells, and stays a record edge
        foreach (var (k, l) in byKey)
            for (int a = 0; a < l.Count; a++) for (int b = a + 1; b < l.Count; b++)
                { var e = E(l[a], l[b]); e.Records++; if (e.RecordExamples.Count < 5) e.RecordExamples.Add(k.ToString()); }
        foreach (var (c, l) in byInterior)
            for (int a = 0; a < l.Count; a++) for (int b = a + 1; b < l.Count; b++)
                { var e = E(l[a], l[b]); e.Interior++; if (e.CellExamples.Count < 5) e.CellExamples.Add(c); }
        foreach (var (c, l) in byExterior)
            for (int a = 0; a < l.Count; a++) for (int b = a + 1; b < l.Count; b++)
                { var e = E(l[a], l[b]); e.Exterior++; if (e.CellExamples.Count < 5) e.CellExamples.Add(Footprint.ExtKey(c.Item1, c.Item2, c.Item3)); }
        for (int i = 0; i < fps.Length; i++)
        {
            if (isBase[i]) continue;
            foreach (var (ws, x, y) in fps[i].Exterior)
                for (int dx = -1; dx <= 1; dx++) for (int dy = -1; dy <= 1; dy++)
                {
                    if (dx == 0 && dy == 0) continue;
                    if (!byExterior.TryGetValue((ws, x + dx, y + dy), out var l)) continue;
                    foreach (var j in l) if (j > i) E(i, j).Neighbours++;   // each ordered sighting from the lower plugin: a count of cell pairs seen that way
                        else if (j < i && !fps[j].Exterior.Contains((ws, x, y))) E(i, j).Neighbours++;
                }
            foreach (var b in fps[i].PlacedBases) if (byKey.TryGetValue(b, out var l)) foreach (var j in l) if (j != i) E(i, j).BaseLinks++;
            foreach (var b in fps[i].Links ?? new()) if (byKey.TryGetValue(b, out var l)) foreach (var j in l) if (j != i) E(i, j).FormLinks++;
        }
        double tEdges = wall.Elapsed.TotalSeconds;

        // every cell by FormKey: the latest plugin's EditorID wins, an earlier one stands where a later override names none
        var cells = new SortedDictionary<string, (string? Edid, string? Worldspace, int? X, int? Y)>(StringComparer.Ordinal);
        foreach (var f in fps)
            foreach (var (k, v) in f.Cells ?? new())
                cells[k] = cells.TryGetValue(k, out var old) ? (v.Edid ?? old.Edid, v.Worldspace ?? old.Worldspace, v.X ?? old.X, v.Y ?? old.Y) : v;
        run?.Stage("assets", "listing each folder's asset paths", 1, "pass");

        // ---- folders, paths, archives ------------------------------------------------------------------------------
        var folders = cfg.EnabledMods.ToList();                                         // MO2 priority, top = highest
        var folderOf = new string?[fps.Length];
        var mods = Path.GetFullPath(modsDir).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        for (int i = 0; i < fps.Length; i++)
        {
            var full = Path.GetFullPath(fps[i].Path);
            if (full.StartsWith(mods, StringComparison.OrdinalIgnoreCase)) folderOf[i] = full[mods.Length..].Split(Path.DirectorySeparatorChar)[0];
        }
        var loadRank = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < fps.Length; i++) loadRank[Path.GetFileNameWithoutExtension(fps[i].Name)] = i;
        var providers = new ConcurrentDictionary<string, ConcurrentBag<Provider>>(StringComparer.Ordinal);
        var unreadable = new ConcurrentBag<string>();
        var runtime = new ConcurrentDictionary<string, Dictionary<string, int>>();
        Parallel.For(0, folders.Count, new ParallelOptions { MaxDegreeOfParallelism = 8 }, f =>
        {
            var name = folders[f];
            var dir = Path.Combine(modsDir, name);
            var counts = new Dictionary<string, int>();
            void Count(string k) => counts[k] = counts.GetValueOrDefault(k) + 1;
            void Note(string p)
            {
                var fn = Path.GetFileName(p);
                if (fn.EndsWith("_distr.ini")) Count("SPID");
                else if (fn.EndsWith("_kid.ini")) Count("KID");
                else if (fn.EndsWith("_swap.ini")) Count("BOS");
                else if (p.StartsWith("skse\\plugins\\skypatcher") || p.Contains("\\skypatcher\\")) Count("SkyPatcher");
                else if (p.StartsWith("skse\\plugins\\") && fn.EndsWith(".dll")) Count("SKSE dll");
                else if (p.StartsWith("scripts\\") && fn.EndsWith(".pex")) Count("Papyrus script");
                else if (p.Contains("openanimationreplacer") || p.Contains("dynamicanimationreplacer")) Count("OAR or DAR animation");
                else if (p.StartsWith("meshes\\") && fn.EndsWith(".hkx")) Count("animation file");
                else if (p.StartsWith("nemesis_engine\\") || p.StartsWith("pandora_engine\\")) Count("behaviour patch");
            }
            foreach (var p in AssetPaths.Loose(dir))
            {
                Note(p);
                if (DataTops.Contains(p.Split('\\')[0])) providers.GetOrAdd(p, _ => new()).Add(new Provider(f, null, 0));
            }
            if (Directory.Exists(dir))
                foreach (var fn in Directory.EnumerateFiles(dir, "*.ini")) Note(Path.GetFileName(fn).ToLowerInvariant());
            var plugins = Enumerable.Range(0, fps.Length).Where(i => string.Equals(folderOf[i], name, StringComparison.OrdinalIgnoreCase)).Select(i => fps[i].Name).ToList();
            foreach (var a in AssetPaths.ArchivesFor(dir, plugins))
            {
                try
                {
                    var stem = Path.GetFileNameWithoutExtension(a);
                    var rank = plugins.Where(pn => stem.StartsWith(Path.GetFileNameWithoutExtension(pn), StringComparison.OrdinalIgnoreCase)).Select(pn => loadRank[Path.GetFileNameWithoutExtension(pn)]).DefaultIfEmpty(0).Max();
                    foreach (var p in AssetPaths.Archive(a))
                    {
                        Note(p);
                        if (DataTops.Contains(p.Split('\\')[0])) providers.GetOrAdd(p, _ => new()).Add(new Provider(f, a, rank));
                    }
                }
                catch (Exception ex) { unreadable.Add($"{a}: {Footprint.Concise(ex)}"); }
            }
            if (counts.Count > 0) runtime[name] = counts;
        });
        double tAssets = wall.Elapsed.TotalSeconds;
        run?.Advance();
        run?.Stage("hash", "comparing the bytes of shared paths", 1, "pass");

        var shared = providers.Where(kv => kv.Value.Select(x => x.Folder).Distinct().Count() > 1).ToDictionary(kv => kv.Key, kv => kv.Value.ToList(), StringComparer.Ordinal);
        // bytes: one hash per (path, provider); archives read one at a time, loose files side by side
        var hashes = new ConcurrentDictionary<(string, int, string?), string>();
        long hashedBytes = 0;
        if (hash)
        {
            Parallel.ForEach(shared.SelectMany(kv => kv.Value.Where(p => p.Archive is null).Select(p => (kv.Key, p))), new ParallelOptions { MaxDegreeOfParallelism = 8 }, x =>
            {
                try
                {
                    var file = Path.Combine(modsDir, folders[x.p.Folder], x.Key);
                    using var s = File.OpenRead(file);
                    Interlocked.Add(ref hashedBytes, s.Length);
                    hashes[(x.Key, x.p.Folder, null)] = Convert.ToHexString(SHA256.HashData(s));
                }
                catch (Exception ex) { unreadable.Add($"{x.Key} in {folders[x.p.Folder]}: {Footprint.Concise(ex)}"); }
            });
            var wanted = shared.SelectMany(kv => kv.Value.Where(p => p.Archive is not null).Select(p => (Path: kv.Key, p))).GroupBy(x => x.p.Archive!).ToList();
            Parallel.ForEach(wanted, new ParallelOptions { MaxDegreeOfParallelism = 4 }, g =>
            {
                try
                {
                    var reader = Mutagen.Bethesda.Archives.Archive.CreateReader(GameRelease.SkyrimSE, g.Key);
                    var want = g.ToDictionary(x => x.Path, x => x.p, StringComparer.Ordinal);
                    foreach (var f in reader.Files)
                    {
                        var n = AssetPaths.Norm(f.Path);
                        if (!want.TryGetValue(n, out var p)) continue;
                        var body = f.GetBytes();
                        Interlocked.Add(ref hashedBytes, body.Length);
                        hashes[(n, p.Folder, p.Archive)] = Convert.ToHexString(SHA256.HashData(body));
                    }
                }
                catch (Exception ex) { unreadable.Add($"{g.Key}: {Footprint.Concise(ex)}"); }
            });
        }
        double tHash = wall.Elapsed.TotalSeconds;
        run?.Advance();

        // winner by the game's rule; a pair edge for each pair of folders on a path
        var assetEdges = new Dictionary<(int, int), AssetEdge>();
        foreach (var (path, ps) in shared)
        {
            var w = ps.Where(p => p.Archive is null).OrderBy(p => p.Folder).Concat(ps.Where(p => p.Archive is not null).OrderByDescending(p => p.ArchiveRank)).First();
            var byFolder = ps.GroupBy(p => p.Folder).Select(g => g.OrderBy(p => p.Archive is null ? 0 : 1).First()).ToList();
            for (int a = 0; a < byFolder.Count; a++) for (int b = a + 1; b < byFolder.Count; b++)
            {
                var (x, y) = (byFolder[a], byFolder[b]);
                var k = x.Folder < y.Folder ? (x.Folder, y.Folder) : (y.Folder, x.Folder);
                if (!assetEdges.TryGetValue(k, out var e)) assetEdges[k] = e = new AssetEdge();
                var hx = hashes.GetValueOrDefault((path, x.Folder, x.Archive));
                var hy = hashes.GetValueOrDefault((path, y.Folder, y.Archive));
                var top = path.Split('\\')[0];
                if (hx is null || hy is null) e.Unknown++;
                else if (hx == hy) e.Identical++;
                else
                {
                    e.Different++;
                    e.DifferentByTop[top] = e.DifferentByTop.GetValueOrDefault(top) + 1;
                    if (w.Folder == k.Item1) e.WinsA++; else if (w.Folder == k.Item2) e.WinsB++;
                    if (e.Examples.Count < 6) e.Examples.Add($"{path} (wins: {folders[w.Folder]})");
                }
            }
        }
        var doc = new
        {
            tool = "ordermap", schema = 2, source = cfg.Source,
            plugins = fps.Select((f, i) => new { name = f.Name, folder = folderOf[i], bytes = File.Exists(f.Path) ? new FileInfo(f.Path).Length : -1, baseGame = isBase[i], error = f.Error, masters = f.Masters, newRecords = f.NewRecords, overrides = f.Overrides,
                interior = f.Interior.Count, exterior = f.Exterior.Count, scriptCarriers = f.ScriptCarriers, exteriorCells = isBase[i] ? new List<string>() : f.Exterior.Select(c => $"{c.Ws}|{c.X}|{c.Y}").ToList() }),
            edges = edges.Select(kv => new { a = fps[kv.Key.Item1].Name, b = fps[kv.Key.Item2].Name, records = kv.Value.Records, interior = kv.Value.Interior, exterior = kv.Value.Exterior,
                neighbours = kv.Value.Neighbours, baseLinks = kv.Value.BaseLinks, formLinks = kv.Value.FormLinks, recordExamples = kv.Value.RecordExamples, cellExamples = kv.Value.CellExamples }),
            folders,
            assetEdges = assetEdges.Select(kv => new { a = folders[kv.Key.Item1], b = folders[kv.Key.Item2], identical = kv.Value.Identical, different = kv.Value.Different, unknown = kv.Value.Unknown,
                winsA = kv.Value.WinsA, winsB = kv.Value.WinsB, differentByTop = kv.Value.DifferentByTop, examples = kv.Value.Examples }),
            runtimeLayers = runtime,
            cells = cells.ToDictionary(kv => kv.Key, kv => (object)new { edid = kv.Value.Edid, worldspace = kv.Value.Worldspace, grid = kv.Value.X is int x && kv.Value.Y is int y ? new[] { x, y } : null }),
            archivesUnreadable = unreadable.ToList(),
            archives = providers.Values.SelectMany(v => v).Where(p => p.Archive is not null).Select(p => p.Archive).Distinct().Count(),
            assetPaths = providers.Count, assetPathsShared = shared.Count, hashed = hash, hashedBytes,
            seconds = new { resolve = tResolve, footprints = tFootprints - tResolve, edges = tEdges - tFootprints, assets = tAssets - tEdges, hash = tHash - tAssets, total = wall.Elapsed.TotalSeconds },
            peakWorkingSet = Process.GetCurrentProcess().PeakWorkingSet64,
        };
        var dir2 = Path.GetDirectoryName(Path.GetFullPath(outFile));
        if (!string.IsNullOrEmpty(dir2)) Directory.CreateDirectory(dir2);
        using (var o = File.Create(outFile)) JsonSerializer.Serialize(o, doc);
        log.WriteLine($"ordermap: {fps.Length} plugins, {edges.Count} plugin pairs, {providers.Count} asset paths ({shared.Count} shared), {assetEdges.Count} folder pairs, hashed {hashedBytes / (1 << 20)} MiB, {unreadable.Count} unreadable, {wall.Elapsed.TotalSeconds:0.0}s");
        return 0;
    }

    sealed class AssetEdge
    {
        public int Identical, Different, Unknown, WinsA, WinsB;
        public readonly Dictionary<string, int> DifferentByTop = new();
        public readonly List<string> Examples = new();
    }
}
