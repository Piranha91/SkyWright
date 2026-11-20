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

/// <summary>The workbench's manual mode. Tests that name a type or
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

/// <summary>B2: OutputFolderRule.Check(output, instance, profile, game)
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

    string? Check(string output) => (string?)Contract.Static("OutputFolderRule", "Check", "the output-folder rule",
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
    [InlineData("instance/NewOut", "instance")]          // inside the instance, on neither list
    [InlineData("instance/downloads", "instance")]
    [InlineData("instance/profiles/Other", "profiles")]
    [InlineData("instance/overwrite/sub", "overwrite")]
    public void A_folder_the_rule_refuses_is_refused_in_one_sentence_naming_why(string rel, string word)
    {
        var why = Check(Path.Combine(_root, rel));
        Assert.False(why is null, $"{rel} was allowed");
        Assert.DoesNotContain("\n", why);
        Assert.EndsWith(".", why);
        // the reason, not the quoted path, names it: the path itself often holds the word
        Assert.Contains(word, why![(why.LastIndexOf('\'') + 1)..], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_relative_path_is_refused() => Assert.NotNull(Check("relative/out"));

    [Theory]
    [InlineData("instance/mods/Enabled/")]
    [InlineData("INSTANCE/MODS/ENABLED")]
    [InlineData("instance/mods/x/../Enabled")]
    [InlineData("game/data")]
    public void A_refused_folder_spelled_another_way_is_still_refused(string rel) =>    // 
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
        // read the patch back after the undo: the reference's override is gone
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

    /// <summary>one rule. The server lane, which an MCP client reaches directly, refuses
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

    /// <summary>the server's out_path lane and the workbench's Output Folder
    /// rule are one rule with one owner. On the same folders both allow the same ones, and every refusal the server gives
    /// carries the workbench rule's own sentence, so the two callers cannot disagree.</summary>
    [Fact]
    public async Task B3g_the_server_lane_and_the_workbench_rule_give_one_verdict_and_one_sentence()
    {
        var game = Path.GetDirectoryName(_w.DataDir)!;
        var mods = Path.Combine(_w.Instance, "mods");
        await using var c = await Server();
        foreach (var o in new[] { _w.DataDir, Path.Combine(_w.DataDir, "meshes"), Path.Combine(mods, "WorldMod"), Path.Combine(mods, "NewSub"),
                                  Path.Combine(_w.Instance, "overwrite"), Path.Combine(_w.Instance, "profiles", "Default"), Path.Combine(_w.Instance, "profiles"),
                                  Path.Combine(_w.Instance, "NewOut"), _w.Instance, game, Path.Combine(game, "Other"), mods,
                                  Path.Combine(_w.Root, "one-rule-" + Guid.NewGuid().ToString("N")[..6]) })
        {
            var wb = (string?)Contract.Static("OutputFolderRule", "Check", "the output-folder rule", o, _w.Instance, "Default", game);
            var (err, text) = await Call(c, "housecarl_apply", new() { ["ops"] = Move(77f), ["patch"] = "WbOne", ["out_path"] = o });
            bool refused = err || text.TrimStart().StartsWith("error:");
            Assert.True(refused == (wb is not null), $"{o}: the workbench rule {(wb is null ? "allows it" : "refuses it")} but the server {(refused ? "refused" : "wrote")}: {text}");
            if (wb is not null) Assert.True(text.Contains(wb), $"{o}: the server's refusal is not the rule's sentence.\n  rule:   {wb}\n  server: {text}");
        }
    }

    /// <summary>With out_path at the mods folder itself a new patch keeps the default lane's name rules: a stem an active
    /// plugin already has is refused or suffixed exactly as it is without out_path.</summary>
    [Fact]
    public async Task B3h_out_path_at_the_mods_folder_keeps_the_default_lanes_name_collision_rule()
    {
        var shadow = new ModKey("WbShadow", ModType.Plugin);
        var dir = Path.Combine(_w.Instance, "mods", "ShadowMod");
        if (!Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
            new SkyrimMod(shadow, SkyrimRelease.SkyrimSE).BeginWrite.ToPath(Path.Combine(dir, shadow.FileName.String)).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
            var prof = Path.Combine(_w.Instance, "profiles", "Default");
            File.AppendAllText(Path.Combine(prof, "plugins.txt"), "*" + shadow.FileName + "\r\n");
            File.AppendAllText(Path.Combine(prof, "loadorder.txt"), shadow.FileName + "\r\n");
            File.AppendAllText(Path.Combine(prof, "modlist.txt"), "+ShadowMod\r\n");
        }
        await using var c = await Server();
        var plain = await Call(c, "housecarl_apply", new() { ["ops"] = Move(78f), ["patch"] = "WbShadow", ["dry_run"] = true });
        var withOut = await Call(c, "housecarl_apply", new() { ["ops"] = Move(78f), ["patch"] = "WbShadow", ["dry_run"] = true, ["out_path"] = Path.Combine(_w.Instance, "mods") });
        bool Refused((bool Error, string Text) r) => r.Error || r.Text.TrimStart().StartsWith("error:");
        string Named((bool Error, string Text) r) => Refused(r) ? "refused" : System.Text.RegularExpressions.Regex.Match(r.Text, @"WbShadow[_0-9]*\.esp").Value;
        // the premise: without out_path the default lane does not hand out the name an active plugin has
        Assert.True(Named(plain) != "WbShadow.esp", "the fixture's active WbShadow.esp did not make the default lane avoid the name: " + plain.Text);
        Assert.Equal(Named(plain), Named(withOut));
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

    // B3c is replaced by ManualWriteTests.H2a, a behaviour test.
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
        var rows = Contract.Static("FindingReaders", "Read", $"the Findings reader for {checkId}", checkId, p);
        return ((System.Collections.IEnumerable)rows!).Cast<object>().ToList();
    }

    static void Clickable(object row, string refKey, string cell)
    {
        Assert.True((bool)Contract.Prop(row, "Clickable")!, "a row naming a reference and a cell must open the viewer");
        Assert.Equal(refKey, (string?)Contract.Prop(row, "RefKey"));
        Assert.Equal(cell, (string?)Contract.Prop(row, "Cell"));
    }

    static void Role(object row, string role) => Assert.Equal(role, (string?)Contract.Prop(row, "Role"));

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
            {"rows":[{"host":"000801:Syn.esp","dependent":"000802:Syn.esp","kind":"ActivateParent","verdict":"LeftBehind","cell":"SynCell (int)","cellKey":"000900:Syn.esp","why":"w"},
                     {"host":"000803:Syn.esp","dependent":"000804:Syn.esp","kind":"ActivateParent","verdict":"LeftBehind","cell":null,"cellKey":null,"why":"w"}],
             "withheldRows":[{"host":"000805:Syn.esp","dependent":"000806:Syn.esp","kind":"ActivateParent","verdict":"LeftBehind","cell":"SynCell (int)","cellKey":"000900:Syn.esp","why":"withheld"}],
             "unreadable":["000807:Syn.esp: the record could not be read"],"modPairs":[]}
            """);
        Assert.Equal(4, rows.Count);
        Clickable(rows[0], "000802:Syn.esp", "000900:Syn.esp");
        Role(rows[0], "finding");
        NotClickable(rows[1]);
        Role(rows[1], "finding");
        Assert.Single(rows, r => (string?)Contract.Prop(r, "Role") == "context");      // the withheld row
        Assert.Single(rows, r => (string?)Contract.Prop(r, "Role") == "incomplete");   // the unreadable entry
    }

    [Fact]
    public void Bos_rows_one_per_entry()
    {
        var rows = Read("geometry.bos-overlay", """
            {"profile":"P","plugins":2,"inis":["x_SWAP.ini"],"rows":[{"formKey":"000801:Syn.esp","cell":"000900:Syn.esp","status":"swapped","recordBase":"000700:Syn.esp","effectiveBase":"000701:Syn.esp"}]}
            """);
        Assert.Single(rows);
        Clickable(rows[0], "000801:Syn.esp", "000900:Syn.esp");
        Role(rows[0], "finding");
    }

    [Fact]
    public void Route_cut_rows_one_per_entry()
    {
        var rows = Read("geometry.route-cut", """
            {"Probe":"blockprobe","Schema":"1","Targets":["Syn.esp"],"Population":"p","Tally":{},
             "Rows":[{"Verdict":"Blocked","FormKey":"000801:Syn.esp","Cell":"000900:Syn.esp","Mesh":"m.nif","By":[],"Why":"w"},
                     {"Verdict":"LikelyBlocked","FormKey":"000802:Syn.esp","Cell":"SynTunnels03","Mesh":"m.nif","By":[],"Why":"w"},
                     {"Verdict":"NoLine","FormKey":"000803:Syn.esp","Cell":"SynTunnels03","Mesh":"m.nif","By":[],"Why":"w"},
                     {"Verdict":"Unjudgeable","FormKey":"000804:Syn.esp","Cell":"SynTunnels03","Mesh":"m.nif","By":[],"Why":"w"}]}
            """);
        Assert.Equal(4, rows.Count);
        Clickable(rows[0], "000801:Syn.esp", "000900:Syn.esp");
        Role(rows[0], "finding");
        // an editor ID for a cell is not a cell key: without the names enrichment the row says so
        NotClickable(rows[1]);
        Assert.Contains("SynTunnels03", (string?)Contract.Prop(rows[1], "WhyNot"));
        Role(rows[1], "finding");
        Role(rows[2], "context");
        Role(rows[3], "incomplete");
        // with the names enrichment's cell for the reference, the same row opens there
        var p = Path.Combine(_dir, "geometry.route-cut.json");
        var resolved = ((System.Collections.IEnumerable)Contract.Static("FindingReaders", "Read", "the route-cut reader with names", "geometry.route-cut", p,
            (Func<string, string?>)(fk => fk == "000802:Syn.esp" ? "000901:Syn.esp" : null))!).Cast<object>().ToList();
        Clickable(resolved[1], "000802:Syn.esp", "000901:Syn.esp");
    }

    [Fact]
    public void Switch_census_rows_one_per_switch_and_joint_flips_say_why_not()
    {
        var rows = Read("state.switch-census", """
            {"summary":{},"perPlugin":[],"switches":[{"key":"000801:Syn.esp","edid":"SynSwitch","cell":"SynCell","cellKey":"000900:Syn.esp","position":"0,0,0"}],
             "jointFlips":[{"stage":"C01:30","switches":["SynSwitchParent"]}],"unreadable":[]}
            """);
        Assert.Equal(2, rows.Count);
        Clickable(rows[0], "000801:Syn.esp", "000900:Syn.esp");
        Role(rows[0], "finding");
        NotClickable(rows[1]);
        Role(rows[1], "context");
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
        Assert.Equal(new[] { "finding", "finding", "incomplete" }, rows.Select(r => (string?)Contract.Prop(r, "Role")));
    }

    /// <summary>B8i: a discovery hit's relevance is read from what finalize recorded (disposition.discovery.relevance
    /// for the check and arm), never re-derived: a relevant hit is a finding, a hit not relevant is context saying why, and
    /// with relevance not computed every hit is shown with the sentence, neither hidden nor called relevant.</summary>
    [Fact]
    public void B8i_discovery_hits_take_their_relevance_from_finalize()
    {
        var p = Path.Combine(_dir, "patch.discovery-pool.json");
        File.WriteAllText(p, """
            {"schemaVersion":1,"probe":"p","mode":"pool","complete":true,"counts":{},
             "hits":[{"file":"SynPatch.esp","kind":"pool-archive","where":{},"masters":["Syn.esp"]},{"file":"SynFar.esp","kind":"pool-archive","where":{},"masters":["Far.esp"]}],
             "unjudgeableItems":[],"detail":{}}
            """);
        List<object> Read(string relevance)
        {
            using var d = JsonDocument.Parse(relevance);
            return ((System.Collections.IEnumerable)Contract.Static("FindingReaders", "Read", "the discovery reader with finalize's relevance",
                "patch.discovery-pool", p, null, d.RootElement.Clone())!).Cast<object>().ToList();
        }
        var computed = Read("""{"computed":true,"relevant":["SynPatch.esp"],"notRelevant":{"count":1,"why":"names no plan target"}}""");
        Assert.Equal(new[] { "finding", "context" }, computed.Select(r => (string?)Contract.Prop(r, "Role")));
        Assert.Contains("names no plan target", (string?)Contract.Prop(computed[1], "Summary"));
        var not = Read("""{"computed":false,"why":"the plan names no target and no pair"}""");
        Assert.Equal(new[] { "finding", "finding" }, not.Select(r => (string?)Contract.Prop(r, "Role")));
        Assert.All(not, r => Assert.Contains("no target and no pair", (string?)Contract.Prop(r, "Summary")));
    }

    [Fact]
    public void An_incomplete_discovery_output_shows_its_unsearched_sources_and_that_it_is_incomplete()
    {
        var rows = Read("patch.discovery-pool", """
            {"schemaVersion":1,"probe":"p","mode":"pair","complete":false,"counts":{},
             "hits":[{"file":"SynPatch.esp","kind":"pool-archive","where":{},"masters":[]}],
             "sourcesNotSearched":[{"kind":"pool","name":"SynDownloads","why":"the folder could not be read"}],"unjudgeableItems":[],"detail":{}}
            """);
        Assert.Equal(1, rows.Count(r => (string?)Contract.Prop(r, "Role") == "finding"));
        Assert.Equal(2, rows.Count(r => (string?)Contract.Prop(r, "Role") == "incomplete"));   // the unsearched source, and complete false
    }

    [Fact]
    public void Scope_rows_for_walk_failures_unreadable_records_and_unmeasured_extents()
    {
        var rows = Read("loadability.masters-and-order", """
            {"complete":false,"walkFailures":["a walk failure"],"unreadableRecords":[{"FormKey":"000801:Syn.esp","why":"bad"}],
             "unmeasured":[{"extent":"e","why":"w"}],"targets":[]}
            """);
        Assert.Equal(4, rows.Count);                 // with complete false, one row says the output is incomplete
        foreach (var r in rows) NotClickable(r);     // no cell is known for an unreadable record
        Assert.Equal(3, rows.Count(r => (string?)Contract.Prop(r, "Role") == "incomplete"));
        Assert.Single(rows, r => (string?)Contract.Prop(r, "Role") == "context");     // the unmeasured extent
    }

    [Fact]
    public void Author_advice_rows_one_per_clash_rule_statement_and_warning()
    {
        var rows = Read("patch.author-advice", """
            {"modId":1,"clashes":[{"with":"Other"}],"rules":[{"r":1}],"statements":[{"s":1}],"resolutions":[],"requirements":[],
             "warnings":["mod 2's page could not be read. What it states about mod 1 is UNKNOWN for this run, not absent."]}
            """);
        Assert.Equal(4, rows.Count);
        foreach (var r in rows) NotClickable(r);
        Assert.Equal(3, rows.Count(r => (string?)Contract.Prop(r, "Role") == "finding"));
        Assert.Single(rows, r => (string?)Contract.Prop(r, "Role") == "incomplete");
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
        Role(rows[0], "finding");
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
        Assert.Equal("candidate-01", (string?)Contract.Static("BundleConflicts", "DefaultArm", "the default arm from plan.reviewArm", Plan("candidate-01")));
        Assert.Equal("baseline", (string?)Contract.Static("BundleConflicts", "DefaultArm", "the default arm from plan.reviewArm", Plan(null)));
    }

    // houseCARL mode: StateToolsTests.H5a and H5b start the server as the client does.

    /// <summary>with no WB_HOUSECARL_LAUNCH and no housecarl entry in
    /// ~/.claude.json, houseCARL mode is refused in one sentence naming the setting that fixes it.</summary>
    [Fact]
    public void H5c_with_no_server_found_houseCARL_mode_refuses_in_one_sentence_naming_WB_HOUSECARL_LAUNCH()
    {
        var claude = Path.Combine(_dir, "claude.json");
        File.WriteAllText(claude, """{"projects":{"C:/elsewhere":{"mcpServers":{"housecarl":{"command":"x.exe"}}}},"mcpServers":{}}""");
        foreach (var cj in new[] { Path.Combine(_dir, "no-claude.json"), claude })
        {
            var ex = Assert.ThrowsAny<InvalidOperationException>(() => Contract.Static("HousecarlMode", "Locate", "houseCARL mode's server launch",
                (string?)null, cj, _dir));
            Assert.DoesNotContain("\n", ex.Message);
            Assert.Contains("WB_HOUSECARL_LAUNCH", ex.Message);
        }
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


/// <summary>the run's work folder (bundle, logs, candidates,
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

    string? Check(string w) => (string?)Contract.Static("WorkFolderRule", "Check", "the work-folder rule", w, Inst, "P", Game);

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

    // With the Output Folder outside the instance the Work Folder defaults to its "workbench-runs" subfolder;
    // with the Output Folder at <instance>/mods there is no default, and the launcher refuses until the user chooses one.
    // Never %LOCALAPPDATA%.
    [Fact]
    public void With_the_output_outside_the_instance_the_default_work_folder_is_its_workbench_runs_subfolder()
    {
        var o = Path.Combine(_root, "elsewhere", "out");
        var d = (string?)Contract.Static("WorkFolderRule", "Default", "the default work folder", o, Inst);
        Assert.Equal(Path.GetFullPath(Path.Combine(o, "workbench-runs")), Path.GetFullPath(d!));
        Assert.Null(Check(d!));
    }

    [Fact]
    public void With_the_output_at_the_instances_mods_folder_there_is_no_default_and_the_reason_is_one_sentence()
    {
        var ex = Assert.ThrowsAny<InvalidOperationException>(() =>
            Contract.Static("WorkFolderRule", "Default", "the default work folder", Path.Combine(Inst, "mods"), Inst));
        Assert.DoesNotContain("\n", ex.Message);
        Assert.Contains("Work Folder", ex.Message);
        Assert.DoesNotContain("AppData", ex.Message);
    }
}

/// <summary>H2: every cell a clickable Findings row names is loadable in the session, and a
/// bundle with Findings rows and no Conflicts rows opens. SessionCells.From(conflictCells, findingCells) is the list of
/// cells the worklist carries (today Program.OpenBundle builds it from Conflicts rows only and throws without one).</summary>
public sealed class SessionCellTests
{
    static IReadOnlyList<string> From(string?[] conflicts, string?[] findings) =>
        (IReadOnlyList<string>)Contract.Static("SessionCells", "From", "the session's loadable cells from both tabs",
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

/// <summary>The readers and the review credit on real outputs. The probes' real outputs carry load-order names, so they are not tracked
/// fixtures: WB_READER_OUTPUTS names a run-local evaluation folder and the tests read its receipts.
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
        // the receipt's json output (route-cut's first output is its text log; the rows file ends .json)
        var output = r.RootElement.GetProperty("outputs").EnumerateArray().Select(x => x.GetProperty("path").GetString()!).Last(x => x.EndsWith(".json", StringComparison.OrdinalIgnoreCase));
        var rows = ((System.Collections.IEnumerable)Contract.Static("FindingReaders", "Read", $"the Findings reader for {check}", check, Path.Combine(run, output))!).Cast<object>().ToList();
        var findingRows = rows.Where(x => (string?)Contract.Prop(x, "Role") == "finding").ToList();
        if (findings.ValueKind == JsonValueKind.Number) Assert.Equal(findings.GetInt32(), findingRows.Count);
        // an output that says it is incomplete (complete false, unreadable[], sourcesNotSearched[], limits[]) shows so
        using var o = JsonDocument.Parse(File.ReadAllText(Path.Combine(run, output)));
        bool incomplete = (o.RootElement.TryGetProperty("complete", out var c) && c.ValueKind == JsonValueKind.False)
            || new[] { "unreadable", "sourcesNotSearched", "unreadableRecords", "walkFailures" }.Any(k => o.RootElement.TryGetProperty(k, out var a) && a.ValueKind == JsonValueKind.Array && a.GetArrayLength() > 0);
        if (incomplete) Assert.Contains(rows, x => (string?)Contract.Prop(x, "Role") == "incomplete");
        // and the reader returns one row per entry of the lists it reads, so a reader returning nothing cannot pass
        var lists = check switch
        {
            "geometry.declared-dependencies" => new[] { "rows", "withheldRows", "unreadable" },
            "state.switch-census" => new[] { "switches", "jointFlips", "unreadable" },
            "patch.discovery-pool" or "patch.discovery-fomod" => new[] { "hits", "unjudgeableItems", "sourcesNotSearched" },
            "loadability.masters-and-order" => new[] { "walkFailures", "unreadableRecords", "limits", "unmeasured" },
            "geometry.route-cut" => new[] { "Rows" },
            _ => new[] { "clashes", "rules", "statements", "resolutions", "requirements", "warnings" },
        };
        int entries = lists.Sum(k => o.RootElement.TryGetProperty(k, out var a) && a.ValueKind == JsonValueKind.Array ? a.GetArrayLength() : 0)
                      + (o.RootElement.TryGetProperty("complete", out var cc) && cc.ValueKind == JsonValueKind.False ? 1 : 0);
        Assert.Equal(entries, rows.Count);                                     // route-cut: every row of its rows file
    }

    [Fact]
    public void L2_new_on_the_review_arm_counts_what_the_diff_receipt_counts()
    {
        var run = Run();
        using var d = JsonDocument.Parse(File.ReadAllText(Path.Combine(run, "receipts", "geometry.object-overlap__candidate-01__diff.json")));
        // ordersweep's own counts from the diff receipt. "New on the review arm" is every created row; the credit splits it
        // the same way.
        var created = d.RootElement.GetProperty("orderEvaluation").GetProperty("created");
        var baseEmit = Path.Combine(run, "sweep", "inv-0001", "overlapprobe", "baseline.json");
        var review = Path.Combine(run, "sweep", "inv-0001", "overlapprobe", "candidate-01.json");
        var diff = Path.Combine(run, "receipts", "geometry.object-overlap__candidate-01__diff.json");
        // Created is recomputed by the shared credit and must equal ordersweep's; the split needs ordersweep's reverse
        // measurement, so it is read from ordersweep's receipt, not recomputed (one owner)
        var n = Contract.Static("ReviewCredit", "Counts", "new-on-the-review-arm from DifferentialRows", diff, baseEmit, review)!;
        Assert.Equal(created.GetProperty("created").GetInt32(), (int)Contract.Prop(n, "Created")!);
        Assert.Equal(created.GetProperty("newDefect").GetInt32(), (int)Contract.Prop(n, "NewDefect")!);
        Assert.Equal(created.GetProperty("unmeasured").GetInt32(), (int)Contract.Prop(n, "Unmeasured")!);
    }
}

/// <summary>B4: an in-place run through the bundle stage on a synthetic MO2 world, with the real probes and
/// the pipeline: nothing is written under the instance or the game folder, and every new file is under the work folder
/// (or the system temp folder). The selection is a mod folder whose plugin places one reference in the master's cell.</summary>
[Trait("tier", "integration")]
public sealed class InPlaceRunTests
{
    static Dictionary<string, (long, DateTime)> Listing(params string[] roots) =>
        roots.SelectMany(r => Directory.EnumerateFileSystemEntries(r, "*", SearchOption.AllDirectories))
             .ToDictionary(p => p, p => File.Exists(p) ? (new FileInfo(p).Length, File.GetLastWriteTimeUtc(p)) : (-1L, Directory.GetLastWriteTimeUtc(p)));

    /// <summary>The synthetic world with a selection: mod folder SelMod, whose plugin places one reference in the master's cell.</summary>
    static ModKey SelWorld(TiltedRefWorld w)
    {
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
        return sel;
    }

    [Fact]
    public void B4_an_in_place_run_through_the_bundle_writes_only_under_its_work_folder()
    {
        using var w = new TiltedRefWorld();
        var sel = SelWorld(w);
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
        var before = Listing(w.Root);                                 // the whole world root, not only instance and game
        var psi = new ProcessStartInfo(R3Bundle.Runcontrol) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { "run", "--entry", entry, "--machine", machine, "--out", work, "--through", "bundle" }) psi.ArgumentList.Add(a);
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
        Assert.True(p.ExitCode == 0, $"runcontrol run --through bundle exited {p.ExitCode}: {text[^Math.Min(800, text.Length)..]}");
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

    static int FreePort() { var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); int p = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop(); return p; }

    /// <summary>B5: the workbench itself, started with no Python anywhere on its PATH and with its one run engine, the built-in one,
    /// driven through its own tools, runs the selection to done: the engine builds the bundle and the workbench checks and opens
    /// it, still with no Python. A run that stops before the bundle fails the test. The engine is this
    /// checkout's runcontrol.exe (or WB_RUNCONTROL); set WB_TEST_SHOTS to a folder to keep pictures of the window.</summary>
    [Fact]
    public async Task B5_with_the_built_in_engine_the_workbench_runs_with_no_python_on_its_path()
    {
        using var w = new TiltedRefWorld();
        SelWorld(w);
        // WB_TEST_INSTANCE, with WB_TEST_PROFILE, WB_TEST_GAME and WB_TEST_MOD, points the same run at a real profile
        var real = Environment.GetEnvironmentVariable("WB_TEST_INSTANCE") is { Length: > 0 } ri ? ri : null;
        string Real(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : throw new InvalidOperationException($"WB_TEST_INSTANCE is set, so {name} must be set too.");
        var instance = real ?? w.Instance;
        var profile = real is null ? "Default" : Real("WB_TEST_PROFILE");
        var mod = real is null ? "SelMod" : Real("WB_TEST_MOD");
        var game = real is null ? Path.GetDirectoryName(w.DataDir)! : Real("WB_TEST_GAME");
        // a real instance is too large to list whole: its profile folder and the chosen mod's folder are watched
        var watched = real is null ? new[] { instance, game } : new[] { Path.Combine(instance, "profiles", profile), Path.Combine(instance, "mods", mod) };
        var minutes = int.TryParse(Environment.GetEnvironmentVariable("WB_TEST_RUN_MINUTES"), out var m) ? m : 20;
        var gh = Path.Combine(R3Bundle.Checkout(), "_geometry-harness");
        var exe = Environment.GetEnvironmentVariable("WB_TEST_WORKBENCH") is { Length: > 0 } we ? we
            : Directory.GetFiles(Path.Combine(gh, "workbench", "bin", "Release"), "workbench.exe", SearchOption.AllDirectories).Single();
        var engine = Environment.GetEnvironmentVariable("WB_RUNCONTROL") is { Length: > 0 } rc ? rc
            : Path.Combine(gh, "runcontrol", "bin", "Release", "net9.0", "runcontrol.exe");
        Assert.True(File.Exists(engine), $"the built-in run engine is not built at {engine}");

        // a PATH with every folder that holds a python*.exe taken out, and proof that it starts no Python
        var path = string.Join(';', (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Where(d => !(Directory.Exists(d) && Directory.EnumerateFiles(d, "python*.exe").Any())));
        var ask = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe")) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = w.Root };
        foreach (var a in new[] { "/d", "/c", "python --version" }) ask.ArgumentList.Add(a);
        ask.Environment["PATH"] = path;
        using (var q = Process.Start(ask)!)
        {
            var said = q.StandardOutput.ReadToEnd() + q.StandardError.ReadToEnd(); q.WaitForExit();
            Assert.True(q.ExitCode != 0 && said.Contains("not recognized"), $"python still starts on the stripped PATH (exit {q.ExitCode}): {said}");
        }

        int port = FreePort();
        var settings = Path.Combine(w.Root, "settings", LauncherSettings.FileName); Directory.CreateDirectory(Path.GetDirectoryName(settings)!);
        var decoy = Path.Combine(w.Root, "decoy-runlog"); Directory.CreateDirectory(decoy);
        var outDir = Path.Combine(w.Root, "out");
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = w.Root };
        psi.Environment["PATH"] = path;
        psi.Environment["WB_MCP_PORT"] = port.ToString();
        psi.Environment["WB_SETTINGS"] = settings;
        psi.Environment["WB_RUNCONTROL"] = engine;
        psi.Environment["HOUSECARL_RUNLOG_DIR"] = decoy;                 // the run must keep its own run log, not this one
        foreach (var k in new[] { "WB_CONTROLLER", "WB_PIPELINE_CMD", "WB_HARNESS", "WB_OUT", "WB_SCRIPT", "WB_MACHINE" }) psi.Environment.Remove(k);
        var before = Listing(watched);
        using var wb = Process.Start(psi)!;
        var console = new System.Text.StringBuilder();
        wb.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (console) console.AppendLine(e.Data); };
        wb.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (console) console.AppendLine(e.Data); };
        wb.BeginOutputReadLine(); wb.BeginErrorReadLine();
        string Console() { lock (console) return console.ToString(); }
        try
        {
            McpClient? client = null;
            for (var until = DateTime.UtcNow.AddSeconds(90); client is null; )
            {
                Assert.False(wb.HasExited, $"the workbench exited before its tools answered: {Console()}");
                try { client = await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions { Endpoint = new Uri($"http://127.0.0.1:{port}/"), TransportMode = HttpTransportMode.StreamableHttp })); }
                catch (Exception) when (DateTime.UtcNow < until) { await Task.Delay(500); }
            }
            await using var _ = client;
            async Task<JsonElement> Ok(string tool, params (string K, object? V)[] kv)
            {
                var r = await client.CallToolAsync(tool, kv.ToDictionary(x => x.K, x => x.V));
                var text = string.Join("\n", r.Content.OfType<TextContentBlock>().Select(c => c.Text));
                Assert.False(r.IsError == true, $"{tool} {kv.FirstOrDefault().V} refused: {text}");
                using var d = JsonDocument.Parse(text);
                return d.RootElement.Clone();
            }
            var shots = Environment.GetEnvironmentVariable("WB_TEST_SHOTS") is { Length: > 0 } sd ? Directory.CreateDirectory(sd).FullName : null;
            async Task Shot(string name) { if (shots is not null) { await Task.Delay(1500); await Ok("workbench_act", ("verb", "capture_window"), ("path", Path.Combine(shots, name + ".png"))); } }

            await Ok("workbench_act", ("verb", "open_profile"), ("mode", "free"), ("instance", instance), ("profile", profile), ("game", game));
            await Ok("workbench_act", ("verb", "select_mods"), ("mods", new[] { mod }));
            await Ok("workbench_act", ("verb", "select_probes"), ("checks", new[] { "geometry.object-overlap" }));
            var set = await Ok("workbench_act", ("verb", "set_output"), ("path", outDir));
            Assert.False(set.GetProperty("launcher").TryGetProperty("controller", out var engineChoice));   // there is no run engine to choose
            await Shot("start-tab");

            var started = await Ok("workbench_act", ("verb", "run"));
            Assert.Equal("running", started.GetProperty("launcher").GetProperty("run").GetProperty("state").GetString());
            var folder = started.GetProperty("launcher").GetProperty("run").GetProperty("folder").GetString()!;
            JsonElement run = default; int pictures = 0, busy = 0;
            for (var until = DateTime.UtcNow.AddMinutes(minutes); DateTime.UtcNow < until; )
            {
                // with pictures asked for, the window is captured about once a second while the run goes (its line refreshes each second)
                if (shots is not null && pictures < 12) { await Task.Delay(1100); await Ok("workbench_act", ("verb", "capture_window"), ("path", Path.Combine(shots, $"run-in-progress-built-in-{++pictures}.png"))); }
                // on a real profile the window can be busy longer than a call waits; the tool says so and the poll asks again
                try { run = (await Ok("workbench_state", ("project", new[] { "launcher" }), ("wait_seconds", shots is not null && pictures < 12 ? 0 : 20))).GetProperty("launcher").GetProperty("run"); }
                catch (Xunit.Sdk.XunitException e) when (e.Message.Contains("its thread is busy")) { busy++; await Task.Delay(2000); continue; }
                if (run.GetProperty("state").GetString() != "running") break;
            }
            await Shot("run-ended-built-in");
            if (busy > 0) System.Console.WriteLine($"B5: the window was too busy to answer {busy} state call(s) during the run");
            string state = run.GetProperty("state").GetString()!, refusal = run.GetProperty("refusal").GetString() ?? "";
            var log = File.ReadAllText(Path.Combine(folder, "pipeline.log"));

            // what the engine itself says of the run: every stage's standing, by its own status command
            var st = new ProcessStartInfo(engine) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in new[] { "status", "--out", Path.Combine(folder, "run") }) st.ArgumentList.Add(a);
            st.Environment["HOUSECARL_RUNLOG_DIR"] = Path.Combine(folder, "status-runlog");
            using var sp = Process.Start(st)!;
            var stages = sp.StandardOutput.ReadToEnd().Split('\n').Select(l => l.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                           .Where(x => x.Length == 2 && !x[0].StartsWith('{')).ToList();
            sp.WaitForExit();
            var accepted = stages.TakeWhile(x => x[1].StartsWith("accepted")).Select(x => x[0]).ToList();
            Assert.True(accepted.Count >= 4 && accepted.Take(4).SequenceEqual(new[] { "census", "prepare", "scope", "plan" }),
                $"the run did not get through plan ({state}; {refusal}); stages: {string.Join(", ", stages.Select(x => x[0] + " " + x[1]))}; log: {log}");

            // no Python anywhere in what the run said, and the run's own run log holds the pipeline's record
            Assert.DoesNotContain("python", log, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("python", refusal, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("\"op\":\"evalpipeline\"", File.ReadAllText(Path.Combine(folder, "run", "runlog", "history.jsonl")).Replace(" ", ""));
            // on a real profile the window draws the opened finding with viewprobe, which is the window's own call and
            // rightly logs to the ambient store; any other record there would be the run's
            var strays = Directory.EnumerateFiles(decoy, "*", SearchOption.AllDirectories).Select(x => Path.GetRelativePath(decoy, x))
                .Where(x => !Path.GetFileName(x).StartsWith("viewprobe-")).ToList();
            var strayOps = strays.Where(x => x.EndsWith(".jsonl")).SelectMany(x => File.ReadLines(Path.Combine(decoy, x)).Take(40)).Select(l => l.Length > 300 ? l[..300] : l).ToList();
            Assert.True(strays.Count == 0, $"something wrote its run log outside the run: {string.Join("; ", strays.Take(12))} :: {string.Join(" | ", strayOps.Take(12))}");
            Assert.StartsWith(Path.Combine(outDir, "workbench-runs"), folder);

            // the scenes stage is always a stated, non-blocking refusal, so bundle is looked for by name, not by position
            Assert.True(stages.Any(x => x[0] == "bundle" && x[1].StartsWith("accepted")),
                $"the engine did not build the bundle ({state}; {refusal}); stages: {string.Join(", ", stages.Select(x => x[0] + " " + x[1]))}");
            // the engine built the bundle: the run is done, and the workbench checked and opened it with the same engine
            Assert.True(state == "done", $"the engine's bundle stage is accepted but the run is '{state}': {refusal}");
            var bundle = run.GetProperty("bundle").GetString()!;
            Assert.True(File.Exists(Path.Combine(bundle, "manifest.json")), "no bundle under the work folder");
            var verdict = Path.Combine(folder, "workbench", "bundle-verify.json");
            for (var until = DateTime.UtcNow.AddSeconds(120); !File.Exists(verdict) && DateTime.UtcNow < until; ) await Task.Delay(500);
            Assert.True(File.Exists(verdict), $"the finished bundle was not checked: {Console()}");
            using var v = JsonDocument.Parse(File.ReadAllText(verdict));
            Assert.True(v.RootElement.GetProperty("bundle").GetProperty("complete").GetBoolean());
            Assert.DoesNotContain("could not be opened", Console());
            var after = Listing(watched);
            var changed = before.Keys.Union(after.Keys).Where(k => !before.TryGetValue(k, out var b) || !after.TryGetValue(k, out var a) || b != a).ToList();
            Assert.True(changed.Count == 0, $"the run changed the instance or the game folder: {string.Join("; ", changed.Take(5))}");
        }
        finally { try { if (!wb.HasExited) wb.Kill(entireProcessTree: true); wb.WaitForExit(10_000); } catch (InvalidOperationException) { } }
    }
}


/// <summary>the output rule on layouts other than the plain one: an MO2 instance inside the game folder (a
/// portable install), and an instance whose ModOrganizer.ini sets base_directory (mods and profiles elsewhere), read
/// through the existing owner (housecarl-core Mo2Instance).</summary>
public sealed class OutputFolderLayoutTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "hc-wb-layout-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    string? Check(string o, string inst, string game) => (string?)Contract.Static("OutputFolderRule", "Check", "the output-folder rule", o, inst, "P", game);

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

/// <summary>H2: the workbench's own write path in a manual session. A commit on a synthetic
/// instance that is not the lab is not refused for being non-lab and lands under the Output Folder, with nothing new under
/// the instance; the re-check of that patch reads it from the Output Folder. Replaces B3c, whose first assertion bound to
/// the test host's Program. Members are reached by parameter name, so the same test runs on the base and after.</summary>
[Trait("tier", "integration")]
public sealed class ManualWriteTests
{
    static string Checkout() => R3Bundle.Checkout();
    static string ServerExe() => Path.Combine(Checkout(), "src", "housecarl-mcp", "bin", "Release", "net9.0", "housecarl-mcp.exe");

    static Dictionary<string, (long, DateTime)> Listing(params string[] roots) =>
        roots.Where(Directory.Exists).SelectMany(r => Directory.EnumerateFileSystemEntries(r, "*", SearchOption.AllDirectories))
             .ToDictionary(p => p, p => File.Exists(p) ? (new FileInfo(p).Length, File.GetLastWriteTimeUtc(p)) : (-1L, Directory.GetLastWriteTimeUtc(p)));

    static object Target(TiltedRefWorld w, string output)
    {
        var ctor = typeof(WriteTarget).GetConstructors().OrderByDescending(c => c.GetParameters().Length).First();
        var args = ctor.GetParameters().Select(p => p.Name switch
        {
            "Instance" => (object?)w.Instance, "Profile" => "Default", "DataDir" => w.DataDir, "Output" => output,
            _ => throw new Xunit.Sdk.XunitException($"WriteTarget has a parameter {p.Name} this test does not know"),
        }).ToArray();
        return ctor.Invoke(args);
    }

    static async Task<PatchWriter> Start(TiltedRefWorld w, string output)
    {
        var m = typeof(PatchWriter).GetMethod("StartAsync", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!;
        var args = m.GetParameters().Select(p => p.Name switch
        {
            "serverExe" => (object?)ServerExe(), "target" => Target(w, output),
            "allowedInstances" => Array.Empty<string>(),            // the base's lab guard: no instance named as allowed
            "dataDir" => Path.Combine(w.Root, "hcdata-" + Guid.NewGuid().ToString("N")), "corpusPath" => TestCorpus.Path,
            "log" => (Action<string>)(_ => { }), "ct" => CancellationToken.None,
            _ => throw new Xunit.Sdk.XunitException($"PatchWriter.StartAsync has a parameter {p.Name} this test does not know"),
        }).ToArray();
        try { return await (Task<PatchWriter>)m.Invoke(null, args)!; }
        catch (InvalidOperationException ex) { throw new Xunit.Sdk.XunitException("the write on a non-lab instance in a manual session was refused: " + ex.Message); }
    }

    [Fact]
    public async Task H2a_a_manual_commit_on_a_non_lab_instance_lands_under_the_output_folder_and_nothing_under_the_instance()
    {
        using var w = new TiltedRefWorld();
        var o = Path.Combine(w.Root, "out");
        var game = Path.GetDirectoryName(w.DataDir)!;
        var before = Listing(w.Instance, game);
        await using var writer = await Start(w, o);
        var pose = new ViewProbe.RefPlacementEdit.Pose(TiltedRefWorld.Pos, TiltedRefWorld.Rot, 1f);
        var answer = await writer.CommitAsync(new[] { new PendingEdit(TiltedRefWorld.Ref, pose, pose with { Position = new HousecarlCore.Vec3(600f, -256f, 64f) }) }, "WbManual", dryRun: false);
        Assert.True(File.Exists(Path.Combine(o, "houseCARL - WbManual", "WbManual.esp")), $"no patch under the Output Folder {o}: {answer}");
        var after = Listing(w.Instance, game);
        var changed = before.Keys.Union(after.Keys).Where(k => !before.TryGetValue(k, out var b) || !after.TryGetValue(k, out var a) || b != a).ToList();
        Assert.True(changed.Count == 0, $"the commit changed the instance or the game folder: {string.Join("; ", changed.Take(5))}");
    }

    /// <summary>A write with no Output Folder chosen is refused, never defaulted to the instance's mods folder.</summary>
    [Fact]
    public async Task H2c_a_write_with_no_output_folder_chosen_is_refused_in_one_sentence()
    {
        using var w = new TiltedRefWorld();
        var ex = await Assert.ThrowsAsync<Xunit.Sdk.XunitException>(() => Start(w, null!));
        Assert.Contains("Output Folder", ex.Message);
        Assert.Empty(Directory.GetFiles(Path.Combine(w.Instance, "mods"), "*.esp", SearchOption.AllDirectories).Where(f => !f.Contains("WorldMod")));
    }

    [Fact]
    public async Task H2b_the_re_check_of_a_patch_in_the_output_folder_reads_it_from_there()
    {
        using var w = new TiltedRefWorld();
        var game = Path.GetDirectoryName(w.DataDir)!;
        File.WriteAllText(Path.Combine(w.Instance, "ModOrganizer.ini"), "[General]\r\ngameName=Skyrim Special Edition\r\ngamePath=@ByteArray(" + game.Replace("\\", "\\\\") + ")\r\nselected_profile=@ByteArray(Default)\r\n");
        Directory.CreateDirectory(Path.Combine(w.Instance, "overwrite"));
        var o = Path.Combine(w.Root, "out");
        // the patch, written under the Output Folder by the server's out_path lane
        var data = Path.Combine(w.Root, "hcdata"); Directory.CreateDirectory(data);
        await using (var c = await McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "housecarl", Command = ServerExe(),
            Arguments = new[] { $"--HouseCarl:DataDir={w.DataDir}", $"--HouseCarl:ModsDir={Path.Combine(w.Instance, "mods")}",
                                $"--HouseCarl:ProfileDir={Path.Combine(w.Instance, "profiles", "Default")}", $"--HouseCarl:CorpusPath={TestCorpus.Path}" },
            EnvironmentVariables = new Dictionary<string, string?> { ["HOUSECARL_DATA_DIR"] = data, ["HOUSECARL_RUNLOG_DIR"] = Path.Combine(data, "runs") },
        })))
        {
            var r = await c.CallToolAsync("housecarl_apply", new Dictionary<string, object?>
            {
                ["ops"] = new[] { new Dictionary<string, object?> { ["formid"] = TiltedRefWorld.Ref.ToString(), ["field_path"] = "Placement.Position", ["value"] = "600,-256,64" } },
                ["patch"] = "WbRe", ["out_path"] = o,
            });
            Assert.False(r.IsError == true, string.Join("\n", r.Content.OfType<TextContentBlock>().Select(b => b.Text)));
        }
        var folder = Path.Combine(o, "houseCARL - WbRe");
        Assert.True(File.Exists(Path.Combine(folder, "WbRe.esp")));
        // a bundle of one overlap receipt on arm candidate-01, its candidate the profile as it is
        var bundle = Path.Combine(w.Root, "bundle");
        Directory.CreateDirectory(Path.Combine(bundle, "receipts")); Directory.CreateDirectory(Path.Combine(bundle, "candidates"));
        File.WriteAllText(Path.Combine(bundle, "plan.json"), """{"planId":"p","reviewArm":"candidate-01","arms":[{"armId":"candidate-01","kind":"candidate","candidateFile":"candidate-01.json"}]}""");
        File.WriteAllText(Path.Combine(bundle, "candidates", "candidate-01.json"), JsonSerializer.Serialize(new { SchemaVersion = 1, Name = "the profile as it is", BaseProfile = "Default", Plugins = new[] { TiltedRefWorld.Master.FileName.String }, Mods = new[] { "WorldMod" }, Changes = Array.Empty<string>() }));
        File.WriteAllText(Path.Combine(bundle, "receipts", "geometry.object-overlap__candidate-01__arm.json"),
            """{"adapter":{"kind":"ordersweep-probe","exe":"overlapprobe","args":["--order","{order}","--emit","{emit}","--base","{base}"],"populationFlag":"--population","commonFlags":true}}""");
        var row = new ConflictRow("overlap|" + TiltedRefWorld.Ref + "|x", "geometry.object-overlap", "OVERLAP", "HcWbWorld.esm", "HcWbWorld.esm", "",
                                  TiltedRefWorld.Ref.ToString(), null, null, "000802:HcWbWorld.esm", null, null, null, null, "calibrated-inference", null);
        var harness = Path.Combine(Checkout(), "_geometry-harness");
        var work = Path.Combine(w.Root, "work", "recheck");
        var m = typeof(Recheck).GetMethod("RunAll", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!;
        var args = m.GetParameters().Select(p => p.Name switch
        {
            "bundleDir" => (object?)bundle, "arm" => "candidate-01", "row" => row, "patchPlugin" => "WbRe.esp",
            "patchFolder" => folder,                                 // the patch's folder by its full path
            "patchMod" => Path.GetFileName(folder),                  // the base: the folder's name, which the window found under the instance's mods
            "instance" => w.Instance, "profile" => "Default", "game" => game, "workDir" => work,
            "engine" => RunEngine.OfBuiltIn(Path.Combine(harness, "runcontrol", "bin", "Release", "net9.0", "runcontrol.exe")), "conflictdiffExe" => Path.Combine(harness, "conflictdiff", "bin", "Release", "net9.0", "conflictdiff.exe"),
            "buildDirs" => Array.Empty<string>(), "localProbesRoot" => harness, "say" => (Action<string>)(_ => { }),
            _ => throw new Xunit.Sdk.XunitException($"Recheck.RunAll has a parameter {p.Name} this test does not know"),
        }).ToArray();
        RecheckResult result;
        try { result = (RecheckResult)m.Invoke(null, args)!; }
        catch (TargetInvocationException ex) when (ex.InnerException is { } ie)
        { throw new Xunit.Sdk.XunitException($"the re-check of the patch under the Output Folder did not read it ({ie.GetType().Name}): {ie.Message}"); }
        var patched = JsonNode.Parse(File.ReadAllText(Path.Combine(work, "order-patched.json")))!;
        Assert.Equal(folder, (string?)patched["Mods"]![0]);          // the probes read the patch from the Output Folder
        Assert.Contains("WbRe.esp", patched["Plugins"]!.AsArray().Select(x => (string?)x));
        Assert.Contains("WbRe.esp", File.ReadAllText(Path.Combine(work, "population.json")));
        Assert.NotNull(result.Verdict);
    }
}

/// <summary>the write tools whose lanes are patch= and into= (a new or existing houseCARL
/// folder in the instance's mods folder) are forwarded when the Output Folder is that mods folder, which the output rule
/// allows; a tool with its own out_path gets it mapped to the Output Folder; the rest are refused naming the concrete
/// reason (no out_path lane) and what to choose instead.</summary>
public sealed class ForwardRuleTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "hc-wb-fwdrule-" + Guid.NewGuid().ToString("N"));
    string Inst => Path.Combine(_root, "instance");
    string Game => Path.Combine(_root, "game");
    string Mods => Path.Combine(Inst, "mods");
    string Elsewhere => Path.Combine(_root, "out");
    public ForwardRuleTests()
    {
        foreach (var d in new[] { "mods/Enabled", "profiles/P", "overwrite" }) Directory.CreateDirectory(Path.Combine(Inst, d));
        Directory.CreateDirectory(Path.Combine(Game, "Data"));
    }
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    (Dictionary<string, JsonElement>? Args, string? Refusal) Prepare(string tool, string output, object args)
    {
        var given = JsonSerializer.SerializeToElement(args).EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
        return ForwardRule.Prepare(tool, given, output, HousecarlCore.Mo2OutputLayout.Of(Inst, "P", Game));
    }

    public static readonly TheoryData<string> PatchLaneTools = new()
    {
        "housecarl_create_plugin", "housecarl_place", "housecarl_nif_set", "housecarl_compile_script", "housecarl_bsa_extract",
        "housecarl_bsa_repack", "housecarl_write_seq", "housecarl_compact_plugin", "housecarl_merge_plugins",
    };

    [Theory, MemberData(nameof(PatchLaneTools))]
    public void With_the_mods_folder_as_output_a_patch_lane_tool_is_forwarded_and_in_place_is_refused(string tool)
    {
        var (args, why) = Prepare(tool, Mods, new { patch = "WbNew" });
        Assert.True(why is null, $"{tool} was refused with the mods folder as the Output Folder: {why}");
        Assert.Equal("WbNew", args!["patch"].GetString());
        Assert.NotNull(Prepare(tool, Mods, new { patch = "WbNew", in_place = true }).Refusal);
    }

    [Theory]
    [InlineData("housecarl_compile_script")]
    [InlineData("housecarl_bsa_extract")]
    [InlineData("housecarl_write_seq")]
    public void A_tool_with_its_own_out_path_gets_the_output_folder_and_an_out_path_outside_it_is_refused(string tool)
    {
        var (args, why) = Prepare(tool, Elsewhere, new { patch = "WbNew" });
        Assert.True(why is null, $"{tool} was refused: {why}");
        Assert.True(args!.ContainsKey("out_path"), $"{tool} was forwarded without the Output Folder as its out_path");
        Assert.Equal(Elsewhere, args["out_path"].GetString());
        Assert.Contains("Output Folder", Prepare(tool, Elsewhere, new { out_path = Path.Combine(_root, "other") }).Refusal);
    }

    [Theory]
    [InlineData("housecarl_create_plugin")]
    [InlineData("housecarl_place")]
    [InlineData("housecarl_nif_set")]
    [InlineData("housecarl_bsa_repack")]
    [InlineData("housecarl_compact_plugin")]
    [InlineData("housecarl_merge_plugins")]
    public void A_tool_with_no_out_path_lane_is_refused_with_the_output_folder_elsewhere_naming_why_and_what_to_choose(string tool)
    {
        var why = Prepare(tool, Elsewhere, new { patch = "WbNew" }).Refusal;
        Assert.NotNull(why);
        Assert.DoesNotContain("\n", why);
        Assert.Contains("out_path", why);                                   // the concrete reason: its lane takes none
        Assert.Contains(HousecarlCore.OutputFolderPolicy.Full(Mods), why);   // and the folder that would let it run
    }
}
