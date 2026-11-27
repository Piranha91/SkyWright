using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Workbench;
using Xunit;

namespace WorkbenchTests;

/// <summary>A verified-bundle fixture holding one lighting receipt (environment.lighting on the review arm: the probe's json first, then
/// the three order files the check read), with the bundle's own copy of the registry, read through the real <see cref="BundleConflicts"/>
/// path (Gate C round 1, item 3). The lighting json is the one under fixtures/lighting/ (cut from the end-to-end scan when present).</summary>
public sealed class LightBundle : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "hc-wb-light-" + Guid.NewGuid().ToString("N"));
    public string Dir => Path.Combine(Root, "bundle");
    public string Verify => Path.Combine(Root, "verify.json");

    static string Sha(string p) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))).ToLowerInvariant();

    /// <param name="lightJson">the lighting output to carry; <paramref name="edit"/> may change the bundle's registry copy.</param>
    public LightBundle(string lightJson, Action<JsonNode>? edit = null)
    {
        Directory.CreateDirectory(Path.Combine(Dir, "receipts"));
        Directory.CreateDirectory(Path.Combine(Dir, "registry"));
        var reg = JsonNode.Parse(File.ReadAllText(Path.Combine(ReviewSpeedPart2Tests.RepoRoot(), "data", "check-registry.json")))!;
        edit?.Invoke(reg);
        File.WriteAllText(Path.Combine(Dir, "registry", "check-registry.json"), reg.ToJsonString());
        File.WriteAllText(Path.Combine(Dir, "plan.json"), new JsonObject
        {
            ["planId"] = "light-fixture", ["reviewArm"] = "candidate-01",
            ["arms"] = new JsonArray(new JsonObject { ["armId"] = "baseline", ["kind"] = "baseline" }, new JsonObject { ["armId"] = "candidate-01", ["kind"] = "candidate" }),
            ["targets"] = new JsonArray(new JsonObject { ["name"] = "COTN - Morthal.esp" }),
            ["checks"] = new JsonArray(new JsonObject { ["checkId"] = LightRows.CheckId, ["evidence"] = new JsonObject { ["strength"] = "calibrated-inference" } }),
        }.ToJsonString());
        var outs = new JsonArray();
        var paths = new List<string>();
        foreach (var (rel, text) in new[]
        {
            ("outputs/environment.lighting/candidate-01.json", File.ReadAllText(lightJson)),
            ("outputs/environment.lighting/arrangements/dependencies.json", """{"SchemaVersion":1,"Plugins":["Skyrim.esm"]}"""),
            ("outputs/environment.lighting/arrangements/selection.json", """{"SchemaVersion":1,"Plugins":["Skyrim.esm","COTN - Morthal.esp"]}"""),
            ("outputs/environment.lighting/arrangements/rest.json", """{"SchemaVersion":1,"Plugins":["Skyrim.esm","Lux.esp"]}"""),
        })
        {
            var full = Path.Combine(Dir, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, text);
            paths.Add(rel);
            outs.Add(new JsonObject { ["receipt"] = "environment.lighting__candidate-01", ["recordedPath"] = rel, ["path"] = rel, ["present"] = true, ["matches"] = true, ["sha256"] = Sha(full) });
        }
        File.WriteAllText(Path.Combine(Dir, "receipts", "environment.lighting__candidate-01.json"), JsonSerializer.Serialize(new
        {
            checkId = LightRows.CheckId, armId = "candidate-01", result = new { execution = "completed" }, validation = new { accepted = true, sentence = "accepted" },
            outputs = paths.Select(p => new { path = p }).ToArray(), adapter = new { output = "json" },
        }));
        File.WriteAllText(Verify, new JsonObject
        {
            ["bundle"] = new JsonObject { ["structurallyValid"] = true, ["complete"] = true, ["defects"] = new JsonArray(), ["mismatched"] = new JsonArray(), ["missing"] = new JsonArray() },
            ["toolIdentity"] = new JsonObject { ["guarantee"] = "verified" }, ["receiptOutputs"] = outs,
        }.ToJsonString());
    }

    public void Dispose() { try { Directory.Delete(Root, true); } catch (IOException) { } }
}

[Trait("tier", "unit")]
public sealed class LightBundleTests
{
    static string Fixture() => Path.Combine(ReviewSpeedPart2Tests.RepoRoot(), "_geometry-harness", "workbench.tests", "fixtures", "lighting", "g6-candidate-01.json");

