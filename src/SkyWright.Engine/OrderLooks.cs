using System.Text.Json;

using HousecarlCore;

namespace SkyWright.Engine;

// ======================================================================
//  OrderLooks — the shared shape of the whole-order "looks": cheap, read-only file reads that report a
//  patch on disk and not enabled, a runtime file pointing at a plugin or record the order does not supply,
//  two mods' swaps on one base, a script whose parent or called class is absent, and a plugin or config
//  naming an empty dummy plugin. A look reports and never writes. Every row names at least two parties
//  (the file's mod and what it points at), carries its evidence, and says how its consequence is
//  established; whatever a look sets aside is counted with the reason, never dropped.
// ======================================================================

/// <summary>What a row is to the person: a question (a patch is off — on purpose?), a finding (the files say
/// something fails and the consequence is established), or a look (worth reading; the consequence is not).</summary>
public enum LookKind { Question, Finding, Look }

/// <summary>One person-facing row. <paramref name="Established"/> names how the consequence is known: "source: …",
/// "game-log: …", "lab: …" or "unasserted: …", and <paramref name="Consequence"/> is worded to that label.</summary>
public sealed record LookRow(string Shape, string Class, LookKind Kind, IReadOnlyList<string> Mods, IReadOnlyList<string> Plugins,
                             IReadOnlyList<string> Files, string Says, string Consequence, string Established)
{
    public string Id { get; init; } = "";
}

/// <summary>A set-aside reason with its count and a few examples — machine-facing only.</summary>
public sealed record LookSetAside(string Reason, int Count, IReadOnlyList<string> Examples);

/// <summary>One look's result: its rows and its set-asides.</summary>
public sealed class LookResult
{
    public string Shape { get; }
    public List<LookRow> Rows { get; } = new();
    readonly Dictionary<string, (int Count, List<string> Examples)> _aside = new(StringComparer.Ordinal);
    public LookResult(string shape) => Shape = shape;

    public void SetAside(string reason, string example)
    {
        if (!_aside.TryGetValue(reason, out var a)) a = (0, new List<string>());
        if (a.Examples.Count < 5) a.Examples.Add(example);
        _aside[reason] = (a.Count + 1, a.Examples);
    }

    public int AsideCount(string reason) => _aside.TryGetValue(reason, out var a) ? a.Count : 0;

    public IReadOnlyList<LookSetAside> SetAsides =>
        _aside.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => new LookSetAside(k.Key, k.Value.Count, k.Value.Examples)).ToList();

    /// <summary>Give every row a stable id ("shape:1" …) in the order the rows were sorted.</summary>
    public void Number()
    {
        for (int i = 0; i < Rows.Count; i++) Rows[i] = Rows[i] with { Id = $"{Shape}:{i + 1}" };
    }
}

/// <summary>The empty-dummy test by structure: a plugin file that holds its TES4 header record and nothing after
/// it (no group, no record), whatever its masters make its size.</summary>
public static class EmptyPlugin
{
    /// <summary>True when the file is exactly one TES4 record (24-byte header + its data size); false when anything
    /// follows; null when the file cannot be read or does not begin with a TES4 record.</summary>
    public static bool? IsEmpty(string path)
    {
        try
        {
            using var f = File.OpenRead(path);
            Span<byte> head = stackalloc byte[24];
            if (f.Read(head) < 24) return null;
            if (head[0] != (byte)'T' || head[1] != (byte)'E' || head[2] != (byte)'S' || head[3] != (byte)'4') return null;
            uint size = BitConverter.ToUInt32(head[4..8]);
            return f.Length == 24L + size;
        }
        catch { return null; }
    }
}

/// <summary>One form a runtime config names: the file, its provider mod, the line, the framework that reads it, the
/// plugin and the local id as written.</summary>
public sealed record ConfigFormRef(string File, string? Provider, int Line, string Framework, string Plugin, uint LocalId, string Token);

/// <summary>Open Animation Replacer's form shape: a JSON object carrying "pluginName" and "formID" (hex without a
/// prefix), anywhere in a config.json or user.json. Read with the JSON reader, so a pair split across lines is found;
/// the line is the one "pluginName" sits on.</summary>
public static class OarFormRefs
{
    public static IReadOnlyList<ConfigFormRef> Extract(string relPath, string? provider, string text)
    {
        var refs = new List<ConfigFormRef>();
        var cursor = new Dictionary<string, int>(StringComparer.Ordinal);   // each repeat of a name is the next one in the text
        JsonDocument doc;
        try { doc = JsonDocument.Parse(text, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }); }
        catch (JsonException) { return refs; }
        using (doc) Walk(doc.RootElement);
        return refs;

        void Walk(JsonElement e)
        {
            if (e.ValueKind == JsonValueKind.Object)
            {
                string? plugin = null, form = null;
                foreach (var p in e.EnumerateObject())
                {
                    if (p.Name == "pluginName" && p.Value.ValueKind == JsonValueKind.String) plugin = p.Value.GetString();
                    else if (p.Name == "formID" && p.Value.ValueKind == JsonValueKind.String) form = p.Value.GetString();
                    else Walk(p.Value);
                }
                if (!string.IsNullOrWhiteSpace(plugin) && form is not null
                    && uint.TryParse(form.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? form[2..] : form,
                                     System.Globalization.NumberStyles.HexNumber, null, out var id))
                    refs.Add(new ConfigFormRef(relPath, provider, LineOf(plugin!), "OAR", plugin!, id & 0x00FFFFFF, $"{plugin}|{form}"));
            }
            else if (e.ValueKind == JsonValueKind.Array)
                foreach (var x in e.EnumerateArray()) Walk(x);
        }

        int LineOf(string plugin)
        {
            int from = cursor.GetValueOrDefault(plugin);
            int at = text.IndexOf("\"" + plugin + "\"", from, StringComparison.Ordinal);
            if (at < 0) return 0;
            cursor[plugin] = at + plugin.Length + 2;
            int n = 1;
            for (int i = 0; i < at; i++) if (text[i] == '\n') n++;
            return n;
        }
    }
}
