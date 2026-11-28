using System.Text.Json;
using HarnessShared;
using HousecarlCore;
using SkyWright.Engine;

/// <summary>records.registration-and-dialogue (record checks unit 4, ruling R8; held). Two parts:
/// <para>The lost edits on registration and dialogue records (the field scope's registrationTypes: quests, topics and their
/// lines, scenes, form lists, locations, story-manager nodes, packages): LostEditCheck's reads and LostEditRule's rule,
/// the same between-mods clauses as records.contested-fields, with the form-list item rule (a form list's items judged item
/// by item: an item one mod added that the winning copy does not hold, or removed and the winning copy holds, is one row).
/// </para>
/// <para>houseCARL's own script and dialogue findings (housecarl_check, findings scripts and dialogue) on the selection's
/// plugins: each is one plugin's own (an unbound script property, a line's text), so they are counted in the coverage under
/// their reasons and never rows (ruling R1).</para>
/// The order of a topic's lines between mods (houseCARL's info_order) is not read; the registry row's limits say so.</summary>
static class RegistrationCheck
{
    public const string Id = "records.registration-and-dialogue";
    public const string Population = "versions of contested registration and dialogue records that do not win (one plugin's copy of one record)";
    public const string NativePopulation = "houseCARL's own script and dialogue checks on the selection's plugins (records with scripts, and the quests and topics the selection wins)";
    public const string ScriptOwn = "an unbound script property houseCARL's script check found: one plugin's own (counted per property)";
    public const string DialogueOwn = "a finding houseCARL's dialogue check made: one plugin's own lines (counted per finding)";
    public const string ScriptUnverifiable = "a script attachment whose compiled script houseCARL could not read";
    public const string ScriptsIncomplete = "houseCARL's script check marked its read incomplete";
    public const string ScriptsUnlisted = "unbound script properties houseCARL counted and did not list";
    public const string SeedUnreachable = "a quest or topic houseCARL's dialogue check could not reach";
    public const string DialogueUnlisted = "dialogue findings houseCARL counted and did not list";
    public const string ItemsUnread = LostEditRule.ItemsUnread;

    public static CheckResult Run(RecordContext ctx)
    {
        var lost = LostEditCheck.Run(ctx, LostEditCheck.Part.Registration, out var matches);
        var selected = ctx.Order.Where(p => p.Selected).Select(p => p.Name).ToList();
        if (selected.Count == 0) return lost;
        return lost with { Coverage = lost.Coverage.Append(Native(ctx, selected, matches)).ToList() };
    }

    static long N(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;
    static IEnumerable<JsonElement> Arr(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray() : Enumerable.Empty<JsonElement>();

    /// <summary>houseCARL's scripts check over the selection's plugins and its dialogue check over the quests and topics the
    /// selection wins (the seeds), as the module's capture asked them, every finding counted under its reason.</summary>
    static CoverageLine Native(RecordContext ctx, IReadOnlyList<string> selected, IReadOnlyList<JsonElement> matches)
    {
        var sel = selected.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var seeds = matches.Where(m => m.TryGetProperty("type", out var t) && t.GetString() is "Quest" or "DialogTopic"
                                       && m.TryGetProperty("winner", out var w) && w.ValueKind == JsonValueKind.String && sel.Contains(w.GetString()!))
                           .Select(m => m.GetProperty("formid").GetString()!).ToArray();
        var args = new Dictionary<string, object?>
        {
            ["findings"] = seeds.Length > 0 ? new[] { "scripts", "dialogue" } : new[] { "scripts" }, ["plugins"] = selected.ToArray(),
            ["format"] = "json", ["limit"] = 1_000_000, ["max_chars"] = 200_000_000,
        };
        if (seeds.Length > 0) args["seeds"] = seeds;
        string answer = ctx.Call(ToolNames.Check, args);
        if (answer.StartsWith("error:", StringComparison.Ordinal))
            throw new CheckRefused($"houseCARL's script and dialogue check refused ({answer[6..].Trim()}); fix what it names and scan again.");
        JsonDocument doc;
        try { doc = JsonDocument.Parse(answer); }
        catch (JsonException) { throw new CheckRefused("houseCARL's script and dialogue check did not answer in JSON; this is a fault in SkyWright or houseCARL, so report it."); }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.TryGetProperty("truncated", out var tr) && tr.ValueKind == JsonValueKind.True)
                throw new CheckRefused("houseCARL's script and dialogue check was cut short, so the check cannot be complete; scan again, and report it if it repeats.");
            if (!root.TryGetProperty("families", out var fams) || !fams.TryGetProperty("scripts", out var sc) || sc.ValueKind != JsonValueKind.Object)
                throw new CheckRefused("houseCARL's answer has no script check in it; this is a fault in SkyWright or houseCARL, so report it.");
            var aside = new Dictionary<string, long>(StringComparer.Ordinal);
            var unjudged = new Dictionary<string, long>(StringComparer.Ordinal);
            void Add(Dictionary<string, long> d, string why, long n) { if (n > 0) d[why] = d.GetValueOrDefault(why) + n; }
            long judged = N(sc, "records_with_scripts");
            long unbound = Arr(sc, "records").Sum(r => (long)Arr(r, "unbound").Count());
            Add(aside, ScriptOwn, unbound);
            if (sc.TryGetProperty("unbound", out var ub) && ub.ValueKind == JsonValueKind.Number) Add(unjudged, ScriptsUnlisted, Math.Abs(ub.GetInt64() - unbound));
            Add(unjudged, ScriptUnverifiable, N(sc, "unverifiable"));
            if (sc.TryGetProperty("read_incomplete", out var ri) && ri.ValueKind == JsonValueKind.True) Add(unjudged, ScriptsIncomplete, 1);
            if (seeds.Length > 0)
            {
                if (!fams.TryGetProperty("dialogue", out var dl) || dl.ValueKind != JsonValueKind.Object)
                    throw new CheckRefused("houseCARL's dialogue check did not answer for the selection's quests and topics; this is a fault in SkyWright or houseCARL, so report it.");
                judged += N(dl, "seeds_validated");
                Add(unjudged, SeedUnreachable, N(dl, "seeds_unreachable_total"));
                long listed = 0;
                foreach (var seed in Arr(dl, "seeds"))
                {
                    listed += Arr(seed, "input_issues").Count();
                    foreach (var topic in Arr(seed, "topics")) listed += Arr(topic, "issues").Count() + Arr(topic, "silent_lines").Count();
                }
                Add(aside, DialogueOwn, listed);
                Add(unjudged, DialogueUnlisted, Math.Abs(N(dl, "findings_found") - listed));
            }
            return new CoverageLine(NativePopulation, judged, unjudged, aside,
                $"seeds {seeds.Length}; each finding is one plugin's own, counted, never a row");
        }
    }
}