    static void SetClass(JsonNode reg, string cls, string key, JsonNode? value) =>
        reg["checks"]!.AsArray().First(c => (string?)c!["id"] == LightRows.CheckId)!["classes"]!.AsArray().First(c => (string?)c!["id"] == cls)![key] = value;

    [Fact]
    public void A_verified_bundle_shows_the_lighting_rows_held_and_counted_lines_and_counted_entries_by_its_own_registry()
    {
        // rejects: the lighting receipt not read through the bundle path, its order files read as rows, admission or the reach-nothing
        // setting taken from anywhere but the bundle's registry copy, the counted entries lost, or the check's row count including them
        using var f = new LightBundle(Fixture());
        var b = BundleConflicts.Read(f.Dir, "candidate-01", f.Verify, "verify: fixture verdict");
        var light = b.Rows.Where(r => r.CheckId == LightRows.CheckId).ToList();
        Assert.NotEmpty(light);
        Assert.All(light, r => Assert.NotNull(r.Light));
        Assert.DoesNotContain(light, r => r.Light!.Class == "light-lights-nothing");
        var counted = b.Findings.Where(x => x.CheckId == LightRows.CheckId && x.Role == "counted, not a clash").ToList();
        Assert.NotEmpty(counted);
        var status = Assert.Single(b.Checks, c => c.CheckId == LightRows.CheckId);
        Assert.Equal("ran", status.State);
        Assert.Equal(light.Count, status.Rows);
        Assert.Contains(status.Lines, l => l.StartsWith($"{counted.Count} light(s) that now reach nothing were left out"));
        Assert.Single(b.EmitsRead, e => e.Receipt == "environment.lighting__candidate-01");
        // the Group 6 known answer at the registry's default (the fixture is the end-to-end scan's output, gate-c/e2e5): 15 stranded lights
        // listed, 39 that reach nothing counted (30 of them with the fixture fact), 2 behind a wall, 19 not judged, 2 over-limit counts
        Assert.Equal(15, light.Count(r => r.Light!.Class == "light-no-fixture" && r.Light.Verdict == "listed"));
        Assert.Equal(2, light.Count(r => r.Light!.Class == "light-behind-wall" && r.Light.Verdict == "listed"));
        Assert.Equal(19, light.Count(r => r.Light!.Verdict == "not judged"));
        Assert.Equal(36, light.Count);
        Assert.Equal(39, counted.Count);
        Assert.Equal(30, counted.Count(c => c.Summary.Contains(" sat at ")));
        Assert.Contains(status.Lines, l => l.StartsWith("cannot tell: 2 surface(s)"));

        // the bundle's registry copy says "list": the same entries become rows, nothing else changes
        using var g = new LightBundle(Fixture(), reg => SetClass(reg, "light-lights-nothing", "whenItLightsNothing", "list"));
        var b2 = BundleConflicts.Read(g.Dir, "candidate-01", g.Verify, "verify: fixture verdict");
        var light2 = b2.Rows.Where(r => r.CheckId == LightRows.CheckId).ToList();
        Assert.Equal(light.Count + counted.Count, light2.Count);
        Assert.Equal(light.Select(r => r.Identity), light2.Where(r => r.Light!.Class != "light-lights-nothing").Select(r => r.Identity));
        Assert.DoesNotContain(b2.Findings, x => x.Role == "counted, not a clash");

        // the bundle's registry copy holds "light left with no fixture": its listed rows become one held line
        using var h = new LightBundle(Fixture(), reg => { SetClass(reg, "light-no-fixture", "admitted", false); SetClass(reg, "light-no-fixture", "held", "held by the test"); });
        var b3 = BundleConflicts.Read(h.Dir, "candidate-01", h.Verify, "verify: fixture verdict");
        int stranded = light.Count(r => r.Light!.Class == "light-no-fixture" && r.Light.Verdict == "listed");
        Assert.True(stranded > 0);
        Assert.DoesNotContain(b3.Rows, r => r.Light?.Class == "light-no-fixture" && r.Light.Verdict == "listed");
        Assert.Contains(Assert.Single(b3.Checks, c => c.CheckId == LightRows.CheckId).Lines, l => l.StartsWith($"held: a light left with no fixture ({stranded})") && l.EndsWith("held by the test"));
    }
}
