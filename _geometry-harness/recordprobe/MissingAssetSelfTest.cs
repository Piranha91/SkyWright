using System.Text.Json;
using HarnessShared;
using HousecarlCore;
using SkyWright.Engine;

/// <summary>recordprobe --self-test: the missing-assets check's known answers (MA1-MA8), each one assertion, over houseCARL
/// answers in the shapes its record summary and housecarl_asset_status give (lab3's transcripts, record checks Gate A),
/// written here. A fixture is a small file system of mod folders, each with loose files and archives, and the game's Data
/// folder with its archives; the fixture answers "does the game find this file" by MO2's rules as the fixture states them
/// (a loose file of a higher-priority mod first, then the archives the plugins load, each name's copy from the
/// highest-priority mod that has one), for the whole order or for only some of its mods.</summary>
static class MissingAssetSelfTest
{
    const string Mods = @"X:\inst\mods";

    sealed class Layer
    {
        public HashSet<string> Loose = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, HashSet<string>> Archives = new(StringComparer.OrdinalIgnoreCase);
    }

    sealed record Rec(string FormId, string Type, string Winner, string[] Touchers, string[] Files, string? Edid = null);

    sealed class Fx
    {
        public List<OrderPlugin> Order = new();
        public List<string> ModPriority = new();   // highest first
        public Dictionary<string, Layer> Layers = new(StringComparer.OrdinalIgnoreCase) { ["Data"] = new Layer() };
        public List<Rec> Recs = new();
        public bool ReadIncomplete;
        public Selection? Sel;

        Layer L(string m) => Layers.TryGetValue(m, out var l) ? l : Layers[m] = new Layer();
        public Fx Loose(string mod, params string[] files) { L(mod).Loose.UnionWith(files); return this; }
        public Fx Archive(string mod, string archive, params string[] files) { L(mod).Archives[archive] = new(files, StringComparer.OrdinalIgnoreCase); return this; }

        /// <summary>One file as the game finds it with these mods enabled: (exists, winner name, in an archive).</summary>
        (bool, string?, bool) Find(string path, IReadOnlyList<string> mods)
        {
            foreach (var m in mods.Append("Data"))
                if (Layers.TryGetValue(m, out var l) && l.Loose.Contains(path)) return (true, m, false);
            // an archive's file itself is a loose file at the root of its layer
            foreach (var m in mods.Append("Data"))
                if (Layers.TryGetValue(m, out var l) && l.Archives.ContainsKey(path)) return (true, m, false);
            // the archives that load: the base archives and each loaded plugin's, the copy of each name from the highest layer;
            // the later-loaded plugin's archive wins among archives
            var loads = new List<string> { "Skyrim - Meshes0.bsa" };
            loads.AddRange(Order.Select(p => Path.GetFileNameWithoutExtension(p.Name) + ".bsa"));
            foreach (var a in Enumerable.Reverse(loads))
            {
                var copy = mods.Append("Data").FirstOrDefault(m => Layers.TryGetValue(m, out var l) && l.Archives.ContainsKey(a));
                if (copy is not null && Layers[copy].Archives[a].Contains(path)) return (true, a, true);
            }
            return (false, null, false);
        }

