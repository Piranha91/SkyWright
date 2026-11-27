using System.Text.Json;
using HarnessShared;
using HousecarlCore;
using SkyWright.Engine;

/// <summary>recordprobe --self-test: the lost-edit checks' known answers (LE1-LE24), each one assertion, over houseCARL
/// answers in the shapes its record tool gives (summary, tree, delta: lab3's transcripts, record checks Gate A), written
/// here so each clause of the rule has a case that only it decides. A fixture is the order, its records (each with its
/// touching plugins in load order, winner last, and each other version's delta lines against the winner) and each
/// plugin's delta lines against another plugin; a pair with no lines is identical.</summary>
static class LostEditSelfTest
{
    const string Mods = @"X:\inst\mods";

    sealed record Rec(string FormId, string Type, string[] Touchers, Dictionary<string, string[]> VsWinner, string? Edid = null,
                      HashSet<string>? Incomplete = null, bool NoDepth = false);

    sealed class Fx
    {
        public List<OrderPlugin> Order = new();
        public List<Rec> Recs = new();
        public Dictionary<(string Source, string Versus, string FormId), string[]> Auth = new();

        public string Call(string tool, IReadOnlyDictionary<string, object?> args)
        {
            if (tool != ToolNames.Records) throw new InvalidOperationException($"the fixture answers {ToolNames.Records} only, not {tool}");
            string form = (string)((Dictionary<string, object?>)args["project"]!)["form"]!;
            var ids = args.TryGetValue("formids", out var f) && f is string[] fs ? fs : Array.Empty<string>();
            Rec R(string id) => Recs.First(r => r.FormId == id);
            object reply = form switch
            {
                "summary" => Summary((IEnumerable<string>)((Dictionary<string, object?>)args["plugins"]!)["names"]!),
                "tree" => new Dictionary<string, object?> { ["form"] = "tree", ["count"] = ids.Length, ["truncated"] = false, ["rows"] = ids.Select(id => Tree(R(id))).ToList() },
                "delta" => new Dictionary<string, object?> { ["form"] = "delta", ["count"] = ids.Length, ["truncated"] = false,
                    ["rows"] = ids.Select(id => Delta(R(id), (string)args["source"]!, (string)args["versus"]!)).ToList() },
                _ => throw new InvalidOperationException("form " + form),
            };
            return JsonSerializer.Serialize(reply);
        }

        object Summary(IEnumerable<string> names)
        {
            var set = names.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var m = Recs.Where(r => r.Touchers.Any(set.Contains)).Select(r =>
            {
                var d = new Dictionary<string, object?> { ["formid"] = r.FormId, ["type"] = r.Type, ["winner"] = r.Touchers[^1] };
                if (!r.NoDepth) d["override_depth"] = r.Touchers.Length;
                return d;
            }).ToList();
            return new Dictionary<string, object?> { ["form"] = "summary", ["total"] = m.Count, ["truncated"] = false, ["matches"] = m };
        }

        static object Tree(Rec r) => new Dictionary<string, object?>
        {
            ["formid"] = r.FormId, ["type"] = r.Type, ["editorid"] = r.Edid, ["touchers"] = r.Touchers,
            ["nodes"] = r.Touchers.Select(t => t == r.Touchers[^1]
                ? new Dictionary<string, object?> { ["plugin"] = t, ["is_winner"] = true }
                : new Dictionary<string, object?> { ["plugin"] = t, ["is_winner"] = false, ["complete"] = r.Incomplete?.Contains(t) != true,
                                                    ["deltas"] = r.VsWinner.GetValueOrDefault(t) ?? Array.Empty<string>() }).ToList(),
        };

        object Delta(Rec r, string source, string versus)
        {
            if (versus == "previous_provider")
            {
                int i = Array.IndexOf(r.Touchers, source);
                if (i == 0) return new Dictionary<string, object?> { ["formid"] = r.FormId, ["error"] = $"versus: no previous provider — '{source}' DEFINES {r.FormId} (bottom of the touching list); there is nothing beneath it to compare against." };
                versus = r.Touchers[i - 1];
            }
            return new Dictionary<string, object?> { ["formid"] = r.FormId, ["type"] = r.Type, ["complete"] = true, ["deltas"] = Auth.GetValueOrDefault((source, versus, r.FormId)) ?? Array.Empty<string>() };
        }
    }

