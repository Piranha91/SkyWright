using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Workbench;
using Xunit;

namespace WorkbenchTests;

/// <summary>A hand-built evaluation: a results folder, the run that made the predictions and the patched run, one pair.
/// Findings: 0 and 1 share one id (two losing plugins), 2 a CELL record, 3 a reference in a persistent cell, 4 a contact
/// row, 5 a navmesh row, 6 a NAVM record. Edits: 0 category 1 (joins finding 0's id), 1 BOUNDS, 2 a DISABLE in a
/// persistent cell, 3 a NAVI field, 4 an ADD, 5 a LOCATION-REF. Overlaps: two admitted, one of them not needs-both.</summary>
public sealed class ResultsFixture : IDisposable
{
    public const string Pair = "pair-a", SubA = "ModA.esp", SubA2 = "ModA2.esp", Partner = "ModB.esp", Patch = "Patch.esp", Master = "Base.esm";
    public const string Cell = "000A01:Base.esm", PersistentCell = "000B00:Base.esm", NavCell = "000A02:Base.esm";
    public const string DupId = "pair-a-rec-records.contested-fields-000101:Base.esm-Placement";

    public string Root { get; } = Path.Combine(Path.GetTempPath(), "hc-wb-results-" + Guid.NewGuid().ToString("N"));
    public string Results => Path.Combine(Root, "results");
    public string Sealed => Path.Combine(Root, "sealed", "ho-" + Pair);
    public string Patched => Path.Combine(Root, "patched", "sc-" + Pair);
    public string Bundle => Path.Combine(Sealed, "bundle", "evidence");
    public string ResultFile => Path.Combine(Results, "scoring", "result", Pair + ".json");
    public string Names => Path.Combine(Bundle, "enrichment", "names", "names.json");
    public string RecordRows => Path.Combine(Bundle, "evidence", "imports", "records.contested-fields__baseline", "03-rows.json");
    public string Presentation => Path.Combine(Sealed, "verdict-plan", "presentation.json");
    public string EditList => Path.Combine(Patched, "precedent-nocards", "Patch", "edits.jsonl");

