using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using Workbench;
using Xunit;

namespace WorkbenchTests;

/// <summary>Gate B of the workbench's manual mode (the run folder's GATE-B.md section 4). Tests that name a type or
/// member the mode adds reach it through <see cref="Contract"/>, so this file compiles on the base commit and each such
/// test fails there on an assertion naming what is missing; the names they reach are the frozen contract.</summary>
internal static class Contract
{
    static readonly Assembly Wb = typeof(PatchWriter).Assembly;

    internal static Type Type(string name, string what) =>
        Wb.GetType("Workbench." + name) ?? throw new Xunit.Sdk.XunitException($"Workbench.{name} does not exist: {what}");

    internal static object? Static(string type, string method, string what, params object?[] args)
    {
        var t = Type(type, what);
        var m = t.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                 .FirstOrDefault(x => x.Name == method && x.GetParameters().Length == args.Length)
                ?? throw new Xunit.Sdk.XunitException($"Workbench.{type}.{method} with {args.Length} argument(s) does not exist: {what}");
        try { return m.Invoke(null, args); }
        catch (TargetInvocationException ex) when (ex.InnerException is not null) { throw ex.InnerException; }
    }

    internal static object? Prop(object o, string name) =>
        (o.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
         ?? throw new Xunit.Sdk.XunitException($"{o.GetType().Name} has no property {name}")).GetValue(o);
}