    static OrderPlugin P(string name, string? folder, bool selected, params string[] masters) =>
        new(name, RecordClashRule.ModOf(folder is null ? Path.Combine(@"X:\game\Data", name) : Path.Combine(Mods, folder, name), Mods),
            RecordChecks.IsGameFile(name), selected, masters);

    /// <summary>The order every case uses; <paramref name="selected"/> names the selection.</summary>
    static List<OrderPlugin> Order(params string[] selected)
    {
        bool S(string n) => selected.Contains(n);
        return new()
        {
            P("Skyrim.esm", null, S("Skyrim.esm")), P("Update.esm", null, S("Update.esm"), "Skyrim.esm"),
            P("Base.esp", "Base Mod", S("Base.esp"), "Skyrim.esm"), P("Base - Extra.esp", "Base Mod", S("Base - Extra.esp"), "Skyrim.esm"),
            P("Patch.esp", "Patch Mod", S("Patch.esp"), "Skyrim.esm", "Base.esp"), P("Hub.esp", "Hub Mod", S("Hub.esp"), "Skyrim.esm", "Patch.esp"),
            P("Mid.esp", "Mid Mod", S("Mid.esp"), "Skyrim.esm"), P("Late.esp", "Late Mod", S("Late.esp"), "Skyrim.esm"),
            P("Other.esp", "Other Mod", S("Other.esp"), "Skyrim.esm"),
        };
    }

    const string R1 = "000801:Skyrim.esm";

    /// <summary>Base.esp moved a placed object of Skyrim.esm; <paramref name="winner"/> overrides it with another position.</summary>
    static Fx Moved(string winner, params string[] selected)
    {
        var fx = new Fx
        {
            Order = Order(selected),
            Recs = { new Rec(R1, "PlacedObject", new[] { "Skyrim.esm", "Base.esp", winner }, new() { ["Base.esp"] = new[] { $"Placement.Position=1,2,3 ({winner} 4,5,6)" } }) },
        };
        fx.Auth[("Base.esp", "Skyrim.esm", R1)] = new[] { "Placement.Position=1,2,3 (Skyrim.esm 0,0,0)" };
        return fx;
    }

    static CheckResult Run(Fx fx, bool cells = false, bool instrument = false, Selection? sel = null) =>
        LostEditCheck.Run(new RecordContext(fx.Call, fx.Order, instrument, sel), cells);

    /// <summary>The setup without the selected mods: LE3's order with Base.esp (the selection, mod Base Mod) not loaded.</summary>
    static Fx Without()
    {
        var fx = Moved("Other.esp");
        fx.Order = fx.Order.Where(p => p.Mod != "folder:Base Mod").ToList();
        fx.Recs[0] = fx.Recs[0] with { Touchers = new[] { "Skyrim.esm", "Other.esp" }, VsWinner = new() };
        return fx;
    }
    static readonly Selection BaseSelected = new(new[] { "Base.esp" }, new[] { "Base Mod" });

    static string Show(IReadOnlyDictionary<string, long>? d) => string.Join(", ", (d ?? new Dictionary<string, long>()).Select(kv => $"{kv.Key} {kv.Value}"));

    /// <summary>Exactly these rows (loser, winner, field) and these no-row reasons; and, when given, these not-judged reasons.</summary>
    static string? Expect(CheckResult r, (string A, string B, string Field)[] rows, (string Why, long N)[] aside, (string Why, long N)[]? unj = null)
    {
        var got = r.Rows.Select(c => (c.A, c.B, c.Alternative?[0].Label ?? "")).ToList();
        var sa = r.Coverage[0].SetAside ?? new Dictionary<string, long>();
        var un = r.Coverage[0].Unjudgeable ?? new Dictionary<string, long>();
        bool ok = got.SequenceEqual(rows) && sa.Count == aside.Length && aside.All(x => sa.GetValueOrDefault(x.Why) == x.N)
                  && (unj is null || (un.Count == unj.Length && unj.All(x => un.GetValueOrDefault(x.Why) == x.N)));
        return ok ? null : $"expected rows [{string.Join("; ", rows)}], no row: {string.Join(", ", aside.Select(x => $"{x.Why} {x.N}"))}"
                           + (unj is null ? "" : $", not judged: {string.Join(", ", unj.Select(x => $"{x.Why} {x.N}"))}")
                           + $"; got rows [{string.Join("; ", got)}], no row: {Show(sa)}, not judged: {Show(un)}";
    }

