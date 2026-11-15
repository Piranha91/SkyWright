using System.Text.Json;
using System.Text.Json.Nodes;
using Workbench;
using Xunit;

namespace WorkbenchTests;

/// <summary>Stage 4, review H2 and the re-run's command: which probe build runs, the refusal to compare two builds, and the
/// probe's arguments and orders as the bundle's own receipt and candidate give them.</summary>
[Trait("tier", "unit")]
public class RecheckTests
{
    static string Folder(string name)
    {
        var d = Path.Combine(AppContext.BaseDirectory, "recheck-" + name + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(d);
        return d;
    }

    [Fact]
    public void The_bundles_own_build_is_used_when_a_folder_holds_it()
    {
        var other = Folder("other"); var same = Folder("same"); var local = Folder("local");
        var digests = new Dictionary<string, string> { [other] = "folder:bbbb", [same] = "folder:aaaa", [local] = "folder:cccc" };
        var (f, d, said) = Recheck.ChooseBuild("overlapprobe", "folder:aaaa", new[] { other, same }, local, x => digests[x]);
        Assert.Equal(same, f);
        Assert.Equal("folder:aaaa", d);
        Assert.Contains("the bundle's own build", said);
    }

    /// <summary>H2's red case: a build other than the bundle's is never taken as the bundle's; the local build runs both
    /// arms and the sentence says the bundle's rows are not compared with it.</summary>
    [Fact]
    public void A_different_build_is_used_for_both_arms_and_said_never_taken_as_the_bundles()
    {
        var other = Folder("other"); var local = Folder("local");
        var digests = new Dictionary<string, string> { [other] = "folder:bbbb", [local] = "folder:cccc" };
        var (f, d, said) = Recheck.ChooseBuild("overlapprobe", "folder:aaaa", new[] { other }, local, x => digests[x]);
        Assert.Equal(local, f);
        Assert.Equal("folder:cccc", d);
        Assert.Contains("both arms re-run with the local build", said);
        Assert.Contains("not compared", said);
    }

    [Fact]
    public void With_no_build_on_disk_the_re_check_is_refused_in_one_sentence()
    {
        var (f, _, said) = Recheck.ChooseBuild("overlapprobe", "folder:aaaa", new[] { Path.Combine(AppContext.BaseDirectory, "no-such") }, null, _ => null);
        Assert.Null(f);
        Assert.Contains("cannot run", said);
        Assert.DoesNotContain('\n', said);
    }

    /// <summary>H2's guard: two runs of different builds are refused before conflictdiff is started (the exe named here
    /// does not exist, so reaching it would fail differently).</summary>
    [Fact]
    public void Two_runs_of_different_builds_are_refused_before_anything_is_compared()
    {
        var a = new ProbeRun("unpatched", "u.json", "folder:aaaa", 1, 0, "u.log");
        var b = new ProbeRun("patched", "p.json", "folder:bbbb", 1, 0, "p.log");
        var ex = Record.Exception(() => Recheck.Compare(a, b, "overlap|x|y", Path.Combine(AppContext.BaseDirectory, "no-conflictdiff.exe")));
        Assert.NotNull(ex);
        Assert.Contains("different probe builds", ex!.Message);
    }

    [Fact]
    public void The_probe_command_comes_from_the_receipts_adapter_with_the_base_only_on_the_patched_run()
    {
        using var d = JsonDocument.Parse("""{ "exe": "overlapprobe", "populationFlag": "--population", "args": ["--order", "{order}", "--emit", "{emit}", "--base", "{base}"], "commonFlags": true }""");
        var un = Recheck.Args(d.RootElement, "o.json", "u.json", null, "W", "unpatched", "pop.json", "I", "P", "G");
        Assert.Equal(new[] { "--order", "o.json", "--emit", "u.json", "--instance", "I", "--profile", "P", "--game", "G", "--population", "pop.json" }, un);
        var pa = Recheck.Args(d.RootElement, "o2.json", "p.json", "u.json", "W", "patched", "pop.json", "I", "P", "G");
        Assert.Equal(new[] { "--order", "o2.json", "--emit", "p.json", "--base", "u.json", "--instance", "I", "--profile", "P", "--game", "G", "--population", "pop.json" }, pa);
        using var es = JsonDocument.Parse("""{ "exe": "contactprobe", "populationFlag": "--population", "args": ["--order", "{order}", "--empty-space", "{out}/{arm}.empty-space.json", "--emit", "{emit}"], "commonFlags": true }""");
        var e = Recheck.Args(es.RootElement, "o.json", "u.json", "b.json", "W", "patched", "pop.json", "I", "P", "G");
        Assert.Equal("W/patched.empty-space.json", e[3]);
        Assert.DoesNotContain("--base", e);
    }

    [Fact]
    public void The_patched_order_is_the_arms_order_with_the_patch_last_and_its_folder_on_top()
    {
        var dir = Folder("orders");
        var cand = Path.Combine(dir, "candidate.json");
        File.WriteAllText(cand, """{ "SchemaVersion": 1, "Name": "arm", "BaseProfile": "Gone", "Plugins": ["A.esm", "B.esp"], "Mods": ["ModB", "ModA"], "Changes": ["x"] }""");
        var (un, pa) = Recheck.Orders(cand, "Lab", "Patch.esp", "houseCARL - Patch", dir);
        var u = JsonNode.Parse(File.ReadAllText(un))!; var p = JsonNode.Parse(File.ReadAllText(pa))!;
        Assert.Equal("Lab", (string?)u["BaseProfile"]);
        Assert.Equal(new[] { "A.esm", "B.esp" }, u["Plugins"]!.AsArray().Select(x => (string?)x));
        Assert.Equal(new[] { "A.esm", "B.esp", "Patch.esp" }, p["Plugins"]!.AsArray().Select(x => (string?)x));
        Assert.Equal(new[] { "houseCARL - Patch", "ModB", "ModA" }, p["Mods"]!.AsArray().Select(x => (string?)x));
        Assert.Equal(new[] { "ModB", "ModA" }, u["Mods"]!.AsArray().Select(x => (string?)x));
    }
}