/// <summary>B2 (GATE-B.md 3.2, frozen with Piranha's answer): OutputFolderRule.Check(output, instance, profile, game)
/// returns null when a folder may take the workbench's output, else one sentence.</summary>
public sealed class OutputFolderRuleTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "hc-wb-outrule-" + Guid.NewGuid().ToString("N"));
    string Inst => Path.Combine(_root, "instance");
    string Game => Path.Combine(_root, "game");

    public OutputFolderRuleTests()
    {
        foreach (var d in new[] { "mods/Enabled/sub", "mods/Disabled", "overwrite", "profiles/P", "profiles/Other" })
            Directory.CreateDirectory(Path.Combine(Inst, d));
        Directory.CreateDirectory(Path.Combine(Game, "Data", "meshes"));
        File.WriteAllText(Path.Combine(Inst, "profiles", "P", "modlist.txt"), "+Enabled\r\n-Disabled\r\n");
        File.WriteAllText(Path.Combine(_root, "afile.txt"), "x");
    }
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    string? Check(string output) => (string?)Contract.Static("OutputFolderRule", "Check", "the output-folder rule (GATE-B.md 3.2)",
        output, Inst, "P", Game);

    [Theory]
    [InlineData("game/Data", "Data")]
    [InlineData("game/Data/meshes", "Data")]
    [InlineData("instance/mods/Enabled", "existing mod folder")]
    [InlineData("instance/mods/Enabled/sub", "existing mod folder")]
    [InlineData("instance/mods/Disabled", "existing mod folder")]
    [InlineData("instance/profiles/P", "profile")]
    [InlineData("instance/overwrite", "overwrite")]
    [InlineData("instance/mods/NewSub", "mods folder itself")]
    [InlineData("instance/profiles", "profiles")]
    [InlineData("instance", "instance")]
    [InlineData("game", "game folder")]
    [InlineData("afile.txt", "file")]
    [InlineData("instance/NewOut", "instance")]          // review M4: inside the instance, on neither list
    [InlineData("instance/downloads", "instance")]
    [InlineData("instance/profiles/Other", "profiles")]
    [InlineData("instance/overwrite/sub", "overwrite")]
    public void A_folder_the_rule_refuses_is_refused_in_one_sentence_naming_why(string rel, string word)
    {
        var why = Check(Path.Combine(_root, rel));
        Assert.False(why is null, $"{rel} was allowed");
        Assert.DoesNotContain("\n", why);
        Assert.EndsWith(".", why);
        Assert.Contains(word, why, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_relative_path_is_refused() => Assert.NotNull(Check("relative/out"));

    [Theory]
    [InlineData("instance/mods/Enabled/")]
    [InlineData("INSTANCE/MODS/ENABLED")]
    [InlineData("instance/mods/x/../Enabled")]
    [InlineData("game/data")]
    public void A_refused_folder_spelled_another_way_is_still_refused(string rel) =>    // review L4
        Assert.NotNull(Check(Path.Combine(_root, rel)));

    [Theory]
    [InlineData("instance/mods")]
    [InlineData("elsewhere/out")]
    public void The_mods_folder_itself_and_a_folder_outside_the_instance_are_allowed(string rel) =>
        Assert.Null(Check(Path.Combine(_root, rel)));
}

/// <summary>B3, B3b, B3c: the houseCARL server writes a patch under the chosen output folder, extends it there and drops
/// an override from it; and the workbench has no lab constant left as its write guard.</summary>
[Trait("tier", "integration")]
public sealed class OutputWriteTests : IClassFixture<TiltedRefWorld>
{
    readonly TiltedRefWorld _w;
    public OutputWriteTests(TiltedRefWorld w) => _w = w;

    static string ServerExe()
    {
        var exe = Path.Combine(R3Bundle.Checkout(), "src", "housecarl-mcp", "bin", "Release", "net9.0", "housecarl-mcp.exe");
        Assert.True(File.Exists(exe), $"build src/housecarl-mcp in Release first: {exe}");
        return exe;
    }

    async Task<McpClient> Server()
    {
        var data = Path.Combine(_w.Root, "hcdata-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(data);
        return await McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "housecarl", Command = ServerExe(),
            Arguments = new[] { $"--HouseCarl:DataDir={_w.DataDir}", $"--HouseCarl:ModsDir={Path.Combine(_w.Instance, "mods")}",
                                $"--HouseCarl:ProfileDir={Path.Combine(_w.Instance, "profiles", "Default")}", $"--HouseCarl:CorpusPath={TestCorpus.Path}" },
            EnvironmentVariables = new Dictionary<string, string?> { ["HOUSECARL_DATA_DIR"] = data, ["HOUSECARL_RUNLOG_DIR"] = Path.Combine(data, "runs") },
        }));
    }

    static async Task<(bool Error, string Text)> Call(McpClient c, string tool, Dictionary<string, object?> args)
    {
        var r = await c.CallToolAsync(tool, args);
        return (r.IsError == true, string.Join("\n", r.Content.OfType<TextContentBlock>().Select(b => b.Text)));
    }

    static List<Dictionary<string, object?>> Move(float z) => new()
    {
        new() { ["formid"] = TiltedRefWorld.Ref.ToString(), ["field_path"] = "Placement.Position", ["value"] = $"512,-256,{z}" },
    };

    string[] PatchesUnderMods(string stem) => Directory.GetFiles(Path.Combine(_w.Instance, "mods"), stem + "*.esp", SearchOption.AllDirectories);

    [Fact]
    public async Task B3_a_patch_written_with_an_output_folder_lands_there_and_nowhere_under_the_mods_folder()
    {
        var o = Path.Combine(_w.Root, "out-" + Guid.NewGuid().ToString("N")[..6]);
        await using var c = await Server();
        var (err, text) = await Call(c, "housecarl_apply", new() { ["ops"] = Move(70f), ["patch"] = "WbOut", ["out_path"] = o });
        Assert.False(err, "housecarl_apply refused: " + text);
        Assert.True(File.Exists(Path.Combine(o, "houseCARL - WbOut", "WbOut.esp")), $"no patch at {o}\\houseCARL - WbOut\\WbOut.esp; the server said: {text}");
        Assert.Empty(PatchesUnderMods("WbOut"));
    }

    [Fact]
    public async Task B3a_with_the_mods_folder_as_output_the_patch_goes_to_a_new_houseCARL_folder()
    {
        var mods = Path.Combine(_w.Instance, "mods");
        await using var c = await Server();
        var (err, text) = await Call(c, "housecarl_apply", new() { ["ops"] = Move(71f), ["patch"] = "WbModsOut", ["out_path"] = mods });
        Assert.False(err, "housecarl_apply refused: " + text);
        Assert.True(File.Exists(Path.Combine(mods, "houseCARL - WbModsOut", "WbModsOut.esp")), text);
        Assert.False(File.Exists(Path.Combine(mods, "WbModsOut.esp")), "the plugin was written loose into the mods folder");
    }

    [Fact]
    public async Task B3b_a_second_write_and_an_undo_reach_the_same_patch_in_the_output_folder()
    {
        var o = Path.Combine(_w.Root, "out-" + Guid.NewGuid().ToString("N")[..6]);
        var folder = Path.Combine(o, "houseCARL - WbTwice");
        await using var c = await Server();
        var first = await Call(c, "housecarl_apply", new() { ["ops"] = Move(72f), ["patch"] = "WbTwice", ["out_path"] = o });
        Assert.False(first.Error, first.Text);
        var second = await Call(c, "housecarl_apply", new() { ["ops"] = Move(73f), ["into"] = folder });
        Assert.False(second.Error, "a second write into the patch under the output folder was refused: " + second.Text);
        Assert.Single(Directory.GetFiles(o, "*.esp", SearchOption.AllDirectories));
        var undo = await Call(c, "housecarl_remove", new() { ["formids"] = new[] { TiltedRefWorld.Ref.ToString() }, ["into"] = folder });
        Assert.False(undo.Error, "dropping the override from the patch under the output folder was refused: " + undo.Text);
        Assert.Empty(PatchesUnderMods("WbTwice"));
        // review L1: read the patch back after the undo: the reference's override is gone
        using (var back = SkyrimMod.CreateFromBinaryOverlay(Path.Combine(folder, "WbTwice.esp"), SkyrimRelease.SkyrimSE))
            Assert.Empty(back.EnumerateMajorRecords<IPlacedObjectGetter>());
    }

    [Fact]
    public async Task B3d_into_an_absolute_folder_houseCARL_does_not_own_is_refused()
    {
        var foreign = Path.Combine(_w.Root, "foreign-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(foreign);
        File.WriteAllText(Path.Combine(foreign, "Foreign.esp"), "not ours");
        await using var c = await Server();
        var (err, text) = await Call(c, "housecarl_apply", new() { ["ops"] = Move(74f), ["into"] = foreign });
        // the server answers some refusals as text starting "error:" without the error flag; either counts as refused
        Assert.True(err || text.TrimStart().StartsWith("error:"), "a write into a folder without houseCARL's ownership marker was accepted: " + text);
        // and it is refused as that folder, by its path, for lacking the marker; not as an unknown patch name
        Assert.Contains(foreign, text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("marker", text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("not ours", File.ReadAllText(Path.Combine(foreign, "Foreign.esp")));
    }

    /// <summary>Coordinator finding on 4474f75d: one rule. The server lane, which an MCP client reaches directly, refuses
    /// every folder rule 3.2 refuses: here the instance root, the profiles folder, a new folder in the instance, and the
    /// game folder outside Data.</summary>
    [Fact]
    public async Task B3f_the_server_lane_refuses_the_instance_root_and_the_game_root_as_rule_3_2_does()
    {
        var game = Path.GetDirectoryName(_w.DataDir)!;
        await using var c = await Server();
        foreach (var bad in new[] { _w.Instance, Path.Combine(_w.Instance, "profiles"), Path.Combine(_w.Instance, "NewOut"), game, Path.Combine(game, "Other") })
        {
            var (err, text) = await Call(c, "housecarl_apply", new() { ["ops"] = Move(76f), ["patch"] = "WbRoot", ["out_path"] = bad });
            Assert.True((err || text.TrimStart().StartsWith("error:")) && !text.Contains("unknown parameter"), $"out_path {bad} was not refused: {text}");
        }
        Assert.Empty(Directory.GetFiles(_w.Root, "WbRoot*.esp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task B3e_an_out_path_in_the_games_Data_or_an_existing_mod_folder_is_refused_by_the_server()
    {
        await using var c = await Server();
        foreach (var bad in new[] { _w.DataDir, Path.Combine(_w.Instance, "mods", "WorldMod") })
        {
            var (err, text) = await Call(c, "housecarl_apply", new() { ["ops"] = Move(75f), ["patch"] = "WbBad", ["out_path"] = bad });
            Assert.True((err || text.TrimStart().StartsWith("error:")) && !text.Contains("unknown parameter"), $"out_path {bad} was not refused by the lane's own rule: {text}");
        }
        Assert.Empty(PatchesUnderMods("WbBad"));
        Assert.Empty(Directory.GetFiles(_w.DataDir, "WbBad*.esp", SearchOption.AllDirectories));
    }

    [Fact]
    public void B3c_the_write_guard_is_no_longer_a_lab_constant()
    {
        Assert.Null(typeof(Program).GetField("LabInstance", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public));
        var start = typeof(PatchWriter).GetMethod("StartAsync", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!;
        Assert.DoesNotContain(start.GetParameters(), p => p.Name == "allowedInstances");
    }
}

/// <summary>B8 (D4 frozen, "Expand into rows"): one Findings reader per json-output probe. FindingReaders.Read(checkId,
/// path) unpacks every entry into one row; a row with a reference and a cell is clickable, one without says why. The
/// fixtures have the probes' real entry shapes (receipts/s3-json-entry-shapes.txt) with synthetic names.</summary>
public sealed class FindingReaderTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "hc-wb-readers-" + Guid.NewGuid().ToString("N"));
    public FindingReaderTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    List<object> Read(string checkId, string json)
    {
        var p = Path.Combine(_dir, checkId + ".json");
        File.WriteAllText(p, json);
        var rows = Contract.Static("FindingReaders", "Read", $"the Findings reader for {checkId} (GATE-B.md section 2)", checkId, p);
        return ((System.Collections.IEnumerable)rows!).Cast<object>().ToList();
    }

    static void Clickable(object row, string refKey, string cell)
    {
        Assert.True((bool)Contract.Prop(row, "Clickable")!, "a row naming a reference and a cell must open the viewer");
        Assert.Equal(refKey, (string?)Contract.Prop(row, "RefKey"));
        Assert.Equal(cell, (string?)Contract.Prop(row, "Cell"));
    }

    static void NotClickable(object row)
    {
        Assert.False((bool)Contract.Prop(row, "Clickable")!);
        var why = (string?)Contract.Prop(row, "WhyNot");
        Assert.False(string.IsNullOrWhiteSpace(why), "a row that cannot open the viewer must say why");
    }

    [Fact]
    public void Dep_rows_one_per_entry_clickable_at_the_dependent()
    {
        var rows = Read("geometry.declared-dependencies", """
            {"rows":[{"host":"000801:Syn.esp","dependent":"000802:Syn.esp","kind":"k","verdict":"left-behind","cell":"SynCell","cellKey":"000900:Syn.esp","why":"w"},
                     {"host":"000803:Syn.esp","dependent":"000804:Syn.esp","kind":"k","verdict":"ok","cell":null,"cellKey":null,"why":"w"}],
             "withheldRows":[],"modPairs":[]}
            """);
        Assert.Equal(2, rows.Count);
        Clickable(rows[0], "000802:Syn.esp", "000900:Syn.esp");
        NotClickable(rows[1]);
    }

    [Fact]
    public void Bos_rows_one_per_entry()
    {
        var rows = Read("geometry.bos-overlay", """
            {"profile":"P","plugins":2,"inis":["x_SWAP.ini"],"rows":[{"formKey":"000801:Syn.esp","cell":"000900:Syn.esp","status":"swapped","recordBase":"000700:Syn.esp","effectiveBase":"000701:Syn.esp"}]}
            """);
        Assert.Single(rows);
        Clickable(rows[0], "000801:Syn.esp", "000900:Syn.esp");
    }

    [Fact]
    public void Route_cut_rows_one_per_entry()
    {
        var rows = Read("geometry.route-cut", """
            {"Probe":"blockprobe","Schema":"1","Targets":["Syn.esp"],"Population":"p","Tally":{},
             "Rows":[{"Verdict":"Blocked","FormKey":"000801:Syn.esp","Cell":"000900:Syn.esp","Mesh":"m.nif","By":[],"Why":"w"},
                     {"Verdict":"None","FormKey":"000802:Syn.esp","Cell":"000900:Syn.esp","Mesh":"m.nif","By":[],"Why":"w"}]}
            """);
        Assert.Equal(2, rows.Count);
        Clickable(rows[0], "000801:Syn.esp", "000900:Syn.esp");
    }

    [Fact]
    public void Switch_census_rows_one_per_switch_and_joint_flips_say_why_not()
    {
        var rows = Read("state.switch-census", """
            {"summary":{},"perPlugin":[],"switches":[{"key":"000801:Syn.esp","edid":"SynSwitch","cell":"SynCell","cellKey":"000900:Syn.esp","position":"0,0,0"}],
             "jointFlips":[{"stage":"SynQuest 10","switches":["000801:Syn.esp"]}]}
            """);
        Assert.Equal(2, rows.Count);
        Clickable(rows[0], "000801:Syn.esp", "000900:Syn.esp");
        NotClickable(rows[1]);
    }

    [Theory]
    [InlineData("patch.discovery-installed")]
    [InlineData("patch.discovery-fomod")]
    [InlineData("patch.discovery-pool")]
    public void Discovery_hits_one_row_per_hit_and_per_unjudgeable_item_none_clickable(string check)
    {
        var rows = Read(check, """
            {"schemaVersion":1,"probe":"p","mode":"pair","complete":true,"counts":{},
             "hits":[{"file":"SynPatch.esp","kind":"plugin","where":{},"masters":["Syn.esp"]},{"file":"SynPatch2.esp","kind":"plugin","where":{},"masters":[]}],
             "unjudgeableItems":[{"what":"an archive","why":"unreadable"}],"detail":{}}
            """);
        Assert.Equal(3, rows.Count);
        foreach (var r in rows) NotClickable(r);
    }

    [Fact]
    public void Scope_rows_for_walk_failures_unreadable_records_and_unmeasured_extents()
    {
        var rows = Read("loadability.masters-and-order", """
            {"complete":false,"walkFailures":["a walk failure"],"unreadableRecords":[{"FormKey":"000801:Syn.esp","why":"bad"}],
             "unmeasured":[{"extent":"e","why":"w"}],"targets":[]}
            """);
        Assert.Equal(3, rows.Count);
        foreach (var r in rows) NotClickable(r);     // no cell is known for an unreadable record
    }

    [Fact]
    public void Author_advice_rows_one_per_clash_rule_statement_and_warning()
    {
        var rows = Read("patch.author-advice", """
            {"modId":1,"clashes":[{"with":"Other"}],"rules":[{"r":1}],"statements":[{"s":1}],"resolutions":[],"requirements":[],"warnings":["w"]}
            """);
        Assert.Equal(4, rows.Count);
        foreach (var r in rows) NotClickable(r);
    }

    [Fact]
    public void Empty_space_sidecar_rows_one_per_row()
    {
        var rows = Read("geometry.fixture-empty-space", """
            {"Schema":"1","State":"baseline","Parameters":{},"Counts":{},"Absent":0,"Population":"p",
             "Rows":[{"FormKey":"000801:Syn.esp","Cell":"000900:Syn.esp"}]}
            """);
        Assert.Single(rows);
        Clickable(rows[0], "000801:Syn.esp", "000900:Syn.esp");
    }
}

/// <summary>B11b, B12: the default arm is the plan's review arm, and houseCARL mode asks the server a client runs which
/// instance it has, never reading a config file itself.</summary>
public sealed class ManualModeBindingTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "hc-wb-bind-" + Guid.NewGuid().ToString("N"));
    public ManualModeBindingTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    string Plan(string? reviewArm)
    {
        var d = Path.Combine(_dir, "b-" + (reviewArm ?? "none"));
        Directory.CreateDirectory(d);
        var plan = new JsonObject { ["planId"] = "p", ["arms"] = new JsonArray(new JsonObject { ["armId"] = "baseline", ["kind"] = "baseline" },
                                                                                  new JsonObject { ["armId"] = "candidate-01", ["kind"] = "candidate" }) };
        if (reviewArm is not null) plan["reviewArm"] = reviewArm;
        File.WriteAllText(Path.Combine(d, "plan.json"), plan.ToJsonString());
        return d;
    }

    [Fact]
    public void B11b_the_default_arm_is_the_plans_review_arm_and_baseline_without_one()
    {
        Assert.Equal("candidate-01", (string?)Contract.Static("BundleConflicts", "DefaultArm", "the default arm from plan.reviewArm (DESIGN R2.1)", Plan("candidate-01")));
        Assert.Equal("baseline", (string?)Contract.Static("BundleConflicts", "DefaultArm", "the default arm from plan.reviewArm (DESIGN R2.1)", Plan(null)));
    }

    [Fact]
    public async Task B12_houseCARL_mode_takes_the_instance_the_server_itself_reports()
    {
        using var a = new TiltedRefWorld();
        var src = Path.Combine(R3Bundle.Checkout(), "src", "housecarl-mcp", "bin", "Release", "net9.0");
        Assert.True(File.Exists(Path.Combine(src, "housecarl-mcp.exe")), "build src/housecarl-mcp in Release first");
        var server = Path.Combine(_dir, "server");
        foreach (var f in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            var t = Path.Combine(server, Path.GetRelativePath(src, f));
            Directory.CreateDirectory(Path.GetDirectoryName(t)!);
            File.Copy(f, t);
        }
        // The server's own saved config (beside its exe) names instance A; a stray config elsewhere names B.
        File.WriteAllText(Path.Combine(server, "houseCARL.user.json"), JsonSerializer.Serialize(new { Mo2InstanceDir = a.Instance }));
        var stray = Path.Combine(_dir, "stray");
        Directory.CreateDirectory(stray);
        File.WriteAllText(Path.Combine(stray, "houseCARL.user.json"), JsonSerializer.Serialize(new { Mo2InstanceDir = Path.Combine(_dir, "B") }));
        File.WriteAllText(Path.Combine(a.Instance, "ModOrganizer.ini"), "[General]\r\ngameName=Skyrim Special Edition\r\ngamePath=@ByteArray(" + Path.GetDirectoryName(a.DataDir)!.Replace("\\", "\\\\") + ")\r\nselected_profile=@ByteArray(Default)\r\n");
        var task = (Task)Contract.Static("HousecarlMode", "ReportedInstanceAsync", "houseCARL mode's read of the server's own instance (DESIGN R2.8)",
                                         Path.Combine(server, "housecarl-mcp.exe"))!;
        await task;
        var got = (string?)task.GetType().GetProperty("Result")!.GetValue(task);
        Assert.Equal(Path.GetFullPath(a.Instance).TrimEnd('\\'), Path.GetFullPath(got!).TrimEnd('\\'));
    }

    [Fact]
    public void B12b_with_no_server_found_houseCARL_mode_refuses_in_one_sentence()
    {
        var ex = Assert.ThrowsAny<InvalidOperationException>(() => Contract.Static("HousecarlMode", "Locate", "houseCARL mode's server location (DESIGN R2.8)",
            (string?)null, Path.Combine(_dir, "no-claude.json"), _dir));
        Assert.DoesNotContain("\n", ex.Message);
        Assert.Contains("WB_HOUSECARL_SERVER", ex.Message);
    }
}

/// <summary>B11: the window and its state server exist before any scene. workbench.exe started with no bundle answers
/// <c>workbench_state project=launcher</c> on WB_MCP_PORT.</summary>
[Trait("tier", "integration")]
public sealed class LauncherProcessTests
{
    static string WorkbenchExe()
    {
        var here = Path.Combine(AppContext.BaseDirectory, "workbench.exe");
        return File.Exists(here) ? here : Path.Combine(R3Bundle.Checkout(), "_geometry-harness", "workbench", "bin", "Release", "net9.0-windows10.0.19041.0", "workbench.exe");
    }

    [Fact]
    public async Task B11_workbench_with_no_bundle_serves_the_launcher_before_any_scene()
    {
        var exe = WorkbenchExe();
        Assert.True(File.Exists(exe), $"build the workbench first: {exe}");
        var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); int port = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop();
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        psi.Environment["WB_MCP_PORT"] = port.ToString();
        psi.Environment["WB_OUT"] = Path.Combine(Path.GetTempPath(), "hc-wb-launcher-" + Guid.NewGuid().ToString("N"));
        using var p = Process.Start(psi)!;
        var output = new System.Text.StringBuilder();
        p.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };
        p.BeginOutputReadLine(); p.BeginErrorReadLine();
        try
        {
            string? text = null;
            var clock = Stopwatch.StartNew();
            while (text is null && clock.Elapsed < TimeSpan.FromSeconds(60) && !p.HasExited)
            {
                try
                {
                    await using var c = await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
                    { Endpoint = new Uri($"http://127.0.0.1:{port}/"), TransportMode = HttpTransportMode.StreamableHttp }));
                    var r = await c.CallToolAsync("workbench_state", new Dictionary<string, object?> { ["project"] = new[] { "launcher" } });
                    text = string.Join("\n", r.Content.OfType<TextContentBlock>().Select(b => b.Text));
                    Assert.False(r.IsError == true, "workbench_state launcher refused: " + text);
                }
                catch (System.Net.Http.HttpRequestException) { await Task.Delay(500); }
            }
            string log; lock (output) log = output.ToString();
            Assert.True(text is not null, $"no launcher answered on port {port} (exited: {p.HasExited}, exit {(p.HasExited ? p.ExitCode : 0)}): {log[^Math.Min(600, log.Length)..]}");
            using var d = JsonDocument.Parse(text!);
            Assert.True(d.RootElement.TryGetProperty("launcher", out _), text);
        }
        finally { if (!p.HasExited) p.Kill(entireProcessTree: true); }
    }
}


