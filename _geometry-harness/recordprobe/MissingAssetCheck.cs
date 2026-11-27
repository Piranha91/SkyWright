using System.Text.Json;
using HarnessShared;
using HousecarlCore;
using SkyWright.Engine;

/// <summary>loadability.missing-assets: a file a plugin's winning record uses that the game does not find because another
/// mod's archive of the same name wins over the archive that has it. houseCARL owns "which files a record names"
/// (AssetLinkHarvest, over the winning copy of each record the selection touches) and "does the game find this file"
/// (housecarl_asset_status, in process); SkyWright.Engine.RecordClashRule.MissingAsset owns "between which mods". This file
/// reads and writes; it decides nothing.
/// <para>A file the game does not find is asked again of the same load order with only the record's own mod and the mods
/// of the plugins it is made with (its masters, transitively) enabled. If none of them has it, it is that mod's own defect
/// (or a mod it needs is not installed). If one has it in an archive, the archive's name is asked of the whole order: the
/// mod whose archive of that name wins is the one that hides the file. A loose file is never hidden (another layer's copy
/// of it would serve it), so a file found loose there is counted as not told apart. A read houseCARL marks incomplete is
/// not judged.</para></summary>
static class MissingAssetCheck
{
    public const string Id = "loadability.missing-assets";
    public const string Kind = "MISSING-ASSET";
    public const string Population = "files a plugin's winning records of the selection use (one plugin, one file)";
    public const string Present = "the game finds the file";
    public const string Incomplete = "a read houseCARL marks incomplete (an archive or folder it could not read, or archives it could not list)";
    public const string PathRefused = "a file name houseCARL refused as a path";
    public const string Unreadable = "files of a record SkyWright could not read (counted per record)";
    public const string NoOwnView = "the record's own mods could not be asked on their own";

    internal sealed record Use(string Plugin, string Path, string FormId, string Type, string? EditorId) { public int Records { get; set; } = 1; }

    /// <summary>One file's answer from housecarl_asset_status: found or not, the layer or archive the game takes it from
    /// and whether that is an archive, whether houseCARL could refuse it, and whether its "not found" may be an incomplete read.</summary>
    internal sealed record Status(bool Exists, string? Winner, bool InArchive, string? Error, bool MaybeIncomplete);

    /// <summary>One housecarl_asset_status call on these files (in order), JSON; refuses an error or a cut answer.</summary>
    static List<Status> Ask(Func<string, IReadOnlyDictionary<string, object?>, string> call, IReadOnlyList<string> paths, string what)
    {
        if (paths.Count == 0) return new List<Status>();
        string answer = call(ToolNames.AssetStatus, new Dictionary<string, object?> { ["asset_paths"] = paths.ToArray(), ["format"] = "json", ["max_chars"] = 200_000_000 });
        if (answer.StartsWith("error:", StringComparison.Ordinal))
            throw new CheckRefused($"houseCARL's file read ({what}) refused ({answer[6..].Trim()}); fix what it names and scan again.");
        JsonElement root;
        try { using var d = JsonDocument.Parse(answer); root = d.RootElement.Clone(); }
        catch (JsonException) { throw new CheckRefused($"houseCARL's file read ({what}) did not answer in JSON; this is a fault in SkyWright or houseCARL, so report it."); }
        if (root.ValueKind != JsonValueKind.Object || root.TryGetProperty("error", out var er) && er.ValueKind == JsonValueKind.String)
            throw new CheckRefused($"houseCARL's file read ({what}) refused; fix what it names and scan again.");
        if (root.TryGetProperty("truncated", out var t) && t.ValueKind == JsonValueKind.True)
            throw new CheckRefused($"houseCARL's file read ({what}) was cut short, so the check cannot be complete; scan again, and report it if it repeats.");
        var rows = root.TryGetProperty("results", out var rs) && rs.ValueKind == JsonValueKind.Array ? rs.EnumerateArray().ToList() : new List<JsonElement>();
        if (rows.Count != paths.Count)
            throw new CheckRefused($"houseCARL's file read ({what}) answered {rows.Count} of {paths.Count} files, so the check cannot be complete; scan again, and report it if it repeats.");
        bool whole = !(root.TryGetProperty("read_incomplete", out var ri) && ri.ValueKind == JsonValueKind.True);
        return rows.Select(r =>
        {
            if (r.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String) return new Status(false, null, false, e.GetString(), false);
            bool exists = r.TryGetProperty("exists", out var x) && x.ValueKind == JsonValueKind.True;
            string? win = null; bool bsa = false;
            if (r.TryGetProperty("winner", out var w) && w.ValueKind == JsonValueKind.Object)
            {
                win = w.TryGetProperty("name", out var n) ? n.GetString() : null;
                bsa = w.TryGetProperty("kind", out var k) && k.GetString() == "BSA";
            }
            bool maybe = !whole || r.EnumerateObject().Any(p => p.Name.StartsWith("absent_may_be_incomplete", StringComparison.Ordinal) && p.Value.ValueKind == JsonValueKind.True);
            return new Status(exists, win, bsa, null, !exists && maybe);
        }).ToList();
    }

