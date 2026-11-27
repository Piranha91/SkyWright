using System.Text.Json;
using HarnessShared;
using HousecarlCore;
using HousecarlMcp;
using SkyWright.Engine;

/// <summary>One plugin of the order as the record checks see it: its name, its mod (RecordClashRule.ModOf), whether it is
/// one of the game's own files, and whether it is in the selection the scan judges.</summary>
sealed record OrderPlugin(string Name, string Mod, bool GameFile, bool Selected, IReadOnlyList<string>? Masters = null);

/// <summary>A check that cannot give a result: one sentence, what went wrong and what to try. Nothing is written.</summary>
sealed class CheckRefused(string message) : Exception(message);

/// <summary>What a check reads with: houseCARL's tools in process, and the order (each plugin's masters as its header
/// names them, null when the header could not be read). <paramref name="Instrument"/>: the lost-edit checks' known-answer
/// mode (LostEditCheck), never a scan.</summary>
sealed record RecordContext(Func<string, IReadOnlyDictionary<string, object?>, string> Call, IReadOnlyList<OrderPlugin> Order, bool Instrument = false);

/// <summary>A check's result: its rows and its coverage, in the conflict file's shape, and each row's values in full
/// (written beside the emit as OUT.json.rows.json; null when the check has none).</summary>
sealed record CheckResult(IReadOnlyList<Conflict> Rows, IReadOnlyList<CoverageLine> Coverage, IReadOnlyList<object>? Detail = null);

/// <summary>The record checks this program runs, by registry id. A check is one function of the context; each runs in its
/// own process (one registry row, one child), so a crash stays in one check.</summary>
static class RecordChecks
{
    public static readonly IReadOnlyDictionary<string, Func<RecordContext, CheckResult>> ById =
        new Dictionary<string, Func<RecordContext, CheckResult>>(StringComparer.Ordinal)
        {
            [DanglingCheck.Id] = DanglingCheck.Run,
            [LostEditCheck.FieldsId] = LostEditCheck.Fields,
            [LostEditCheck.CellsId] = LostEditCheck.Cells,
        };

    /// <summary>A plugin is one of the game's own files: Bethesda's or Creation Club's (BaseGamePlugins owns the names).</summary>
    public static bool IsGameFile(string plugin) => BaseGamePlugins.IsBaseGame(plugin) || BaseGamePlugins.IsCreationClub(plugin);

    /// <summary>A plugin in words: "JKs Skyrim.esp (mod JK's Skyrim)", or the plugin's name alone when it is a mod of its own.</summary>
    public static string Named(OrderPlugin? p, string name) =>
        p is not null && p.Mod.StartsWith("folder:", StringComparison.Ordinal) ? $"{name} (mod {RecordClashRule.ModName(p.Mod)})" : name;
}

/// <summary>loadability.dangling-references: a record that points at a record of another mod's plugin which the installed
/// plugin does not have. houseCARL's errors sweep (housecarl_check, findings dangling) over the whole order finds the links;
/// RecordClashRule.Dangling says which are between two mods. A link is judged when either plugin is in the selection
/// (Gate A review finding 1), so the sweep is never scoped by the plugin that holds the link.</summary>
static class DanglingCheck
{
    public const string Id = "loadability.dangling-references";
    public const string Kind = "DANGLING";
    public const string Population = "dangling links houseCARL's errors sweep found in the load order";
    public const string Unscannable = "records houseCARL could not read, whose links are not judged";
    public const string ScanFailed = "plugins houseCARL could not scan, whose links are not judged";
    public const string Unparsed = "plugins houseCARL could not parse and left out of the sweep, whose links are not judged";

    public static readonly IReadOnlyDictionary<string, object?> Args = new Dictionary<string, object?>
    {
        ["findings"] = new[] { "dangling" }, ["format"] = "json", ["limit"] = 1_000_000, ["max_chars"] = 200_000_000,
    };

    public static CheckResult Run(RecordContext ctx) => Judge(ctx.Call(ToolNames.Check, Args), ctx.Order);

    /// <summary>The target's plugin, from its FormKey ("0136DB:Skyrim.esm"). <paramref name="holder"/> is not read: it is
    /// there so the sabotage arm "the target's plugin taken from the plugin that holds the link" is a one-line change.</summary>
    static string OriginOf(string target, string holder) => target[(target.LastIndexOf(':') + 1)..];