/// <summary>M2 (Gate B design review, decided under the standing rule): the run's work folder (bundle, logs, candidates,
/// re-checks) never goes under the instance's mods folder or anywhere MO2 would list it as a mod; only a patch's own new
/// "houseCARL - name" folder may land there. WorkFolderRule.Check(work, instance, profile, game) returns null or one
/// sentence; the default work folder is outside the instance.</summary>
public sealed class WorkFolderRuleTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "hc-wb-workrule-" + Guid.NewGuid().ToString("N"));
    string Inst => Path.Combine(_root, "instance");
    string Game => Path.Combine(_root, "game");
    public WorkFolderRuleTests()
    {
        foreach (var d in new[] { "mods/Enabled", "overwrite", "profiles/P" }) Directory.CreateDirectory(Path.Combine(Inst, d));
        Directory.CreateDirectory(Path.Combine(Game, "Data"));
        File.WriteAllText(Path.Combine(Inst, "profiles", "P", "modlist.txt"), "+Enabled\r\n");
    }
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    string? Check(string w) => (string?)Contract.Static("WorkFolderRule", "Check", "the work-folder rule (GATE-B.md 3.1)", w, Inst, "P", Game);

    [Theory]
    [InlineData("instance/mods")]
    [InlineData("instance/mods/workbench-runs")]
    [InlineData("instance/mods/Enabled")]
    [InlineData("instance/overwrite")]
    [InlineData("instance/profiles/P")]
    [InlineData("instance")]
    [InlineData("game")]
    [InlineData("game/Data")]
    public void A_work_folder_inside_the_instance_or_the_game_is_refused(string rel)
    {
        var why = Check(Path.Combine(_root, rel));
        Assert.False(why is null, $"{rel} was allowed as a work folder");
        Assert.DoesNotContain("\n", why);
    }

    [Fact]
    public void A_work_folder_outside_the_instance_is_allowed() => Assert.Null(Check(Path.Combine(_root, "elsewhere", "work")));

    // Coordinator ruling (2026-11-14, under the standing rule; Piranha's "any output can be written to the user's chosen
    // location"): with the Output Folder outside the instance the Work Folder defaults to its "workbench-runs" subfolder;
    // with the Output Folder at <instance>/mods there is no default, and the launcher refuses until the user chooses one.
    // Never %LOCALAPPDATA%.
    [Fact]
    public void With_the_output_outside_the_instance_the_default_work_folder_is_its_workbench_runs_subfolder()
    {
        var o = Path.Combine(_root, "elsewhere", "out");
        var d = (string?)Contract.Static("WorkFolderRule", "Default", "the default work folder (GATE-B.md 3.1)", o, Inst);
        Assert.Equal(Path.GetFullPath(Path.Combine(o, "workbench-runs")), Path.GetFullPath(d!));
        Assert.Null(Check(d!));
    }

    [Fact]
    public void With_the_output_at_the_instances_mods_folder_there_is_no_default_and_the_reason_is_one_sentence()
    {
        var ex = Assert.ThrowsAny<InvalidOperationException>(() =>
            Contract.Static("WorkFolderRule", "Default", "the default work folder (GATE-B.md 3.1)", Path.Combine(Inst, "mods"), Inst));
        Assert.DoesNotContain("\n", ex.Message);
        Assert.Contains("Work Folder", ex.Message);
        Assert.DoesNotContain("AppData", ex.Message);
    }
}

