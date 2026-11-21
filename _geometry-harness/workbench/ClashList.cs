using System.Text.Json;
using System.Windows;

namespace Workbench;

/// <summary>One clash of a clash list: the mods it is between, what clashes, and where (a record or a cell, as
/// FormID:plugin). Severity is the writer's own word, shown as given.</summary>
internal sealed record ClashEntry(int Number, IReadOnlyList<string> Mods, string What, string? Record, string? Cell, string? Severity)
{
    /// <summary>The record a click opens: the clash's record, else its cell.</summary>
    internal string? OpensAt => Record ?? Cell;
}

/// <summary>A clash list: clashes written by someone or something other than SkyWright's checks (an AI, a script, a
/// person), in the format docs/clash-list-format.md describes. It is read, checked whole and listed under Conflicts ›
/// In the world; a clash that names a record or cell opens in the view on the loaded load order.</summary>
internal sealed record ClashList(string File, string? Title, string? LoadOrder, IReadOnlyList<ClashEntry> Clashes)
{
    internal const string Format = "skywright-clash-list";
    internal const int Version = 1;
    /// <summary>The file name ending a clash list carries, so it is recognised without being read.</summary>
    internal const string Suffix = ".clashes.json";

    /// <summary>The clash list open in the window, or null.</summary>
    internal static ClashList? Current { get; set; }

    /// <summary>Read and check a clash list; anything wrong is refused in one sentence naming where.</summary>
    internal static ClashList Read(string file)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(System.IO.File.ReadAllText(file), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }); }
        catch (JsonException ex) { throw new InvalidOperationException($"'{file}' is not valid JSON ({ex.Message.TrimEnd('.')}); fix the file and load it again."); }
        catch (IOException ex) { throw new InvalidOperationException($"'{file}' could not be read ({ex.Message.TrimEnd('.')})."); }
        using (doc) return Parse(doc.RootElement, file);
    }

    internal static ClashList Parse(JsonElement root, string file)
    {
        string Where(string at) => $"'{Path.GetFileName(file)}' {at}";
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidOperationException($"{Where("is not a JSON object")}; a clash list starts with {{ \"format\": \"{Format}\", ... }}.");
        if (Str(root, "format") != Format) throw new InvalidOperationException($"{Where($"does not say \"format\": \"{Format}\"")}, so it is not a clash list.");
        if (!root.TryGetProperty("version", out var v) || v.ValueKind != JsonValueKind.Number || v.GetInt32() != Version)
            throw new InvalidOperationException($"{Where($"is not version {Version} of the clash list format")}; this SkyWright reads version {Version}.");
        if (!root.TryGetProperty("clashes", out var list) || list.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException($"{Where("has no \"clashes\" list")}; add one, even if it is empty.");
        var clashes = new List<ClashEntry>();
        int n = 0;
        foreach (var c in list.EnumerateArray())
        {
            n++;
            string at = $"clash {n}";
            if (c.ValueKind != JsonValueKind.Object) throw new InvalidOperationException($"{Where($"{at} is not an object")}.");
            var mods = c.TryGetProperty("mods", out var m) && m.ValueKind == JsonValueKind.Array
                ? m.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString()!.Trim() : "").ToList() : null;
            if (mods is null || mods.Count < 2 || mods.Any(x => x.Length == 0))
                throw new InvalidOperationException($"{Where($"{at} does not name at least two mods in \"mods\"")}; a clash is between two or more mods or plugins.");
            var what = Str(c, "what");
            if (string.IsNullOrWhiteSpace(what)) throw new InvalidOperationException($"{Where($"{at} has no \"what\"")}; say in a sentence what clashes.");
            var record = Key(c, "record", at); var cell = Key(c, "cell", at);
            clashes.Add(new ClashEntry(n, mods, what.Trim(), record, cell, Str(c, "severity")?.Trim()));
        }
        return new ClashList(file, Str(root, "title")?.Trim(), Str(root, "loadOrder")?.Trim(), clashes);

        string? Key(JsonElement c, string name, string at)
        {
            var k = Str(c, name);
            if (k is null) return null;
            if (!Mutagen.Bethesda.Plugins.FormKey.TryFactory(k.Trim(), out var fk))
                throw new InvalidOperationException($"{Where($"{at} gives \"{name}\": \"{k}\"")}, which is not a FormID:plugin such as \"000891:Skyrim.esm\".");
            return fk.ToString();
        }
    }

    static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    /// <summary>A clash as the list shows it, and its tooltip.</summary>
    internal static string Label(ClashEntry c) =>
        $"{c.Number}. {string.Join(" / ", c.Mods)}  ·  {c.What}" + (c.Severity is { Length: > 0 } s ? $"  ({s})" : "");
    internal static string Tip(ClashEntry c) =>
        c.OpensAt is { } k ? $"opens {k} in the view, on the loaded load order" : "names no record or cell, so it does not open in the view";

    /// <summary>A click on a clash: open its record or cell on the load order the World tab holds.</summary>
    internal static string OpenEntry(ClashEntry c) =>
        c.OpensAt is { } key ? WorldView.PickKey(key)
        : throw new InvalidOperationException($"clash {c.Number} names no \"record\" or \"cell\", so there is nothing to show in the view.");
}

/// <summary>A clash list file (*.clashes.json): listed under Conflicts › In the world, and the load order it names loaded.</summary>
internal sealed class ClashListReader : IInputReader
{
    public string Kind => "clash list";
    public string Accepts => $"a clash list ({ClashList.Suffix})";

    public InputMatch? Recognise(string path) =>
        File.Exists(path) && path.EndsWith(ClashList.Suffix, StringComparison.OrdinalIgnoreCase) ? new InputMatch(this, path, "a clash list") : null;

    public string Open(InputMatch match)
    {
        var list = ClashList.Read(match.Path);
        ClashList.Current = list;
        string loaded = "";
        if (list.LoadOrder is { Length: > 0 } lo)
        {
            // the load order the clashes are in, so they open in the view; a refusal is said and the list still shows
            var full = Path.IsPathFullyQualified(lo) ? lo : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(match.Path)!, lo));
            try { InputReaders.Open(full); loaded = $"; its load order '{full}' is being read"; }
            catch (InvalidOperationException ex) { loaded = $"; its load order could not be loaded: {ex.Message.TrimEnd('.')}"; }
        }
        Shell.FillConflicts();
        if (Application.Current is not null && Shell.Tabs is not null) Shell.ShowTab("Conflicts");
        int opens = list.Clashes.Count(c => c.OpensAt is not null);
        return $"opened the clash list '{Path.GetFileName(match.Path)}': {list.Clashes.Count} clash(es), {opens} of them open in the view{loaded}.";
    }
}