    static readonly (string, long)[] None = Array.Empty<(string, long)>();

    /// <summary>Mid.esp changed a Scale; Late.esp, made with Skyrim.esm only, restates Skyrim.esm's value over it; Other.esp wins.</summary>
    static Fx Restated(params string[] selected)
    {
        const string id = "000810:Skyrim.esm";
        var fx = new Fx
        {
            Order = Order(selected),
            Recs = { new Rec(id, "PlacedObject", new[] { "Skyrim.esm", "Mid.esp", "Late.esp", "Other.esp" },
                             new() { ["Mid.esp"] = new[] { "Scale=2 (Other.esp 3)" }, ["Late.esp"] = new[] { "Scale=1 (Other.esp 3)" } }) },
        };
        fx.Auth[("Mid.esp", "Skyrim.esm", id)] = new[] { "Scale=2 (Skyrim.esm 1)" };
        fx.Auth[("Late.esp", "Mid.esp", id)] = new[] { "Scale=1 (Mid.esp 2)" };
        return fx;
    }

    static Fx OneRecord(string id, string type, string[] vsWinner, string[] authored, bool selectedBase = true)
    {
        var fx = new Fx { Order = Order(selectedBase ? "Base.esp" : "Mid.esp"), Recs = { new Rec(id, type, new[] { "Skyrim.esm", "Base.esp", "Other.esp" }, new() { ["Base.esp"] = vsWinner }) } };
        fx.Auth[("Base.esp", "Skyrim.esm", id)] = authored;
        return fx;
    }

    /// <summary>A row's full values (CheckResult.Detail), read as the rows.json file holds them.</summary>
    static JsonElement DetailOf(CheckResult r, int i) => JsonSerializer.SerializeToElement(r.Detail![i]);
    static string? Value(CheckResult r, int i, string k) => DetailOf(r, i).GetProperty(k) is { ValueKind: JsonValueKind.String } v ? v.GetString() : null;
    static IReadOnlyList<string> PathsOf(CheckResult r, int i) => DetailOf(r, i).GetProperty("paths").EnumerateArray().Select(x => x.GetString() ?? "").ToList();

    static bool OneSentence(string s) => s.EndsWith('.') && System.Text.RegularExpressions.Regex.Matches(s, @"[.!?](\s|$)").Count == 1;