/// <summary>H2 (Gate B design review): every cell a clickable Findings row names is loadable in the session, and a
/// bundle with Findings rows and no Conflicts rows opens. SessionCells.From(conflictCells, findingCells) is the list of
/// cells the worklist carries (today Program.OpenBundle builds it from Conflicts rows only and throws without one).</summary>
public sealed class SessionCellTests
{
    static IReadOnlyList<string> From(string?[] conflicts, string?[] findings) =>
        (IReadOnlyList<string>)Contract.Static("SessionCells", "From", "the session's loadable cells from both tabs (DESIGN R2.16)",
            (IEnumerable<string?>)conflicts, (IEnumerable<string?>)findings)!;

    [Fact]
    public void A_cell_only_a_Findings_row_names_is_loadable_and_a_click_into_it_is_planned_not_refused()
    {
        var cells = From(new[] { "000A01:Syn.esm" }, new[] { "000A02:Syn.esm", null });
        Assert.Contains("000A02:Syn.esm", cells);
        Assert.Equal("000A01:Syn.esm", cells[0]);                          // the Conflicts tab's top row still opens first
        var plan = ClickThrough.Plan("000801:Syn.esm", "000A02:Syn.esm", "000A01:Syn.esm", cells, 0, _ => false);
        Assert.IsType<ClickPlan.Switch>(plan);
    }

