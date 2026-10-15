using System.Security.Cryptography;
using System.Text.Json;

namespace HarnessShared;

// The discovery probes' machine-readable answer (patchprobe, poolprobe, fomodprobe `--json PATH`), schema 1.
//
// One shape for all three, so evalrun reads every discovered patch the same way: each HIT names a plugin a
// modder could install, where it was found, and the sha256 of the exact bytes that would be installed (the file on
// disk, or the archive entry). A hit whose bytes were not read carries `sha256: null` and says why, so a
// qualification by content hash can never be faked by a name. BCL-only on purpose: fomodprobe does not reference
// housecarl-core.
public sealed class DiscoveryHits
{
    readonly object _lock = new();
    readonly string _probe, _mode;
    readonly List<Dictionary<string, object?>> _hits = new();
    readonly List<Dictionary<string, object?>> _searched = new(), _notSearched = new(), _unjudgeable = new();
    readonly Dictionary<string, object?> _detail = new();
    bool _complete = true;

    public DiscoveryHits(string probe, string mode) { _probe = probe; _mode = mode; }

    public void Searched(string kind, string path, string? name = null)
    {
        lock (_lock) _searched.Add(new() { ["kind"] = kind, ["path"] = path, ["name"] = name });
    }

    /// <summary>A source the probe was meant to read and could not. Makes the output incomplete.</summary>
    public void NotSearched(string kind, string path, string why)
    {
        lock (_lock) { _notSearched.Add(new() { ["kind"] = kind, ["path"] = path, ["why"] = why }); _complete = false; }
    }

    public void Unjudgeable(string what, string why)
    {
        lock (_lock) _unjudgeable.Add(new() { ["what"] = what, ["why"] = why });
    }

    public void Detail(string key, object? value) { lock (_lock) _detail[key] = value; }

    /// <summary>One hit. Either <paramref name="hash"/> is given or <paramref name="unhashedReason"/> says why not;
    /// a read that failed for an I/O reason also passes <paramref name="ioFailure"/> true, which makes the output
    /// incomplete rather than quietly unhashed.</summary>
    public void Hit(string file, string kind, Dictionary<string, object?> where, Hashed? hash, string hashSource,
                    IEnumerable<string>? masters, Dictionary<string, object?>? detail, string? unhashedReason = null,
                    bool ioFailure = false)
    {
        var h = new Dictionary<string, object?>
        {
            ["file"] = file, ["kind"] = kind, ["where"] = where,
            ["sha256"] = hash?.Sha256,
            ["bytes"] = hash?.Length,
            ["hashSource"] = hash is null ? null : hashSource,
            ["masters"] = masters?.ToList() ?? new List<string>(),
            ["detail"] = detail ?? new Dictionary<string, object?>(),
        };
        if (hash is null) h["unhashedReason"] = unhashedReason ?? "no reason recorded";
        lock (_lock)
        {
            _hits.Add(h);
            if (ioFailure) _complete = false;
        }
    }

    /// <summary>The sha256 and length of a file on disk, streamed, or null with the reason.</summary>
    public static Hashed? HashFile(string path, out string? why)
    {
        why = null;
        try
        {
            using var fs = File.OpenRead(path);
            var sha = Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
            return new Hashed(sha, fs.Length);
        }
        catch (Exception ex) { why = $"could not read {path}: {ex.GetType().Name}: {ex.Message.Split('\n')[0]}"; return null; }
    }

    public static Hashed HashBytes(byte[] bytes) =>
        new(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), bytes.LongLength);

    public void Write(string path, int judged, int unjudgeable)
    {
        Dictionary<string, object?> doc;
        lock (_lock)
        {
            doc = new()
            {
                ["schemaVersion"] = 1, ["probe"] = _probe, ["mode"] = _mode, ["complete"] = _complete,
                ["generatedUtc"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                ["sourcesSearched"] = _searched, ["sourcesNotSearched"] = _notSearched,
                ["counts"] = new Dictionary<string, object?> { ["judged"] = judged, ["unjudgeable"] = unjudgeable, ["hits"] = _hits.Count },
                ["hits"] = _hits, ["unjudgeableItems"] = _unjudgeable, ["detail"] = _detail,
            };
        }
        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var tmp = full + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, full, true);
        Console.WriteLine($"hit list: {_hits.Count} hit(s) written to {full}");
    }
}

public sealed record Hashed(string Sha256, long Length);