    static string Sha(string p) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))).ToLowerInvariant();
    static string Fwd(string p) => p.Replace('\\', '/');

    static void Write(string path, object json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, json as string ?? JsonSerializer.Serialize(json));
    }

    static object Lost(string a, string b, string type, string fk, string field, string lost, string winner, string? beneath) => new
    {
        Kind = "LOST-EDIT", A = a, B = b, Field = field, Summary = $"{type} {fk}: {field} - {a}'s value lost to {b}'s",
        Necessary = new[] { new { FormKey = fk, Label = (string?)null, Values = new { lost, winner, overridden = beneath }, Paths = new[] { field } } },
    };

    public static string EditLine(string fk, string kind, string type, object? before, object? patch, object? cell, object? extra = null)
    {
        var o = new JsonObject
        {
            ["id"] = $"stage|{fk}|{kind}", ["formKey"] = fk, ["recordType"] = type, ["kind"] = kind, ["editorId"] = null,
            ["values"] = new JsonObject
            {
                ["unpatchedWinner"] = new JsonObject { ["plugin"] = before is null ? null : Partner, ["values"] = before is null ? null : JsonSerializer.SerializeToNode(before) },
                ["patch"] = new JsonObject { ["plugin"] = Patch, ["values"] = JsonSerializer.SerializeToNode(patch ?? new { }) },
            },
            ["touched"] = new JsonObject
            {
                ["base"] = new JsonObject { ["formKey"] = "000900:Base.esm", ["type"] = "Static", ["editorId"] = "HcCrate" },
                ["cell"] = JsonSerializer.SerializeToNode(cell ?? new { formKey = (string?)null, editorId = (string?)null, name = (string?)null, grid = (string?)null, worldspace = (string?)null }),
            },
        };
        if (extra is not null) foreach (var kv in JsonSerializer.SerializeToNode(extra)!.AsObject().ToList()) { o.Remove(kv.Key); o[kv.Key] = kv.Value?.DeepClone(); }
        return o.ToJsonString();
    }

    public ResultsFixture()
    {
        // the run that made the predictions
        Write(Names, new
        {
            schema = "names-v1",
            places = new Dictionary<string, object> { ["HcNavHouse"] = new { name = "Nav House", formKey = NavCell, from = Master } },
            missing = new[] { new { formKey = Cell, editorId = "HcYard", type = "Cell" } },
            refs = new Dictionary<string, object>
            {
                ["000101:Base.esm"] = new { @base = "000900:Base.esm", carriedBy = new[] { Master, SubA, Partner }, cells = new Dictionary<string, string> { [Master] = Cell, [SubA] = Cell, [Partner] = Cell } },
                ["000102:Base.esm"] = new { @base = "000900:Base.esm", carriedBy = new[] { Master, Partner }, cells = new Dictionary<string, string> { [Master] = PersistentCell + "!persistent", [Partner] = PersistentCell + "!persistent" } },
                ["000103:ModA.esp"] = new { @base = "000900:Base.esm", carriedBy = new[] { SubA }, cells = new Dictionary<string, string> { [SubA] = Cell } },
                ["000104:Base.esm"] = new { @base = "000900:Base.esm", carriedBy = new[] { Master, Partner }, cells = new Dictionary<string, string> { [Master] = Cell, [Partner] = Cell } },
                ["000108:ModB.esp"] = new { @base = "000900:Base.esm", carriedBy = new[] { Partner }, cells = new Dictionary<string, string> { [Partner] = Cell } },
                ["000A01:Base.esm"] = new { @base = (string?)null, carriedBy = new[] { Master }, cells = new Dictionary<string, string>() },
                ["000301:Base.esm"] = new { @base = (string?)null, carriedBy = new[] { Master }, cells = new Dictionary<string, string>() },
            },
            bases = new Dictionary<string, object> { ["000900:Base.esm"] = new { type = "Static", editorId = "HcCrate" } },
        });
        Write(RecordRows, new
        {
            Conflicts = new[]
            {
                Lost(SubA, Partner, "PlacedObject", "000101:Base.esm", "Placement", "Position=1,1,1", "Position=3,3,3", "Position=0,0,0"),
                Lost(Master, SubA, "PlacedObject", "000101:Base.esm", "Placement", "not a pair row", "x", "y"),
                Lost(SubA2, Partner, "PlacedObject", "000101:Base.esm", "Placement", "Position=2,2,2", "Position=3,3,3", "Position=1,1,1"),
                Lost(SubA, Partner, "PlacedObject", "000102:Base.esm", "Scale", "1.5", "2", null),
                Lost(Partner, SubA, "NavigationMesh", "000301:Base.esm", "Data", "tris 10", "tris 12", "tris 8"),
            },
        });
        Write(Path.Combine(Bundle, "evidence", "imports", "records.cell-attributes__baseline", "03-rows.json"), new
        {
            Conflicts = new[] { Lost(SubA, Partner, "Cell", Cell, "WaterHeight", "10", "20", "0") },
        });
        var contact = Path.Combine(Bundle, "sweep", "inv-0001", "contactprobe", "baseline.json");
        Write(contact, new
        {
            Conflicts = new object[]
            {
                new { Kind = "CONTACT", A = Master, B = Partner, Summary = "a row of the rest of the load order", Necessary = new[] { new { FormKey = "000999:Base.esm" } } },
                new
                {
                    Kind = "CONTACT", A = SubA, B = Partner, Summary = "Orphaned: Static HcShelf  attached on Container HcCrate which ModB.esp moved",
                    Necessary = new[] { new { FormKey = "000103:ModA.esp", Role = "placed-ref", Label = "HcYard (int)", Physical = "Holds", PhysicalDetail = "touches nothing within 4u" } },
                    Alternative = new[] { new { FormKey = "000104:Base.esm", Role = "placed-ref" } },
                },
            },
        });
        var navown = Path.Combine(Bundle, "sweep", "inv-0001", "navownprobe", "baseline.json");
        Write(navown, new
        {
            Conflicts = new object[]
            {
                new
                {
                    Kind = "EDGE-IDX", A = SubA, B = Partner, Summary = "navmesh 000301:Base.esm edge-links 000302:Base.esm triangle 5 of 4",
                    Necessary = new[] { new { FormKey = "000301:Base.esm", Role = "navmesh", Label = "HcNavHouse (int)  3 tris -> 4", Physical = "Holds", PhysicalDetail = "the link's triangle is gone" } },
                    Alternative = new[] { new { FormKey = "000302:Base.esm", Role = "navmesh", Triangle = 5 } },
                },
            },
        });
        Write(Presentation, new
        {
            reader = new
            {
                prediction = new
                {
                    record = new object[]
                    {
                        new { checkId = "records.contested-fields", admission = "admitted", formKey = "000101:Base.esm" },
                        new { checkId = "geometry.object-overlap", kind = "OVERLAP", A = SubA, B = Partner, formKey = "000103:ModA.esp", other = new[] { "000108:ModB.esp" }, summary = "Static from ModA.esp / Static from ModB.esp: crossing", physical = "Holds", admission = "admitted", attribution = "needs-both", attributionMod = SubA },
                        new { checkId = "geometry.object-overlap", kind = "OVERLAP", A = SubA, B = Partner, formKey = "000104:Base.esm", other = new[] { "000108:ModB.esp" }, summary = "an overlap that was there already", admission = "alreadyPresent", attribution = "needs-both" },
                        new { checkId = "geometry.object-overlap", kind = "OVERLAP", A = SubA, B = Partner, formKey = "000102:Base.esm", other = new[] { "000108:ModB.esp" }, summary = "Static from Base.esm / Static from ModB.esp: crossing", physical = "Holds", admission = "admitted", attribution = "with-target", attributionMod = SubA },
                    },
                },
            },
        });

        // the patched run
        var before = Path.Combine(Patched, "bundle", "evidence", "candidates", "candidate-00-baseline.json");
        var after = Path.Combine(Patched, "bundle", "evidence", "candidates", "candidate-04.json");
        Write(before, new { SchemaVersion = 1, BaseProfile = "EP-sc-pair-a", Plugins = new[] { Master, SubA, SubA2, Partner }, Provenance = new { arm = "combined", role = "combined", keys = Array.Empty<string>() } });
        Write(after, new { SchemaVersion = 1, BaseProfile = "EP-sc-pair-a", Plugins = new[] { Master, SubA, SubA2, Partner, Patch }, Provenance = new { arm = "combined+key:Patch.esp", role = "patched", keys = new[] { Patch } } });
        Write(Path.Combine(Patched, "bundle", "evidence", "manifest.json"), new
        {
            evidence = new[] { new { path = "candidates/candidate-00-baseline.json", sha256 = Sha(before) }, new { path = "candidates/candidate-04.json", sha256 = Sha(after) } },
        });
        var yard = new { formKey = Cell, editorId = "HcYard", name = (string?)null, grid = (string?)null, worldspace = (string?)null };
        Write(EditList, string.Join("\n", new[]
        {
            EditLine("000101:Base.esm", "MOVE", "REFR", new Dictionary<string, string?> { ["Placement.Position"] = "3,3,3" }, new Dictionary<string, string?> { ["Placement.Position"] = "1,1,2" }, yard,
                new { move = new { dx = 2.0, dy = 2.0, dz = 1.0, distance = 3.0 } }),
            EditLine("000105:Base.esm", "FIELD:Primitive", "REFR", new Dictionary<string, string?> { ["Primitive.Bounds"] = "1,1,1" }, new Dictionary<string, string?> { ["Primitive.Bounds"] = "2,2,2" }, yard),
            EditLine("000106:Base.esm", "DISABLE", "REFR", new Dictionary<string, string?> { ["MajorRecordFlagsRaw"] = "0", ["IsDeleted"] = "False" }, new Dictionary<string, string?> { ["MajorRecordFlagsRaw"] = "2048", ["IsDeleted"] = "False" },
                new { formKey = PersistentCell, editorId = (string?)null, name = (string?)null, grid = (string?)null, worldspace = "HcWorld" }, new { disable = new { mechanism = new[] { "InitiallyDisabled" } } }),
            EditLine("000200:Base.esm", "FIELD:MapInfos", "NAVI", new { }, new { }, null, new { patchDiff = new { deltas = new[] { "MapInfos: 3 vs 4 item(s)" } } }),
            EditLine("000800:Patch.esp", "ADD", "REFR", null, new { }, yard),
            EditLine("000107:Base.esm", "LOCATION-REF", "REFR", new Dictionary<string, string?> { ["LocationReference"] = null }, new Dictionary<string, string?> { ["LocationReference"] = "000700:Base.esm" }, yard),
            EditLine("000109:Base.esm", "FIELD:Regions", "CELL", new { }, new { }, yard),
        }) + "\n");

        // the results folder
        Write(Path.Combine(Bundle, "manifest.json"), new
        {
            evidence = new[] { "enrichment/names/names.json", "evidence/imports/records.contested-fields__baseline/03-rows.json", "evidence/imports/records.cell-attributes__baseline/03-rows.json" }
                .Select(rel => new { path = rel, sha256 = Sha(Path.Combine(Bundle, rel.Replace('/', Path.DirectorySeparatorChar))) }).ToArray(),
        });
        object Finding(string id, string lane, string? field, bool acted, params (string Key, string Role)[] subjects) =>
            new { id, lane, subjects = subjects.ToDictionary(s => s.Key, s => s.Role), field, actedOn = acted };
        object Edit(string fk, string kind, string type, int category, string rule, string? cell, params string[] joined) =>
            new { formKey = fk, kind, recordType = type, category, rule, joined, cell };
        Write(ResultFile, new
        {
            pair = Pair, subject = new[] { SubA, SubA2 }, partner = new[] { Partner }, patch = new { plugin = Patch },
            governing = new { setup = "combined", armId = "baseline" },
            sealedBundleManifestSha256 = Sha(Path.Combine(Bundle, "manifest.json")),
            geometryMeta = new
            {
                contactprobe = new { source = new { path = Fwd(contact), sha256 = Sha(contact) } },
                navownprobe = new { source = new { path = Fwd(navown), sha256 = Sha(navown) } },
            },
            findings = new[]
            {
                Finding(DupId, "record", "Placement", true, ("000101:Base.esm", "record")),
                Finding(DupId, "record", "Placement", true, ("000101:Base.esm", "record")),
                Finding($"{Pair}-rec-records.cell-attributes-{Cell}-WaterHeight", "record", "WaterHeight", false, (Cell, "record")),
                Finding($"{Pair}-rec-records.contested-fields-000102:Base.esm-Scale", "record", "Scale", false, ("000102:Base.esm", "record")),
                Finding($"{Pair}-contactprobe-002", "contact", null, true, ("000103:ModA.esp", "dependent"), ("000104:Base.esm", "host")),
                Finding($"{Pair}-navownprobe-001", "navmesh", null, false, ("000301:Base.esm", "navmesh"), ("000302:Base.esm", "navmesh")),
                Finding($"{Pair}-rec-records.contested-fields-000301:Base.esm-Data", "record", "Data", false, ("000301:Base.esm", "record")),
            },
            edits = new[]
            {
                Edit("000101:Base.esm", "MOVE", "REFR", 1, "joins a needs-both finding", Cell, DupId),
                Edit("000105:Base.esm", "BOUNDS", "REFR", 3, "non-spatial, invented value", Cell),
                Edit("000106:Base.esm", "DISABLE", "REFR", 5, "spatial, co-located, unmeasured", PersistentCell),
                Edit("000200:Base.esm", "FIELD:MapInfos", "NAVI", 3, "non-spatial, invented value", null),
                Edit("000800:Patch.esp", "ADD", "REFR", 5, "spatial, co-located, unmeasured", Cell),
                Edit("000107:Base.esm", "LOCATION-REF", "REFR", 4, "forward", Cell),
            },
            notEdits = new[] { new { formKey = "000109:Base.esm", kind = "FIELD:Regions" } },
        });
        Write(Path.Combine(Results, "scoring", "result", "summary.json"), new Dictionary<string, object> { [Pair] = new { findings = 7 } });
        Write(Path.Combine(Results, "scoring", "score-driver.log"), $"2026-01-01 00:00:00 sc-{Pair}: identity unchanged\n2026-01-01 00:00:01 sc-{Pair}: -> {Patched}\n2026-01-01 00:00:02 all scoring runs done\n");
        Write(Path.Combine(Results, "scoring", "entries", $"sc-{Pair}.json"), new { id = "sc-" + Pair, source = new { instance = Fwd(Path.Combine(Root, "instance")), profile = "source" }, profile = "EP-sc-pair-a" });
        Write(Path.Combine(Results, "machine.json"), new { lab = new { game = Fwd(Path.Combine(Root, "game")) } });
        Write(Path.Combine(Results, "verdicts-pre-patch.json"), new
        {
            pairs = new[] { new { pair = "ho-" + Pair, presentationSha256 = Sha(Presentation), bundleManifestSha256 = Sha(Path.Combine(Bundle, "manifest.json")) } },
        });
        Write(Path.Combine(Results, "NOTES.md"), "a file the receipt lists and the reader does not read\n");
        Receipt();
    }

    /// <summary>Write the receipt over the results folder's files as they are now.</summary>
    public void Receipt()
    {
        var listed = new[] { "NOTES.md", "scoring/score-driver.log" }
            .Concat(Directory.EnumerateFiles(Path.Combine(Results, "scoring", "result"), "*.json").Select(f => "scoring/result/" + Path.GetFileName(f)))
            .Concat(Directory.EnumerateFiles(Path.Combine(Results, "scoring", "entries"), "*.json").Select(f => "scoring/entries/" + Path.GetFileName(f)));
        File.WriteAllText(Path.Combine(Results, "SCORING-RECEIPT.txt"), "scoring receipt\nwritten 2026-01-01\n"
            + string.Join("\n", listed.Select(rel => $"{Sha(Path.Combine(Results, rel.Replace('/', Path.DirectorySeparatorChar)))}  {rel}")) + "\n");
    }

    /// <summary>Change one byte of a file, keeping its length.</summary>
    public static void Flip(string path)
    {
        var b = File.ReadAllBytes(path);
        int i = Array.LastIndexOf(b, (byte)'}');
        b[i > 0 ? i - 1 : b.Length - 1] ^= 0x01;
        File.WriteAllBytes(path, b);
    }

    public void Dispose() { try { Directory.Delete(Root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
}

/// <summary>The evaluation results reader on a hand-built evaluation.</summary>
[Trait("tier", "unit")]
public sealed class ResultsTests
{
    static ResultRow Finding(IReadOnlyList<ResultRow> rows, int index) => rows.Single(r => r.Lane is not ("edit" or "overlap") && r.Index == index);
    static ResultRow EditOf(IReadOnlyList<ResultRow> rows, string kind) => rows.Single(r => r.Lane == "edit" && r.Kind == kind);

    [Fact]
    public void A_results_folder_or_its_receipt_is_recognised_and_another_folder_is_not()
    {
        using var fx = new ResultsFixture();
        Assert.True(EvaluationResults.LooksLike(fx.Results));
        Assert.True(EvaluationResults.LooksLike(Path.Combine(fx.Results, "SCORING-RECEIPT.txt")));
        Assert.False(EvaluationResults.LooksLike(fx.Bundle));
        Assert.False(EvaluationResults.LooksLike(Path.Combine(fx.Results, "machine.json")));
    }

    [Fact]
    public void Counts_come_from_the_result_file_and_the_overlap_count_from_the_overlap_list_once_rows_are_loaded()
    {
        using var fx = new ResultsFixture();
        var ev = EvaluationResults.Open(fx.Results);
        var pair = Assert.Single(ev.Pairs);
        Assert.Equal(new ResultCounts(3, 4, 5, 2, 1, 2, null), pair.Counts);
        Assert.Null(ev.Totals.NotScored);
        var rows = pair.LoadRows();
        Assert.Equal(new ResultCounts(3, 4, 5, 2, 1, 2, 2), ev.Totals);
        Assert.Equal(3, rows.Count(r => r.Group == ResultGroup.Addressed));
        Assert.Equal(4, rows.Count(r => r.Group == ResultGroup.NotAddressed));
        Assert.Equal(5, rows.Count(r => r.Group == ResultGroup.PatchChange));
        Assert.Equal(2, rows.Count(r => r.Group == ResultGroup.NotScored));
        Assert.Equal(rows.Count, rows.Select(r => r.Key).Distinct().Count());
        Assert.All(rows, r => Assert.False(string.IsNullOrWhiteSpace(r.Summary)));
    }

    [Fact]
    public void Every_file_the_receipt_lists_is_checked_and_the_receipt_file_opens_the_same_folder()
    {
        using var fx = new ResultsFixture();
        var ev = EvaluationResults.Open(Path.Combine(fx.Results, "SCORING-RECEIPT.txt"));
        Assert.Equal(Path.GetFullPath(fx.Results), ev.Folder);
        Assert.Equal(new[] { "NOTES.md", "scoring/entries/sc-pair-a.json", "scoring/result/pair-a.json", "scoring/result/summary.json", "scoring/score-driver.log" },
            ev.Checked.Select(c => c.File).OrderBy(x => x, StringComparer.Ordinal));
    }

    [Fact]
    public void A_changed_result_file_refuses_by_name()
    {
        using var fx = new ResultsFixture();
        ResultsFixture.Flip(fx.ResultFile);
        var x = Assert.Throws<InvalidOperationException>(() => EvaluationResults.Open(fx.Results));
        Assert.Contains("scoring/result/pair-a.json does not match", x.Message);
    }

    [Fact]
    public void A_result_file_the_receipt_does_not_list_refuses_by_name()
    {
        using var fx = new ResultsFixture();
        File.WriteAllText(Path.Combine(fx.Results, "scoring", "result", "pair-b.json"), "{}");
        var x = Assert.Throws<InvalidOperationException>(() => EvaluationResults.Open(fx.Results));
        Assert.Contains("scoring/result/pair-b.json is not listed", x.Message);
    }

    [Fact]
    public void A_listed_result_file_that_is_missing_refuses_by_name()
    {
        using var fx = new ResultsFixture();
        File.Delete(Path.Combine(fx.Results, "scoring", "result", "summary.json"));
        var x = Assert.Throws<InvalidOperationException>(() => EvaluationResults.Open(fx.Results));
        Assert.Contains("scoring/result/summary.json, which is missing", x.Message);
    }

    [Fact]
    public void A_changed_file_the_reader_does_not_read_is_a_note_and_not_a_refusal()
    {
        using var fx = new ResultsFixture();
        File.AppendAllText(Path.Combine(fx.Results, "NOTES.md"), "edited\n");
        var ev = EvaluationResults.Open(fx.Results);
        Assert.Contains(ev.Notes, n => n.Contains("NOTES.md does not match"));
        Assert.DoesNotContain(ev.Checked, c => c.File == "NOTES.md");
    }

    [Fact]
    public void A_finding_id_that_occurs_twice_shows_the_two_source_rows_with_their_own_losing_plugin()
    {
        using var fx = new ResultsFixture();
        var rows = EvaluationResults.Open(fx.Results).Pairs[0].LoadRows();
        ResultRow first = Finding(rows, 0), second = Finding(rows, 1);
        Assert.Equal(new[] { "losing mod's value", "winning mod's value", "value beneath both" }, first.Values.Select(v => v.Label));
        Assert.Equal((ResultsFixture.SubA, "Position=1,1,1"), (first.Values[0].Plugin, first.Values[0].Text));
        Assert.Equal((ResultsFixture.SubA2, "Position=2,2,2"), (second.Values[0].Plugin, second.Values[0].Text));
        Assert.Equal(ResultsFixture.Partner, second.Values[1].Plugin);
        Assert.Equal("REFR", first.RecordType);
        Assert.Equal("Placement", first.Field);
        Assert.Equal((ResultsFixture.Cell, "000101:Base.esm"), (first.Place.Cell, first.Place.RefKey));
    }

    [Fact]
    public void A_value_that_is_not_there_reads_none()
    {
        using var fx = new ResultsFixture();
        var rows = EvaluationResults.Open(fx.Results).Pairs[0].LoadRows();
        Assert.Equal("(none)", Finding(rows, 3).Values[2].Text);
        Assert.Equal("(none)", EditOf(rows, "LOCATION-REF").Values.Single(v => v.Label == "before the patch").Text);
    }

    [Fact]
    public void A_BOUNDS_edit_takes_its_values_from_the_edit_lists_Primitive_field()
    {
        using var fx = new ResultsFixture();
        var row = EditOf(EvaluationResults.Open(fx.Results).Pairs[0].LoadRows(), "BOUNDS");
        Assert.Equal(("before the patch", ResultsFixture.Partner, "1,1,1"), (row.Values[0].Label, row.Values[0].Plugin, row.Values[0].Text));
        Assert.Equal(("the patch sets", ResultsFixture.Patch, "2,2,2"), (row.Values[1].Label, row.Values[1].Plugin, row.Values[1].Text));
        Assert.Equal((3, "non-spatial, invented value"), (row.Category, row.Rule));
        Assert.Equal(("Static", "HcCrate"), (row.BaseType, row.BaseEditorId));
        Assert.Null(row.Note);
    }

    [Fact]
    public void A_long_edit_lists_only_the_values_that_differ_and_says_how_many()
    {
        using var fx = new ResultsFixture();
        var was = Enumerable.Range(0, 14).ToDictionary(i => $"Data[{i}]", i => (string?)"same");
        var now = new Dictionary<string, string?>(was) { ["Data[7]"] = "changed" };
        var lines = File.ReadAllLines(fx.EditList);
        lines[1] = ResultsFixture.EditLine("000105:Base.esm", "FIELD:Primitive", "REFR", was, now, null);
        File.WriteAllLines(fx.EditList, lines);
        var row = EditOf(EvaluationResults.Open(fx.Results).Pairs[0].LoadRows(), "BOUNDS");
        Assert.Equal("Data[7]=same", row.Values.Single(v => v.Label == "before the patch").Text);
        Assert.Equal("Data[7]=changed", row.Values.Single(v => v.Label == "the patch sets").Text);
        Assert.Contains("1 of this field's 14 values", row.Note);
    }

    [Fact]
    public void An_edit_whose_values_are_the_same_before_and_after_says_so_and_shows_how_the_record_differs()
    {
        using var fx = new ResultsFixture();
        var same = new Dictionary<string, string?> { ["Owner"] = null };
        var lines = File.ReadAllLines(fx.EditList);
        lines[1] = ResultsFixture.EditLine("000105:Base.esm", "FIELD:Primitive", "REFR", same, same, null, new { patchDiff = new { deltas = new[] { "Flags=1 (ModB.esp 0)" } } });
        File.WriteAllLines(fx.EditList, lines);
        var row = EditOf(EvaluationResults.Open(fx.Results).Pairs[0].LoadRows(), "BOUNDS");
        Assert.Contains("same value", row.Note);
        Assert.Equal("Flags=1 (ModB.esp 0)", row.Values.Single(v => v.Label == "how the patch's record differs").Text);
        Assert.DoesNotContain("(none)", row.Summary);
    }

    [Fact]
    public void A_category_1_edit_is_listed_on_the_findings_it_joins_and_is_not_a_row()
    {
        using var fx = new ResultsFixture();
        var rows = EvaluationResults.Open(fx.Results).Pairs[0].LoadRows();
        Assert.DoesNotContain(rows, r => r.Lane == "edit" && r.Kind == "MOVE");
        foreach (var i in new[] { 0, 1 })
        {
            var line = Assert.Single(Finding(rows, i).JoinedEdits);
            Assert.Contains("MOVE REFR 000101:Base.esm", line);
            Assert.Contains("1,1,2", line);
        }
        Assert.Empty(Finding(rows, 3).JoinedEdits);
    }

    [Fact]
    public void An_edit_list_row_that_is_not_a_scored_edit_is_not_a_row()
    {
        using var fx = new ResultsFixture();
        Assert.DoesNotContain(EvaluationResults.Open(fx.Results).Pairs[0].LoadRows(), r => r.FormKey == "000109:Base.esm");
    }

    [Fact]
    public void An_added_record_and_an_empty_field_say_which_values_the_edit_list_does_not_hold()
    {
        using var fx = new ResultsFixture();
        var rows = EvaluationResults.Open(fx.Results).Pairs[0].LoadRows();
        var add = EditOf(rows, "ADD");
        Assert.Empty(add.Values);
        Assert.Contains("added record", add.Note);
        Assert.Equal((ResultsFixture.Cell, "000800:Patch.esp", "after"), (add.Place.Cell, add.Place.RefKey, add.DefaultSetup));
        var navi = EditOf(rows, "FIELD:MapInfos");
        Assert.Contains("no values", navi.Note);
        Assert.Equal("MapInfos: 3 vs 4 item(s)", navi.Values.Single(v => v.Label == "how the patch's record differs").Text);
    }

    [Fact]
    public void A_predicted_row_opens_before_the_patch_and_an_edit_after_it_except_a_disable()
    {
        using var fx = new ResultsFixture();
        var rows = EvaluationResults.Open(fx.Results).Pairs[0].LoadRows();
        Assert.All(rows.Where(r => r.Lane != "edit"), r => Assert.Equal("before", r.DefaultSetup));
        Assert.Equal("before", EditOf(rows, "DISABLE").DefaultSetup);
        Assert.Equal("after", EditOf(rows, "BOUNDS").DefaultSetup);
        Assert.Equal("InitiallyDisabled", EditOf(rows, "DISABLE").Values.Single(v => v.Label == "how it is disabled").Text);
    }

    [Fact]
    public void A_reference_filed_in_a_persistent_cell_has_no_cell_and_says_so()
    {
        using var fx = new ResultsFixture();
        var rows = EvaluationResults.Open(fx.Results).Pairs[0].LoadRows();
        foreach (var place in new[] { Finding(rows, 3).Place, EditOf(rows, "DISABLE").Place })
        {
            Assert.Null(place.Cell);
            Assert.True(place.InPersistentCell);
            Assert.NotNull(place.RefKey);
            Assert.Contains("persistent cell", place.WhyNot);
        }
        Assert.Contains("in HcWorld's persistent cell", EditOf(rows, "DISABLE").Summary);
    }

    [Fact]
    public void An_edit_in_a_persistent_cell_that_carries_a_grid_is_known_from_the_names_mark()
    {
        using var fx = new ResultsFixture();
        File.WriteAllText(fx.EditList, File.ReadAllText(fx.EditList).Replace("\"grid\":null,\"worldspace\":\"HcWorld\"", "\"grid\":\"0,0\",\"worldspace\":\"HcWorld\""));
        var place = EditOf(EvaluationResults.Open(fx.Results).Pairs[0].LoadRows(), "DISABLE").Place;
        Assert.Null(place.Cell);
        Assert.True(place.InPersistentCell);
    }

    [Fact]
    public void A_record_with_no_place_says_why_by_its_type_and_a_cell_record_opens_itself()
    {
        using var fx = new ResultsFixture();
        var rows = EvaluationResults.Open(fx.Results).Pairs[0].LoadRows();
        var navi = EditOf(rows, "FIELD:MapInfos").Place;
        Assert.Null(navi.Cell);
        Assert.False(navi.InPersistentCell);
        Assert.Contains("NAVI", navi.WhyNot);
        var cell = Finding(rows, 2);
        Assert.Equal(("CELL", ResultsFixture.Cell, (string?)null, "HcYard"), (cell.RecordType, cell.Place.Cell, cell.Place.RefKey, cell.EditorId));
    }

    [Fact]
    public void A_contact_row_shows_what_the_check_says_and_opens_on_its_two_references()
    {
        using var fx = new ResultsFixture();
        var row = Finding(EvaluationResults.Open(fx.Results).Pairs[0].LoadRows(), 4);
        Assert.Equal(("contact", "CONTACT", ResultGroup.Addressed), (row.Lane, row.Kind, row.Group));
        Assert.Equal(new[] { "what the check says", "label", "physical state", "physical detail" }, row.Values.Select(v => v.Label));
        Assert.Equal("touches nothing within 4u", row.Values[3].Text);
        Assert.Equal((ResultsFixture.Cell, "000103:ModA.esp", "000104:Base.esm"), (row.Place.Cell, row.Place.RefKey, row.Place.OtherKey));
    }

    [Fact]
    public void A_navmesh_row_and_a_navmesh_record_open_the_cell_the_navmesh_checks_label_names()
    {
        using var fx = new ResultsFixture();
        var rows = EvaluationResults.Open(fx.Results).Pairs[0].LoadRows();
        var row = Finding(rows, 5);
        Assert.Equal(("navmesh", "EDGE-IDX", "NAVM"), (row.Lane, row.Kind, row.RecordType));
        Assert.Equal((ResultsFixture.NavCell, "000301:Base.esm", "000302:Base.esm"), (row.Place.Cell, row.Place.RefKey, row.Place.OtherKey));
        var record = Finding(rows, 6);
        Assert.Equal(("record", "NAVM", ResultsFixture.NavCell), (record.Lane, record.RecordType, record.Place.Cell));
        Assert.Equal(3, record.Values.Count);
    }

    [Fact]
    public void A_predicted_overlap_keeps_its_attribution_and_says_when_it_is_not_both_mods()
    {
        using var fx = new ResultsFixture();
        var overlaps = EvaluationResults.Open(fx.Results).Pairs[0].LoadRows().Where(r => r.Group == ResultGroup.NotScored).ToList();
        Assert.Equal(new[] { "needs-both", "with-target" }, overlaps.Select(r => r.Attribution));
        Assert.Equal(("000103:ModA.esp", "000108:ModB.esp", ResultsFixture.Cell), (overlaps[0].Place.RefKey, overlaps[0].Place.OtherKey, overlaps[0].Place.Cell));
        Assert.Null(overlaps[0].Note);
        Assert.Contains("with-target", overlaps[1].Note);
        // its own cell is a persistent cell, so it opens in its partner's
        Assert.Equal(ResultsFixture.Cell, overlaps[1].Place.Cell);
    }

    [Fact]
    public void The_two_setups_are_the_patched_runs_scored_order_and_that_order_with_the_patch()
    {
        using var fx = new ResultsFixture();
        var pair = EvaluationResults.Open(fx.Results).Pairs[0];
        Assert.EndsWith("candidates/candidate-00-baseline.json", pair.Before!.OrderFile);
        Assert.EndsWith("candidates/candidate-04.json", pair.After!.OrderFile);
        Assert.Equal("EP-sc-pair-a", pair.After.Profile);
        Assert.EndsWith("/instance", pair.After.Instance);
        Assert.EndsWith("/game", pair.After.Game);
        Assert.Contains(pair.Notes, n => n.Contains("the receipt does not pin"));
    }

    [Fact]
    public void A_setup_file_that_does_not_match_the_patched_runs_manifest_is_not_offered()
    {
        using var fx = new ResultsFixture();
        ResultsFixture.Flip(Path.Combine(fx.Patched, "bundle", "evidence", "candidates", "candidate-04.json"));
        var pair = EvaluationResults.Open(fx.Results).Pairs[0];
        Assert.NotNull(pair.Before);
        Assert.Null(pair.After);
        Assert.Contains(pair.Notes, n => n.Contains("candidate-04.json does not match"));
    }

    [Fact]
    public void A_missing_patched_run_still_lists_every_row_and_says_what_is_not_shown()
    {
        using var fx = new ResultsFixture();
        Directory.Delete(fx.Patched, true);
        var pair = EvaluationResults.Open(fx.Results).Pairs[0];
        Assert.Null(pair.PatchedRun);
        Assert.Null(pair.Before);
        Assert.Null(pair.After);
        Assert.Contains(pair.Notes, n => n.Contains("patched run") && n.Contains("not on disk"));
        var rows = pair.LoadRows();
        Assert.Equal(5, rows.Count(r => r.Group == ResultGroup.PatchChange));
        var bounds = EditOf(rows, "BOUNDS");
        Assert.Empty(bounds.Values);
        Assert.Contains("not on disk", bounds.Note);
        Assert.Equal(ResultsFixture.Cell, bounds.Place.Cell);                                   // the result file's own cell
        Assert.True(EditOf(rows, "DISABLE").Place.InPersistentCell);                            // known from the names mark
        Assert.Equal(3, Finding(rows, 0).Values.Count);                                         // predicted rows are whole
        Assert.Equal("MOVE REFR 000101:Base.esm", Assert.Single(Finding(rows, 0).JoinedEdits));
    }

    [Fact]
    public void A_missing_sealed_run_still_lists_findings_and_edits_and_says_what_is_not_shown()
    {
        using var fx = new ResultsFixture();
        Directory.Delete(fx.Sealed, true);
        var pair = EvaluationResults.Open(fx.Results).Pairs[0];
        Assert.Null(pair.SealedRun);
        Assert.Contains(pair.Notes, n => n.Contains("made the predictions") && n.Contains("not on disk"));
        var rows = pair.LoadRows();
        Assert.Equal(7, rows.Count(r => r.Group is ResultGroup.Addressed or ResultGroup.NotAddressed));
        Assert.DoesNotContain(rows, r => r.Group == ResultGroup.NotScored);
        Assert.Null(pair.Counts.NotScored);
        var first = Finding(rows, 0);
        Assert.Empty(first.Values);
        Assert.Contains("not on disk", first.Note);
        Assert.Contains("not on disk", first.Place.WhyNot);
        Assert.Equal(2, EditOf(rows, "BOUNDS").Values.Count);
    }

    [Fact]
    public void A_sealed_run_that_was_moved_is_read_where_the_caller_says_it_is()
    {
        using var fx = new ResultsFixture();
        var recorded = fx.Sealed.Replace('\\', '/');
        var moved = Path.Combine(fx.Root, "elsewhere");
        Directory.Move(fx.Sealed, moved);
        var pair = EvaluationResults.Open(fx.Results, at => string.Equals(at, recorded, StringComparison.OrdinalIgnoreCase) ? moved : null).Pairs[0];
        var rows = pair.LoadRows();
        Assert.Equal(3, Finding(rows, 0).Values.Count);
        Assert.Equal(4, Finding(rows, 4).Values.Count);
        Assert.Equal(2, pair.Counts.NotScored);
    }

    [Fact]
    public void A_sealed_manifest_that_does_not_match_the_result_file_hides_the_values_and_cells_it_pins()
    {
        using var fx = new ResultsFixture();
        ResultsFixture.Flip(Path.Combine(fx.Bundle, "manifest.json"));
        var pair = EvaluationResults.Open(fx.Results).Pairs[0];
        var rows = pair.LoadRows();
        var first = Finding(rows, 0);
        Assert.Empty(first.Values);
        Assert.Contains("manifest.json does not match", first.Note);
        Assert.Null(first.Place.Cell);
        Assert.Contains(pair.Notes, n => n.Contains("manifest.json does not match"));
        Assert.Equal(4, Finding(rows, 4).Values.Count);                                         // the contact file has its own pin
    }

    [Fact]
    public void A_rows_file_that_does_not_match_the_sealed_manifest_hides_its_values_and_names_the_file()
    {
        using var fx = new ResultsFixture();
        ResultsFixture.Flip(fx.RecordRows);
        var pair = EvaluationResults.Open(fx.Results).Pairs[0];
        var rows = pair.LoadRows();
        var first = Finding(rows, 0);
        Assert.Empty(first.Values);
        Assert.Contains("records.contested-fields__baseline/03-rows.json does not match", first.Note);
        Assert.Equal(ResultsFixture.Cell, first.Place.Cell);                                    // names still match
        Assert.Equal(3, Finding(rows, 2).Values.Count);                                         // the other check's file still matches
    }

    [Fact]
    public void A_names_file_that_does_not_match_the_sealed_manifest_gives_no_cell_and_names_the_file()
    {
        using var fx = new ResultsFixture();
        ResultsFixture.Flip(fx.Names);
        var pair = EvaluationResults.Open(fx.Results).Pairs[0];
        var first = Finding(pair.LoadRows(), 0);
        Assert.Null(first.Place.Cell);
        Assert.Contains("names.json does not match", first.Place.WhyNot);
        Assert.Equal(3, first.Values.Count);
        Assert.Contains(pair.Notes, n => n.Contains("names.json does not match"));
    }

    [Fact]
    public void A_probe_file_that_does_not_match_the_result_file_hides_what_the_check_says()
    {
        using var fx = new ResultsFixture();
        ResultsFixture.Flip(Path.Combine(fx.Bundle, "sweep", "inv-0001", "contactprobe", "baseline.json"));
        var row = Finding(EvaluationResults.Open(fx.Results).Pairs[0].LoadRows(), 4);
        Assert.Empty(row.Values);
        Assert.Contains("contactprobe/baseline.json does not match", row.Note);
        Assert.Equal(("000103:ModA.esp", "000104:Base.esm"), (row.FormKey, row.Place.OtherKey));   // from the finding's own roles
    }

    [Fact]
    public void An_overlap_list_that_does_not_match_its_pin_lists_no_overlaps_and_counts_none()
    {
        using var fx = new ResultsFixture();
        ResultsFixture.Flip(fx.Presentation);
        var pair = EvaluationResults.Open(fx.Results).Pairs[0];
        Assert.DoesNotContain(pair.LoadRows(), r => r.Group == ResultGroup.NotScored);
        Assert.Null(pair.Counts.NotScored);
        Assert.Contains(pair.Notes, n => n.Contains("presentation.json does not match verdicts-pre-patch.json"));
    }

    [Fact]
    public void Opening_and_loading_changes_no_file_of_the_evaluation()
    {
        using var fx = new ResultsFixture();
        Dictionary<string, string> Listing() => Directory.EnumerateFiles(fx.Root, "*", SearchOption.AllDirectories)
            .ToDictionary(f => f, f => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))) + "|" + File.GetLastWriteTimeUtc(f).Ticks);
        var before = Listing();
        var ev = EvaluationResults.Open(fx.Results);
        ev.Pairs[0].LoadRows();
        Assert.Equal(before, Listing());
    }

    [Fact]
    public void A_file_another_reader_holds_open_can_still_be_read_and_a_writer_is_kept_out_while_it_is_read()
    {
        using var fx = new ResultsFixture();
        using (new FileStream(fx.ResultFile, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.Single(EvaluationResults.Open(fx.Results).Pairs);
        using var held = ResultFiles.Open(fx.Names);
        Assert.False(held.CanWrite);
        Assert.ThrowsAny<IOException>(() => new FileStream(fx.Names, FileMode.Open, FileAccess.Write, FileShare.ReadWrite).Dispose());
    }

    [Fact]
    public void Rows_are_read_once_and_a_cancelled_read_is_not_kept()
    {
        using var fx = new ResultsFixture();
        var pair = EvaluationResults.Open(fx.Results).Pairs[0];
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => pair.LoadRows(cancelled.Token));
        var rows = pair.LoadRows();
        Assert.Same(rows, pair.LoadRows());
        Assert.Equal(14, rows.Count);
    }
}