    [Fact]
    public void A_bundle_with_Findings_rows_and_no_Conflicts_rows_has_a_cell_to_open()
    {
        var cells = From(Array.Empty<string?>(), new[] { "000A02:Syn.esm" });
        Assert.Equal(new[] { "000A02:Syn.esm" }, cells);
    }
}

/// <summary>M6 and L2 on real outputs. The probes' real outputs carry load-order names, so they are not tracked
/// fixtures: WB_READER_OUTPUTS names a run-local evaluation folder (run s3 of the task) and the tests read its receipts.
/// Without it the test fails saying so (it is a known answer for a real run, not a skip).</summary>
[Trait("tier", "real-run")]
public sealed class RealOutputReaderTests
{
    static string Run() => Environment.GetEnvironmentVariable("WB_READER_OUTPUTS") is { Length: > 0 } r && Directory.Exists(r) ? r
        : throw new Xunit.Sdk.XunitException("set WB_READER_OUTPUTS to an evaluation run folder (its receipts/ and outputs/) to read real probe outputs");

    [Theory]
    [InlineData("geometry.declared-dependencies", "candidate-01")]
    [InlineData("state.switch-census", "candidate-01")]
    [InlineData("patch.discovery-pool", "baseline")]
    [InlineData("patch.discovery-fomod", "baseline")]
    [InlineData("loadability.masters-and-order", "candidate-01")]
    [InlineData("geometry.route-cut", "candidate-01")]
    [InlineData("patch.author-advice", "baseline")]
    public void Each_reader_gives_as_many_finding_rows_as_the_receipt_counts_and_marks_an_incomplete_output(string check, string arm)
    {
        var run = Run();
        using var r = JsonDocument.Parse(File.ReadAllText(Path.Combine(run, "receipts", $"{check}__{arm}.json")));
        var findings = r.RootElement.GetProperty("result").GetProperty("findings");
        var output = r.RootElement.GetProperty("outputs")[0].GetProperty("path").GetString()!;
        var rows = ((System.Collections.IEnumerable)Contract.Static("FindingReaders", "Read", $"the Findings reader for {check}", check, Path.Combine(run, output))!).Cast<object>().ToList();
        var findingRows = rows.Where(x => (string?)Contract.Prop(x, "Role") == "finding").ToList();
        if (findings.ValueKind == JsonValueKind.Number) Assert.Equal(findings.GetInt32(), findingRows.Count);
        // an output that says it is incomplete (complete false, unreadable[], sourcesNotSearched[], limits[]) shows so
        using var o = JsonDocument.Parse(File.ReadAllText(Path.Combine(run, output)));
        bool incomplete = (o.RootElement.TryGetProperty("complete", out var c) && c.ValueKind == JsonValueKind.False)
            || new[] { "unreadable", "sourcesNotSearched", "unreadableRecords", "walkFailures" }.Any(k => o.RootElement.TryGetProperty(k, out var a) && a.ValueKind == JsonValueKind.Array && a.GetArrayLength() > 0);
        if (incomplete) Assert.Contains(rows, x => (string?)Contract.Prop(x, "Role") == "incomplete");
    }