    public static readonly (string Name, Func<string?> Check)[] Cases =
    {
        ("LE1 a winner made with the plugin whose change it overrides gives 0 rows, counted as made with it", () =>
            Expect(Run(Moved("Patch.esp", "Base.esp")), [], [(RecordClashRule.MadeWith, 1), (RecordClashRule.NotSelected, 1)])),
        ("LE2 made with, through another master: Hub.esp masters Patch.esp, which masters Base.esp, so 0 rows, counted as made with it", () =>
            Expect(Run(Moved("Hub.esp", "Base.esp")), [], [(RecordClashRule.MadeWith, 1), (RecordClashRule.NotSelected, 1)])),
        ("LE3 a winner of another mod not made with the plugin is exactly 1 row, its field", () =>
            Expect(Run(Moved("Other.esp", "Base.esp")), [("Base.esp", "Other.esp", "Placement")], [(RecordClashRule.NotSelected, 1)])),
        ("LE4 R5: a plugin that restates what it was made with has not changed the field (0 rows; the other version outside the selection)", () =>
            Expect(Run(Restated("Late.esp")), [], [(RecordClashRule.NotSelected, 2)])),
        ("LE5 R5: the same fixture IS a row under the instrument's previous provider (the plugin beneath it)", () =>
            Expect(Run(Restated("Late.esp"), instrument: true), [("Late.esp", "Other.esp", "Scale")], [(RecordClashRule.NotSelected, 2)])),
        ("LE6 a record stored compressed is no change in game: only the Scale changed is a row", () =>
            Expect(Run(OneRecord(R1, "PlacedObject", new[] { "SkyrimMajorRecordFlags=Compressed (Other.esp 0)", "Scale=2 (Other.esp 1)" },
                                 new[] { "SkyrimMajorRecordFlags=Compressed (Skyrim.esm 0)", "Scale=2 (Skyrim.esm 1)" })), [("Base.esp", "Other.esp", "Scale")], [(RecordClashRule.NotSelected, 1)])),
        ("LE7 numbers at four decimals and -0 equal to 0: a Scale written 1.34 for 1.3399999 is no change, a rotation equal in game is not lost", () =>
        {
            var r = Run(OneRecord(R1, "PlacedObject",
                new[] { "Scale=1.34 (Other.esp 1.2)", "Placement.Position=5,0,0 (Other.esp 1,0,0)", "Placement.Rotation=0,1,2 (Other.esp -0,1,2.00001)" },
                new[] { "Scale=1.34 (Skyrim.esm 1.3399999)", "Placement.Position=5,0,0 (Skyrim.esm 1,0,0)", "Placement.Rotation=0,1,2 (Skyrim.esm 0,1,1)" }));
            return Expect(r, [("Base.esp", "Other.esp", "Placement")], [(RecordClashRule.NotSelected, 1)])
                   ?? (r.Rows[0].Necessary[0].PhysicalDetail == "its Placement field" && PathsOf(r, 0).SequenceEqual(new[] { "Placement.Position" })
                       ? null : "the row's paths are not exactly Placement.Position");
        }),
        ("LE8 a cell's grid flags outside the byte the game reads are no change: only the water height is a row", () =>
            Expect(Run(OneRecord("000D00:Skyrim.esm", "Cell", new[] { "Grid.Flags=256 (Other.esp 0)", "WaterHeight=10 (Other.esp 0)" },
                                 new[] { "Grid.Flags=256 (Skyrim.esm Quad1)", "WaterHeight=10 (Skyrim.esm 0)" }), cells: true),
                   [("Base.esp", "Other.esp", "WaterHeight")], [(RecordClashRule.NotSelected, 1)])),
        ("LE9 a list only reordered is no change where the game does not read its order, and not judged where it does", () =>
        {
            var fx = OneRecord("000802:Skyrim.esm", "Container", new[] { "Items: same 3 item(s), ORDER DIFFERS from Other.esp" }, new[] { "Items: same 3 item(s), ORDER DIFFERS from Skyrim.esm" });
            fx.Recs.Add(new Rec("000803:Skyrim.esm", "Npc", new[] { "Skyrim.esm", "Base.esp", "Other.esp" }, new() { ["Base.esp"] = new[] { "Packages: same 2 item(s), ORDER DIFFERS from Other.esp" } }));
            fx.Auth[("Base.esp", "Skyrim.esm", "000803:Skyrim.esm")] = new[] { "Packages: same 2 item(s), ORDER DIFFERS from Skyrim.esm" };
            return Expect(Run(fx), [], [(RecordClashRule.NotSelected, 2)], [(LostEditRule.OrderRead, 1)]);
        }),
        ("LE10 R6: a navmesh record is set aside, counted as the navmesh check's", () =>
            Expect(Run(OneRecord("000804:Skyrim.esm", "NavigationMesh", new[] { "Data.Vertices=1 (Other.esp 2)" }, new[] { "Data.Vertices=1 (Skyrim.esm 0)" })), [], [(LostEditRule.Navmesh, 2)])),
        ("LE11 the instrument keeps navmesh records, as the Python capture did", () =>
            Expect(Run(OneRecord("000804:Skyrim.esm", "NavigationMesh", new[] { "Data.Vertices=1 (Other.esp 2)" }, new[] { "Data.Vertices=1 (Skyrim.esm 0)" }), instrument: true),
                   [("Base.esp", "Other.esp", "Data")], [(RecordClashRule.NotSelected, 1)])),
        ("LE12 a form list is set aside for the registration and dialogue check (unit 4)", () =>
            Expect(Run(OneRecord("000805:Skyrim.esm", "FormList", new[] { "Items: 3 vs Other.esp 4 item(s)" }, new[] { "Items: 3 vs Skyrim.esm 2 item(s)" })), [], [(LostEditRule.Registration, 2)])),
        ("LE13 a pair with neither plugin in the selection is 0 rows, counted as outside the selection", () =>
        {
            // Mid.esp, selected, has the record too, so the record is read; Base.esp's change lost to Other.esp involves neither
            var fx = Moved("Other.esp", "Mid.esp");
            fx.Recs[0] = fx.Recs[0] with { Touchers = new[] { "Skyrim.esm", "Base.esp", "Mid.esp", "Other.esp" } };
            return Expect(Run(fx), [], [(RecordClashRule.NotSelected, 2)]);
        }),
        ("LE14 C5, the other direction: another mod's change lost to a selected plugin is 1 row", () =>
            Expect(Run(Moved("Other.esp", "Other.esp")), [("Base.esp", "Other.esp", "Placement")], [(RecordClashRule.GameFileSide, 1)])),
        ("LE15 a delta line SkyWright cannot read is counted as not judged, and the rest of the version is still judged", () =>
        {
            var fx = Moved("Other.esp", "Base.esp");
            fx.Auth[("Base.esp", "Skyrim.esm", R1)] = new[] { "Placement.Position=1,2,3 (Skyrim.esm 0,0,0)", "Wiggle~ a line of no known shape" };
            return Expect(Run(fx), [("Base.esp", "Other.esp", "Placement")], [(RecordClashRule.NotSelected, 1)], [(LostEditRule.Unreadable, 1)]);
        }),
        ("LE16 a read houseCARL marks incomplete is not judged, never a clean result", () =>
        {
            var fx = Moved("Other.esp", "Base.esp");
            fx.Recs[0] = fx.Recs[0] with { Incomplete = new() { "Base.esp" } };
            return Expect(Run(fx), [], [(RecordClashRule.NotSelected, 1)], [(LostEditRule.Incomplete, 1)]);
        }),
        ("LE17 two plugins of one mod are 0 rows, counted as one mod's own business", () =>
            Expect(Run(Moved("Base - Extra.esp", "Base.esp")), [], [(RecordClashRule.SameModEdit, 1), (RecordClashRule.NotSelected, 1)])),
        ("LE18 a game file's version lost to a mod is 0 rows, counted as the game's own file", () =>
        {
            var fx = new Fx { Order = Order("Other.esp"), Recs = { new Rec(R1, "PlacedObject", new[] { "Skyrim.esm", "Update.esm", "Other.esp" }, new() { ["Update.esm"] = new[] { "Scale=2 (Other.esp 1)" } }) } };
            fx.Auth[("Update.esm", "Skyrim.esm", R1)] = new[] { "Scale=2 (Skyrim.esm 1)" };
            return Expect(Run(fx), [], [(RecordClashRule.GameFileSide, 2)]);
        }),
        ("LE19 a plugin that defines the record authored every field it holds: its value lost is a row with nothing it was made with", () =>
        {
            var fx = new Fx { Order = Order("Base.esp"), Recs = { new Rec("000900:Base.esp", "PlacedObject", new[] { "Base.esp", "Other.esp" }, new() { ["Base.esp"] = new[] { "Scale=2 (Other.esp 1)" } }) } };
            var r = Run(fx);
            return Expect(r, [("Base.esp", "Other.esp", "Scale")], None)
                   ?? (Value(r, 0, "madeWithValue") is null && Value(r, 0, "lost") == "2" && Value(r, 0, "winning") == "1" ? null : "the row's values are not lost 2, winning 1, made with nothing");
        }),
        ("LE20 a field the plugin removed from what it was made with, restored by the winner, is a row", () =>
            Expect(Run(OneRecord(R1, "PlacedObject", new[] { "LocationReference: ABSENT here (Other.esp has 00085A:Skyrim.esm)" },
                                 new[] { "LocationReference: ABSENT here (Skyrim.esm has 00085A:Skyrim.esm)" })), [("Base.esp", "Other.esp", "LocationReference")], [(RecordClashRule.NotSelected, 1)])),
        ("LE21 a plugin whose masters' header could not be read is not judged", () =>
        {
            var fx = Moved("Other.esp", "Base.esp");
            fx.Order = fx.Order.Select(p => p.Name == "Base.esp" ? p with { Masters = null } : p).ToList();
            return Expect(Run(fx), [], [(RecordClashRule.NotSelected, 1)], [(LostEditRule.MastersUnread, 1)]);
        }),
        ("LE22 the row says it in a modder's words: the plugins and their mods, what wins and that the winner was not made with the loser, one sentence", () =>
        {
            var fx = Moved("Other.esp", "Base.esp");
            var carried = Run(fx);
            fx.Auth[("Other.esp", "Skyrim.esm", R1)] = new[] { "Placement.Position=4,5,6 (Skyrim.esm 0,0,0)" };
            var own = Run(fx);
            if (carried.Rows.Count != 1 || own.Rows.Count != 1) return $"{carried.Rows.Count} and {own.Rows.Count} rows";
            string[] banned = { "provider", "delta", "FormKey", "override depth", "versus", "toucher", "loser", "selection", "instrument" };
            foreach (var s in new[] { carried.Rows[0].Summary, own.Rows[0].Summary })
            {
                if (banned.FirstOrDefault(b => s.Contains(b, StringComparison.OrdinalIgnoreCase)) is { } bad) return $"'{bad}' in: {s}";
                if (!s.Contains("Base.esp (mod Base Mod)") || !s.Contains("Other.esp (mod Other Mod) wins") || !s.Contains("not made with Base.esp")) return "the plugins, their mods, the winner or 'not made with' are not named: " + s;
                if (!OneSentence(s)) return "not one sentence: " + s;
            }
            if (!carried.Rows[0].Summary.Contains("the value it was made with")) return "a winner carrying an older value is not said: " + carried.Rows[0].Summary;
            return own.Rows[0].Summary.Contains("a change of its own") ? null : "a winner's own change is not said: " + own.Rows[0].Summary;
        }),
        ("LE23 what the row names: the record, and one row per field and losing plugin, each its own identity", () =>
        {
            var fx = Moved("Other.esp", "Base.esp", "Mid.esp");
            fx.Recs[0] = fx.Recs[0] with { Touchers = new[] { "Skyrim.esm", "Base.esp", "Mid.esp", "Other.esp" },
                VsWinner = new() { ["Base.esp"] = new[] { "Placement.Position=1,2,3 (Other.esp 4,5,6)", "Scale=2 (Other.esp 1)" }, ["Mid.esp"] = new[] { "Placement.Position=7,8,9 (Other.esp 4,5,6)" } } };
            fx.Auth[("Base.esp", "Skyrim.esm", R1)] = new[] { "Placement.Position=1,2,3 (Skyrim.esm 0,0,0)", "Scale=2 (Skyrim.esm 1)" };
            fx.Auth[("Mid.esp", "Skyrim.esm", R1)] = new[] { "Placement.Position=7,8,9 (Skyrim.esm 0,0,0)" };
            var r = Run(fx);
            if (r.Rows.Count != 3) return $"{r.Rows.Count} rows, not 3";
            if (r.Rows.Any(c => c.Necessary.Count != 1 || c.Necessary[0].FormKey != R1)) return "a row does not name exactly the record " + R1;
            var ids = r.Rows.Select(c => ConflictIo.RowIdentity(c.Kind, c.Necessary[0].FormKey, c.Alternative?[0].FormKey, c.A)).ToList();
            return ids.Distinct().Count() == 3 ? null : "two rows share an identity: " + string.Join(" | ", ids);
        }),
        ("LE24 a rotation written a full turn apart is no change in game (the scope's anglesRadians); the instrument, as the Python capture, compares it as numbers", () =>
        {
            Fx Turned() => OneRecord(R1, "PlacedObject", new[] { "Placement.Rotation=0,5.2217526,0 (Other.esp 0,1,0)", "Placement.Position=5,0,0 (Other.esp 1,0,0)" },
                new[] { "Placement.Rotation=0,5.2217526,0 (Skyrim.esm 0,-1.0614327,0)", "Placement.Position=5,0,0 (Skyrim.esm 1,0,0)" });
            var shipped = Run(Turned());
            var instrument = Run(Turned(), instrument: true);
            IReadOnlyList<string> Paths(CheckResult r) => r.Detail is { Count: 1 } ? PathsOf(r, 0) : Array.Empty<string>();
            return Expect(shipped, [("Base.esp", "Other.esp", "Placement")], [(RecordClashRule.NotSelected, 1)])
                   ?? (Paths(shipped).SequenceEqual(new[] { "Placement.Position" }) && Paths(instrument).SequenceEqual(new[] { "Placement.Rotation", "Placement.Position" })
                       ? null : $"paths {string.Join(",", Paths(shipped))} and under the instrument {string.Join(",", Paths(instrument))}, not Placement.Position and Placement.Rotation,Placement.Position");
        }),
        ("LE26 review U2-F2: two positions 0.157 units apart (the lab's row 119) are one position in game: no row; the instrument, as the Python capture, keeps it", () =>
        {
            Fx Nudged() => OneRecord(R1, "PlacedObject", new[] { "Placement.Position=-80589.77,5339.966,-4773.748 (Other.esp -80589.77,5339.966,-4773.591)" },
                new[] { "Placement.Position=-80589.77,5339.966,-4773.748 (Skyrim.esm -80589.77,5339.966,-4765.8276)" });
            return Expect(Run(Nudged()), [], [(RecordClashRule.NotSelected, 1)])
                   ?? Expect(Run(Nudged(), instrument: true), [("Base.esp", "Other.esp", "Placement")], [(RecordClashRule.NotSelected, 1)]);
        }),
        ("LE27 review U2-F2: a position lost by 0.72 units (the lab's row 62, the nearest real one) is still a row", () =>
            Expect(Run(OneRecord(R1, "PlacedObject", new[] { "Placement.Position=23873.617,-45234.43,-28.741133 (Other.esp 23873.617,-45234.43,-29.464401)" },
                new[] { "Placement.Position=23873.617,-45234.43,-28.741133 (Skyrim.esm 23873.617,-45234.43,-29.464401)" })), [("Base.esp", "Other.esp", "Placement")], [(RecordClashRule.NotSelected, 1)])),
        ("LE28 review U2-F2: a cell's occlusion data and height map (the lab's rows 420 and 421) are derived data, counted and never a row; the instrument keeps them", () =>
        {
            Fx Cell() => OneRecord("000D00:Skyrim.esm", "Cell",
                new[] { "OcclusionData=0300AB (Other.esp 0300CD)", "MaxHeightData.HeightMap: 1024 vs Other.esp 1024 item(s)", "WaterHeight=10 (Other.esp 0)" },
                new[] { "OcclusionData=0300AB (Skyrim.esm 0300CD)", "MaxHeightData.HeightMap: 1024 vs Skyrim.esm 1024 item(s)", "WaterHeight=10 (Skyrim.esm 0)" });
            return Expect(Run(Cell(), cells: true), [("Base.esp", "Other.esp", "WaterHeight")], [(RecordClashRule.NotSelected, 1), (LostEditRule.Derived, 2)])
                   ?? Expect(Run(Cell(), cells: true, instrument: true),
                       [("Base.esp", "Other.esp", "OcclusionData"), ("Base.esp", "Other.esp", "MaxHeightData"), ("Base.esp", "Other.esp", "WaterHeight")], [(RecordClashRule.NotSelected, 1)]);
        }),
        ("LE25 review U2-F1: the setup without the selected mods gives 0 rows and says what it looked at, the selection's plugin and mod folder, neither loaded", () =>
        {
            var r = Run(Without(), sel: BaseSelected);
            if (r.Rows.Count != 0) return $"{r.Rows.Count} rows";
            var looked = r.Coverage.FirstOrDefault(c => c.Population == RecordChecks.SelectionLooked);
            if (looked is null) return $"no '{RecordChecks.SelectionLooked}' line; coverage: {string.Join(" | ", r.Coverage.Select(c => $"{c.Population} judged {c.Judged}"))}";
            var sa = looked.SetAside ?? new Dictionary<string, long>();
            return looked.Judged == 2 && sa.Count == 2 && sa.GetValueOrDefault(RecordChecks.PluginNotLoaded) == 1 && sa.GetValueOrDefault(RecordChecks.ModNoPlugin) == 1
                ? null : $"judged {looked.Judged}, no row: {Show(sa)}; not judged 2 with the plugin 1 and the mod folder 1";
        }),
    };

    /// <summary>LE3's emit, for runcontrol's self-test: one row between two mods.</summary>
    public static CheckResult CrossModEmit() => Run(Moved("Other.esp", "Base.esp"));

    /// <summary>LE25's emit, for runcontrol's self-test: the setup without the selected mods.</summary>
    public static CheckResult WithoutSelectionEmit() => Run(Without(), sel: BaseSelected);
}
