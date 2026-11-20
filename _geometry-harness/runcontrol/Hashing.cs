using System.Security.Cryptography;
using HarnessShared;

namespace RunControl;

/// <summary>File and text hashes, the stage ledger's output fingerprints, and bytes on disk counted once per file.</summary>
public static class Hashing
{
    /// <summary>sha256 of a file, lower-case hex; an unreadable file throws (the probes' own owner).</summary>
    public static string Sha256File(string path) => ConfigurationIdentity.Sha256File(path);

    /// <summary>evalrun's sha256_file: null when the file cannot be read.</summary>
    public static string? Sha256FileOrNull(string path)
    {
        try { return Sha256File(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>sha256 of a text's UTF-8 bytes.</summary>
    public static string Sha256Text(string text) => Sha256Bytes(PyJson.Utf8(text));

    public static string Sha256Bytes(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    /// <summary>st_mtime_ns: a file's last write time in nanoseconds since 1970, in the 100 ns steps Windows keeps.</summary>
    public static long MtimeNs(string path) => (File.GetLastWriteTimeUtc(path).Ticks - DateTime.UnixEpoch.Ticks) * 100;

    /// <summary>A stage output's fingerprint: a file's sha256; a folder's "dir:" hash over [relative path, size,
    /// mtime_ns] rows in Python's path order; null when nothing is there.</summary>
    public static string? Fingerprint(string path)
    {
        if (File.Exists(path)) return Sha256File(path);
        if (!Directory.Exists(path)) return null;
        var rows = new List<object?>();
        foreach (string f in PyOrder.SortedPaths(PyPath.RGlob(path)))
            if (File.Exists(f))
                rows.Add(new List<object?> { PyPath.RelativeTo(f, path), new FileInfo(f).Length, MtimeNs(f) });
        return "dir:" + PyJson.Sha256Json(rows);
    }

    /// <summary>A stage tool's digest: a file's sha256; a folder's "dir:" hash over [relative path, sha256] rows;
    /// null when nothing is there.</summary>
    public static string? ToolDigest(string path)
    {
        if (File.Exists(path)) return Sha256File(path);
        if (!Directory.Exists(path)) return null;
        var rows = new List<object?>();
        foreach (string f in PyOrder.SortedPaths(PyPath.RGlob(path)))
            if (File.Exists(f))
                rows.Add(new List<object?> { PyPath.RelativeTo(f, path), Sha256File(f) });
        return "dir:" + PyJson.Sha256Json(rows);
    }

    /// <summary>Bytes under a folder with each file counted once, so a hard-linked file is one file. Without
    /// topFiles the folder's own files are left out.</summary>
    public static long UniqueBytes(string folder, bool topFiles = true)
    {
        var seen = new HashSet<(uint, ulong)>();
        var seenPaths = new HashSet<string>();
        long total = 0;
        var todo = new Stack<string>();
        todo.Push(folder);
        bool top = true;
        while (todo.Count > 0)
        {
            string dir = todo.Pop();
            bool own = top;
            top = false;
            string[] files, dirs;
            try { files = Directory.GetFiles(dir); dirs = Directory.GetDirectories(dir); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (string d in dirs) todo.Push(d);
            if (own && !topFiles) continue;
            foreach (string f in files)
            {
                var id = NativeFile.Identity(f);
                if (id is { } v && v.index != 0)
                {
                    if (seen.Add((v.volume, v.index))) total += v.size;
                }
                else if (File.Exists(f) && seenPaths.Add(f)) total += id?.size ?? new FileInfo(f).Length;
            }
        }
        return total;
    }
}

/// <summary>One hash per (resolved path, size, mtime) per invocation.</summary>
public sealed class HashMemo
{
    readonly Dictionary<(string, long, long), string?> _cache = [];

    /// <summary>The file's sha256, or null when it cannot be read.</summary>
    public string? Sha256(string path)
    {
        (string, long, long) key;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return null;
            key = (PyPath.Resolve(path), info.Length, Hashing.MtimeNs(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        if (!_cache.TryGetValue(key, out var sha)) _cache[key] = sha = Hashing.Sha256FileOrNull(path);
        return sha;
    }
}