    [Fact]
    public void L2_new_on_the_review_arm_counts_what_the_diff_receipt_counts()
    {
        var run = Run();
        using var d = JsonDocument.Parse(File.ReadAllText(Path.Combine(run, "receipts", "geometry.object-overlap__candidate-01__diff.json")));
        // ordersweep's own counts: in s3, created 19, of which newDefect 2 and unmeasured 17 (receipts/s5-derived-checks.txt
        // has the same split). "New on the review arm" is every created row; the credit splits it the same way.
        var created = d.RootElement.GetProperty("orderEvaluation").GetProperty("created");
        var baseEmit = Path.Combine(run, "sweep", "inv-0001", "overlapprobe", "baseline.json");
        var review = Path.Combine(run, "sweep", "inv-0001", "overlapprobe", "candidate-01.json");
        var n = Contract.Static("ReviewCredit", "Counts", "new-on-the-review-arm from DifferentialRows (DESIGN R2.1)", baseEmit, review)!;
        Assert.Equal(created.GetProperty("created").GetInt32(), (int)Contract.Prop(n, "Created")!);
        Assert.Equal(created.GetProperty("newDefect").GetInt32(), (int)Contract.Prop(n, "NewDefect")!);
        Assert.Equal(created.GetProperty("unmeasured").GetInt32(), (int)Contract.Prop(n, "Unmeasured")!);
    }
}