    public static CheckResult Run(RecordContext ctx)
    {
        var order = ctx.Order;
        var byName = order.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
        var setAside = new Dictionary<string, long>(StringComparer.Ordinal);
        var unjudged = new Dictionary<string, long>(StringComparer.Ordinal);
        void Add(Dictionary<string, long> d, string why, long n = 1) => d[why] = d.GetValueOrDefault(why) + n;

        var selected = order.Where(p => p.Selected).Select(p => p.Name).ToList();
        if (selected.Count == 0)
        {
            var lines = new List<CoverageLine> { new(Population, 0, null, null, "rows 0; the selection holds no plugin in this load order") };
            if (RecordChecks.NothingSelectedHere(ctx) is { } looked) lines.Add(looked);
            return new CheckResult(Array.Empty<Conflict>(), lines, Array.Empty<object>());
        }
        if (ctx.AssetLinks is null) throw new CheckRefused("this run cannot read the records' files, so the missing-assets check cannot run; this is a fault in SkyWright, so report it.");
        // the mods of the selection: its mod folders, and the folder of each selected plugin
        var selMods = new HashSet<string>(ctx.Selection?.Mods ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        foreach (var p in order.Where(p => p.Selected && p.Mod.StartsWith("folder:", StringComparison.Ordinal))) selMods.Add(RecordClashRule.ModName(p.Mod));

        // every record the selection touches, as its winning plugin holds it, and the files it uses
        var summary = LostEditCheck.Ask(ctx, new() { ["plugins"] = new Dictionary<string, object?> { ["names"] = selected }, ["project"] = new Dictionary<string, object?> { ["form"] = "summary" } }, "the selection's records");
        var matches = summary.TryGetProperty("matches", out var ms) && ms.ValueKind == JsonValueKind.Array ? ms.EnumerateArray().ToList() : new List<JsonElement>();
        long total = summary.TryGetProperty("total", out var tot) && tot.ValueKind == JsonValueKind.Number ? tot.GetInt64() : -1;
        if (total != matches.Count)
            throw new CheckRefused($"houseCARL listed {matches.Count} of the {total} records the selection touches, so the check cannot be complete; scan again, and report it if it repeats.");
        if (matches.Count == 0)
            throw new CheckRefused($"houseCARL found no record in {string.Join(", ", selected)}, which the load order loads, so it did not read them; scan again, and report it if it repeats.");
        var uses = new Dictionary<(string, string), Use>();
        long records = 0;
        static string? S(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        foreach (var m in matches)
        {
            string formid = S(m, "formid") ?? "", type = S(m, "type") ?? "", winner = S(m, "winner") ?? "";
            winner = RecordDelta.Spelling(winner, order.Select(p => p.Name).ToList()) ?? winner;
            var paths = ctx.AssetLinks(formid, winner);
            if (paths is null) { Add(unjudged, Unreadable); continue; }
            records++;
            foreach (var raw in paths)
            {
                string path = raw.Replace('/', '\\');
                var key = (winner.ToLowerInvariant(), path.ToLowerInvariant());
                if (uses.TryGetValue(key, out var u)) u.Records++;
                else uses[key] = new Use(winner, path, formid, type, S(m, "editorid"));
            }
        }

        // does the game find each file: the whole order, then, for each file it does not find, the record's own mods alone
        var files = uses.Values.Select(u => u.Path).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        var full = files.Zip(Ask(ctx.Call, files, "the files the selection's records use")).ToDictionary(x => x.First, x => x.Second, StringComparer.OrdinalIgnoreCase);
        var verdicts = new List<(Use U, string? Archive, LinkEnd? Shipper, LinkEnd? Hider, RecordVerdict V)>();
        var lost = new List<Use>();
        var found = new List<(Use U, string Why)>();   // every file not asked again, for the rows file
        foreach (var u in uses.Values)
        {
            var st = full[u.Path];
            if (st.Error is not null) { Add(unjudged, PathRefused); found.Add((u, PathRefused)); continue; }
            if (st.Exists) { Add(setAside, Present); found.Add((u, Present)); continue; }
            if (st.MaybeIncomplete) { Add(unjudged, Incomplete); continue; }
            lost.Add(u);
        }
        LinkEnd Layer(string? layer, string archive) => layer switch
        {
            null => throw new InvalidOperationException("no layer"),
            "overwrite" => new LinkEnd(archive, "layer:overwrite", false, selMods.Contains("overwrite")),
            "Data" => new LinkEnd(archive, "plugin:" + archive, GameArchive(archive), false),
            _ => new LinkEnd(archive, "folder:" + layer, false, selMods.Contains(layer)),
        };
        foreach (var g in lost.GroupBy(u => u.Plugin, StringComparer.OrdinalIgnoreCase))
        {
            var p = byName.GetValueOrDefault(g.Key);
            var needer = p is null ? new LinkEnd(g.Key, "plugin:" + g.Key, RecordChecks.IsGameFile(g.Key), false, Loaded: false) : new LinkEnd(p.Name, p.Mod, p.GameFile, p.Selected);
            // the record's own mod and the mods of the plugins it is made with, in the order's priority
            var made = RecordClashRule.Masters(g.Key, n => byName.TryGetValue(n, out var o) ? o.Masters : null).Closure.Append(g.Key);
            var own = made.Select(n => byName.GetValueOrDefault(n)?.Mod).Where(m => m?.StartsWith("folder:", StringComparison.Ordinal) == true)
                .Select(m => RecordClashRule.ModName(m!)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var ownMods = ctx.EnabledMods.Where(own.Contains).ToList();
            var uses2 = g.ToList();
            if (ctx.OwnView is null) { foreach (var u in uses2) Add(unjudged, NoOwnView); continue; }
            var view = ctx.OwnView(ownMods);
            var there = uses2.Zip(Ask(view, uses2.Select(u => u.Path).ToList(), $"{g.Key}'s own mods' files")).ToList();
            // the archives that have a file there: who ships each one there, and whose archive of that name wins in the order
            var archives = there.Where(x => x.Second.Exists && x.Second.InArchive && x.Second.Winner is not null).Select(x => x.Second.Winner!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var shipOf = archives.Zip(Ask(view, archives, $"{g.Key}'s own mods' archives")).ToDictionary(x => x.First, x => x.Second, StringComparer.OrdinalIgnoreCase);
            var winOf = archives.Zip(Ask(ctx.Call, archives, "the archives of those names in the load order")).ToDictionary(x => x.First, x => x.Second, StringComparer.OrdinalIgnoreCase);
            foreach (var (u, st) in there)
            {
                if (st.Error is not null) { Add(unjudged, PathRefused); continue; }
                if (st.MaybeIncomplete) { Add(unjudged, Incomplete); continue; }
                LinkEnd? shipper = null, hider = null; string? archive = null;
                if (st.Exists && st.InArchive && st.Winner is { } a)
                {
                    archive = a;
                    var s = shipOf[a]; var w = winOf[a];
                    if ((!s.Exists && s.MaybeIncomplete) || (!w.Exists && w.MaybeIncomplete)) { Add(unjudged, Incomplete); continue; }
                    shipper = s.Exists && s.Winner is { } sl ? Layer(sl, a) : null;
                    hider = w.Exists && w.Winner is { } wl ? Layer(wl, a) : null;
                    if (shipper is null) hider = null;
                }
                else if (st.Exists && st.Winner is { } loose) shipper = Layer(loose, u.Path);   // loose there: never hidden by an archive, so not told apart
                var v = RecordClashRule.MissingAsset(needer, shipper, hider);
                verdicts.Add((u, archive, shipper, hider, v));
                if (!v.Row) Add(setAside, v.Reason);
            }
        }

        var rows = new List<Conflict>();
        var detail = new List<object>();
        foreach (var (u, why) in found)
            detail.Add(new { check = Id, plugin = u.Plugin, file = u.Path, formid = u.FormId, type = u.Type, editorid = u.EditorId, records = u.Records,
                             archive = (string?)null, shipperMod = (string?)null, hiderMod = (string?)null, row = false, reason = why });
        foreach (var (u, archive, shipper, hider, v) in verdicts)
        {
            detail.Add(new { check = Id, plugin = u.Plugin, file = u.Path, formid = u.FormId, type = u.Type, editorid = u.EditorId, records = u.Records,
                             archive, shipperMod = shipper is null ? null : RecordClashRule.ModName(shipper.Mod), hiderMod = hider is null ? null : RecordClashRule.ModName(hider.Mod),
                             row = v.Row, reason = v.Reason });
            if (!v.Row) continue;
            var p = byName.GetValueOrDefault(u.Plugin);
            rows.Add(new Conflict(Kind, u.Plugin, hider is null ? "" : RecordClashRule.ModName(hider.Mod), Sentence(p, u, archive, shipper, hider),
                new[] { new ConflictRecord(u.FormId, "record", u.EditorId, PhysicalState.Holds, $"the file {u.Path}") },
                new[] { new ConflictRecord($"{u.Path}#{archive}", "file", u.Path) }));
        }
        long judged = setAside.Values.Sum() + rows.Count;
        var line = new CoverageLine(Population, judged, unjudged, setAside,
            $"rows {rows.Count}; {files.Count} files named by {records} records");
        return new CheckResult(rows, new[] { line }, detail);
    }

    /// <summary>An archive in the game's Data folder that is the game's own: a base archive or one of a game file's.</summary>
    static bool GameArchive(string archive)
    {
        string stem = Path.GetFileNameWithoutExtension(archive);
        if (stem.StartsWith("Skyrim - ", StringComparison.OrdinalIgnoreCase)) return true;
        string owner = stem.EndsWith(" - Textures", StringComparison.OrdinalIgnoreCase) ? stem[..^" - Textures".Length] : stem;
        return new[] { ".esm", ".esl", ".esp" }.Any(x => RecordChecks.IsGameFile(owner + x));
    }

    /// <summary>The row in a modder's words: the plugin and its mod, the file, the record, which mod has it in which archive,
    /// and whose archive of the same name wins.</summary>
    public static string Sentence(OrderPlugin? p, Use u, string? archive, LinkEnd? shipper, LinkEnd? hider)
    {
        int colon = u.FormId.IndexOf(':');
        string id = colon > 0 ? $"{u.FormId[..colon]} in {u.FormId[(colon + 1)..]}" : u.FormId;
        string what = u.EditorId is { Length: > 0 } ? $"its {LostEditCheck.TypeWords(u.Type)} {u.EditorId} ({id})" : $"its {LostEditCheck.TypeWords(u.Type)} {id}";
        string more = u.Records > 1 ? $" and {u.Records - 1} other record(s)" : "";
        string ships = shipper is null ? "no mod it is made with" : p is not null && string.Equals(p.Mod, shipper.Mod, StringComparison.OrdinalIgnoreCase) ? "its own mod" : $"mod {RecordClashRule.ModName(shipper.Mod)}";
        string wins = hider is null ? "another archive of the same name" : $"mod {RecordClashRule.ModName(hider.Mod)}'s archive of the same name";
        return $"{RecordChecks.Named(p, u.Plugin)} uses the file {u.Path} in {what}{more}; {ships} has it in the archive {archive ?? "that the game loads"}, "
             + $"but {wins} wins and does not have it, so the game does not find the file.";
    }
}
