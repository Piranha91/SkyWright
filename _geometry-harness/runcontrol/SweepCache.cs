namespace RunControl;

/// <summary>The sweep's cache: request key to the emit it produced, with the configuration identity's own hashes for
/// re-checking. An entry names its emit relative to the cache file's folder and records the emit's sha256. Earlier
/// launches' caches are read and never written.</summary>
public sealed class SweepCache
{
    public const long Version = 2;

    public string FilePath { get; }
    public string Log { get; }
    public PyDict Entries { get; }
    readonly List<(string file, PyDict entries)> _prior = [];

    public SweepCache(string path, string log, IEnumerable<string>? prior = null)
    {
        FilePath = path;
        Log = log;
        object? own = SweepFiles.Exists(path) ? PyJson.LoadEvalFile(path) : null;
        Entries = PyValue.Truthy(own) ? PyValue.Dict(own) : new PyDict();
        foreach (string given in prior ?? [])
        {
            string p = PyPath.Str(given);
            if ((SweepFiles.Exists(p) ? PyJson.LoadEvalFile(p) : null) is not PyDict doc)
            {
                Note($"PRIOR {p}: no readable cache file there (that launch recorded nothing); nothing reused from it");
                continue;
            }
            _prior.Add((p, doc));
        }
    }

    // Written beside the file and moved over it, so a stop mid-save leaves the last whole cache.
    public void Save()
    {
        string tmp = PyPath.Join(SweepFiles.Parent(FilePath), PyPath.Name(FilePath) + ".tmp");
        SweepFiles.WriteText(tmp, PyJson.Dumps(Entries, indent: 1));
        File.Move(tmp, FilePath, overwrite: true);
    }

    /// <summary>One cache decision, printed and appended to the cache log.</summary>
    public void Note(string line)
    {
        SweepFiles.Print($"   cache: {line}");
        SweepFiles.AppendText(Log, line + "\n");
    }

    /// <summary>(the emit an entry names, null) when the entry can be checked at all, else (null, why not).</summary>
    public static (string? emit, string? why) EntryEmit(PyDict e, string cacheFile)
    {
        if (!PyValue.Eq(e.Get("cacheVersion"), Version) || !PyValue.Truthy(e.Get("emitSha256")))
            return (null, "the entry predates folder-relative, hashed entries, so the emit it names cannot be verified — never reused");
        string rel = PyValue.Truthy(e.Get("emit")) ? PyText.Str(e.Get("emit")) : "";
        var parts = rel.Split('/').Where(x => x.Length > 0 && x != ".").ToList();
        string back = rel.Replace('/', '\\');
        bool drive = (back.Length >= 2 && back[1] == ':') || back.StartsWith(@"\\", StringComparison.Ordinal);
        if (rel.Length == 0 || rel.StartsWith('/') || drive || parts.Contains(".."))
            return (null, $"emit path {PyText.Repr(rel)} is not inside the cache's own folder");
        string folder = PyPath.Resolve(SweepFiles.Parent(cacheFile));
        string emit = PyPath.Join(folder, parts.ToArray());
        if (PyPath.RelativeTo(PyPath.Resolve(emit), folder) == null)
            return (null, $"emit path {PyText.Repr(rel)} resolves outside the cache's own folder");
        return (emit, null);
    }

    /// <summary>(emit, the cache file that served it) for an entry that checks out, else null. This launch's own
    /// entries come first, then each earlier cache in the order given.</summary>
    public (string emit, string cacheFile)? Lookup(string key, string label)
    {
        bool found = false;
        foreach (var (cacheFile, entries) in new[] { (FilePath, Entries) }.Concat(_prior))
        {
            if (entries.Get(key) is not { } raw) continue;
            var e = PyValue.Dict(raw);
            found = true;
            string where = SweepFiles.SamePath(cacheFile, FilePath) ? "" : $" (earlier launch {PyPath.Name(SweepFiles.Parent(cacheFile))})";
            var (emit, why) = EntryEmit(e, cacheFile);
            if (emit == null) { Note($"INVALIDATED {label}: {why}{where}"); continue; }
            if (!File.Exists(emit)) { Note($"INVALIDATED {label}: emit {PyText.Str(e["emit"])} is gone{where}"); continue; }
            if (Hashing.Sha256File(emit) != PyText.Lower(PyText.Str(e["emitSha256"])))
            {
                Note($"INVALIDATED {label}: emit {PyText.Str(e["emit"])} no longer hashes to the value recorded with it{where}");
                continue;
            }
            if (!PyValue.Truthy(e.Get("identity")))
            {
                Note($"INVALIDATED {label}: the cached emit carries no configuration identity — missing identity prevents reuse{where}");
                continue;
            }
            var identity = PyValue.Dict(e["identity"]);
            var plugins = Items(identity, "plugins");
            var overlays = Items(identity, "overlays");
            string? changed = null;
            foreach (var (kind, items) in new[] { ("plugin", plugins), ("overlay", overlays) })
            {
                foreach (var it in items)
                {
                    string p = PyPath.Str((string)it["path"]!);
                    if (!SweepFiles.Exists(p)) changed = $"{kind} {p} no longer exists";
                    else if (Hashing.Sha256File(p) != PyText.Lower((string)it["sha256"]!)) changed = $"{kind} {PyPath.Name(p)} content changed since the cached run";
                    if (changed != null) break;
                }
                if (changed != null) break;
            }
            if (changed != null) { Note($"INVALIDATED {label}: {changed}{where}"); continue; }
            Note($"REUSED {label}: request key {SweepFiles.First(key, 12)} matched; {plugins.Count} plugin and {overlays.Count} overlay hashes and the emit's "
                 + $"own hash re-verified on disk; emit {PyText.Str(e["emit"])}{where}");
            return (emit, cacheFile);
        }
        if (!found) Note($"MISS {label}: no entry for request key {SweepFiles.First(key, 12)}");
        return null;
    }

