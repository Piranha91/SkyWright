using System.Diagnostics;
using HousecarlCore.Progress;
using Mutagen.Bethesda.Skyrim;

namespace HarnessShared;

/// <summary>The sweep patchprobe's --verify and the houseCARL integration module's patchfind share (moved verbatim from
/// patchprobe's Program.cs, sections 1-3, in step 3 of the SkyWright / houseCARL boundary): every MO2 instance under the
/// search roots with its active plugins, every plugin of every instance indexed by each master and by each master pair it
/// reconciles, and the download pool's archives by name. It prints what patchprobe printed; a caller that records
/// where it searched (patchfind's hit list) passes <c>searched</c> and <c>notSearched</c>.</summary>
public sealed class PatchIndex
{
    public required List<Instance> Instances { get; init; }
    public required Instance Target { get; init; }
    public required Dictionary<(string, string), List<Found>> ByPair { get; init; }
    public required Dictionary<string, List<Found>> ByMaster { get; init; }
    public required Dictionary<string, List<Found>> SeenPlugin { get; init; }
    public required int Scanned { get; init; }
    public required int Unreadable { get; init; }
    public required List<(string File, string Name, string ModName)> PoolNames { get; init; }

    // "Masters both" carries information only when both sides are things a plugin could choose not to
    // master. Everything masters Skyrim.esm, so a pair with a base-game master on one side narrows
    // nothing, and a match found through it rests ENTIRELY on the record overlap. Marked, not hidden:
    // the record test is still exact, but the pair test behind it is doing no work.
    // USSEP is not base game but behaves the same way here — nearly everything masters it — so it is
    // listed for the same reason, not because it is vanilla.
    public static readonly HashSet<string> Ubiquitous = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Skyrim.esm", "Update.esm", "Dawnguard.esm", "HearthFires.esm", "Dragonborn.esm",
        "SkyrimVR.esm", "Unofficial Skyrim Special Edition Patch.esp",
    };

    // GENERATED plugins are never an answer to "where can I get a patch". DynDOLOD, ParallaxGen,
    // Synthesis and Reqtificator write these from whatever load order they were run against, so the
    // copy sitting in another list describes THAT list's mods and paths, not the target's. Worse, they
    // are regenerated: a fix imported from one goes stale on the next run. They do genuinely override
    // the records — the first run had DynDOLOD.esp and PG_1.esp filling candidate lists — so they are
    // excluded explicitly and COUNTED, not silently dropped.
    public static readonly HashSet<string> Generated = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "DynDOLOD.esm", "DynDOLOD.esp", "Occlusion.esp", "PGPatcher.esp", "PG_1.esp",
        "Synthesis.esp", "Requiem for the Indifferent.esp", "TexGen_Output.esp",
    };

    // Compare as normalised full paths: the configured target may carry forward slashes or a trailing separator
    // (harness.local.json is hand-written), the discovered directory never does.
    public static string Norm(string d) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(d));

    /// <summary>The sweep, or null when the target instance is not among the instances found (said on stderr).</summary>
    public static PatchIndex? Build(List<string> roots, string target, string downloadPool, Run run, Stopwatch sw,
        Action<string, string, string>? searched = null, Action<string, string, string>? notSearched = null)
    {
        // ---------------------------------------------------------------------------------------
        // 1. Find every MO2 instance
        // ---------------------------------------------------------------------------------------
        var instances = new List<Instance>();
        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) { notSearched?.Invoke("root", root, "the search root does not exist"); continue; }
            // A recursive enumeration THROWS partway through on the first unreadable directory (a drive
            // root has System Volume Information), losing every result found so far. Walk it manually so
            // one denied folder costs that folder and nothing else.
            var inis = new List<string>();
            var stack = new Stack<(string Dir, int Depth)>();
            stack.Push((root, 0));
            while (stack.Count > 0)
            {
                var (dir0, depth) = stack.Pop();
                try
                {
                    foreach (var f in Directory.EnumerateFiles(dir0, "ModOrganizer.ini")) inis.Add(f);
                    if (depth < 3)
                        foreach (var sub in Directory.EnumerateDirectories(dir0))
                        {
                            var leaf = Path.GetFileName(sub);
                            if (leaf.StartsWith('$') || leaf.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase)) continue;
                            stack.Push((sub, depth + 1));
                        }
                }
                catch { /* unreadable directory: skip it, keep everything else */ }
            }

            foreach (var ini in inis)
            {
                var dir = Path.GetDirectoryName(ini)!;
                if (dir.Contains("$RECYCLE", StringComparison.OrdinalIgnoreCase)) continue;
                var mods = Path.Combine(dir, "mods");
                if (!Directory.Exists(mods)) continue;

                string? profile = null;
                foreach (var line in File.ReadLines(ini))
                    if (line.StartsWith("selected_profile", StringComparison.OrdinalIgnoreCase))
                    {
                        // MO2 writes this as @ByteArray(Name); the name is what is between the parentheses.
                        int o = line.IndexOf('('), c = line.LastIndexOf(')');
                        profile = o >= 0 && c > o ? line[(o + 1)..c] : line.Split('=', 2).Last().Trim();
                    }

                // Exempt from the missing-master refusal (ruling 12): a patch whose master is inactive is this probe's finding, not an order it measures.
                var active = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (profile is not null)
                {
                    // plugins.txt does NOT list the base-game masters at all — MO2 treats Skyrim.esm,
                    // Update.esm, the DLC and SkyrimVR.esm as implicitly always on. Reading only plugins.txt
                    // therefore reports Skyrim.esm as INACTIVE, which made the applicability filter reject
                    // 134 patches for "missing" Skyrim.esm.
                    //
                    // Derived, not hardcoded to a game: loadorder.txt lists EVERY plugin in order, so
                    // anything in loadorder.txt that plugins.txt never mentions is exactly an implicit
                    // always-on master. Here that is the 6 that make 484 -> 490, which is the plugin count
                    // Mo2LoadOrder.Build independently arrives at.
                    var pdir = Path.Combine(dir, "profiles", profile);
                    var plugins = Path.Combine(pdir, "plugins.txt");
                    var listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    if (File.Exists(plugins))
                        foreach (var line in File.ReadLines(plugins))
                        {
                            var t = line.Trim();
                            if (t.Length == 0 || t.StartsWith('#')) continue;
                            var name = t.StartsWith('*') ? t[1..].Trim() : t;
                            listed.Add(name);
                            if (t.StartsWith('*')) active.Add(name);
                        }

                    var loadorder = Path.Combine(pdir, "loadorder.txt");
                    if (File.Exists(loadorder))
                        foreach (var line in File.ReadLines(loadorder))
                        {
                            var t = line.Trim();
                            if (t.Length == 0 || t.StartsWith('#')) continue;
                            if (!listed.Contains(t)) active.Add(t);
                        }
                }
                instances.Add(new Instance(Path.GetFileName(dir), dir, mods, profile ?? "?", active));
            }
        }

        var targetInst = instances.FirstOrDefault(i => string.Equals(Norm(i.Dir), Norm(target), StringComparison.OrdinalIgnoreCase));
        Console.WriteLine($"MO2 instances found: {instances.Count}");
        foreach (var inst in instances) searched?.Invoke("instance", inst.Dir, inst.Name);
        foreach (var inst in instances)
            Console.WriteLine($"   {(inst == targetInst ? "TARGET " : "       ")}{inst.Name,-28} profile '{inst.Profile}'  {inst.Active.Count,5} active plugins");
        if (targetInst is null) { Console.Error.WriteLine("target instance not found."); return null; }

        // ---------------------------------------------------------------------------------------
        // 2. Index every plugin in every instance by the master pairs it reconciles
        // ---------------------------------------------------------------------------------------
        // A plugin with N masters asserts up to N*(N-1)/2 pairwise reconciliations. Capped, because a
        // merged patch or a big overhaul can master dozens of plugins and would otherwise dominate the
        // index with pairs it does not meaningfully reconcile.
        const int MaxMasters = 12;
        var byPair = new Dictionary<(string, string), List<Found>>();
        var byMaster = new Dictionary<string, List<Found>>(StringComparer.OrdinalIgnoreCase);
        var seenPlugin = new Dictionary<string, List<Found>>(StringComparer.OrdinalIgnoreCase);
        int scanned = 0, unreadable = 0;
        run.Feature("instances", instances.Count, "instances").Stage("index", "indexing every instance's plugins", instances.Count, "instances");


        foreach (var inst in instances)
        {
            run.Advance();
            foreach (var modDir in Directory.EnumerateDirectories(inst.Mods))
            {
                foreach (var pl in Directory.EnumerateFiles(modDir).Where(IsPlugin))
                {
                    scanned++;
                    string[] masters;
                    try
                    {
                        using var m = SkyrimMod.CreateFromBinaryOverlay(pl, SkyrimRelease.SkyrimSE);
                        masters = m.ModHeader.MasterReferences.Select(x => x.Master.FileName.String).ToArray();
                    }
                    catch { unreadable++; continue; }
                    if (masters.Length < 2) continue;

                    string file = Path.GetFileName(pl);
                    var state = inst == targetInst
                        ? (inst.Active.Contains(file) ? Where.Active : Where.Installed)
                        : (inst.Active.Contains(file) ? Where.OtherListActive : Where.OtherListPresent);
                    var f = new Found(file, inst.Name, Path.GetFileName(modDir), state, pl, masters);

                    if (!seenPlugin.TryGetValue(file, out var lst)) seenPlugin[file] = lst = new List<Found>();
                    lst.Add(f);

                    // Indexed by EACH master as well as by pair. The pair index is capped at 12 masters so a
                    // merged megapatch cannot flood it, but --verify must still be able to find that megapatch:
                    // there the question is not "does this look like a patch for A and B" but "does this file
                    // override the record that is broken", and a big patch is a perfectly good answer to it.
                    foreach (var m in masters)
                    {
                        var mk = m.ToLowerInvariant();
                        if (!byMaster.TryGetValue(mk, out var mb)) byMaster[mk] = mb = new List<Found>();
                        mb.Add(f);
                    }

                    if (masters.Length > MaxMasters) continue;
                    for (int a = 0; a < masters.Length; a++)
                        for (int b = a + 1; b < masters.Length; b++)
                        {
                            var key = Key(masters[a], masters[b]);
                            if (!byPair.TryGetValue(key, out var bag)) byPair[key] = bag = new List<Found>();
                            bag.Add(f);
                        }
                }
            }
            Console.WriteLine($"   scanned {inst.Name,-28} {scanned:N0} plugins so far  ({sw.Elapsed.TotalSeconds:0}s)");
        }
        Console.WriteLine($"\nindexed {scanned:N0} plugins ({unreadable:N0} unreadable), {byPair.Count:N0} distinct master pairs  ({sw.Elapsed.TotalSeconds:0.0}s)");

        // ---------------------------------------------------------------------------------------
        // 3. The shared download pool, by name only
        // ---------------------------------------------------------------------------------------
        // No archive is opened. MO2 writes a .meta beside every download naming the mod it came from, so
        // the pool can be searched for CANDIDATES cheaply. This is a weaker signal than a master pair and
        // is labelled as such — it says "an archive with a promising name exists", never "a patch exists".
        var poolNames = new List<(string File, string Name, string ModName)>();
        if (Directory.Exists(downloadPool))
        {
            foreach (var meta in Directory.EnumerateFiles(downloadPool, "*.meta"))
            {
                string? name = null, modName = null;
                try
                {
                    foreach (var line in File.ReadLines(meta))
                    {
                        if (line.StartsWith("name =", StringComparison.OrdinalIgnoreCase) || line.StartsWith("name=", StringComparison.OrdinalIgnoreCase))
                            name ??= line.Split('=', 2)[1].Trim();
                        else if (line.StartsWith("modName", StringComparison.OrdinalIgnoreCase))
                            modName ??= line.Split('=', 2)[1].Trim();
                        if (name is not null && modName is not null) break;
                    }
                }
                catch { continue; }
                poolNames.Add((Path.GetFileNameWithoutExtension(meta), name ?? "", modName ?? ""));
            }
        }
        Console.WriteLine($"download pool: {poolNames.Count:N0} archives with metadata at {downloadPool}");
        if (Directory.Exists(downloadPool)) searched?.Invoke("download-pool", downloadPool, "archive names from .meta files; no archive opened");
        else notSearched?.Invoke("download-pool", downloadPool, "the download pool directory does not exist");

        return new PatchIndex
        {
            Instances = instances, Target = targetInst, ByPair = byPair, ByMaster = byMaster, SeenPlugin = seenPlugin,
            Scanned = scanned, Unreadable = unreadable, PoolNames = poolNames,
        };
    }

    public static bool IsPlugin(string p)
    {
        var e = Path.GetExtension(p);
        return e.Equals(".esp", StringComparison.OrdinalIgnoreCase)
            || e.Equals(".esm", StringComparison.OrdinalIgnoreCase)
            || e.Equals(".esl", StringComparison.OrdinalIgnoreCase);
    }
    public static (string, string) Key(string a, string b)
        => string.Compare(a, b, StringComparison.OrdinalIgnoreCase) <= 0 ? (a.ToLowerInvariant(), b.ToLowerInvariant())
                                                                         : (b.ToLowerInvariant(), a.ToLowerInvariant());
}

public enum Where { Active = 0, Installed = 1, OtherListActive = 2, OtherListPresent = 3 }
/// <param name="FullPath">Kept so --verify can reopen the candidate and read its RECORDS, not just its header.</param>
/// <param name="Masters">The full declared master set — what --applicable is decided from.</param>
public sealed record Found(string Plugin, string Instance, string Mod, Where State, string FullPath, string[] Masters);
public sealed record Instance(string Name, string Dir, string Mods, string Profile, HashSet<string> Active);