/// <summary>The reader on a real finished evaluation. Its files carry load-order names and measured counts, so they are not
/// tracked: WB_RESULTS names the results folder and WB_RESULTS_EXPECT a JSON file of the counts to reproduce
/// ({"totals":{...},"pairs":{"id":{...}},"places":{...}}). WB_RESULTS_REPORT, when set, names a file the measured place
/// counts, times, notes and one sample row per kind are written to. Without the first two the tests fail saying so.</summary>
[Trait("tier", "real-run")]
public sealed class RealResultsTests
{
    static string Folder() => Environment.GetEnvironmentVariable("WB_RESULTS") is { Length: > 0 } r && Directory.Exists(r) ? r
        : throw new Xunit.Sdk.XunitException("set WB_RESULTS to a finished evaluation's results folder (SCORING-RECEIPT.txt and scoring/result/) to read real results");

    static JsonElement Expect()
    {
        var path = Environment.GetEnvironmentVariable("WB_RESULTS_EXPECT") is { Length: > 0 } e && File.Exists(e) ? e
            : throw new Xunit.Sdk.XunitException("set WB_RESULTS_EXPECT to a JSON file of the counts WB_RESULTS must reproduce (totals, pairs, places)");
        using var d = JsonDocument.Parse(File.ReadAllText(path));
        return d.RootElement.Clone();
    }

