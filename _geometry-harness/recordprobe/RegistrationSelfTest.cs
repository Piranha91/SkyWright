using System.Text.Json;
using HarnessShared;
using SkyWright.Engine;
using Fx = LostEditSelfTest.Fx;
using Rec = LostEditSelfTest.Rec;

/// <summary>recordprobe --self-test: the registration and dialogue check's known answers (RG1-RG14, record checks unit 4,
/// ruling R8), each one assertion, over houseCARL answers in the shapes its record tool gives (summary, tree, delta, and a
/// form list's items read as fields) and its check tool gives (the scripts and dialogue findings), written here so each
/// clause has a case that only it decides. The order is LostEditSelfTest's: Skyrim.esm, Update.esm, Base.esp and
/// Base - Extra.esp (mod Base Mod), Patch.esp (made with Base.esp), Hub.esp (made with Patch.esp), Mid.esp, Late.esp,
/// Other.esp, each in its own mod.</summary>
static class RegistrationSelfTest
{
    const string FL = "000805:Skyrim.esm";
    const string A = "000A0A:Skyrim.esm", B = "000A0B:Skyrim.esm", X = "000A01:Base.esp", Y = "000A02:Other.esp";

    /// <summary>A form list of Skyrim.esm holding A and B; Base.esp's copy holds <paramref name="mine"/>, the winner's
    /// <paramref name="theirs"/>, with houseCARL's delta lines for both comparisons as its FieldsDiff writes them.</summary>
    static Fx List(string winner, string[] mine, string[] theirs, string[] authored, string[] vsWinner, params string[] selected)
    {
        var fx = new Fx
        {
            Order = LostEditSelfTest.Order(selected),
            Recs = { new Rec(FL, "FormList", new[] { "Skyrim.esm", "Base.esp", winner }, new() { ["Base.esp"] = vsWinner }, "SelfTestList") },
        };
        fx.Auth[("Base.esp", "Skyrim.esm", FL)] = authored;
        fx.Items[("Skyrim.esm", FL)] = new[] { A, B };
        fx.Items[("Base.esp", FL)] = mine;
        fx.Items[(winner, FL)] = theirs;
        return fx;
    }

    /// <summary>Base.esp added X; the winner's copy holds A, B and its own Y, not X.</summary>
    static Fx Added(string winner, params string[] selected) => List(winner, new[] { A, B, X }, new[] { A, B, Y },
        new[] { $"Items: 3 vs Skyrim.esm 2 item(s) — only here: [2] {X}" },
        new[] { $"Items: 3 vs {winner} 3 item(s), contents differ — only here: [2] {X}; only in {winner}: [2] {Y}" }, selected);

    static CheckResult Run(Fx fx, bool instrument = false, Selection? sel = null) =>
        RegistrationCheck.Run(new RecordContext(fx.Call, fx.Order, instrument, sel));

    static string Show(IReadOnlyDictionary<string, long>? d) => string.Join(", ", (d ?? new Dictionary<string, long>()).Select(kv => $"{kv.Key} {kv.Value}"));

    /// <summary>The lost-edit line's exact rows (loser, winner, field), no-row reasons and, when given, not-judged reasons.</summary>
    static string? Expect(CheckResult r, (string A, string B, string Field)[] rows, (string Why, long N)[] aside, (string Why, long N)[]? unj = null)
    {
        var got = r.Rows.Select(c => (c.A, c.B, c.Alternative?[0].Label ?? "")).ToList();
        var line = r.Coverage.FirstOrDefault(c => c.Population == RegistrationCheck.Population);
        if (line is null) return $"no '{RegistrationCheck.Population}' line; coverage: {string.Join(" | ", r.Coverage.Select(c => c.Population))}";
        var sa = line.SetAside ?? new Dictionary<string, long>();
        var un = line.Unjudgeable ?? new Dictionary<string, long>();
        bool ok = got.SequenceEqual(rows) && sa.Count == aside.Length && aside.All(x => sa.GetValueOrDefault(x.Why) == x.N)
                  && (unj is null || (un.Count == unj.Length && unj.All(x => un.GetValueOrDefault(x.Why) == x.N)));
        return ok ? null : $"expected rows [{string.Join("; ", rows)}], no row: {string.Join(", ", aside.Select(x => $"{x.Why} {x.N}"))}"
                           + (unj is null ? "" : $", not judged: {string.Join(", ", unj.Select(x => $"{x.Why} {x.N}"))}")
                           + $"; got rows [{string.Join("; ", got)}], no row: {Show(sa)}, not judged: {Show(un)}";
    }

