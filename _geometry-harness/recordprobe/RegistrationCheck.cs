using System.Text.Json;
using HousecarlCore;

/// <summary>records.registration-and-dialogue. STUB for the known answers' red point (record checks unit 4): every line a
/// registration record's version differs from the winner by is a row, and houseCARL's script and dialogue findings are rows.
/// The rule replaces it.</summary>
static class RegistrationCheck
{
    public const string Id = "records.registration-and-dialogue";
    public const string Population = "versions of contested registration and dialogue records that do not win (one plugin's copy of one record)";
    public const string NativePopulation = "houseCARL's own script and dialogue checks on the selection's plugins";
    public const string ScriptOwn = "an unbound script property: one plugin's own";
    public const string DialogueOwn = "a dialogue finding: one plugin's own";
    public const string ScriptUnverifiable = "a script attachment houseCARL could not read";
    public const string ItemsUnread = "a list judged item by item whose items houseCARL did not list in full";

    static readonly HashSet<string> Types = new(StringComparer.Ordinal) { "Quest", "DialogTopic", "DialogResponses", "Scene", "FormList", "Location", "Package" };

    public static CheckResult Run(RecordContext ctx)
    {
        var selected = ctx.Order.Where(p => p.Selected).Select(p => p.Name).ToList();
        var summary = LostEditCheck.Ask(ctx, new() { ["plugins"] = new Dictionary<string, object?> { ["names"] = selected }, ["project"] = new Dictionary<string, object?> { ["form"] = "summary" } }, "the selection's records");
        var ids = summary.GetProperty("matches").EnumerateArray().Where(m => Types.Contains(m.GetProperty("type").GetString() ?? "")).Select(m => m.GetProperty("formid").GetString()!).ToArray();
        var rows = new List<Conflict>();
        if (ids.Length > 0)
        {
            var trees = LostEditCheck.Ask(ctx, new() { ["formids"] = ids, ["project"] = new Dictionary<string, object?> { ["form"] = "tree" } }, "trees");
            foreach (var t in trees.GetProperty("rows").EnumerateArray())
            {
                var nodes = t.GetProperty("nodes").EnumerateArray().ToList();
                string winner = nodes.First(n => n.GetProperty("is_winner").GetBoolean()).GetProperty("plugin").GetString()!;
                foreach (var n in nodes.Where(n => !n.GetProperty("is_winner").GetBoolean()))
                    foreach (var line in n.TryGetProperty("deltas", out var d) ? d.EnumerateArray().Select(x => x.GetString() ?? "") : Enumerable.Empty<string>())
                    {
                        string field = line.Split(':', '=')[0];
                        string loser = n.GetProperty("plugin").GetString()!;
                        rows.Add(new Conflict("LOST-EDIT", loser, winner, $"{loser} lost {field} to {winner}.",
                            new[] { new ConflictRecord(t.GetProperty("formid").GetString()!, "record", null) }, new[] { new ConflictRecord($"{t.GetProperty("formid").GetString()}#{field}#{loser}", "field", field) }));
                    }
            }
        }
        var lines = new List<CoverageLine> { new(Population, rows.Count, null, null, "STUB") };
        try
        {
            using var doc = JsonDocument.Parse(ctx.Call(ToolNames.Check, new Dictionary<string, object?> { ["findings"] = new[] { "scripts", "dialogue" }, ["format"] = "json" }));
            foreach (var r in doc.RootElement.GetProperty("families").GetProperty("scripts").GetProperty("records").EnumerateArray())
                foreach (var u in r.GetProperty("unbound").EnumerateArray())
                    rows.Add(new Conflict("UNBOUND-SCRIPT-PROPERTY", r.GetProperty("plugin").GetString()!, r.GetProperty("plugin").GetString()!, "unbound script property.", new[] { new ConflictRecord(r.GetProperty("formid").GetString()!, "record", null) }));
            lines.Add(new CoverageLine(NativePopulation, 0, null, null, "STUB"));
        }
        catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException or JsonException) { }
        return new CheckResult(rows, lines);
    }
}
