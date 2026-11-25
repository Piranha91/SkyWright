using System.Globalization;
using System.Text.Json;
using HousecarlCore;
using Mutagen.Bethesda.Plugins;
using ViewProbe;

namespace Workbench;

/// <summary>Item 5's server answers from the houseCARL server the workbench binds (its arm's profile): <c>near=</c> for doors,
/// <c>references=</c> for what names a reference, the door's <c>TeleportDestination</c>, a base's record type, and the
/// commit's placement check (F4) for the reference as drawn.</summary>
internal sealed class ServerContextSource : IContextSource
{
    readonly Task<PatchWriter> _writer;
    readonly Func<FormKey, RefPlacementEdit.Pose?> _drawn;

    /// <summary>Takes the writer on the UI thread, where it is started; the reads then run on the pool.</summary>
    internal ServerContextSource(Task<PatchWriter> writer, Func<FormKey, RefPlacementEdit.Pose?> drawn) { _writer = writer; _drawn = drawn; }

    async Task<JsonElement> Records(Dictionary<string, object?> args)
    {
        args["format"] = "json";
        args.TryAdd("max_chars", 1_000_000);
        var text = await (await _writer.ConfigureAwait(false)).CallAsync("housecarl_records", args).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(text);
        if (doc.RootElement.TryGetProperty("truncated", out var cut) && cut.ValueKind == JsonValueKind.True)
            throw new InvalidOperationException("houseCARL cut its answer short, so it is not read; reopen the scene to read it again.");
        return doc.RootElement.Clone();
    }

    static IEnumerable<JsonElement> Rows(JsonElement root, string name) =>
        root.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array ? a.EnumerateArray() : Enumerable.Empty<JsonElement>();

    static string? S(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    async Task<Dictionary<string, string>> Fields(string key, params string[] fields)
    {
        var root = await Records(new() { ["formids"] = new[] { key }, ["project"] = new Dictionary<string, object?> { ["form"] = "fields", ["fields"] = fields, ["depth"] = 3 } });
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var r in Rows(root, "records"))
        {
            if (S(r, "error") is { } err) throw new InvalidOperationException($"houseCARL could not read {key}: {err}");
            foreach (var f in Rows(r, "fields"))
                if (S(f, "path") is { } p && f.TryGetProperty("value", out var v)) d[p] = v.ValueKind == JsonValueKind.String ? v.GetString()! : v.GetRawText();
        }
        return d;
    }

    /// <summary>The most rows one read lists; more are counted from the answer's total, never cut silently.</summary>
    internal const int Cap = 200;

    static int Total(JsonElement root, int listed) => root.TryGetProperty("total", out var t)
        ? t.ValueKind == JsonValueKind.Number ? t.GetInt32() : int.TryParse(t.GetString(), out var n) ? n : listed
        : listed;

    public async Task<IReadOnlyList<string>> DoorsNearAsync(FormKey key, double within) => (await DoorsNearCountedAsync(key, within)).Doors;

    public async Task<(IReadOnlyList<string> Doors, int NotListed)> DoorsNearCountedAsync(FormKey key, double within)
    {
        var root = await Records(new()
        {
            ["near"] = new Dictionary<string, object?> { ["of"] = new[] { key.ToString() }, ["within"] = within },
            ["types"] = new[] { "REFR" }, ["where"] = new[] { "TeleportDestination exists" }, ["limit"] = Cap,
        }).ConfigureAwait(false);
        var doors = Rows(root, "matches").Select(m => S(m, "formid")).OfType<string>().ToList();
        return (doors, Math.Max(0, Total(root, doors.Count) - doors.Count));
    }

    static readonly string[] NamingFields = { "EnableParent", "LinkedReferences", "ActivateParents", "TeleportDestination" };

    public async Task<IReadOnlyList<(string Key, string Field)>> NamesAsync(FormKey key) => (await NamesCountedAsync(key)).Names;

    public async Task<(IReadOnlyList<(string Key, string Field)> Names, int NotListed)> NamesCountedAsync(FormKey key)
    {
        var root = await Records(new() { ["references"] = new[] { key.ToString() }, ["limit"] = Cap }).ConfigureAwait(false);
        var list = new List<(string, string)>();
        foreach (var m in Rows(root, "matches"))
        {
            if (S(m, "formid") is not { } id) continue;
            string type = S(m, "type") ?? "record", ed = S(m, "editorid") ?? "";
            if (type is "PlacedObject" or "PlacedNpc")
            {
                // the field of the referencer that holds the key, as the server reads it
                var f = await Fields(id, NamingFields).ConfigureAwait(false);
                // the field's name without its element index (LinkedReferences[0].Reference names it as LinkedReferences)
                var hit = f.Where(kv => kv.Value.Contains(key.ToString(), StringComparison.OrdinalIgnoreCase))
                           .Select(kv => kv.Key.Split('.')[0].Split('[')[0]).Distinct().ToList();
                list.Add((id, hit.Count > 0 ? string.Join(", ", hit) : $"{type} (a field outside {string.Join(", ", NamingFields)})"));
            }
            else list.Add((id, ed.Length > 0 ? $"{type} {ed}" : type));
        }
        return (list, Math.Max(0, Total(root, Rows(root, "matches").Count()) - Rows(root, "matches").Count()));
    }

    public async Task<(string? FarDoor, string? FarCell, Vec3? Arrival)> DestinationAsync(string door)
    {
        var f = await Fields(door, "TeleportDestination").ConfigureAwait(false);
        if (!f.TryGetValue("TeleportDestination.Door", out var far)) return (null, null, null);
        // the doorstep a player arrives at coming out through this door is the FAR door's destination, not this door's own
        // (this door's TeleportDestination is the arrival point on the far side)
        var c = await Fields(far, "*parent.FormKey", "TeleportDestination").ConfigureAwait(false);
        Vec3? at = c.TryGetValue("TeleportDestination.Position", out var p) ? Parse(p) : null;
        return (far, c.TryGetValue("*parent.FormKey", out var cell) ? cell : null, at);
    }

    public async Task<string?> BaseTypeAsync(string key)
    {
        var f = await Fields(key, "Base").ConfigureAwait(false);
        if (!f.TryGetValue("Base", out var b) || b.Split(' ')[0] is not { Length: > 0 } bk) return null;
        var root = await Records(new() { ["formids"] = new[] { bk } }).ConfigureAwait(false);
        return Rows(root, "records").Select(r => S(r, "type")).FirstOrDefault(t => t is not null);
    }

    public async Task<string?> EnableParentAsync(FormKey key)
    {
        var f = await Fields(key.ToString(), "EnableParent").ConfigureAwait(false);
        return f.TryGetValue("EnableParent.Reference", out var r) ? r : f.TryGetValue("EnableParent", out var e) ? e : null;
    }

    public async Task<string?> MismatchAsync(FormKey key)
        => _drawn(key) is { } pose ? await (await _writer.ConfigureAwait(false)).PlacementMismatchAsync(key, pose).ConfigureAwait(false) : null;

    static Vec3? Parse(string s)
    {
        var parts = s.Trim('[', ']', '(', ')', ' ').Split(',');
        if (parts.Length != 3) return null;
        return float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
            && float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y)
            && float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var z) ? new Vec3(x, y, z) : null;
    }
}