    static readonly (string, long)[] None = Array.Empty<(string, long)>();
    static JsonElement DetailOf(CheckResult r, int i) => JsonSerializer.SerializeToElement(r.Detail![i]);
    static string? Value(CheckResult r, int i, string k) => r.Detail is { } d && d.Count > i && DetailOf(r, i).TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    static bool OneSentence(string s) => s.EndsWith('.') && System.Text.RegularExpressions.Regex.Matches(s, @"[.!?](\s|$)").Count == 1;

    /// <summary>houseCARL's check answer for the selection: two records with an unbound script property each (one with two),
    /// and one seeded quest with one input issue and a topic with one issue and one silent line, as its json writes them.</summary>
    static string NativeAnswer() => JsonSerializer.Serialize(new Dictionary<string, object?>
    {
        ["families"] = new Dictionary<string, object?>
        {
            ["scripts"] = new Dictionary<string, object?>
            {
                ["records_with_scripts"] = 5, ["unbound"] = 3, ["unverifiable"] = 1, ["read_incomplete"] = false,
                ["records"] = new object[]
                {
                    new Dictionary<string, object?> { ["formid"] = "000B01:Base.esp", ["type"] = "Npc", ["plugin"] = "Base.esp",
                        ["unbound"] = new object[] { new Dictionary<string, object?> { ["property"] = "DeathContainer", ["script"] = "S", ["class"] = "unbound_object" },
                                                     new Dictionary<string, object?> { ["property"] = "Count", ["script"] = "S", ["class"] = "unbound_scalar" } } },
                    new Dictionary<string, object?> { ["formid"] = "000B02:Base.esp", ["type"] = "Activator", ["plugin"] = "Base.esp",
                        ["unbound"] = new object[] { new Dictionary<string, object?> { ["property"] = "Door", ["script"] = "T", ["class"] = "unbound_object" } } },
                },
            },
            ["dialogue"] = new Dictionary<string, object?>
            {
                ["seeds_validated"] = 2, ["seeds_unreachable_total"] = 0, ["findings_found"] = 3,
                ["seeds"] = new object[]
                {
                    new Dictionary<string, object?>
                    {
                        ["seed"] = "000C01:Base.esp", ["input_issues"] = new object[] { "a quest stage with no log entry" },
                        ["topics"] = new object[] { new Dictionary<string, object?> { ["topic"] = "000C02:Base.esp",
                            ["issues"] = new object[] { new Dictionary<string, object?> { ["severity"] = "warning", ["message"] = "response 1 text contains non-ASCII char(s)" } },
                            ["silent_lines"] = new object[] { new Dictionary<string, object?> { ["info"] = "000C03:Base.esp" } } } },
                    },
                },
            },
        },
    });

