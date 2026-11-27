using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using HarnessShared;
using Workbench;
using Xunit;

namespace WorkbenchTests;

/// <summary>Record checks round 3, a hand-built manual run's bundle opened on its review arm, read as the window reads it
/// (BundleConflicts.Read on the bundle's verdict). U2-F1: a lost-edit row the setup without the selected mods was compared
/// with (its receipt accepted with no rows) is new with the selected mods, and its line says so. Unit 3: an AI-side
/// evaluation's imported receipt of loadability.missing-assets, an id the app now lists itself, stays out of the Findings.</summary>
[Trait("tier", "unit")]
public sealed class RecordBundleTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "sw-record-bundle-" + Guid.NewGuid().ToString("N")[..8]);
    string Dir => Path.Combine(_root, "bundle");
    string Verify => Path.Combine(_root, "verify.json");
    readonly JsonArray _outputs = new();
    const string Sentence = "Base.esp (mod Base Mod) changed the Scale of the placed object 000801 in Skyrim.esm to 2; Other.esp (mod Other Mod) wins with 1 and was not made with Base.esp, so the change is lost in game.";

    void Receipt(string key, string check, string rel, object file, string kind)
    {
        var full = Path.Combine(Dir, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        if (file is ConflictFile cf) ConflictIo.Write(full, cf); else File.WriteAllText(full, (string)file);
        File.WriteAllText(Path.Combine(Dir, "receipts", key + ".json"), JsonSerializer.Serialize(new
        {
            checkId = check, result = new { execution = "completed" }, validation = new { accepted = true },
            outputs = new[] { new { path = rel } }, adapter = new { kind, output = "conflict-file" },
        }));
        _outputs.Add(new JsonObject { ["receipt"] = key, ["recordedPath"] = rel, ["path"] = rel, ["present"] = true, ["matches"] = true,
                                      ["sha256"] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(full))).ToLowerInvariant() });
    }

    public RecordBundleTests()
    {
        Directory.CreateDirectory(Path.Combine(Dir, "receipts"));
        File.WriteAllText(Path.Combine(Dir, "plan.json"), new JsonObject
        {
            ["planId"] = "record-fixture", ["reviewArm"] = "candidate-01",
            ["arms"] = new JsonArray(new JsonObject { ["armId"] = "baseline", ["kind"] = "baseline" }, new JsonObject { ["armId"] = "candidate-01", ["kind"] = "candidate" }),
            ["targets"] = new JsonArray(new JsonObject { ["name"] = "Base.esp" }),
            ["checks"] = new JsonArray(
                new JsonObject { ["checkId"] = "records.contested-fields", ["evidence"] = new JsonObject { ["strength"] = "record-fact" } },
                new JsonObject { ["checkId"] = "loadability.missing-assets", ["evidence"] = new JsonObject { ["strength"] = "record-fact" } }),
        }.ToJsonString());
        const string T = "2026-11-27 00:00:00Z";
        // the setup without the selected mods: no row, and what it looked at (the selection's plugin, not loaded)
        Receipt("records.contested-fields__baseline", "records.contested-fields", "outputs/cf/baseline.json", new ConflictFile("recordprobe", "i", T, Array.Empty<Conflict>(),
            new[] { new CoverageLine("versions", 0), new CoverageLine("plugins and mod folders of the selection, looked for in this load order", 1) }), "probe-emit");
        var row = new Conflict("LOST-EDIT", "Base.esp", "Other.esp", Sentence,
            new[] { new ConflictRecord("000801:Skyrim.esm", "record", null, PhysicalState.Holds, "its Scale field") },
            new[] { new ConflictRecord("000801:Skyrim.esm#Scale#Base.esp", "field", "Scale") });
        Receipt("records.contested-fields__candidate-01", "records.contested-fields", "outputs/cf/candidate-01.json", new ConflictFile("recordprobe", "i", T, new[] { row },
            new[] { new CoverageLine("versions", 1) }), "probe-emit");
        // an AI-side evaluation's import of the missing-assets id, which the app now runs itself
        var imported = new Conflict("MISSING-ASSET", "Base.esp", "", "meshes/x.nif has no provider in this view", new[] { new ConflictRecord("000802:Base.esp", "record", null, PhysicalState.Holds) });
        Receipt("loadability.missing-assets__candidate-01", "loadability.missing-assets", "imports/ma/result.json", new ConflictFile("recordprobe", "i", T, new[] { imported }), "mcp-import");
        File.WriteAllText(Verify, new JsonObject
        {
            ["bundle"] = new JsonObject { ["structurallyValid"] = true, ["complete"] = true, ["defects"] = new JsonArray(), ["mismatched"] = new JsonArray(), ["missing"] = new JsonArray() },
            ["toolIdentity"] = new JsonObject { ["guarantee"] = "verified" }, ["receiptOutputs"] = _outputs,
        }.ToJsonString());
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    [Fact]
    public void U2_F1_a_lost_edit_compared_with_the_setup_without_the_selected_mods_is_new_with_them_and_says_so()
    {
        var b = BundleConflicts.Read(Dir, "candidate-01", Verify, "verify: fixture verdict");
        Assert.True(b.Manual);
        var f = Assert.Single(b.Findings, x => x.CheckId == "records.contested-fields");
        Assert.True(f.NewOnArm);
        var ranked = Assert.Single(FindingRanking.Rank(b.Findings.Where(x => x.CheckId == "records.contested-fields"), b.Targets, null));
        // the row names no cell the fixture's names could resolve, so the shared line adds that it does not open the viewer
        Assert.StartsWith("1. " + Sentence + "   (new with the selected mods; finding; records.contested-fields)", FindingWords.Line(ranked, b.Manual));
        var status = Assert.Single(b.Checks, c => c.CheckId == "records.contested-fields");
        Assert.Equal("1 new row(s) with the selected mods; 0 were there before and are not listed", CheckStatusWords.Rows(status));
    }

    [Fact]
    public void The_missing_assets_tile_is_held_with_its_own_reason_a_plain_line_and_help()
    {
        var c = Assert.Single(Launcher.Registry(), x => x.Id == "loadability.missing-assets");
        Assert.True(c.Held);
        Assert.Equal("probe-emit", c.Kind);
        Assert.Equal("it is held back until it has found a real clash between two mods", c.HeldReason);
        Assert.Equal(c.HeldReason, new Launcher().WhyNotHere(c));
        Assert.Contains("archive of the same name", ProbeNames.Says(c.Id));
        var help = ProbeHelp.Of(c.Id);
        Assert.NotNull(help);
        Assert.Contains("archive", help!.What + help.How);
    }

    [Fact]
    public void An_AI_side_import_of_the_missing_assets_id_stays_out_of_the_apps_Findings_and_is_said_once()
    {
        var b = BundleConflicts.Read(Dir, "candidate-01", Verify, "verify: fixture verdict");
        Assert.DoesNotContain(b.Findings, x => x.CheckId == "loadability.missing-assets");
        Assert.DoesNotContain(b.Checks, c => c.CheckId == "loadability.missing-assets");
        Assert.Contains(b.Notes, n => n.Contains("checks this SkyWright does not list", StringComparison.Ordinal) && n.Contains("loadability.missing-assets", StringComparison.Ordinal));
    }
}
