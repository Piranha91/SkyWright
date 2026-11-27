using System.Text.Json;
using HousecarlCore;
using HarnessShared;
using SkyWright.Engine;

/// <summary>records.contested-fields and records.cell-attributes: a field one mod changed whose winning value is another
/// mod's, where the winner was not made with the loser. houseCARL's record tool, called in process, says what differs (the
/// tree of each contested record the selection touches, and each loser's delta against what it was made with: R2, R5);
/// SkyWright.Engine owns the rest: the delta-line parser (RecordDelta), the field scope and equality (RecordFieldScope),
/// which versions are judged and what is lost (LostEditRule), and between which mods (RecordClashRule). This file reads and
/// writes; it decides nothing.
/// <para>A version is one plugin's copy of one contested record that does not win. Both directions are judged (C5): a
/// version is judged when its plugin or the winner is in the selection. <c>--instrument</c> is the earlier Python capture's
/// rule, a known answer only (never a scan): the selection's plugins as the only losers, each against the plugin beneath it
/// (previous_provider), with no between-mods clause.</para></summary>
static class LostEditCheck
{
    public const string FieldsId = "records.contested-fields";
    public const string CellsId = "records.cell-attributes";
    public const string Kind = "LOST-EDIT";
    public const string NoDepth = "a record houseCARL gave no override depth";
    public const string NoTree = "a contested record whose tree houseCARL did not return";
    public const string NoCompare = "a version houseCARL could not compare";
    const int Batch = 400;

    public static CheckResult Fields(RecordContext ctx) => Run(ctx, cells: false);
    public static CheckResult Cells(RecordContext ctx) => Run(ctx, cells: true);

    /// <summary>One record tool call in JSON; refuses an error, a non-JSON answer, or an answer cut short.</summary>
    static JsonElement Ask(RecordContext ctx, Dictionary<string, object?> args, string what)
    {
        args["format"] = "json"; args["limit"] = 1_000_000; args["max_chars"] = 200_000_000;
        string answer = ctx.Call(ToolNames.Records, args);
        if (answer.StartsWith("error:", StringComparison.Ordinal))
            throw new CheckRefused($"houseCARL's record read ({what}) refused ({answer[6..].Trim()}); fix what it names and scan again.");
        JsonElement root;
        try { using var d = JsonDocument.Parse(answer); root = d.RootElement.Clone(); }
        catch (JsonException) { throw new CheckRefused($"houseCARL's record read ({what}) did not answer in JSON; this is a fault in SkyWright or houseCARL, so report it."); }
        if (root.ValueKind != JsonValueKind.Object)
            throw new CheckRefused($"houseCARL's record read ({what}) gave no answer; this is a fault in SkyWright or houseCARL, so report it.");
        if (root.TryGetProperty("error", out var er) && er.ValueKind == JsonValueKind.String)
            throw new CheckRefused($"houseCARL's record read ({what}) refused ({er.GetString()}); fix what it names and scan again.");
        if (root.TryGetProperty("truncated", out var t) && t.ValueKind == JsonValueKind.True)
            throw new CheckRefused($"houseCARL's record read ({what}) was cut short, so the check cannot be complete; scan again, and report it if it repeats.");
        return root;
    }