    static void Same(JsonElement want, ResultCounts got, string what)
    {
        foreach (var p in want.EnumerateObject())
        {
            int? have = p.Name switch
            {
                "addressed" => got.Addressed, "notAddressed" => got.NotAddressed, "patchChange" => got.PatchChange,
                "category3" => got.Category3, "category4" => got.Category4, "category5" => got.Category5, "notScored" => got.NotScored,
                _ => throw new Xunit.Sdk.XunitException($"WB_RESULTS_EXPECT names a count '{p.Name}' the reader does not have"),
            };
            Assert.True(p.Value.GetInt32() == have, $"{what}: {p.Name} is {have}, expected {p.Value.GetInt32()}");
        }
    }

    [Fact]
    public void The_real_results_reproduce_their_counts_and_every_row_is_whole()
    {
        var expect = Expect();
        var clock = Stopwatch.StartNew();
        var ev = EvaluationResults.Open(Folder());
        var report = new JsonObject { ["openSeconds"] = Math.Round(clock.Elapsed.TotalSeconds, 2) };
        var times = new JsonObject(); var notes = new JsonObject();
        var all = new List<ResultRow>();
        foreach (var pair in ev.Pairs)
        {
            clock.Restart();
            var rows = pair.LoadRows();
            times[pair.Id] = Math.Round(clock.Elapsed.TotalSeconds, 2);
            notes[pair.Id] = new JsonArray(pair.Notes.Select(n => (JsonNode?)n).ToArray());
            all.AddRange(rows);

            if (expect.GetProperty("pairs").TryGetProperty(pair.Id, out var want)) Same(want, pair.Counts, pair.Id);
            var c = pair.Counts;
            Assert.Equal(c.Addressed, rows.Count(r => r.Group == ResultGroup.Addressed));
            Assert.Equal(c.NotAddressed, rows.Count(r => r.Group == ResultGroup.NotAddressed));
            Assert.Equal(c.PatchChange, rows.Count(r => r.Group == ResultGroup.PatchChange));
            Assert.Equal(c.NotScored, rows.Count(r => r.Group == ResultGroup.NotScored));
            Assert.All(rows, r => Assert.False(string.IsNullOrWhiteSpace(r.Summary), $"{r.Key} has no summary"));
            Assert.All(rows, r => Assert.True(r.Place.Cell is not null || r.Place.WhyNot is { Length: > 0 }, $"{r.Key} has neither a cell nor a sentence why not"));
            if (pair.SealedRun is not null)
                Assert.All(rows.Where(r => r.Lane == "record"), r => Assert.True(r.Values.Count == 3, $"{r.Key} has {r.Values.Count} values: {r.Note}"));
        }
        Assert.Equal(expect.GetProperty("pairs").EnumerateObject().Select(p => p.Name).OrderBy(x => x), ev.Pairs.Select(p => p.Id).OrderBy(x => x));
        Same(expect.GetProperty("totals"), ev.Totals, "totals");
        Assert.Equal(all.Count, all.Select(r => r.Key).Distinct().Count());

        var places = new JsonObject();
        foreach (var g in Enum.GetValues<ResultGroup>())
        {
            var mine = all.Where(r => r.Group == g).ToList();
            places[g.ToString()] = new JsonObject
            {
                ["rows"] = mine.Count, ["withCell"] = mine.Count(r => r.Place.Cell is not null),
                ["inPersistentCell"] = mine.Count(r => r.Place.InPersistentCell), ["noPlace"] = mine.Count(r => r.Place.Cell is null && !r.Place.InPersistentCell),
                ["withNote"] = mine.Count(r => r.Note is not null),
            };
        }
        report["loadRowsSeconds"] = times; report["places"] = places; report["pairNotes"] = notes;
        report["evaluationNotes"] = new JsonArray(ev.Notes.Select(n => (JsonNode?)n).ToArray());
        report["checked"] = ev.Checked.Count;
        report["findingNotes"] = new JsonArray(all.Where(r => r.Lane is not ("edit" or "overlap") && r.Note is not null).Select(r => (JsonNode?)$"{r.Key}: {r.Note}").ToArray());
        report["samples"] = new JsonObject(all.GroupBy(r => $"{r.Group} {r.Lane} {(r.Lane == "record" ? r.RecordType : r.Kind.Split(':')[0])}")
            .Select(g => KeyValuePair.Create(g.Key, (JsonNode?)g.First().Summary)));
        report["whyNot"] = new JsonObject(all.Where(r => r.Place.Cell is null).GroupBy(r => r.Place.WhyNot ?? "").OrderByDescending(g => g.Count())
            .Select(g => KeyValuePair.Create(g.Key, (JsonNode?)g.Count())));
        if (Environment.GetEnvironmentVariable("WB_RESULTS_REPORT") is { Length: > 0 } to)
            File.WriteAllText(to, report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        if (expect.TryGetProperty("places", out var wantPlaces))
            foreach (var p in wantPlaces.EnumerateObject())
                Assert.True(p.Value.GetInt32() == places[p.Name]!["withCell"]!.GetValue<int>(), $"rows with a cell in {p.Name}: {places[p.Name]!["withCell"]}, expected {p.Value.GetInt32()}");
    }

    [Fact]
    public void A_copy_of_the_real_results_with_one_byte_changed_in_a_result_file_refuses_by_name()
    {
        var folder = Folder();
        var copy = Path.Combine(Path.GetTempPath(), "hc-wb-results-copy-" + Guid.NewGuid().ToString("N"));
        try
        {
            // the top-level files and scoring/, read from the original and written only to the copy
            Directory.CreateDirectory(copy);
            foreach (var f in Directory.EnumerateFiles(folder)) File.Copy(f, Path.Combine(copy, Path.GetFileName(f)));
            foreach (var f in Directory.EnumerateFiles(Path.Combine(folder, "scoring"), "*", SearchOption.AllDirectories))
            {
                var to = Path.Combine(copy, "scoring", Path.GetRelativePath(Path.Combine(folder, "scoring"), f));
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                File.Copy(f, to);
            }
            var whole = EvaluationResults.Open(copy);
            Assert.NotEmpty(whole.Pairs);
            var victim = Path.Combine(copy, "scoring", "result", whole.Pairs[0].Id + ".json");
            File.SetAttributes(victim, FileAttributes.Normal);
            ResultsFixture.Flip(victim);
            var x = Assert.Throws<InvalidOperationException>(() => EvaluationResults.Open(copy));
            Assert.Contains($"scoring/result/{whole.Pairs[0].Id}.json does not match", x.Message);
        }
        finally { try { Directory.Delete(copy, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }
}
