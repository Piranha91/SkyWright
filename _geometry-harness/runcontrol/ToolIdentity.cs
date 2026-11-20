using System.Security.Cryptography;

namespace RunControl;

/// <summary>A file that exists and could not be read: the code it holds cannot be identified.</summary>
public sealed class Unreadable(string path, Exception error) : Exception($"{path}: {error.Message}")
{
    public string FilePath { get; } = path;
    public Exception Error { get; } = error;
}

/// <summary>The one identity rule for the programs an evaluation runs, compared by digest, never by path.</summary>
public static class ToolIdentity
{
    static string FileSha256(string path)
    {
        try { return Hashing.Sha256File(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new Unreadable(path, ex); }
    }

    // prefix + sha256 over the lines' UTF-8 bytes, in order.
    static string Digest(string prefix, IEnumerable<string> lines)
    {
        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (string ln in lines) h.AppendData(PyJson.Utf8(ln));
        return prefix + Convert.ToHexString(h.GetHashAndReset()).ToLowerInvariant();
    }

    /// <summary>A Release folder's identity: every top-level file except *.pdb, plus every file under runtimes/,
    /// each by lower-cased relative path and sha256. state "absent" when the folder does not exist.</summary>
    public static PyDict Package(string folder)
    {
        string locator = PyPath.Str(folder);
        if (!Directory.Exists(folder))
            return new PyDict { ["kind"] = "package", ["locator"] = locator, ["state"] = "absent", ["digest"] = null, ["files"] = new List<object?>() };
        var built = Directory.GetFiles(folder).ToList();
        built.AddRange(PyPath.RGlob(PyPath.Join(folder, "runtimes")).Where(File.Exists));
        var rows = PyOrder.SortedBy(
            built.Where(q => PyText.Lower(PyPath.Suffix(q)) != ".pdb")
                 .Select(q => (path: PyText.Lower(PyPath.RelativeTo(q, folder)!), file: q)),
            r => r.path);
        var files = rows.Select(r => new PyDict { ["path"] = r.path, ["sha256"] = FileSha256(r.file) }).ToList();
        return new PyDict
        {
            ["kind"] = "package", ["locator"] = locator, ["state"] = "present",
            ["digest"] = Digest("folder:", files.Select(f => $"{f["path"]}\0{f["sha256"]}\n")),
            ["files"] = files.Cast<object?>().ToList(),
        };
    }

    static bool PathLike(string tok) => tok.Contains('/') || tok.Contains('\\');

    /// <summary>A --probe-cmd override's identity: the command text and the bytes of every file its tokens name.
    /// A bare first token is found through PATH and recorded as a locator only.</summary>
    public static PyDict ProbeCmd(string text)
    {
        var toks = PyText.Split(text);
        var inputs = new List<object?>();
        string? interpreter = null;
        for (int i = 0; i < toks.Count; i++)
        {
            string tok = toks[i];
            if (i == 0 && !PathLike(tok))
            {
                string? found = Which(tok);
                interpreter = found == null ? null : PyPath.Resolve(found);
                continue;
            }
            if (File.Exists(tok))
                inputs.Add(new PyDict { ["token"] = (long)i, ["path"] = PyPath.Resolve(tok), ["sha256"] = FileSha256(tok) });
            else if (PathLike(tok))
                inputs.Add(new PyDict { ["token"] = (long)i, ["path"] = PyPath.Str(tok), ["sha256"] = null });
        }
        var rec = new PyDict { ["kind"] = "probe-cmd", ["command"] = text, ["interpreter"] = interpreter, ["inputs"] = inputs };
        if (inputs.Count == 0) { rec["state"] = "unverifiable"; rec["digest"] = null; return rec; }
        var lines = new List<string> { text + "\n" };
        lines.AddRange(inputs.Cast<PyDict>().Select(r => $"{r["token"]}\0{r["sha256"] ?? "absent"}\n"));
        rec["state"] = "present";
        rec["digest"] = Digest("probe-cmd:", lines);
        return rec;
    }

    // shutil.which on Windows: the current folder, then PATH, trying the name and each PATHEXT ending.
    static string? Which(string cmd)
    {
        string dirname = Path.GetDirectoryName(cmd) ?? "";
        var path = new List<string>();
        if (dirname.Length > 0) { path.Add(dirname); cmd = Path.GetFileName(cmd); }
        else
        {
            string? env = Environment.GetEnvironmentVariable("PATH");
            if (string.IsNullOrEmpty(env)) return null;
            path.AddRange(env.Split(';'));
            if (Environment.GetEnvironmentVariable("NoDefaultCurrentDirectoryInExePath") == null) path.Insert(0, ".");
        }
        string extSource = Environment.GetEnvironmentVariable("PATHEXT") is { Length: > 0 } e ? e : ".COM;.EXE;.BAT;.CMD;.VBS;.JS;.WS;.MSC";
        var exts = extSource.Split(';').Where(x => x.Length > 0).ToList();
        var files = new List<string> { cmd };
        files.AddRange(exts.Select(x => cmd + x));
        string suffix = Path.GetExtension(cmd).ToUpperInvariant();
        if (!exts.Any(x => x.ToUpperInvariant() == suffix)) { files.RemoveAt(0); files.Add(cmd); }
        var seen = new HashSet<string>();
        foreach (string dir in path)
        {
            if (!seen.Add(PyPath.NormCase(dir))) continue;
            foreach (string f in files)
            {
                string name = dir.Length == 0 ? f : dir.TrimEnd('\\', '/') + "\\" + f;
                if (File.Exists(name)) return name;
            }
        }
        return null;
    }

    /// <summary>The controller's identity: the package digest of the folder this program runs from.</summary>
    public static PyDict Controller() => Package(AppContext.BaseDirectory);

    /// <summary>The sha256 of the controller's own assembly, recorded on stage receipts, not bound.</summary>
    public static string DriverSha256() => Hashing.Sha256File(Path.Combine(AppContext.BaseDirectory, "runcontrol.dll"));

    /// <summary>What differs between two identities of one program, file by file, in plain words.</summary>
    public static List<string> Differences(PyDict old, PyDict now)
    {
        static Dictionary<string, object?> Rows(PyDict rec)
        {
            string? kind = rec.Get("kind") as string;
            var (list, name) = kind == "scripts" ? ("files", "name") : kind == "probe-cmd" ? ("inputs", "path") : ("files", "path");
            var rows = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var r in (rec.Get(list) as List<object?> ?? []).Cast<PyDict>()) rows[(string)r[name]!] = r["sha256"];
            return rows;
        }
        var a = Rows(old);
        var b = Rows(now);
        var result = new List<string>();
        foreach (string k in PyOrder.SortedByCodePoint(a.Keys.Union(b.Keys)))
        {
            if (!b.ContainsKey(k) || (b[k] == null && a.GetValueOrDefault(k) != null)) result.Add($"{k} removed");
            else if (!a.ContainsKey(k) || (a[k] == null && b[k] != null)) result.Add($"{k} added");
            else if (!Equals(a[k], b[k])) result.Add($"{k} changed {First12(PyText.Str(a[k]))} -> {First12(PyText.Str(b[k]))}");
        }
        if (old.Get("kind") as string == "probe-cmd" && !Equals(old.Get("command"), now.Get("command")))
            result.Add($"command text {PyText.Repr(old.Get("command"))} -> {PyText.Repr(now.Get("command"))}");
        return result;
    }

    /// <summary>A digest's first twelve hex characters, after its kind prefix.</summary>
    public static string Short(object? digest)
    {
        string s = digest is null or "" or false or 0L ? "none" : PyText.Str(digest);
        int colon = s.IndexOf(':');
        return First12(colon < 0 ? s : s[(colon + 1)..]);
    }

    // text[:12], counted in characters as Python counts them.
    static string First12(string s)
    {
        int units = 0, n = 0;
        for (int i = 0; i < s.Length && n < 12; n++) { i += char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]) ? 2 : 1; units = i; }
        return s[..units];
    }
}