    // identity.get(name, []) as a list of rows.
    static List<PyDict> Items(PyDict identity, string name) =>
        identity.ContainsKey(name) ? PyValue.Iter(identity[name]).Select(x => (PyDict)x!).ToList() : [];

    /// <summary>Record an emit this launch wrote under its request key, and save.</summary>
    public void Record(string key, string label, string emit)
    {
        var ident = OrderSweep.ReadIdentity(emit);
        string? rel = PyPath.RelativeTo(PyPath.Resolve(emit), PyPath.Resolve(SweepFiles.Parent(FilePath)));
        if (rel == null)
        {
            Note($"NOT-RECORDED {label}: emit {emit} is not inside this cache's folder, so an entry could not name it relative to the cache");
            return;
        }
        Entries[key] = new PyDict
        {
            ["cacheVersion"] = Version, ["emit"] = rel, ["emitSha256"] = Hashing.Sha256File(emit), ["label"] = label,
            ["recordedUtc"] = PyText.UtcNowZ(), ["identity"] = ident,
        };
        if (ident == null) Note($"RECORDED {label}: emit carries NO configuration identity — this entry can never be reused");
        else Note($"RECORDED {label}: fingerprint {SweepFiles.First(PyText.Str(ident["fingerprint"]), 12)}, {PyValue.Len(ident["plugins"])} plugins hashed");
        Save();
    }
}

/// <summary>Paths and text files as the Python sweep handles them.</summary>
public static class SweepFiles
{
    /// <summary>Path(p).exists().</summary>
    public static bool Exists(string p) => File.Exists(p) || Directory.Exists(p);

    /// <summary>Path(p).parent, as text.</summary>
    public static string Parent(string p)
    {
        string s = PyPath.Str(p);
        int i = s.LastIndexOf('\\');
        if (i < 0) return ".";
        string head = s[..i];
        if (i == s.Length - 1) return s;
        return head.Length == 0 || head.EndsWith(':') ? head + "\\" : head;
    }

    /// <summary>Path(p).stem: the name without its last suffix.</summary>
    public static string Stem(string p)
    {
        string name = PyPath.Name(p);
        return name[..(name.Length - PyPath.Suffix(p).Length)];
    }

    /// <summary>Path(a) == Path(b) on Windows: the same text, case ignored.</summary>
    public static bool SamePath(string a, string b) => PyText.Lower(PyPath.Str(a)) == PyText.Lower(PyPath.Str(b));

    /// <summary>text[:n], counted in characters as Python counts them.</summary>
    public static string First(string s, int n)
    {
        int i = 0;
        for (int k = 0; k < n && i < s.Length; k++) i += char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]) ? 2 : 1;
        return s[..i];
    }

    /// <summary>(mtime_ns, size) of a file or folder; null when nothing is there.</summary>
    public static (long mtime, long size)? Signature(string p)
    {
        try
        {
            if (File.Exists(p)) return (Hashing.MtimeNs(p), new FileInfo(p).Length);
            if (Directory.Exists(p)) return (Hashing.MtimeNs(p), 0);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return null;
    }

    // Python's text mode: "\n" is written as the platform's line end, UTF-8 without a mark.
    static byte[] Native(string text) => PyJson.Utf8(Environment.NewLine == "\n" ? text : text.Replace("\n", Environment.NewLine));

    /// <summary>Path.write_text(text, encoding="utf-8").</summary>
    public static void WriteText(string path, string text) => File.WriteAllBytes(path, Native(text));

    /// <summary>open(path, "a", encoding="utf-8").write(text).</summary>
    public static void AppendText(string path, string text)
    {
        using var f = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        byte[] bytes = Native(text);
        f.Write(bytes, 0, bytes.Length);
    }

    /// <summary>Path.read_text(encoding="utf-8", errors="replace"): every line end read as "\n".</summary>
    public static string ReadTextReplace(string path) =>
        new System.Text.UTF8Encoding(false, false).GetString(File.ReadAllBytes(path)).Replace("\r\n", "\n").Replace('\r', '\n');

    /// <summary>print(text).</summary>
    public static void Print(string text) => Console.Out.Write((Environment.NewLine == "\n" ? text : text.Replace("\n", Environment.NewLine)) + Environment.NewLine);
}