    public static readonly (string Name, Func<string?> Check)[] Cases =
    {
        ("RG1 R8: an item one mod added to a form list that the winning copy, of another mod not made with it, does not hold is exactly 1 row, naming the item", () =>
        {
            var r = Run(Added("Other.esp", "Base.esp"));
            return Expect(r, [("Base.esp", "Other.esp", "Items")], [(RecordClashRule.NotSelected, 1)])
                   ?? (Value(r, 0, "item") == X && Value(r, 0, "added") == "yes" ? null : $"the row's item is '{Value(r, 0, "item")}' added '{Value(r, 0, "added")}', not {X} added yes");
        }),
        ("RG2 R8: an item one mod removed from a form list that the winning copy still holds is exactly 1 row, naming the item, said removed", () =>
        {
            var r = Run(List("Other.esp", new[] { A }, new[] { A, B }, new[] { $"Items: 1 vs Skyrim.esm 2 item(s) — only in Skyrim.esm: [1] {B}" },
                new[] { $"Items: 1 vs Other.esp 2 item(s) — only in Other.esp: [1] {B}" }, "Base.esp"));
            return Expect(r, [("Base.esp", "Other.esp", "Items")], [(RecordClashRule.NotSelected, 1)])
                   ?? (Value(r, 0, "item") == B && Value(r, 0, "added") == "no" ? null : $"the row's item is '{Value(r, 0, "item")}' added '{Value(r, 0, "added")}', not {B} added no");
        }),
        ("RG3 a winner made with the plugin whose item it drops gives 0 rows, counted as made with it", () =>
            Expect(Run(Added("Patch.esp", "Base.esp")), [], [(RecordClashRule.MadeWith, 1), (RecordClashRule.NotSelected, 1)])),
        ("RG4 two plugins of one mod are 0 rows, counted as one mod's own business", () =>
            Expect(Run(Added("Base - Extra.esp", "Base.esp")), [], [(RecordClashRule.SameModEdit, 1), (RecordClashRule.NotSelected, 1)])),
        ("RG5 a game file on either side is 0 rows, counted as the game's own file", () =>
        {
            var fx = Added("Other.esp", "Other.esp");
            fx.Recs[0] = fx.Recs[0] with { Touchers = new[] { "Skyrim.esm", "Update.esm", "Other.esp" }, VsWinner = new() { ["Update.esm"] = new[] { $"Items: 3 vs Other.esp 3 item(s), contents differ — only here: [2] {X}; only in Other.esp: [2] {Y}" } } };
            fx.Auth[("Update.esm", "Skyrim.esm", FL)] = new[] { $"Items: 3 vs Skyrim.esm 2 item(s) — only here: [2] {X}" };
            fx.Items[("Update.esm", FL)] = new[] { A, B, X };
            return Expect(Run(fx), [], [(RecordClashRule.GameFileSide, 2)]);
        }),
        ("RG6 a pair with neither plugin in the selection is 0 rows, counted as outside the selection", () =>
        {
            var fx = Added("Other.esp", "Mid.esp");
            fx.Recs[0] = fx.Recs[0] with { Touchers = new[] { "Skyrim.esm", "Base.esp", "Mid.esp", "Other.esp" } };
            return Expect(Run(fx), [], [(RecordClashRule.NotSelected, 2)]);
        }),
        ("RG7 a list only reordered: a form list (the game reads its order) is not judged, a location's keywords are no change; 0 rows", () =>
        {
            var fx = List("Other.esp", new[] { B, A }, new[] { A, B, Y }, new[] { "Items: same 2 item(s), ORDER DIFFERS from Skyrim.esm" },
                new[] { $"Items: 2 vs Other.esp 3 item(s) — only in Other.esp: [2] {Y}" }, "Base.esp");
            const string loc = "000806:Skyrim.esm";
            fx.Recs.Add(new Rec(loc, "Location", new[] { "Skyrim.esm", "Base.esp", "Other.esp" }, new() { ["Base.esp"] = new[] { "Keywords: same 2 item(s), ORDER DIFFERS from Other.esp" } }));
            fx.Auth[("Base.esp", "Skyrim.esm", loc)] = new[] { "Keywords: same 2 item(s), ORDER DIFFERS from Skyrim.esm" };
            return Expect(Run(fx), [], [(RecordClashRule.NotSelected, 2)], [(LostEditRule.OrderRead, 1)]);
        }),
        ("RG8 a delta line SkyWright cannot read is counted as not judged, and the rest of the version is still judged", () =>
        {
            var fx = Added("Other.esp", "Base.esp");
            fx.Auth[("Base.esp", "Skyrim.esm", FL)] = fx.Auth[("Base.esp", "Skyrim.esm", FL)].Append("Wiggle~ a line of no known shape").ToArray();
            return Expect(Run(fx), [("Base.esp", "Other.esp", "Items")], [(RecordClashRule.NotSelected, 1)], [(LostEditRule.Unreadable, 1)]);
        }),
        ("RG9 a read houseCARL marks incomplete is not judged, never a clean result", () =>
        {
            var fx = Added("Other.esp", "Base.esp");
            fx.Recs[0] = fx.Recs[0] with { Incomplete = new() { "Base.esp" } };
            return Expect(Run(fx), [], [(RecordClashRule.NotSelected, 1)], [(LostEditRule.Incomplete, 1)]);
        }),
        ("RG10 a form list whose items houseCARL could not all read is not judged, never a clean result", () =>
        {
            var fx = Added("Other.esp", "Base.esp");
            fx.ItemNote.Add(("Other.esp", FL));
            return Expect(Run(fx), [], [(RecordClashRule.NotSelected, 1)], [(RegistrationCheck.ItemsUnread, 1)]);
        }),
        ("RG11 houseCARL's script and dialogue findings (one plugin's own) are counted under their own reasons, never rows", () =>
        {
            var fx = Added("Patch.esp", "Base.esp");
            fx.Recs.Add(new Rec("000C01:Base.esp", "Quest", new[] { "Base.esp" }, new()));   // a quest the selection wins: the dialogue check's seed
            fx.CheckAnswer = NativeAnswer();
            var r = Run(fx);
            if (r.Rows.Count != 0) return $"{r.Rows.Count} rows: {string.Join(" | ", r.Rows.Select(c => c.Summary))}";
            var n = r.Coverage.FirstOrDefault(c => c.Population == RegistrationCheck.NativePopulation);
            if (n is null) return $"no '{RegistrationCheck.NativePopulation}' line";
            var sa = n.SetAside ?? new Dictionary<string, long>();
            var un = n.Unjudgeable ?? new Dictionary<string, long>();
            return n.Judged == 7 && sa.Count == 2 && sa.GetValueOrDefault(RegistrationCheck.ScriptOwn) == 3 && sa.GetValueOrDefault(RegistrationCheck.DialogueOwn) == 3
                   && un.Count == 1 && un.GetValueOrDefault(RegistrationCheck.ScriptUnverifiable) == 1
                ? null : $"judged {n.Judged}, no row: {Show(sa)}, not judged: {Show(un)}; expected judged 7, script 3, dialogue 3, unverifiable 1";
        }),
        ("RG12 the row says it in a modder's words: the plugins and their mods, the list, the item, what wins and 'not made with', one sentence", () =>
        {
            var r = Run(Added("Other.esp", "Base.esp"));
            if (r.Rows.Count != 1) return $"{r.Rows.Count} rows";
            var s = r.Rows[0].Summary;
            string[] banned = { "provider", "delta", "FormKey", "override depth", "versus", "toucher", "loser", "selection", "instrument", "Items" };
            if (banned.FirstOrDefault(b => s.Contains(b, StringComparison.Ordinal)) is { } bad) return $"'{bad}' in: {s}";
            if (!s.Contains("Base.esp (mod Base Mod)") || !s.Contains("Other.esp (mod Other Mod) wins") || !s.Contains("not made with Base.esp")
                || !s.Contains("form list") || !s.Contains("000A01")) return "the plugins, their mods, the list, the item, the winner or 'not made with' are not named: " + s;
            return OneSentence(s) ? null : "not one sentence: " + s;
        }),
        ("RG13 two items lost on one list are two rows, each its own identity", () =>
        {
            var fx = List("Other.esp", new[] { A, X }, new[] { A, B, Y }, new[] { $"Items: 2 vs Skyrim.esm 2 item(s), contents differ — only here: [1] {X}; only in Skyrim.esm: [1] {B}" },
                new[] { $"Items: 2 vs Other.esp 3 item(s) — only here: [1] {X}; only in Other.esp: [1] {B}; [2] {Y}" }, "Base.esp");
            var r = Run(fx);
            if (r.Rows.Count != 2) return $"{r.Rows.Count} rows, not 2 (X added, B removed)";
            var ids = r.Rows.Select(c => ConflictIo.RowIdentity(c.Kind, c.Necessary[0].FormKey, c.Alternative?[0].FormKey, c.A)).ToList();
            return ids.Distinct().Count() == 2 ? null : "the two rows share an identity: " + string.Join(" | ", ids);
        }),
        ("RG14 the instrument, as the Python capture, compares a form list as one value: 1 row on the field, no item", () =>
        {
            var r = Run(Added("Other.esp", "Base.esp"), instrument: true);
            return Expect(r, [("Base.esp", "Other.esp", "Items")], [(RecordClashRule.NotSelected, 1)])
                   ?? (Value(r, 0, "item") is null ? null : $"the instrument's row names the item {Value(r, 0, "item")}");
        }),
    };

    /// <summary>RG1's emit, for runcontrol's self-test: one item lost between two mods.</summary>
    public static CheckResult CrossModEmit() => Run(Added("Other.esp", "Base.esp"));
}