    public static CheckResult Judge(string answer, IReadOnlyList<OrderPlugin> order)
    {
        if (answer.StartsWith("error:", StringComparison.Ordinal))
            throw new CheckRefused($"houseCARL's errors sweep refused ({answer[6..].Trim()}); fix what it names and scan again.");
        JsonDocument doc;
        try { doc = JsonDocument.Parse(answer); }
        catch (JsonException) { throw new CheckRefused("houseCARL's errors sweep did not answer in JSON; this is a fault in SkyWright or houseCARL, so report it."); }
        using (doc)
        {
            var root = doc.RootElement;
            if (!root.TryGetProperty("families", out var fams) || !fams.TryGetProperty("errors", out var errors) || errors.ValueKind != JsonValueKind.Object)
                throw new CheckRefused("houseCARL's answer has no errors sweep in it; this is a fault in SkyWright or houseCARL, so report it.");
            long missing = errors.TryGetProperty("accounting", out var acc) && acc.TryGetProperty("dangling_missing", out var dm) ? dm.GetInt64() : 0;
            if (missing > 0)
                throw new CheckRefused($"houseCARL's errors sweep left {missing} dangling link(s) out of its listing, so the check cannot be complete; scan again, and report it if it repeats.");
            var byName = order.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
            var rows = new List<Conflict>();
            var setAside = new Dictionary<string, long>(StringComparer.Ordinal);
            var unjudged = new Dictionary<string, long>(StringComparer.Ordinal);
            long listed = 0, judged = 0;
            var sections = errors.TryGetProperty("plugins", out var ps) && ps.ValueKind == JsonValueKind.Array ? ps.EnumerateArray().ToList() : new List<JsonElement>();
            foreach (var sec in sections)
            {
                string holder = sec.GetProperty("plugin").GetString() ?? "";
                if (sec.TryGetProperty("scan_error", out var se) && se.ValueKind == JsonValueKind.String && se.GetString() is { Length: > 0 })
                    unjudged[ScanFailed] = unjudged.GetValueOrDefault(ScanFailed) + 1;
                if (sec.TryGetProperty("unscannable_records", out var ur) && ur.ValueKind == JsonValueKind.Number && ur.GetInt64() > 0)
                    unjudged[Unscannable] = unjudged.GetValueOrDefault(Unscannable) + ur.GetInt64();
                if (!sec.TryGetProperty("dangling", out var ds) || ds.ValueKind != JsonValueKind.Array) continue;
                foreach (var d in ds.EnumerateArray())
                {
                    listed++;
                    string source = d.GetProperty("source").GetString() ?? "", target = d.GetProperty("target").GetString() ?? "";
                    string origin = OriginOf(target, holder);
                    var h = byName.GetValueOrDefault(holder);
                    var t = byName.GetValueOrDefault(origin);
                    var verdict = RecordClashRule.Dangling(End(h, holder), End(t, origin));
                    judged++;
                    if (!verdict.Row) { setAside[verdict.Reason] = setAside.GetValueOrDefault(verdict.Reason) + 1; continue; }
                    string? edid = d.TryGetProperty("source_editorid", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
                    string? type = d.TryGetProperty("source_type", out var st) && st.ValueKind == JsonValueKind.String ? st.GetString() : null;
                    string path = d.TryGetProperty("path", out var pa) && pa.ValueKind == JsonValueKind.String ? pa.GetString() ?? "" : "";
                    rows.Add(new Conflict(Kind, holder, origin, Sentence(h, holder, t, origin, type, edid),
                        new[] { new ConflictRecord(source, "record", edid, PhysicalState.Holds, $"its {path} field") },
                        new[] { new ConflictRecord(target, "missing record", null) }));
                }
            }
            long total = errors.TryGetProperty("dangling", out var tot) && tot.ValueKind == JsonValueKind.Number ? tot.GetInt64() : -1;
            if (total != listed)
                throw new CheckRefused($"houseCARL's errors sweep counted {total} dangling link(s) and listed {listed}, so the check cannot be complete; scan again, and report it if it repeats.");
            long scanned = errors.TryGetProperty("scanned_plugins", out var sp) && sp.ValueKind == JsonValueKind.Number ? sp.GetInt64() : 0;
            // the whole-order sweep leaves out a plugin houseCARL could not parse (ErrorCheck: the order less ExcludedPlugins)
            // and names it under excluded_plugins; any other plugin of the order it did not sweep is unexplained
            long unparsed = root.TryGetProperty("excluded_plugins", out var ex) && ex.ValueKind == JsonValueKind.Array
                ? ex.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() : x.TryGetProperty("plugin", out var xp) ? xp.GetString() : null)
                    .Count(n => n is not null && byName.ContainsKey(n)) : 0;
            if (unparsed > 0) unjudged[Unparsed] = unparsed;
            if (scanned + unparsed < order.Count)
                throw new CheckRefused($"houseCARL's errors sweep covered {scanned} of the order's {order.Count} plugins and did not say why the others were left out, so the check cannot be complete; scan again, and report it if it repeats.");
            var line = new CoverageLine(Population, judged, unjudged, setAside,
                $"rows {rows.Count}; plugins swept {scanned} of {order.Count}; a link is judged when either plugin is in the selection");
            return new CheckResult(rows, new[] { line });
        }
    }

    static LinkEnd End(OrderPlugin? p, string name) =>
        p is null ? new LinkEnd(name, "plugin:" + name, RecordChecks.IsGameFile(name), false, Loaded: false)
                  : new LinkEnd(p.Name, p.Mod, p.GameFile, p.Selected);

    /// <summary>The row in a modder's words: the two plugins and their mods, what the record is, and what it means.</summary>
    public static string Sentence(OrderPlugin? h, string holder, OrderPlugin? t, string origin, string? type, string? edid)
    {
        string what = (type, edid) switch
        {
            ({ } ty, { Length: > 0 } ed) => $"its {ty} {ed}",
            ({ } ty, _) => $"one of its {ty} records",
            (_, { Length: > 0 } ed) => $"its record {ed}",
            _ => "one of its records",
        };
        return $"{RecordChecks.Named(h, holder)} uses a record of {RecordChecks.Named(t, origin)} in {what}, and the installed {origin} does not have that record: "
             + "the two were made for different versions.";
    }
}