    static string? S(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    static List<string> Lines(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Array
        ? v.EnumerateArray().Select(x => x.GetString() ?? "").ToList() : new List<string>();
    static bool Complete(JsonElement e) => !e.TryGetProperty("complete", out var c) || c.ValueKind != JsonValueKind.False;

    /// <summary>Rows of the formids asked, batched; a batch that returns fewer rows than it asked for refuses.</summary>
    static Dictionary<string, JsonElement> Rows(RecordContext ctx, IEnumerable<string> ids, Func<string[], Dictionary<string, object?>> args, string what)
    {
        var got = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        var all = ids.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.Ordinal).ToList();
        for (int i = 0; i < all.Count; i += Batch)
        {
            var batch = all.Skip(i).Take(Batch).ToArray();
            var root = Ask(ctx, args(batch), what);
            var rows = root.TryGetProperty("rows", out var rs) && rs.ValueKind == JsonValueKind.Array ? rs.EnumerateArray().ToList() : new List<JsonElement>();
            if (rows.Count < batch.Length)
                throw new CheckRefused($"houseCARL's record read ({what}) returned {rows.Count} of {batch.Length} records, so the check cannot be complete; scan again, and report it if it repeats.");
            foreach (var r in rows) if (S(r, "formid") is { } f) got[f] = r;
        }
        return got;
    }

    sealed record Version(string FormId, string Type, string? EditorId, string Loser, string Winner, IReadOnlyList<string> Touchers,
                          List<string> VsWinner, bool NodeComplete, string? Versus, IReadOnlyDictionary<string, (IReadOnlyList<string> Lines, bool Complete)> Providers);

    public static CheckResult Run(RecordContext ctx, bool cells)
    {
        var scope = RecordFieldScope.Shipped;
        var order = ctx.Order;
        var names = order.Select(p => p.Name).ToList();
        var byName = order.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
        var direct = order.ToDictionary(p => p.Name, p => p.Masters, StringComparer.OrdinalIgnoreCase);
        var closures = new Dictionary<string, (IReadOnlySet<string> Closure, bool Blind)>(StringComparer.OrdinalIgnoreCase);
        (IReadOnlySet<string> Closure, bool Blind) MastersOf(string p) =>
            closures.TryGetValue(p, out var c) ? c : closures[p] = RecordClashRule.Masters(p, n => direct.TryGetValue(n, out var m) ? m : null);
        var setAside = new Dictionary<string, long>(StringComparer.Ordinal);
        var unjudged = new Dictionary<string, long>(StringComparer.Ordinal);
        void Add(Dictionary<string, long> d, string why, long n = 1) => d[why] = d.GetValueOrDefault(why) + n;
        string population = cells ? "versions of contested cells that do not win (one plugin's copy of one cell)"
                                  : "versions of contested records other than cells that do not win (one plugin's copy of one record)";

        var selected = order.Where(p => p.Selected).Select(p => p.Name).ToList();
        if (selected.Count == 0)
        {
            var lines = new List<CoverageLine> { new(population, 0, null, null, "rows 0; the selection holds no plugin in this load order") };
            if (RecordChecks.NothingSelectedHere(ctx) is { } looked) lines.Add(looked);
            return new CheckResult(Array.Empty<Conflict>(), lines, Array.Empty<object>());
        }

        // the selection: every record a selected plugin touches; contested ones (override depth above one) are read
        var summary = Ask(ctx, new() { ["plugins"] = new Dictionary<string, object?> { ["names"] = selected }, ["project"] = new Dictionary<string, object?> { ["form"] = "summary" } }, "the selection's records");
        var matches = summary.TryGetProperty("matches", out var ms) && ms.ValueKind == JsonValueKind.Array ? ms.EnumerateArray().ToList() : new List<JsonElement>();
        long total = summary.TryGetProperty("total", out var tot) && tot.ValueKind == JsonValueKind.Number ? tot.GetInt64() : -1;
        if (total != matches.Count)
            throw new CheckRefused($"houseCARL listed {matches.Count} of the {total} records the selection touches, so the check cannot be complete; scan again, and report it if it repeats.");
        if (matches.Count == 0)
            throw new CheckRefused($"houseCARL found no record in {string.Join(", ", selected)}, which the load order loads, so it did not read them; scan again, and report it if it repeats.");
        var contested = new List<string>();
        foreach (var m in matches)
        {
            bool isCell = S(m, "type") == "Cell";
            if (isCell != cells) continue;
            if (!m.TryGetProperty("override_depth", out var d) || d.ValueKind != JsonValueKind.Number) { Add(unjudged, NoDepth); continue; }
            if (d.GetInt32() > 1 && S(m, "formid") is { } f) contested.Add(f);
        }

        // who touches each contested record, who wins, and what each version differs from the winner by
        var trees = Rows(ctx, contested, b => new() { ["formids"] = b, ["project"] = new Dictionary<string, object?> { ["form"] = "tree" } }, "the contested records' trees");
        var pending = new List<Version>();
        foreach (var fid in contested)
        {
            if (!trees.TryGetValue(fid, out var tree) || S(tree, "error") is not null) { Add(unjudged, NoTree); continue; }
            string type = S(tree, "type") ?? "";
            var touchers = Lines(tree, "touchers").Select(t => RecordDelta.Spelling(t, names) ?? t).ToList();
            var nodes = tree.TryGetProperty("nodes", out var ns) && ns.ValueKind == JsonValueKind.Array ? ns.EnumerateArray().ToList() : new List<JsonElement>();
            string? winner = nodes.Where(n => n.TryGetProperty("is_winner", out var w) && w.ValueKind == JsonValueKind.True).Select(n => S(n, "plugin")).FirstOrDefault();
            if (winner is null) { Add(unjudged, NoTree); continue; }
            winner = RecordDelta.Spelling(winner, names) ?? winner;
            var providers = nodes.Where(n => S(n, "plugin") is not null).GroupBy(n => S(n, "plugin")!.ToLowerInvariant())
                .ToDictionary(g => g.Key, g => ((IReadOnlyList<string>)Lines(g.First(), "deltas"), Complete(g.First())));
            foreach (var n in nodes)
            {
                string loser = RecordDelta.Spelling(S(n, "plugin"), names) ?? S(n, "plugin") ?? "";
                if (string.Equals(loser, winner, StringComparison.OrdinalIgnoreCase)) continue;
                var lm = MastersOf(loser); var wm = MastersOf(winner);
                var admit = LostEditRule.Admit(type, End(byName, loser), End(byName, winner), wm.Closure.Contains(loser), lm.Blind || wm.Blind, ctx.Instrument, scope);
                if (!admit.Judge) { if (admit.SetAside is { } sa) Add(setAside, sa); if (admit.NotJudged is { } nj) Add(unjudged, nj); continue; }
                string? versus = ctx.Instrument ? "previous_provider" : LostEditRule.MadeWithPole(touchers, loser, lm.Closure);
                pending.Add(new Version(fid, type, S(tree, "editorid"), loser, winner, touchers, Lines(n, "deltas"), Complete(n), versus, providers));
            }
        }

        // each judged version's own changes: against what it was made with (R5), or the plugin beneath it (--instrument)
        var deltas = new Dictionary<(string Loser, string Versus, string FormId), JsonElement>();
        foreach (var g in pending.Where(v => v.Versus is not null).GroupBy(v => (v.Loser, v.Versus!)))
            foreach (var (fid, row) in Rows(ctx, g.Select(v => v.FormId), b => new() { ["formids"] = b, ["source"] = g.Key.Loser, ["versus"] = g.Key.Item2, ["project"] = new Dictionary<string, object?> { ["form"] = "delta" } },
                                             $"{g.Key.Loser}'s changes"))
                deltas[(g.Key.Loser, g.Key.Item2, fid)] = row;

        var found = new List<(Version V, LostField F)>();
        long judged = 0, fields = 0;
        foreach (var v in pending)
        {
            bool defines = v.Versus is null, complete = v.NodeComplete;
            var authored = new List<string>();
            if (!defines)
            {
                if (!deltas.TryGetValue((v.Loser, v.Versus!, v.FormId), out var row)) complete = false;
                else if (S(row, "error") is { } err)
                {
                    if (ctx.Instrument && err.Contains("DEFINES", StringComparison.Ordinal)) defines = true;
                    else { Add(unjudged, NoCompare); continue; }
                }
                else { authored = Lines(row, "deltas"); complete &= Complete(row); }
            }
            var lm = MastersOf(v.Loser);
            // the instrument is the Python capture's rule, which compared rotations and positions as plain numbers and listed derived data
            var r = LostEditRule.Judge(v.Type, authored, v.VsWinner, names, scope, complete, defines, lm.Closure, v.Providers, lm.Blind, angles: !ctx.Instrument, tolerances: !ctx.Instrument);
            foreach (var (why, n) in r.NotJudged) Add(unjudged, why, n);
            foreach (var (why, n) in r.SetAside ?? new Dictionary<string, long>()) Add(setAside, why, n);
            if (!complete) continue;
            judged++; fields += r.FieldsJudged;
            foreach (var f in r.Lost) found.Add((v, f));
        }

        // whether the winner changed each lost field itself, against what it was made with, or only carries an older value
        var winnerPole = found.Select(x => x.V).Distinct().ToDictionary(v => v, v => LostEditRule.MadeWithPole(v.Touchers, v.Winner, MastersOf(v.Winner).Closure));
        var winnerDeltas = new Dictionary<(string, string, string), JsonElement>();
        foreach (var g in winnerPole.Where(kv => kv.Value is not null).GroupBy(kv => (kv.Key.Winner, kv.Value!)))
            foreach (var (fid, row) in Rows(ctx, g.Select(kv => kv.Key.FormId), b => new() { ["formids"] = b, ["source"] = g.Key.Winner, ["versus"] = g.Key.Item2, ["project"] = new Dictionary<string, object?> { ["form"] = "delta" } },
                                             $"{g.Key.Winner}'s changes"))
                winnerDeltas[(g.Key.Winner, g.Key.Item2, fid)] = row;

        var rows = new List<Conflict>();
        var detail = new List<object>();
        foreach (var (v, f) in found)
        {
            string? wPole = winnerPole[v];
            bool? changed;
            if (wPole is null) changed = LostEditRule.WinnerChanged(f.Field, Array.Empty<string>(), true, true, v.Type, names, scope);
            else if (!winnerDeltas.TryGetValue((v.Winner, wPole, v.FormId), out var wr) || S(wr, "error") is not null) changed = null;
            else changed = LostEditRule.WinnerChanged(f.Field, Lines(wr, "deltas"), Complete(wr), false, v.Type, names, scope);
            var lp = byName.GetValueOrDefault(v.Loser); var wp = byName.GetValueOrDefault(v.Winner);
            string sentence = Sentence(lp, v.Loser, wp, v.Winner, v.Type, v.EditorId, v.FormId, f, changed, ctx.Instrument);
            rows.Add(new Conflict(Kind, v.Loser, v.Winner, sentence,
                new[] { new ConflictRecord(v.FormId, "record", v.EditorId, PhysicalState.Holds, $"its {f.Field} field") },
                new[] { new ConflictRecord($"{v.FormId}#{f.Field}#{v.Loser}", "field", f.Field) }));
            detail.Add(new
            {
                check = cells ? CellsId : FieldsId, formid = v.FormId, type = v.Type, editorid = v.EditorId, field = f.Field, paths = f.Paths,
                loser = v.Loser, loserMod = lp is null ? null : RecordClashRule.ModName(lp.Mod), lost = f.Lost,
                winner = v.Winner, winnerMod = wp is null ? null : RecordClashRule.ModName(wp.Mod), winning = f.Winner,
                madeWith = v.Versus, madeWithValue = f.MadeWith, winnerMadeWith = wPole,
                winnerChangedIt = changed switch { true => "yes", false => "no", null => "unknown" },
            });
        }
        var line = new CoverageLine(population, judged, unjudged, setAside,
            $"rows {rows.Count}; fields compared {fields}; " + (ctx.Instrument
                ? "INSTRUMENT: the earlier Python capture's rule (selected plugins as losers, against the plugin beneath, no between-mods clause), never a scan"
                : "a version is judged when its plugin or the winner is in the selection, against what its plugin was made with"));
        return new CheckResult(rows, new[] { line }, detail);
    }

    static LinkEnd End(Dictionary<string, OrderPlugin> byName, string name) =>
        byName.TryGetValue(name, out var p) ? new LinkEnd(p.Name, p.Mod, p.GameFile, p.Selected)
                                            : new LinkEnd(name, "plugin:" + name, RecordChecks.IsGameFile(name), false, Loaded: false);

    /// <summary>A record type in words: "placed object" for PlacedObject, "NPC" for Npc.</summary>
    internal static string TypeWords(string type) => type switch
    {
        "Npc" => "NPC", "PlacedNpc" => "placed NPC", "NavigationMesh" => "navmesh",
        _ => System.Text.RegularExpressions.Regex.Replace(type, "(?<=[a-z])(?=[A-Z])", " ").ToLowerInvariant(),
    };

    /// <summary>A value short and plain enough to say inside the sentence, or null.</summary>
    static string? Sayable(string? v) => v is { Length: > 0 and <= 48 } && !v.Contains(". ") && !v.Contains(';') && !v.EndsWith('.') ? v : null;

    /// <summary>The row in a modder's words: what was changed, whose value wins, and that the winner was not made with the loser.</summary>
    public static string Sentence(OrderPlugin? lp, string loser, OrderPlugin? wp, string winner, string type, string? edid, string formId,
                                  LostField f, bool? winnerChanged, bool instrument)
    {
        int colon = formId.IndexOf(':');
        string id = colon > 0 ? $"{formId[..colon]} in {formId[(colon + 1)..]}" : formId;
        string what = edid is { Length: > 0 } ? $"the {TypeWords(type)} {edid} ({id})" : $"the {TypeWords(type)} {id}";
        // the two values only when they read differently: a list houseCARL compares by content is shown as its item count
        bool say = Sayable(f.Lost) is not null && Sayable(f.Winner) is not null && f.Lost != f.Winner;
        string to = say ? $" to {f.Lost}" : "", with = say ? $" with {f.Winner}" : "";
        string whose = instrument ? "" : winnerChanged switch
        {
            true => ", a change of its own,",
            false => ", the value it was made with,",
            null => "",
        };
        string tail = instrument ? "." : $" and was not made with {loser}, so the change is lost in game.";
        return $"{RecordChecks.Named(lp, loser)} changed the {f.Field} of {what}{to}; {RecordChecks.Named(wp, winner)} wins{with}{whose}{tail}";
    }
}