        public string Status(IReadOnlyDictionary<string, object?> args, IReadOnlyList<string> mods)
        {
            var paths = (string[])args["asset_paths"]!;
            return JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["profile"] = "fixture", ["read_incomplete"] = ReadIncomplete, ["bsa_failures"] = ReadIncomplete ? new[] { "Base.bsa: could not be read" } : Array.Empty<string>(),
                ["warnings"] = Array.Empty<string>(),
                ["results"] = paths.Select(p =>
                {
                    var (ok, win, bsa) = Find(p, mods);
                    var r = new Dictionary<string, object?> { ["path"] = p, ["error"] = null, ["exists"] = ok,
                        ["winner"] = win is null ? null : new Dictionary<string, object?> { ["name"] = win, ["kind"] = bsa ? "BSA" : "loose" }, ["providers"] = Array.Empty<object>(), ["ambiguous"] = false };
                    if (!ok) { r["absent_may_be_incomplete_read_failure"] = ReadIncomplete; r["absent_may_be_incomplete_undiscovered_archives"] = false; }
                    return r;
                }).ToList(),
                ["truncated"] = false,
            });
        }

        public string Call(string tool, IReadOnlyDictionary<string, object?> args, IReadOnlyList<string> mods)
        {
            if (tool == ToolNames.AssetStatus) return Status(args, mods);
            if (tool != ToolNames.Records) throw new InvalidOperationException($"the fixture answers {ToolNames.Records} and {ToolNames.AssetStatus} only, not {tool}");
            var names = ((IEnumerable<string>)((Dictionary<string, object?>)args["plugins"]!)["names"]!).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var m = Recs.Where(r => r.Touchers.Any(names.Contains)).Select(r => new Dictionary<string, object?>
                { ["formid"] = r.FormId, ["type"] = r.Type, ["editorid"] = r.Edid, ["winner"] = r.Winner, ["override_depth"] = r.Touchers.Length }).ToList();
            return JsonSerializer.Serialize(new Dictionary<string, object?> { ["form"] = "summary", ["total"] = m.Count, ["truncated"] = false, ["matches"] = m });
        }

        public RecordContext Context() => new((t, a) => Call(t, a, ModPriority), Order, false, Sel)
        {
            AssetLinks = (formid, plugin) => Recs.FirstOrDefault(r => r.FormId == formid && string.Equals(r.Winner, plugin, StringComparison.OrdinalIgnoreCase))?.Files,
            OwnView = mods => (t, a) => Call(t, a, mods),
            EnabledMods = ModPriority,
        };
    }

    static OrderPlugin P(string name, string? folder, bool selected, params string[] masters) =>
        new(name, RecordClashRule.ModOf(folder is null ? Path.Combine(@"X:\game\Data", name) : Path.Combine(Mods, folder, name), Mods),
            RecordChecks.IsGameFile(name), selected, masters);

    /// <summary>The order: Skyrim.esm, Update.esm, Mid.esp (Mid Mod), Base.esp (Base Mod), Patch.esp (Patch Mod, made with
    /// Base.esp), Other.esp (Other Mod); mods by priority, highest first: Other Mod, Patch Mod, Base Mod, Mid Mod.</summary>
    static Fx New(params string[] selected)
    {
        bool S(string n) => selected.Contains(n);
        var fx = new Fx
        {
            Order = new()
            {
                P("Skyrim.esm", null, S("Skyrim.esm")), P("Update.esm", null, S("Update.esm"), "Skyrim.esm"),
                P("Mid.esp", "Mid Mod", S("Mid.esp"), "Skyrim.esm"), P("Base.esp", "Base Mod", S("Base.esp"), "Skyrim.esm"),
                P("Patch.esp", "Patch Mod", S("Patch.esp"), "Skyrim.esm", "Base.esp"), P("Other.esp", "Other Mod", S("Other.esp"), "Skyrim.esm"),
            },
            ModPriority = new() { "Other Mod", "Patch Mod", "Base Mod", "Mid Mod" },
        };
        fx.Archive("Data", "Skyrim - Meshes0.bsa", @"meshes\vanilla.nif");
        return fx;
    }

    const string File1 = @"meshes\base\bread.nif", File2 = @"meshes\base\paddle.nif";

    /// <summary>MA3's case: Base.esp's static names a mesh its mod ships in Base.bsa; Other Mod ships an archive named Base.bsa
    /// too, without it, and Other Mod has the higher priority, so its copy is the one the game loads.</summary>
    static Fx Hidden(params string[] selected)
    {
        var fx = New(selected);
        fx.Archive("Base Mod", "Base.bsa", File1).Archive("Other Mod", "Base.bsa", @"meshes\other\thing.nif");
        fx.Recs.Add(new Rec("000D62:Base.esp", "Static", "Base.esp", new[] { "Base.esp" }, new[] { File1 }, "BaseBread"));
        return fx;
    }

    static string Show(IReadOnlyDictionary<string, long>? d) => string.Join(", ", (d ?? new Dictionary<string, long>()).Select(kv => $"{kv.Key} {kv.Value}"));

    /// <summary>Exactly these rows (plugin, mod whose archive wins) and these no-row reasons; and, when given, these not-judged ones.</summary>
    static string? Expect(CheckResult r, (string A, string B)[] rows, (string Why, long N)[] aside, (string Why, long N)[]? unj = null)
    {
        var got = r.Rows.Select(c => (c.A, c.B)).ToList();
        var sa = r.Coverage[0].SetAside ?? new Dictionary<string, long>();
        var un = r.Coverage[0].Unjudgeable ?? new Dictionary<string, long>();
        bool ok = got.SequenceEqual(rows) && sa.Count == aside.Length && aside.All(x => sa.GetValueOrDefault(x.Why) == x.N)
                  && (unj is null ? un.Count == 0 : un.Count == unj.Length && unj.All(x => un.GetValueOrDefault(x.Why) == x.N));
        return ok ? null : $"expected rows [{string.Join("; ", rows)}], no row: {string.Join(", ", aside.Select(x => $"{x.Why} {x.N}"))}"
                           + $", not judged: {(unj is null ? "nothing" : string.Join(", ", unj.Select(x => $"{x.Why} {x.N}")))}"
                           + $"; got rows [{string.Join("; ", got)}], no row: {Show(sa)}, not judged: {Show(un)}";
    }

    static CheckResult Run(Fx fx) => MissingAssetCheck.Run(fx.Context());

    static bool OneSentence(string s) => s.EndsWith('.') && System.Text.RegularExpressions.Regex.Matches(s, @"[.!?](\s|$)").Count == 1;

    public static readonly (string Name, Func<string?> Check)[] Cases =
    {
        ("MA1 one mod's own missing file (as each of the lab's 11): 0 rows, counted as its own defect; a file the game finds is counted too", () =>
        {
            var fx = New("Base.esp").Loose("Base Mod", File2);
            fx.Recs.Add(new Rec("000D62:Base.esp", "Static", "Base.esp", new[] { "Base.esp" }, new[] { File1, File2 }));
            return Expect(Run(fx), [], [(RecordClashRule.AssetOwnDefect, 1), (MissingAssetCheck.Present, 1)]);
        }),
        ("MA2 a file missing from a game file's record: 0 rows, counted as the game's version", () =>
        {
            var fx = New("Update.esm");
            fx.Recs.Add(new Rec("000801:Skyrim.esm", "Static", "Update.esm", new[] { "Skyrim.esm", "Update.esm" }, new[] { @"meshes\gone.nif" }));
            return Expect(Run(fx), [], [(RecordClashRule.AssetNamedByGameFile, 1)]);
        }),
        ("MA3 made up between two mods: another mod's archive of the same name wins and lacks the file, exactly 1 row", () =>
            Expect(Run(Hidden("Base.esp")), [("Base.esp", "Other Mod")], [])),
        ("MA4 the same case with none of the three in the selection (a selected plugin touches the record, Base.esp wins it): 0 rows, counted as outside the selection", () =>
        {
            var fx = Hidden("Mid.esp");
            fx.Recs[0] = fx.Recs[0] with { FormId = "000D62:Skyrim.esm", Touchers = new[] { "Skyrim.esm", "Mid.esp", "Base.esp" } };
            return Expect(Run(fx), [], [(RecordClashRule.NotSelected, 1)]);
        }),
        ("MA5 a read houseCARL marks incomplete is not judged, never a clean result or a row", () =>
        {
            var fx = Hidden("Base.esp");
            fx.ReadIncomplete = true;
            return Expect(Run(fx), [], [], [(MissingAssetCheck.Incomplete, 1)]);
        }),
        ("MA6 the row says it in a modder's words: the plugin and its mod, the file, whose archive wins, one sentence, no internal terms", () =>
        {
            var r = Run(Hidden("Base.esp"));
            if (r.Rows.Count != 1) return $"{r.Rows.Count} rows";
            var s = r.Rows[0].Summary;
            string[] banned = { "provider", "FormKey", "formid", "selection", "layer", "BSA", "needer", "shipper", "hider", "asset" };
            if (banned.FirstOrDefault(b => s.Contains(b, StringComparison.Ordinal)) is { } bad) return $"'{bad}' in: {s}";
            foreach (var want in new[] { "Base.esp (mod Base Mod)", File1, "Base.bsa", "mod Other Mod's archive", "wins", "BaseBread" })
                if (!s.Contains(want, StringComparison.Ordinal)) return $"'{want}' not in: {s}";
            return OneSentence(s) ? null : "not one sentence: " + s;
        }),
        ("MA7 the record's own mod ships the archive that replaces the one with the file: 0 rows, counted as its own defect (its own mods do not serve it)", () =>
        {
            // Patch.esp (made with Base.esp) uses a file of Base Mod's Base.bsa, and Patch Mod ships an archive named Base.bsa without it
            var fx = New("Patch.esp");
            fx.Archive("Base Mod", "Base.bsa", File1).Archive("Patch Mod", "Base.bsa");
            fx.Recs.Add(new Rec("000D63:Patch.esp", "Static", "Patch.esp", new[] { "Patch.esp" }, new[] { File1 }));
            return Expect(Run(fx), [], [(RecordClashRule.AssetOwnDefect, 1)]);
        }),
        ("MA8 the setup without the selected mods gives 0 rows and says what it looked at", () =>
        {
            var fx = Hidden();
            fx.Order = fx.Order.Where(p => p.Mod != "folder:Base Mod").ToList();
            fx.Sel = new Selection(new[] { "Base.esp" }, new[] { "Base Mod" });
            var r = Run(fx);
            var looked = r.Coverage.FirstOrDefault(c => c.Population == RecordChecks.SelectionLooked);
            return r.Rows.Count == 0 && looked is { Judged: 2 } ? null : $"{r.Rows.Count} rows; looked at: {looked?.Judged.ToString() ?? "no line"}";
        }),
    };

    /// <summary>MA3's emit, for runcontrol's self-test: one row between two mods.</summary>
    public static CheckResult CrossModEmit() => Run(Hidden("Base.esp"));
}