/// <summary>B4 (F11, M2): an in-place run through the bundle stage on a synthetic MO2 world, with the real probes and
/// the pipeline: nothing is written under the instance or the game folder, and every new file is under the work folder
/// (or the system temp folder). The selection is a mod folder whose plugin places one reference in the master's cell.</summary>
[Trait("tier", "integration")]
public sealed class InPlaceRunTests
{
    static Dictionary<string, (long, DateTime)> Listing(params string[] roots) =>
        roots.SelectMany(r => Directory.EnumerateFileSystemEntries(r, "*", SearchOption.AllDirectories))
             .ToDictionary(p => p, p => File.Exists(p) ? (new FileInfo(p).Length, File.GetLastWriteTimeUtc(p)) : (-1L, Directory.GetLastWriteTimeUtc(p)));

    [Fact]
    public void B4_an_in_place_run_through_the_bundle_writes_only_under_its_work_folder()
    {
        using var w = new TiltedRefWorld();
        var sel = new ModKey("HcWbSel", ModType.Plugin);
        var m = new SkyrimMod(sel, SkyrimRelease.SkyrimSE);
        using (var master = SkyrimMod.CreateFromBinaryOverlay(Path.Combine(w.Instance, "mods", "WorldMod", TiltedRefWorld.Master.FileName), SkyrimRelease.SkyrimSE))
        {
            var cellGetter = master.EnumerateMajorRecords<ICellGetter>().Single();
            var cell = cellGetter.DeepCopy();
            cell.Temporary.Clear();
            cell.Temporary.Add(new PlacedObject(new FormKey(sel, 0x800), SkyrimRelease.SkyrimSE)
            {
                Base = new FormLinkNullable<IPlaceableObjectGetter>(new FormKey(TiltedRefWorld.Master, 0x801)),
                Placement = new Placement { Position = new P3Float(512, -256, 64), Rotation = new P3Float(0, 0, 0) },
            });
            var block = new CellBlock { BlockNumber = 2, GroupType = GroupTypeEnum.InteriorCellBlock };
            var sub = new CellSubBlock { BlockNumber = 0, GroupType = GroupTypeEnum.InteriorCellSubBlock };
            sub.Cells.Add(cell); block.SubBlocks.Add(sub); m.Cells.Records.Add(block);
            m.ModHeader.MasterReferences.Add(new MasterReference { Master = TiltedRefWorld.Master });
            var dir = Path.Combine(w.Instance, "mods", "SelMod"); Directory.CreateDirectory(dir);
            m.BeginWrite.ToPath(Path.Combine(dir, sel.FileName.String)).WithLoadOrder(master).Write();
        }
        var prof = Path.Combine(w.Instance, "profiles", "Default");
        File.WriteAllText(Path.Combine(prof, "loadorder.txt"), TiltedRefWorld.Master.FileName + "\r\n" + sel.FileName + "\r\n");
        File.WriteAllText(Path.Combine(prof, "plugins.txt"), "*" + TiltedRefWorld.Master.FileName + "\r\n*" + sel.FileName + "\r\n");
        File.WriteAllText(Path.Combine(prof, "modlist.txt"), "+SelMod\r\n+WorldMod\r\n");
        File.WriteAllText(Path.Combine(w.Instance, "ModOrganizer.ini"), "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\n");
        Directory.CreateDirectory(Path.Combine(w.Instance, "overwrite"));
        var game = Path.GetDirectoryName(w.DataDir)!;
        var work = Path.Combine(w.Root, "work");
        var entry = Path.Combine(w.Root, "entry.json");
        File.WriteAllText(entry, JsonSerializer.Serialize(new
        {
            schema = 1, id = "wb-b4", inPlace = new { instance = w.Instance, profile = "Default", game },
            selection = new { mods = new[] { "SelMod" } },
            evaluation = new { select = new[] { "geometry.object-overlap" }, depth = "standard" },
        }));
        var machine = Path.Combine(w.Root, "machine.json");
        File.WriteAllText(machine, JsonSerializer.Serialize(new
        {
            readerCmd = "\"" + Path.Combine(R3Bundle.Checkout(), "src", "housecarl-mcp", "bin", "Release", "net9.0", "housecarl-mcp.exe") + "\"",
        }));
        var decoy = Path.Combine(w.Root, "decoy-runlog"); Directory.CreateDirectory(decoy);
        var before = Listing(w.Root);                                 // review M2: the whole world root, not only instance and game
        var psi = new ProcessStartInfo("python") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { "-I", Path.Combine(R3Bundle.Checkout(), "_geometry-harness", "evalpipeline.py"), "run", "--entry", entry,
                                  "--machine", machine, "--out", work, "--through", "bundle" }) psi.ArgumentList.Add(a);
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        psi.Environment["HOUSECARL_RUNLOG_DIR"] = decoy;              // the run must set its own, under the work folder (not setdefault)
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEndAsync(); var e = p.StandardError.ReadToEndAsync();
        p.WaitForExit();
        var text = o.Result + e.Result;
        var after = Listing(w.Root);
        bool Under(string path, string dir) => Path.GetFullPath(path).StartsWith(Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                                             || string.Equals(Path.GetFullPath(path), Path.GetFullPath(dir), StringComparison.OrdinalIgnoreCase);
        var changed = before.Keys.Union(after.Keys).Where(k => !before.TryGetValue(k, out var b) || !after.TryGetValue(k, out var a) || b != a)
                            .Where(k => !Under(k, work) && !string.Equals(Path.GetFullPath(k), Path.GetFullPath(w.Root), StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.True(changed.Count == 0, $"the run wrote outside its work folder: {string.Join("; ", changed.Take(5))}");
        Assert.True(p.ExitCode == 0, $"evalpipeline run --through bundle exited {p.ExitCode}: {text[^Math.Min(800, text.Length)..]}");
        Assert.Empty(Directory.EnumerateFileSystemEntries(decoy));
        var bundle = Path.Combine(work, "bundle", "evidence");
        Assert.True(File.Exists(Path.Combine(bundle, "manifest.json")), "no bundle under the work folder");
        using var plan = JsonDocument.Parse(File.ReadAllText(Path.Combine(bundle, "plan.json")));
        Assert.Equal("candidate-01", plan.RootElement.GetProperty("reviewArm").GetString());
        var baseArm = plan.RootElement.GetProperty("arms").EnumerateArray().Single(a => a.GetProperty("kind").GetString() == "baseline");
        Assert.DoesNotContain(baseArm.GetProperty("plugins").EnumerateArray(), x => x.GetString() == sel.FileName.String);
        var names = Directory.GetFiles(bundle, "names.json", SearchOption.AllDirectories).Single();
        Assert.DoesNotContain("000800:HcWbSel.esp", File.ReadAllText(names).Split("\"unread\"")[1], StringComparison.OrdinalIgnoreCase);
    }
}


/// <summary>Review M4: the output rule on layouts other than the plain one: an MO2 instance inside the game folder (a
/// portable install), and an instance whose ModOrganizer.ini sets base_directory (mods and profiles elsewhere), read
/// through the existing owner (housecarl-core Mo2Instance).</summary>
public sealed class OutputFolderLayoutTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "hc-wb-layout-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    string? Check(string o, string inst, string game) => (string?)Contract.Static("OutputFolderRule", "Check", "the output-folder rule (GATE-B.md 3.2)", o, inst, "P", game);

    [Fact]
    public void An_instance_inside_the_game_folder_still_allows_its_mods_folder_and_refuses_Data()
    {
        var game = Path.Combine(_root, "game"); var inst = Path.Combine(game, "MO2");
        foreach (var d in new[] { "mods/Enabled", "profiles/P", "overwrite" }) Directory.CreateDirectory(Path.Combine(inst, d));
        Directory.CreateDirectory(Path.Combine(game, "Data"));
        File.WriteAllText(Path.Combine(inst, "profiles", "P", "modlist.txt"), "+Enabled\r\n");
        Assert.Null(Check(Path.Combine(inst, "mods"), inst, game));
        Assert.NotNull(Check(Path.Combine(game, "Data"), inst, game));
        Assert.NotNull(Check(Path.Combine(game, "Other"), inst, game));
    }

    [Fact]
    public void A_base_directory_instance_is_read_through_its_ini()
    {
        var inst = Path.Combine(_root, "inst"); var bd = Path.Combine(_root, "basedir"); var game = Path.Combine(_root, "game");
        foreach (var d in new[] { "mods/Enabled", "profiles/P", "overwrite" }) Directory.CreateDirectory(Path.Combine(bd, d));
        Directory.CreateDirectory(inst); Directory.CreateDirectory(Path.Combine(game, "Data"));
        File.WriteAllText(Path.Combine(bd, "profiles", "P", "modlist.txt"), "+Enabled\r\n");
        File.WriteAllText(Path.Combine(inst, "ModOrganizer.ini"), "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(P)\r\n[Settings]\r\nbase_directory=" + bd.Replace("\\", "/") + "\r\n");
        Assert.NotNull(Check(Path.Combine(bd, "mods", "Enabled"), inst, game));
        Assert.Null(Check(Path.Combine(bd, "mods"), inst, game));
    }
}
