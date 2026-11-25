using System.Text;

namespace RunControl;

/// <summary>Reuse of an earlier manual run's preparation (census, prepare, scope). A later run in the same Work
/// Folder takes it over only when everything it was built from is the same: the instance, profile, game and
/// selection; every file of the profile's folder; every file of each mod folder either arm loads, of the overwrite
/// folder and of the game's Data folder; the plugin files of the disabled, unticked mods the scope also reads; and
/// the programs that built it. The key is written into every manual run folder.</summary>
public static class PrepReuse
{
    public const string KeyFile = "preparation-key.json";
    public static readonly string[] Stages = ["census", "prepare", "scope"];
    static readonly string[] TextSuffixes = [".json", ".txt", ".log", ".jsonl"];

    // sha256 of a folder's whole tree by relative path, size and last write (and, with hashPlugins, the content of the
    // plugin files at its top, which a tool can replace keeping size and time); null when the folder is absent.
    static string? Listing(string folder, bool hashPlugins = false)
    {
        if (!Directory.Exists(folder)) return null;
        var rows = new List<object?>();
        void Walk(string dir, string rel)
        {
            foreach (string f in PyOrder.SortedByCodePoint(Directory.GetFiles(dir).Select(PyPath.Name)))
            {
                string p = Path.Combine(dir, f), name = PyText.Lower(rel + f);
                try
                {
                    var row = new List<object?> { name, new FileInfo(p).Length, Hashing.MtimeNs(p) };
                    if (hashPlugins && rel.Length == 0 && PluginFiles.Suffixes.Any(s => PyText.Lower(f).EndsWith(s, StringComparison.Ordinal)))
                        row.Add(Hashing.Sha256File(p));
                    rows.Add(row);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { rows.Add(new List<object?> { name, null, null }); }
            }
            foreach (string d in PyOrder.SortedByCodePoint(Directory.GetDirectories(dir).Select(PyPath.Name)))
            {
                string sub = Path.Combine(dir, d);
                if ((File.GetAttributes(sub) & FileAttributes.ReparsePoint) == 0) Walk(sub, rel + d + "/");   // a link is listed nowhere and not entered
            }
        }
        Walk(folder, "");
        return Hashing.Sha256Text(PyJson.Dumps(rows, compact: true));
    }

    // A view entry that names a mod folder by its own path, not by a name under mods/.
    static bool Absolute(string mod) => Path.IsPathFullyQualified(mod) || mod.StartsWith('\\') || mod.StartsWith('/');

    static string Slashed(string path) => PyText.Lower(path.Replace('\\', '/'));

    /// <summary>The preparation key: five parts compared separately, so a refusal can name the part that differs.</summary>
    public static PyDict Key(string instance, string profile, string game, IEnumerable<string> selection, IEnumerable<PyDict> arms,
                             PyDict tools, IEnumerable<string>? alsoScanned = null)
    {
        string pdir = PyPath.Join(instance, "profiles", profile);
        var profileFiles = new PyDict();
        if (Directory.Exists(pdir))
            foreach (string f in PyOrder.SortedPaths(Directory.GetFiles(pdir))) profileFiles[PyText.Lower(PyPath.Name(f))] = Hashing.Sha256File(f);
        var mods = PyOrder.SortedByLowerKey(PyOrder.SortedByCodePoint(arms.SelectMany(a => Py.Strs(a.Get("mods"))).Distinct()));
        var folders = new PyDict();
        foreach (string m in mods)
            folders["mod:" + PyText.Lower(m)] = Listing(Absolute(m) ? PyPath.Str(m) : PyPath.Join(instance, "mods", m), hashPlugins: true);
        folders["overwrite"] = Listing(PyPath.Join(instance, "overwrite"), hashPlugins: true);
        folders["data"] = Listing(PyPath.Join(game, "Data"));
        var scanned = new PyDict();
        foreach (string p in PyOrder.SortedByLowerKey(alsoScanned ?? []))
            scanned[Slashed(p)] = File.Exists(p) ? Hashing.Sha256File(p) : null;
        return new PyDict
        {
            ["schema"] = 1L,
            ["identity"] = new PyDict
            {
                ["instance"] = Slashed(instance), ["profile"] = PyText.Lower(profile), ["game"] = Slashed(game),
                ["selection"] = Py.Objs(PyOrder.SortedByCodePoint(selection.Select(PyText.Lower))),
            },
            ["profileFiles"] = profileFiles, ["folders"] = folders, ["alsoScanned"] = scanned, ["tools"] = tools,
        };
    }

    /// <summary>The programs the preparation runs: this controller and the scopeprobe build, each by its package
    /// digest, and houseCARL's rulebook the probe's contributor walk reads (the build's, <see cref="Checkout.Corpus"/>).</summary>
    public static PyDict ToolsDigest(string? scopeprobeFolder = null, string? corpus = null)
    {
        corpus ??= Checkout.Corpus;
        return new PyDict
        {
            ["runcontrol-package"] = ToolIdentity.Controller()["digest"],
            ["scopeprobe-package"] = ToolIdentity.Package(scopeprobeFolder ?? Path.GetDirectoryName(Checkout.ScopeprobeExe)!)["digest"],
            ["corpus.json"] = File.Exists(corpus) ? Hashing.Sha256File(corpus) : null,
        };
    }

    static readonly (string part, string sentence)[] Parts =
    [
        ("identity", "a different instance, profile, game or selection"),
        ("profileFiles", "a profile file changed (modlist.txt, plugins.txt, loadorder.txt or another file in the profile folder)"),
        ("folders", "a file in a mod folder the run loads, the overwrite folder or the game's Data folder changed"),
        ("alsoScanned", "a plugin of a disabled mod the scope stage also reads changed"),
        ("tools", "the pipeline, the scopeprobe build or the field schema changed"),
    ];

    static object? ReadJson(string path) => PyJson.Loads(PyJson.Decode(File.ReadAllBytes(path), bom: false));

    static bool Unreadable(Exception ex) => ex is IOException or UnauthorizedAccessException or FormatException or ArgumentException or InvalidCastException;

    /// <summary>The newest earlier run folder under the Work Folder whose key equals this run's and whose census,
    /// prepare and scope were accepted and resolve the same arms; else null and the sentence why.</summary>
    public static (string? run, string why) Find(string workFolder, string thisOut, PyDict mine, IEnumerable<PyDict> arms)
    {
        var candidates = new List<string>();
        if (Directory.Exists(workFolder))
            foreach (string d in Directory.EnumerateFileSystemEntries(workFolder))
            {
                string run = PyPath.Join(d, "run");
                if (PyOrder.SamePath(PyPath.Resolve(run), PyPath.Resolve(thisOut)) || !File.Exists(PyPath.Join(run, KeyFile))) continue;
                candidates.Add(run);
            }
        if (candidates.Count == 0) return (null, "no earlier run in the Work Folder recorded its preparation");
        candidates = candidates.OrderByDescending(r => File.GetLastWriteTimeUtc(PyPath.Join(r, KeyFile))).ToList();
        var mineArms = Py.Copy(arms.Cast<object?>().ToList());
        (string run, string? part)? nearest = null;
        foreach (string run in candidates)
        {
            PyDict theirs;
            try { theirs = Py.D(ReadJson(PyPath.Join(run, KeyFile))); }
            catch (Exception ex) when (Unreadable(ex)) { continue; }
            string? part = Parts.Select(p => p.part).FirstOrDefault(p => !Py.Eq(mine.Get(p), theirs.Get(p)));
            if (part != null)
            {
                if (nearest == null || (part != "identity" && nearest.Value.part == "identity")) nearest = (run, part);
                continue;
            }
            PyDict stages;
            object? theirArms;
            try
            {
                stages = Py.DictOrEmpty(Py.D(ReadJson(PyPath.Join(run, "stages.json"))).Get("stages"));
                theirArms = ReadJson(PyPath.Join(run, "arms.json"));
            }
            catch (Exception ex) when (Unreadable(ex))
            {
                nearest ??= (run, null);
                continue;
            }
            if (!Stages.All(s => Py.Truthy(Py.DictOrEmpty(stages.Get(s)).Get("accepted"))) || !Py.Eq(mineArms, theirArms))
            {
                nearest ??= (run, null);
                continue;
            }
            return (run, "");
        }
        if (nearest is not { } near) return (null, "no earlier run in the Work Folder has a readable preparation");
        string name = PyPath.Name(Path.GetDirectoryName(near.run)!);
        if (near.part == null) return (null, $"{name} has the same inputs, but its preparation did not finish or resolved other load orders");
        if (near.part == "identity") return (null, "no earlier run in the Work Folder used this profile and selection");
        return (null, $"{name} used this profile and selection, but {Parts.First(p => p.part == near.part).sentence}");
    }

    // The earlier run's path and this run's, in the four spellings a text file may hold: backslashes, forward
    // slashes, and each as JSON writes it.
    static List<(string old, string now)> Pairs(string old, string now)
    {
        var pairs = new List<(string, string)>();
        foreach (var (o, n) in new[] { (old, now), (old.Replace('\\', '/'), now.Replace('\\', '/')) })
        {
            pairs.Add((o.Replace("\\", "\\\\"), n.Replace("\\", "\\\\")));
            pairs.Add((o, n));
        }
        return pairs.Where(p => p.Item1 != p.Item2).ToList();
    }

    static string Swap(string text, List<(string old, string now)> pairs)
    {
        foreach (var (o, n) in pairs) text = text.Replace(o, n, StringComparison.Ordinal);
        return text;
    }

    static void CopyTree(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (string f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)));
        foreach (string d in Directory.GetDirectories(from)) CopyTree(d, Path.Combine(to, Path.GetFileName(d)));
    }

    /// <summary>Copies the earlier run's census, prepare and scope folders into this run folder, the earlier folder's
    /// path rewritten to this one's in every text file, and returns each stage's earlier receipt rewritten the same
    /// way (the driver records it again under this run's own inputs).</summary>
    public static PyDict Seed(string oldRun, string newRun)
    {
        oldRun = PyPath.Resolve(oldRun);
        newRun = PyPath.Resolve(newRun);
        var pairs = Pairs(oldRun, newRun);
        var records = Py.D(Py.D(ReadJson(PyPath.Join(oldRun, "stages.json")))["stages"]);
        var seeded = new PyDict();
        var strict = new UTF8Encoding(false, throwOnInvalidBytes: true);
        foreach (string s in Stages)
        {
            string dst = PyPath.Join(newRun, s);
            if (Directory.Exists(dst)) Directory.Delete(dst, recursive: true);
            CopyTree(PyPath.Join(oldRun, s), dst);
            foreach (string p in PyPath.RGlob(dst))
            {
                if (!File.Exists(p) || !TextSuffixes.Contains(PyText.Lower(PyPath.Suffix(p)))) continue;
                string text;
                try { text = strict.GetString(File.ReadAllBytes(p)); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException) { continue; }
                // Python reads and writes these in text mode: every line end is read as "\n" and written as the platform's
                text = text.Replace("\r\n", "\n").Replace('\r', '\n');
                string changed = Swap(text, pairs);
                if (changed != text) File.WriteAllBytes(p, strict.GetBytes(changed.Replace("\n", Environment.NewLine)));
            }
            var rec = Py.D(PyJson.Loads(Swap(PyJson.Dumps(records[s]), pairs)));
            foreach (string path in Py.DictOrEmpty(rec.Get("outputs")).Keys)
                if (PyPath.RelativeTo(PyPath.Resolve(path), newRun) == null)
                    throw new Refusal($"stage {s} of {oldRun} names an output outside its run folder: {path}; run without --reuse-preparation-from.");
            seeded[s] = rec;
        }
        return seeded;
    }
}
